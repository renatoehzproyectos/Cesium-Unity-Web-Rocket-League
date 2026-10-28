# Vehicle physics draft (not compiled)

`Assets/Scripts/Runtime/Vehicles/AircraftModel.cs` only holds the seam (`IVehicleModel`, `VehiclePhysicsFactory`) and a **placeholder** model.
The files here are an earlier, untested attempt at real models, kept as reference for the JSBSim work:

- `AircraftModel.cs.txt` - jets + airliner (stall-curve lift, wave drag, afterburner, rate-command controls with g limit, VTOL lift fan / nozzle for the F-35B).
- `HelicopterModel.cs.txt` - attitude-command cyclic, vertical-speed collective with hover hold, skids; also contains the DeLorean (wheels + anti-gravity hover).
- `VehicleWheels.cs.txt` - ray-cast strut / tyre contacts and `HoverCore` (attitude command + vertical-speed hold).

They are written against `VehicleSpec` (mass, inertia, wheels, aero numbers) and `VehicleDriver.ApplyHyperDrive`; they compiled against a stub of the Unity API only.
