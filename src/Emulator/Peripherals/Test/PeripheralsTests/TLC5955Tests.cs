//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Linq;

using Antmicro.Renode.Peripherals.SPI;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class TLC5955Tests
    {
        [SetUp]
        public void SetUp()
        {
            device = new TLC5955();
        }

        [Test]
        public void ShouldLatchOnlyOnFallingEdge()
        {
            var frame = Enumerable.Range(0, TLC5955.FrameSize).Select(x => (byte)x).ToArray();
            foreach(var value in frame)
            {
                Assert.AreEqual(0, device.Transmit(value));
            }

            device.FinishTransmission();
            CollectionAssert.AreEqual(new byte[TLC5955.FrameSize], device.LatchedRegister);

            device.OnGPIO(TLC5955.LatchGPIO, true);
            CollectionAssert.AreEqual(new byte[TLC5955.FrameSize], device.LatchedRegister);
            device.OnGPIO(TLC5955.LatchGPIO, false);

            CollectionAssert.AreEqual(frame, device.LatchedRegister);
            Assert.AreEqual(1, device.LatchedFrames);
            Assert.AreEqual((ulong)TLC5955.FrameSize, device.TotalBytes);
        }

        [Test]
        public void ShouldRetainMostRecentShiftRegisterBytes()
        {
            for(var i = 0; i < TLC5955.FrameSize + 3; ++i)
            {
                device.Transmit((byte)i);
            }

            var expected = Enumerable.Range(3, TLC5955.FrameSize).Select(x => (byte)x).ToArray();
            CollectionAssert.AreEqual(expected, device.ShiftRegister);
        }

        [Test]
        public void ShouldResetStateAndReturnDefensiveCopies()
        {
            device.Transmit(0xaa);
            device.OnGPIO(TLC5955.LatchGPIO, true);
            device.OnGPIO(TLC5955.LatchGPIO, false);
            var snapshot = device.LatchedRegister;
            snapshot[TLC5955.FrameSize - 1] = 0;
            Assert.AreEqual(0xaa, device.LatchedRegister[TLC5955.FrameSize - 1]);

            device.Reset();

            CollectionAssert.AreEqual(new byte[TLC5955.FrameSize], device.ShiftRegister);
            CollectionAssert.AreEqual(new byte[TLC5955.FrameSize], device.LatchedRegister);
            Assert.AreEqual(0, device.TotalBytes);
            Assert.AreEqual(0, device.LatchedFrames);
        }

        [Test]
        public void ShouldDecodePrimeMatrixAndTrackGrayscalePhase()
        {
            var frame = new byte[TLC5955.FrameSize];
            frame[38 * 2 + 1] = 0x12;
            frame[38 * 2 + 2] = 0x34;
            foreach(var value in frame)
            {
                device.Transmit(value);
            }
            device.OnGPIO(TLC5955.LatchGPIO, true);
            device.OnGPIO(TLC5955.LatchGPIO, false);
            Assert.AreEqual(0x1234, device.Matrix[0]);

            device.OnGPIO(TLC5955.GrayscaleClockGPIO, true);
            device.OnGPIO(TLC5955.GrayscaleClockGPIO, false);
            device.OnGPIO(TLC5955.GrayscaleClockGPIO, true);
            Assert.AreEqual(2, device.GrayscaleClockEdges);
            Assert.AreEqual(2, device.GrayscalePhase);
        }

        private TLC5955 device;
    }
}
