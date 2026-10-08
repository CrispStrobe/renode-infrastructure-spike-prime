// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using System.Collections.Generic;
using System.Linq;
using Antmicro.Renode.Peripherals.UART;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class LegoLpf2DataBudgetTests
    {
        [SetUp]
        public void SetUp()
        {
            port = new LegoLpf2Port();
            output = new List<byte>();
            port.CharReceived += output.Add;
        }

        [Test]
        public void ShouldLeaveDefaultReportsUnlimited()
        {
            Stream();
            Assert.IsFalse(port.DataReportsLimited);
            port.WriteChar(0x02);
            port.WriteChar(0x02);
            CollectionAssert.AreEqual(Report.Concat(Report), output);
            Assert.AreEqual(0, port.DataReportsRemaining);
        }

        [Test]
        public void ShouldPreserveDiscoveryWhileSuppressingData()
        {
            var baseline = new LegoLpf2Port("ultrasonic");
            var discovery = new List<byte>();
            baseline.CharReceived += discovery.Add;
            baseline.StartNegotiation();
            port.SetDataReportBudget(0);
            port.Attach("ultrasonic");
            port.StartNegotiation();
            CollectionAssert.AreEqual(discovery, output);
            output.Clear();
            port.WriteChar(0x04);
            Assert.AreEqual(Lpf2PortState.Streaming, port.State);
            Assert.IsEmpty(output);
            Assert.IsTrue(port.DataReportsLimited);
        }

        [TestCase("ack")]
        [TestCase("nack")]
        [TestCase("mode")]
        [TestCase("periodic")]
        public void ShouldEmitExactlyOneBudgetedDataFrame(string producer)
        {
            Stream();
            port.SetDataReportBudget(1);
            Produce(producer);
            Produce(producer);
            CollectionAssert.AreEqual(Report, output);
            Assert.AreEqual(0, port.DataReportsRemaining);
            Assert.AreEqual(0, port.DroppedTransmitBytes);
        }

        [Test]
        public void ShouldKeepDeviceMotionAndClockRunningWithoutData()
        {
            var motor = new Lpf2MediumMotor();
            port.AttachDevice(motor);
            port.StartNegotiation();
            port.WriteChar(0x04);
            SendFrame(0xc0, 50);
            output.Clear();
            port.SetDataReportBudget(0);
            var before = port.EmulatedTimeMicroseconds;
            port.AdvanceEmulatedTime(1000000);
            Assert.IsEmpty(output);
            Assert.AreEqual(before + 1000000, port.EmulatedTimeMicroseconds);
            Assert.AreEqual(500, motor.EncoderDegrees);
            port.ResumeDataReports();
            port.WriteChar(0x02);
            CollectionAssert.AreEqual(new byte[] {0xc0, 50, 0x0d}, output);
        }

        [Test]
        public void ShouldShareBudgetAcrossEveryDataProducer()
        {
            Stream();
            port.SetDataReportBudget(2);
            Produce("nack");
            Produce("periodic");
            Produce("mode");
            Produce("ack");
            CollectionAssert.AreEqual(Report.Concat(Report), output);
            Assert.AreEqual(0, port.DataReportsRemaining);
        }

        [Test]
        public void ShouldReserveBudgetBeforeSynchronousSubscriberReentry()
        {
            Stream();
            port.SetDataReportBudget(1);
            var reentered = false;
            port.CharReceived += value =>
            {
                if(!reentered)
                {
                    reentered = true;
                    port.WriteChar(0x02);
                }
            };
            port.WriteChar(0x02);
            Assert.IsTrue(reentered);
            CollectionAssert.AreEqual(Report, output);
            Assert.AreEqual(0, port.DataReportsRemaining);
        }

        [Test]
        public void ShouldPreserveBudgetAcrossAttachmentAndNegotiation()
        {
            Stream();
            port.SetDataReportBudget(1);
            port.Detach();
            port.Attach("ultrasonic");
            port.StartNegotiation();
            Assert.IsTrue(port.DataReportsLimited);
            Assert.AreEqual(1, port.DataReportsRemaining);
            output.Clear();
            port.WriteChar(0x04);
            port.WriteChar(0x02);
            CollectionAssert.AreEqual(Report, output);
        }

        [Test]
        public void ShouldRestoreUnlimitedReportsOnlyOnExplicitResumeOrModelReset()
        {
            Stream();
            port.SetDataReportBudget(0);
            port.ResumeDataReports();
            Assert.IsFalse(port.DataReportsLimited);
            port.WriteChar(0x02);
            CollectionAssert.AreEqual(Report, output);
            port.SetDataReportBudget(0);
            port.Reset();
            Assert.IsFalse(port.DataReportsLimited);
            port.StartNegotiation();
            output.Clear();
            port.WriteChar(0x04);
            CollectionAssert.AreEqual(Report, output);
        }

        [Test]
        public void ShouldAllowMaximumBudgetWithoutCounterWrap()
        {
            Stream();
            port.SetDataReportBudget(uint.MaxValue);
            port.WriteChar(0x02);
            Assert.AreEqual(uint.MaxValue - 1, port.DataReportsRemaining);
            port.SetDataReportBudget(0);
            output.Clear();
            port.WriteChar(0x02);
            Assert.IsEmpty(output);
            Assert.AreEqual(0, port.DataReportsRemaining);
        }

        [Test]
        public void ShouldNotDiscardBytesAlreadyQueuedBeforeBudgetChange()
        {
            port.Attach("ultrasonic");
            port.StartNegotiation();
            port.WriteChar(0x04);
            var expected = output.ToArray();
            var buffered = new LegoLpf2Port("ultrasonic");
            buffered.StartNegotiation();
            buffered.WriteChar(0x04);
            Assert.AreEqual(expected.Length, buffered.PendingTransmitBytes);
            buffered.SetDataReportBudget(0);
            var delivered = new List<byte>();
            buffered.CharReceived += delivered.Add;
            CollectionAssert.AreEqual(expected, delivered);
            Assert.AreEqual(0, buffered.PendingTransmitBytes);
            buffered.WriteChar(0x02);
            CollectionAssert.AreEqual(expected, delivered);
        }

        private void Stream()
        {
            port.Attach("ultrasonic");
            port.StartNegotiation();
            port.WriteChar(0x04);
            output.Clear();
        }

        private void Produce(string producer)
        {
            switch(producer)
            {
                case "ack": port.WriteChar(0x04); break;
                case "nack": port.WriteChar(0x02); break;
                case "mode": SendFrame(0x43, 0); break;
                case "periodic": port.AdvanceEmulatedTime(100000); break;
                default: Assert.Fail("Unknown producer"); break;
            }
        }

        private void SendFrame(byte header, byte payload)
        {
            port.WriteChar(header);
            port.WriteChar(payload);
            port.WriteChar((byte)(0xff ^ header ^ payload));
        }

        private static readonly byte[] Report = {0xc8, 0xe8, 0x03, 0xdc};
        private LegoLpf2Port port;
        private List<byte> output;
    }
}
