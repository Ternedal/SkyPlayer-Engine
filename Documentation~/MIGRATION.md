# Migration plan

## Boundary

SkyPlayer-Engine owns playback and XR mechanics. A client owns catalog/API integration, branding, UI and product settings.

## Phase 1 — engine bootstrap

Move the proven generic primitives without changing the production Skyplayer client:

- ExoVideo
- Projection
- StereoEquirect shader
- PassthroughFeature
- Java Media3 bridge
- native GLES3 bridge

The Android pieces are grouped into one .androidlib so Media3 dependencies live with the engine instead of requiring every client to maintain Skyplayer's custom mainTemplate.gradle.

## Phase 2 — high-level player split

Extract from the current Skyplayer VrVideoPlayer:

- playback state machine
- play / pause / seek
- external texture lifecycle
- projection geometry
- zoom and focus pan
- format switching
- AR/background-key uniforms
- decoder error/fallback hooks

Keep in the Skyplayer adapter:

- SceneDto
- CompanionClient
- SettingsStore
- progress persistence
- Stash resume semantics
- UI labels and theme behavior

Target shape:

```csharp
var player = gameObject.AddComponent<VrPlayer>();
player.Initialize(camera, options);
player.Open(new MediaSource(streamUrl, resumePosition), projection);
```

## Phase 3 — Skyplayer consumes the package

1. Add dk.ternedal.skyplayer.engine to Skyplayer.
2. Adapt product code to the engine API.
3. Remove duplicate native/plugin/shader files only after a Quest build passes.
4. Verify VR180 SBS, seek, pan, passthrough and AR keying on-device.
5. Pin the engine to a release tag instead of floating main.

## Phase 4 — Kaliv-VR

Kaliv-VR starts as a thin Unity/OpenXR client over the same engine. No fork of playback code.
