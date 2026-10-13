using RocketWorld.Sim;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// PhysX configuration that mirrors RocketSim/Bullet, plus the RocketSim(sim space) &lt;-&gt; Unity coordinate mapping.
    /// Sim space: X forward, Y right, Z up (Bullet units = metres). Unity: Z forward, X right, Y up.
    /// The mapping u = (sim.y, sim.z, sim.x) is a cyclic permutation (determinant +1), so every cross product / torque / angular velocity
    /// keeps its meaning and no pseudovector sign fixes are needed.
    /// </summary>
    public static class PhysicsSetup
    {
        public const int CarLayer = 30;                        // wheel rays and the chase camera ignore it
        public static int WorldMask => ~(1 << CarLayer);

        public static Vector3 ToUnity(Vec3 s) => new Vector3(s.y, s.z, s.x);
        public static Vec3 ToSim(Vector3 u) => new Vec3(u.z, u.x, u.y);

        static PhysicsMaterial _world, _car;
        /// <summary>RocketSim: car-vs-world friction 0.3, restitution 0.3 (mutator defaults). Both materials use Average so the pair is exactly 0.3 / 0.3.</summary>
        public static PhysicsMaterial WorldMaterial => _world != null ? _world : _world = Make("rw-world");
        public static PhysicsMaterial CarMaterial => _car != null ? _car : _car = Make("rw-car");
        static PhysicsMaterial Make(string name) => new PhysicsMaterial(name)
        {
            dynamicFriction = RL.CarWorldFriction, staticFriction = RL.CarWorldFriction, bounciness = RL.CarWorldRestitution,
            frictionCombine = PhysicsMaterialCombine.Average, bounceCombine = PhysicsMaterialCombine.Average,
        };

        /// <summary>We step PhysX ourselves at 120 Hz (Physics.Simulate) so the order is RocketSim's: pre-tick -> step -> post-tick.</summary>
        public static void Apply()
        {
            Physics.simulationMode = SimulationMode.Script;
            Physics.gravity = new Vector3(0f, RL.GravityZ * RL.UuToBt, 0f);   // -13 m/s^2
            Physics.defaultSolverIterations = 10;                              // btContactSolverInfo::m_numIterations
            Physics.defaultSolverVelocityIterations = 1;
            Physics.bounceThreshold = 0.2f;                                    // Bullet's restitution velocity threshold
            Physics.autoSyncTransforms = true;                                 // tiles move with origin shifts
            Physics.queriesHitBackfaces = false;
            Physics.queriesHitTriggers = false;
            Physics.reuseCollisionCallbacks = true;
        }
    }
}
