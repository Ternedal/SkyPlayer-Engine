# SkyPlayer Engine

Shared Unity/OpenXR VR playback engine, extracted from [Ternedal/Skyplayer](https://github.com/Ternedal/Skyplayer). It is a Unity package (`dk.ternedal.skyplayer.engine`) that gives a client video playback on Android/Quest, flat and spherical projection, and Meta Quest passthrough.

The engine owns playback and XR mechanics. A client owns catalog and API integration, input mapping, UI, branding and settings persistence.

## Status

- Package version `0.1.0`. There are no tags or releases; consumers pin a commit.
- The repository has no automated tests and no CI. Nothing here is evidence of a passing Unity build or of playback on a physical headset.
- [Kaliv VR](https://github.com/Ternedal/ModelRig/tree/main/vr) consumes the package, pinned to a commit in `vr/Packages/manifest.json`.
- The Skyplayer application remains the reference client for the original implementation until its adapter has been migrated. See [Documentation~/MIGRATION.md](Documentation~/MIGRATION.md).

## Requirements

| | |
|---|---|
| Unity | 6000.0 or newer (`package.json`) |
| Unity packages | `com.unity.xr.openxr` 1.16.1, `com.unity.modules.androidjni` 1.0.0 |
| Android | `minSdk` 29, `compileSdk` 34 |
| Android NDK | 27.2.12479018, CMake 3.22.1 |
| Media3 | `media3-exoplayer`, `media3-datasource`, `media3-common` 1.4.1 |

The Android pieces live in one library module, `Runtime/Plugins/Android/SkyPlayerEngine.androidlib`, so the Media3 dependencies travel with the engine. The module resolves the NDK from `ANDROID_NDK_ROOT`, or from `UNITY_EDITOR_PATH` if that is set. Its manifest requests `android.permission.INTERNET` and declares `com.oculus.feature.PASSTHROUGH` as optional.

## Install

Add the package to the client's `Packages/manifest.json` with a Git URL pinned to a commit:

```json
{
  "dependencies": {
    "dk.ternedal.skyplayer.engine": "https://github.com/Ternedal/SkyPlayer-Engine.git#<commit-sha>"
  }
}
```

Pin a full commit SHA rather than `main`, so a build is reproducible.

To use passthrough, enable **SkyPlayer Engine Passthrough (FB)** under Project Settings ▸ XR Plug-in Management ▸ OpenXR for the Android build target. The feature is only built into the player when it is enabled there.

## Use

Everything is in the `SkyPlayer.Engine` namespace (assembly `Ternedal.SkyPlayer.Engine`).

```csharp
using SkyPlayer.Engine;

var player = gameObject.AddComponent<VrPlayer>();
player.Initialize(camera, VrPlayerOptions.Default);
player.Open(new MediaSource(streamUrl, resumePositionSeconds), Projection.FromHint("VR180_SBS"));
```

### `VrPlayer`

A `MonoBehaviour` that owns Media3/ExoPlayer playback, the external GPU texture, the flat or spherical display geometry, projection, zoom, pan, recentering and the background-key shader parameters. It knows nothing about catalogs, progress persistence, themes or controller mappings.

| Area | Members |
|---|---|
| Setup | `Initialize(camera, options)`, `SetPresentationRoot(root, layer)` |
| Media | `Open(source, projection)`, `Stop()` |
| Transport | `Play()`, `Pause()`, `TogglePlayPause()`, `Seek(fraction01)`, `SeekRelative(seconds)`, `SeekToSeconds(seconds)` |
| Projection | `ApplyProjection(projection)`, `SetFormatHint(hint)` |
| View | `SetDefaultZoom(zoom01)`, `SetZoom01(zoom01)`, `AdjustZoom(delta)`, `Pan(yawDegrees, pitchDegrees)`, `Recenter()`, `ApplyOriginShift(delta)` |
| Background key | `ApplyBackgroundKey(settings)`, `TrySampleKeyColorAtGaze(out color)` |
| State | `IsPrepared`, `IsPlaying`, `IsEnded`, `Length`, `Time`, `FormatLabel`, `Projection`, `Zoom01`, `Display` |
| Events | `StateChanged`, `DisplayChanged`, `Ended` |

`StateChanged` carries a state string: `loading`, `playing`, `ended`, or `error:<message>`.

`SetPresentationRoot` lets the client decide which transform and layer the generated media geometry belongs to. A client that never calls it gets the geometry at the world root on the default layer.

### Projection

`Projection.FromHint(hint)` resolves a format hint. An unknown or missing hint gives flat 2D.

| Hint | Result |
|---|---|
| `FLAT` | Flat, mono |
| `SBS_3D` | Flat, side-by-side stereo |
| `VR180_MONO`, `VR180_SBS` | 180° sphere, mono or side-by-side |
| `VR360_MONO`, `VR360_SBS` | 360° sphere, mono or side-by-side |
| `OU_TB` | 180° sphere, over-under stereo |

`Projection.Cycle` lists the hints in the order a manual format button steps through them.

### Passthrough

`PassthroughFeature` is an OpenXR feature (`dk.ternedal.skyplayer.engine.passthrough`) that uses the `XR_FB_passthrough` extension directly, without the Meta XR SDK or AR Foundation. Call `PassthroughFeature.SetEnabled(bool)` at runtime; `Supported` and `Running` report the state. On a runtime that does not expose the extension, enabling it does nothing.

### Lower-level pieces

- `ExoVideo` is the C# bridge to the Java plugin `dk.ternedal.skyplayer.engine.ExoVideoPlugin` and the native GLES3 video bridge. `VrPlayer` uses it; most clients do not need it directly.
- `Resources/SkyPlayerEngine/StereoEquirect.shader` is the stereo and equirectangular video shader. It is kept under `Resources` so that it is included in player builds.

## Repository layout

| Path | Contents |
|---|---|
| `Runtime/Playback/` | `VrPlayer`, `VrPlayerOptions`, `MediaSource`, `ExoVideo` |
| `Runtime/Projection/` | `Projection`, `Geometry`, `Stereo` |
| `Runtime/XR/` | `PassthroughFeature` |
| `Runtime/Plugins/Android/SkyPlayerEngine.androidlib/` | Java Media3 bridge, native GLES3 bridge, Gradle and CMake build |
| `Runtime/Resources/SkyPlayerEngine/` | Video shader |
| `Documentation~/MIGRATION.md` | Engine/client boundary and the migration phases |
| `CHANGELOG.md` | Changes by version |

## Related repositories

- [Ternedal/ModelRig](https://github.com/Ternedal/ModelRig) — contains Kaliv VR (`vr/`), which consumes this package. `vr/RENDERING.md` describes how Kaliv wraps `VrPlayer`.
- [Ternedal/Skyplayer](https://github.com/Ternedal/Skyplayer) — the application the engine was extracted from (private repository).

## License

MIT. See [LICENSE](LICENSE).
