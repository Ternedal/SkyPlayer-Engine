# Kaliv ecosystem integration

Documentation snapshot: 2026-10-23. SkyPlayer-Engine provides shared Unity/OpenXR media playback primitives. The original [Skyplayer](https://github.com/Ternedal/Skyplayer) remains the reference application until migration is verified.

## Ecosystem

[ModelRig](https://github.com/Ternedal/ModelRig) coordinates backend orchestration and authenticated media access. [Kaliv](https://github.com/Ternedal/Kaliv) is the product website; Kaliv VR is a prospective engine consumer. [KalivNexus](https://github.com/Ternedal/KalivNexus) is a **proposed** external API broker, not a verified engine integration. [BodyRig](https://github.com/Ternedal/BodyRig), [VoiceRig](https://github.com/Ternedal/VoiceRig) and [VisionRig](https://github.com/Ternedal/VisionRig) retain their own subsystem contracts.

## Compatibility and safety

- Never assume that clients share ModelRig's filesystem: generated images/videos require authenticated media retrieval and configurable external storage.
- Verify platform compatibility and physical Quest playback before asserting production support.
- Pin exact commits and test evidence; documentation alone does not change release readiness.
- Do not commit user media, credentials or proprietary training sources.

## Verification backlog

- [ ] Document Unity/OpenXR versions and package integration from source.
- [ ] Verify playback controls and Quest performance on physical hardware.
- [ ] Verify ModelRig media integration and failure recovery.
