//
// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    [NonParallelizable]
    public class TI_DA8xx_Timer64Tests
    {
        [SetUp]
        public void SetUp()
        {
            EmulationManager.Instance.Clear();
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
            timer = new TI_DA8xx_Timer64(machine, Frequency);
        }

        [TearDown]
        public void TearDown()
        {
            EmulationManager.Instance.Clear();
        }

        [Test]
        public void ShouldExposeDocumentedRevisionResetAndWritableMasks()
        {
            Assert.AreEqual(0x4472020Cu, timer.ReadDoubleWord(Revision));
            Assert.AreEqual(0, timer.ReadDoubleWord(TimerControl));
            Assert.AreEqual(0, timer.ReadDoubleWord(TimerGlobalControl));
            Assert.AreEqual(0, timer.ReadDoubleWord(InterruptControlAndStatus));

            timer.WriteDoubleWord(TimerControl, uint.MaxValue);
            timer.WriteDoubleWord(TimerGlobalControl, uint.MaxValue);
            timer.WriteDoubleWord(InterruptControlAndStatus, uint.MaxValue);
            Assert.AreEqual(0x04C03FFEu, timer.ReadDoubleWord(TimerControl));
            Assert.AreEqual(0x0000FF1Fu, timer.ReadDoubleWord(TimerGlobalControl));
            Assert.AreEqual(0x00050005u, timer.ReadDoubleWord(InterruptControlAndStatus));

            timer.Reset();
            Assert.False(timer.IRQ12.IsSet);
            Assert.False(timer.IRQ34.IsSet);
        }

        [Test]
        public void ShouldRun64BitOneShotLatchStatusAndClearWithW1C()
        {
            timer.WriteDoubleWord(Period12, 9);
            timer.WriteDoubleWord(Period34, 0);
            timer.WriteDoubleWord(TimerGlobalControl, Timer12ResetRelease | Timer34ResetRelease);
            timer.WriteDoubleWord(InterruptControlAndStatus, PeriodInterruptEnable12);
            timer.WriteDoubleWord(TimerControl, EnableOnce12);

            AdvanceMilliseconds(11);

            Assert.True(timer.IRQ12.IsSet);
            Assert.AreEqual(PeriodInterruptEnable12 | PeriodInterruptStatus12,
                timer.ReadDoubleWord(InterruptControlAndStatus));
            var stopped = timer.ReadDoubleWord(Counter12);
            AdvanceMilliseconds(20);
            Assert.AreEqual(stopped, timer.ReadDoubleWord(Counter12));

            timer.WriteDoubleWord(InterruptControlAndStatus,
                PeriodInterruptEnable12 | PeriodInterruptStatus12);
            Assert.False(timer.IRQ12.IsSet);
            Assert.AreEqual(PeriodInterruptEnable12,
                timer.ReadDoubleWord(InterruptControlAndStatus));
        }

        [Test]
        public void ShouldLatchMatchWhileMaskedAndAssertWhenEnabled()
        {
            timer.WriteDoubleWord(Period12, 4);
            timer.WriteDoubleWord(TimerGlobalControl, Timer12ResetRelease | Timer34ResetRelease);
            timer.WriteDoubleWord(TimerControl, EnableOnce12);
            AdvanceMilliseconds(6);

            Assert.False(timer.IRQ12.IsSet);
            Assert.AreEqual(PeriodInterruptStatus12,
                timer.ReadDoubleWord(InterruptControlAndStatus));

            timer.WriteDoubleWord(InterruptControlAndStatus, PeriodInterruptEnable12);
            Assert.True(timer.IRQ12.IsSet);
        }

        [Test]
        public void ShouldGateCounterWithResetAndGPIOAndLatch64BitRead()
        {
            timer.WriteDoubleWord(Period12, 1000);
            timer.WriteDoubleWord(TimerGlobalControl, Timer12ResetRelease | Timer34ResetRelease);
            timer.WriteDoubleWord(TimerControl, EnableContinuous12);
            AdvanceMilliseconds(5);
            Assert.That(timer.ReadDoubleWord(Counter12), Is.GreaterThan(0));

            timer.WriteDoubleWord(TimerGlobalControl, Timer12ResetRelease);
            Assert.AreEqual(0, timer.ReadDoubleWord(Counter12));
            AdvanceMilliseconds(5);
            Assert.AreEqual(0, timer.ReadDoubleWord(Counter12));

            timer.WriteDoubleWord(Counter34, 0x12345678);
            timer.WriteDoubleWord(Counter12, 0xABCDEF01);
            Assert.AreEqual(0xABCDEF01u, timer.ReadDoubleWord(Counter12));
            timer.WriteDoubleWord(Counter34, 0x87654321);
            Assert.AreEqual(0x12345678u, timer.ReadDoubleWord(Counter34),
                "TIM12 must latch TIM34 for a coherent 64-bit read");
        }

        [Test]
        public void ShouldRoutePhysicalTimerEventThroughAM1808AintcEvent21()
        {
            var aintc = new TI_DA8xx_AINTC();
            timer.IRQ12.Connect(aintc, Timer0Interrupt12Event);
            aintc.WriteDoubleWord(ChannelMap5, 1u << 8); // event 21 -> host IRQ channel 1
            aintc.WriteDoubleWord(EnableIndexedSet, (uint)Timer0Interrupt12Event);
            aintc.WriteDoubleWord(HostEnableIndexedSet, 1);
            aintc.WriteDoubleWord(GlobalEnable, 1);

            timer.WriteDoubleWord(Period12, 4);
            timer.WriteDoubleWord(TimerGlobalControl, Timer12ResetRelease | Timer34ResetRelease);
            timer.WriteDoubleWord(InterruptControlAndStatus, PeriodInterruptEnable12);
            timer.WriteDoubleWord(TimerControl, EnableOnce12);
            AdvanceMilliseconds(6);

            Assert.True(aintc.IRQ.IsSet);
            Assert.AreEqual(Timer0Interrupt12Event, aintc.ReadDoubleWord(HostPrioritizedIndex2));
            timer.WriteDoubleWord(InterruptControlAndStatus,
                PeriodInterruptEnable12 | PeriodInterruptStatus12);
            aintc.WriteDoubleWord(StatusIndexedClear, (uint)Timer0Interrupt12Event);
            Assert.False(aintc.IRQ.IsSet);
        }

        private void AdvanceMilliseconds(ulong milliseconds)
        {
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMilliseconds(milliseconds), true);
        }

        private Machine machine;
        private TI_DA8xx_Timer64 timer;

        private const ulong Frequency = 1000;
        private const uint Timer12ResetRelease = 1u << 0;
        private const uint Timer34ResetRelease = 1u << 1;
        private const uint EnableOnce12 = 1u << 6;
        private const uint EnableContinuous12 = 2u << 6;
        private const uint PeriodInterruptEnable12 = 1u << 0;
        private const uint PeriodInterruptStatus12 = 1u << 1;
        private const int Timer0Interrupt12Event = 21;

        private const long Revision = 0x00;
        private const long Counter12 = 0x10;
        private const long Counter34 = 0x14;
        private const long Period12 = 0x18;
        private const long Period34 = 0x1C;
        private const long TimerControl = 0x20;
        private const long TimerGlobalControl = 0x24;
        private const long InterruptControlAndStatus = 0x44;
        private const long GlobalEnable = 0x010;
        private const long StatusIndexedClear = 0x024;
        private const long EnableIndexedSet = 0x028;
        private const long HostEnableIndexedSet = 0x034;
        private const long ChannelMap5 = 0x414;
        private const long HostPrioritizedIndex2 = 0x904;
    }
}
