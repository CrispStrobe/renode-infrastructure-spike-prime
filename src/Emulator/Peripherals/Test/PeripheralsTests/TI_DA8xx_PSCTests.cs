//
// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
//
using System;
using Antmicro.Renode.Peripherals.Miscellaneous;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class TI_DA8xx_PSCTests
    {
        [Test]
        public void ShouldExposeExactPowerOnResetState()
        {
            var psc = new TI_DA8xx_PSC(16, powerDomain1Module: 15);
            Assert.AreEqual(0x44825A00u, psc.ReadDoubleWord(0));
            Assert.AreEqual(0x001FF101u, psc.ReadDoubleWord(0x300));
            Assert.AreEqual(0u, psc.ReadDoubleWord(0x200));
            Assert.AreEqual(0xB00u, psc.ReadDoubleWord(0x800));
            Assert.AreEqual(0u, psc.ReadDoubleWord(0xA00));
            Assert.AreEqual(0u, psc.ReadDoubleWord(0x128));
            Assert.AreEqual(0u, psc.ReadDoubleWord(0x120));
        }

        [Test]
        public void ShouldCompleteModuleTransitionsSynchronously()
        {
            var psc = new TI_DA8xx_PSC(16);
            psc.WriteDoubleWord(0xA00 + 6 * 4, 3);
            psc.WriteDoubleWord(0x120, 1);
            Assert.AreEqual(0x1E03u, psc.ReadDoubleWord(0x800 + 6 * 4));
            Assert.AreEqual(0u, psc.ReadDoubleWord(0x128));

            psc.WriteDoubleWord(0xA00 + 6 * 4, 1);
            psc.WriteDoubleWord(0x120, 1);
            Assert.AreEqual(0x1A01u, psc.ReadDoubleWord(0x800 + 6 * 4));

            psc.WriteDoubleWord(0xA00 + 6 * 4, 2);
            psc.WriteDoubleWord(0x120, 1);
            Assert.AreEqual(0xA02u, psc.ReadDoubleWord(0x800 + 6 * 4));
        }

        [Test]
        public void ShouldApplyLocalResetImmediatelyAndIsolatePowerDomains()
        {
            var psc = new TI_DA8xx_PSC(16, powerDomain1Module: 15);
            psc.WriteDoubleWord(0xA00 + 15 * 4, 0x103);
            Assert.AreEqual(0xB00u, psc.ReadDoubleWord(0x800 + 14 * 4));
            Assert.AreEqual(0xB00u, psc.ReadDoubleWord(0x800 + 15 * 4));
            psc.WriteDoubleWord(0x120, 2);
            Assert.AreEqual(0x1F03u, psc.ReadDoubleWord(0x800 + 15 * 4));
            Assert.AreEqual(0xB00u, psc.ReadDoubleWord(0x800 + 14 * 4));
        }

        [Test]
        public void ShouldExposeConfiguredPostBootloaderStateAndResetBackToIt()
        {
            var psc = new TI_DA8xx_PSC(16, (1L << 6) | (1L << 7) | (1L << 14), 15, true);
            Assert.AreEqual(0x301u, psc.ReadDoubleWord(0x200));
            Assert.AreEqual(0x1E03u, psc.ReadDoubleWord(0x800 + 14 * 4));
            Assert.AreEqual(0xB00u, psc.ReadDoubleWord(0x800 + 15 * 4));
            psc.WriteDoubleWord(0xA00 + 14 * 4, 0);
            psc.Reset();
            Assert.AreEqual(3u, psc.ReadDoubleWord(0xA00 + 14 * 4));
        }

        [Test]
        public void ShouldRejectImpossibleModuleConfigurations()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TI_DA8xx_PSC(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TI_DA8xx_PSC(33));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TI_DA8xx_PSC(16, powerDomain1Module: 16));
        }
    }
}
