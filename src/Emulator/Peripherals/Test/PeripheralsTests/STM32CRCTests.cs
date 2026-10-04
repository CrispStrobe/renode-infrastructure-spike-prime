// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors

using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.CRC;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class STM32CRCTests
    {
        [Test]
        public void ShouldUseFixedF4InputOrderWithoutOptionalReversalFields()
        {
            var crc = new STM32_CRC(STM32Series.F4);
            // CRC-32/MPEG-2: polynomial 0x04C11DB7, initial 0xFFFFFFFF,
            // no reflection/xor. Feed the MSB-first bytes 12 34 56 78.
            Assert.AreEqual(0xFFFFFFFFu, crc.ReadDoubleWord(Data));
            crc.WriteDoubleWord(Data, 0x12345678);
            Assert.AreEqual(0xDF8A8A2Bu, crc.ReadDoubleWord(Data));
            crc.WriteDoubleWord(Data, 0x90ABCDEF);
            Assert.AreEqual(0x36B13E20u, crc.ReadDoubleWord(Data));
            crc.WriteDoubleWord(Control, 1);
            Assert.AreEqual(0xFFFFFFFFu, crc.ReadDoubleWord(Data));
            crc.WriteDoubleWord(Data, 0x12345678);
            Assert.AreEqual(0xDF8A8A2Bu, crc.ReadDoubleWord(Data));
            crc.Reset();
            Assert.AreEqual(0xFFFFFFFFu, crc.ReadDoubleWord(Data));
            crc.WriteDoubleWord(Data, 0x12345678);
            Assert.AreEqual(0xDF8A8A2Bu, crc.ReadDoubleWord(Data));
        }

        [Test]
        public void ShouldAllowFirstF4DataWriteBeforeAnyExplicitResetOrRead()
        {
            var crc = new STM32_CRC(STM32Series.F4);
            crc.WriteDoubleWord(Data, 0x12345678);
            Assert.AreEqual(0xDF8A8A2Bu, crc.ReadDoubleWord(Data));
        }

        [Test]
        public void ShouldRetainFlexibleFamilyReversalModes(
            [Values(STM32Series.F0, STM32Series.WBA)] STM32Series series,
            [Values(0, 1, 2, 3)] int mode,
            [Values(false, true)] bool reverseOutput)
        {
            var crc = new STM32_CRC(series);
            crc.WriteDoubleWord(Control, 1u | ((uint)mode << 5) |
                (reverseOutput ? 0x80u : 0u));
            crc.WriteDoubleWord(Data, 0x12345678);
            // Independent MSB-first polynomial reference, with hardware REV_IN
            // grouping and optional bit-reversed output. No CRCEngine oracle.
            uint[] expected = reverseOutput
                ? new[] { 0xD45151FBu, 0xB5F6F167u, 0xDBBE3F34u, 0x5092782Du }
                : new[] { 0xDF8A8A2Bu, 0xE68F6FADu, 0x2CFC7DDBu, 0xB41E490Au };
            Assert.AreEqual(expected[mode], crc.ReadDoubleWord(Data));
            crc.Reset();
            crc.WriteDoubleWord(Data, 0x12345678);
            Assert.AreEqual(0xDF8A8A2Bu, crc.ReadDoubleWord(Data));
        }

        [Test]
        public void ShouldRetainFlexibleByteAndWordAccess(
            [Values(STM32Series.F0, STM32Series.WBA)] STM32Series series)
        {
            var crc = new STM32_CRC(series);
            crc.WriteWord(Data, 0x1234);
            crc.WriteByte(Data, 0x56);
            Assert.AreEqual(0xBE988657u, crc.ReadDoubleWord(Data));
            crc.Reset();
            foreach(var value in new byte[] { 0x31, 0x32, 0x33, 0x34, 0x35,
                                             0x36, 0x37, 0x38, 0x39 })
                crc.WriteByte(Data, value);
            // Standard CRC-32/MPEG-2 check vector "123456789".
            Assert.AreEqual(0x0376E6E7u, crc.ReadDoubleWord(Data));
        }

        private const long Data = 0x0;
        private const long Control = 0x8;
    }
}
