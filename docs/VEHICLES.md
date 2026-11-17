# Vehicles

Select with the **vehicle button** (top right, next to Ball), **V** (next vehicle) or **D-pad right**. `P` still toggles the Cessna.

| Vehicle | Model | Touch controls (besides the stick) | Keys |
|---|---|---|---|
| Car | RocketSim | JUMP, BOOST, ROLL ◀ ▶, HYPER | unchanged |
| Cessna | JSBSim (existing) | throttle slider, rudder slider, BRAKE, HYPER | W/S elevator, A/D aileron, Q/E rudder, Space/Ctrl throttle, Shift brakes |
| F-35B | `F35B.glb` | throttle slider, rudder slider, **LIFT ▲ / LIFT ▼** (vertical throttle), **VTOL** toggle, AFTERBURNER, BRAKE, HYPER | M = VTOL, Space/Ctrl = lever (or lift in VTOL) |
| F-15E | `F15E.glb` | throttle slider, rudder slider, AFTERBURNER, BRAKE, GEAR, HYPER | G = gear, Shift = afterburner |
| Helicopter | `Heli.glb` + procedural rotors | collective slider (springs back to centre = hover hold), rudder slider, BRAKE, HYPER | Space/Ctrl = collective |
| DeLorean | `DeLorean.glb` | UP ▲ / DOWN ▼, HOVER toggle, BOOST, BRAKE, HYPER | M = hover (wheels fold flat), Space/Ctrl = up/down |

* Throttle slider: drag to set the lever; it keeps its value and follows the keyboard / gamepad. Rudder slider springs back to centre.
* **Hyper Boost** (H, D-pad up, or the HYPER button which latches on/off): the vehicle's normal top speed ×6 (+500 %), eased in over 0.6 s and out over 1.8 s. Works on every vehicle (car, boat, Cessna included).
  While it is on, ground colliders are loaded in burst mode; at thousands of km/h the streamed terrain can still lag behind the vehicle.
* Controls panel lists the new rebindable actions: next vehicle, Hyper Boost, VTOL / hover, gear, flaps.

## Code map (`Assets/Scripts/Runtime/Vehicles/`)

