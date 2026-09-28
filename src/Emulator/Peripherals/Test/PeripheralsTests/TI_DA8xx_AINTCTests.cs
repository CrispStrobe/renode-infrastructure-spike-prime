//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Peripherals.IRQControllers;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class TI_DA8xx_AINTCTests
    {
        [SetUp]
        public void SetUp()
        {
            aintc = new TI_DA8xx_AINTC();
        }

        [Test]
        public void ShouldReportAM1808RevisionAndResetState()
        {
            Assert.AreEqual(0x4E82A900u, aintc.ReadDoubleWord(Revision));
            Assert.AreEqual(NonePending, aintc.ReadDoubleWord(GlobalPrioritizedIndex));
            Assert.False(aintc.IRQ.IsSet);
            Assert.False(aintc.FIQ.IsSet);
        }

        [Test]
        public void ShouldRouteLatchedSystemEventToIrqAndAcknowledgeIt()
        {
            // Event 25 is UART1 in AM1808. Map it to channel/host IRQ (1).
            aintc.WriteDoubleWord(ChannelMap6, 1u << 8);
            aintc.WriteDoubleWord(EnableIndexedSet, 25);
            aintc.WriteDoubleWord(HostEnableIndexedSet, 1);
            aintc.WriteDoubleWord(GlobalEnable, 1);

            aintc.OnGPIO(25, true);
            aintc.OnGPIO(25, false);

            Assert.True(aintc.IRQ.IsSet);
            Assert.False(aintc.FIQ.IsSet);
            Assert.AreEqual(25, aintc.ReadDoubleWord(HostPrioritizedIndex2));
            Assert.AreEqual(1u << 25, aintc.ReadDoubleWord(SystemRawStatus1));

            aintc.WriteDoubleWord(StatusIndexedClear, 25);
            Assert.False(aintc.IRQ.IsSet);
            Assert.AreEqual(NonePending, aintc.ReadDoubleWord(HostPrioritizedIndex2));
        }

        [Test]
        public void ShouldHonorGlobalSystemAndHostEnables()
        {
            aintc.WriteDoubleWord(ChannelMap1, 1u << 8); // event 5 -> IRQ
            aintc.OnGPIO(5, true);
            Assert.False(aintc.IRQ.IsSet);

            aintc.WriteDoubleWord(EnableSet1, 1u << 5);
            aintc.WriteDoubleWord(HostEnable, 2);
            Assert.False(aintc.IRQ.IsSet);

            aintc.WriteDoubleWord(GlobalEnable, 1);
            Assert.True(aintc.IRQ.IsSet);

            aintc.WriteDoubleWord(EnableClear1, 1u << 5);
            Assert.False(aintc.IRQ.IsSet);
        }

        [Test]
        public void ShouldPrioritizeLowerChannelAndCalculateVector()
        {
            // event 40 -> channel 7, event 41 -> channel 1
            aintc.WriteDoubleWord(ChannelMap10, 7u | (1u << 8));
            aintc.WriteDoubleWord(EnableIndexedSet, 40);
            aintc.WriteDoubleWord(EnableIndexedSet, 41);
            aintc.OnGPIO(40, true);
            aintc.OnGPIO(41, true);
            aintc.WriteDoubleWord(VectorBase, 0x80000000);
            aintc.WriteDoubleWord(VectorSize, 3); // 32-byte slots

            Assert.AreEqual(41, aintc.ReadDoubleWord(GlobalPrioritizedIndex));
            Assert.AreEqual(0x80000520u, aintc.ReadDoubleWord(GlobalPrioritizedVector));
        }

        private TI_DA8xx_AINTC aintc;

        private const uint NonePending = 1u << 31;
        private const long Revision = 0x000;
        private const long GlobalEnable = 0x010;
        private const long StatusIndexedClear = 0x024;
        private const long EnableIndexedSet = 0x028;
        private const long HostEnableIndexedSet = 0x034;
        private const long GlobalPrioritizedIndex = 0x080;
        private const long GlobalPrioritizedVector = 0x084;
        private const long SystemRawStatus1 = 0x200;
        private const long EnableSet1 = 0x300;
        private const long EnableClear1 = 0x380;
        private const long ChannelMap1 = 0x404;
        private const long ChannelMap6 = 0x418;
        private const long ChannelMap10 = 0x428;
        private const long HostPrioritizedIndex2 = 0x904;
        private const long HostEnable = 0x1500;
        private const long VectorBase = 0x050;
        private const long VectorSize = 0x054;
    }
}
