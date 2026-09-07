//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Peripherals.Sensors;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class LSM6DS3TRCTests
    {
        [SetUp]
        public void SetUp()
        {
            device = new LSM6DS3TRC();
        }

        [Test]
        public void ShouldIdentifyAndCompleteSoftwareResetImmediately()
        {
            WriteRegister(0x10, 0xA5);
            WriteRegister(0x12, 0x01);

            Assert.AreEqual(0x6A, ReadRegister(0x0F));
            Assert.AreEqual(0, ReadRegister(0x10));
            Assert.AreEqual(0, ReadRegister(0x12));
        }

        [Test]
        public void ShouldHonorAutoIncrementForBurstReadsAndWrites()
        {
            WriteRegister(0x12, 0x44);
            device.Write(new byte[] { 0x10, 0x11, 0x22 });
            device.Write(new byte[] { 0x10 });

            CollectionAssert.AreEqual(new byte[] { 0x11, 0x22 }, device.Read(2));
            Assert.AreEqual(0x44, device.Control3);
        }

        [Test]
        public void ShouldInjectLittleEndianSamplesAndClearReadyFlagsAfterCompleteReads()
        {
            WriteRegister(0x12, 0x44);
            device.FeedSample(-2, 0x1234, -2, 3, 4, 5, -6);
            Assert.AreEqual(0x07, device.Status);

            device.Write(new byte[] { 0x20 });
            CollectionAssert.AreEqual(new byte[] { 0xFE, 0xFF }, device.Read(2));
            Assert.AreEqual(0x03, device.Status);

            device.Write(new byte[] { 0x22 });
            CollectionAssert.AreEqual(new byte[] { 0x34, 0x12, 0xFE, 0xFF, 0x03, 0x00 }, device.Read(6));
            Assert.AreEqual(0x01, device.Status);

            device.Write(new byte[] { 0x28 });
            CollectionAssert.AreEqual(new byte[] { 0x04, 0x00, 0x05, 0x00, 0xFA, 0xFF }, device.Read(6));
            Assert.AreEqual(0, device.Status);
        }

        [Test]
        public void ShouldKeepReadyFlagUntilLastAxisByteIsRead()
        {
            WriteRegister(0x12, 0x04);
            device.FeedAngularRateSample(1, 2, 3);
            device.Write(new byte[] { 0x22 });
            device.Read(5);
            Assert.AreEqual(0x02, device.Status);
            device.Read(1);
            Assert.AreEqual(0, device.Status);
        }

        [Test]
        public void ShouldExposeDefensiveRegisterSnapshot()
        {
            device.FeedAccelerationSample(1, 2, 3);
            var snapshot = device.RegisterSnapshot;
            snapshot[0x28] = 0xFF;

            Assert.AreEqual(0x01, device.RegisterSnapshot[0x28]);
            device.Reset();
            Assert.AreEqual(0, device.Status);
            Assert.AreEqual(0x6A, device.RegisterSnapshot[0x0F]);
        }

        [Test]
        public void ShouldAdvanceDeterministicSamplesIntoFifo()
        {
            WriteRegister(0x12, 0x04);
            device.SetNextSample(7, 1, 2, 3, 4, 5, 6);
            device.AdvanceSample();
            Assert.AreEqual(1, device.GeneratedSamples);
            Assert.AreEqual(12, device.FifoBytes);
            Assert.AreEqual(6, ReadRegister(0x3A));
            device.Write(new byte[] { 0x3E });
            CollectionAssert.AreEqual(new byte[] { 1, 0 }, device.Read(2));
        }

        [Test]
        public void ShouldDriveDataReadyAndFifoThresholdInterrupts()
        {
            WriteRegister(0x0D, 0x01);
            device.FeedAccelerationSample(1, 2, 3);
            Assert.IsTrue(device.Connections[0].IsSet);
            WriteRegister(0x12, 0x04);
            device.Write(new byte[] { 0x28 });
            device.Read(6);
            Assert.IsFalse(device.Connections[0].IsSet);

            WriteRegister(0x06, 0x06);
            WriteRegister(0x0D, 0x08);
            device.AdvanceSample();
            Assert.IsTrue(device.Connections[0].IsSet);
        }

        private byte ReadRegister(byte address)
        {
            device.Write(new byte[] { address });
            return device.Read(1)[0];
        }

        private void WriteRegister(byte address, byte value)
        {
            device.Write(new byte[] { address, value });
        }

        private LSM6DS3TRC device;
    }
}
