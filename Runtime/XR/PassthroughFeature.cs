using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
#endif

namespace SkyPlayer.Engine
{
    /// <summary>
    /// Meta Quest passthrough via the raw <c>XR_FB_passthrough</c> OpenXR extension —
    /// no Meta XR SDK, no AR Foundation, render-pipeline agnostic (matches this app's
    /// lightweight pure-OpenXR + Built-in RP setup).
    ///
    /// How it works: the passthrough camera feed is a compositor layer supplied by the
    /// runtime. We create + start it, then intercept <c>xrEndFrame</c> to (1) insert the
    /// passthrough layer as an UNDERLAY (first = bottom of the layer stack) and (2) flag
    /// Unity's projection layer for source-alpha blending so the transparent parts of the
    /// eye texture (camera cleared to alpha 0) let the passthrough show through. Without
    /// that alpha flag the app layer is opaque and passthrough is fully occluded (black).
    ///
    /// Toggle at runtime via <see cref="SetEnabled"/>. Safe no-op if the runtime doesn't
    /// expose the extension (e.g. non-Quest). The feature must be enabled in
    /// Project Settings ▸ XR Plug-in Management ▸ OpenXR for it to be baked into the build.
    /// </summary>
#if UNITY_EDITOR
    [OpenXRFeature(
        UiName = "SkyPlayer Engine Passthrough (FB)",
        BuildTargetGroups = new[] { BuildTargetGroup.Android },
        Company = "Ternedal",
        Desc = "Meta Quest passthrough via XR_FB_passthrough as a compositor underlay.",
        DocumentationLink = "",
        OpenxrExtensionStrings = ExtString,
        Version = "1.0.0",
        FeatureId = featureId,
        Priority = -100)]   // low priority → our xrEndFrame hook wraps closest to the real call
#endif
    public class PassthroughFeature : OpenXRFeature
    {
        public const string featureId = "dk.ternedal.skyplayer.engine.passthrough";
        public const string ExtString = "XR_FB_passthrough";

        // ---- OpenXR constants ----
        // Struct types (XR_FB_passthrough, ext #119). NB: 1000118000 is SYSTEM_PASSTHROUGH_
        // PROPERTIES_FB — the create-infos start at ...001, so these must NOT be based at 000.
        const uint XR_TYPE_PASSTHROUGH_CREATE_INFO_FB       = 1000118001;
        const uint XR_TYPE_PASSTHROUGH_LAYER_CREATE_INFO_FB = 1000118002;
        const uint XR_TYPE_COMPOSITION_LAYER_PASSTHROUGH_FB = 1000118003;
        const uint XR_TYPE_COMPOSITION_LAYER_PROJECTION     = 35;
        const uint XR_PASSTHROUGH_LAYER_PURPOSE_RECONSTRUCTION_FB = 0;
        // Blend the app layer over passthrough using the texture's (unpremultiplied) alpha.
        const ulong BLEND_SRC_ALPHA_UNPREMULT = 0x2 /*SOURCE_ALPHA*/ | 0x4 /*UNPREMULTIPLIED*/;

        // ---- handles / state (static: the xrEndFrame hook is a static native callback) ----
        static ulong _session;
        static ulong _passthrough;      // XrPassthroughFB
        static ulong _layer;            // XrPassthroughLayerFB
        static IntPtr _ptLayerStruct;   // pinned XrCompositionLayerPassthroughFB
        static bool _extEnabled;
        static bool _sessionRunning;
        static bool _wantOn;            // desired state from the app
        static bool _running;           // passthrough actually started + injecting
        static volatile int _injectCount;  // frames where we injected the underlay (diagnostics)
        static bool _diagLogged;

        // ---- resolved FB function pointers (cached delegates) ----
        static Del_CreatePassthrough      _createPassthrough;
        static Del_DestroyPassthrough     _destroyPassthrough;
        static Del_PassthroughStart       _passthroughStart;
        static Del_PassthroughPause       _passthroughPause;
        static Del_CreatePassthroughLayer _createLayer;
        static Del_DestroyPassthroughLayer _destroyLayer;
        static Del_PassthroughLayerResume _layerResume;
        static Del_PassthroughLayerPause  _layerPause;

