//
// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
//
using Antmicro.Renode.Peripherals.Miscellaneous;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class TI_DA8xx_SYSCFG0Tests
    {
        [Test]
        public void ShouldExposeExactIdentificationAndResetValues()
        {
            var syscfg = new TI_DA8xx_SYSCFG0(0x12345678);
            Assert.AreEqual(0x4E840102u, syscfg.ReadDoubleWord(0));
            Assert.AreEqual(0x1B7D102Fu, syscfg.ReadDoubleWord(0x18));
            Assert.AreEqual(0x12345678u, syscfg.ReadDoubleWord(0x20));
            Assert.AreEqual(4u, syscfg.ReadDoubleWord(0x24));
            Assert.AreEqual(uint.MaxValue, syscfg.ReadDoubleWord(0x170));
            Assert.AreEqual(0xFF00u, syscfg.ReadDoubleWord(0x188));
            // CFGCHIP4/AMUTECLR is a write-only action register.
            Assert.AreEqual(0u, syscfg.ReadDoubleWord(0x18C));
            syscfg.WriteDoubleWord(0x18C, uint.MaxValue);
            Assert.AreEqual(0u, syscfg.ReadDoubleWord(0x18C));
        }

        [Test]
        public void ShouldAllowPinMuxWritesRegardlessOfKickOnRevisionTwo()
        {
            var syscfg = new TI_DA8xx_SYSCFG0();
            syscfg.WriteDoubleWord(0x38, 0xDEADBEEF);
            syscfg.WriteDoubleWord(0x3C, 0);
            for(var i = 0; i < 20; i++)
            {
                syscfg.WriteDoubleWord(0x120 + 4 * i, (uint)(i + 1));
                Assert.AreEqual((uint)(i + 1), syscfg.ReadDoubleWord(0x120 + 4 * i));
            }
            Assert.AreEqual(0u, syscfg.ReadDoubleWord(0x38));
            syscfg.WriteDoubleWord(0x38, 0x83E70B13);
            syscfg.WriteDoubleWord(0x3C, 0x95A4F1E0);
            Assert.AreEqual(0u, syscfg.ReadDoubleWord(0x38));
            Assert.AreEqual(0u, syscfg.ReadDoubleWord(0x3C));
            syscfg.WriteDoubleWord(0x120, 0xA5A5A5A5);
            Assert.AreEqual(0xA5A5A5A5u, syscfg.ReadDoubleWord(0x120));
        }

        [Test]
        public void ShouldImplementChipSignalSetAndClear()
        {
            var syscfg = new TI_DA8xx_SYSCFG0();
            syscfg.WriteDoubleWord(0x174, 0x101);
            syscfg.WriteDoubleWord(0x174, 0x10);
            Assert.AreEqual(0x111u, syscfg.ReadDoubleWord(0x174));
            syscfg.WriteDoubleWord(0x178, 0x100);
            Assert.AreEqual(0x11u, syscfg.ReadDoubleWord(0x174));
            Assert.AreEqual(0u, syscfg.ReadDoubleWord(0x178));
        }

        [Test]
        public void ShouldExposeExactPostBootloaderPinConfiguration()
        {
            var syscfg = new TI_DA8xx_SYSCFG0(postBootloaderState: true);
            Assert.AreEqual(0x1101u, syscfg.ReadDoubleWord(0x12C));
            Assert.AreEqual(0x22002210u, syscfg.ReadDoubleWord(0x130));
            Assert.AreEqual(0x00222222u, syscfg.ReadDoubleWord(0x148));
            Assert.AreEqual(0xF7D6FFFFu, syscfg.ReadDoubleWord(0x170));
            syscfg.WriteDoubleWord(0x12C, 0);
            syscfg.Reset();
            Assert.AreEqual(0x1101u, syscfg.ReadDoubleWord(0x12C));
        }
    }
}
