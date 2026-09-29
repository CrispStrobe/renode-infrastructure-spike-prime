// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture, NonParallelizable]
    public class TI_DA8xx_ECAPTests
    {
        [SetUp]
        public void SetUp()
        {
            EmulationManager.Instance.Clear(); machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
            pwm = new TI_DA8xx_ECAP(machine, 1000);
        }
        [TearDown]
        public void TearDown() => EmulationManager.Instance.Clear();

        [Test]
        public void ShouldExposeExactRevisionAndNativeControlLanes()
        {
            const ulong address = 0x01F07000;
            machine.SystemBus.Register(pwm, new BusRangeRegistration(address, (ulong)pwm.Size));
            Assert.AreEqual(0x44D22100u, pwm.ReadDoubleWord(0x5C));
            Assert.AreEqual(6, pwm.ReadWord(0x2A));
            CollectionAssert.AreEqual(new byte[] { 0, 0, 6, 0 }, machine.SystemBus.ReadBytes(address + 0x28, 4));
            machine.SystemBus.WriteDoubleWord(address + 8, 9);
            Assert.AreEqual(9u, pwm.ReadDoubleWord(0x10), "Active period write mirrors shadow");
            machine.SystemBus.WriteDoubleWord(address + 0xC, 5);
            Assert.AreEqual(5u, pwm.ReadDoubleWord(0x14), "Active compare write mirrors shadow");
        }

        [Test]
        public void ShouldGenerateApwmCompareEdgesAndPreserveFrozenCounter()
        {
            Configure(9, 5);
            Assert.AreEqual(0.5, pwm.DutyCycle, 1e-10);
            Assert.True(pwm.Output.IsSet);
            Advance(5);
            Assert.False(pwm.Output.IsSet);
            Assert.AreEqual(5u, pwm.ReadDoubleWord(0));
            pwm.WriteWord(0x2A, 0x200);
            Advance(20);
            Assert.AreEqual(5u, pwm.ReadDoubleWord(0));
            pwm.WriteWord(0x2A, 0x210);
            Advance(5);
            Assert.True(pwm.Output.IsSet);
            Assert.AreEqual(0u, pwm.ReadDoubleWord(0));
        }

        [Test]
        public void ShouldLoadShorterPeriodShadowAtBoundaryAndLatchEventIrq()
        {
            Configure(9, 5);
            pwm.WriteWord(0x2C, 0x40);
            Advance(2);
            pwm.WriteDoubleWord(0x10, 4); pwm.WriteDoubleWord(0x14, 1);
            Assert.AreEqual(0.5, pwm.DutyCycle, 1e-10);
            Advance(7);
            Assert.True(pwm.Operational, pwm.UnsupportedConfiguration);
            Assert.AreEqual(4u, pwm.ReadDoubleWord(8));
            Assert.AreEqual(0.2, pwm.DutyCycle, 1e-10);
            Assert.True(pwm.IRQ.IsSet);
            Assert.AreEqual(0x41, pwm.ReadWord(0x2E) & 0x41);
            pwm.WriteByte(0x30, 0x41);
            Assert.False(pwm.IRQ.IsSet);
            Advance(1);
            Assert.True(pwm.Output.IsSet);
            Assert.AreEqual(0u, pwm.ReadDoubleWord(0));
        }

        [Test]
        public void ShouldHandleZeroFullAndInvertedDutyAndDriveMotorCOrD()
        {
            Configure(9, 0);
            Assert.AreEqual(0, pwm.DutyCycle);
            pwm.WriteDoubleWord(0xC, 10);
            Assert.AreEqual(1, pwm.DutyCycle);
            pwm.WriteWord(0x2A, 0x610);
            Assert.AreEqual(0, pwm.DutyCycle);
            pwm.WriteDoubleWord(0xC, 8);
            Assert.AreEqual(0.2, pwm.DutyCycle, 1e-10);
            var motor = new PWMDrivenMotor(machine, pwm, maximumEdgesPerSecond: 100);
            motor.OnGPIO(0, true);
            Advance(100);
            Assert.AreEqual(2, motor.TachometerCount);
        }

        [Test]
        public void ShouldRejectUnsupportedCaptureSyncAndExcessiveCycleRate()
        {
            pwm.WriteWord(0x2A, 0x10);
            Assert.False(pwm.Operational);
            Assert.That(pwm.UnsupportedConfiguration, Does.Contain("capture"));
            pwm.WriteWord(0x2A, 0x230);
            Assert.That(pwm.UnsupportedConfiguration, Does.Contain("synchronization"));
            var fast = new TI_DA8xx_ECAP(machine, 100000000, 1000);
            fast.WriteWord(0x2A, 0x210);
            Advance(10);
            Assert.False(fast.Operational);
            Assert.That(fast.UnsupportedConfiguration, Does.Contain("rate"));
            Assert.AreEqual(0, fast.DutyCycle);
            Assert.False(fast.Output.IsSet); Assert.False(fast.IRQ.IsSet);
        }

        private void Configure(uint period, uint compare)
        {
            pwm.WriteDoubleWord(8, period); pwm.WriteDoubleWord(0xC, compare);
            pwm.WriteWord(0x2A, 0x210);
        }
        private void Advance(ulong milliseconds) => ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMilliseconds(milliseconds), true);
        private Machine machine;
        private TI_DA8xx_ECAP pwm;
    }
}
