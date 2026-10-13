using System.Globalization;
using System.IO;
using NUnit.Framework;
using RocketWorld.Sim;
using RocketWorld.Sim.Tests;
using UnityEngine;

namespace RocketWorld.Tests
{
    public class SimCoreTests
    {
        const float Eps = 1e-4f;

        [Test] public void SafeNormalizedOfZeroIsUnitX()
        {
            Assert.AreEqual(1f, Vec3.Zero.SafeNormalized().x);
            Assert.AreEqual(0f, Vec3.Zero.NormalizedOrZero().x);     // RocketSim's Vec::Normalized() returns zero instead
        }

        [Test] public void CurvesMatchRocketSim()
        {
            Assert.AreEqual(0.53356f, RL.SteerAngleFromSpeed.GetOutput(0), Eps);
            Assert.AreEqual((0.53356f + 0.31930f) / 2, RL.SteerAngleFromSpeed.GetOutput(250), Eps);
            Assert.AreEqual(0.03454f, RL.SteerAngleFromSpeed.GetOutput(99999), Eps);
            Assert.AreEqual(1f, RL.LongFriction.GetOutput(0.3f), Eps);            // empty curve -> default output
            Assert.AreEqual(0.6f, RL.LatFriction.GetOutput(0.5f), Eps);
        }

        [Test] public void OctaneInertiaMatchesNativeBullet()
        {
            var cfg = CarConfig.Octane();
            var inertia = cfg.LocalInertia(RL.CarMassBt);
            // m_invInertiaLocal printed by RocketSim itself (tools/parity): 0.01856446 0.01043374 0.00758155
            Assert.AreEqual(0.01856446f, 1f / inertia.x, 2e-7f);
            Assert.AreEqual(0.01043374f, 1f / inertia.y, 2e-7f);
            Assert.AreEqual(0.00758155f, 1f / inertia.z, 2e-7f);
            Assert.AreEqual(120.373f, cfg.EffectiveHitboxSize().x * RL.BtToUu, 0.01f);
        }

        [Test] public void UnityMappingIsAProperRotation()
        {
            var a = new Vec3(0.3f, -1.2f, 2.5f); var b = new Vec3(-0.7f, 0.4f, 1.1f);
            var lhs = PhysicsSetup.ToUnity(a.Cross(b));
            var rhs = Vector3.Cross(PhysicsSetup.ToUnity(a), PhysicsSetup.ToUnity(b));
            Assert.AreEqual(lhs.x, rhs.x, Eps); Assert.AreEqual(lhs.y, rhs.y, Eps); Assert.AreEqual(lhs.z, rhs.z, Eps);
            var rt = PhysicsSetup.ToSim(PhysicsSetup.ToUnity(a));
            Assert.AreEqual(a.x, rt.x, Eps); Assert.AreEqual(a.y, rt.y, Eps); Assert.AreEqual(a.z, rt.z, Eps);
            Assert.AreEqual(1f, PhysicsSetup.ToUnity(new Vec3(1, 0, 0)).z);      // sim forward -> Unity +Z
        }

        // ---- the Unity project is pinned to native RocketSim: final states of the shared scenarios (tools/parity/scen) ----
        static (Vec3 pos, Vec3 vel, float boost) Run(string scenario, IRayCaster world = null)
        {
            var cfg = CarConfig.Octane();
            var inertia = cfg.LocalInertia(RL.CarMassBt);
            var body = new BulletLikeBody { invInertiaLocal = new Vec3(1 / inertia.x, 1 / inertia.y, 1 / inertia.z), pos = new Vec3(0, 0, 17 * RL.UuToBt) };
            var car = new RocketVehicle(body, world ?? new FlatFloor(), cfg);
            car.OnStateSet(true);
            var path = Path.Combine(Application.dataPath, "..", "tools", "parity", "scen", scenario + ".csv");
            var inv = CultureInfo.InvariantCulture;
            const float dt = 1f / 120f;
            foreach (var line in File.ReadLines(path))
            {
                var p = line.Split(',');
                car.controls = new CarControls
                {
                    throttle = float.Parse(p[0], inv), steer = float.Parse(p[1], inv), pitch = float.Parse(p[2], inv), yaw = float.Parse(p[3], inv), roll = float.Parse(p[4], inv),
                    jump = p[5] == "1", boost = p[6] == "1", handbrake = p[7] == "1",
                };
                car.PreTick(dt); body.Step(dt); car.PostTick(dt); car.FinishTick();
            }
            return (body.pos * RL.BtToUu, body.vel * RL.BtToUu, car.state.boost);
        }

        static void AssertPos((Vec3 pos, Vec3 vel, float boost) r, float x, float y, float z, float tol = 1f)
        {
            Assert.AreEqual(x, r.pos.x, tol, "x"); Assert.AreEqual(y, r.pos.y, tol, "y"); Assert.AreEqual(z, r.pos.z, tol, "z");
        }

        [Test] public void GoldenDrive() { var r = Run("s1_drive"); AssertPos(r, 3201.90f, 0f, 17.03f); Assert.AreEqual(1410.06f, r.vel.x, 1f); }
        [Test] public void GoldenTurn() { AssertPos(Run("s2_turn"), -395.87f, 2145.54f, 17.03f); }
        [Test] public void GoldenBoost() { var r = Run("s3_boost"); AssertPos(r, 4186.21f, 0f, 17.03f); Assert.AreEqual(0f, r.boost, 0.01f); }
        [Test] public void GoldenPowerslide() { AssertPos(Run("s4_powerslide"), 2468.89f, 756.31f, 17.03f); }
        [Test] public void GoldenReverse() { AssertPos(Run("s5_reverse"), 43.10f, 0f, 17.03f); }
        [Test] public void GoldenJumpAndAirControl() { AssertPos(Run("s6_jump_air"), 2.95f, 0f, 169.90f); }
        [Test] public void GoldenFrontFlip() { AssertPos(Run("s7_frontflip"), 2780.66f, 0f, 88.98f); }
        [Test] public void GoldenSideFlip() { AssertPos(Run("s8_sideflip_air"), 2764.42f, 614.67f, 88.86f); }
    }
}
