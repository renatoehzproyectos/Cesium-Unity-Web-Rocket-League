using NUnit.Framework;
using RocketWorld.Sim;

namespace RocketWorld.Tests
{
    public class CinematicPolicyTests
    {
        [Test] public void SlidersAreClampedAndZeroMeansOff()
        {
            Assert.AreEqual(0f, CinematicPolicy.ClampAmount(-1f), 1e-6f);
            Assert.AreEqual(2f, CinematicPolicy.ClampAmount(5f), 1e-6f);
            Assert.IsFalse(CinematicPolicy.IsOn(0f));
            Assert.IsTrue(CinematicPolicy.IsOn(1f));
        }
        [Test] public void DaylightIsOneAtNoonAndZeroAtMidnight()
        {
            Assert.AreEqual(1f, CinematicPolicy.Daylight(14f), 1e-4f);
            Assert.AreEqual(0f, CinematicPolicy.Daylight(0f), 1e-4f);
        }
        [Test] public void BloomIsStrongerAtNightWhileBoostingAndWithTheSlider()
        {
            Assert.Greater(CinematicPolicy.BloomIntensity(0f, false), CinematicPolicy.BloomIntensity(1f, false));
            Assert.Greater(CinematicPolicy.BloomIntensity(1f, true), CinematicPolicy.BloomIntensity(1f, false));
            Assert.AreEqual(2f * CinematicPolicy.BloomIntensity(1f, false, 1f), CinematicPolicy.BloomIntensity(1f, false, 2f), 1e-5f);
            Assert.AreEqual(0f, CinematicPolicy.BloomIntensity(0f, true, 0f), 1e-6f);
        }
        [Test] public void MotionBlurNeedsSpeedAndIsCapped()
        {
            Assert.AreEqual(0f, CinematicPolicy.MotionBlurIntensity(5f, false), 1e-6f);
            Assert.Greater(CinematicPolicy.MotionBlurIntensity(46f, false), 0.6f);
            Assert.LessOrEqual(CinematicPolicy.MotionBlurIntensity(500f, true, 2f), 1f);
            Assert.AreEqual(0f, CinematicPolicy.MotionBlurIntensity(46f, true, 0f), 1e-6f);
        }
        [Test] public void DepthOfFieldKeepsTheNearWorldSharp()
        {
            Assert.AreEqual(120f, CinematicPolicy.DofStart(8f), 1e-4f);
            Assert.AreEqual(140f, CinematicPolicy.DofStart(40f), 1e-4f);
            Assert.AreEqual(820f, CinematicPolicy.DofEnd(120f), 1e-4f);
            Assert.AreEqual(0.9f, CinematicPolicy.DofMaxRadius(1f), 1e-4f);
            Assert.AreEqual(1.3f, CinematicPolicy.DofMaxRadius(2f), 1e-4f);
        }
    }
}
