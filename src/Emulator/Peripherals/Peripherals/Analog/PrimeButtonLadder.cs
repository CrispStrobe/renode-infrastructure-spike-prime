// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT

using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.Miscellaneous;

namespace Antmicro.Renode.Peripherals.Analog
{
    // SPIKE Prime's four user buttons share two resistor ladders. This bridge
    // turns deterministic digital button state into persistent ADC1 samples.
    public class PrimeButtonLadder : SimpleContainer<Button>, IGPIOReceiver
    {
        public PrimeButtonLadder(IMachine machine, STM32_ADC adc) : base(machine)
        {
            this.adc = adc;
            Reset();
        }

        public override void Register(Button button, NumberRegistrationPoint<int> registrationPoint)
        {
            if(registrationPoint.Address < 0 || registrationPoint.Address >= ButtonCount)
            {
                throw new RegistrationException("Prime button index is outside the supported ladder inputs.");
            }
            base.Register(button, registrationPoint);
            button.IRQ.Connect(this, registrationPoint.Address);
        }

        public override void Unregister(Button button)
        {
            base.Unregister(button);
            button.IRQ.Disconnect();
        }

        public void OnGPIO(int number, bool value)
        {
            if(number < 0 || number >= ButtonCount)
            {
                throw new ArgumentOutOfRangeException(nameof(number));
            }
            pressed[number] = value;
            UpdateSamples();
        }

        public override void Reset()
        {
            Array.Clear(pressed, 0, pressed.Length);
            UpdateSamples();
        }

        public bool Center => pressed[CenterButton];
        public bool Left => pressed[LeftButton];
        public bool Right => pressed[RightButton];
        public bool Bluetooth => pressed[BluetoothButton];
        public ushort Ladder0Value { get; private set; }
        public ushort Ladder1Value { get; private set; }

        private void UpdateSamples()
        {
            // Ladder 0 channel 1 is Center. Channel 2 is the charger input and
            // remains released here; the power model owns charger state.
            var ladder0Flags = pressed[CenterButton] ? 2 : 0;
            var ladder1Flags = (pressed[LeftButton] ? 1 : 0)
                | (pressed[RightButton] ? 2 : 0)
                | (pressed[BluetoothButton] ? 4 : 0);
            Ladder0Value = ValueForFlags(Ladder0Levels, ladder0Flags);
            Ladder1Value = ValueForFlags(Ladder1Levels, ladder1Flags);
            adc.SetChannelValue(14, Ladder0Value); // PC4 / ADC1_IN14
            adc.SetChannelValue(1, Ladder1Value);  // PA1 / ADC1_IN1
        }

        private static ushort ValueForFlags(ushort[] levels, int flags)
        {
            // The decoder orders combinations as none, CH2, CH1, CH1|CH2,
            // CH0, CH0|CH2, CH0|CH1, all. Pick a value strictly inside the
            // corresponding threshold interval so boundary noise is excluded.
            var interval = FlagIntervals[flags];
            if(interval == 0)
            {
                return 0xFFF;
            }
            return (ushort)((levels[interval - 1] + levels[interval]) / 2);
        }

        private readonly STM32_ADC adc;
        private readonly bool[] pressed = new bool[ButtonCount];

        private static readonly int[] FlagIntervals = { 0, 4, 2, 6, 1, 5, 3, 7 };
        private static readonly ushort[] Ladder0Levels = { 3642, 3142, 2879, 2634, 2449, 2209, 2072, 1800 };
        private static readonly ushort[] Ladder1Levels = { 3872, 3394, 3009, 2755, 2538, 2327, 2141, 1969 };

        private const int CenterButton = 0;
        private const int LeftButton = 1;
        private const int RightButton = 2;
        private const int BluetoothButton = 3;
        private const int ButtonCount = 4;
    }
}
