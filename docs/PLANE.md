# Plane mode (JSBSim flight model)

Press **P** (or the **Plane** button; pad: L3) to turn the car into a small airplane built from boxes; press again to come back as a car.
Start on the ground and take off, or switch in the air (40 m/s, throttle 55%). Plane mode and boat mode never overlap.

| Control | Action |
|---|---|
| W / S | elevator (S = nose up) |
| A / D | ailerons (nose wheel steering on the ground) |
| Q / E | rudder |
| Space / Ctrl | throttle lever up / down (persistent) |
| Shift | wheel brakes |

HUD: IAS (kt), altitude (ft, ellipsoidal), vertical speed, throttle bar, STALL warning.

## What is JSBSim here
`Assets/Resources/Flight/boxplane.xml` is a real JSBSim aircraft file (it also loads in native JSBSim: copy it to `aircraft/boxplane`). Its mass,
inertia, geometry and aerodynamic derivatives are simplified from JSBSim's c172x; engine = JSBSim electric engine (160 hp) + the c172x
fixed-pitch propeller tables. `Assets/Scripts/Sim/Flight/JsbModel.cs` is a C# port of the pieces of JSBSim needed to fly it (engine-free):
standard atmosphere, wind angles, the aerodynamics axes with the `<function>` / table evaluator, `aerosurface_scale`, electric engine, propeller
(thrust and power tables, helical-tip-Mach effects, P-factor, gyroscopic moment) and the gear strut spring/damper.
PhysX integrates the rigid body (instead of JSBSim's integrator) and does the collisions; gravity is real (9.81 m/s2) for the plane only.

## Verified (`bash tools/flight/run.sh`, CI job `flight`)
- Frame by frame against **native JSBSim** (built from the source you provided; golden logs in `tools/flight/golden`, 6 scenarios x 300 frames
  from windmilling prop start to full power, high alpha, sideslip, dive): atmosphere, alpha/beta, qbar, aero forces and moments agree to 1e-12;
  propeller thrust, moments and rpm agree to <= 0.02 lbf*ft (JSBSim adds Earth's rotation to the gyroscopic term, the port does not).
- Free flight with a plain 6-DOF integrator: trim at 100 kt (alpha 0.6 deg, L/D 9.7), bounded hands-off flight with a slow spiral, stall 44 kt.
- Body-frame mapping (JSBSim x fwd / y right / z down to Unity x right / y up / z forward is a reflection: axial vectors flip) in `FlightModelTests`.
Regenerate the golden logs with `tools/flight/gen_golden.py` (needs a JSBSim build).

## Not verified / approximations
- **Nothing that needs Unity was compiled or run here**: PlaneDriver, PlaneVisual, the HUD and the integration into RocketCar need a first-build fix pass.
- Gear friction is a simplified tire model (JSBSim's Lagrange-multiplier friction solver is not ported); the strut spring/damper is JSBSim's formula.
- No flaps, no fuel, no wind or turbulence, no ground effect, no actuators (surfaces follow a rate limit in the driver).
- The tile colliders are only streamed around the car (260 m): at high speed fly low only when you mean to land.
- Water is solid for the plane (no floatplane).
