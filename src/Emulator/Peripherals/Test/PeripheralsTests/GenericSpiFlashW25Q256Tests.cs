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

        // Winbond W25Q256JV Rev. I, sections 8.2.8/8.2.9/8.2.12:
        // 0Bh follows B7h/E9h addressing mode and still takes one dummy byte.
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void ShouldFastReadUsingSelectedAddressMode(int modeTransitions)
        {
            memory.WriteBytes(0x001010, new byte[] { 0x31, 0x42, 0x53 });
            memory.WriteBytes(0x01001010, new byte[] { 0xA6, 0xB7, 0xC8 });
            // Decoy where a three-byte decoder would land on the high address.
            memory.WriteBytes(0x010010, new byte[] { 0xD1, 0xE2, 0xF3 });

            if(modeTransitions >= 1) Execute(0xB7);
            if(modeTransitions == 2) Execute(0xE9);

            var fourByteMode = modeTransitions == 1;
            var address = fourByteMode
                ? new byte[] { 0x01, 0x00, 0x10, 0x10 }
                : new byte[] { 0x00, 0x10, 0x10 };
            var expected = fourByteMode
                ? new byte[] { 0xA6, 0xB7, 0xC8 }
                : new byte[] { 0x31, 0x42, 0x53 };

            Begin(0x0B, address, new byte[] { 0xFF });
            var result = Clock(expected.Length);
            flash.FinishTransmission();

            CollectionAssert.AreEqual(expected, result);
        }

        // Unlike 0Bh, dedicated 0Ch always takes four address bytes.
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void ShouldKeepDedicatedFourByteFastReadIndependentOfAddressMode(int modeTransitions)
        {
            memory.WriteBytes(0x01001010, new byte[] { 0xA6, 0xB7, 0xC8 });
            memory.WriteBytes(0x010010, new byte[] { 0xD1, 0xE2, 0xF3 });

            if(modeTransitions >= 1) Execute(0xB7);
            if(modeTransitions == 2) Execute(0xE9);

            Begin(0x0C, new byte[] { 0x01, 0x00, 0x10, 0x10 }, new byte[] { 0xFF });
            var result = Clock(3);
            flash.FinishTransmission();

            CollectionAssert.AreEqual(new byte[] { 0xA6, 0xB7, 0xC8 }, result);
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
