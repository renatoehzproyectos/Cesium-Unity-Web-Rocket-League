using System;

namespace RocketWorld.Sim
{
    /// <summary>
    /// RocketSim's ball model. Constants are RLConst.h values; the car-ball "extra impulse" below was read out of the compiled
    /// RocketSim 2.2.1 WASM (Arena car-ball callback) and its curve table was dumped from the module's memory, so it matches
    /// the original game exactly. Units: "uu" (1 uu = 0.02 m). Sim space: X forward, Y right, Z up.
    /// The numbers that PhysX has to reproduce through materials/solver settings were measured on that same WASM:
    /// gravity -650 uu/s^2, restitution 0.6, sliding friction 0.35 (spin-up 2.5*mu*g/r: solid sphere), linear damping (1-0.03)^t,
    /// no angular damping, max speed 6000 uu/s, max spin 6 rad/s, rest height 93.15 uu.
    /// </summary>
    public static class RLBall
    {
        public const float RadiusUu = 91.25f;                // visual / nominal radius
        public const float CollisionRadiusUu = 93.15f;       // rest height on the ground (radius + Bullet's contact margin)
        public const float MassBt = 30f;
        public const float MaxSpeedUu = 6000f, MaxAngSpeed = 6f;
        public const float Drag = 0.03f, Friction = 0.35f, Restitution = 0.6f;
        public const float CarBallFriction = 2f, CarBallRestitution = 0f;   // CARBALL_COLLISION_*

        public const float ExtraImpulseZScale = 0.35f;       // z of (ball - car) is scaled by this before normalising
        public const float ExtraImpulseForwardScale = 0.65f; // the part of the hit direction along the car's nose is scaled by this
        public const float ExtraImpulseMaxDeltaVelUu = 4600f;
        public const int ExtraImpulseMinTickGap = 2;         // applied at most every 2nd tick (Bullet callback guard: last + 1 >= tick -> skip)
        public static readonly LinearCurve ExtraImpulseFactor = new LinearCurve(0, 0.65f, 500, 0.65f, 2300, 0.55f, 4600, 0.30f);

        /// <summary>
        /// Extra velocity (uu/s) added to the ball when a car touches it, on top of the normal contact response:
        /// dir = normalize(h - (1 - 0.65) * f * (f . h)),  h = normalize((ball - car) * (1, 1, 0.35)),  f = car forward;
        /// dv = dir * s * curve(s),  s = min(|ballVel - carVel|, 4600).
        /// </summary>
        public static Vec3 ExtraHitVelocity(Vec3 relPosUu, Vec3 relVelUu, Vec3 carForward)
        {
            float s = Math.Min(relVelUu.Length(), ExtraImpulseMaxDeltaVelUu);
            if (!(s > 0f)) return Vec3.Zero;
            Vec3 h = (relPosUu * new Vec3(1f, 1f, ExtraImpulseZScale)).NormalizedOrZero();
            Vec3 fwdPart = carForward * h.Dot(carForward);
            Vec3 dir = (h - fwdPart * (1f - ExtraImpulseForwardScale)).NormalizedOrZero();
            return dir * (s * ExtraImpulseFactor.GetOutput(s));
        }
    }
}
