//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Peripherals.SD;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class SDCardTests
    {
        [TestCase(0x000001AAu, 0x000001AAu)]
        [TestCase(0xFFFFF15Au, 0x0000015Au)]
        public void ShouldReturnInterfaceConditionInNativeMode(uint argument, uint expectedResponse)
        {
            using(var card = new SDCard(CardCapacity))
            {
                var response = card.HandleCommand(SendInterfaceConditionCommand, argument);

                Assert.AreEqual(32u, response.Length);
                Assert.AreEqual(expectedResponse, response.AsUInt32());
            }
        }

        [Test]
        public void ShouldKeepSpiInterfaceConditionResponseFraming()
        {
            using(var card = new SDCard(CardCapacity, spiMode: true))
            {
                var response = card.HandleCommand(SendInterfaceConditionCommand, 0xFFFFF15Au);

                Assert.AreEqual(40u, response.Length);
                CollectionAssert.AreEqual(new byte[] { 0x01, 0x00, 0x00, 0x01, 0x5A }, response.AsByteArray());
            }
        }

        [Test]
        public void ShouldNotChangeStatusResponseSemantics()
        {
            using(var nativeCard = new SDCard(CardCapacity))
            using(var spiCard = new SDCard(CardCapacity, spiMode: true))
            {
                var nativeStatus = nativeCard.HandleCommand(SendStatusCommand, 0);
                var spiStatus = spiCard.HandleCommand(SendStatusCommand, 0);

                Assert.AreEqual(32u, nativeStatus.Length);
                Assert.AreEqual(nativeCard.CardStatus.AsUInt32(), nativeStatus.AsUInt32());
                Assert.AreEqual(16u, spiStatus.Length);
                CollectionAssert.AreEqual(new byte[] { 0x01, 0x00 }, spiStatus.AsByteArray());
            }
        }

        private const uint SendInterfaceConditionCommand = 8;
        private const uint SendStatusCommand = 13;
        private const int CardCapacity = 1024 * 1024;
    }
}