        // ---- xrEndFrame interception ----
        static IntPtr _origGetProc;
        static Del_GetInstanceProcAddr _getProcHook;   // kept alive
        static Del_EndFrame _endFrameHook;             // kept alive
        static IntPtr _realEndFrame;
        static Del_EndFrame _realEndFrameDel;

        // ================= public app API =================

        /// <summary>Turn passthrough on/off. Cheap + idempotent; may be called before the
        /// session begins (state is applied once passthrough is created).</summary>
        public static void SetEnabled(bool on)
        {
            _wantOn = on;
            Reconcile();
        }

        public static bool Supported => _extEnabled;
        public static bool Running   => _running;

        /// <summary>Call from the main thread (e.g. AppController.Update). Logs once, when the
        /// xrEndFrame underlay injection has actually started running — confirms the hook fires
        /// (so if the room is still black, the cause is compositing/alpha, not the plumbing).</summary>
        public static void LogDiagOnce()
        {
            if (_diagLogged || _injectCount <= 0) return;
            _diagLogged = true;
            Debug.Log($"[Passthrough] xrEndFrame underlay injecting OK (frames={_injectCount}, running={_running}, ext={_extEnabled})");
        }

        // ================= OpenXRFeature lifecycle =================

        protected override IntPtr HookGetInstanceProcAddr(IntPtr func)
        {
            _origGetProc = func;
            _getProcHook = Intercepted_GetInstanceProcAddr;
            return Marshal.GetFunctionPointerForDelegate(_getProcHook);
        }

        protected override bool OnInstanceCreate(ulong xrInstance)
        {
            _instance = xrInstance;
            _extEnabled = OpenXRRuntime.IsExtensionEnabled(ExtString);
            if (!_extEnabled)
            {
                Debug.LogWarning("[Passthrough] XR_FB_passthrough not enabled by the runtime — passthrough unavailable.");
                return true; // stay loaded but inert
            }

            _createPassthrough      = Resolve<Del_CreatePassthrough>("xrCreatePassthroughFB");
            _destroyPassthrough     = Resolve<Del_DestroyPassthrough>("xrDestroyPassthroughFB");
            _passthroughStart       = Resolve<Del_PassthroughStart>("xrPassthroughStartFB");
            _passthroughPause       = Resolve<Del_PassthroughPause>("xrPassthroughPauseFB");
            _createLayer            = Resolve<Del_CreatePassthroughLayer>("xrCreatePassthroughLayerFB");
            _destroyLayer           = Resolve<Del_DestroyPassthroughLayer>("xrDestroyPassthroughLayerFB");
            _layerResume            = Resolve<Del_PassthroughLayerResume>("xrPassthroughLayerResumeFB");
            _layerPause             = Resolve<Del_PassthroughLayerPause>("xrPassthroughLayerPauseFB");
            Debug.Log("[Passthrough] XR_FB_passthrough enabled; FB functions resolved.");
            return true;
        }

        protected override void OnSessionCreate(ulong xrSession) { _session = xrSession; }

        protected override void OnSessionBegin(ulong xrSession)
        {
            _session = xrSession;
            _sessionRunning = true;
            CreatePassthrough();
            Reconcile();
        }

        protected override void OnSessionEnd(ulong xrSession)
        {
            _sessionRunning = false;
            _running = false;
        }

        protected override void OnSessionDestroy(ulong xrSession) { DestroyPassthrough(); }

        // ================= passthrough create / start / stop =================

