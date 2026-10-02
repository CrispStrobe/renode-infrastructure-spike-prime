// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    [NonParallelizable]
    public class STM32TimerSoftwareEventTests
    {
        [SetUp]
        public void SetUp()
        {
            EmulationManager.Instance.Clear();
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
            timer = new STM32_Timer(machine, 100000, 0xFFFF);
        }

        [TearDown]
        public void TearDown()
        {
            EmulationManager.Instance.Clear();
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void ShouldLatchSoftwareCompareWhileInterruptDisabled(int channel)
        {
            var bit = 1u << channel;
            var ccr = 0x34 + 4 * (channel - 1);
            timer.WriteDoubleWord(0x24, 65000); // CNT, stopped
            timer.WriteDoubleWord(ccr, 1000);
            timer.WriteDoubleWord(0x14, bit); // EGR CCxG, no UG
            Assert.AreEqual(bit, (timer.ReadDoubleWord(0x10) & 0x1F));
            Assert.IsFalse(timer.IRQ.IsSet);
            Assert.IsFalse(timer.CaptureCompareInterrupt.IsSet);
            Assert.AreEqual(65000u, timer.ReadDoubleWord(0x24));
            Assert.AreEqual(1000u, timer.ReadDoubleWord(ccr));
            Assert.AreEqual(0u, timer.ReadDoubleWord(0x14)); // write-only

            timer.WriteDoubleWord(0x0C, bit); // DIER CCxIE
            Assert.IsTrue(timer.IRQ.IsSet);
            Assert.IsTrue(timer.CaptureCompareInterrupt.IsSet);
            Assert.IsFalse(timer.UpdateInterrupt.IsSet);

            timer.WriteDoubleWord(0x10, ~bit); // SR write zero clears CCxIF
            Assert.IsFalse(timer.IRQ.IsSet);
            Assert.AreEqual(0u, (timer.ReadDoubleWord(0x10) & 0x1F));
        }

        [Test]
        public void ShouldGenerateEnabledCompareWithoutResettingRunningCounter()
        {
            timer.WriteDoubleWord(0x2C, 0xFFFF);
            timer.WriteDoubleWord(0x34, 500);
            timer.WriteDoubleWord(0x24, 1000); // missed compare target
            timer.WriteDoubleWord(0x0C, 2);
            timer.WriteDoubleWord(0x00, 1);
            timer.WriteDoubleWord(0x14, 2);
            Assert.IsTrue(timer.IRQ.IsSet);
            Assert.AreEqual(2u, (timer.ReadDoubleWord(0x10) & 0x1F));
            Assert.AreEqual(1000u, timer.ReadDoubleWord(0x24));
            Assert.AreEqual(500u, timer.ReadDoubleWord(0x34));
            timer.WriteDoubleWord(0x14, 0); // no event
            Assert.IsTrue(timer.IRQ.IsSet);
            timer.WriteDoubleWord(0x10, 0);
            timer.WriteDoubleWord(0x14, 0);
            Assert.IsFalse(timer.IRQ.IsSet);
        }

        [Test]
        public void ShouldKeepIndependentChannelFlagsAndMask()
        {
            timer.WriteDoubleWord(0x0C, 2);
            timer.WriteDoubleWord(0x14, 0x1E);
            Assert.AreEqual(0x1Eu, (timer.ReadDoubleWord(0x10) & 0x1F));
            Assert.IsTrue(timer.IRQ.IsSet);
            timer.WriteDoubleWord(0x10, ~2u);
            Assert.AreEqual(0x1Cu, (timer.ReadDoubleWord(0x10) & 0x1F));
            Assert.IsFalse(timer.IRQ.IsSet);
            timer.WriteDoubleWord(0x0C, 4);
            Assert.IsTrue(timer.IRQ.IsSet);
            timer.Reset();
            Assert.IsFalse(timer.IRQ.IsSet);
            Assert.AreEqual(0u, (timer.ReadDoubleWord(0x10) & 0x1F));
        }

        [Test]
        public void ShouldKeepUpdateGenerationSeparateFromCompareGeneration()
        {
            timer.WriteDoubleWord(0x24, 123);
            timer.WriteDoubleWord(0x14, 0);
            Assert.AreEqual(123u, timer.ReadDoubleWord(0x24));
            timer.WriteDoubleWord(0x14, 1); // UG still resets the counter
            Assert.AreEqual(0u, timer.ReadDoubleWord(0x24));
        }

        [Test]
        public void ShouldLatchNaturalOutputCompareWhileInterruptMasked()
        {
            timer.WriteDoubleWord(0x2C, 0xFFFF);
            timer.WriteDoubleWord(0x34, 5);
            timer.WriteDoubleWord(0x20, 1); // CC1E
            timer.WriteDoubleWord(0x00, 1); // CEN, interrupt masked
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(60), true);
            Assert.AreEqual(2u, timer.ReadDoubleWord(0x10) & 0x1F);
            Assert.IsFalse(timer.IRQ.IsSet);
            timer.WriteDoubleWord(0x0C, 2);
            Assert.IsTrue(timer.IRQ.IsSet);
        }

        [Test]
        public void ShouldLatchZeroOutputCompareAtRolloverWhileInterruptMasked()
        {
            timer.WriteDoubleWord(0x2C, 0xFFFF);
            timer.WriteDoubleWord(0x34, 0);
            timer.WriteDoubleWord(0x24, 0xFFF0);
            timer.WriteDoubleWord(0x20, 1); // CC1E, CC1IE masked
            timer.WriteDoubleWord(0x00, 1);
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(250), true);
            Assert.AreEqual(2u, timer.ReadDoubleWord(0x10) & 0x1F);
            Assert.IsFalse(timer.IRQ.IsSet);
            timer.WriteDoubleWord(0x0C, 2);
            Assert.IsTrue(timer.IRQ.IsSet);
        }

        private Machine machine;
        private STM32_Timer timer;
    }
}
