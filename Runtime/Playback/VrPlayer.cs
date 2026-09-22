using System;
using System.Collections;
using UnityEngine;

namespace SkyPlayer.Engine
{
    /// <summary>
    /// Product-neutral VR video player.
    ///
    /// Owns Media3/ExoPlayer playback, the external GPU texture, flat/sphere display geometry,
    /// projection, zoom/pan/recenter and background-key uniforms. It deliberately knows nothing
    /// about catalogs, Stash, progress persistence, themes or controller mappings.
    /// </summary>
    public sealed class VrPlayer : MonoBehaviour
    {
        private Camera _camera;
        private ExoVideo _exo;
        private Material _material;
        private GameObject _display;
        private Texture2D _externalTexture;

        private MediaSource _source;
        private Projection _projection;
        private VrPlayerOptions _options;
        private BackgroundKeySettings _key;

        private int _playSession;
        private int _boundTextureId;
        private bool _prepared;
        private bool _playingIntent;
        private bool _endedHandled;

        private float _span = 0.85f;
        private const float SpanMin = 0.35f;
        private const float SpanMax = 1.15f;
        private float _flatDistance = 4f;
        private const float FlatMin = 2f;
        private const float FlatMax = 14f;

        private Quaternion _baseRotation = Quaternion.identity;
        private float _panYaw;
        private float _panPitch;
        private const float PanPitchClamp = 70f;

        public bool IsPrepared => _prepared;
        public bool IsPlaying => _exo != null && _exo.IsPlayingNow();
        public bool IsEnded => _exo != null && _exo.IsEnded();
        public double Length => _exo != null ? _exo.DurationMs() / 1000.0 : 0;
        public double Time => _exo != null ? _exo.PositionMs() / 1000.0 : 0;
        public string FormatLabel => _projection.Label;
        public Projection Projection => _projection;
        public float Zoom01 => Mathf.InverseLerp(SpanMax, SpanMin, _span);

        public event Action<string> StateChanged;
        public event Action Ended;

        public void Initialize(Camera camera, VrPlayerOptions? options = null)
        {
            _camera = camera != null ? camera : throw new ArgumentNullException(nameof(camera));
            _options = options ?? VrPlayerOptions.Default;
            _key = _options.BackgroundKey;
            _span = Mathf.Lerp(SpanMax, SpanMin, Mathf.Clamp01(_options.DefaultZoom));

            var shader = Shader.Find("SkyPlayer/Engine/StereoEquirect");
            if (shader == null)
                throw new InvalidOperationException(
                    "SkyPlayer Engine shader is missing from the build: SkyPlayer/Engine/StereoEquirect");

            _material = new Material(shader);
            _material.SetFloat("_Span", _span);

            _exo = new ExoVideo();
            _exo.Create();
            _exo.NativePing();

            if (!_exo.Available)
                StateChanged?.Invoke("error:Media3/ExoPlayer bridge unavailable");
        }

        public void Open(MediaSource source, Projection projection)
        {
            EnsureInitialized();

            int session = ++_playSession;
            _source = source;
            _prepared = false;
            _endedHandled = false;
            _playingIntent = true;
            StateChanged?.Invoke("loading");

            _span = Mathf.Lerp(SpanMax, SpanMin, Mathf.Clamp01(_options.DefaultZoom));
            _material.SetFloat("_Span", _span);
            ApplyProjection(projection);

            _exo.SetUrl(source.Url);
            StartCoroutine(PollReady(session));
        }

        private IEnumerator PollReady(int session)
        {
            float elapsed = 0f;
            while (elapsed < 30f)
            {
                if (session != _playSession) yield break;

                if (_exo.IsReady())
                {
                    if (session != _playSession) yield break;

                    _prepared = true;
                    long durationMs = _exo.DurationMs();
                    double resume = _source.ResumePositionSeconds;
                    if (resume > 1 && durationMs > 0 && resume < (durationMs / 1000.0) - 2)
                        _exo.SeekTo((long)(resume * 1000));

                    RebuildGeometry(1280, 720);
                    StateChanged?.Invoke("playing");
                    yield break;
                }

                string error = _exo.LastError();
                if (!string.IsNullOrEmpty(error))
                {
                    StateChanged?.Invoke("error:" + error);
                    yield break;
                }

                elapsed += UnityEngine.Time.unscaledDeltaTime;
                yield return null;
            }

            if (session == _playSession)
                StateChanged?.Invoke("error:Timeout (ExoPlayer)");
        }

