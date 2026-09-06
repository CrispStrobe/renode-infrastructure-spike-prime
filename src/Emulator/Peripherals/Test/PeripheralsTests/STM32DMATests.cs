//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.Memory;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class STM32DMATests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);

            dma = new STM32DMA(machine);
            source = new IncrementingBytePeripheral();
            destination = new MappedMemory(machine, MemorySize);

            machine.SystemBus.Register(dma, new BusPointRegistration(DmaAddress));
            machine.SystemBus.Register(source, new BusRangeRegistration(SourceAddress, 1));
            machine.SystemBus.Register(destination, new BusRangeRegistration(DestinationAddress, MemorySize));
        }

        [TearDown]
        public void TearDown()
        {
            machine.Dispose();
        }

        [Test]
        public void ShouldClampFifoRequestToRemainingDataAndCompleteNormalTransfer()
        {
            ConfigurePeripheralToMemory(numberOfData: 3, circular: false, fifoEnabled: true);

            dma.OnGPIO(0, true);

            CollectionAssert.AreEqual(new byte[] { 0, 1, 2 }, destination.ReadBytes(0, 3));
            Assert.AreEqual(0, ReadNumberOfData());
            Assert.False(IsStreamEnabled());
            Assert.True(IsTransferComplete());
            Assert.True(dma.Connections[0].IsSet);
        }

        [Test]
        public void ShouldReloadCircularTransferAndRetainEnable()
        {
            ConfigurePeripheralToMemory(numberOfData: 2, circular: true, fifoEnabled: false);

            dma.OnGPIO(0, true);
            Assert.AreEqual(1, ReadNumberOfData());
            Assert.False(IsTransferComplete());

            dma.OnGPIO(0, true);
            Assert.AreEqual(2, ReadNumberOfData());
            Assert.True(IsStreamEnabled());
            Assert.True(IsTransferComplete());
            Assert.True(dma.Connections[0].IsSet);
            CollectionAssert.AreEqual(new byte[] { 0, 1 }, destination.ReadBytes(0, 2));

            dma.WriteDoubleWord(LowInterruptClear, TransferCompleteFlag);
            Assert.False(IsTransferComplete());
            Assert.False(dma.Connections[0].IsSet);

            dma.OnGPIO(0, true);
            Assert.AreEqual(1, ReadNumberOfData());
            Assert.AreEqual(2, destination.ReadByte(0));
        }

        [Test]
        public void ShouldPaceMemoryToPeripheralTransferWithRequests()
        {
            destination.WriteBytes(0, new byte[] { 0x11, 0x22, 0x33 });
            dma.WriteDoubleWord(StreamPeripheralAddress, SourceAddress);
            dma.WriteDoubleWord(StreamMemory0Address, DestinationAddress);
            dma.WriteDoubleWord(StreamNumberOfData, 3);
            dma.WriteDoubleWord(StreamConfiguration,
                StreamEnable | TransferCompleteInterruptEnable | MemoryIncrement | MemoryToPeripheral);

            CollectionAssert.IsEmpty(source.WrittenBytes);
            Assert.AreEqual(3, ReadNumberOfData());

            dma.OnGPIO(0, true);
            CollectionAssert.AreEqual(new byte[] { 0x11 }, source.WrittenBytes);
            Assert.AreEqual(2, ReadNumberOfData());

            dma.OnGPIO(0, true);
            dma.OnGPIO(0, true);
            CollectionAssert.AreEqual(new byte[] { 0x11, 0x22, 0x33 }, source.WrittenBytes);
            Assert.AreEqual(0, ReadNumberOfData());
            Assert.False(IsStreamEnabled());
            Assert.True(IsTransferComplete());
        }

        [Test]
        public void ShouldPreserveTransmitRequestUntilStreamIsEnabled()
        {
            destination.WriteByte(0, 0xA5);
            dma.WriteDoubleWord(StreamPeripheralAddress, SourceAddress);
            dma.WriteDoubleWord(StreamMemory0Address, DestinationAddress);
            dma.WriteDoubleWord(StreamNumberOfData, 1);
            dma.WriteDoubleWord(StreamConfiguration, MemoryToPeripheral);

            dma.OnGPIO(0, true);
            CollectionAssert.IsEmpty(source.WrittenBytes);

            dma.WriteDoubleWord(StreamConfiguration, StreamEnable | MemoryToPeripheral);
            CollectionAssert.AreEqual(new byte[] { 0xA5 }, source.WrittenBytes);
            Assert.AreEqual(0, ReadNumberOfData());
            Assert.False(IsStreamEnabled());
        }

        [Test]
        public void ShouldPreserveReceiveRequestUntilStreamIsEnabled()
        {
            dma.WriteDoubleWord(StreamPeripheralAddress, SourceAddress);
            dma.WriteDoubleWord(StreamMemory0Address, DestinationAddress);
            dma.WriteDoubleWord(StreamNumberOfData, 1);

            dma.OnGPIO(0, true);
            dma.WriteDoubleWord(StreamConfiguration, StreamEnable);

            Assert.AreEqual(0, destination.ReadByte(0));
            Assert.AreEqual(0, ReadNumberOfData());
            Assert.False(IsStreamEnabled());
        }

        private void ConfigurePeripheralToMemory(uint numberOfData, bool circular, bool fifoEnabled)
        {
            dma.WriteDoubleWord(StreamPeripheralAddress, SourceAddress);
            dma.WriteDoubleWord(StreamMemory0Address, DestinationAddress);
            dma.WriteDoubleWord(StreamNumberOfData, numberOfData);
            dma.WriteDoubleWord(StreamFifoControl, fifoEnabled ? FifoEnabledWithFullThreshold : 0);

            var configuration = StreamEnable | TransferCompleteInterruptEnable | MemoryIncrement;
            if(circular)
            {
                configuration |= CircularMode;
            }
            dma.WriteDoubleWord(StreamConfiguration, configuration);
        }

        private uint ReadNumberOfData()
        {
            return dma.ReadDoubleWord(StreamNumberOfData) & 0xFFFF;
        }

        private bool IsStreamEnabled()
        {
            return (dma.ReadDoubleWord(StreamConfiguration) & StreamEnable) != 0;
        }

        private bool IsTransferComplete()
        {
            return (dma.ReadDoubleWord(LowInterruptStatus) & TransferCompleteFlag) != 0;
        }

        private Machine machine;
        private STM32DMA dma;
        private IncrementingBytePeripheral source;
        private MappedMemory destination;

        private const uint DmaAddress = 0x40026400;
        private const uint SourceAddress = 0x4001300C;
        private const uint DestinationAddress = 0x20000000;
        private const uint MemorySize = 0x100;

        private const long LowInterruptStatus = 0x0;
        private const long LowInterruptClear = 0x8;
        private const long StreamConfiguration = 0x10;
        private const long StreamNumberOfData = 0x14;
        private const long StreamPeripheralAddress = 0x18;
        private const long StreamMemory0Address = 0x1C;
        private const long StreamFifoControl = 0x24;

        private const uint StreamEnable = 1 << 0;
        private const uint TransferCompleteInterruptEnable = 1 << 4;
        private const uint CircularMode = 1 << 8;
        private const uint MemoryIncrement = 1 << 10;
        private const uint MemoryToPeripheral = 1 << 6;
        private const uint TransferCompleteFlag = 1 << 5;
        private const uint FifoEnabledWithFullThreshold = (1 << 2) | 3;

        private sealed class IncrementingBytePeripheral : IBytePeripheral
        {
            public byte ReadByte(long offset)
            {
                return nextValue++;
            }

            public void WriteByte(long offset, byte value)
            {
                WrittenBytes.Add(value);
            }

            public void Reset()
            {
                nextValue = 0;
                WrittenBytes.Clear();
            }

            public List<byte> WrittenBytes { get; } = new List<byte>();
            private byte nextValue;
        }
    }
}
