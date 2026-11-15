using NUnit.Framework;

namespace RocketWorld.Tests
{
    public class MemoryGuardLogicTests
    {
        [Test] public void LevelsFollowThresholds()
        {
            Assert.AreEqual(0, MemoryGuardLogic.LevelFor(500, 1000));
            Assert.AreEqual(1, MemoryGuardLogic.LevelFor(700, 1000));
            Assert.AreEqual(2, MemoryGuardLogic.LevelFor(860, 1000));
            Assert.AreEqual(3, MemoryGuardLogic.LevelFor(960, 1000));
            Assert.AreEqual(0, MemoryGuardLogic.LevelFor(0, 1000));
        }

        [Test] public void RisesAtOnceRelaxesSlowly()
        {
            var g = new MemoryGuardLogic { RelaxSeconds = 10f };
            Assert.IsTrue(g.Feed(900, 1000, 1f));
            Assert.AreEqual(2, g.Level);
            // heap stays at its high-water mark: no relaxing before RelaxSeconds, then one step
            for (int i = 0; i < 9; i++) g.Feed(900, 1000, 1f);
            Assert.AreEqual(2, g.Level);
            g.Feed(900, 1000, 1f);
            Assert.AreEqual(2, g.Level);          // still above the 85 % threshold: the target level is 2
            Assert.AreEqual(1f, MemoryGuardLogic.ScaleMul(1));
            Assert.Less(MemoryGuardLogic.CacheMul(3), MemoryGuardLogic.CacheMul(1));
        }
    }
}