        public void ApplyProjection(Projection projection)
        {
            EnsureInitialized();
            _projection = projection;

            bool keyEnabled = _key.Enabled && projection.Geometry == Geometry.Sphere;
            _material.SetFloat("_Mode", projection.Geometry == Geometry.Sphere ? 1f : 0f);
            _material.SetFloat("_Key", keyEnabled ? 1f : 0f);
            _material.SetFloat("_Hfov", projection.Hfov <= 0 ? 360f : projection.Hfov);
            _material.SetFloat("_Stereo", (float)(int)projection.Stereo);

            if (keyEnabled)
            {
                _span = 1f;
                _material.SetFloat("_Span", _span);
            }

            ApplyBackgroundKey(_key);

            if (_prepared)
            {
                int w = _exo != null ? _exo.OutW() : 0;
                int h = _exo != null ? _exo.OutH() : 0;
                RebuildGeometry(w > 0 ? w : 1280, h > 0 ? h : 720);
            }
        }

        public void SetFormatHint(string hint) => ApplyProjection(Projection.FromHint(hint));

        public void ApplyBackgroundKey(BackgroundKeySettings settings)
        {
            _key = settings;
            if (_material == null) return;

            bool enabled = settings.Enabled && _projection.Geometry == Geometry.Sphere;
            _material.SetFloat("_Key", enabled ? 1f : 0f);
            _material.SetFloat("_SatThresh", Mathf.Clamp(settings.Threshold, 0.02f, 0.95f));
            _material.SetFloat("_KeyMode", Mathf.Clamp(settings.Mode, 0, 7));
            _material.SetFloat("_HoleFill", settings.DetailMode > 0 ? 1f : 0f);
            _material.SetFloat("_Soften", SoftenAmount(settings.DetailMode));
            _material.SetColor("_KeyColor", settings.KeyColor);
            _material.SetFloat("_Spill", SpillStrength(settings.SpillMode));
        }

        public bool TrySampleKeyColorAtGaze(out Color color)
        {
            color = Color.clear;
            if (_camera == null || _display == null || !IsImmersiveSphere) return false;
            Texture src = _externalTexture;
            if (src == null || src.width < 8 || src.height < 8) return false;

            Vector3 d = Quaternion.Inverse(_display.transform.rotation) * _camera.transform.forward;
            if (d.sqrMagnitude < 1e-6f) return false;
            d.Normalize();

            float lon = Mathf.Atan2(d.x, d.z);
            float lat = Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f));
            float s = Mathf.Max(_span, 0.05f);
            float hfov = _projection.Hfov <= 0 ? 360f : _projection.Hfov;
            float u = hfov <= 180f
                ? lon / (Mathf.PI * s) + 0.5f
                : lon / (2f * Mathf.PI * s) + 0.5f;
            float v = lat / (Mathf.PI * s) + 0.5f;

            if (u < 0f || u > 1f || v < 0f || v > 1f) return false;
            if (_projection.Stereo == Stereo.SBS) u *= 0.5f;
            else if (_projection.Stereo == Stereo.OU) v = v * 0.5f + 0.5f;

            const int sampleSize = 24;
            int px = Mathf.Clamp(Mathf.RoundToInt(u * src.width) - sampleSize / 2, 0, src.width - sampleSize);
            int py = Mathf.Clamp(Mathf.RoundToInt(v * src.height) - sampleSize / 2, 0, src.height - sampleSize);

