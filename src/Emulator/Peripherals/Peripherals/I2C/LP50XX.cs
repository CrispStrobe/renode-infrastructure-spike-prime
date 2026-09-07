//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Core;

namespace Antmicro.Renode.Peripherals.I2C
{
    // Register-level model of the common LP50xx interface used by LP5012-class
    // RGB LED drivers. LED current shaping and PWM timing are intentionally
    // represented as deterministic byte values rather than analogue light.
    public sealed class LP50XX : II2CPeripheral, IGPIOReceiver
    {
        public LP50XX()
        {
            registers = new byte[RegisterCount];
            Reset();
        }

        public void Write(byte[] data)
        {
            if(!enabled || data.Length == 0)
            {
                return;
            }

            address = data[0];
            for(var i = 1; i < data.Length; ++i)
            {
                WriteRegister(address, data[i]);
                if(AutoIncrementEnabled)
                {
                    address = (byte)((address + 1) % RegisterCount);
                }
            }
        }

        public byte[] Read(int count = 1)
        {
            if(count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            var result = new byte[count];
            if(!enabled)
            {
                return result;
            }

            for(var i = 0; i < count; ++i)
            {
                result[i] = registers[address];
                if(AutoIncrementEnabled)
                {
                    address = (byte)((address + 1) % RegisterCount);
                }
            }
            return result;
        }

        public void FinishTransmission()
        {
            address = 0;
        }

        public void OnGPIO(int number, bool value)
        {
            if(number != EnableGPIO)
            {
                return;
            }
            if(enabled && !value)
            {
                ResetRegisters();
            }
            enabled = value;
        }

        public void Reset()
        {
            enabled = false;
            ResetRegisters();
        }

        public bool Enabled => enabled;
        public bool ChipEnabled => enabled && (registers[DeviceConfig0] & ChipEnable) != 0;
        public bool AutoIncrementEnabled => (registers[DeviceConfig1] & AutoIncrement) != 0;
        public byte[] RegisterSnapshot => (byte[])registers.Clone();

        public byte[] OutputColorSnapshot
        {
            get
            {
                var result = new byte[OutputCount];
                Array.Copy(registers, Output0Color, result, 0, result.Length);
                return result;
            }
        }

        // Returns four deterministic RGB triplets after applying each module's
        // brightness byte. This is intended for UI adapters, not light physics.
        public byte[][] RenderedModuleSnapshot
        {
            get
            {
                var result = new byte[ModuleCount][];
                for(var module = 0; module < result.Length; ++module)
                {
                    result[module] = new byte[ColorsPerModule];
                    for(var color = 0; color < ColorsPerModule; ++color)
                    {
                        var raw = registers[Output0Color + module * ColorsPerModule + color];
                        var brightness = registers[Led0Brightness + module];
                        result[module][color] = ChipEnabled && !GlobalOutputOff
                            ? (byte)((raw * brightness + byte.MaxValue / 2) / byte.MaxValue)
                            : (byte)0;
                    }
                }
                return result;
            }
        }

        private void WriteRegister(byte register, byte value)
        {
            if(register == ResetRegister)
            {
                if(value == SoftwareReset)
                {
                    ResetRegisters();
                }
                return;
            }
            if(register < RegisterCount)
            {
                registers[register] = value;
            }
        }

        private bool GlobalOutputOff => (registers[DeviceConfig1] & GlobalOff) != 0;

        private void ResetRegisters()
        {
            Array.Clear(registers, 0, registers.Length);
            registers[DeviceConfig1] = DeviceConfig1ResetValue;
            address = 0;
        }

        public const int EnableGPIO = 0;
        public const int OutputCount = 12;
        public const int ModuleCount = 4;

        private const int RegisterCount = 0x18;
        private const byte DeviceConfig0 = 0x00;
        private const byte DeviceConfig1 = 0x01;
        private const byte Led0Brightness = 0x07;
        private const byte Output0Color = 0x0B;
        private const byte ResetRegister = 0x17;
        private const byte ChipEnable = 1 << 6;
        private const byte AutoIncrement = 1 << 3;
        private const byte DeviceConfig1ResetValue = 0x3C;
        private const byte GlobalOff = 1 << 0;
        private const int ColorsPerModule = 3;
        private const byte SoftwareReset = 0xFF;

        private readonly byte[] registers;
        private byte address;
        private bool enabled;
    }
}
