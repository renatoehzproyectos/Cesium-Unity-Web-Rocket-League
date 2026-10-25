using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RocketWorld.Sim;
using UnityEngine;

namespace RocketWorld.Tests
{
    /// <summary>The C# port of JSBSim against native JSBSim 1.2.x logs (tools/flight/golden). The full comparison runs in tools/flight (CI job "flight").</summary>
    public class FlightModelTests
    {
        static JsbAircraft Load()
        {
            string r = Path.Combine(Application.dataPath, "Resources/Flight");
            return JsbAircraft.Load(File.ReadAllText(r + "/boxplane.xml"), File.ReadAllText(r + "/eng_electric160.xml"), File.ReadAllText(r + "/prop_75in2f.xml"));
        }

        [Test] public void MatchesNativeJsbsimOnTheCruiseRun()
        {
            var ac = Load();
            var lines = File.ReadAllLines(Path.Combine(Application.dataPath, "../tools/flight/golden/cruise.csv"));
            var col = lines[1].Split(',').Select((h, i) => new { h = h.Trim(), i }).ToDictionary(x => x.h, x => x.i);
            double[][] rows = lines.Skip(2).Where(l => l.Length > 5).Select(l => l.Split(',').Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray()).ToArray();
            ac.propRpm = rows[0][col["propeller-rpm"]];
            double dt = rows[1][0] - rows[0][0];
            for (int k = 0; k < rows.Length; k++)
            {
                var r = rows[k];
                ac.elevatorPosRad = r[col["elevator-pos-rad"]]; ac.aileronPosRad = r[col["aileron-pos-rad"]]; ac.rudderPosRad = r[col["rudder-pos-rad"]];
                ac.throttle = r[0] > 1.0 ? 0.7 : 0.0;
                var o = ac.Step(new D3(r[col["u-fps"]], r[col["v-fps"]], r[col["w-fps"]]), new D3(r[col["p-rad_sec"]], r[col["q-rad_sec"]], r[col["r-rad_sec"]]), r[col["h-sl-ft"]], dt);
                if (k < 1) continue;
                Assert.AreEqual(r[col["alpha-rad"]], o.alpha, 1e-9);
                Assert.AreEqual(r[col["qbar-psf"]], o.qbar, 1e-6);
                Assert.AreEqual(r[col["fbz-aero-lbs"]], o.aeroForce.z, 1e-6 * System.Math.Max(1, System.Math.Abs(r[col["fbz-aero-lbs"]])));
                Assert.AreEqual(r[col["m-aero-lbsft"]], o.aeroMoment.y, 1e-6 * System.Math.Max(1, System.Math.Abs(r[col["m-aero-lbsft"]])));
                Assert.AreEqual(r[col["fbx-prop-lbs"]], o.propForce.x, 2e-3 * System.Math.Max(10, System.Math.Abs(r[col["fbx-prop-lbs"]])));
            }
        }

        [Test] public void BodyFrameMappingKeepsHandedness()
        {
            // JSBSim nose-up pitch rate q>0 must be a rotation that raises the nose in Unity (negative about +X), right wing down p>0 negative about +Z, yaw right r>0 positive about +Y
            Assert.AreEqual(new Vector3(0, 0, -1), PlaneModel.MomentToUnity(new D3(1, 0, 0)) / PlaneModel.LbfFtToNm);
            Assert.AreEqual(new Vector3(-1, 0, 0), PlaneModel.MomentToUnity(new D3(0, 1, 0)) / PlaneModel.LbfFtToNm);
            Assert.AreEqual(new Vector3(0, 1, 0), PlaneModel.MomentToUnity(new D3(0, 0, 1)) / PlaneModel.LbfFtToNm);
            Assert.AreEqual(new Vector3(0, 0, 1), PlaneModel.ForceToUnity(new D3(1, 0, 0)) / PlaneModel.LbfToN);   // forward
            Assert.AreEqual(new Vector3(0, -1, 0), PlaneModel.ForceToUnity(new D3(0, 0, 1)) / PlaneModel.LbfToN);  // body z is down
        }
    }
}
