// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Peripherals.UART;

namespace Antmicro.Renode.PeripheralsTests
{
    // Robot Lab lockstep (renode-infrastructure-spike-prime#37): external shaft,
    // per-tick duty integrator, and internal mode unchanged.
    [TestFixture]
    [NonParallelizable]
    public class LegoLpf2ExternalShaftTests
    {
        [Test]
        public void ShouldReportInjectedShaftInPosFramesAndExtrapolateOnlyFromTheAnchor()
        {
            using(var machine = new Machine())
            {
                var rig = new Rig(machine);
                var output = rig.Stream();
                rig.Port.EnableExternalShaft();
                var motor = (Lpf2ElectricalMotor)rig.Port.Device;
                Assert.IsTrue(motor.ExternalShaft);
                rig.Port.SetShaft(1000.0, 360.0);
                // A full forward drive must not move an external shaft.
                rig.Forward(1000);
                var reports = 0;
                for(var tick = 1; tick <= 10; tick++)
                {
                    var before = output.Count;
                    rig.Port.Tick();
                    Assert.AreEqual(1000.0 + 360.0 * tick / 1000.0, motor.PositionDegrees, 1e-9, "extrapolated shaft at tick " + tick);
                    Assert.AreEqual(360.0, motor.AngularVelocityDegreesPerSecond);
                    Assert.IsFalse(motor.Stalled);
                    var pos = PosReport(output, before);
                    if(pos.HasValue)
                    {
                        reports++;
                        Assert.AreEqual((int)Math.Round(1000.0 + 360.0 * tick / 1000.0), pos.Value, "POS report at tick " + tick);
                    }
                }
                Assert.AreEqual(1, reports, "one POS report per 10 ms");
                Assert.AreEqual(100, motor.Power, "the bridge drive is still sampled");

                // A new anchor replaces the extrapolation; zero speed holds the shaft.
                rig.Port.SetShaft(-250.5, 0.0);
                for(var tick = 1; tick <= 20; tick++)
                {
                    var before = output.Count;
                    rig.Port.Tick();
                    Assert.AreEqual(-250.5, motor.PositionDegrees, "held shaft at tick " + tick);
                    var pos = PosReport(output, before);
                    if(pos.HasValue)
                    {
                        Assert.AreEqual((int)Math.Round(-250.5), pos.Value);
                    }
                }
            }
        }

