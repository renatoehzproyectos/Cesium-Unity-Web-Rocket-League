using UnityEngine;

namespace RocketWorld
{
    /// <summary>Every drivable thing. Car and Cessna are the originals (RocketSim / JSBSim); the rest are driven by <see cref="VehicleDriver"/>.</summary>
    public enum VehicleKind { Car = 0, Cessna = 1, F35B = 2, F15E = 3, Helicopter = 4, DeLorean = 5, Starship = 6, Megaliner = 7, Antonov = 8 }

    /// <summary>Which on-screen touch layout a vehicle uses (see <see cref="TouchLayout"/>).</summary>
    public enum TouchProfile { Car, Cessna, Jet, Vtol, Helicopter, DeLorean, Drone, Rocket, Airliner, Cargo }

    public enum AeroClass { None, Jet }

    public struct BoxDef
    {
        public Vector3 center, size;
        public BoxDef(float cx, float cy, float cz, float sx, float sy, float sz) { center = new Vector3(cx, cy, cz); size = new Vector3(sx, sy, sz); }
    }

    public struct WheelDef
    {
        public Vector3 pos; public float radius; public bool steer, drive, rear;
        public WheelDef(float x, float y, float z, float r, bool steer = false, bool drive = false, bool rear = false) { pos = new Vector3(x, y, z); radius = r; this.steer = steer; this.drive = drive; this.rear = rear; }
    }

    /// <summary>Static description of a vehicle: model, mass, collision shape, contact points and aerodynamic / engine numbers (SI units, model space: x right, y up, z forward).</summary>
    public sealed class VehicleSpec
    {
        public VehicleKind kind; public string name, model;
        public float mass; public Vector3 inertia;                 // kg, kg m2 about (pitch = x, yaw = y, roll = z)
        public Vector3 com;                                        // centre of mass in model space (the model is shifted so it sits on the body origin)
        public BoxDef[] boxes; public WheelDef[] wheels;
        public float vMax;                                         // m/s, the normal top speed; Hyper Boost targets HyperBoost.Mul x this
        public float camBack, camUp, camLook;                      // chase camera
        public TouchProfile profile;
        public AeroClass aero;
        public bool hasGear, vtol, afterburner, reverse, flaps, hoverCar, rotors;

        // aerodynamics (jets)
        public float wingArea, cd0, kInd, clAlpha, alphaStall, cyBeta, gearCd, flapCl, flapCd;
        public float thrustMil, thrustAb;                          // N at sea level
        public float pitchRate, rollRate, yawRate;                 // rad/s at full stick
        public float pitchAccel, rollAccel, yawAccel;              // rad/s2 control power at full authority
        public float gLimit, ctrlQ, kBeta;                         // g limit of the pitch command, dynamic pressure of full authority (Pa), weathercock gain

        // main rotor / hover geometry (model space)
        public Vector3 hub, tailRotor; public float rotorRadius, tailRadius;
        public Vector3[] nozzles;                                  // exhaust positions (afterburner glow)

        // --- Starship / Megaliner / Antonov
        public bool rocket, airliner, hasRamp;
        public float startPitchDeg;                                // 90 = stands on its tail on the pad
        public float flameScale = 1f, flameWidth = 1f;             // exhaust plume size
        public float crashSpeed;                                   // rocket: relative speed (m/s) of a hull-vs-world hit that destroys it
        public BoxDef[] holdBoxes;                                 // walkable / drivable interior (PlaneLayer, seen by the wheel rays of other cars)
        public Vector3 rampHinge; public float rampClosedDeg, rampOpenSlopeDeg, rampLen, rampWidth;   // rear ramp: node "Ramp" turns about +X; 0 = open (as modelled), rampClosedDeg = shut
    }

    public static class Vehicles
    {
        public const float G = 9.80665f;
        public static readonly VehicleKind[] Order = { VehicleKind.Car, VehicleKind.Cessna, VehicleKind.F35B, VehicleKind.F15E, VehicleKind.Helicopter, VehicleKind.DeLorean, VehicleKind.Starship, VehicleKind.Megaliner, VehicleKind.Antonov };
        /// <summary>Vehicles the demo bots may fly (the rocket, the giants and the cargo plane need a pilot).</summary>
        public static readonly VehicleKind[] BotOrder = { VehicleKind.Car, VehicleKind.Cessna, VehicleKind.F35B, VehicleKind.F15E, VehicleKind.Helicopter, VehicleKind.DeLorean };
        static VehicleSpec _f35, _f15, _heli, _dmc, _star, _mega, _an;

