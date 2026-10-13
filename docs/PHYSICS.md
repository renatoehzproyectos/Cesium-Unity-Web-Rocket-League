# Physics: RocketSim's car model on Unity PhysX

**Goal:** drive exactly like RocketSim, but with PhysX doing the rigid-body integration and the *exact* collision against the real
Google 3D Tiles meshes (no ray-sampled walls, no fitted floor plane).

## Architecture
```
RocketWorld.Sim  (engine-free C#, asmdef noEngineReferences)      <- line-by-line port of RocketSim
  RocketVehicle   Car.cpp + btVehicleRL.cpp: 4 raycast wheels, suspension, tyre friction, throttle/brake/steer, handbrake,
                  boost, jump, double jump, flips, air control, auto-flip, auto-roll, supersonic, speed limits
  RL / CarConfig  RLConst.h + the Octane (Fennec) config; Bullet's effective hitbox & inertia
  IBody / IRayCaster   what the model needs from an engine

RocketWorld.Runtime (Unity)                                         <- the PhysX adapter
  PhysxBody        IBody over a Rigidbody (sim-space cache, immediate impulses, AddForce/AddTorque for forces)
  PhysxRayCaster   Physics.Raycast against the tile colliders (car layer excluded)
  RocketCar        Rigidbody + BoxCollider hitbox + 120 Hz loop + interpolation + origin shift + spawn/rescue
  TileColliderBudget  MeshColliders only near the car (Cesium's own physics meshes bake every tile on the main thread)
```
Tick order is `Arena::Step`: `PreTick` (vehicle forces/impulses) -> `Physics.Simulate(1/120)` -> `PostTick` -> `FinishTick` (speed limits).

## Units and axes
Bullet units are metres (1 bt = 50 uu; gravity -650 uu/s^2 = -13 m/s^2, car mass 180). The model runs in RocketSim space
(X forward, Y right, Z up) and the adapter converts with `u = (sim.y, sim.z, sim.x)`. That is a cyclic permutation (det +1): cross
products, torques and angular velocities keep their meaning with no sign fixes. (Unity's X = right, Y = up, Z = forward.)

## What was matched on purpose
| RocketSim / Bullet | PhysX setup |
|---|---|
| Fixed 120 Hz, one step per tick | `Physics.simulationMode = Script`, `Physics.Simulate(1/120)` |
| Torques are applied as `I_world * accel` | model computes the world torque, `AddTorque(..., Force)` with the same inertia tensor |
| Inertia from the *Bullet* box shape | `LocalInertia`: Bullet shrinks the box by `2*(0.04 - margin)` = 0.134 uu per axis (found by comparing `m_invInertiaLocal`) |
| Collision box = same shrunken box, offset (13.88, 0, 20.76) uu | `BoxCollider` of `EffectiveHitboxSize`, centre = hitbox offset, CoM at the body origin |
| Car-world friction 0.3, restitution 0.3 | `PhysicsMaterial` 0.3/0.3 on car and tile colliders, `Average` combine |
| `applyImpulse` changes velocity immediately | `PhysxBody` edits cached velocities and writes them back before the step |
| Bullet runs without gyroscopic torque | `PhysxCalibration` spins a torque-free body at startup; if PhysX precesses it, `tau = w x (I w)` is added to cancel it |
| Solver iterations 10, restitution threshold 0.2 | `Physics.defaultSolverIterations`, `Physics.bounceThreshold` |
| `m_solverInfo.m_timeStep` is still 1/60 on tick 0 | replicated in `RocketVehicle` (only matters for the first wheel push-back) |
| Wheel rays hit the world only | car layer 30 excluded from the ray mask |
| Auto-flip / auto-roll use the car-world contact normal | `CarContactSensor` forwards `OnCollisionEnter/Stay` normals |

## Verified (here, with the real RocketSim built from source)
`tools/parity` runs the **same per-tick controls** through native RocketSim and through `RocketVehicle` (with a Bullet-style integrator) and
compares positions, velocities, angular velocities and orientation every tick:

| scenario | max position error |
|---|---|
| drive, steer, boost (infinite floor), powerslide, reverse | <= 0.01 uu |
| jump + air control, front flip, side flip | <= 0.04 uu |
| boost up a 12 deg ramp + jump | 0.005 uu |
| drive over a ramp crest and land | ~8 uu after 6 s (edge tie-breaking at the crest) |

The static-rest transient is also reproduced once the floor is a triangle mesh (a box floor made Bullet's own ray cast imprecise by ~2 mm).
`SimCoreTests` pins the Unity project to those numbers (final positions of every scenario) and runs in CI (`parity` job + Unity EditMode job).

## NOT verifiable without Unity (be aware)
- PhysX vs Bullet **contact response** (body against buildings, restitution, depenetration, CCD) cannot be bit-identical: different solvers.
  Friction/restitution/mass/inertia/shape are matched; feel can still differ at hard impacts.
- The gyroscopic detection threshold (0.02 rad/s over 0.25 s) and the compensation are untested against a real PhysX build.
- `Physics.Raycast` vs Bullet's mesh ray test may break ties differently on shared triangle edges.
- Nothing in `Assets/Scripts/Runtime` has been compiled by Unity; the physics files were type-checked against stubs only.

## Not ported (RocketSim features outside this game)
Ball and ball hits, car-car bumps/demos, boost pads, boost recharge, mutators, the other hitboxes (Dominus, Plank...). They are data/logic
additions to `CarConfig` / `RocketVehicle`; the Fennec is the Octane hitbox.

## Tuning knobs
`TileColliderBudget` (radius, adds per cycle), `PhysxBody.cancelGyroscopic`, `RocketCar.infiniteBoost`, `Physics` values in `PhysicsSetup.Apply`.
