//
// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
//
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.Memory;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class TI_DA8xx_EDMA3CCTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
            dma = new TI_DA8xx_EDMA3CC(machine);
            source = new MappedMemory(machine, MemorySize);
            destination = new MappedMemory(machine, MemorySize);
            machine.SystemBus.Register(dma, new BusRangeRegistration(DmaAddress, 0x8000));
            machine.SystemBus.Register(source, new BusRangeRegistration(SourceAddress, MemorySize));
            machine.SystemBus.Register(destination, new BusRangeRegistration(DestinationAddress, MemorySize));
        }

        [TearDown]
        public void TearDown()
        {
            machine.Dispose();
            EmulationManager.Instance.Clear();
        }

        [Test]
        public void ShouldExposeExactMapAndKeepQdmaExecutionOutOfV1()
        {
            Assert.AreEqual(0x40015300u, dma.ReadDoubleWord(0));
            Assert.AreEqual(0x213344u, dma.ReadDoubleWord(4));
            Assert.AreEqual(0x203344u, new TI_DA8xx_EDMA3CC(machine, 1).ReadDoubleWord(4));
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x100 + 3 * 4));
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x340));
            dma.WriteDoubleWord(0x200, 0x12345667);
            Assert.AreEqual(0x12345667u & 0x3FFCu, dma.ReadDoubleWord(0x200));
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x4000));
            Assert.False(dma.IRQ.IsSet);
            Assert.False(dma.ErrorIRQ.IsSet);
        }

        [Test]
        public void ShouldGateShadowAccessWithDraeAndDrainEnabledHardwareEvent()
        {
            source.WriteBytes(0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 0, 8);
            Configure(2, SourceAddress, DestinationAddress, 4, 2, 1, 4, 4, 0, 0,
                (5u << 12) | (1u << 20));

            dma.WriteDoubleWord(0x2030, 1u << 2);
            dma.WriteDoubleWord(0x2060, 1u << 5);
            dma.OnGPIO(2, true);
            CollectionAssert.AreEqual(new byte[4], destination.ReadBytes(0, 4));

            dma.WriteDoubleWord(0x340, (1u << 2) | (1u << 5));
            dma.WriteDoubleWord(0x2030, 1u << 2);
            dma.WriteDoubleWord(0x2060, 1u << 5);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, destination.ReadBytes(0, 4));
            dma.OnGPIO(2, true);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, destination.ReadBytes(0, 8));
            Assert.AreEqual(1u << 5, dma.ReadDoubleWord(0x2068));
            Assert.True(dma.IRQ.IsSet);
            dma.WriteDoubleWord(0x2070, 1u << 5);
            Assert.False(dma.IRQ.IsSet);
        }

        [Test]
        public void ShouldCopyAbSynchronizedFramesWithSignedIndexes()
        {
            source.WriteBytes(0x10, new byte[] { 9, 8, 7, 6 }, 0, 4);
            Configure(0, SourceAddress + 0x12, DestinationAddress, 2, 2, 2, -2, 2, 0, 4, 1u << 2);
            dma.WriteDoubleWord(0x340, 1);
            dma.WriteDoubleWord(0x2010, 1);
            dma.WriteDoubleWord(0x2010, 1);
            CollectionAssert.AreEqual(new byte[] { 7, 6, 9, 8, 7, 6, 9, 8 }, destination.ReadBytes(0, 8));
            Assert.AreEqual(SourceAddress + 0x12, dma.ReadDoubleWord(Param(0, 1)));
        }

        [Test]
        public void ShouldPerformSixASynchronizedEventsAndReplaceFinalBIndexWithCIndex()
        {
            var bytes = new byte[24];
            for(var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i + 1);
            source.WriteBytes(0, bytes, 0, bytes.Length);
            Configure(4, SourceAddress, DestinationAddress, 4, 3, 2, 4, 4, 4, 4, 0);
            dma.WriteDoubleWord(0x340, 1u << 4);
            for(var i = 0; i < 6; i++) dma.WriteDoubleWord(0x2010, 1u << 4);
            CollectionAssert.AreEqual(bytes, destination.ReadBytes(0, bytes.Length));
            Assert.AreEqual(0u, dma.ReadDoubleWord(Param(4, 7)));
        }

        [Test]
        public void ShouldKeepStaticParameterAndSupportFullAndAliasLinkAddresses()
        {
            source.WriteByte(0, 0x5A);
            Configure(6, SourceAddress, DestinationAddress, 1, 1, 1, 1, 1, 0, 0,
                (6u << 12) | (1u << 20) | (1u << 3), link: 0x40A0);
            var beforeSource = dma.ReadDoubleWord(Param(6, 1));
            var beforeCounts = dma.ReadDoubleWord(Param(6, 2));
            dma.WriteDoubleWord(0x340, 1u << 6);
            dma.WriteDoubleWord(0x2010, 1u << 6);
            Assert.AreEqual(0x5A, destination.ReadByte(0));
            Assert.AreEqual(beforeSource, dma.ReadDoubleWord(Param(6, 1)));
            Assert.AreEqual(beforeCounts, dma.ReadDoubleWord(Param(6, 2)));

            Configure(7, SourceAddress, DestinationAddress + 4, 1, 1, 1, 1, 1, 0, 0, 0, link: 0x4020);
            Configure(1, SourceAddress + 1, DestinationAddress + 5, 1, 1, 1, 1, 1, 0, 0, 1u << 3);
            dma.WriteDoubleWord(0x340, (1u << 6) | (1u << 7));
            dma.WriteDoubleWord(0x2010, 1u << 7);
            Assert.AreEqual(SourceAddress + 1, dma.ReadDoubleWord(Param(7, 1)));

            Configure(9, SourceAddress, DestinationAddress + 6, 1, 1, 1, 1, 1, 0, 0, 0, link: 0x0020);
            dma.WriteDoubleWord(0x340, (1u << 6) | (1u << 7) | (1u << 9));
            dma.WriteDoubleWord(0x2010, 1u << 9);
            Assert.AreEqual(SourceAddress + 1, dma.ReadDoubleWord(Param(9, 1)));
        }

        [Test]
        public void ShouldExposeGlobalAndIndependentShadowRegionsAndIntermediateInterrupt()
        {
            source.WriteBytes(0, new byte[] { 1, 2 }, 0, 2);
            Configure(3, SourceAddress, DestinationAddress, 1, 1, 2, 1, 1, 1, 1,
                (3u << 12) | (1u << 21));
            dma.WriteDoubleWord(0x348, 1u << 3); // DRAE1
            dma.WriteDoubleWord(0x2260, 1u << 3); // region 1 IESR
            dma.WriteDoubleWord(0x2210, 1u << 3);
            Assert.AreEqual(1u << 3, dma.ReadDoubleWord(0x2268));
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x2068));
            Assert.False(dma.IRQ.IsSet); // exported completion is region 0
            dma.WriteDoubleWord(0x1070, 1u << 3); // global ICR
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x2268));
        }

        [Test]
        public void ShouldAcceptAZeroByteDummyUnlessAllCountsAreNull()
        {
            Configure(8, SourceAddress, DestinationAddress, 0, 1, 1, 0, 0, 0, 0, 1u << 3);
            dma.WriteDoubleWord(0x340, 1u << 8);
            dma.WriteDoubleWord(0x2010, 1u << 8);
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x300));
            Assert.False(dma.ErrorIRQ.IsSet);
        }

        [Test]
        public void ShouldIssueTwoFixedThirtyTwoBitFifoWrites()
        {
            source.WriteBytes(0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 0, 8);
            var fifo = new RecordingDoubleWordPeripheral();
            machine.SystemBus.Register(fifo, new BusRangeRegistration(FifoAddress, 4));
            Configure(10, SourceAddress, FifoAddress, 8, 1, 1, 8, 0, 0, 0,
                (1u << 1) | (2u << 8) | (1u << 3));
            dma.WriteDoubleWord(0x340, 1u << 10);
            dma.WriteDoubleWord(0x2010, 1u << 10);
            CollectionAssert.AreEqual(new uint[] { 0x04030201, 0x08070605 }, fifo.Writes);
            CollectionAssert.AreEqual(new long[] { 0, 0 }, fifo.Offsets);
        }

        [Test]
        public void ShouldReloadLinkedParameterAndSupportFixedFifoDestination()
        {
            source.WriteBytes(0, new byte[] { 0xA1, 0xB2, 0xC3 }, 0, 3);
            var fifo = new RecordingBytePeripheral();
            machine.SystemBus.Register(fifo, new BusRangeRegistration(FifoAddress, 1));
            Configure(1, SourceAddress, FifoAddress, 3, 1, 1, 3, 0, 0, 0, 1u << 1, link: 3 * 0x20);
            Configure(3, SourceAddress + 1, DestinationAddress, 1, 1, 1, 1, 1, 0, 0, 1u << 3);
            dma.WriteDoubleWord(0x340, 1u << 1);
            dma.WriteDoubleWord(0x2010, 1u << 1);
            CollectionAssert.AreEqual(new byte[] { 0xA1, 0xB2, 0xC3 }, fifo.Writes);
            Assert.AreEqual(SourceAddress + 1, dma.ReadDoubleWord(Param(1, 1)));
            dma.WriteDoubleWord(0x2010, 1u << 1);
            Assert.AreEqual(0xB2, destination.ReadByte(0));
        }

        [Test]
        public void ShouldReportNullAndBusErrorsAtTheirOwnersAndClearW1C()
        {
            dma.WriteDoubleWord(0x340, 1);
            dma.WriteDoubleWord(0x2010, 1);
            Assert.AreEqual(1u, dma.ReadDoubleWord(0x300));
            Assert.True(dma.ErrorIRQ.IsSet);
            dma.WriteDoubleWord(0x308, 1);
            Assert.False(dma.ErrorIRQ.IsSet);

            var tc = new TI_DA8xx_EDMA3TC();
            tc.WriteDoubleWord(0x124, 1);
            dma.AttachTransferController(tc, 0);
            Configure(0, 0xDEAD0000, DestinationAddress, 1, 1, 1, 1, 1, 0, 0, 1u << 3);
            dma.WriteDoubleWord(0x2010, 1);
            Assert.AreEqual(1u, tc.ReadDoubleWord(0x120));
            Assert.True(tc.IRQ.IsSet);
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x318));
            tc.WriteDoubleWord(0x128, 1);
            Assert.False(tc.IRQ.IsSet);
        }

        private void Configure(int channel, uint sourceAddress, uint destinationAddress,
            ushort aCount, ushort bCount, ushort cCount, short sourceBIndex, short destinationBIndex,
            short sourceCIndex, short destinationCIndex, uint option, int link = 0xFFFF)
        {
            dma.WriteDoubleWord(Param(channel, 0), option);
            dma.WriteDoubleWord(Param(channel, 1), sourceAddress);
            dma.WriteDoubleWord(Param(channel, 2), ((uint)bCount << 16) | aCount);
            dma.WriteDoubleWord(Param(channel, 3), destinationAddress);
            dma.WriteDoubleWord(Param(channel, 4), ((uint)(ushort)destinationBIndex << 16) | (ushort)sourceBIndex);
            dma.WriteDoubleWord(Param(channel, 5), (uint)link | ((uint)bCount << 16));
            dma.WriteDoubleWord(Param(channel, 6), ((uint)(ushort)destinationCIndex << 16) | (ushort)sourceCIndex);
            dma.WriteDoubleWord(Param(channel, 7), cCount);
        }

        private static long Param(int channel, int word) => 0x4000 + channel * 0x20 + word * 4;

        private sealed class RecordingBytePeripheral : IBytePeripheral
        {
            public byte ReadByte(long offset) => 0;
            public void WriteByte(long offset, byte value) => Writes.Add(value);
            public void Reset() => Writes.Clear();
            public List<byte> Writes { get; } = new List<byte>();
        }

        private sealed class RecordingDoubleWordPeripheral : IDoubleWordPeripheral
        {
            public uint ReadDoubleWord(long offset) => 0;
            public void WriteDoubleWord(long offset, uint value)
            {
                Offsets.Add(offset);
                Writes.Add(value);
            }
            public void Reset()
            {
                Offsets.Clear();
                Writes.Clear();
            }
            public List<long> Offsets { get; } = new List<long>();
            public List<uint> Writes { get; } = new List<uint>();
        }

        private Machine machine;
        private TI_DA8xx_EDMA3CC dma;
        private MappedMemory source, destination;
        private const uint DmaAddress=0x10000000, SourceAddress=0x20000000, DestinationAddress=0x30000000, FifoAddress=0x40000000;
        private const long MemorySize=0x1000;
    }
}
