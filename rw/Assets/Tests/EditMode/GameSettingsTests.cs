using NUnit.Framework;

namespace RocketWorld.Tests
{
    public class GameSettingsTests
    {
        [Test] public void ShareQueryNeverContainsSecrets()
        {
            var s = new GameSettings { googleKey = "SECRET-KEY", ionToken = "SECRET-TOKEN" };
            var q = s.ShareQuery(10f, 20f, 30f);
            StringAssert.DoesNotContain("SECRET", q);
            StringAssert.Contains("lat=36.72130", q);
            StringAssert.Contains("yaw=10", q);
        }

        [Test] public void PresetsAreSane()
        {
            foreach (var p in GameSettings.Presets)
            {
                Assert.That(p.lat, Is.InRange(-90.0, 90.0), p.name);
                Assert.That(p.lon, Is.InRange(-180.0, 180.0), p.name);
                Assert.That(p.h, Is.InRange(-200f, 800f), p.name);
            }
            Assert.AreEqual(6, GameSettings.CarColors.Length);
        }

        [Test] public void CredentialsCheckFollowsProvider()
        {
            var s = new GameSettings { provider = TileProvider.GoogleDirect, googleKey = "", ionToken = "x" };
            Assert.IsFalse(s.HasCredentials);
            s.provider = TileProvider.CesiumIon;
            Assert.IsTrue(s.HasCredentials);
        }

        [Test] public void MobileDefaultsAreLeaner()
        {
            var s = new GameSettings();
            float before = s.renderScale;
            DeviceProfile.ApplyFirstRunDefaults(s);       // desktop in the Editor: unchanged
            Assert.AreEqual(before, s.renderScale);
        }
    }
}
