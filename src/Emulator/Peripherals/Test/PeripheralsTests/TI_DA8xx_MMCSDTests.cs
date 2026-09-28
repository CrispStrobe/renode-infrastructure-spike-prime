//
// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
//
using System;
using System.IO;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.SD;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class TI_DA8xx_MMCSDTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
            controller = new TI_DA8xx_MMCSD(machine);
            machine.SystemBus.Register(controller, new BusRangeRegistration(ControllerAddress, 0x1000));
            image = Enumerable.Range(0, ImageSize).Select(x => (byte)(x * 29 + 7)).ToArray();
            imagePath = Path.GetTempFileName();
            File.WriteAllBytes(imagePath, image);
        }

        [TearDown]
        public void TearDown()
        {
            card?.Dispose();
            machine.Dispose();
            EmulationManager.Instance.Clear();
            File.Delete(imagePath);
        }

        [Test]
        public void ShouldExposeExactResetValuesMasksAndAccessWidths()
        {
            Assert.AreEqual(0u, controller.ReadDoubleWord(Control));
            Assert.AreEqual(0xFFu, controller.ReadDoubleWord(Clock));
            Assert.AreEqual(0x200u, controller.ReadDoubleWord(Status0));
            Assert.AreEqual(0x26u, controller.ReadDoubleWord(Status1));
            Assert.AreEqual(0x200u, controller.ReadDoubleWord(BlockLength));
            Assert.AreEqual(0xFFFFu, controller.ReadDoubleWord(NumberOfBlocksCounter));
            Assert.AreEqual(1u, controller.ReadDoubleWord(SDIOStatus0));
            Assert.False(controller.IRQ.IsSet);
            Assert.False(controller.SDIOIRQ.IsSet);
            Assert.False(controller.RxDMARequest.IsSet);
            Assert.False(controller.TxDMARequest.IsSet);

            controller.WriteDoubleWord(Control, UInt32.MaxValue);
            controller.WriteDoubleWord(Clock, UInt32.MaxValue);
            controller.WriteDoubleWord(InterruptMask, UInt32.MaxValue);
            controller.WriteDoubleWord(ResponseTimeout, UInt32.MaxValue);
            controller.WriteDoubleWord(BlockLength, UInt32.MaxValue);
            controller.WriteDoubleWord(FifoControl, UInt32.MaxValue);
            Assert.AreEqual(0x7C7u, controller.ReadDoubleWord(Control));
            Assert.AreEqual(0x3FFu, controller.ReadDoubleWord(Clock));
            Assert.AreEqual(0x3EFFu, controller.ReadDoubleWord(InterruptMask));
            Assert.AreEqual(0x3FFFFu, controller.ReadDoubleWord(ResponseTimeout));
            Assert.AreEqual(0xFFFu, controller.ReadDoubleWord(BlockLength));
            Assert.AreEqual(0x1Fu, controller.ReadDoubleWord(FifoControl));

            controller.Reset();
            controller.WriteByte(Clock + 1, 3);
            Assert.AreEqual(0x3FFu, controller.ReadDoubleWord(Clock));
            controller.WriteWord(NumberOfBlocksCounter, 0);
            Assert.AreEqual(0xFFFFu, controller.ReadDoubleWord(NumberOfBlocksCounter));
        }

        [Test]
        public void ShouldOnlyTimeoutExpectedResponseAndAssertPendingInterruptOnUnmask()
        {
            controller.WriteDoubleWord(Command, 0); // CMD0, no response
            Assert.AreEqual(0x200u, controller.ReadDoubleWord(Status0));

            controller.WriteDoubleWord(Command, DataRead | DataWrite | 24u); // rejected write, no response
            Assert.AreEqual(0x220u, controller.ReadDoubleWord(Status0));

            controller.WriteDoubleWord(Command, Response48 | 13u);
            Assert.False(controller.IRQ.IsSet);
            controller.WriteDoubleWord(InterruptMask, ResponseTimeoutStatus);
            Assert.True(controller.IRQ.IsSet);
            Assert.AreEqual(0x210u, controller.ReadDoubleWord(Status0));
            Assert.False(controller.IRQ.IsSet);
            Assert.AreEqual(0x200u, controller.ReadDoubleWord(Status0));
        }

        [Test]
        public void ShouldLatchSdioInterruptWhileMaskedAndClearItW1C()
        {
            controller.SetSDIOInterrupt(true);
            Assert.AreEqual(1u, controller.ReadDoubleWord(SDIOInterruptStatus));
            Assert.False(controller.SDIOIRQ.IsSet);
            controller.WriteDoubleWord(SDIOInterruptEnable, 1);
            Assert.True(controller.SDIOIRQ.IsSet);
            controller.WriteDoubleWord(SDIOInterruptStatus, 1);
            Assert.AreEqual(0u, controller.ReadDoubleWord(SDIOInterruptStatus));
            Assert.False(controller.SDIOIRQ.IsSet);
        }

        [Test]
        public void ShouldExposeShortAndLongResponsesInTiRegisterOrder()
        {
            AttachCard();
            controller.WriteDoubleWord(Command, Response48 | 13u);
            Assert.AreEqual(card.CardStatus.AsUInt32(), controller.ReadDoubleWord(Response67));
            Assert.AreEqual(13u, controller.ReadDoubleWord(CommandIndex));

            card.Reset();
            var expected = card.CardIdentification;
            controller.WriteDoubleWord(Command, Response136 | 2u);
            Assert.AreEqual(expected.AsUInt32(0), controller.ReadDoubleWord(Response01));
            Assert.AreEqual(expected.AsUInt32(32), controller.ReadDoubleWord(Response23));
            Assert.AreEqual(expected.AsUInt32(64), controller.ReadDoubleWord(Response45));
            Assert.AreEqual(expected.AsUInt32(96), controller.ReadDoubleWord(Response67));
        }

        [Test]
        public void ShouldReadSingleAndMultipleBlocksAndCompleteAtFinalByte()
        {
            AttachCard();
            controller.WriteDoubleWord(BlockLength, 64);
            controller.WriteDoubleWord(NumberOfBlocks, 2);
            controller.WriteDoubleWord(Argument, 0);
            controller.WriteDoubleWord(Command, DataRead | Response48 | 18u);

            CollectionAssert.AreEqual(image.Take(64).ToArray(), ReadBytes(64));
            Assert.AreEqual(1u, controller.ReadDoubleWord(NumberOfBlocksCounter));
            CollectionAssert.AreEqual(image.Skip(64).Take(64).ToArray(), ReadBytes(64));
            Assert.AreEqual(0u, controller.ReadDoubleWord(NumberOfBlocksCounter));
            Assert.AreEqual(0x1205u, controller.ReadDoubleWord(Status0) & 0x1205u);
            Assert.AreEqual(0x200u, controller.ReadDoubleWord(Status0));
        }

        [Test]
        public void ShouldIssueOneDmaPulsePerThirtyTwoOrSixtyFourByteChunk()
        {
            AttachCard();
            var pulses = 0;
            controller.RxDMARequest.AddStateChangedHook(value => { if(value) pulses++; });
            controller.WriteDoubleWord(BlockLength, 128);
            controller.WriteDoubleWord(Command, DmaDataRead | Response48 | 17u);
            Assert.AreEqual(1, pulses);
            ReadBytes(32);
            Assert.AreEqual(1, pulses);
            Advance();
            Assert.AreEqual(2, pulses);

            controller.Reset();
            pulses = 0;
            controller.WriteDoubleWord(BlockLength, 128);
            controller.WriteDoubleWord(FifoControl, 1u << 2);
            controller.WriteDoubleWord(Command, DmaDataRead | Response48 | 17u);
            Assert.AreEqual(1, pulses);
            ReadBytes(64);
            Advance();
            Assert.AreEqual(2, pulses);
        }

        [Test]
        public void ShouldCompleteFixedLengthCommandDataAndResetMidTransfer()
        {
            AttachCard();
            controller.WriteDoubleWord(Command, DataRead | Response48 | 6u);
            Assert.AreEqual(64, ReadBytes(64).Length);
            Assert.AreEqual(0u, controller.ReadDoubleWord(NumberOfBlocksCounter));
            Assert.AreEqual(0x1001u, controller.ReadDoubleWord(Status0) & 0x1001u);

            card.Reset();
            controller.WriteDoubleWord(Command, Response48 | 55u);
            controller.WriteDoubleWord(Command, DataRead | Response48 | 51u);
            Assert.AreEqual(8, ReadBytes(8).Length);
            Assert.AreEqual(0u, controller.ReadDoubleWord(NumberOfBlocksCounter));

            var pulses = 0;
            controller.RxDMARequest.AddStateChangedHook(value => { if(value) pulses++; });
            card.Reset();
            controller.WriteDoubleWord(BlockLength, 128);
            controller.WriteDoubleWord(Command, DmaDataRead | Response48 | 17u);
            ReadBytes(32);
            var beforeReset = pulses;
            controller.WriteDoubleWord(Control, 1);
            Advance();
            Assert.AreEqual(beforeReset, pulses);
            Assert.AreEqual(0u, controller.ReadDoubleWord(NumberOfBlocksCounter));
            Assert.AreEqual(0u, controller.ReadDoubleWord(Status0) & 0x1401u);
        }

        [Test]
        public void ShouldRejectWritesWithoutMutatingTheImage()
        {
            AttachCard(persistent: true);
            controller.WriteDoubleWord(Argument, 0);
            controller.WriteDoubleWord(Command, DataRead | DataWrite | Response48 | 24u);
            controller.WriteDoubleWord(DataTransmit, 0xDEADBEEF);
            Assert.AreNotEqual(0u, controller.ReadDoubleWord(Status0) & WriteCrcError);
            card.Dispose();
            card = null;
            CollectionAssert.AreEqual(image, File.ReadAllBytes(imagePath));
        }

        [Test]
        public void ShouldTransferCmd17ThroughEdmaChannel16WithoutReentrantLoss()
        {
            AttachCard();
            var dma = new TI_DA8xx_EDMA3CC(machine);
            var memory = new MappedMemory(machine, 0x1000);
            machine.SystemBus.Register(dma, new BusRangeRegistration(DmaAddress, 0x8000));
            machine.SystemBus.Register(memory, new BusRangeRegistration(MemoryAddress, 0x1000));
            controller.RxDMARequest.Connect(dma, 16);

            WriteParameter(dma, 16, (1u << 20) | (16u << 12) | (2u << 8) | (1u << 2) | 1u,
                ControllerAddress + (uint)DataReceive, 4, 8, MemoryAddress, 0, 4, 0xFFFF, 8, 0, 32, 16);
            dma.WriteDoubleWord(0x340, 1u << 16);
            dma.WriteDoubleWord(0x2030, 1u << 16);
            dma.WriteDoubleWord(0x2060, 1u << 16);

            controller.WriteDoubleWord(BlockLength, 512);
            controller.WriteDoubleWord(NumberOfBlocks, 1);
            controller.WriteDoubleWord(Command, DmaDataRead | Response48 | 17u);
            for(var i = 0; i < 20; i++) Advance();

            CollectionAssert.AreEqual(image.Take(512).ToArray(), memory.ReadBytes(0, 512));
            Assert.AreEqual(1u << 16, dma.ReadDoubleWord(0x2068));
            Assert.True(dma.IRQ.IsSet);
            Assert.AreEqual(0u, controller.ReadDoubleWord(NumberOfBlocksCounter));
        }

        private void AttachCard(bool persistent = false)
        {
            card = new SDCard(imagePath, ImageSize, persistent);
            controller.Register(card, NullRegistrationPoint.Instance);
        }

        private byte[] ReadBytes(int count)
        {
            var result = new byte[count];
            for(var i = 0; i < count; i++) result[i] = controller.ReadByte(DataReceive);
            return result;
        }

        private void Advance()
        {
            EmulationManager.Instance.CurrentEmulation.MasterTimeSource.Run(2);
        }

        private static void WriteParameter(TI_DA8xx_EDMA3CC dma, int channel, uint option,
            uint source, ushort aCount, ushort bCount, uint destination, short sourceBIndex,
            short destinationBIndex, ushort link, ushort reload, short sourceCIndex,
            short destinationCIndex, ushort cCount)
        {
            var offset = 0x4000 + channel * 0x20;
            dma.WriteDoubleWord(offset + 0x00, option);
            dma.WriteDoubleWord(offset + 0x04, source);
            dma.WriteDoubleWord(offset + 0x08, ((uint)bCount << 16) | aCount);
            dma.WriteDoubleWord(offset + 0x0C, destination);
            dma.WriteDoubleWord(offset + 0x10, ((uint)(ushort)destinationBIndex << 16) | (ushort)sourceBIndex);
            dma.WriteDoubleWord(offset + 0x14, ((uint)reload << 16) | link);
            dma.WriteDoubleWord(offset + 0x18, ((uint)(ushort)destinationCIndex << 16) | (ushort)sourceCIndex);
            dma.WriteDoubleWord(offset + 0x1C, cCount);
        }

        private Machine machine;
        private TI_DA8xx_MMCSD controller;
        private SDCard card;
        private string imagePath;
        private byte[] image;

        private const int ImageSize=4096;
        private const uint ControllerAddress=0x01C40000, DmaAddress=0x10000000, MemoryAddress=0x30000000;
        private const long Control=0x00, Clock=0x04, Status0=0x08, Status1=0x0C, InterruptMask=0x10;
        private const long ResponseTimeout=0x14, BlockLength=0x1C, NumberOfBlocks=0x20, NumberOfBlocksCounter=0x24;
        private const long DataReceive=0x28, DataTransmit=0x2C, Command=0x30, Argument=0x34;
        private const long Response01=0x38, Response23=0x3C, Response45=0x40, Response67=0x44, CommandIndex=0x50;
        private const long SDIOStatus0=0x68, SDIOInterruptEnable=0x6C, SDIOInterruptStatus=0x70, FifoControl=0x74;
        private const uint Response48=1u << 9, Response136=2u << 9, DataWrite=1u << 11, DataRead=1u << 13;
        private const uint DmaDataRead=DataRead | (1u << 16), ResponseTimeoutStatus=1u << 4, WriteCrcError=1u << 5;
    }
}
