// Copyright (c) 2026 CrispStrobe
// This file is licensed under the MIT License. See 'licenses/MIT.txt'.
using System.Collections.Generic;
using System.Linq;
using Antmicro.Renode.Peripherals.UART;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class LegoLpf2PortTests
    {
        [SetUp]
        public void SetUp()
        {
            port = new LegoLpf2Port();
            output = new List<byte>();
            port.CharReceived += output.Add;
        }

        [Test]
        public void ShouldNegotiateAttachedSensorAndStartStreamingOnAck()
        {
            port.Attach("ultrasonic");
            port.StartNegotiation();
            Assert.AreEqual(Lpf2PortState.WaitingForAck, port.State);
            CollectionAssert.AreEqual(new byte[]
            {
                0x00,
                0x40, 0x3e, 0x81,
                0x49, 0x00, 0x00, 0xb6,
                0x52, 0x00, 0xc2, 0x01, 0x00, 0x6e,
                0x98, 0x00, 0x44, 0x49, 0x53, 0x54, 0x4c, 0x00, 0x00, 0x00, 0x21,
                0xa0, 0x80, 0x01, 0x01, 0x03, 0x00, 0xdc,
                0x04,
            }, output);
            output.Clear();
            port.WriteChar(0x04);
            Assert.AreEqual(Lpf2PortState.Streaming, port.State);
            CollectionAssert.AreEqual(DataFrame(0, 0xe8, 0x03), output);
        }

        [Test]
        public void ShouldExposeDeterministicSensorInputAndRetransmitOnNack()
        {
            var sensor = new Lpf2UltrasonicSensor();
            sensor.SetDistance(321);
            port.AttachDevice(sensor);
            port.StartNegotiation();
            port.WriteChar(0x04);
            output.Clear();
            port.WriteChar(0x02);
            CollectionAssert.AreEqual(DataFrame(0, 0x41, 0x01), output);
        }

        [Test]
        public void ShouldSelectMotorEncoderModeAndApplyOutputCommand()
        {
            var motor = new Lpf2MediumMotor();
            port.AttachDevice(motor);
            port.StartNegotiation();
            port.WriteChar(0x04);
            output.Clear();
            SendFrame(port, 0x43, 0x02);
            output.Clear();
            SendFrame(port, 0xc0, 50);
            port.Advance(1000);
            Assert.AreEqual(50, motor.Power);
            Assert.AreEqual(50, motor.SpeedPercent);
            Assert.AreEqual(500, motor.AngularVelocityDegreesPerSecond);
            Assert.AreEqual(500, motor.EncoderDegrees);
            CollectionAssert.AreEqual(DataFrame(2, 0xf4, 0x01, 0x00, 0x00), output.Skip(output.Count - 6));
        }

        [Test]
        public void ShouldModelLoadAndStallWithoutChangingEncoder()
        {
            var motor = new Lpf2MediumMotor();
            motor.AcceptOutput(0, new byte[] { 80 });
            motor.SetLoad(50);
            motor.Advance(1000);
            Assert.AreEqual(400, motor.EncoderDegrees);
            motor.SetLoad(90);
            motor.Advance(1000);
            Assert.IsTrue(motor.Stalled);
            Assert.AreEqual(0, motor.SpeedPercent);
            Assert.AreEqual(0, motor.AngularVelocityDegreesPerSecond);
            Assert.AreEqual(400, motor.EncoderDegrees);
        }

        [Test]
        public void ShouldModelTechnicLargeMotorContractAndRecoverAfterFaults()
        {
            var motor = new Lpf2TechnicLargeMotor();
            port.AttachDevice(motor);
            port.StartNegotiation();
            Assert.AreEqual(0x00, output[0]);
            CollectionAssert.AreEqual(motor.DiscoveryBytes, output.Skip(1));
            AssertSequenceExists(output, new byte[] { 0x40, 0x2e, 0x91 });
            AssertSequenceExists(output, new byte[] { 0x49, 0x05, 0x03, 0xb0 });
            AssertSequenceExists(output, new byte[] { 0x95, 0x04, 0x4d, 0x49, 0x4e, 0x00, 0x24 });
            AssertSequenceExists(output, new byte[] { 0x93, 0x04, 0x44, 0x45, 0x47, 0x00, 0x2e });

            port.WriteChar(0x04);
            SendFrame(port, 0xc0, 80);
            motor.SetLoad(50);
            motor.Advance(1000);
            Assert.AreEqual(420, motor.PositionDegrees);
            motor.SetLoad(90);
            motor.Advance(1000);
            Assert.IsTrue(motor.Stalled);
            Assert.AreEqual(420, motor.PositionDegrees);

            port.WriteChar(0xc0);
            port.WriteChar(10);
            port.WriteChar(0); // corrupt checksum
            Assert.AreEqual(1, port.InvalidFrames);
            port.Detach();
            port.Attach("large-motor");
            Assert.AreEqual(Lpf2PortState.Attached, port.State);
            Assert.IsInstanceOf<Lpf2TechnicLargeMotor>(port.Device);
        }

        [Test]
        public void ShouldHandleWriteCommandAndRejectInvalidMode()
        {
            var motor = new Lpf2MediumMotor();
            port.AttachDevice(motor);
            SendFrame(port, 0x44, unchecked((byte)-25));
            Assert.AreEqual(-25, motor.Power);

            output.Clear();
            SendFrame(port, 0x43, 7);
            Assert.AreEqual(1, port.InvalidFrames);
            CollectionAssert.AreEqual(new byte[] { 0x02 }, output);

            output.Clear();
            SendFrame(port, 0xc7, 50);
            Assert.AreEqual(2, port.InvalidFrames);
            CollectionAssert.AreEqual(new byte[] { 0x02 }, output);
        }

        [Test]
        public void ShouldBoundACompleteMaximumFrameAndResynchronize()
        {
            port.Attach("motor");
            port.WriteChar(0xf8);
            Assert.AreEqual(1, port.InvalidFrames);
            Assert.AreEqual(0x02, output.Last());

            SendFrame(port, 0x43, 2);
            Assert.AreEqual(2, port.SelectedMode);
        }

        [Test]
        public void ShouldRejectBadChecksumsAndDetachCleanly()
        {
            port.Attach("motor");
            port.WriteChar(0xc0);
            port.WriteChar(70);
            port.WriteChar(0);
            Assert.AreEqual(1, port.InvalidFrames);
            Assert.AreEqual(0x02, output.Last());
            port.Detach();
            Assert.AreEqual(Lpf2PortState.Detached, port.State);
            Assert.IsNull(port.Device);
        }

        [Test]
        public void ShouldSettleAttachTimeoutAndReconnectOnEmulatedTimeOnly()
        {
            var timedPort = new LegoLpf2Port("ultrasonic", "PC1", "PC0");
            var bytes = new List<byte>();
            timedPort.CharReceived += bytes.Add;

            Assert.AreEqual(1, timedPort.TopologyGeneration);
            Assert.IsTrue(timedPort.Gpio1Attached);
            Assert.IsTrue(timedPort.Gpio2Attached);
            Assert.AreEqual("PC1", timedPort.Gpio1Pin);
            Assert.AreEqual("PC0", timedPort.Gpio2Pin);
            timedPort.AdvanceEmulatedTime(LegoLpf2Port.AttachmentSettleMicroseconds - 1);
            Assert.AreEqual(Lpf2PortState.Attached, timedPort.State);
            Assert.IsEmpty(bytes);

            timedPort.AdvanceEmulatedTime(1);
            Assert.AreEqual(Lpf2PortState.WaitingForAck, timedPort.State);
            CollectionAssert.AreEqual(UltrasonicDiscovery, bytes);
            timedPort.AdvanceEmulatedTime(LegoLpf2Port.NegotiationTimeoutMicroseconds);
            Assert.AreEqual(Lpf2PortState.TimedOut, timedPort.State);
            Assert.AreEqual(1, timedPort.Timeouts);

            timedPort.Detach();
            Assert.AreEqual(2, timedPort.TopologyGeneration);
            Assert.IsFalse(timedPort.Gpio1Attached);
            timedPort.Attach("motor");
            Assert.AreEqual(3, timedPort.TopologyGeneration);
            timedPort.AdvanceEmulatedTime(LegoLpf2Port.AttachmentSettleMicroseconds);
            Assert.AreEqual(Lpf2PortState.WaitingForAck, timedPort.State);
        }

        [Test]
        public void ShouldReportAtExactCadenceAndPreserveFractionalAdvance()
        {
            var motor = new Lpf2MediumMotor();
            port.AttachDevice(motor);
            port.StartNegotiation();
            port.WriteChar(0x04);
            output.Clear();
            SendFrame(port, 0xc0, 50);

            port.AdvanceEmulatedTime(99999);
            Assert.IsEmpty(output);
            port.AdvanceEmulatedTime(1);
            CollectionAssert.AreEqual(DataFrame(0, 50), output);
            Assert.AreEqual(50, motor.EncoderDegrees);
            output.Clear();
            port.AdvanceEmulatedTime(250000);
            Assert.AreEqual(2 * DataFrame(0, 50).Length, output.Count);
            Assert.AreEqual(175, motor.EncoderDegrees);
        }

        [Test]
        public void ShouldBoundUndeliveredOutputAndRecoverAfterTruncation()
        {
            var quietPort = new LegoLpf2Port("ultrasonic");
            quietPort.StartNegotiation();
            quietPort.WriteChar(0x04);
            quietPort.AdvanceEmulatedTime(100000000);
            Assert.AreEqual(LegoLpf2Port.MaximumTransmitQueueLength, quietPort.PendingTransmitBytes);
            Assert.Greater(quietPort.DroppedTransmitBytes, 0);

            port.Attach("motor");
            port.WriteChar(0xd8); // Declares an eight-byte payload, then truncates.
            port.AdvanceEmulatedTime(LegoLpf2Port.AttachmentSettleMicroseconds + LegoLpf2Port.NegotiationTimeoutMicroseconds);
            Assert.AreEqual(Lpf2PortState.TimedOut, port.State);
            port.Detach();
            port.Attach("motor");
            SendFrame(port, 0x43, 2);
            Assert.AreEqual(2, port.SelectedMode);
        }

        [Test]
        public void ShouldRejectZeroCadenceAndBoundLargeTimeJumps()
        {
            Assert.Throws<System.ArgumentException>(() => port.AttachDevice(new ZeroCadenceDevice()));

            var quietPort = new LegoLpf2Port("ultrasonic");
            quietPort.StartNegotiation();
            quietPort.WriteChar(0x04);
            quietPort.AdvanceEmulatedTime(1000000000000);
            Assert.AreEqual(9998976, quietPort.CoalescedReports);
            Assert.LessOrEqual(quietPort.PendingTransmitBytes, LegoLpf2Port.MaximumTransmitQueueLength);
        }

        private static void SendFrame(LegoLpf2Port target, byte header, params byte[] payload)
        {
            var checksum = (byte)(0xff ^ header);
            target.WriteChar(header);
            foreach(var value in payload)
            {
                checksum ^= value;
                target.WriteChar(value);
            }
            target.WriteChar(checksum);
        }

        private static byte[] DataFrame(byte mode, params byte[] payload)
        {
            var header = (byte)(0xc0 | ((byte)System.Math.Log(payload.Length, 2) << 3) | mode);
            var result = new List<byte> { header };
            result.AddRange(payload);
            result.Add(result.Aggregate((byte)0xff, (checksum, value) => (byte)(checksum ^ value)));
            return result.ToArray();
        }

        private static byte[] Frame(byte header, params byte[] payload)
        {
            var result = new List<byte> { header };
            result.AddRange(payload);
            result.Add(result.Aggregate((byte)0xff, (checksum, value) => (byte)(checksum ^ value)));
            return result.ToArray();
        }

        private static void AssertSequenceExists(IReadOnlyList<byte> haystack, IReadOnlyList<byte> needle)
        {
            Assert.IsTrue(Enumerable.Range(0, haystack.Count - needle.Count + 1)
                .Any(offset => needle.SequenceEqual(haystack.Skip(offset).Take(needle.Count))));
        }

        private LegoLpf2Port port;
        private List<byte> output;

        private static readonly byte[] UltrasonicDiscovery =
        {
            0x00,
            0x40, 0x3e, 0x81,
            0x49, 0x00, 0x00, 0xb6,
            0x52, 0x00, 0xc2, 0x01, 0x00, 0x6e,
            0x98, 0x00, 0x44, 0x49, 0x53, 0x54, 0x4c, 0x00, 0x00, 0x00, 0x21,
            0xa0, 0x80, 0x01, 0x01, 0x03, 0x00, 0xdc,
            0x04,
        };

        private sealed class ZeroCadenceDevice : ILpf2Device
        {
            public byte TypeId => 1;
            public string Name => "Invalid";
            public IReadOnlyList<Lpf2Mode> Modes => new[] { new Lpf2Mode(0, "BAD", 1, Lpf2DataType.Int8) };
            public uint ReportIntervalMicroseconds => 0;
            public byte[] ReadMode(byte mode) => new byte[] { 0 };
            public void AcceptOutput(byte mode, byte[] payload) { }
            public void Advance(uint milliseconds) { }
            public void Reset() { }
        }
    }
}