        static void CreatePassthrough()
        {
            if (!_extEnabled || _passthrough != 0 || _createPassthrough == null) return;

            var pci = new XrPassthroughCreateInfoFB { type = XR_TYPE_PASSTHROUGH_CREATE_INFO_FB };
            int r = _createPassthrough(_session, ref pci, out _passthrough);
            if (r != 0) { Debug.LogError("[Passthrough] xrCreatePassthroughFB failed: " + r); return; }

            var lci = new XrPassthroughLayerCreateInfoFB
            {
                type = XR_TYPE_PASSTHROUGH_LAYER_CREATE_INFO_FB,
                passthrough = _passthrough,
                purpose = XR_PASSTHROUGH_LAYER_PURPOSE_RECONSTRUCTION_FB,
            };
            r = _createLayer(_session, ref lci, out _layer);
            if (r != 0) { Debug.LogError("[Passthrough] xrCreatePassthroughLayerFB failed: " + r); return; }

            // Pre-pin the composition layer we submit every frame (handle is stable).
            var comp = new XrCompositionLayerPassthroughFB
            {
                type = XR_TYPE_COMPOSITION_LAYER_PASSTHROUGH_FB,
                flags = 0,
                space = 0,
                layerHandle = _layer,
            };
            _ptLayerStruct = Marshal.AllocHGlobal(Marshal.SizeOf<XrCompositionLayerPassthroughFB>());
            Marshal.StructureToPtr(comp, _ptLayerStruct, false);
            Debug.Log("[Passthrough] passthrough + layer created.");
        }

        static void DestroyPassthrough()
        {
            _running = false;
            if (_layer != 0 && _destroyLayer != null) { _destroyLayer(_layer); _layer = 0; }
            if (_passthrough != 0 && _destroyPassthrough != null) { _destroyPassthrough(_passthrough); _passthrough = 0; }
            if (_ptLayerStruct != IntPtr.Zero) { Marshal.FreeHGlobal(_ptLayerStruct); _ptLayerStruct = IntPtr.Zero; }
        }

        /// <summary>Bring actual passthrough state in line with <c>_wantOn</c>.</summary>
        static void Reconcile()
        {
            if (!_extEnabled || !_sessionRunning || _passthrough == 0) return;

            if (_wantOn && !_running)
            {
                int a = _passthroughStart(_passthrough);
                int b = _layerResume(_layer);
                _running = (a == 0 && b == 0);
                Debug.Log($"[Passthrough] start passthrough={a} resumeLayer={b} → running={_running}");
            }
            else if (!_wantOn && _running)
            {
                _layerPause(_layer);
                _passthroughPause(_passthrough);
                _running = false;
                Debug.Log("[Passthrough] paused.");
            }
        }

        // ================= native interception =================

