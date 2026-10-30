using NUnit.Framework;
using RocketWorld.Sim;

namespace RocketWorld.Tests
{
    public class CamTunePolicyTests
    {
        [Test] public void DefaultDelayKeepsTheOriginalSmoothing()
        {
            Assert.AreEqual(3.6f, CamTunePolicy.Rate(3.6f, CamTunePolicy.DefaultDelay), 1e-5f);
        }
        [Test] public void LongerDelayIsLazierAndZeroIsRigid()
        {
            Assert.Less(CamTunePolicy.Rate(6.5f, 2f), CamTunePolicy.Rate(6.5f, 1f));
            Assert.Greater(CamTunePolicy.Blend(CamTunePolicy.Rate(6.5f, 0f), 0.016f), 0.99f);      // delay 0: the camera is on the target in one frame
            Assert.Less(CamTunePolicy.Blend(CamTunePolicy.Rate(6.5f, 3f), 0.016f), CamTunePolicy.Blend(CamTunePolicy.Rate(6.5f, 1f), 0.016f));
        }
        [Test] public void DelayIsClamped()
        {
            Assert.AreEqual(0f, CamTunePolicy.ClampDelay(-1f));
            Assert.AreEqual(CamTunePolicy.MaxDelay, CamTunePolicy.ClampDelay(99f));
        }
        [Test] public void OffsetsAreClamped()
        {
            Assert.AreEqual(CamTunePolicy.MaxTurnH, CamTunePolicy.TapStep(CamTunePolicy.MaxTurnH - 1f, +1, 5f, CamTunePolicy.MaxTurnH), 1e-5f);
            Assert.AreEqual(-CamTunePolicy.MaxMove, CamTunePolicy.TapStep(-CamTunePolicy.MaxMove, -1, 0.5f, CamTunePolicy.MaxMove), 1e-5f);
        }
        [Test] public void HoldOnlyRepeatsAfterTheInitialDelay()
        {
            Assert.AreEqual(10f, CamTunePolicy.HoldStep(10f, +1, 0.2f, 0.1f, 30f, 0.35f, 90f), 1e-5f);
            Assert.AreEqual(13f, CamTunePolicy.HoldStep(10f, +1, 0.5f, 0.1f, 30f, 0.35f, 90f), 1e-5f);
            Assert.AreEqual(7f, CamTunePolicy.HoldStep(10f, -1, 0.5f, 0.1f, 30f, 0.35f, 90f), 1e-5f);
        }
        [Test] public void FovStaysInsideASaneRange()
        {
            Assert.AreEqual(CamTunePolicy.MinFov, CamTunePolicy.Fov(30f, -40f));
            Assert.AreEqual(CamTunePolicy.MaxFovAbs, CamTunePolicy.Fov(110f, 40f));
            Assert.AreEqual(90f, CamTunePolicy.Fov(80f, 10f), 1e-5f);
        }
    }
}
