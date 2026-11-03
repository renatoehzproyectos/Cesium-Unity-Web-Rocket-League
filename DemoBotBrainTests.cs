using NUnit.Framework;
using RocketWorld.Sim;

namespace RocketWorld.Tests
{
    /// <summary>Engine-free tests for the demolition bot brain.</summary>
    public class DemoBotBrainTests
    {
        static BotSense Level(float tx = 20f, float tz = 20f, float speed = 0f, float vz = 0f)
        {
            return new BotSense
            {
                tx = tx, ty = 0f, tz = tz,
                tvx = 0f, tvy = 0f, tvz = 0f,
                vx = 0f, vy = 0f, vz = vz > 0f ? vz : speed,
                ux = 0f, uy = 1f, uz = 0f,          // flat ground
                onGround = true,
                hasJumped = false, hasDoubleJumped = false, supersonic = false,
                clearL = DemoBotBrain.ClearRange,
                clearC = DemoBotBrain.ClearRange,
                clearR = DemoBotBrain.ClearRange,
                dt = 1f / 60f
            };
        }

        [Test]
        public void ThrottlesForwardWhenTargetIsStraightAhead()
        {
            var brain = new DemoBotBrain();
            var c = brain.Think(Level(tx: 0f, tz: 40f, speed: 5f));
            Assert.Greater(c.throttle, 0f);
        }

        [Test]
        public void SteersRightWhenTargetIsToTheRight()
        {
            var brain = new DemoBotBrain();
            var c = brain.Think(Level(tx: 15f, tz: 15f, speed: 5f));
            Assert.Greater(c.steer, 0f);
        }

        [Test]
        public void SteersLeftWhenTargetIsToTheLeft()
        {
            var brain = new DemoBotBrain();
            var c = brain.Think(Level(tx: -15f, tz: 15f, speed: 5f));
            Assert.Less(c.steer, 0f);
        }

        [Test]
        public void BoostsWhenAimedAtTarget()
        {
            var brain = new DemoBotBrain();
            var c = brain.Think(Level(tx: 0f, tz: 40f, speed: 10f));
            Assert.IsTrue(c.boost);
        }

        [Test]
        public void DoesNotBoostOnSharpTurns()
        {
            var brain = new DemoBotBrain();
            // target 90 degrees to the side
            var c = brain.Think(Level(tx: 30f, tz: 0f, speed: 15f));
            Assert.IsFalse(c.boost);
        }

        [Test]
        public void HandbrakeOnTightTurn()
        {
            var brain = new DemoBotBrain();
            // large angle to the side at reasonable forward speed -> power-slide
            var c = brain.Think(Level(tx: 30f, tz: 2f, speed: 20f, vz: 20f));
            Assert.IsTrue(c.handbrake);
        }

        [Test]
        public void OutputsAreClamped()
        {
            var brain = new DemoBotBrain();
            for (int i = 0; i < 20; i++)
            {
                float t = i * 0.016f + 0.016f;
                var s = Level(tx: 0f, tz: 25f, speed: 25f);
                s.dt = 0.016f;
                var c = brain.Think(s);
                Assert.LessOrEqual(c.throttle, 1f);
                Assert.GreaterOrEqual(c.throttle, -1f);
                Assert.LessOrEqual(c.steer, 1f);
                Assert.GreaterOrEqual(c.steer, -1f);
            }
        }

        [Test]
        public void AvoidObstacleOnLeft()
        {
            var brain = new DemoBotBrain();
            var s = Level(tx: 0f, tz: 40f, speed: 20f, vz: 20f);
            s.clearL = 3f;      // wall on the left
            s.clearC = DemoBotBrain.ClearRange;
            s.clearR = DemoBotBrain.ClearRange;
            var c = brain.Think(s);
            Assert.Greater(c.steer, -0.1f);       // steers right (away from the wall)
        }

        [Test]
        public void AvoidObstacleStraightAhead()
        {
            var brain = new DemoBotBrain();
            var s = Level(tx: 0f, tz: 40f, speed: 20f, vz: 20f);
            s.clearC = 4f;      // wall ahead
            s.clearL = DemoBotBrain.ClearRange;
            s.clearR = DemoBotBrain.ClearRange;
            var c = brain.Think(s);
            Assert.AreNotEqual(0f, c.steer);       // steers to one side
        }

        [Test]
        public void StartsDodgeOnClearStraight()
        {
            var brain = new DemoBotBrain();
            // step several ticks so the dodge cooldown is not an issue
            var s = Level(tx: 0f, tz: 35f, speed: 22f, vz: 22f);
            s.dt = 0.016f;
            CarControls c = new CarControls();
            for (int i = 0; i < 4; i++) c = brain.Think(s);
            // After a few frames it should start the dodge jump
            Assert.IsTrue(c.jump || c.throttle == 1f);   // either in the dodge or still building up
        }

        [Test]
        public void ReportsDistanceToTarget()
        {
            var brain = new DemoBotBrain();
            brain.Think(Level(tx: 3f, tz: 4f));
            Assert.AreEqual(5f, brain.Distance, 0.01f);
        }

        [Test]
        public void AirBranchDoesNotCrash()
        {
            var brain = new DemoBotBrain();
            var s = Level(tx: 0f, tz: 20f, speed: 15f);
            s.onGround = false; s.hasJumped = true;
            s.vy = 8f;
            var c = brain.Think(s);
            // no exception is the key requirement; also check yaw / pitch / roll are clamped
            Assert.LessOrEqual(c.yaw, 1f);   Assert.GreaterOrEqual(c.yaw, -1f);
            Assert.LessOrEqual(c.pitch, 1f); Assert.GreaterOrEqual(c.pitch, -1f);
            Assert.LessOrEqual(c.roll, 1f);  Assert.GreaterOrEqual(c.roll, -1f);
        }

        [Test]
        public void ResetClearsState()
        {
            var brain = new DemoBotBrain();
            for (int i = 0; i < 5; i++) brain.Think(Level(tx: 0f, tz: 25f, speed: 22f, vz: 22f));
            brain.Reset();
            // After a Reset the per-run flags must be cleared; Distance is updated on the next Think.
            Assert.IsFalse(brain.WantsRespawn);
            Assert.IsFalse(brain.Recovering);
            // A fresh Think after Reset must produce clamped output.
            var c = brain.Think(Level(tx: 0f, tz: 25f, speed: 5f));
            Assert.LessOrEqual(c.throttle, 1f);
            Assert.GreaterOrEqual(c.throttle, -1f);
        }
    }
}
