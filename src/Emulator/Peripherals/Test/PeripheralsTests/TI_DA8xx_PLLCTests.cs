//
// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
//
using Antmicro.Renode.Peripherals.Miscellaneous;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class TI_DA8xx_PLLCTests
    {
        [Test]
        public void ShouldExposePrimaryPowerOnResetState()
        {
            var pll = new TI_DA8xx_PLLC(true);
            Assert.AreEqual(0x44813C00u, pll.ReadDoubleWord(0));
            Assert.AreEqual(0x72u, pll.ReadDoubleWord(0x100));
            Assert.AreEqual(0x13u, pll.ReadDoubleWord(0x110));
            Assert.AreEqual(0x8000u, pll.ReadDoubleWord(0x114));
            Assert.AreEqual(0x8001u, pll.ReadDoubleWord(0x11C));
            Assert.AreEqual(0x8005u, pll.ReadDoubleWord(0x16C));
            Assert.AreEqual(0u, pll.ReadDoubleWord(0x13C));
            Assert.AreEqual(0x7Fu, pll.ReadDoubleWord(0x150));
        }

        [Test]
        public void ShouldExposeSecondaryMapAndRejectPrimaryOnlyRegisters()
        {
            var pll = new TI_DA8xx_PLLC(false);
            Assert.AreEqual(0x44814400u, pll.ReadDoubleWord(0));
            Assert.AreEqual(0u, pll.ReadDoubleWord(0x114));
            Assert.AreEqual(2u, pll.ReadDoubleWord(0x120));
            pll.WriteDoubleWord(0x114, uint.MaxValue);
            pll.WriteDoubleWord(0x160, uint.MaxValue);
            Assert.AreEqual(0u, pll.ReadDoubleWord(0x114));
            Assert.AreEqual(0u, pll.ReadDoubleWord(0x160));
        }

        [Test]
        public void ShouldTrackDividerChangesAndCompleteGoSynchronously()
        {
            var pll = new TI_DA8xx_PLLC(true);
            pll.WriteDoubleWord(0x118, 0x8004);
            Assert.AreEqual(1u, pll.ReadDoubleWord(0x144));
            pll.WriteDoubleWord(0x11C, 0x8003);
            Assert.AreEqual(3u, pll.ReadDoubleWord(0x144));
            Assert.AreEqual(0x8003u, pll.ReadDoubleWord(0x11C));
            Assert.AreEqual(0u, pll.ReadDoubleWord(0x13C) & 1u);
            pll.WriteDoubleWord(0x138, 1);
            Assert.AreEqual(0u, pll.ReadDoubleWord(0x144));
            Assert.AreEqual(0u, pll.ReadDoubleWord(0x138));
        }

        [Test]
        public void ShouldDeriveClockAndSystemStatus()
        {
            var pll = new TI_DA8xx_PLLC(true);
            pll.WriteDoubleWord(0x148, 1);
            Assert.AreEqual(1u, pll.ReadDoubleWord(0x14C));
            pll.WriteDoubleWord(0x118, 4);
            Assert.AreEqual(0x7Eu, pll.ReadDoubleWord(0x150));
        }

        [Test]
        public void ShouldExposeProvenPrimaryPostBootloaderState()
        {
            var pll = new TI_DA8xx_PLLC(true, true);
            Assert.AreEqual(0x18u, pll.ReadDoubleWord(0x110));
            Assert.AreEqual(0x8000u, pll.ReadDoubleWord(0x114));
            Assert.AreEqual(0x8001u, pll.ReadDoubleWord(0x128));
            Assert.AreEqual(0x8001u, pll.ReadDoubleWord(0x11C));
            Assert.AreEqual(0x8000u, pll.ReadDoubleWord(0x168));
            Assert.AreEqual(4u, pll.ReadDoubleWord(0x13C));
            Assert.AreEqual(1u, pll.ReadDoubleWord(0x100) & 1u);
            Assert.AreEqual(0u, pll.ReadDoubleWord(0x100) & 2u);
            Assert.AreEqual(8u, pll.ReadDoubleWord(0x100) & 8u);
        }
    }
}
