# Rocket World - drive a Rocket League car on the real world, in the browser

Unity 6 **Web** project: Cesium for Unity streams Google Photorealistic 3D Tiles and you drive a Fennec with **RocketSim's car physics running
on Unity PhysX**, so collision against buildings and terrain is exact (real mesh colliders), not approximated.

**Status: 100 % of the planned scope (see `docs/ROADMAP.md`).** Read the honesty note: nothing has been compiled or run *in Unity* yet.

> **Build note.** My sandbox has no Unity/Vercel. What *is* verified, with real tools: the RocketSim vehicle model (`Assets/Scripts/Sim`) was
> compiled and compared tick by tick against native RocketSim built from source (<= 0.04 uu error on 8 scenarios, see `docs/PHYSICS.md`), the 12
> EditMode tests pass in a stub harness, the physics-facing Unity scripts type-check against stubs, and the JSON/YAML parse. **Not verified:** the
> Unity compile of the rest, PhysX contact feel, the WebGL build, Vercel, Google tiles. Expect a first-build fix iteration and send me the log.

## Controls
WASD/arrows drive · Space jump (twice = flip, with direction = dodge) · Shift boost (infinite) · Ctrl/X drift · Q/E air-roll · C camera (chase → helicopter → orbit; also in settings; `?cam=heli`) · R rescue ·
Esc / gear = settings · link button copies a URL for this place (never includes your API key). Mobile: on-screen pads. Full gamepad support (any controller the browser exposes; right stick orbits the camera). The **Controls** button next to the gear opens a rebinding screen: click a slot (key, alt key, gamepad) and press the new input; Backspace clears, Esc cancels. Gamepad users press Menu/Start to open it and navigate with the D-pad (A bind, X clear, Y reset, B close). Bindings are stored in this browser. Analog sticks/triggers need the Input System package as the active input handler (set automatically by `ProjectSetup`).
URL params: `?viewcull=0` (load a radius round the car instead of only the view) `?water=0` (no water system) `?key= ?ion= ?lat= ?lon= ?h= ?hdg= ?t= ?car= ?sse= ?scale= ?bench=1`.

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
**RocketWorld -> Build WebGL**; serve the output folder (build/WebGL/WebGL) with COOP/COEP headers (e.g. `vercel dev`). Commit the generated `.meta` files after the first open.

## Multijugador / Multiplayer
Host-authoritative, up to 4 players (1 host + 3), Netcode for GameObjects over WebRTC/PeerJS, browser to browser (join code shown at the top of the screen). Setup and design: `docs/MULTIPLAYER.md`. Code: `Assets/Net`.

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


## Look & UI
Custom web template (`Assets/WebGLTemplates/RocketWorld`: page title "Rocket World", favicon/app icons, branded loader), a dark-glass IMGUI theme
(`UiTheme.cs`), a key-free circular minimap (`Minimap.cs`: top-down render of the already-loaded world; click it to toggle rotate / north-up),
speed readout and a softly tuned chase camera. Changing the product name resets browser-saved settings once.

## Opening sequence
The game starts inside a cargo plane 1 km above the start point (`PlaneIntro.cs`). You can drive around the hold; when the terrain below has loaded
the rear ramp opens (animated) and you drive off and fall. Add `?intro=0` to the URL to skip it. Tuning constants (altitude, ramp angles, hold size)
are at the top of `PlaneIntro.cs`.

## Ball
Press **B** (or the Ball button, or right-stick click on a gamepad) to put the ball in front of the car; press again to bring it back. It is a PhysX
rigidbody tuned to RocketSim's ball (`RocketBall.cs`, constants and the extra car-ball hit impulse in `Sim/RlBall.cs`): mass 30, gravity -650 uu/s^2, restitution 0.6,
friction 0.35, linear damping (1-0.03)^t, max speed 6000 uu/s, max spin 6 rad/s, and RocketSim's car-ball contact (friction 2.0, restitution 0 + the
extra impulse curve). RocketSim never turns the ball; PhysX integrates its orientation from the angular velocity, so it spins on pitch, yaw and roll.
Touching it fires sparkle sprites (`BallSparks.cs`). The boost is now the web game's soft glowing trail (`BoostFx.cs`).

## Water
The car floats and drives like a boat on seas, lakes and rivers, using free OpenStreetMap polygons (OpenFreeMap, no API key). See `docs/WATER.md`; checks: `bash tools/water/run.sh`.

## Plane mode
Press P: a box-built airplane flown by a C# port of JSBSim (checked against native JSBSim). See `docs/PLANE.md`; checks: `bash tools/flight/run.sh`.

## Tiles
View-driven streaming with a large adaptive cache, no holes (coarse tiles outside the view), a look-ahead camera, haze and anisotropic filtering for a sharper, fuller terrain. See `docs/TILES.md`.
