namespace RocketWorld.Sim
{
    public struct WheelPairConfig
    {
        public float wheelRadius, suspensionRestLength;
        public Vec3 connectionPointOffset;
    }

    public sealed class CarConfig
    {
        public Vec3 hitboxSize, hitboxPosOffset;       // uu
        public WheelPairConfig frontWheels, backWheels;
        public float dodgeDeadzone = 0.5f;

        /// <summary>RocketSim CAR_CONFIG_OCTANE (the Fennec uses the Octane hitbox).</summary>
        public static CarConfig Octane() => new CarConfig
        {
            hitboxSize = new Vec3(120.507f, 86.6994f, 38.6591f),
            hitboxPosOffset = new Vec3(13.8757f, 0, 20.755f),
            frontWheels = new WheelPairConfig { wheelRadius = 12.50f, suspensionRestLength = 38.755f, connectionPointOffset = new Vec3(51.25f, 25.90f, 20.755f) },
            backWheels = new WheelPairConfig { wheelRadius = 15.00f, suspensionRestLength = 37.055f, connectionPointOffset = new Vec3(-33.75f, 29.50f, 20.755f) },
        };

        /// <summary>
        /// The box Bullet actually simulates, in metres (full extents). btBoxShape builds its implicit dimensions with the default 0.04 margin,
        /// then setSafeMargin lowers the margin to min(0.04, 0.1 * smallest half-extent): the shape ends up 2*(0.04 - margin) smaller than nominal
        /// (0.134 uu for the Octane). Measured against RocketSim's own m_invInertiaLocal; used for both the inertia and the PhysX collider.
        /// </summary>
        public Vec3 EffectiveHitboxSize()
        {
            float hx = hitboxSize.x * RL.UuToBt / 2, hy = hitboxSize.y * RL.UuToBt / 2, hz = hitboxSize.z * RL.UuToBt / 2;
            float minHalf = System.Math.Min(hx, System.Math.Min(hy, hz));
            float margin = System.Math.Min(0.04f, 0.1f * minHalf);
            float shrink = 2 * (0.04f - margin);
            return new Vec3(hx * 2 - shrink, hy * 2 - shrink, hz * 2 - shrink);
        }

        /// <summary>Principal inertia (kg m^2) in sim axes X(roll) Y(pitch) Z(yaw), as btBoxShape::calculateLocalInertia.</summary>
        public Vec3 LocalInertia(float mass)
        {
            var e = EffectiveHitboxSize();
            float lx = e.x, ly = e.y, lz = e.z, k = mass / 12f;
            return new Vec3(k * (ly * ly + lz * lz), k * (lx * lx + lz * lz), k * (lx * lx + ly * ly));
        }
    }

    public struct CarControls
    {
        public float throttle, steer, pitch, yaw, roll;
        public bool jump, boost, handbrake;
        static float C(float v) => v > 1 ? 1 : (v < -1 ? -1 : v);
        public void ClampFix() { throttle = C(throttle); steer = C(steer); pitch = C(pitch); yaw = C(yaw); roll = C(roll); }
    }

    public sealed class CarState
    {
        public bool isOnGround = true, hasJumped, hasDoubleJumped, hasFlipped, isFlipping, isJumping, isBoosting, isAutoFlipping, isSupersonic;
        public float jumpTime, flipTime, airTime, airTimeSinceJump, boost = RL.BoostSpawnAmount, boostingTime, timeSinceBoosted, handbrakeVal, supersonicTime;
        public float autoFlipTimer, autoFlipTorqueScale;
        public Vec3 flipRelTorque;
        public bool lastJump;
        public readonly bool[] wheelsWithContact = new bool[4];
        public bool worldHasContact;
        public Vec3 worldContactNormal;
        public Mat3 rotMat = Mat3.Identity;       // body basis at the end of the previous tick (Car::GetForwardDir etc. read this)
    }

    /// <summary>The rigid body the model drives. Positions in bt units (metres), axes: X forward, Y right, Z up (RocketSim space).</summary>
    public interface IBody
    {
        Vec3 Position { get; }                  // centre of mass (== body origin)
        Mat3 Basis { get; }
        Vec3 LinearVelocity { get; set; }
        Vec3 AngularVelocity { get; set; }
        float Mass { get; }
        Vec3 InvInertiaLocal { get; }
        void ApplyCentralForce(Vec3 force);     // continuous: integrated over the next step
        void ApplyTorque(Vec3 torque);          // continuous, world space
        void ApplyCentralImpulse(Vec3 impulse); // immediate velocity change
        void ApplyImpulse(Vec3 impulse, Vec3 relPos);
    }

    public struct RayHit { public Vec3 point, normal; public float fraction; }

    public interface IRayCaster
    {
        /// <summary>Closest hit of the segment from-&gt;to against the static world (the chassis must be excluded).</summary>
        bool Cast(Vec3 from, Vec3 to, out RayHit hit);
    }
}