        public static string NameOf(VehicleKind k)
        {
            switch (k)
            {
                case VehicleKind.Car: return "Coche";
                case VehicleKind.Cessna: return "Cessna";
                case VehicleKind.F35B: return "F-35B";
                case VehicleKind.F15E: return "F-15E";
                case VehicleKind.Helicopter: return "Helicóptero";
                case VehicleKind.DeLorean: return "DeLorean";
                case VehicleKind.Starship: return "Starship";
                case VehicleKind.Megaliner: return "Megaliner";
                case VehicleKind.Antonov: return "Antonov An-124";
                default: return "Vehículo";
            }
        }

        public static TouchProfile ProfileOf(VehicleKind k)
        {
            switch (k)
            {
                case VehicleKind.Cessna: return TouchProfile.Cessna;
                case VehicleKind.F35B: return TouchProfile.Vtol;
                case VehicleKind.F15E: return TouchProfile.Jet;
                case VehicleKind.Helicopter: return TouchProfile.Helicopter;
                case VehicleKind.DeLorean: return TouchProfile.DeLorean;
                case VehicleKind.Starship: return TouchProfile.Rocket;
                case VehicleKind.Megaliner: return TouchProfile.Airliner;
                case VehicleKind.Antonov: return TouchProfile.Cargo;
                default: return TouchProfile.Car;
            }
        }

        /// <summary>Spec of a <see cref="VehicleDriver"/> vehicle; null for the Car and the Cessna.</summary>
        public static VehicleSpec Get(VehicleKind k)
        {
            switch (k)
            {
                case VehicleKind.F35B: return _f35 ?? (_f35 = JsbVehicleCfg.Align(MakeF35()));
                case VehicleKind.F15E: return _f15 ?? (_f15 = JsbVehicleCfg.Align(MakeF15()));
                case VehicleKind.Helicopter: return _heli ?? (_heli = MakeHeli());
                case VehicleKind.DeLorean: return _dmc ?? (_dmc = JsbVehicleCfg.Align(MakeDeLorean()));
                case VehicleKind.Starship: return _star ?? (_star = JsbVehicleCfg.Align(MakeStarship()));
                case VehicleKind.Megaliner: return _mega ?? (_mega = JsbVehicleCfg.Align(MakeMegaliner()));
                case VehicleKind.Antonov: return _an ?? (_an = JsbVehicleCfg.Align(MakeAntonov()));
                default: return null;
            }
        }

        // Model space = the optimised GLB in Resources/Models/Vehicles (tools/models/prep.mjs): nose +Z, up +Y, metres, origin at the bounding box centre.
        static VehicleSpec MakeF15() => new VehicleSpec
        {
            kind = VehicleKind.F15E, name = "F-15E", model = "Models/Vehicles/F15E", mass = 19000f, inertia = new Vector3(225000f, 257000f, 39000f),
            boxes = new[] { new BoxDef(0, -0.8f, -0.4f, 2.6f, 2.0f, 15.8f), new BoxDef(0, -1.0f, -2.2f, 13.0f, 0.28f, 4.4f), new BoxDef(0, -1.0f, -7.0f, 8.6f, 0.25f, 2.6f),
                            new BoxDef(1.7f, 0.9f, -6.4f, 0.3f, 3.2f, 3.4f), new BoxDef(-1.7f, 0.9f, -6.4f, 0.3f, 3.2f, 3.4f), new BoxDef(0, -0.6f, 6.3f, 1.2f, 1.1f, 3.0f) },
            wheels = new[] { new WheelDef(1.55f, -2.55f, -0.9f, 0.42f, rear: true), new WheelDef(-1.55f, -2.55f, -0.9f, 0.42f, rear: true), new WheelDef(0, -2.55f, 5.0f, 0.30f, steer: true) },
            vMax = 355f, camBack = 27f, camUp = 6.5f, camLook = 18f, profile = TouchProfile.Jet, aero = AeroClass.Jet, hasGear = true, afterburner = true,
            wingArea = 56.5f, cd0 = 0.021f, kInd = 0.14f, clAlpha = 4.2f, alphaStall = 0.40f, cyBeta = 0.7f, gearCd = 0.035f,
            nozzles = new[] { new Vector3(0.95f, -0.5f, -9.6f), new Vector3(-0.95f, -0.5f, -9.6f) }, thrustMil = 130000f, thrustAb = 210000f, pitchRate = 1.3f, rollRate = 3.2f, yawRate = 0.5f, pitchAccel = 6f, rollAccel = 14f, yawAccel = 3f, gLimit = 9f, ctrlQ = 6000f, kBeta = 2.2f,
        };

