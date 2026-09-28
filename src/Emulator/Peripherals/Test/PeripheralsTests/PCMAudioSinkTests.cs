// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
using Antmicro.Renode.Peripherals.Sound;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class PCMAudioSinkTests
    {
        [Test]
        public void ShouldRecordDmaWritesDeterministically()
        {
            var sink = new PCMAudioSink(3);
            sink.OnGPIO(0, true);
            sink.WriteByte(0, 1);
            sink.WriteByte(0, 2);
            sink.WriteByte(0, 3);
            sink.WriteByte(0, 4);
            CollectionAssert.AreEqual(new byte[] { 2, 3, 4 }, sink.Snapshot);
            Assert.AreEqual(4, sink.TotalBytes);
            Assert.AreEqual(1, sink.DroppedBytes);
            Assert.AreEqual(4, sink.LastSample);
        }

        [Test]
        public void ShouldReturnAndRemoveOldestSample()
        {
            var sink = new PCMAudioSink();
            sink.OnGPIO(0, true);
            sink.WriteByte(0, 0x81);
            Assert.AreEqual(0x81, sink.ReadByte(0));
            Assert.AreEqual(0, sink.BufferedBytes);
        }

        [Test]
        public void ShouldObserveAmplifierEnable()
        {
            var sink = new PCMAudioSink();
            sink.OnGPIO(0, true);
            Assert.IsTrue(sink.Enabled);
            sink.Reset();
            Assert.IsFalse(sink.Enabled);
            sink.WriteByte(0, 0x55);
            Assert.AreEqual(0, sink.BufferedBytes);
            Assert.AreEqual(1, sink.DisabledBytes);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => sink.WriteByte(1, 0));
        }

        [Test]
        public void ShouldMaskAndPaceTwelveBitDacSamples()
        {
            var sink = new PCMAudioSink(2);
            sink.OnGPIO(0, true);
            sink.WriteWord(0, 0xF234);
            sink.WriteWord(0, 0x0567);
            Assert.AreEqual(2, sink.PendingSamples);
            Assert.AreEqual(0, sink.EmittedSamples);
            sink.AdvanceSampleClock();
            Assert.AreEqual(0x234, sink.LastDacSample);
            Assert.AreEqual(1, sink.EmittedSamples);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => sink.AdvanceSampleClock(-1));
        }

        [Test]
        public void ShouldPaceOneSamplePerRisingTriggerEdge()
        {
            var sink = new PCMAudioSink();
            sink.OnGPIO(0, true);
            sink.WriteWord(0, 0x123);
            sink.WriteWord(0, 0x456);

            sink.OnGPIO(1, false);
            Assert.AreEqual(0, sink.EmittedSamples);
            sink.OnGPIO(1, true);
            Assert.AreEqual(1, sink.EmittedSamples);
            Assert.AreEqual(0x123, sink.LastDacSample);
            sink.OnGPIO(1, false);
            sink.OnGPIO(1, true);
            Assert.AreEqual(2, sink.EmittedSamples);
            Assert.AreEqual(0x456, sink.LastDacSample);
        }
    }
}
