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
| Airliner | `A320.glb` | throttle slider, rudder slider, BRAKE, REVERSE, GEAR, FLAPS, HYPER | G gear, F flaps, Shift reverse |

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
rebuilds them as URP Lit), textures to 1024 px JPEG, geometry simplified (DeLorean 2.3 M → ~90 k triangles; a few unsimplifiable grille details were dropped; A320 cockpit interior removed).
Named parts: `Gear_Down` / `Gear_Up` (F-15E), `Gear` (A320), `Wheel_F0/F1/R0/R1` → `Spin` (DeLorean), and `Marker_Origin/Nose/Up` (orientation).
The licences of the source models were not part of the upload: check them (Sketchfab pages) before publishing, and add credits to `THIRD_PARTY.md`.

## Network

Pose flags bits 5-7 carry the vehicle kind (bit 4 = aircraft / vehicle mode as before), so other players see the right model. Vehicle switching is host / solo only, like plane mode.
