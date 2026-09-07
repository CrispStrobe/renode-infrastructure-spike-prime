//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Peripherals.I2C;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class LP50XXTests
    {
        [SetUp]
        public void SetUp()
        {
            device = new LP50XX();
        }

        [Test]
        public void ShouldIgnoreTrafficUntilEnabledAndResetOnDisable()
        {
            WriteRegister(0x00, 0x40);
            Assert.IsFalse(device.ChipEnabled);

            device.OnGPIO(LP50XX.EnableGPIO, true);
            WriteRegister(0x00, 0x40);
            Assert.IsTrue(device.ChipEnabled);

            device.OnGPIO(LP50XX.EnableGPIO, false);
            Assert.IsFalse(device.Enabled);
            device.OnGPIO(LP50XX.EnableGPIO, true);
            Assert.AreEqual(0, ReadRegister(0x00));
        }

        [Test]
        public void ShouldHonorAutoIncrementForColorBurst()
        {
            device.OnGPIO(LP50XX.EnableGPIO, true);
            WriteRegister(0x01, 0x08);
            device.Write(new byte[] { 0x0B, 1, 2, 3, 4, 5, 6 });

            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 0, 0, 0, 0, 0, 0 },
                device.OutputColorSnapshot);
            device.Write(new byte[] { 0x0B });
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6 }, device.Read(6));
        }

        [Test]
        public void ShouldKeepAddressFixedWithoutAutoIncrement()
        {
            device.OnGPIO(LP50XX.EnableGPIO, true);
            WriteRegister(0x01, 0x00);
            device.Write(new byte[] { 0x0B, 1, 2, 3 });

            Assert.AreEqual(3, device.OutputColorSnapshot[0]);
            Assert.AreEqual(0, device.OutputColorSnapshot[1]);
        }

        [Test]
        public void ShouldApplySoftwareResetAndProtectSnapshot()
        {
            device.OnGPIO(LP50XX.EnableGPIO, true);
            WriteRegister(0x01, 0x08);
            WriteRegister(0x0B, 0xAA);
            WriteRegister(0x17, 0xFF);

            var snapshot = device.RegisterSnapshot;
            CollectionAssert.AreEqual(new byte[LP50XX.OutputCount], device.OutputColorSnapshot);
            snapshot[0] = 0x40;
            Assert.IsFalse(device.ChipEnabled);
            Assert.IsTrue(device.Enabled);
        }

        [Test]
        public void ShouldAcceptEssentialFirmwareInitializationAndExposeRenderedModules()
        {
            device.OnGPIO(LP50XX.EnableGPIO, true);
            // DEVICE_CONFIG0 through LED3_BRIGHTNESS, as written in one DMA
            // transaction by the pinned Essential Pybricks driver.
            device.Write(new byte[] { 0x00, 0x40, 0x1C, 0, 0, 0, 0, 0, 51, 38, 0, 0 });
            device.Write(new byte[] { 0x0B, 255, 128, 0, 10, 20, 30 });

            var rendered = device.RenderedModuleSnapshot;
            CollectionAssert.AreEqual(new byte[] { 51, 26, 0 }, rendered[0]);
            CollectionAssert.AreEqual(new byte[] { 1, 3, 4 }, rendered[1]);
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0 }, rendered[2]);

            // Every returned level is defensive for GUI consumers.
            rendered[0][0] = 0;
            Assert.AreEqual(51, device.RenderedModuleSnapshot[0][0]);
        }

        [Test]
        public void ShouldHonorGlobalOffWithoutDestroyingRawColors()
        {
            device.OnGPIO(LP50XX.EnableGPIO, true);
            WriteRegister(0x00, 0x40);
            WriteRegister(0x07, 0xFF);
            WriteRegister(0x0B, 0x80);
            WriteRegister(0x01, 0x01);

            Assert.AreEqual(0x80, device.OutputColorSnapshot[0]);
            Assert.AreEqual(0, device.RenderedModuleSnapshot[0][0]);
        }

        private void WriteRegister(byte address, byte value)
        {
            device.Write(new byte[] { address, value });
        }

        private byte ReadRegister(byte address)
        {
            device.Write(new byte[] { address });
            return device.Read(1)[0];
        }

        private LP50XX device;
    }
}