        static VehicleSpec MakeF35() => new VehicleSpec
        {
            kind = VehicleKind.F35B, name = "F-35B", model = "Models/Vehicles/F35B", mass = 17500f, inertia = new Vector3(180000f, 210000f, 35000f),
            boxes = new[] { new BoxDef(0, -0.1f, 0.2f, 2.5f, 1.9f, 14.0f), new BoxDef(0, -0.3f, -1.8f, 10.6f, 0.25f, 4.6f), new BoxDef(0, -0.5f, -6.4f, 6.2f, 0.25f, 2.6f),
                            new BoxDef(1.1f, 0.9f, -5.8f, 0.25f, 1.8f, 2.4f), new BoxDef(-1.1f, 0.9f, -5.8f, 0.25f, 1.8f, 2.4f) },
            wheels = new[] { new WheelDef(1.7f, -1.55f, -1.5f, 0.28f, rear: true), new WheelDef(-1.7f, -1.55f, -1.5f, 0.28f, rear: true), new WheelDef(0, -1.55f, 4.2f, 0.25f, steer: true) },
            vMax = 365f, camBack = 23f, camUp = 5.5f, camLook = 15f, profile = TouchProfile.Vtol, aero = AeroClass.Jet, vtol = true, afterburner = true, hasGear = true,
            wingArea = 42.7f, cd0 = 0.022f, kInd = 0.16f, clAlpha = 3.6f, alphaStall = 0.45f, cyBeta = 0.7f, gearCd = 0f,
            nozzles = new[] { new Vector3(0f, -0.1f, -7.7f) }, thrustMil = 125000f, thrustAb = 190000f, pitchRate = 1.1f, rollRate = 3.0f, yawRate = 0.45f, pitchAccel = 5.5f, rollAccel = 12f, yawAccel = 2.6f, gLimit = 9f, ctrlQ = 6000f, kBeta = 2.0f,
        };

        static VehicleSpec MakeHeli() => new VehicleSpec
        {
            kind = VehicleKind.Helicopter, name = "Helicóptero", model = "Models/Vehicles/Heli", mass = 2800f, inertia = new Vector3(3200f, 2400f, 1600f), com = new Vector3(0, 0, 2.2f),
            boxes = new[] { new BoxDef(0, -0.2f, 1.0f, 2.1f, 2.6f, 5.4f), new BoxDef(0, 0.3f, -3.8f, 0.5f, 0.9f, 5.4f), new BoxDef(0, 0.5f, -6.0f, 0.3f, 2.2f, 1.2f) },
            wheels = new[] { new WheelDef(1.44f, -2.5f, 0.9f, 0.06f), new WheelDef(-1.44f, -2.5f, 0.9f, 0.06f), new WheelDef(1.44f, -2.5f, 4.6f, 0.06f), new WheelDef(-1.44f, -2.5f, 4.6f, 0.06f) },
            vMax = 85f, camBack = 22f, camUp = 6.5f, camLook = 8f, profile = TouchProfile.Helicopter, rotors = true,
            hub = new Vector3(0, 2.45f, 2.2f), rotorRadius = 6.2f, tailRotor = new Vector3(0.28f, 0.4f, -6.1f), tailRadius = 1.0f,
        };