            RenderTexture tmp = RenderTexture.GetTemporary(src.width, src.height, 0);
            RenderTexture previous = RenderTexture.active;
            Texture2D sample = null;
            try
            {
                Graphics.Blit(src, tmp);
                RenderTexture.active = tmp;
                sample = new Texture2D(sampleSize, sampleSize, TextureFormat.RGBA32, false);
                sample.ReadPixels(new Rect(px, py, sampleSize, sampleSize), 0, 0);
                sample.Apply(false);

                Color sum = Color.clear;
                Color[] pixels = sample.GetPixels();
                foreach (Color c in pixels) sum += c;
                color = sum / Mathf.Max(pixels.Length, 1);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SkyPlayer.Engine] key-color sample failed: " + e.Message);
                return false;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(tmp);
                if (sample != null) Destroy(sample);
            }
        }

        public void TogglePlayPause()
        {
            if (_exo == null) return;
            _playingIntent = !_playingIntent;
            if (_playingIntent) _exo.Play();
            else _exo.Pause();
        }

        public void Play()
        {
            if (_exo == null) return;
            _playingIntent = true;
            _exo.Play();
        }

        public void Pause()
        {
            if (_exo == null) return;
            _playingIntent = false;
            _exo.Pause();
        }

        public void Seek(float fraction01)
        {
            if (_exo == null) return;
            long duration = _exo.DurationMs();
            if (duration > 0)
                _exo.SeekTo((long)(Mathf.Clamp01(fraction01) * duration));
        }

        public void SeekRelative(double seconds) => SeekToSeconds(Time + seconds);

        public void SeekToSeconds(double seconds)
        {
            if (_exo == null) return;
            long duration = _exo.DurationMs();
            double max = duration > 500 ? duration / 1000.0 - 0.5 : double.MaxValue;
            double target = Math.Max(0, Math.Min(max, seconds));
            _exo.SeekTo((long)(target * 1000));
        }

        /// <summary>
        /// Adjust zoom with a signed normalized delta. Positive moves closer/more immersive;
        /// negative moves out. Input-device mapping belongs to the client.
        /// </summary>
        public void AdjustZoom(float delta)
        {
            if (IsImmersiveSphere)
            {
                _span = Mathf.Clamp(_span + delta * 0.6f, SpanMin, SpanMax);
                _material?.SetFloat("_Span", _span);
            }
            else
            {
                _flatDistance = Mathf.Clamp(_flatDistance - delta * 6f, FlatMin, FlatMax);
                PlaceFlatInFront(_flatDistance);
            }
        }

        public void SetZoom01(float zoom01)
        {
            _span = Mathf.Lerp(SpanMax, SpanMin, Mathf.Clamp01(zoom01));
            _material?.SetFloat("_Span", _span);
        }

        /// <summary>Pan focus in degrees. Positive yaw turns right; pitch is clamped to ±70°.</summary>
        public void Pan(float yawDegrees, float pitchDegrees)
        {
            if (!IsImmersiveSphere) return;
            _panYaw += yawDegrees;
            _panPitch = Mathf.Clamp(_panPitch + pitchDegrees, -PanPitchClamp, PanPitchClamp);
            ApplySphereRotation();
        }

        public void ApplyOriginShift(Quaternion delta)
        {
            _baseRotation = delta * _baseRotation;
            ApplySphereRotation();
        }

        public void Recenter()
        {
            if (IsImmersiveSphere) RecenterSphere();
            else PlaceFlatInFront(_flatDistance);
        }

        public void Stop()
        {
            ++_playSession;
            if (_exo != null) _exo.Pause();

            _playingIntent = false;
            _prepared = false;
            _endedHandled = false;

            if (_display != null)
            {
                Destroy(_display);
                _display = null;
            }

            if (_externalTexture != null)
            {
                Destroy(_externalTexture);
                _externalTexture = null;
                _boundTextureId = 0;
            }
        }

        private void LateUpdate()
        {
            if (_exo == null) return;

            IntPtr renderFn = _exo.RenderEventFunc();
            if (renderFn != IntPtr.Zero)
                GL.IssuePluginEvent(renderFn, 1);

            BindOrRefreshExternalTexture();

            if (IsImmersiveSphere && _display != null && _camera != null)
            {
                _display.transform.position = _camera.transform.position;
                ApplySphereRotation();
            }

            if (_prepared && !_endedHandled)
            {
                if (_exo.IsEnded())
                {
                    _endedHandled = true;
                    _playingIntent = false;
                    StateChanged?.Invoke("ended");
                    Ended?.Invoke();
                    return;
                }

                string error = _exo.LastError();
                if (!string.IsNullOrEmpty(error))
                {
                    _endedHandled = true;
                    _playingIntent = false;
                    StateChanged?.Invoke("error:" + error);
                }
            }
        }

        private void BindOrRefreshExternalTexture()
        {
            int textureId = _exo.OutTexId();
            int width = _exo.OutW();
            int height = _exo.OutH();
            if (textureId == 0 || width <= 0 || height <= 0) return;

            bool changed = _externalTexture == null ||
                           _boundTextureId != textureId ||
                           _externalTexture.width != width ||
                           _externalTexture.height != height;
            if (!changed) return;

            if (_externalTexture != null)
                Destroy(_externalTexture);

            _externalTexture = Texture2D.CreateExternalTexture(
                width, height, TextureFormat.RGBA32, false, false, (IntPtr)textureId);
            _externalTexture.wrapMode = TextureWrapMode.Repeat;
            _boundTextureId = textureId;
            _material.SetTexture("_MainTex", _externalTexture);
            RebuildGeometry(width, height);

            Debug.Log($"[SkyPlayer.Engine] external GL texture bound id={textureId} {width}x{height}");
        }

        private bool IsImmersiveSphere => _projection.Geometry == Geometry.Sphere;

        private void RebuildGeometry(int width, int height)
        {
            if (_display != null) Destroy(_display);

            if (IsImmersiveSphere)
            {
                _display = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(_display.GetComponent<Collider>());
                _display.name = "SkyPlayerEngine.VideoSphere";
                _display.transform.localScale = Vector3.one * 50f;
                _display.GetComponent<Renderer>().sharedMaterial = _material;
                RecenterSphere();
                return;
            }

            _display = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(_display.GetComponent<Collider>());
            _display.name = "SkyPlayerEngine.VideoQuad";

            const float wide = 3.6f;
            float perEyeWidth = _projection.Stereo == Stereo.SBS ? width * 0.5f : width;
            float aspect = height > 0 ? perEyeWidth / height : 1.777f;
            _display.transform.localScale = new Vector3(wide, wide / aspect, 1f);
            _display.GetComponent<Renderer>().sharedMaterial = _material;
            PlaceFlatInFront(_flatDistance);
        }

        private void PlaceFlatInFront(float distance)
        {
            if (_camera == null || _display == null) return;
            Vector3 forward = _camera.transform.forward;
            forward.y = 0;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();

            _display.transform.position = _camera.transform.position + forward * distance;
            _display.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            _display.transform.Rotate(0, 180f, 0);
        }

        private void RecenterSphere()
        {
            if (_camera == null || _display == null) return;
            _display.transform.position = _camera.transform.position;
            _baseRotation = _camera.transform.rotation;
            _panYaw = 0f;
            _panPitch = 0f;
            ApplySphereRotation();
        }

        private void ApplySphereRotation()
        {
            if (_display == null) return;
            _display.transform.rotation =
                _baseRotation *
                Quaternion.AngleAxis(_panYaw, Vector3.up) *
                Quaternion.AngleAxis(_panPitch, Vector3.right);
        }

        private static float SpillStrength(int mode) =>
            mode == 0 ? 0f : mode == 1 ? 0.35f : mode == 3 ? 0.85f : 0.60f;

        private static float SoftenAmount(int mode) =>
            mode == 0 ? 0f : mode == 1 ? 0.45f : mode == 3 ? 0.85f : 0.65f;

        private void EnsureInitialized()
        {
            if (_camera == null || _exo == null || _material == null)
                throw new InvalidOperationException("Call VrPlayer.Initialize(...) before using the player.");
        }

        private void OnDestroy()
        {
            Stop();
            _exo?.Release();
            if (_material != null) Destroy(_material);
        }
    }
}
