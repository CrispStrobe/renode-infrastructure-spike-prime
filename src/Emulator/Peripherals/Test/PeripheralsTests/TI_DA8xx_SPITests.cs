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
using Antmicro.Renode.Peripherals.SPI;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class TI_DA8xx_SPITests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
            spi = new TI_DA8xx_SPI(machine);
            target = new RecordingTarget();
            machine.SystemBus.Register(spi, new BusPointRegistration(SPIAddress));
            spi.Register(target, new NumberRegistrationPoint<int>(0));
            spi.WriteDoubleWord(GlobalControl0, 1);
            spi.WriteDoubleWord(GlobalControl1, Enable | Master | ClockMode);
            spi.WriteDoubleWord(Format0, 8);
        }

        [TearDown]
        public void TearDown()
        {
            machine.Dispose();
        }

        [Test]
        public void ShouldTransferOnlyWhenDataLaneIsWritten()
        {
            spi.WriteWord(Data1 + 2, 0x0000);
            CollectionAssert.IsEmpty(target.Bytes);

            spi.WriteByte(Data1 + 3, 0x10);
            CollectionAssert.IsEmpty(target.Bytes);
            Assert.AreEqual(0x10000000, spi.ReadDoubleWord(Data1));
            Assert.AreEqual(Enable | Master | ClockMode, spi.ReadDoubleWord(GlobalControl1));
            Assert.AreEqual(8, spi.ReadDoubleWord(Format0));

            spi.WriteByte(Data1, 0x5A);
            CollectionAssert.AreEqual(new byte[] { 0x5A }, target.Bytes);
            Assert.AreEqual(0xA5, spi.ReadDoubleWord(Buffer) & 0xFF);
            Assert.AreNotEqual(0, spi.ReadDoubleWord(Buffer) & ReceiveEmpty);
        }

        [Test]
        public void ShouldExposeLevelGatedReceiveInterrupt()
        {
            spi.WriteDoubleWord(InterruptEnable, ReceiveInterrupt);
            spi.WriteDoubleWord(Data1, 0x11);
            Assert.IsFalse(spi.IRQ.IsSet, "SPILVL must route an event to INT1");

            spi.WriteDoubleWord(InterruptLevel, ReceiveInterrupt);
            Assert.IsTrue(spi.IRQ.IsSet);
            Assert.AreEqual(0xEE, spi.ReadDoubleWord(EmulationBuffer) & 0xFF);
            Assert.IsTrue(spi.IRQ.IsSet, "SPIEMU must not consume receive data");
            Assert.AreEqual(0x24, spi.ReadDoubleWord(InterruptVector));
            Assert.IsFalse(spi.IRQ.IsSet);
            Assert.AreNotEqual(0, spi.ReadDoubleWord(Buffer) & ReceiveEmpty);
        }

        [Test]
        public void ShouldRouteTransmitEventAndPrioritizeOverrunVector()
        {
            const uint transmitInterrupt = 1 << 9;
            spi.WriteDoubleWord(InterruptEnable, transmitInterrupt | ReceiveInterrupt | ReceiveOverrun);
            spi.WriteDoubleWord(InterruptLevel, uint.MaxValue);
            Assert.AreEqual(0x35F, spi.ReadDoubleWord(InterruptLevel));
            Assert.IsFalse(spi.IRQ.IsSet);
            spi.WriteDoubleWord(Data1, 0);
            spi.ReadDoubleWord(Buffer);
            Assert.IsTrue(spi.IRQ.IsSet);
            Assert.AreEqual(0x28, spi.ReadDoubleWord(InterruptVector));
            spi.WriteDoubleWord(Data1, 1);
            spi.WriteDoubleWord(Data1, 2);
            spi.WriteDoubleWord(Data1, 3);
            Assert.AreEqual(0x26, spi.ReadDoubleWord(InterruptVector));
            Assert.AreEqual(0, spi.ReadDoubleWord(Flags) & ReceiveOverrun);
            Assert.AreEqual(0x24, spi.ReadDoubleWord(InterruptVector));
            Assert.AreEqual(0x24, spi.ReadDoubleWord(InterruptVector));
            Assert.AreEqual(0x28, spi.ReadDoubleWord(InterruptVector));
        }

        [Test]
        public void ShouldClearOnlyWrittenFlagLanes()
        {
            spi.WriteDoubleWord(Data1, 1);
            spi.WriteDoubleWord(Data1, 2);
            spi.WriteDoubleWord(Data1, 3);
            spi.WriteByte(Flags + 1, 1);
            Assert.AreNotEqual(0, spi.ReadDoubleWord(Flags) & ReceiveInterrupt, "the internal RXBUF is promoted");
            Assert.AreNotEqual(0, spi.ReadDoubleWord(Flags) & ReceiveOverrun);
            spi.WriteByte(Flags + 1, 1);
            Assert.AreEqual(0, spi.ReadDoubleWord(Flags) & ReceiveInterrupt);
            spi.WriteByte(Flags, (byte)ReceiveOverrun);
            Assert.AreEqual(0, spi.ReadDoubleWord(Flags) & ReceiveOverrun);
        }

        [Test]
        public void ShouldDetectOverrunAndUnsupportedCharacterLength()
        {
            spi.WriteDoubleWord(Data1, 0x01);
            spi.WriteDoubleWord(Data1, 0x02);
            Assert.AreEqual(0, spi.ReadDoubleWord(Flags) & ReceiveOverrun, "SPIBUF and RXBUF hold two characters");
            spi.WriteDoubleWord(Data1, 0x03);
            Assert.AreNotEqual(0, spi.ReadDoubleWord(Flags) & ReceiveOverrun);
            Assert.AreNotEqual(0, spi.ReadDoubleWord(EmulationBuffer) & BufferReceiveOverrun);
            Assert.AreEqual(0xFE, spi.ReadDoubleWord(Buffer) & 0xFF, "overrun keeps the unread byte");
            Assert.AreEqual(0xFD, spi.ReadDoubleWord(Buffer) & 0xFF, "reading SPIBUF promotes internal RXBUF");
            Assert.AreNotEqual(0, spi.ReadDoubleWord(Buffer) & ReceiveEmpty);

            spi.WriteDoubleWord(Flags, ReceiveOverrun);
            Assert.AreEqual(0, spi.ReadDoubleWord(Flags) & ReceiveOverrun);
            spi.WriteDoubleWord(Format0, 7);
            spi.WriteDoubleWord(Data1, 0x33);
            Assert.AreNotEqual(0, spi.ReadDoubleWord(Flags) & DataLengthError);
            CollectionAssert.AreEqual(new byte[] { 0x01, 0x02, 0x03 }, target.Bytes);
        }

        [Test]
        public void ShouldSupportInternalLoopbackAndResetDisable()
        {
            spi.WriteDoubleWord(GlobalControl1, Enable | Master | ClockMode | Loopback);
            spi.WriteDoubleWord(Data1, 0xC3);
            Assert.AreEqual(0xC3, spi.ReadDoubleWord(Buffer) & 0xFF);
            CollectionAssert.IsEmpty(target.Bytes);

            spi.WriteDoubleWord(GlobalControl0, 0);
            spi.WriteDoubleWord(Data1, 0x44);
            CollectionAssert.IsEmpty(target.Bytes);
            Assert.AreNotEqual(0, spi.ReadDoubleWord(Buffer) & ReceiveEmpty);
        }

        private Machine machine;
        private TI_DA8xx_SPI spi;
        private RecordingTarget target;

        private const long GlobalControl0 = 0x00;
        private const long GlobalControl1 = 0x04;
        private const long InterruptEnable = 0x08;
        private const long InterruptLevel = 0x0C;
        private const long Flags = 0x10;
        private const long Data1 = 0x3C;
        private const long Buffer = 0x40;
        private const long EmulationBuffer = 0x44;
        private const long Format0 = 0x50;
        private const long InterruptVector = 0x64;
        private const uint SPIAddress = 0x01F0E000;
        private const uint Master = 1;
        private const uint ClockMode = 1 << 1;
        private const uint Loopback = 1 << 16;
        private const uint Enable = 1 << 24;
        private const uint DataLengthError = 1;
        private const uint ReceiveOverrun = 1 << 6;
        private const uint ReceiveInterrupt = 1 << 8;
        private const uint ReceiveEmpty = 1u << 31;
        private const uint BufferReceiveOverrun = 1u << 30;

        private sealed class RecordingTarget : ISPIPeripheral
        {
            public byte Transmit(byte data)
            {
                Bytes.Add(data);
                return (byte)~data;
            }

            public void FinishTransmission()
            {
                FinishedTransactions++;
            }

            public void Reset()
            {
                Bytes.Clear();
            }

            public List<byte> Bytes { get; } = new List<byte>();
            public int FinishedTransactions { get; private set; }
        }

        [Test]
        public void ShouldRouteHardwareChipSelectAndExchangeComplete16BitWords()
        {
            var hardware = new TI_DA8xx_SPI(machine, externalChipSelect: false);
            machine.SystemBus.Register(hardware, new BusPointRegistration(SPIAddress + 0x1000));
            var adcTarget = new RecordingTarget();
            hardware.Register(adcTarget, new NumberRegistrationPoint<int>(3));
            hardware.Register(target, new NumberRegistrationPoint<int>(0));
            hardware.WriteDoubleWord(GlobalControl0, 1);
            hardware.WriteDoubleWord(GlobalControl1, Enable | Master | ClockMode);
            hardware.WriteDoubleWord(Format0, 16);
            hardware.WriteDoubleWord(Data1, 0x00F71234);
            Assert.AreEqual(0xEDCB, hardware.ReadDoubleWord(Buffer) & 0xFFFF);
            CollectionAssert.AreEqual(new byte[] { 0x12, 0x34 }, adcTarget.Bytes);
            CollectionAssert.IsEmpty(target.Bytes);
            Assert.AreEqual(1, adcTarget.FinishedTransactions);
            hardware.WriteDoubleWord(Data1, 0x00FF5678);
            Assert.AreEqual(0, hardware.ReadDoubleWord(Buffer) & 0xFFFF);
            Assert.AreEqual(2, adcTarget.Bytes.Count, "all CS lines high must not clock a target");
        }

        [Test]
        public void ShouldKeepHardwareTransactionAcrossControlLanesAndFinishOnDisable()
        {
            var hardware = new TI_DA8xx_SPI(machine, externalChipSelect: false);
            machine.SystemBus.Register(hardware, new BusPointRegistration(SPIAddress + 0x1000));
            hardware.Register(target, new NumberRegistrationPoint<int>(3));
            hardware.WriteDoubleWord(GlobalControl0, 1);
            hardware.WriteDoubleWord(GlobalControl1, Enable | Master | ClockMode);
            hardware.WriteDoubleWord(Format0, 16);
            hardware.WriteWord(Data1 + 2, 0x10F7);
            CollectionAssert.IsEmpty(target.Bytes);
            hardware.WriteWord(Data1, 0xA5C3);
            Assert.AreEqual(0, target.FinishedTransactions);
            hardware.WriteDoubleWord(GlobalControl1, 0);
            Assert.AreEqual(1, target.FinishedTransactions);
            CollectionAssert.AreEqual(new byte[] { 0xA5, 0xC3 }, target.Bytes);
        }
    }
}
