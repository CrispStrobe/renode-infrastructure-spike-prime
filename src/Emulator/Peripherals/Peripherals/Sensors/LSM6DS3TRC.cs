//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Peripherals.I2C;

namespace Antmicro.Renode.Peripherals.Sensors
{
    // Deterministic register-level model of the I2C interface used by the
    // LSM6DS3TR-C. It intentionally does not model timing or sensor physics.
    public class LSM6DS3TRC : II2CPeripheral
    {
        public LSM6DS3TRC()
        {
            registers = new byte[RegisterCount];
            Reset();
        }

        public void Write(byte[] data)
        {
            if(data.Length == 0)
            {
                return;
            }

            address = data[0];
            for(var i = 1; i < data.Length; ++i)
            {
                WriteRegister(address, data[i]);
                IncrementAddressIfEnabled();
            }
        }

        public byte[] Read(int count = 1)
        {
            if(count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            var result = new byte[count];
            for(var i = 0; i < result.Length; ++i)
            {
                result[i] = registers[address];
                ClearDataReadyAfterRead(address);
                IncrementAddressIfEnabled();
            }
            return result;
        }

        public void FinishTransmission()
        {
            address = 0;
        }

        public void Reset()
        {
            Array.Clear(registers, 0, registers.Length);
            registers[(byte)Registers.WhoAmI] = WhoAmIValue;
            address = 0;
        }

        public void FeedAccelerationSample(short x, short y, short z)
        {
            WriteVector(Registers.AccelerometerXLow, x, y, z);
            registers[(byte)Registers.Status] |= AccelerometerDataReady;
        }

        public void FeedAngularRateSample(short x, short y, short z)
        {
            WriteVector(Registers.GyroscopeXLow, x, y, z);
            registers[(byte)Registers.Status] |= GyroscopeDataReady;
        }

        public void FeedTemperatureSample(short temperature)
        {
            WriteInt16(Registers.TemperatureLow, temperature);
            registers[(byte)Registers.Status] |= TemperatureDataReady;
        }

        public void FeedSample(short temperature, short angularRateX, short angularRateY, short angularRateZ,
            short accelerationX, short accelerationY, short accelerationZ)
        {
            FeedTemperatureSample(temperature);
            FeedAngularRateSample(angularRateX, angularRateY, angularRateZ);
            FeedAccelerationSample(accelerationX, accelerationY, accelerationZ);
        }

        public byte[] RegisterSnapshot => (byte[])registers.Clone();

        public byte Status => registers[(byte)Registers.Status];

        public byte Control3 => registers[(byte)Registers.Control3];

        private void WriteRegister(byte register, byte value)
        {
            if(register == (byte)Registers.WhoAmI || register == (byte)Registers.Status
                || register >= (byte)Registers.TemperatureLow && register <= (byte)Registers.AccelerometerZHigh)
            {
                return;
            }

            if(register == (byte)Registers.Control3 && (value & SoftwareReset) != 0)
            {
                var currentAddress = address;
                Reset();
                address = currentAddress;
                return;
            }

            registers[register] = value;
        }

        private void IncrementAddressIfEnabled()
        {
            if((registers[(byte)Registers.Control3] & AutoIncrement) != 0)
            {
                address++;
            }
        }

        private void ClearDataReadyAfterRead(byte register)
        {
            if(register == (byte)Registers.TemperatureHigh)
            {
                registers[(byte)Registers.Status] &= unchecked((byte)~TemperatureDataReady);
            }
            else if(register == (byte)Registers.GyroscopeZHigh)
            {
                registers[(byte)Registers.Status] &= unchecked((byte)~GyroscopeDataReady);
            }
            else if(register == (byte)Registers.AccelerometerZHigh)
            {
                registers[(byte)Registers.Status] &= unchecked((byte)~AccelerometerDataReady);
            }
        }

        private void WriteVector(Registers firstRegister, short x, short y, short z)
        {
            var firstAddress = (byte)firstRegister;
            WriteInt16(firstAddress, x);
            WriteInt16((byte)(firstAddress + 2), y);
            WriteInt16((byte)(firstAddress + 4), z);
        }

        private void WriteInt16(Registers firstRegister, short value)
        {
            WriteInt16((byte)firstRegister, value);
        }

        private void WriteInt16(byte firstRegister, short value)
        {
            registers[firstRegister] = (byte)value;
            registers[firstRegister + 1] = (byte)(value >> 8);
        }

        private const int RegisterCount = 256;
        private const byte WhoAmIValue = 0x6A;
        private const byte SoftwareReset = 1 << 0;
        private const byte AutoIncrement = 1 << 2;
        private const byte AccelerometerDataReady = 1 << 0;
        private const byte GyroscopeDataReady = 1 << 1;
        private const byte TemperatureDataReady = 1 << 2;

        private readonly byte[] registers;
        private byte address;

        private enum Registers : byte
        {
            WhoAmI = 0x0F,
            Control3 = 0x12,
            Status = 0x1E,
            TemperatureLow = 0x20,
            TemperatureHigh = 0x21,
            GyroscopeXLow = 0x22,
            GyroscopeZHigh = 0x27,
            AccelerometerXLow = 0x28,
            AccelerometerZHigh = 0x2D,
        }
    }
}
