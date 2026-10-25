using NUnit.Framework;

namespace RocketWorld.Tests
{
    public class AntiCrashLogicTests
    {
        static void Run(AntiCrashLogic l, float frameMs, float seconds) { for (float t = 0; t < seconds; t += frameMs / 1000f) l.Feed(frameMs); }

        [Test] public void StartsAtTheMinimum()
        {
            var l = new AntiCrashLogic();
            Assert.AreEqual(0, l.Level);
            AntiCrashLogic.Resolve(0, 2f, 2f, 0.75f, out var sc, out var dof, out var mb);
            Assert.AreEqual(AntiCrashLogic.MinScale, sc, 1e-4f); Assert.AreEqual(0f, dof); Assert.AreEqual(0f, mb);
        }

        [Test] public void RisesToTheUsersSettingsOnAFastDevice()
        {
            var l = new AntiCrashLogic(); Run(l, 16.7f, 120f);
            Assert.AreEqual(AntiCrashLogic.MaxLevel, l.Level);
            AntiCrashLogic.Resolve(l.Level, 2f, 2f, 0.75f, out var sc, out var dof, out var mb);
            Assert.AreEqual(2f, sc, 1e-4f); Assert.AreEqual(2f, dof, 1e-4f); Assert.AreEqual(0.75f, mb, 1e-4f);
        }

        [Test] public void NeverExceedsTheUsersSliders()
        {
            for (int lv = 0; lv <= AntiCrashLogic.MaxLevel; lv++)
            {
                AntiCrashLogic.Resolve(lv, 0.5f, 1f, 0f, out var sc, out var dof, out var mb);
                Assert.LessOrEqual(sc, 0.5f + 1e-4f); Assert.LessOrEqual(dof, 1f + 1e-4f); Assert.AreEqual(0f, mb);
            }
        }

        [Test] public void DropsWhenBelow30Fps()
        {
            var l = new AntiCrashLogic(); Run(l, 16.7f, 60f); int high = l.Level;
            Run(l, 40f, 10f);
            Assert.Less(l.Level, high);
        }

        [Test] public void SettlesAtAMiddleLevelWithoutPingPong()
        {
            var l = new AntiCrashLogic(); int changes = 0;
            // frame time depends on level: 16 ms up to level 8, 40 ms above (too slow)
            for (float t = 0; t < 240f;)
            {
                float ms = l.Level <= 8 ? 16f : 40f; t += ms / 1000f;
                if (l.Feed(ms)) changes++;
            }
            Assert.LessOrEqual(l.Level, 9);
            Assert.GreaterOrEqual(l.Level, 7);
            Assert.Less(changes, 50);
        }

        [Test] public void SlowFramesNeverGoBelowZero()
        {
            var l = new AntiCrashLogic(); Run(l, 60f, 30f);
            Assert.AreEqual(0, l.Level);
        }
    }
}
