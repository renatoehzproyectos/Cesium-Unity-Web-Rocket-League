using System;

namespace RocketWorld.Sim
{
    /// <summary>Values copied 1:1 from RocketSim's RLConst.h. "uu" = Unreal units, "bt" = Bullet units (metres): 1 bt = 50 uu.</summary>
    public static class RL
    {
        public const float BtToUu = 50f, UuToBt = 1f / 50f;
        public const float GravityZ = -650f;
        public const float CarMassBt = 180f;
        public const float CarMaxSpeed = 2300f, CarMaxAngSpeed = 5.5f;
        public const float BoostMax = 100f, BoostUsedPerSecond = BoostMax / 3, BoostMinTime = 0.1f;
        public const float BoostAccelGround = 2975 / 3f, BoostAccelAir = 3175 / 3f, BoostSpawnAmount = BoostMax / 3;
        public const float SupersonicStartSpeed = 2200f, SupersonicMaintainMinSpeed = SupersonicStartSpeed - 100f, SupersonicMaintainMaxTime = 1f;
        public const float PowerslideRiseRate = 5, PowerslideFallRate = 2;
        public const float ThrottleTorqueAmount = CarMassBt * 400f;
        public static readonly float BrakeTorqueAmount = CarMassBt * (14.25f + (1f / 3f));
        public const float StoppingForwardVel = 25f, CoastingBrakeFactor = 0.15f, BrakingNoThrottleSpeedThresh = 0.01f, ThrottleDeadzone = 0.001f;
        public const float ThrottleAirAccel = 200 / 3f;
        public const float JumpAccel = 4375f / 3f, JumpImmediateForce = 875f / 3f;
        public const float JumpMinTime = 0.025f, JumpResetTimePad = 1 / 40f, JumpMaxTime = 0.2f, DoubleJumpMaxDelay = 1.25f;
        public const float JumpPreMinAccelScale = 0.62f;
        public const float FlipZDamp120 = 0.35f, FlipZDampStart = 0.15f, FlipZDampEnd = 0.21f;
        public const float FlipTorqueTime = 0.65f, FlipPitchlockExtraTime = 0.3f;
        public const float FlipInitialVelScale = 500f, FlipTorqueX = 260f, FlipTorqueY = 224f;
        public const float FlipForwardImpulseMaxSpeedScale = 1f, FlipSideImpulseMaxSpeedScale = 1.9f, FlipBackwardImpulseMaxSpeedScale = 2.5f;
        public static readonly float FlipBackwardImpulseScaleX = 16f / 15f;
        public static readonly float CarTorqueScale = (float)(2 * Math.PI / (1 << 16) * 1000);
        public const float CarAutoflipImpulse = 200, CarAutoflipTorque = 50, CarAutoflipTime = 0.4f, CarAutoflipRollThresh = 2.8f;
        public static readonly float CarAutoflipNormzThresh = (float)Math.Sqrt(0.5);
        public const float CarAutorollForce = 100, CarAutorollTorque = 80;
        public static readonly Vec3 CarAirControlTorque = new Vec3(130, 95, 400), CarAirControlDamping = new Vec3(30, 20, 50);
        public const float CarWorldFriction = 0.3f, CarWorldRestitution = 0.3f;     // CARWORLD_COLLISION_*

        // btVehicle tuning
        public const float SuspensionForceScaleFront = 36f - (1f / 4f);
        public static readonly float SuspensionForceScaleBack = 54f + (1f / 4f) + (1.5f / 100f);
        public const float SuspensionStiffness = 500f, WheelsDampingCompression = 25f, WheelsDampingRelaxation = 40f;
        public const float MaxSuspensionTravel = 12f, SuspensionSubtraction = 0.05f;
        // Bullet solver values RocketSim relies on
        public const float SolverErp = 0.2f, InitialSolverTimeStep = 1f / 60f;

        public static readonly LinearCurve SteerAngleFromSpeed = new LinearCurve(0, 0.53356f, 500, 0.31930f, 1000, 0.18203f, 1500, 0.10570f, 1750, 0.08507f, 3000, 0.03454f);
        public static readonly LinearCurve PowerslideSteerAngleFromSpeed = new LinearCurve(0, 0.39235f, 2500, 0.12610f);
        public static readonly LinearCurve DriveSpeedTorqueFactor = new LinearCurve(0, 1.0f, 1400, 0.1f, 1410, 0.0f);
        public static readonly LinearCurve NonStickyFrictionFactor = new LinearCurve(0, 0.1f, 0.7075f, 0.5f, 1, 1.0f);
        public static readonly LinearCurve LatFriction = new LinearCurve(0, 1.0f, 1, 0.2f);
        public static readonly LinearCurve LongFriction = new LinearCurve();                      // empty => default output 1
        public static readonly LinearCurve HandbrakeLatFrictionFactor = new LinearCurve(0, 0.1f);
        public static readonly LinearCurve HandbrakeLongFrictionFactor = new LinearCurve(0, 0.5f, 1, 0.9f);
    }
}
