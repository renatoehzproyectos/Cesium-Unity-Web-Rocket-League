using NUnit.Framework;
using RocketWorld.Sim;

namespace RocketWorld.Tests
{
    /// <summary>Engine-free tests for the flying demolition bot brain.</summary>
    public class FlightBotBrainTests
    {
        static FlightProfile Jet() => new FlightProfile { style = FlightStyle.FixedWing, minSpeed = 78f, cruiseSpeed = 150f, maxSpeed = 266f, reach = 7f, cruiseAgl = 120f };
        static FlightProfile Heli() => new FlightProfile { style = FlightStyle.Rotor, cruiseSpeed = 30f, maxSpeed = 35f, reach = 5.5f, cruiseAgl = 25f };

        static FlightSense Level(float tx, float ty, float tz, float speed = 150f)
        {
            float R = 600f;
            return new FlightSense
            {
                tx = tx, ty = ty, tz = tz, vz = speed, uy = 1f, agl = 500f, maxRay = R,
                clearF = R, clearL = R, clearR = R, clearU = R, clearD = R, clearUp = 120f, dt = 1f / 60f,
            };
        }

        [Test]
        public void JetBanksRightForATargetOnTheRight()
        {
            var c = new FlightBotBrain(Jet()).Think(Level(400f, 0f, 400f));
            Assert.Greater(c.roll, 0.2f);
        }

        [Test]
        public void JetBanksLeftForATargetOnTheLeft()
        {
            var c = new FlightBotBrain(Jet()).Think(Level(-400f, 0f, 400f));
            Assert.Less(c.roll, -0.2f);
        }

        [Test]
        public void JetPullsUpForATargetAbove()
        {
            var c = new FlightBotBrain(Jet()).Think(Level(0f, 300f, 300f));
            Assert.Greater(c.pitch, 0.2f);
        }

        [Test]
        public void JetPullsUpWhenAWallIsClose()
        {
            var s = Level(0f, 0f, 800f); s.clearF = 40f;
            var c = new FlightBotBrain(Jet()).Think(s);
            Assert.AreEqual(1f, c.pitch, 1e-4f);
        }

        [Test]
        public void JetClimbsWhenNearTheGround()
        {
            var s = Level(0f, -300f, 800f); s.agl = 10f;
            var c = new FlightBotBrain(Jet()).Think(s);
            Assert.Greater(c.pitch, 0.3f);
        }

        [Test]
        public void StallProtectionAppliesFullThrottle()
        {
            var c = new FlightBotBrain(Jet()).Think(Level(0f, 0f, 800f, 60f));
            Assert.AreEqual(1f, c.lever, 1e-4f);
        }

        [Test]
        public void OutputsAreClamped()
        {
            var brain = new FlightBotBrain(Jet());
            for (int i = 0; i < 50; i++)
            {
                var c = brain.Think(Level(i * 20f - 500f, i * 10f - 200f, 300f));
                Assert.LessOrEqual(c.pitch, 1f); Assert.GreaterOrEqual(c.pitch, -1f);
                Assert.LessOrEqual(c.roll, 1f); Assert.GreaterOrEqual(c.roll, -1f);
                Assert.LessOrEqual(c.lever, 1f); Assert.GreaterOrEqual(c.lever, 0f);
            }
        }

        [Test]
        public void CrashedOnTheGroundRequestsRespawn()
        {
            var brain = new FlightBotBrain(Jet());
            var s = Level(0f, 0f, 100f, 0f); s.onGround = true;
            for (int i = 0; i < 400; i++) brain.Think(s);
            Assert.IsTrue(brain.WantsRespawn);
            brain.Reset();
            Assert.IsFalse(brain.WantsRespawn);
        }

        [Test]
        public void HelicopterYawsRightAndFlowsForwardTowardsATargetOnTheRight()
        {
            var s = Level(20f, 0f, 40f, 5f); s.agl = 25f; s.clearF = s.clearL = s.clearR = s.clearU = 90f; s.maxRay = 90f;
            var c = new FlightBotBrain(Heli()).Think(s);
            Assert.Greater(c.yaw, 0f);
            Assert.Greater(c.fwd, 0f);
        }

        [Test]
        public void HelicopterClimbsOverAnObstacle()
        {
            var s = Level(0f, 0f, 80f, 10f); s.agl = 25f; s.clearF = 10f; s.clearL = s.clearR = s.clearU = 90f; s.maxRay = 90f;
            var c = new FlightBotBrain(Heli()).Think(s);
            Assert.Greater(c.vert, 0.5f);
        }

        [Test]
        public void SeparationSteersCarsAwayFromNeighbours()
        {
            var s = new BotSense { tx = 0f, tz = 40f, vz = 15f, uy = 1f, onGround = true, clearL = 40f, clearC = 40f, clearR = 40f, dt = 1f / 60f, sx = -1f };
            var c = new DemoBotBrain().Think(s);
            Assert.Less(c.steer, 0f);
        }
    }
}
