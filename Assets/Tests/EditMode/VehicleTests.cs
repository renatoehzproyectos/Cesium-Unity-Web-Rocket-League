using NUnit.Framework;
using UnityEngine;

namespace RocketWorld.Tests
{
    /// <summary>Data-level checks of the vehicle table, the touch layouts and Hyper Boost (no scene, no physics).</summary>
    public class VehicleTests
    {
        static readonly VehicleKind[] Driven = { VehicleKind.F35B, VehicleKind.F15E, VehicleKind.Helicopter, VehicleKind.DeLorean };

        [Test] public void EveryDrivenVehicleHasACompleteSpec()
        {
            foreach (var k in Driven)
            {
                var s = Vehicles.Get(k);
                Assert.IsNotNull(s, k.ToString());
                Assert.AreEqual(k, s.kind);
                Assert.IsTrue(s.model.StartsWith("Models/Vehicles/"), s.name);
                Assert.Greater(s.mass, 100f, s.name); Assert.Greater(s.vMax, 10f, s.name);
                Assert.Greater(s.boxes.Length, 0, s.name); Assert.Greater(s.wheels.Length, 0, s.name);
                Assert.Greater(s.camBack, s.camUp * 0.5f, s.name);
                Assert.AreEqual(Vehicles.ProfileOf(k), s.profile, s.name);
            }
            Assert.IsNull(Vehicles.Get(VehicleKind.Car)); Assert.IsNull(Vehicles.Get(VehicleKind.Cessna));
        }

        [Test] public void ColliderBoxesAndWheelsStayInsideTheModel()
        {
            foreach (var k in Driven)
            {
                var s = Vehicles.Get(k);
                foreach (var b in s.boxes) Assert.Less(Mathf.Abs(b.center.x) + b.size.x * 0.5f, 20f, s.name);
                foreach (var w in s.wheels) Assert.Greater(w.radius, 0f, s.name);
            }
        }

        [Test] public void OrderListsEveryKindOnce()
        {
            Assert.AreEqual(System.Enum.GetValues(typeof(VehicleKind)).Length, Vehicles.Order.Length);
            var seen = new System.Collections.Generic.HashSet<VehicleKind>(Vehicles.Order);
            Assert.AreEqual(Vehicles.Order.Length, seen.Count);
        }

        [Test] public void EveryTouchProfileHasAHyperButtonAndNoOverlappingWidgets()
        {
            foreach (TouchProfile p in System.Enum.GetValues(typeof(TouchProfile)))
            {
                var ws = TouchLayout.For(p);
                bool hyper = false;
                for (int i = 0; i < ws.Length; i++)
                {
                    hyper |= ws[i].id == "hyper";
                    Assert.GreaterOrEqual(ws[i].zone.x, 0f, p + "/" + ws[i].id); Assert.LessOrEqual(ws[i].zone.xMax, 1.0001f, p + "/" + ws[i].id);
                    Assert.GreaterOrEqual(ws[i].zone.y, 0f, p + "/" + ws[i].id); Assert.LessOrEqual(ws[i].zone.yMax, 1.0001f, p + "/" + ws[i].id);
                    for (int j = i + 1; j < ws.Length; j++) Assert.IsFalse(ws[i].zone.Overlaps(ws[j].zone), $"{p}: {ws[i].id} overlaps {ws[j].id}");
                }
                Assert.IsTrue(hyper, p + " has no Hyper Boost button");
                Assert.Less(ws.Length, 16, p.ToString());                       // CarInput keeps state for 16 widgets
            }
        }

        [Test] public void AircraftHaveAThrottleSliderAndARudder()
        {
            foreach (var p in new[] { TouchProfile.Cessna, TouchProfile.Jet, TouchProfile.Vtol })
            {
                var ids = new System.Collections.Generic.List<string>(); foreach (var w in TouchLayout.For(p)) ids.Add(w.id);
                Assert.Contains("throttle", ids, p.ToString()); Assert.Contains("rudder", ids, p.ToString());
            }
            var vtol = new System.Collections.Generic.List<string>(); foreach (var w in TouchLayout.For(TouchProfile.Vtol)) vtol.Add(w.id);
            Assert.Contains("up", vtol); Assert.Contains("down", vtol); Assert.Contains("mode", vtol);        // F-35B vertical throttle + VTOL toggle
        }

        [Test] public void HyperBoostIsFivehundredPercentMore()
        {
            Assert.AreEqual(1f, HyperBoost.Mul(0f), 1e-5f);
            Assert.AreEqual(6f, HyperBoost.Mul(1f), 1e-5f);
            Assert.AreEqual(6f, HyperBoost.Mul(3f), 1e-5f);                  // clamped
        }

        [Test] public void AtmosphereThinsWithAltitude()
        {
            Assert.AreEqual(1.225f, Atmosphere.Density(0f), 1e-3f);
            Assert.Less(Atmosphere.Density(10000f), Atmosphere.Density(5000f));
            Assert.Less(Atmosphere.Sound(11000f), Atmosphere.Sound(0f));
        }

        [Test] public void NewInputActionsHaveDefaults()
        {
            var b = new InputBindings();
            foreach (var a in new[] { CarAction.Vehicle, CarAction.HyperBoost, CarAction.VehicleMode, CarAction.Gear, CarAction.Flaps })
            {
                var d = InputBindings.Default(a);
                Assert.IsFalse(string.IsNullOrEmpty(d.key0), a.ToString());
                Assert.IsFalse(string.IsNullOrEmpty(InputBindings.Label(a)), a.ToString());
            }
        }
    }
}
