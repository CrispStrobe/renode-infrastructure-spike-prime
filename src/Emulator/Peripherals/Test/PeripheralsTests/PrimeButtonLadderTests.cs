// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Analog;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class PrimeButtonLadderTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
            adc = new STM32_ADC(machine);
            ladder = new PrimeButtonLadder(adc);
        }

        [TearDown]
        public void TearDown()
        {
            EmulationManager.Instance.CurrentEmulation.RemoveMachine(machine);
            machine.Dispose();
        }

        [Test]
        public void ShouldMapButtonsToSourceCitedLadderIntervals()
        {
            Assert.AreEqual(0xFFF, ladder.Ladder0Value);
            Assert.AreEqual(0xFFF, ladder.Ladder1Value);

            ladder.OnGPIO(0, true);
            Assert.That(ladder.Ladder0Value, Is.GreaterThan(2879).And.LessThan(3142));
            Assert.AreEqual(ladder.Ladder0Value, ReadChannel(14));

            ladder.OnGPIO(1, true);
            Assert.That(ladder.Ladder1Value, Is.GreaterThan(2538).And.LessThan(2755));
            Assert.AreEqual(ladder.Ladder1Value, ReadChannel(1));
            ladder.OnGPIO(2, true);
            ladder.OnGPIO(3, true);
            Assert.That(ladder.Ladder1Value, Is.GreaterThan(1969).And.LessThan(2141));
        }

        [Test]
        public void ShouldRestoreReleasedSamplesAndRejectUnknownInput()
        {
            ladder.OnGPIO(3, true);
            ladder.Reset();
            Assert.False(ladder.Bluetooth);
            Assert.AreEqual(0xFFF, ladder.Ladder1Value);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => ladder.OnGPIO(4, true));
        }

        private uint ReadChannel(uint channel)
        {
            adc.WriteDoubleWord(0x08, 0);                // CR2 power down
            adc.WriteDoubleWord(0x34, channel);          // SQR3 SQ1
            adc.WriteDoubleWord(0x2C, 0);                // SQR1 one conversion
            adc.WriteDoubleWord(0x08, 1);                // CR2 ADON
            adc.WriteDoubleWord(0x08, (1u << 30) | 1);   // CR2 SWSTART | ADON
            ((Antmicro.Renode.Time.BaseClockSource)machine.ClockSource).Advance(
                Antmicro.Renode.Time.TimeInterval.FromMicroseconds(101), true);
            return adc.ReadDoubleWord(0x4C);             // DR
        }

        private Machine machine;
        private STM32_ADC adc;
        private PrimeButtonLadder ladder;
    }
}