        [Test]
        public void ShouldRefuseLoadAndShaftOutsideTheirMode()
        {
            var motor = new Lpf2ElectricalMotor();
            Assert.Throws<InvalidOperationException>(() => motor.SetShaft(1, 1));
            motor.SetLoad(100);
            motor.SetDrive(10000, false);
            Assert.IsTrue(motor.Stalled);
            motor.EnableExternalShaft();
            Assert.IsFalse(motor.Stalled, "an external shaft never stalls in the model");
            Assert.AreEqual(0, motor.LoadPercent);
            Assert.Throws<InvalidOperationException>(() => motor.SetLoad(100));
            Assert.Throws<ArgumentOutOfRangeException>(() => motor.SetShaft(double.NaN, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => motor.SetShaft(0, double.PositiveInfinity));
            motor.SetShaft(12.5, -90);
            motor.Advance(1000);
            Assert.AreEqual(-77.5, motor.PositionDegrees, 1e-12);
            motor.Reset();
            Assert.IsTrue(motor.ExternalShaft, "reset keeps the shaft source");
            motor.Advance(1000);
            Assert.AreEqual(0.0, motor.PositionDegrees);
        }

        [Test]
        public void ShouldKeepExternalShaftForMotorsAttachedLater()
        {
            using(var machine = new Machine())
            {
                var rig = new Rig(machine);
                rig.Port.EnableExternalShaft();
                rig.Port.Detach();
                Assert.Throws<InvalidOperationException>(() => rig.Port.SetShaft(1, 1));
                rig.Port.Attach("motor");
                Assert.IsTrue(((Lpf2ElectricalMotor)rig.Port.Device).ExternalShaft);
                Assert.IsTrue(rig.Port.ExternalShaft);
            }
        }

        [Test]
        public void ShouldAverageScriptedBridgeDutyPerTickAndReset()
        {
            using(var machine = new Machine())
            {
                var rig = new Rig(machine);
                rig.Port.EnableExternalShaft();
                rig.Port.SetShaft(0, 0);
                // ARR 999: a period of 1000 counts, so CCR 500 is drive 5000.
                foreach(var ccr in new uint[] { 0, 500, 1000, 500 })
                {
                    rig.Forward(ccr);
                    rig.Port.Tick();
                }
                var average = rig.Port.TakeDriveAverage();
                Assert.AreEqual(4, average.Ticks);
                Assert.AreEqual(5000.0, average.MeanDrive, 1.0);
                Assert.AreEqual(5000, average.LastDrive);
                Assert.AreEqual(0, average.BrakingTicks);
                Assert.AreEqual(0.0, ((Lpf2ElectricalMotor)rig.Port.Device).PositionDegrees, "duty never moves an external shaft");

                var empty = rig.Port.TakeDriveAverage();
                Assert.AreEqual(0, empty.Ticks);
                Assert.AreEqual(0.0, empty.MeanDrive);
                Assert.AreEqual(5000, empty.LastDrive, "last sample survives a reset");

                rig.Reverse(250); rig.Port.Tick();
                rig.Brake(); rig.Port.Tick(); rig.Port.Tick();
                rig.Coast(); rig.Port.Tick();
                rig.Reverse(1000); rig.Port.Tick();
                average = rig.Port.TakeDriveAverage();
                Assert.AreEqual(5, average.Ticks);
                Assert.AreEqual(2, average.BrakingTicks);
                Assert.AreEqual((-2500.0 - 10000.0) / 5, average.MeanDrive, 1e-9);
                Assert.AreEqual(-10000, average.LastDrive);
            }
        }

        [Test]
        public void ShouldKeepInternalMechanicsBitIdenticalWithAndWithoutDriveAveraging()
        {
            // The reference is the pre-lockstep integrator, copied verbatim.
            var reference = new ReferenceMotor();
            var traces = new List<string>[2];
            for(var run = 0; run < 2; run++)
            {
                using(var machine = new Machine())
                {
                    var rig = new Rig(machine);
                    var output = rig.Stream();
                    var motor = (Lpf2ElectricalMotor)rig.Port.Device;
                    var trace = traces[run] = new List<string>();
                    if(run == 0) reference = new ReferenceMotor();
                    for(var tick = 1; tick <= 1000; tick++)
                    {
                        var before = output.Count;
                        int drive; bool braking; byte load;
                        Script(rig, motor, tick, out drive, out braking, out load);
                        rig.Port.Tick();
                        if(run == 1 && tick % 5 == 0) rig.Port.TakeDriveAverage();
                        if(run == 0)
                        {
                            reference.SetDrive(drive, braking);
                            reference.SetLoad(load);
                            reference.Advance(1);
                            Assert.AreEqual(BitConverter.DoubleToInt64Bits(reference.Position),
                                BitConverter.DoubleToInt64Bits(motor.PositionDegrees), "position bits at tick " + tick);
                            Assert.AreEqual(BitConverter.DoubleToInt64Bits(reference.Speed),
                                BitConverter.DoubleToInt64Bits(motor.AngularVelocityDegreesPerSecond), "speed bits at tick " + tick);
                        }
                        var pos = PosReport(output, before);
                        trace.Add(tick + ":" + BitConverter.DoubleToInt64Bits(motor.PositionDegrees) + ":" + (pos.HasValue ? pos.Value.ToString() : "-"));
                    }
                }
            }
            CollectionAssert.AreEqual(traces[0], traces[1]);
            Assert.AreEqual(100, traces[0].FindAll(line => !line.EndsWith(":-")).Count, "a POS report every 10 ms for 1 s");
        }

        // 1 s scripted drive: ramps, reversal through brake, coast and a load.
        private static void Script(Rig rig, Lpf2ElectricalMotor motor, int tick, out int drive, out bool braking, out byte load)
        {
            braking = false;
            load = 0;
            if(tick <= 300) { var ccr = (uint)(tick * 3); rig.Forward(ccr); drive = (int)(ccr * 10); }
            else if(tick <= 350) { rig.Brake(); drive = 0; braking = true; }
            else if(tick <= 600) { rig.Reverse(700); drive = -7000; }
            else if(tick <= 700) { rig.Coast(); drive = 0; }
            else
            {
                rig.Forward(1000); drive = 10000;
                load = (byte)(tick < 900 ? 40 : 100);
                motor.SetLoad(load);
            }
        }

        private static int? PosReport(List<byte> output, int from)
        {
            // DATA mode 2, four bytes: header 0xd2, little-endian int32, checksum.
            for(var i = output.Count - 6; i >= from; i--)
            {
                if(output[i] == 0xd2)
                {
                    return BitConverter.ToInt32(new[] { output[i + 1], output[i + 2], output[i + 3], output[i + 4] }, 0);
                }
            }
            return null;
        }

        private sealed class ReferenceMotor
        {
            public void SetDrive(int value, bool braking) { duty = value; brake = braking; }
            public void SetLoad(byte value) { load = value; }
            public double Position => position;
            public double Speed => speed;
            private bool Stalled => duty != 0 && load == 100;
            public void Advance(uint milliseconds)
            {
                var seconds = milliseconds / 1000.0;
                var target = Stalled ? 0 : duty * 1110.0 / 10000 * (100 - load) / 100;
                var old = speed;
                var acceleration = duty != 0 ? 2000.0 : brake ? 4000.0 : 200.0;
                speed = Stalled ? 0 : Math.Max(old - acceleration * seconds, Math.Min(old + acceleration * seconds, target));
                if(!Stalled) position += (old + speed) * 0.5 * seconds;
            }
            private int duty, load;
            private bool brake;
            private double speed, position;
        }

        // One port with the board's TIM1 bridge contract on pins 9/11.
        private sealed class Rig
        {
            public Rig(Machine machine)
            {
                Gpio = new STM32_GPIOPort(machine);
                Timer = new STM32_Timer(machine, 96000000, 1000);
                Port = new LegoLpf2ElectricalPort(machine, Gpio, 0, Gpio, 1, Gpio, 2, Gpio, 3, "motor", Gpio, 9, 11, Timer);
                Timer.WriteDoubleWord(0x2c, 999);
                Timer.WriteDoubleWord(0x18, 0x6060);
                Timer.WriteDoubleWord(0x20, 0x33);
                Timer.WriteDoubleWord(0x44, 0x8000);
                Timer.WriteDoubleWord(0, 1);
                Gpio.WriteDoubleWord(0x24, (1u << 4) | (1u << 12));
            }

            // UART mode on TX, LPF2 handshake, then the POS mode (2).
            public List<byte> Stream()
            {
                var output = new List<byte>();
                Port.CharReceived += output.Add;
                Mode(2, 2);
                Port.StartNegotiation();
                Port.WriteChar(0x04);
                Assert.AreEqual(Lpf2PortState.Streaming, Port.State);
                var checksum = (byte)(0xff ^ 0x43 ^ 0x02);
                Port.WriteChar(0x43); Port.WriteChar(0x02); Port.WriteChar(checksum);
                output.Clear();
                return output;
            }

            public void Forward(uint ccr)
            {
                Timer.WriteDoubleWord(0x34, ccr);
                Mode(9, 2); Mode(11, 1); Gpio.WriteDoubleWord(0x14, 1u << 11);
            }

            public void Reverse(uint ccr)
            {
                Timer.WriteDoubleWord(0x38, ccr);
                Mode(9, 1); Mode(11, 2); Gpio.WriteDoubleWord(0x14, 1u << 9);
            }

            public void Brake()
            {
                Mode(9, 1); Mode(11, 1); Gpio.WriteDoubleWord(0x14, (1u << 9) | (1u << 11));
            }

            public void Coast()
            {
                Mode(9, 1); Mode(11, 1); Gpio.WriteDoubleWord(0x14, 0);
            }

            private void Mode(int pin, uint mode)
            {
                Gpio.WriteDoubleWord(0, (Gpio.ReadDoubleWord(0) & ~(3u << (pin * 2))) | (mode << (pin * 2)));
            }

            public readonly STM32_GPIOPort Gpio;
            public readonly STM32_Timer Timer;
            public readonly LegoLpf2ElectricalPort Port;
        }
    }
}
