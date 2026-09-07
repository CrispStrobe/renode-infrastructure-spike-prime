// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Peripherals.UART;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class SpikeFaultSoakTests
    {
        [Test]
        public void ShouldRepeatLpf2FaultResetAndReconnectWithinFixedCeilings()
        {
            var random = new Random(0x5350494B);
            for(var cycle = 0; cycle < CycleCount; ++cycle)
            {
                var output = new List<byte>();
                var port = new LegoLpf2Port("motor", "PC1", "PC0");
                port.CharReceived += output.Add;
                port.AdvanceEmulatedTime(LegoLpf2Port.AttachmentSettleMicroseconds);
                port.WriteChar(0x04);
                output.Clear();

                // A complete but corrupt frame must be rejected without retaining input.
                port.WriteChar(0xc0);
                port.WriteChar((byte)random.Next(256));
                port.WriteChar(0x00);
                Assert.AreEqual(1, port.InvalidFrames);
                Assert.LessOrEqual(port.PendingTransmitBytes, LegoLpf2Port.MaximumTransmitQueueLength);

                // A truncated maximum frame must not grow, and detach must discard it.
                port.WriteChar(0xf8);
                for(var i = 0; i < 7; ++i)
                {
                    port.WriteChar((byte)random.Next(256));
                }
                port.Detach();
                Assert.AreEqual(Lpf2PortState.Detached, port.State);
                Assert.AreEqual(0, port.PendingTransmitBytes);

                port.Attach("motor");
                port.AdvanceEmulatedTime(LegoLpf2Port.AttachmentSettleMicroseconds);
                port.WriteChar(0x04);
                var motor = (Lpf2MediumMotor)port.Device;
                motor.AcceptOutput(0, new byte[] { 100 });
                motor.SetLoad(100);
                port.AdvanceEmulatedTime(ReportBudgetMicroseconds);
                Assert.IsTrue(motor.Stalled);
                Assert.AreEqual(0, motor.SpeedPercent);

                port.Reset();
                Assert.AreEqual(Lpf2PortState.Attached, port.State);
                Assert.AreEqual(0, port.InvalidFrames);
                Assert.AreEqual(0, port.PendingTransmitBytes);
            }
        }

        [Test]
        public void ShouldBoundAnUnobservedLpf2QueueAcrossLargeEmulatedTimeJumps()
        {
            var port = new LegoLpf2Port("ultrasonic");
            port.StartNegotiation();
            port.WriteChar(0x04);
            for(var cycle = 0; cycle < CycleCount; ++cycle)
            {
                port.AdvanceEmulatedTime(LargeJumpMicroseconds);
                Assert.AreEqual(LegoLpf2Port.MaximumTransmitQueueLength, port.PendingTransmitBytes);
                Assert.Greater(port.DroppedTransmitBytes, 0);
            }
            Assert.AreEqual((ulong)CycleCount * LargeJumpMicroseconds, port.EmulatedTimeMicroseconds);
            Assert.Greater(port.CoalescedReports, 0);
        }

        [Test]
        public void ShouldRepeatLowBatteryAndChargerTransitionsAfterReset()
        {
            var power = new BrickPowerController();
            for(var cycle = 0; cycle < CycleCount; ++cycle)
            {
                power.SetBatteryMillivolts(5900);
                Assert.IsTrue(power.BatteryLow);
                Assert.IsFalse(power.Connections[BrickPowerController.PowerGoodOutput].IsSet);
                power.SetChargerConnected(true);
                power.OnGPIO(BrickPowerController.ChargerModeInput, true);
                Assert.AreEqual(BrickPowerController.ChargeStates.Charging, power.ChargeState);
                power.SetChargeComplete(true);
                Assert.AreEqual(BrickPowerController.ChargeStates.Complete, power.ChargeState);
                power.Reset();
                Assert.AreEqual(BrickPowerController.ChargeStates.Suspended, power.ChargeState);
                Assert.IsFalse(power.ShutdownRequested);
                power.SetChargerConnected(false);
                power.SetBatteryMillivolts(7400);
            }
        }

        [Test]
        public void ShouldKeepRejectedFlashWritesUnchangedAcrossResetCycles()
        {
            using(var machine = new Machine())
            using(var memory = new MappedMemory(machine, FlashSize))
            {
                var flash = new GenericSpiFlash(memory, 0xEF, 0x40, 0x19,
                    writeStatusCanSetWriteEnable: false, sectorSizeKB: 64,
                    secondaryStatusRegisterReadCommand: 0x35);
                for(var cycle = 0; cycle < CycleCount; ++cycle)
                {
                    memory.WriteByte(TestAddress, 0xA5);
                    flash.Transmit(0x12);
                    foreach(var value in AddressBytes(TestAddress))
                    {
                        flash.Transmit(value);
                    }
                    flash.Transmit(0x00);
                    flash.FinishTransmission();
                    Assert.AreEqual(0xA5, memory.ReadByte(TestAddress));
                    flash.Reset();
                }
            }
        }

        private static byte[] AddressBytes(long address)
        {
            return new[] { (byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address };
        }

        private const int CycleCount = 256;
        private const ulong ReportBudgetMicroseconds = 100000;
        private const ulong LargeJumpMicroseconds = 1000000000;
        private const int FlashSize = 32 * 1024 * 1024;
        private const int TestAddress = 0x01001010;
    }
}
