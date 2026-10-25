using NUnit.Framework;

namespace RocketWorld.Tests
{
    public class InputBindingsTests
    {
        [Test] public void DefaultsCoverEveryActionWithAKeyOrPad()
        {
            var ib = new InputBindings();
            foreach (var a in InputBindings.Actions)
            {
                var b = ib.Get(a);
                Assert.IsTrue(!string.IsNullOrEmpty(b.key0) || b.pad != PadInput.None, a.ToString());
            }
        }

        [Test] public void OneKeyCanServeSeveralActions()
        {
            var ib = new InputBindings();
            ib.SetKey(CarAction.Jump, 0, "W");                     // W is also Accelerate's key
            Assert.AreEqual("W", ib.GetKey(CarAction.Jump, 0));
            Assert.AreEqual("W", ib.GetKey(CarAction.Accelerate, 0));
        }

        [Test] public void OnePadInputCanServeSeveralActions()
        {
            var ib = new InputBindings();
            ib.SetPad(CarAction.Boost, PadInput.ButtonSouth);      // A is also Jump's button
            Assert.AreEqual(PadInput.ButtonSouth, ib.GetPad(CarAction.Boost));
            Assert.AreEqual(PadInput.ButtonSouth, ib.GetPad(CarAction.Jump));
        }

        [Test] public void MenuKeyboardKeysAreLocked()
        {
            var ib = new InputBindings();
            Assert.IsNull(ib.SetKey(CarAction.Menu, 0, "M"));
            Assert.AreEqual("", ib.GetKey(CarAction.Menu, 0) ?? "");
        }

        [Test] public void EncodeDecodeRoundTrips()
        {
            var b = new InputBindings.Binding { key0 = "Q", key1 = "", pad = PadInput.RightStickLeft };
            Assert.IsTrue(InputBindings.TryDecode(InputBindings.Encode(b), out var r));
            Assert.AreEqual("Q", r.key0); Assert.AreEqual("", r.key1); Assert.AreEqual(PadInput.RightStickLeft, r.pad);
        }

        [Test] public void DecodeRejectsGarbage()
        {
            Assert.IsFalse(InputBindings.TryDecode("", out _));
            Assert.IsFalse(InputBindings.TryDecode("a|b", out _));
            Assert.IsFalse(InputBindings.TryDecode("a|b|999", out _));
        }

        [Test] public void ResetRestoresDefaults()
        {
            var ib = new InputBindings();
            ib.SetKey(CarAction.Jump, 0, "J"); ib.Clear(CarAction.Boost, InputBindings.SlotPad);
            ib.ResetAll();
            Assert.AreEqual("Space", ib.GetKey(CarAction.Jump, 0));
            Assert.AreEqual(PadInput.ButtonEast, ib.GetPad(CarAction.Boost));
        }
    }
}