        static T Resolve<T>(string name) where T : Delegate
        {
            IntPtr p = IntPtr.Zero;
            IntPtr namePtr = Marshal.StringToHGlobalAnsi(name);
            try
            {
                var getProc = Marshal.GetDelegateForFunctionPointer<Del_GetInstanceProcAddr>(xrGetInstanceProcAddr);
                if (getProc(_instance, namePtr, out p) != 0 || p == IntPtr.Zero)
                {
                    Debug.LogError("[Passthrough] could not resolve " + name);
                    return null;
                }
            }
            finally { Marshal.FreeHGlobal(namePtr); }
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        static ulong _instance;   // XrInstance, captured in OnInstanceCreate

        [AOT.MonoPInvokeCallback(typeof(Del_GetInstanceProcAddr))]
        static int Intercepted_GetInstanceProcAddr(ulong instance, IntPtr name, out IntPtr function)
        {
            _instance = instance;
            var orig = Marshal.GetDelegateForFunctionPointer<Del_GetInstanceProcAddr>(_origGetProc);
            int r = orig(instance, name, out function);
            if (r == 0 && function != IntPtr.Zero)
            {
                string fn = Marshal.PtrToStringAnsi(name);
                if (fn == "xrEndFrame")
                {
                    _realEndFrame = function;
                    _realEndFrameDel = Marshal.GetDelegateForFunctionPointer<Del_EndFrame>(_realEndFrame);
                    _endFrameHook ??= Intercepted_EndFrame;
                    function = Marshal.GetFunctionPointerForDelegate(_endFrameHook);
                }
            }
            return r;
        }

        [AOT.MonoPInvokeCallback(typeof(Del_EndFrame))]
        static int Intercepted_EndFrame(ulong session, IntPtr frameEndInfo)
        {
            if (!_running || _ptLayerStruct == IntPtr.Zero || frameEndInfo == IntPtr.Zero)
                return _realEndFrameDel(session, frameEndInfo);

            _injectCount++;   // diagnostics (read + logged once from the main thread)

            // XrFrameEndInfo: layerCount @28, layers ptr @32 (64-bit ABI).
            uint layerCount = (uint)Marshal.ReadInt32(frameEndInfo, 28);
            IntPtr layers   = Marshal.ReadIntPtr(frameEndInfo, 32);

            // Flag Unity's projection layer(s) so their alpha lets passthrough show through.
            for (int i = 0; i < layerCount; i++)
            {
                IntPtr lp = Marshal.ReadIntPtr(layers, i * IntPtr.Size);
                if (lp == IntPtr.Zero) continue;
                if ((uint)Marshal.ReadInt32(lp, 0) == XR_TYPE_COMPOSITION_LAYER_PROJECTION)
                {
                    long flags = Marshal.ReadInt64(lp, 16);            // XrCompositionLayerFlags @16
                    Marshal.WriteInt64(lp, 16, flags | (long)BLEND_SRC_ALPHA_UNPREMULT);
                }
            }

            // New layer array with the passthrough underlay first (bottom of the stack).
            int newCount = (int)layerCount + 1;
            IntPtr newLayers = Marshal.AllocHGlobal(newCount * IntPtr.Size);
            IntPtr infoCopy  = Marshal.AllocHGlobal(40); // sizeof(XrFrameEndInfo)
            try
            {
                Marshal.WriteIntPtr(newLayers, 0, _ptLayerStruct);
                for (int i = 0; i < layerCount; i++)
                    Marshal.WriteIntPtr(newLayers, (i + 1) * IntPtr.Size, Marshal.ReadIntPtr(layers, i * IntPtr.Size));

                // Copy the incoming struct and swap in our layer list.
                for (int off = 0; off < 40; off += 8) Marshal.WriteInt64(infoCopy, off, Marshal.ReadInt64(frameEndInfo, off));
                Marshal.WriteInt32(infoCopy, 28, newCount);
                Marshal.WriteIntPtr(infoCopy, 32, newLayers);

                return _realEndFrameDel(session, infoCopy);
            }
            finally
            {
                Marshal.FreeHGlobal(newLayers);
                Marshal.FreeHGlobal(infoCopy);
            }
        }

        // ================= delegate + struct definitions =================

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int Del_GetInstanceProcAddr(ulong instance, IntPtr name, out IntPtr function);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int Del_EndFrame(ulong session, IntPtr frameEndInfo);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int Del_CreatePassthrough(ulong session, ref XrPassthroughCreateInfoFB ci, out ulong passthrough);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int Del_DestroyPassthrough(ulong passthrough);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int Del_PassthroughStart(ulong passthrough);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int Del_PassthroughPause(ulong passthrough);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int Del_CreatePassthroughLayer(ulong session, ref XrPassthroughLayerCreateInfoFB ci, out ulong layer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int Del_DestroyPassthroughLayer(ulong layer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int Del_PassthroughLayerResume(ulong layer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int Del_PassthroughLayerPause(ulong layer);

        [StructLayout(LayoutKind.Sequential)]
        struct XrPassthroughCreateInfoFB { public uint type; public IntPtr next; public ulong flags; }

        [StructLayout(LayoutKind.Sequential)]
        struct XrPassthroughLayerCreateInfoFB
        { public uint type; public IntPtr next; public ulong passthrough; public ulong flags; public uint purpose; }

        [StructLayout(LayoutKind.Sequential)]
        struct XrCompositionLayerPassthroughFB
        { public uint type; public IntPtr next; public ulong flags; public ulong space; public ulong layerHandle; }
    }
}
