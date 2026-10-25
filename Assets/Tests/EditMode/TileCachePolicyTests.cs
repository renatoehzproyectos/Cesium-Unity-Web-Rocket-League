using NUnit.Framework;
using RocketWorld.Sim;

namespace RocketWorld.Tests
{
    public class TileCachePolicyTests
    {
        const long MB = 1024 * 1024;
        [Test] public void MaxBudgetScalesAndIsBoundedByTheHeap()
        {
            Assert.AreEqual(1152 * MB, TileCachePolicy.MaxBudget(384 * MB, false, 3200 * MB));
            Assert.AreEqual(500 * MB, TileCachePolicy.MaxBudget(256 * MB, false, 1000 * MB));
            Assert.AreEqual(384 * MB, TileCachePolicy.MaxBudget(192 * MB, true, 4000 * MB));
        }
        [Test] public void BudgetShrinksUnderPressureGrowsWithRoomAndStaysBounded()
        {
            Assert.AreEqual((long)(400 * MB * 0.85), TileCachePolicy.Next(400 * MB, 2000 * MB, 2000 * MB, 200 * MB, 1000 * MB));
            Assert.AreEqual((long)(400 * MB * 1.08), TileCachePolicy.Next(400 * MB, 500 * MB, 2000 * MB, 200 * MB, 1000 * MB));
            Assert.AreEqual(400 * MB, TileCachePolicy.Next(400 * MB, 1500 * MB, 2000 * MB, 200 * MB, 1000 * MB));
            Assert.AreEqual(200 * MB, TileCachePolicy.Next(210 * MB, 2000 * MB, 2000 * MB, 200 * MB, 1000 * MB));
            Assert.AreEqual(1000 * MB, TileCachePolicy.Next(990 * MB, 100 * MB, 2000 * MB, 200 * MB, 1000 * MB));
        }
        [Test] public void PhysicsRingGrowsWithSpeedAndIsCapped()
        {
            Assert.AreEqual(150f, TileCachePolicy.PhysicsRing(0f), 1e-4f);
            Assert.AreEqual(265f, TileCachePolicy.PhysicsRing(46f), 1e-3f);
            Assert.AreEqual(330f, TileCachePolicy.PhysicsRing(200f), 1e-4f);
        }
    }
}
