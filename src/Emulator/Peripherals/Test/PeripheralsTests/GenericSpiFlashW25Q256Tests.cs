//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.SPI;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class GenericSpiFlashW25Q256Tests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            memory = new MappedMemory(machine, FlashSize);
            flash = new GenericSpiFlash(memory, 0xEF, 0x40, 0x19,
                writeStatusCanSetWriteEnable: false, sectorSizeKB: 64,
                secondaryStatusRegisterReadCommand: 0x35);
        }

        [TearDown]
        public void TearDown()
        {
            memory.Dispose();
            machine.Dispose();
        }

        [Test]
        public void ShouldReportW25Q256IdentityAndIdleStatus()
        {
            AssertCommand(0x9F, new byte[] { 0xEF, 0x40, 0x19 });
            AssertCommand(0x05, new byte[] { 0x00 });
            AssertCommand(0x35, new byte[] { 0x00 });
        }

        [Test]
        public void ShouldTrackAndConsumeWriteEnableLatch()
        {
            Execute(0x06);
            AssertCommand(0x05, new byte[] { 0x02 });

            Execute(0x12, AddressBytes(TestAddress), new byte[] { 0xA5 });

            AssertCommand(0x05, new byte[] { 0x00 });
            Assert.AreEqual(0xA5, memory.ReadByte(TestAddress));
        }

        [Test]
        public void ShouldFastReadAcrossFourByteAddressWithDummyCycle()
        {
            memory.WriteBytes(TestAddress, new byte[] { 0x11, 0x22, 0x33 });

            Begin(0x0C, AddressBytes(TestAddress), new byte[] { 0xFF });
            var result = Clock(3);
            flash.FinishTransmission();

            CollectionAssert.AreEqual(new byte[] { 0x11, 0x22, 0x33 }, result);
        }

        [Test]
        public void ShouldApplyNorProgrammingRulesAndRequireWriteEnable()
        {
            memory.WriteByte(TestAddress, 0x0F);
            Execute(0x12, AddressBytes(TestAddress), new byte[] { 0xF0 });
            Assert.AreEqual(0x0F, memory.ReadByte(TestAddress));

            Execute(0x06);
            Execute(0x12, AddressBytes(TestAddress), new byte[] { 0xF0 });
            Assert.AreEqual(0x00, memory.ReadByte(TestAddress));
        }

        [Test]
        public void ShouldEraseFourByteSectorAndBlockAndRemainImmediatelyIdle()
        {
            memory.WriteByte(TestAddress, 0x00);
            Execute(0x06);
            Execute(0x21, AddressBytes(TestAddress));
            Assert.AreEqual(0xFF, memory.ReadByte(TestAddress));
            AssertCommand(0x05, new byte[] { 0x00 });

            memory.WriteByte(TestAddress, 0x00);
            Execute(0x06);
            Execute(0xDC, AddressBytes(TestAddress));
            Assert.AreEqual(0xFF, memory.ReadByte(TestAddress));
            AssertCommand(0x05, new byte[] { 0x00 });
        }

        [Test]
        public void ShouldRequireWriteEnableForChipErase()
        {
            memory.WriteByte(TestAddress, 0x00);
            Execute(0xC7);
            Assert.AreEqual(0x00, memory.ReadByte(TestAddress));

            Execute(0x06);
            Execute(0xC7);
            Assert.AreEqual(0xFF, memory.ReadByte(TestAddress));
            AssertCommand(0x05, new byte[] { 0x00 });
        }

        [Test]
        public void ShouldInjectAndRecoverFromWholeProgramOperationFailure()
        {
            memory.WriteBytes(TestAddress, new byte[] { 0xFF, 0xFF });
            flash.FailNextProgramOperations = 1;

            Execute(0x06);
            Execute(0x12, AddressBytes(TestAddress), new byte[] { 0xA5, 0x5A });

            CollectionAssert.AreEqual(new byte[] { 0xFF, 0xFF }, memory.ReadBytes(TestAddress, 2));
            Assert.AreEqual(0, flash.FailNextProgramOperations);
            Assert.AreEqual(1, flash.InjectedProgramFailures);
            AssertCommand(0x05, new byte[] { 0x00 });

            Execute(0x06);
            Execute(0x12, AddressBytes(TestAddress), new byte[] { 0xA5, 0x5A });
            CollectionAssert.AreEqual(new byte[] { 0xA5, 0x5A }, memory.ReadBytes(TestAddress, 2));
            Assert.AreEqual(1, flash.InjectedProgramFailures);
        }

        [Test]
        public void ShouldInjectAndRecoverFromSegmentAndChipEraseFailures()
        {
            memory.WriteByte(TestAddress, 0x00);
            flash.FailNextEraseOperations = 2;

            Execute(0x06);
            Execute(0x21, AddressBytes(TestAddress));
            Assert.AreEqual(0x00, memory.ReadByte(TestAddress));
            Execute(0x06);
            Execute(0xC7);
            Assert.AreEqual(0x00, memory.ReadByte(TestAddress));
            Assert.AreEqual(0, flash.FailNextEraseOperations);
            Assert.AreEqual(2, flash.InjectedEraseFailures);

            Execute(0x06);
            Execute(0x21, AddressBytes(TestAddress));
            Assert.AreEqual(0xFF, memory.ReadByte(TestAddress));
            Assert.AreEqual(2, flash.InjectedEraseFailures);
        }

        private void AssertCommand(byte command, byte[] expected)
        {
            Begin(command);
            CollectionAssert.AreEqual(expected, Clock(expected.Length));
            flash.FinishTransmission();
        }

        private void Execute(byte command, params byte[][] segments)
        {
            Begin(command, segments);
            flash.FinishTransmission();
        }

        private void Begin(byte command, params byte[][] segments)
        {
            flash.Transmit(command);
            foreach(var segment in segments)
            {
                foreach(var value in segment)
                {
                    flash.Transmit(value);
                }
            }
        }

        private byte[] Clock(int count)
        {
            var result = new byte[count];
            for(var i = 0; i < count; ++i)
            {
                result[i] = flash.Transmit(0xFF);
            }
            return result;
        }

        private static byte[] AddressBytes(long address)
        {
            return new byte[]
            {
                (byte)(address >> 24), (byte)(address >> 16),
                (byte)(address >> 8), (byte)address,
            };
        }

        private Machine machine;
        private MappedMemory memory;
        private GenericSpiFlash flash;

        private const int FlashSize = 32 * 1024 * 1024;
        private const int TestAddress = 0x01001010;
    }
}