        static VehicleSpec MakeDeLorean() => new VehicleSpec
        {
            kind = VehicleKind.DeLorean, name = "DeLorean", model = "Models/Vehicles/DeLorean", mass = 1230f, inertia = new Vector3(1700f, 1900f, 450f),
            boxes = new[] { new BoxDef(0, -0.1f, 0, 1.7f, 0.75f, 4.0f) },
            wheels = new[] { new WheelDef(0.69f, -0.382f, 1.2f, 0.30f, steer: true), new WheelDef(-0.69f, -0.382f, 1.2f, 0.30f, steer: true),
                             new WheelDef(0.696f, -0.348f, -1.162f, 0.33f, drive: true, rear: true), new WheelDef(-0.696f, -0.348f, -1.162f, 0.33f, drive: true, rear: true) },
            vMax = 55f, camBack = 8.5f, camUp = 3.0f, camLook = 5f, profile = TouchProfile.DeLorean, hoverCar = true,
        };

        // ---------------------------------------------------------------------------------------------------------------- Starship Mk1 (Resources/Flight/jsb/starship.xml, raptor.xml)
        // Model space: 50 m long, nose +Z, tail at z = -25. Contact points = three landing skids on an 8 m circle (JSBSim contacts LEG_0..2). com = JSBSim CG (29.5 m from the nose).
        static VehicleSpec MakeStarship() => new VehicleSpec
        {
            kind = VehicleKind.Starship, name = "Starship", model = "Models/Vehicles/Starship", mass = 664000f, inertia = new Vector3(8.3e7f, 8.3e7f, 4.0e6f), com = new Vector3(0, 0, -4.5f),
            boxes = new[] { new BoxDef(0, 0, 17.0f, 6.0f, 6.0f, 14.0f), new BoxDef(0, 0, -3.5f, 8.6f, 8.6f, 37.0f), new BoxDef(0, 0, -20.5f, 18.8f, 0.7f, 4.0f), new BoxDef(0, 0, 9.0f, 15.0f, 0.5f, 2.5f) },
            wheels = new[] { new WheelDef(0f, 8.0f, -24.7f, 0f), new WheelDef(-6.93f, -4.0f, -24.7f, 0f), new WheelDef(6.93f, -4.0f, -24.7f, 0f) },
            vMax = 1500f, camBack = 70f, camUp = 14f, camLook = 20f, profile = TouchProfile.Rocket, hasGear = true, rocket = true, startPitchDeg = 90f, crashSpeed = 4f,
            flameScale = 9f, flameWidth = 3.2f,
            nozzles = new[] { new Vector3(0, 1.1f, -25.5f), new Vector3(1.0f, -0.55f, -25.5f), new Vector3(-1.0f, -0.55f, -25.5f), new Vector3(0, 3.0f, -25.5f), new Vector3(2.6f, -1.5f, -25.5f), new Vector3(-2.6f, -1.5f, -25.5f) },
        };

        // ---------------------------------------------------------------------------------------------------------------- Megaliner (megaliner.xml, megafan.xml). 294 m long, 307 m span, 93 m tall = the Statue of Liberty
        // (ground to torch). Model origin = bounding-box centre, belly at y = -46.5. No landing-gear mesh: three skid contacts under the belly.
        static VehicleSpec MakeMegaliner() => new VehicleSpec
        {
            kind = VehicleKind.Megaliner, name = "Megaliner", model = "Models/Vehicles/Megaliner", mass = 8.0e6f, inertia = new Vector3(4.3e10f, 5.8e10f, 1.5e10f),
            boxes = new[] { new BoxDef(0, -21f, 0, 34f, 50f, 270f), new BoxDef(0, -6f, 46f, 305f, 2f, 52f), new BoxDef(0, 25f, -105f, 3f, 40f, 25f), new BoxDef(0, 41f, -105f, 90f, 2f, 20f) },
            wheels = new[] { new WheelDef(0, -45.5f, 110f, 1.0f, steer: true), new WheelDef(-9f, -45.5f, 38f, 1.0f, rear: true), new WheelDef(9f, -45.5f, 38f, 1.0f, rear: true) },
            vMax = 260f, camBack = 420f, camUp = 90f, camLook = 160f, profile = TouchProfile.Airliner, aero = AeroClass.Jet, airliner = true, flaps = true, hasGear = false,
            nozzles = new[] { new Vector3(-90f, -12f, -100f), new Vector3(-30f, -12f, -100f), new Vector3(30f, -12f, -100f), new Vector3(90f, -12f, -100f) },
            flameScale = 8f, flameWidth = 6f,
        };

