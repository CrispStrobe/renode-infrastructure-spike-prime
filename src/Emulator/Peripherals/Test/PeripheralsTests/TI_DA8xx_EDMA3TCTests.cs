//
// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
//
using Antmicro.Renode.Peripherals.DMA;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class TI_DA8xx_EDMA3TCTests
    {
        [TestCase(0x212u)]
        [TestCase(0x213u)]
        public void ShouldExposeExactReadOnlyIdentityAndConfiguration(uint configuration)
        {
            var tc = new TI_DA8xx_EDMA3TC(configuration);
            Assert.AreEqual(0x40003B00u, tc.ReadDoubleWord(0));
            Assert.AreEqual(configuration, tc.ReadDoubleWord(4));
            Assert.AreEqual(0u, tc.ReadDoubleWord(0x100));
            Assert.False(tc.IRQ.IsSet);
        }

        [Test]
        public void ShouldGateAndClearErrorsWithDocumentedMask()
        {
            var tc = new TI_DA8xx_EDMA3TC();
            tc.ReportError(0xFu, 0x1234);
            Assert.AreEqual(0xDu, tc.ReadDoubleWord(0x120));
            Assert.AreEqual(0x1234u, tc.ReadDoubleWord(0x12C));
            Assert.False(tc.IRQ.IsSet);
            tc.WriteDoubleWord(0x124, 0xFu);
            Assert.AreEqual(0xDu, tc.ReadDoubleWord(0x124));
            Assert.True(tc.IRQ.IsSet);
            tc.WriteDoubleWord(0x128, 0x5);
            Assert.AreEqual(8u, tc.ReadDoubleWord(0x120));
            tc.WriteDoubleWord(0x128, 8);
            Assert.False(tc.IRQ.IsSet);
        }
    }
}