* `VehicleSpec.cs` - `VehicleKind`, `TouchProfile`, per-vehicle table (model, mass, inertia, collision boxes, wheel / skid points, camera, aero + engine numbers), `HyperBoost`, `Atmosphere`.
* `VehicleDriver.cs` - mode switching (the car's Rigidbody is re-purposed like the plane does), colliders, model, HUD numbers, `HyperDrive`.
* `VehicleVisual.cs` - loads the GLB, fixes orientation from the `Marker_*` nodes, animates gear / DeLorean wheels / helicopter rotors / afterburner / hover glow.
* `AircraftModel.cs` - **the physics seam** (`IVehicleModel`, `VehiclePhysicsFactory`) and `PlaceholderPhysics`.
* `../TouchLayout.cs` + `../CarInput.cs` - widget-based touch controls; `Controls` gained `leverSet/lever`, `vert`, `hyper`, `wheelBrake`.

## Plugging in the real (JSBSim) physics

Everything except the per-tick physics is done. `PlaceholderPhysics` only lets every vehicle be driven with every control.
Implement `IVehicleModel` for each vehicle and return it from `VehiclePhysicsFactory.Create(spec)`:

* `Enter(d, rb, airborne)` - initial state (the driver has already set mass, inertia, gravity off, colliders, pose and velocity).
* `Step(d, rb, c, dt)` - called at 120 Hz before `Physics.Simulate`; read `c` (`throttle` = stick forward/back, `steer` = stick left/right, `roll` = rudder slider / Q E,
  `lever` + `leverSet` = throttle slider, `jump` / `handbrake` = lever up / down keys, `boost` = afterburner / reverse / turbo, `vert` = vertical throttle,
  `wheelBrake`), add forces to `rb`, call `d.ApplyHyperDrive(rb, dir, spec.vMax, dt, steer)` for Hyper Boost, and set `d.Viz` (gear, wheel fold, rotor rpm, burner).
* `Toggle(d, what)` - Mode (VTOL / hover), Gear, Flaps button presses.
* `Lever`, `OnGround`, `AlphaDeg`, `Stalled`, `Status` - HUD.

`docs/vehicle-physics-draft/` holds an earlier hand-written attempt (jets with VTOL, helicopter, DeLorean hover) that can serve as reference; it is not compiled.
For Hyper Boost with real aerodynamics, cap the dynamic pressure at ~1.25× the normal top speed's (as `PlaneDriver` now does for the Cessna) so lift and control authority stay sane.

## Models

`Assets/Resources/Models/Vehicles/*.glb` come from the five source models via `tools/models/prep.mjs` (`npm i` in that folder, then
`node prep.mjs all <sources> Assets/Resources/Models/Vehicles`): nose +Z, up +Y, metres, origin at the bounding-box centre, materials reduced to base colour (the game
rebuilds them as URP Lit), textures to 1024 px JPEG, geometry simplified (DeLorean 2.3 M → ~90 k triangles; a few unsimplifiable grille details were dropped).
Named parts: `Gear_Down` / `Gear_Up` (F-15E), `Wheel_F0/F1/R0/R1` → `Spin` (DeLorean), and `Marker_Origin/Nose/Up` (orientation).
The licences of the source models were not part of the upload: check them (Sketchfab pages) before publishing, and add credits to `THIRD_PARTY.md`.

## Network

Pose flags bits 5-7 carry the vehicle kind (bit 4 = aircraft / vehicle mode as before), so other players see the right model. Vehicle switching is host / solo only, like plane mode.

## Starship, Megaliner and Antonov An-124 (added)

All three are JSBSim vehicles (`tools/flight3/gen_big_vehicles.py` writes the files into `Assets/Resources/Flight/jsb/`; the C# port in `Assets/Scripts/Sim/Flight` runs them, `tools/flight2/run_all.sh` checks it frame by frame against native JSBSim).
Vehicles can only be selected by the solo player / the match creator (`carCtl.simulated`), so the Tsar Bomba can only be set off there too.

| | model | JSBSim file | engines | notes |
|---|---|---|---|---|
| **Starship** | `starship_mk1.glb`, 50 m | `starship.xml`, `raptor.xml` | 6 × native liquid `<rocket_engine>` (Isp 327 s SL → 360 s vac, 3.6 O/F, 2.26 MN each) | stands on its tail (pitch 90°), 120 t dry + 544 t propellant (separate FUEL / OXIDIZER tanks that really drain), rate-command SAS (engine gimbal ±11° + RCS moments), 3 landing skids |
| **Megaliner** | `Meshy_AI_Skyborne_Megaliner…glb`, **293 m long, 307 m span, 93 m tall = Statue of Liberty (ground to torch)** | `megaliner.xml`, `megafan.xml` | 8 × turbofan (JSBSim JT9D-3 tables, 485 klbf each) | 8 000 t, 16 000 m² wing, rotates at ~160 kt after ~2 km, no gear mesh (belly skids), flaps / spoilers / thrust reverse |
| **Antonov An-124** | `antonov.glb`, 69 m | `an124.xml`, `d18t.xml` | 4 × D-18T class turbofans (229.7 kN) | tricycle gear, flaps, spoilers, reverse; **rear ramp is its own model node** |

Controls (keyboard): throttle lever = Space / Ctrl (as the other planes), **G** gear / rocket legs, **F** flaps, **M** spoilers, **O** cargo ramp (Antonov; touch button `RAMP`).

* **Rocket crash.** The hull colliders carry a `VehicleCrashSensor`; a hit with the world faster than `crashSpeed` (4 m/s relative) destroys the rocket. The legs are ray springs below the hull, so a slow, upright touch-down is survived. Solo / match creator: `TsarBomba` (50 Mt: flash, 4.6 km fireball, shock ring, 64 km mushroom, 15 km blast that throws other bodies, rumble, camera shake), also broadcast to the joined players (`INetRole.BroadcastExplosion` → `PlayerNet.ExplosionRpc`). The player is back in the car at the crash site.
* **Antonov hold.** Invisible PlaneLayer boxes (deck, walls, ceiling, bulkhead; same trick as the intro cargo plane) plus a ramp collider on a hinge that turns with the model node `Ramp` (open 0° … shut 34°, 9 s). Other players' cars (host-simulated) can drive up the ramp and ride inside; the pilot shuts the ramp and takes off. The ramp opening travels in `PoseSnap.aux`, the vehicle kind in `PoseSnap.veh`.
* Models are prepared by `tools/models/prep.mjs` (jobs `Starship`, `Megaliner`, `Antonov`); the Megaliner height constant is `STATUE_M = 93`.
