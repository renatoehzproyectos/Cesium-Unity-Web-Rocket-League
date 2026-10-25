using NUnit.Framework;
using RocketWorld.Sim;

namespace RocketWorld.Tests
{
    public class CinematicPolicyTests
    {
        [Test] public void DaylightIsOneAtNoonAndZeroAtMidnight()
        {
            Assert.AreEqual(1f, CinematicPolicy.Daylight(14f), 1e-4f);
            Assert.AreEqual(0f, CinematicPolicy.Daylight(0f), 1e-4f);
        }
        [Test] public void BloomIsStrongerAtNightAndWhileBoosting()
        {
            Assert.Greater(CinematicPolicy.BloomIntensity(0f, false), CinematicPolicy.BloomIntensity(1f, false));
            Assert.Greater(CinematicPolicy.BloomIntensity(1f, true), CinematicPolicy.BloomIntensity(1f, false));
        }
        [Test] public void MotionBlurNeedsSpeedAndIsCapped()
        {
            Assert.AreEqual(0f, CinematicPolicy.MotionBlurIntensity(5f, false), 1e-6f);
            Assert.Greater(CinematicPolicy.MotionBlurIntensity(46f, false), 0.6f);
            Assert.LessOrEqual(CinematicPolicy.MotionBlurIntensity(500f, true), 0.8f);
        }
        [Test] public void DepthOfFieldKeepsTheNearWorldSharp()
        {
            Assert.AreEqual(120f, CinematicPolicy.DofStart(8f), 1e-4f);
            Assert.AreEqual(140f, CinematicPolicy.DofStart(40f), 1e-4f);
            Assert.AreEqual(820f, CinematicPolicy.DofEnd(120f), 1e-4f);
        }
        [Test] public void EdgeBlurTightensWithSpeed()
        {
            Assert.AreEqual(0.6f, CinematicPolicy.EdgeBlurStrength(0f), 1e-4f);
            Assert.AreEqual(1f, CinematicPolicy.EdgeBlurStrength(46f), 1e-4f);
        }
    }
}
