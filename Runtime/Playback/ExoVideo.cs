using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SkyPlayer.Engine
{
    /// <summary>
    /// C# bridge to the native ExoPlayer plugin (dk.ternedal.skyplayer.engine.ExoVideoPlugin).
    /// Phase 1: decode + audio + state. Phase 2 adds the video texture.
    /// </summary>
    public class ExoVideo
    {
        private AndroidJavaObject _plugin;
        public bool Available { get; private set; }

#if UNITY_ANDROID && !UNITY_EDITOR
        [DllImport("exovideo")] private static extern int ExoNativePing();
        [DllImport("exovideo")] private static extern IntPtr GetRenderEventFunc();
        [DllImport("exovideo")] private static extern int ExoNativeSetPlugin(IntPtr plugin);
        [DllImport("exovideo")] private static extern int ExoNativeGetOutTex();
        [DllImport("exovideo")] private static extern int ExoNativeGetW();
        [DllImport("exovideo")] private static extern int ExoNativeGetH();
#endif

        /// <summary>GPU step A: confirm the native .so built, loaded and is callable.</summary>
        public void NativePing()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { Debug.Log("[ExoVideo] native ping=" + ExoNativePing()); }
            catch (Exception e) { Debug.LogError("[ExoVideo] native load FAILED: " + e); }
#endif
        }

        public IntPtr RenderEventFunc()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { return GetRenderEventFunc(); } catch { return IntPtr.Zero; }
#else
            return IntPtr.Zero;
#endif
        }

        /// <summary>Native GL texture id the video frame is blitted into (0 until ready).</summary>
        public int OutTexId()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { return ExoNativeGetOutTex(); } catch { return 0; }
#else
            return 0;
#endif
        }

        public int OutW()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { return ExoNativeGetW(); } catch { return 0; }
#else
            return 0;
#endif
        }

        public int OutH()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { return ExoNativeGetH(); } catch { return 0; }
#else
            return 0;
#endif
        }

        public void Create()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = up.GetStatic<AndroidJavaObject>("currentActivity");
                _plugin = new AndroidJavaObject("dk.ternedal.skyplayer.engine.ExoVideoPlugin", activity);
                _plugin.Call("create");
                Available = true;
                // Hand the plugin object to the native side so it can drive the SurfaceTexture.
                int ok = ExoNativeSetPlugin(_plugin.GetRawObject());
                Debug.Log("[ExoVideo] plugin created. native setPlugin=" + ok);
            }
            catch (Exception e)
            {
                Available = false;
                Debug.LogError("[ExoVideo] init failed (plugin/dependency missing?): " + e);
            }
#else
            Available = false;
#endif
        }

        public void SetUrl(string url)
        {
            Debug.Log("[ExoVideo] setUrl " + url);
            _plugin?.Call("setUrl", url);
        }

        public void Play()        => _plugin?.Call("play");
        public void Pause()       => _plugin?.Call("pause");
        public void SeekTo(long ms) => _plugin?.Call("seekTo", ms);

        public void Release()
        {
            try { _plugin?.Call("release"); } catch { /* ignore */ }
            _plugin?.Dispose();
            _plugin = null;
        }

        public bool   IsReady()    => _plugin != null && _plugin.Call<bool>("isReady");
        public bool   IsEnded()    => _plugin != null && _plugin.Call<bool>("isEnded");
        public long   DurationMs() => _plugin != null ? _plugin.Call<long>("getDurationMs") : 0;
        public long   PositionMs() => _plugin != null ? _plugin.Call<long>("getPositionMs") : 0;
        public bool   IsPlayingNow() => _plugin != null && _plugin.Call<bool>("getIsPlaying");
        public string LastError()  => _plugin != null ? _plugin.Call<string>("getLastError") : "";
        public int    FrameCount() => _plugin != null ? _plugin.Call<int>("getFrameCount") : 0;
        // Video frames now flow GPU-side: native blits into a GL texture (OutTexId/OutW/OutH).
    }
}
