# Roadmap (re-baselined)

**Scope change at 75 %:** all collision, physics, car movement and height sampling were removed. RocketSim, the native
plugin, the floor/wall/collider code and the driving controls are gone (they live on in the 50 % zip). The project is now a
render-only Cesium for Unity web viewer with a showcase Fennec and an orbit camera, so the percentages below are measured
against that scope.

| # | Phase | Weight | Status |
|---|-------|-------:|--------|
| 1 | Foundation: repo, Unity build script, CI, Vercel hosting | 15 | **done (untested)** |
| 2 | World: Cesium georeference + Google 3D Tiles/ion, credentials UI, locations, tile tuning | 20 | **done (untested)** |
| 3 | Showcase car + orbit camera (mouse, touch, keyboard) | 15 | **done (untested)** |
| 4 | Look: RocketWorld-matching flat sky/fog/baked lighting, procedural sky + time of day, car colours | 15 | **done (untested)** |
| 5 | UX: share links, host-header check, settings persistence, mobile input | 10 | **done (untested)** |
| 6 | Performance: profile on real hardware, memory caps, WebGPU, 60 FPS at max settings | 15 | next |
| 7 | QA/hardening: Play Mode/build tests, cross-browser matrix, loading screen | 10 | |

**Phases 1-5 = 75 %.**

## Known limitations at 75 %
- The car does not move and nothing is solid; ground height is a manual setting (preset values are approximate: the
  ellipsoid height of the street depends on geoid + terrain, so adjust the slider until the wheels touch the road).
- `com.unity.modules.physics` stays in the manifest only because Cesium for Unity itself references it; no project code uses it
  (CI fails if `Physics.`/`Rigidbody`/`MeshCollider` appear in `Assets/Scripts`).
- Nothing has been compiled or run in Unity yet.
