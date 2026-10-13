using NUnit.Framework;

namespace RocketWorld.Tests
{
    public class AutoQualityLogicTests
    {
        static AutoQualityLogic.Action Run(AutoQualityLogic l, float frameMs, float seconds, out int degrades, out int improves)
        {
            degrades = improves = 0; var last = AutoQualityLogic.Action.None;
            for (float t = 0; t < seconds; t += frameMs / 1000f)
            {
                var a = l.Feed(frameMs);
                if (a == AutoQualityLogic.Action.Degrade) degrades++;
                if (a == AutoQualityLogic.Action.Improve) improves++;
                if (a != AutoQualityLogic.Action.None) last = a;
            }
            return last;
        }

        [Test] public void SteadySixtyFpsNeverDegrades()
        {
            var l = new AutoQualityLogic();
            Run(l, 16.6f, 15f, out int d, out _);
            Assert.AreEqual(0, d);
        }

        [Test] public void SlowFramesDegradeAfterTwoWindows()
        {
            var l = new AutoQualityLogic();
            Run(l, 28f, 2.5f, out int d, out _);
            Assert.GreaterOrEqual(d, 1);
        }

        [Test] public void DegradeRespectsCooldown()
        {
            var l = new AutoQualityLogic();
            Run(l, 28f, 3.2f, out int d, out _);
            Assert.AreEqual(1, d, "a second degrade within the 3 s cooldown would over-correct");
        }

        [Test] public void LongStableStretchImproves()
        {
            var l = new AutoQualityLogic();
            Run(l, 16.6f, 25f, out _, out int imp);
            Assert.AreEqual(1, imp);
        }

        [Test] public void HitchesAreIgnored()
        {
            var l = new AutoQualityLogic();
            for (int i = 0; i < 20; i++) { l.Feed(900f); }
            Run(l, 16.6f, 3f, out int d, out _);
            Assert.AreEqual(0, d);
        }

        [Test] public void DegradeSoonAfterImproveDoublesTheWait()
        {
            var l = new AutoQualityLogic();
            Run(l, 16.6f, 22f, out _, out int imp1);
            Assert.AreEqual(1, imp1);
            Run(l, 30f, 6f, out int d, out _);                       // it hurt: degrade again right away
            Assert.GreaterOrEqual(d, 1);
            Run(l, 16.6f, 25f, out _, out int imp2);                 // old wait (20 s) would improve; doubled wait (40 s) must not
            Assert.AreEqual(0, imp2);
        }
    }
}
