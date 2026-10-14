# RocketWorld (Unity) - drive a Rocket League car on the real world, in the browser

Unity 6 **Web** project: Cesium for Unity streams Google Photorealistic 3D Tiles and you drive a Fennec with **RocketSim's car physics running
on Unity PhysX**, so collision against buildings and terrain is exact (real mesh colliders), not approximated.

**Status: 100 % of the planned scope (see `docs/ROADMAP.md`).** Read the honesty note: nothing has been compiled or run *in Unity* yet.

> **Honest status.** My sandbox has no Unity/Vercel. What *is* verified, with real tools: the RocketSim vehicle model (`Assets/Scripts/Sim`) was
> compiled and compared tick by tick against native RocketSim built from source (<= 0.04 uu error on 8 scenarios, see `docs/PHYSICS.md`), the 12
> EditMode tests pass in a stub harness, the physics-facing Unity scripts type-check against stubs, and the JSON/YAML parse. **Not verified:** the
> Unity compile of the rest, PhysX contact feel, the WebGL build, Vercel, Google tiles. Expect a first-build fix iteration and send me the log.

## Controls
WASD/arrows drive · Space jump (twice = flip, with direction = dodge) · Shift boost (infinite) · Ctrl/X drift · Q/E air-roll · C camera · R rescue ·
Esc / gear = settings · link button copies a URL for this place (never includes your API key). Mobile: on-screen pads. Gamepad supported.
URL params: `?key= ?ion= ?lat= ?lon= ?h= ?hdg= ?t= ?car= ?sse= ?scale= ?bench=1`.

## Deploy: GitHub -> Vercel
1. Push this folder to `main` of a new GitHub repo.
2. **Unity license for CI** (Settings -> Secrets -> Actions). Personal: follow https://game.ci/docs/github/activation and add `UNITY_LICENSE`, `UNITY_EMAIL`,
   `UNITY_PASSWORD` (or `UNITY_SERIAL` for Pro/Plus). Without them CI still runs the static checks and the **parity** job; the Unity jobs are skipped with a warning.
3. Push -> Actions: static checks, parity vs native RocketSim, Unity EditMode tests, WebGL build -> publishes to branch **`release`**.
   (Run the workflow manually to choose WebGPU instead of WebGL 2.)
4. Vercel -> *Add New Project* -> import the repo -> **Production Branch = `release`**, Framework *Other*, no build command, output `.`.
   `vercel.json` sets COOP/COEP (needed by Cesium's threaded WASM) and caching; the page shows a red banner if the headers are missing.
5. Open the site -> gear -> paste your Google Maps Platform key (Map Tiles API enabled; restrict by HTTP referrer to your Vercel domain).

## Verify the physics yourself
```
bash tools/parity/build.sh /path/to/RocketSim      # builds native RocketSim + the C# port, runs every scenario, fails on divergence
```
In Unity: Test Runner -> EditMode (`SimCoreTests` pins the final state of every scenario to native RocketSim). In the game: settings -> *Run benchmark*
(or `?bench=1`) gives frame-time percentiles and a PASS/BORDERLINE/FAIL verdict for 60 FPS; the HUD (debug) shows wheels/ground/colliders/gyro-compensation.

## Local development
Unity **6000.0.62f1** (Web module). First open creates the URP asset, materials and boot scene (`RocketWorld/Ensure project setup`). Build with
**RocketWorld -> Build WebGL**; serve with COOP/COEP headers (e.g. `vercel dev`). Commit the generated `.meta` files after the first open.

## Layout
```
Assets/Scripts/Sim       engine-free RocketSim port: RocketVehicle, RL constants, CarConfig, interfaces (+ RocketSim MIT notice)
Assets/Scripts/Runtime   GameRoot, RocketCar + PhysX adapter (PhysxBody, ray caster, contact sensor), TileColliderBudget, cameras,
                         CarInput, DebugHud, AutoQuality, BenchmarkRunner, DeviceProfile, GameSettings
Assets/Scripts/Editor    ProjectSetup (URP asset/scene/materials from code), BuildScript (WebGL settings, CI entry, index.html check)
Assets/Tests/EditMode    SimCoreTests (golden values from native RocketSim), AutoQuality + settings tests, ReferenceRig
tools/parity             native RocketSim reference + C# harness + scenarios + comparer
deploy/vercel.json       headers for the hosted build
docs/                    PHYSICS (design, what is matched/verified/unverifiable), ROADMAP
```
