//
// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.IRQControllers;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class TI_DA8xx_GPIOTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            gpio = new TI_DA8xx_GPIO(machine);
        }

        [TearDown]
        public void TearDown()
        {
            machine.Dispose();
        }

        [Test]
        public void ShouldExposeNonDestructiveByteAndWordReadsThroughSystemBus()
        {
            const ulong address = 0x01E26000;
            machine.SystemBus.Register(gpio, new BusRangeRegistration(address, (ulong)gpio.Size));
            gpio.OnGPIO(43, true);
            gpio.OnGPIO(44, true);
            gpio.OnGPIO(55, true);
            CollectionAssert.AreEqual(new byte[] { 0x05, 0x01, 0x83, 0x44 }, machine.SystemBus.ReadBytes(address, 4));
            CollectionAssert.AreEqual(new byte[] { 0x00, 0x18, 0x80, 0x00 }, machine.SystemBus.ReadBytes(address + 0x48, 4));
            Assert.AreEqual(0x1800, machine.SystemBus.ReadWord(address + 0x48));
            Assert.AreEqual(0x0080, machine.SystemBus.ReadWord(address + 0x4A));
            gpio.WriteDoubleWord(0x4C, (1u << 11) | (1u << 23));
            gpio.OnGPIO(43, false);
            gpio.OnGPIO(43, true);
            gpio.OnGPIO(55, false);
            gpio.OnGPIO(55, true);
            CollectionAssert.AreEqual(new byte[] { 0x00, 0x08, 0x80, 0x00 }, machine.SystemBus.ReadBytes(address + 0x5C, 4));
            Assert.AreEqual(0x00800800u, gpio.ReadDoubleWord(0x5C));
        }

        [Test]
        public void ShouldKeepNarrowWritesWithinTheirLaneForAliasesAndW1C()
        {
            const ulong address = 0x01E26000;
            machine.SystemBus.Register(gpio, new BusRangeRegistration(address, (ulong)gpio.Size));
            gpio.WriteDoubleWord(0x38, 0);
            gpio.WriteDoubleWord(0x3C, 0x00800800);
            machine.SystemBus.WriteByte(address + 0x45, 0x08); // CLR_DATA lane 1: GPIO43 only.
            Assert.AreEqual(0x00800000u, gpio.ReadDoubleWord(0x3C));
            machine.SystemBus.WriteWord(address + 0x3C, 0x1800); // Preserve upper output lane.
            Assert.AreEqual(0x00801800u, gpio.ReadDoubleWord(0x3C));
            gpio.WriteDoubleWord(0x4C, 0x00800800);
            gpio.WriteDoubleWord(0x44, 0x00800800);
            gpio.WriteDoubleWord(0x40, 0x00800800);
            machine.SystemBus.WriteByte(address + 0x5D, 0x08); // W1C GPIO43, preserve GPIO55.
            Assert.AreEqual(0x00800000u, gpio.ReadDoubleWord(0x5C));
            machine.SystemBus.WriteWord(address + 0x5E, 0x0080);
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x5C));
        }

        [Test]
        public void ShouldExposeExactResetMapAndMasks()
        {
            Assert.AreEqual(0x44830105u, gpio.ReadDoubleWord(0x00));
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x08));
            Assert.AreEqual(0xFFFFFFFFu, gpio.ReadDoubleWord(0x10));
            Assert.AreEqual(0xFFFFFFFFu, gpio.ReadDoubleWord(0x38));
            Assert.AreEqual(0xFFFFFFFFu, gpio.ReadDoubleWord(0x60));
            Assert.AreEqual(0xFFFFFFFFu, gpio.ReadDoubleWord(0x88));
            Assert.AreEqual(0xFFFFu, gpio.ReadDoubleWord(0xB0));
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0xB4));
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0xC0));
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0xD4));
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x04));
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0xD8));
            Assert.AreEqual(0x1000, gpio.Size);

            gpio.WriteDoubleWord(0x08, 0xFFFFFFFF);
            gpio.WriteDoubleWord(0xB0, 0);
            gpio.WriteDoubleWord(0xB8, 0xFFFFFFFF);
            Assert.AreEqual(0x1FFu, gpio.ReadDoubleWord(0x08));
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0xB0));
            Assert.AreEqual(0xFFFFu, gpio.ReadDoubleWord(0xB4));
        }

        [Test]
        public void ShouldIgnoreOutputWritesWhileInputAndDriveConfiguredOutputs()
        {
            var pin43 = 1u << 11; // GPIO43 is group 1 bit 11.
            gpio.WriteDoubleWord(0x40, pin43);
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x3C) & pin43);
            Assert.False(gpio.Connections[43].IsSet);

            gpio.WriteDoubleWord(0x38, 0xFFFFFFFFu & ~pin43);
            gpio.WriteDoubleWord(0x40, pin43);
            Assert.AreEqual(pin43, gpio.ReadDoubleWord(0x3C) & pin43);
            Assert.AreEqual(pin43, gpio.ReadDoubleWord(0x40) & pin43);
            Assert.AreEqual(pin43, gpio.ReadDoubleWord(0x44) & pin43);
            Assert.AreEqual(pin43, gpio.ReadDoubleWord(0x48) & pin43);
            Assert.True(gpio.Connections[43].IsSet);

            gpio.WriteDoubleWord(0x44, pin43);
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x3C) & pin43);
            Assert.False(gpio.Connections[43].IsSet);
            gpio.WriteDoubleWord(0x3C, pin43);
            Assert.True(gpio.Connections[43].IsSet);
            gpio.WriteDoubleWord(0x3C, 0);
            Assert.False(gpio.Connections[43].IsSet);
        }

        [Test]
        public void ShouldReadInjectedInputsAndPreserveOutputLatchAcrossDirectionChanges()
        {
            var pin44 = 1u << 12;
            gpio.OnGPIO(44, true);
            Assert.AreEqual(pin44, gpio.ReadDoubleWord(0x48) & pin44);
            Assert.True(gpio.Connections[44].IsSet);

            gpio.WriteDoubleWord(0x38, 0xFFFFFFFFu & ~pin44);
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x48) & pin44);
            gpio.WriteDoubleWord(0x40, pin44);
            Assert.AreEqual(pin44, gpio.ReadDoubleWord(0x48) & pin44);
            gpio.OnGPIO(44, false); // An external level cannot override a push-pull output.
            Assert.AreEqual(pin44, gpio.ReadDoubleWord(0x48) & pin44);
            gpio.WriteDoubleWord(0x38, 0xFFFFFFFF);
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x48) & pin44);

            gpio.WriteDoubleWord(0x40, pin44); // Ignored while input; latch remains high.
            gpio.WriteDoubleWord(0x38, 0xFFFFFFFFu & ~pin44);
            Assert.AreEqual(pin44, gpio.ReadDoubleWord(0x48) & pin44);
        }

        [Test]
        public void ShouldLatchRisingAndFallingEdgesAndReadSameStateThroughSetAndClearRegisters()
        {
            var pin80 = 1u << 16; // Bank 5 pin 0, group 2 bit 16.
            gpio.WriteDoubleWord(0x74, pin80);
            Assert.AreEqual(pin80, gpio.ReadDoubleWord(0x74));
            Assert.AreEqual(pin80, gpio.ReadDoubleWord(0x78));
            gpio.OnGPIO(80, true);
            Assert.AreEqual(pin80, gpio.ReadDoubleWord(0x84));

            gpio.WriteDoubleWord(0x7C, pin80);
            Assert.AreEqual(pin80, gpio.ReadDoubleWord(0x7C));
            Assert.AreEqual(pin80, gpio.ReadDoubleWord(0x80));
            gpio.WriteDoubleWord(0x84, pin80);
            gpio.OnGPIO(80, false);
            Assert.AreEqual(pin80, gpio.ReadDoubleWord(0x84));

            gpio.WriteDoubleWord(0x78, pin80);
            gpio.WriteDoubleWord(0x80, pin80);
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x74));
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x7C));
            gpio.WriteDoubleWord(0x84, pin80);
            gpio.OnGPIO(80, true);
            gpio.OnGPIO(80, false);
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x84));
        }

        [Test]
        public void ShouldGateBankInterruptWithoutDiscardingPendingStatusAndClearW1C()
        {
            const uint bank2Pin3 = 1u << 3;
            const uint bank3Pin4 = 1u << 20;
            gpio.WriteDoubleWord(0x4C, bank2Pin3 | bank3Pin4);
            gpio.OnGPIO(35, true);
            gpio.OnGPIO(52, true);
            Assert.AreEqual(bank2Pin3 | bank3Pin4, gpio.ReadDoubleWord(0x5C));
            Assert.False(gpio.Bank2IRQ.IsSet);
            Assert.False(gpio.Bank3IRQ.IsSet);

            gpio.WriteDoubleWord(0x08, 1u << 2);
            Assert.True(gpio.Bank2IRQ.IsSet);
            Assert.False(gpio.Bank3IRQ.IsSet);
            gpio.WriteDoubleWord(0x08, (1u << 2) | (1u << 3));
            Assert.True(gpio.Bank3IRQ.IsSet);
            gpio.WriteDoubleWord(0x5C, bank2Pin3);
            Assert.False(gpio.Bank2IRQ.IsSet);
            Assert.True(gpio.Bank3IRQ.IsSet);
            Assert.AreEqual(bank3Pin4, gpio.ReadDoubleWord(0x5C));
            gpio.WriteDoubleWord(0x5C, bank3Pin4);
            Assert.False(gpio.Bank3IRQ.IsSet);
        }

        [Test]
        public void ShouldGenerateEdgesFromOutputTransitionsAndResetAllState()
        {
            const uint pin = 1u << 5;
            gpio.WriteDoubleWord(0x10, 0xFFFFFFFFu & ~pin);
            gpio.WriteDoubleWord(0x24, pin);
            gpio.WriteDoubleWord(0x2C, pin);
            gpio.WriteDoubleWord(0x08, 1);
            gpio.WriteDoubleWord(0x18, pin);
            Assert.AreEqual(pin, gpio.ReadDoubleWord(0x34));
            Assert.True(gpio.Bank0IRQ.IsSet);
            gpio.WriteDoubleWord(0x34, pin);
            gpio.WriteDoubleWord(0x1C, pin);
            Assert.AreEqual(pin, gpio.ReadDoubleWord(0x34));

            gpio.Reset();
            Assert.AreEqual(0xFFFFFFFFu, gpio.ReadDoubleWord(0x10));
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x14));
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x20));
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x24));
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x34));
            Assert.False(gpio.Bank0IRQ.IsSet);
            Assert.False(gpio.Connections[5].IsSet);
        }

        [Test]
        public void ShouldRouteEv3ResetPinBankToExactAintcEvent47()
        {
            var aintc = new TI_DA8xx_AINTC();
            gpio.Bank5IRQ.Connect(aintc, 47);
            aintc.WriteDoubleWord(0x42C, 1u << 24); // event 47 -> IRQ channel 1
            aintc.WriteDoubleWord(0x028, 47);       // enable system event 47
            aintc.WriteDoubleWord(0x034, 1);        // enable IRQ host
            aintc.WriteDoubleWord(0x010, 1);        // global enable

            const uint gpio80 = 1u << 16;
            gpio.WriteDoubleWord(0x74, gpio80);
            gpio.WriteDoubleWord(0x08, 1u << 5);
            gpio.OnGPIO(80, true);
            Assert.True(gpio.Bank5IRQ.IsSet);
            Assert.True(aintc.IRQ.IsSet);
            Assert.AreEqual(47u, aintc.ReadDoubleWord(0x904));

            gpio.WriteDoubleWord(0x84, gpio80);
            Assert.False(gpio.Bank5IRQ.IsSet);
            // AINTC is a second-level latch and remains asserted until SICR.
            Assert.True(aintc.IRQ.IsSet);
            aintc.WriteDoubleWord(0x024, 47);
            Assert.False(aintc.IRQ.IsSet);
        }

        private Machine machine;
        private TI_DA8xx_GPIO gpio;
    }
}
