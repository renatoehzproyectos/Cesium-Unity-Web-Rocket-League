# Roadmap

| # | Phase | Weight | Status |
|---|-------|-------:|--------|
| 1 | Foundation: repo, Unity build script, CI, Vercel hosting | 10 | **done (untested in Unity)** |
| 2 | World: Cesium georeference + Google 3D Tiles/ion, credentials UI, locations, tile tuning | 15 | **done (untested)** |
| 3 | Orbit camera + showcase car, UX: share links, host-header check, settings persistence, touch | 10 | **done (untested)** |
| 4 | Look: RocketWorld flat sky/fog/baked lighting, procedural sky + time of day, car colours | 10 | **done (untested)** |
| 5 | Performance: device profile, adaptive 60 FPS controller, benchmark, caching, IL2CPP speed, WebGPU switch | 15 | **done (untested)** |
| 6 | Physics: RocketSim car model ported (line by line) onto PhysX, exact tile collision, chase camera, touch driving | 30 | **done (model verified vs native RocketSim; PhysX side untested)** |
| 7 | QA: parity regression vs native RocketSim, EditMode tests, CI gates | 10 | **done (CI jobs untested on GitHub)** |

**All planned phases are implemented = 100 % of the planned scope.** "Done" means written and, where noted, verified against native
RocketSim; it does *not* mean run in Unity: that first build is yours (see README) and is expected to need a fix pass.

## Known risks (first build)
- C# in `Assets/Scripts/Runtime` and `Editor` has never been compiled by Unity (the physics files were checked against stubs).
- PhysX-vs-Bullet contact feel (see `docs/PHYSICS.md`), gyroscopic compensation, collider cooking hitches on Web.
- `PlayerSettings.WebGL.*` property names differ between 6000.0.x patches; the build script tolerates missing ones and logs them.
