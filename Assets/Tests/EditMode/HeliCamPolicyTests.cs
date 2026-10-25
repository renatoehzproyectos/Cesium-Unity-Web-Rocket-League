using NUnit.Framework;
using RocketWorld.Sim;

namespace RocketWorld.Tests
{
    public class HeliCamPolicyTests
    {
        [Test] public void ParkedCarIsSeenFromAboveAtAboutSeventyDegrees()
        {
            float h = HeliCamPolicy.Height(0f, false, 1f), b = HeliCamPolicy.Back(0f, false, 1f);
            Assert.AreEqual(38f, h, 1e-4f);
            Assert.AreEqual(14f, b, 1e-4f);
            Assert.AreEqual(69.8f, HeliCamPolicy.PitchDeg(h, b), 0.2f);
        }
        [Test] public void ClimbsAndPullsBackWithSpeed()
        {
            Assert.Greater(HeliCamPolicy.Height(46f, false, 1f), HeliCamPolicy.Height(0f, false, 1f));
            Assert.Greater(HeliCamPolicy.Back(46f, false, 1f), HeliCamPolicy.Back(0f, false, 1f));
            Assert.Greater(HeliCamPolicy.Fov(46f), HeliCamPolicy.Fov(0f));
        }
        [Test] public void ZoomIsClamped()
        {
            Assert.AreEqual(38f * HeliCamPolicy.MaxZoom, HeliCamPolicy.Height(0f, false, 10f), 1e-4f);
            Assert.AreEqual(38f * HeliCamPolicy.MinZoom, HeliCamPolicy.Height(0f, false, 0.01f), 1e-4f);
        }
        [Test] public void CameraCycleVisitsChaseHelicopterOrbit()
        {
            Assert.AreEqual(2, GameRoot.NextCameraMode(0));
            Assert.AreEqual(1, GameRoot.NextCameraMode(2));
            Assert.AreEqual(0, GameRoot.NextCameraMode(1));
        }
    }
}
