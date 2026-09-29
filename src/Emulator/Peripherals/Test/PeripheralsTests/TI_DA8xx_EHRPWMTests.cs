// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture, NonParallelizable]
    public class TI_DA8xx_EHRPWMTests
    {
        [SetUp]
        public void SetUp()
        {
            EmulationManager.Instance.Clear();
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
            pwm = new TI_DA8xx_EHRPWM(machine, 1000);
        }

        [TearDown]
        public void TearDown() => EmulationManager.Instance.Clear();

        [Test]
        public void ShouldExposeHalfwordResetAndByteDwordBusLanes()
        {
            const ulong address = 0x01F02000;
            machine.SystemBus.Register(pwm, new BusRangeRegistration(address, (ulong)pwm.Size));
            Assert.AreEqual(0x83, pwm.ReadWord(0));
            CollectionAssert.AreEqual(new byte[] { 0x83, 0, 1, 0 }, machine.SystemBus.ReadBytes(address, 4));
            machine.SystemBus.WriteDoubleWord(address + 0x10, 0x12340000);
            Assert.AreEqual(0x1234, pwm.ReadWord(0x12));
            Assert.AreEqual(0x100, pwm.ReadWord(0xE) & 0x100);
            Assert.False(pwm.OutputA.IsSet);
            Assert.False(pwm.IRQ.IsSet);
            pwm.WriteWord(8, 0x1234);
            Assert.AreEqual(0x1234, pwm.ReadWord(8), "Frozen TBCNT accepts the full 16-bit value");
        }

        [Test]
        public void ShouldGenerateUpCountPwmAndHoldCounterWhenFrozen()
        {
            Configure(0, 9, 5);
            Assert.True(pwm.OutputB.IsSet);
            Assert.AreEqual(0.5, pwm.DutyCycleB, 1e-10);
            Advance(4);
            Assert.True(pwm.OutputB.IsSet);
            Assert.AreEqual(4, pwm.ReadWord(8));
            Advance(1);
            Assert.False(pwm.OutputB.IsSet);
            pwm.WriteWord(0, 3 | 8);
            Advance(30);
            Assert.AreEqual(5, pwm.ReadWord(8));
            Assert.False(pwm.OutputB.IsSet);
            pwm.WriteWord(0, 8);
            Advance(5);
            Assert.True(pwm.OutputB.IsSet);
        }

        [Test]
        public void ShouldRespectZeroAndFullDutyAndQualifierCollisionPriority()
        {
            Configure(0, 9, 0);
            Assert.AreEqual(0, pwm.DutyCycleB);
            Assert.False(pwm.OutputB.IsSet);
            pwm.WriteWord(0x14, 10);
            Advance(10);
            Assert.AreEqual(1, pwm.DutyCycleB);
            Assert.True(pwm.OutputB.IsSet);
            pwm.WriteWord(0x14, 9);
            pwm.WriteWord(0x18, 0x102 | 8); // PRD SET outranks CBU CLEAR in up-count.
            Advance(10);
            Assert.AreEqual(1, pwm.DutyCycleB);
        }

        [Test]
        public void ShouldLoadCompareShadowOnlyAtZero()
        {
            Configure(0, 9, 5);
            Advance(2);
            pwm.WriteWord(0xE, 0x10); // B shadow, zero load.
            pwm.WriteWord(0x14, 8);
            Assert.AreEqual(0.5, pwm.DutyCycleB, 1e-10);
            Assert.AreEqual(0x200, pwm.ReadWord(0xE) & 0x200);
            Advance(8);
            Assert.AreEqual(0.8, pwm.DutyCycleB, 1e-10);
            Assert.AreEqual(0, pwm.ReadWord(0xE) & 0x200);
        }

        [Test]
        public void ShouldGenerateSymmetricUpDownAndDownCountWaveforms()
        {
            Configure(2, 10, 3);
            pwm.WriteWord(0x18, 0x100 | 0x800); // CBU CLEAR, CBD SET.
            Assert.AreEqual(0.3, pwm.DutyCycleB, 1e-10);
            Advance(11);
            Assert.AreEqual(9, pwm.ReadWord(8));
            Assert.AreEqual(0, pwm.ReadWord(2) & 1);
            pwm.Reset();
            pwm.WriteWord(0xA, 9);
            pwm.WriteWord(0xE, 0x50);
            pwm.WriteWord(0x14, 5);
            pwm.WriteWord(0x18, 0x404); // PRD CLEAR, CBD CLEAR? use explicit below.
            pwm.WriteWord(0x18, 0x408); // PRD SET, CBD CLEAR.
            pwm.WriteWord(0, 1);
            Assert.AreEqual(0.4, pwm.DutyCycleB, 1e-10);
            Advance(1);
            Assert.AreEqual(9, pwm.ReadWord(8));
            Assert.True(pwm.OutputB.IsSet);
        }

        [Test]
        public void ShouldPrescaleAndLatchEventIrqThroughAintc65()
        {
            var aintc = new TI_DA8xx_AINTC();
            pwm.IRQ.Connect(aintc, 65);
            aintc.WriteDoubleWord(0x440, 1u << 8);
            aintc.WriteDoubleWord(0x28, 65);
            aintc.WriteDoubleWord(0x34, 1);
            aintc.WriteDoubleWord(0x10, 1);
            Configure(0, 9, 5);
            pwm.WriteWord(0x32, 0x9); // zero interrupt.
            pwm.WriteWord(0x34, 2);   // every second zero.
            Advance(10);
            Assert.False(pwm.IRQ.IsSet);
            Advance(10);
            Assert.True(pwm.IRQ.IsSet);
            Assert.True(aintc.IRQ.IsSet);
            Assert.AreEqual(65u, aintc.ReadDoubleWord(0x904));
            pwm.WriteWord(0x38, 1);
            Assert.False(pwm.IRQ.IsSet);
            aintc.WriteDoubleWord(0x24, 65);
            Assert.False(aintc.IRQ.IsSet);
        }

        [Test]
        public void ShouldApplyTripSafetyAndImmediateContinuousSoftwareForce()
        {
            Configure(0, 9, 5);
            pwm.WriteWord(0x28, 0x5); // force both channels low on trip.
            pwm.WriteWord(0x2A, 4);
            pwm.WriteWord(0x30, 4);
            Assert.False(pwm.OutputB.IsSet);
            Assert.AreEqual(0, pwm.DutyCycleB);
            Assert.True(pwm.TripIRQ.IsSet);
            Assert.AreEqual(5, pwm.ReadWord(0x2C));
            pwm.WriteWord(0x2E, 5);
            Assert.False(pwm.TripIRQ.IsSet);
            pwm.WriteWord(0x1A, 0xC0);
            pwm.WriteWord(0x1C, 8); // B continuous high.
            Assert.AreEqual(1, pwm.DutyCycleB);
            Assert.True(pwm.OutputB.IsSet);
        }

        [Test]
        public void ShouldRejectPathologicalFrequencyAndUnsupportedExtensionsExplicitly()
        {
            var fast = new TI_DA8xx_EHRPWM(machine, 100000000, 1000);
            fast.WriteWord(0, 0);
            fast.WriteWord(0x32, 9);
            fast.WriteWord(0x34, 1);
            Advance(10);
            Assert.False(fast.Operational);
            Assert.That(fast.UnsupportedConfiguration, Does.Contain("rate"));
            Assert.AreEqual(0, fast.DutyCycleA);
            Assert.AreEqual(0, fast.ReadWord(0x36));
            Assert.False(fast.IRQ.IsSet);
            Configure(0, 9, 5);
            pwm.WriteWord(0x1E, 1);
            Assert.False(pwm.Operational);
            Assert.That(pwm.UnsupportedConfiguration, Does.Contain("deadband"));
            Assert.AreEqual(0, pwm.DutyCycleB);
        }

        [Test]
        public void ShouldEmitFiniteMotorQuadratureThroughEv3PhysicalGpioAndAintc47()
        {
            Configure(0, 9, 5);
            var motor = new PWMDrivenMotor(machine, pwm, channel: 1, maximumEdgesPerSecond: 100, updateFrequency: 1000);
            var gpio = new TI_DA8xx_GPIO(machine);
            var aintc = new TI_DA8xx_AINTC();
            motor.TachoA.Connect(gpio, 91); motor.TachoB.Connect(gpio, 4);
            gpio.Connections[63].Connect(motor, 0); gpio.Connections[54].Connect(motor, 1);
            gpio.Bank5IRQ.Connect(aintc, 47);
            aintc.WriteDoubleWord(0x42C, 1u << 24);
            aintc.WriteDoubleWord(0x28, 47); aintc.WriteDoubleWord(0x34, 1); aintc.WriteDoubleWord(0x10, 1);
            gpio.WriteDoubleWord(0x74, 1u << 27); gpio.WriteDoubleWord(8, 1u << 5);
            gpio.WriteDoubleWord(0x38, ~(1u << 31 | 1u << 22));
            gpio.WriteDoubleWord(0x40, 1u << 31);
            Assert.AreEqual("Forward", motor.State);
            Advance(100);
            Assert.AreEqual(5, motor.TachometerCount);
            Assert.AreEqual(5, motor.EmittedEdges);
            Assert.True(aintc.IRQ.IsSet);
            Assert.AreEqual(47u, aintc.ReadDoubleWord(0x904));
            gpio.WriteDoubleWord(0x44, 1u << 31); gpio.WriteDoubleWord(0x40, 1u << 22);
            Assert.AreEqual("Reverse", motor.State);
            Advance(100);
            Assert.AreEqual(0, motor.TachometerCount);
            Assert.AreEqual(10, motor.EmittedEdges);
            gpio.WriteDoubleWord(0x40, 1u << 31);
            Assert.AreEqual("Brake", motor.State);
            Advance(100);
            Assert.AreEqual(10, motor.EmittedEdges);
            gpio.WriteDoubleWord(0x44, 1u << 31 | 1u << 22);
            Assert.AreEqual("Coast", motor.State);
            Advance(100);
            Assert.AreEqual(10, motor.EmittedEdges);
        }

        [Test]
        public void ShouldDriveAllFourEv3MotorSourcesThroughTheirPhysicalEncoderPins()
        {
            Configure(0, 9, 5);
            pwm.WriteWord(0x12, 2); pwm.WriteWord(0x16, 0x12);
            var ecap0 = new TI_DA8xx_ECAP(machine, 1000);
            var ecap1 = new TI_DA8xx_ECAP(machine, 1000);
            ecap0.WriteDoubleWord(8, 9); ecap0.WriteDoubleWord(0xC, 8); ecap0.WriteWord(0x2A, 0x210);
            ecap1.WriteDoubleWord(8, 9); ecap1.WriteDoubleWord(0xC, 10); ecap1.WriteWord(0x2A, 0x210);
            var sources = new IPWMDutyCycleSource[] { pwm, pwm, ecap0, ecap1 };
            var channels = new[] { 1, 0, 0, 0 };
            var drive0 = new[] { 63, 33, 104, 83 };
            var drive1 = new[] { 54, 3, 89, 90 };
            var encoder0 = new[] { 91, 88, 93, 105 };
            var encoder1 = new[] { 4, 41, 62, 40 };
            var gpio = new TI_DA8xx_GPIO(machine);
            var motors = new PWMDrivenMotor[4];
            for(var port = 0; port < motors.Length; port++)
            {
                motors[port] = new PWMDrivenMotor(machine, sources[port], channels[port], maximumEdgesPerSecond: 100);
                motors[port].TachoA.Connect(gpio, encoder0[port]); motors[port].TachoB.Connect(gpio, encoder1[port]);
                gpio.Connections[drive0[port]].Connect(motors[port], 0); gpio.Connections[drive1[port]].Connect(motors[port], 1);
                foreach(var pin in new[] { drive0[port], drive1[port] })
                {
                    var dir = 0x10 + (pin / 32) * 0x28;
                    gpio.WriteDoubleWord(dir, gpio.ReadDoubleWord(dir) & ~(1u << (pin % 32)));
                }
                gpio.WriteDoubleWord(0x18 + (drive0[port] / 32) * 0x28, 1u << (drive0[port] % 32));
            }
            Advance(100);
            var expected = new long[] { 5, 2, 8, 10 };
            for(var port = 0; port < motors.Length; port++)
            {
                Assert.AreEqual(expected[port], motors[port].TachometerCount, "Motor port " + port);
                Assert.AreEqual((ulong)expected[port], motors[port].EmittedEdges);
                foreach(var pin in new[] { encoder0[port], encoder1[port] })
                {
                    var bit = 1u << (pin % 32);
                    Assert.AreEqual(gpio.Connections[pin].IsSet ? bit : 0u,
                        gpio.ReadDoubleWord(0x20 + (pin / 32) * 0x28) & bit);
                }
            }
            Assert.True(gpio.Connections[91].IsSet);
            Assert.True(gpio.Connections[88].IsSet); Assert.True(gpio.Connections[41].IsSet);
            Assert.False(gpio.Connections[93].IsSet); Assert.False(gpio.Connections[62].IsSet);
            Assert.True(gpio.Connections[105].IsSet); Assert.True(gpio.Connections[40].IsSet);
        }

        [Test]
        public void ShouldScaleMotorDutyAndBoundEmittedEdgesAtMaximumRate()
        {
            Configure(0, 9, 2);
            var motor = new PWMDrivenMotor(machine, pwm, channel: 1, maximumEdgesPerSecond: 100);
            motor.OnGPIO(0, true);
            Advance(100);
            Assert.AreEqual(2, motor.TachometerCount);
            pwm.WriteWord(0x14, 10);
            Advance(100);
            Assert.AreEqual(12, motor.TachometerCount);
            pwm.WriteWord(0x14, 0);
            Advance(100);
            Assert.AreEqual(12, motor.TachometerCount);
        }

        private void Configure(ushort mode, ushort period, ushort compare)
        {
            pwm.WriteWord(0, 3 | 8);
            pwm.WriteWord(0xA, period); pwm.WriteWord(0xE, 0x50);
            pwm.WriteWord(0x14, compare); pwm.WriteWord(0x18, 0x102);
            pwm.WriteWord(0, (ushort)(mode | 8));
        }
        private void Advance(ulong milliseconds) => ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMilliseconds(milliseconds), true);
        private Machine machine;
        private TI_DA8xx_EHRPWM pwm;
    }
}
