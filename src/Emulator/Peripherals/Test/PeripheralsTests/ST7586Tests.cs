//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Video;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class ST7586Tests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
            display = new ST7586(machine);
        }

        [TearDown]
        public void TearDown()
        {
            display.Dispose();
            machine.Dispose();
        }

        [Test]
        public void ShouldRenderEV3ThreePixelEncodingAndStableChecksum()
        {
            InitializeAndSetWindow(0, 0, 0, 0);
            Data(0xE0);

            Assert.IsTrue(display.DisplayEnabled);
            Assert.AreEqual(0x00, display.GetPixel(0, 0));
            Assert.AreEqual(0xFF, display.GetPixel(1, 0));
            Assert.AreEqual(0xFF, display.GetPixel(2, 0));
            Assert.AreEqual(0xD1AB8C3Au, display.FrameChecksum);
            Assert.AreEqual(1, display.AcceptedDataBytes);
            Assert.AreEqual(ST7586.VisibleWidth, display.TakeScreenshot().Width);
            Assert.AreEqual(ST7586.VisibleHeight, display.TakeScreenshot().Height);
        }

        [Test]
        public void ShouldGateTrafficByExternalChipSelectAndReset()
        {
            display.OnGPIO(ST7586.ChipSelectGPIO, true);
            Command(0x11);
            Command(0x29);
            Assert.IsFalse(display.DisplayEnabled);

            display.OnGPIO(ST7586.ChipSelectGPIO, false);
            InitializeAndSetWindow(0, 0, 0, 0);
            Data(0xE0);
            Assert.AreEqual(0, display.GetPixel(0, 0));

            display.OnGPIO(ST7586.ResetGPIO, true);
            Assert.IsFalse(display.DisplayEnabled);
            Assert.AreEqual(0xFF, display.GetPixel(0, 0));
            Assert.AreEqual(0, display.AcceptedDataBytes);
            display.OnGPIO(ST7586.ResetGPIO, false);
            Data(0xFF);
            Assert.AreEqual(0, display.AcceptedDataBytes, "reset clears memory-write state");
        }

        [Test]
        public void ShouldKeepCommandStateAcrossTransactionsAndWrapBoundedWindow()
        {
            Initialize();
            Command(0x2A);
            display.OnGPIO(ST7586.ChipSelectGPIO, true);
            display.FinishTransmission();
            display.OnGPIO(ST7586.ChipSelectGPIO, false);
            Data(0); Data(0); Data(0); Data(0);
            Command(0x2B);
            Data(0); Data(0); Data(0); Data(0);
            Command(0x2C);
            Data(0xE0);
            Data(0x1C);

            Assert.AreEqual(0xFF, display.GetPixel(0, 0), "second byte wraps and replaces the single-cell window");
            Assert.AreEqual(0x00, display.GetPixel(1, 0));
            Assert.AreEqual(2, display.AcceptedDataBytes);
        }

        [Test]
        public void ShouldApplyAddressMirroringAndInversion()
        {
            Initialize();
            Command(0x36); Data(0xC0);
            SetWindow(0, 0, 0, 0);
            Command(0x2C); Data(0xE0);
            Assert.AreEqual(0x00, display.GetPixel(ST7586.VisibleWidth - 1, ST7586.VisibleHeight - 1));

            Command(0x21);
            Assert.AreEqual(0xFF, display.GetPixel(ST7586.VisibleWidth - 1, ST7586.VisibleHeight - 1));
        }

        private void InitializeAndSetWindow(int startColumn, int endColumn, int startRow, int endRow)
        {
            Initialize();
            SetWindow(startColumn, endColumn, startRow, endRow);
            Command(0x2C);
        }

        [Test]
        public void ShouldPublishOneRgbFrameAtTransactionBoundaryAndReturnIndependentSnapshot()
        {
            InitializeAndSetWindow(0, 1, 0, 0);
            display.FinishTransmission();
            var events = 0;
            byte[] published = null;
            display.FrameRendered += frame => { events++; published = (byte[])frame.Clone(); };
            Data(0xE0); Data(0x1C);
            Assert.AreEqual(0, events);
            display.FinishTransmission();
            Assert.AreEqual(1, events);
            Assert.AreEqual(ST7586.VisibleWidth * ST7586.VisibleHeight * 3, published.Length);
            Assert.AreEqual(0, published[0]);
            Assert.AreEqual(255, published[3]);
            var snapshot = display.GetFrameSnapshot();
            Assert.AreEqual(ST7586.VisibleWidth * ST7586.VisibleHeight, snapshot.Length);
            snapshot[0] = 255;
            Assert.AreEqual(0, display.GetFrameSnapshot()[0]);
            display.OnGPIO(ST7586.ChipSelectGPIO, true);
            Data(0xFF);
            display.FinishTransmission();
            Assert.AreEqual(1, events, "CS-gated bytes do not publish unchanged frames");
            display.OnGPIO(ST7586.ResetGPIO, true);
            display.RefreshFrame();
            Data(0xFF);
            display.FinishTransmission();
            Assert.AreEqual(2, events, "reset-gated bytes do not dirty the reset frame");
        }

        private void Initialize()
        {
            Command(0x11);
            Command(0x38);
            Command(0x3A); Data(0x02);
            Command(0x20);
            Command(0x29);
        }

        private void SetWindow(int startColumn, int endColumn, int startRow, int endRow)
        {
            Command(0x2A);
            Data((byte)(startColumn >> 8)); Data((byte)startColumn);
            Data((byte)(endColumn >> 8)); Data((byte)endColumn);
            Command(0x2B);
            Data((byte)(startRow >> 8)); Data((byte)startRow);
            Data((byte)(endRow >> 8)); Data((byte)endRow);
        }

        private void Command(byte value)
        {
            display.OnGPIO(ST7586.DataCommandGPIO, false);
            display.Transmit(value);
        }

        private void Data(byte value)
        {
            display.OnGPIO(ST7586.DataCommandGPIO, true);
            display.Transmit(value);
        }

        private Machine machine;
        private ST7586 display;
    }
}
