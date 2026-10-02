// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    [NonParallelizable]
    public class STM32TimerRolloverTests
    {
        [SetUp]
        public void SetUp()
        {
            EmulationManager.Instance.Clear();
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
            timer = new STM32_Timer(machine, 1000, 0xFFFF);
        }
        [TearDown]
        public void TearDown() { EmulationManager.Instance.Clear(); }

        [Test]
        public void ShouldWakeOnZeroCompareAcrossRepeatedRollover()
        {
            Check(0);
        }
        [Test]
        public void ShouldWakeOnNonzeroCompareAfterRollover()
        {
            Check(3);
        }
        [Test]
        public void ShouldExposeOverflowAndZeroCompareTogetherOnInterrupt()
        {
            var observer = new FlagObserver(timer);
            timer.IRQ.Connect(observer, 0);
            timer.WriteDoubleWord(0x34, 0);
            timer.WriteDoubleWord(0x0C, 3);
            timer.WriteDoubleWord(0x24, 0xFFF0);
            timer.WriteDoubleWord(0x00, 1);
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMilliseconds(25), true);
            Assert.AreEqual(3u, observer.FlagsOnRise & 3u);
        }
        private sealed class FlagObserver : IGPIOReceiver
        {
            public FlagObserver(STM32_Timer timer) { this.timer = timer; }
            public void Reset() { FlagsOnRise = 0; }
            public void OnGPIO(int number, bool value)
            {
                if(value) FlagsOnRise = timer.ReadDoubleWord(0x10);
            }
            public uint FlagsOnRise { get; private set; }
            private readonly STM32_Timer timer;
        }
        private void Check(uint compare)
        {
            timer.WriteDoubleWord(0x34, compare);
            timer.WriteDoubleWord(0x0C, 2); // CC1 interrupt without update IRQ.
            timer.WriteDoubleWord(0x00, 1); // Up-counting, periodic, enabled.
            for(var attempt = 0; attempt < 3; attempt++)
            {
                timer.WriteDoubleWord(0x10, 0); // Acknowledge prior flags.
                timer.WriteDoubleWord(0x24, 0xFFF0);
                timer.WriteDoubleWord(0x0C, 2); // Re-arm against the new counter value.
                Assert.AreEqual(false, timer.IRQ.IsSet);
                ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMilliseconds(10), true);
                Assert.AreEqual(0u, timer.ReadDoubleWord(0x10) & 2u);
                Assert.AreEqual(false, timer.IRQ.IsSet);
                ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMilliseconds(15), true);
                Assert.AreEqual(2u, timer.ReadDoubleWord(0x10) & 2u);
                Assert.AreEqual(true, timer.IRQ.IsSet);
                timer.WriteDoubleWord(0x10, 0xFFFFFFFD); // Clear CC1 only.
                Assert.AreEqual(false, timer.IRQ.IsSet);
            }
        }
        private Machine machine;
        private STM32_Timer timer;
    }
}
