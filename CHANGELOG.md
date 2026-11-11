# Changelog

## Unreleased

Changes on `main` after the 0.1.0 entry below was written. The package version is still `0.1.0`.

### Added
- `VrPlayer`: product-neutral high-level player with open, play, pause, seek, projection switching, zoom, pan, recentering, origin-shift compensation and background keying (`8e3b550`).
- `MediaSource` and `VrPlayerOptions` / `BackgroundKeySettings` (`fb50014`, `657b2f0`).
- `VrPlayer.SetDefaultZoom` for the zoom applied when the next item opens (`9b42a9f`).
- `VrPlayer.SetPresentationRoot`, so a client owns the transform and layer of the generated media geometry (`d0c2174`).
- Runtime assembly definition `Ternedal.SkyPlayer.Engine` and the `com.unity.modules.androidjni` dependency (`e4ef386`, `8c63105`).

### Changed
- The video shader lives under `Resources/SkyPlayerEngine/` and is loaded from there, so it is included in player builds (`391e0aa`, `e07eadb`, `300f99f`).
- The native library and Java plugin use the `dk.ternedal.skyplayer.engine` namespace (`c750e5d`, `7f6c09d`, `ede7171`).

### Fixed
- A change of video resolution no longer changes the GL texture id (`8a6d169`).
- The runtime assembly no longer references editor-only code (`c651c37`).
- Passthrough fails closed when the OpenXR runtime exports are incomplete (`a6c0087`).

This resolves two items from the 0.1.0 "Not yet migrated" list: high-level player orchestration and tracking-origin compensation (`VrPlayer.ApplyOriginShift`). Input mapping, UI and Stash integration stay in the clients by design; see [Documentation~/MIGRATION.md](Documentation~/MIGRATION.md).

## 0.1.0 - 2026-09-22

Initial engine bootstrap extracted from the working Skyplayer implementation.

### Added
- VR projection primitives.
- Android Media3/ExoPlayer C# bridge.
- Native GLES3 zero-copy video bridge.
- Stereo/equirectangular video shader.
- Raw OpenXR XR_FB_passthrough feature.
- Self-contained Android library module with Media3 dependencies.

### Not yet migrated
- High-level VrVideoPlayer orchestration.
- Tracking-origin compensation.
- Client input mapping and UI.
- Stash companion/API integration.