        // ---------------------------------------------------------------------------------------------------------------- Antonov An-124 (an124.xml, d18t.xml). 69.1 m long, origin = bbox centre, ground at y = -7.607.
        // Hold (invisible PlaneLayer boxes): deck at y = -4.67, 5.4 m wide, from z = +24.5 (bulkhead) to the tail; the ramp hinge is at (0, -4.67, -12.09) and the model node "Ramp" turns about it.
        static VehicleSpec MakeAntonov() => new VehicleSpec
        {
            kind = VehicleKind.Antonov, name = "Antonov An-124", model = "Models/Vehicles/Antonov", mass = 295000f, inertia = new Vector3(8.8e7f, 1.2e8f, 3.1e7f),
            boxes = new[] { new BoxDef(0, -3.0f, 29.7f, 4.2f, 4.6f, 9.6f), new BoxDef(0, -0.1f, 2.0f, 73.0f, 0.8f, 14f), new BoxDef(-11f, -3.0f, 7f, 1.6f, 1.8f, 5f), new BoxDef(11f, -3.0f, 7f, 1.6f, 1.8f, 5f),
                            new BoxDef(-22f, -3.0f, 7f, 1.6f, 1.8f, 5f), new BoxDef(22f, -3.0f, 7f, 1.6f, 1.8f, 5f), new BoxDef(0, 3.8f, -30.5f, 0.7f, 7.5f, 6f), new BoxDef(0, 0.9f, -31.5f, 26f, 0.5f, 5f),
                            new BoxDef(0, -0.3f, -28.3f, 5.5f, 2.0f, 12.5f) },
            holdBoxes = new[] { new BoxDef(0, -4.95f, 6.2f, 6.2f, 0.6f, 36.6f),                                   // deck (front bulkhead to the ramp hinge)
                                new BoxDef(-3.0f, -2.5f, 1.2f, 0.6f, 4.8f, 46.6f), new BoxDef(3.0f, -2.5f, 1.2f, 0.6f, 4.8f, 46.6f),   // side walls
                                new BoxDef(0, 0.05f, 1.2f, 6.6f, 0.6f, 46.6f),                                    // ceiling
                                new BoxDef(0, -2.5f, 24.8f, 6.2f, 5.2f, 0.6f) },                                  // front bulkhead
            wheels = new[] { new WheelDef(0, -7.0f, 26.05f, 0.6f, steer: true), new WheelDef(-2.6f, -7.0f, 0.55f, 0.6f, drive: true, rear: true), new WheelDef(2.6f, -7.0f, 0.55f, 0.6f, drive: true, rear: true) },
            vMax = 240f, camBack = 85f, camUp = 18f, camLook = 45f, profile = TouchProfile.Cargo, aero = AeroClass.Jet, airliner = true, flaps = true, hasGear = true, hasRamp = true,
            rampHinge = new Vector3(0, -4.67f, -12.09f), rampClosedDeg = 34f, rampOpenSlopeDeg = 13.9f, rampLen = 10.3f, rampWidth = 5.2f,
            nozzles = new[] { new Vector3(-22f, -3.0f, 4f), new Vector3(-11f, -3.0f, 4f), new Vector3(11f, -3.0f, 4f), new Vector3(22f, -3.0f, 4f) }, flameScale = 1.2f, flameWidth = 0.9f,
        };
    }

    /// <summary>Hyper Boost: +500 % speed (6x) on every vehicle, smoothed in and out.</summary>
    public static class HyperBoost
    {
        public const float Extra = 5f;                  // "500 % more speed"
        public static float Mul(float t01) => 1f + Extra * Mathf.Clamp01(t01);
    }

    /// <summary>International Standard Atmosphere, troposphere + stratosphere.</summary>
    public static class Atmosphere
    {
        public static float Density(float h) => 1.225f * Mathf.Exp(-Mathf.Max(h, -400f) / 8500f);
        public static float Sound(float h) => h < 11000f ? 340.29f * Mathf.Sqrt(Mathf.Max(0.7f, 1f - 2.25577e-5f * Mathf.Max(h, 0f))) : 295f;
        public static float Smooth01(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
    }
}
