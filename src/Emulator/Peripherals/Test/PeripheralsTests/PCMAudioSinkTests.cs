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
            sink.WriteByte(0, 0x81);
            Assert.AreEqual(0x81, sink.ReadByte(0));
            Assert.AreEqual(0, sink.BufferedBytes);
        }
    }
}
