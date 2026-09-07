//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;

namespace Antmicro.Renode.Peripherals.UART
{
    public interface ILpf2Device
    {
        byte TypeId { get; }
        string Name { get; }
        IReadOnlyList<Lpf2Mode> Modes { get; }
        byte[] ReadMode(byte mode);
        void AcceptOutput(byte mode, byte[] payload);
        void Advance(uint milliseconds);
        void Reset();
    }

    public sealed class Lpf2Mode
    {
        public Lpf2Mode(byte number, string name, byte values, Lpf2DataType dataType)
        {
            Number = number;
            Name = name;
            Values = values;
            DataType = dataType;
        }

        public byte Number { get; }
        public string Name { get; }
        public byte Values { get; }
        public Lpf2DataType DataType { get; }
    }

    public enum Lpf2DataType : byte
    {
        Int8 = 0,
        Int16 = 1,
        Int32 = 2,
        Float = 3,
    }

    public sealed class Lpf2UltrasonicSensor : ILpf2Device
    {
        public Lpf2UltrasonicSensor()
        {
            Modes = new[] { new Lpf2Mode(0, "DISTL", 1, Lpf2DataType.Int16) };
            Reset();
        }

        public void SetDistance(ushort millimeters)
        {
            DistanceMillimeters = millimeters;
        }

        public byte[] ReadMode(byte mode)
        {
            if(mode != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }
            return EncodeLittleEndian(DistanceMillimeters, 2);
        }

        public void AcceptOutput(byte mode, byte[] payload)
        {
            // This deterministic checkpoint models distance input only.
        }

        public void Advance(uint milliseconds)
        {
        }

        public void Reset()
        {
            DistanceMillimeters = 1000;
        }

        public byte TypeId => 62;
        public string Name => "Ultrasonic Sensor";
        public IReadOnlyList<Lpf2Mode> Modes { get; }
        public ushort DistanceMillimeters { get; private set; }

        private static byte[] EncodeLittleEndian(int value, int size)
        {
            var result = new byte[size];
            for(var i = 0; i < size; ++i)
            {
                result[i] = (byte)(value >> (8 * i));
            }
            return result;
        }
    }

    public sealed class Lpf2MediumMotor : ILpf2Device
    {
        public Lpf2MediumMotor()
        {
            Modes = new[]
            {
                new Lpf2Mode(0, "POWER", 1, Lpf2DataType.Int8),
                new Lpf2Mode(1, "SPEED", 1, Lpf2DataType.Int8),
                new Lpf2Mode(2, "POS", 1, Lpf2DataType.Int32),
            };
            Reset();
        }

        public byte[] ReadMode(byte mode)
        {
            switch(mode)
            {
                case 0: return new[] { unchecked((byte)Power) };
                case 1: return new[] { unchecked((byte)SpeedPercent) };
                case 2: return EncodeLittleEndian(EncoderDegrees, 4);
                default: throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        public void AcceptOutput(byte mode, byte[] payload)
        {
            if(mode != 0 || payload.Length == 0)
            {
                return;
            }
            Power = Clamp(unchecked((sbyte)payload[0]), -100, 100);
            UpdateMotionState();
        }

        public void SetLoad(byte percent)
        {
            LoadPercent = (byte)Clamp(percent, 0, 100);
            UpdateMotionState();
        }

        public void SetStallThreshold(byte percent)
        {
            StallThresholdPercent = (byte)Clamp(percent, 1, 100);
            UpdateMotionState();
        }

        public void Advance(uint milliseconds)
        {
            UpdateMotionState();
            if(Stalled || AngularVelocityDegreesPerSecond == 0)
            {
                return;
            }
            positionRemainder += (long)AngularVelocityDegreesPerSecond * milliseconds;
            EncoderDegrees += (int)(positionRemainder / 1000);
            positionRemainder %= 1000;
        }

        public void Reset()
        {
            Power = 0;
            SpeedPercent = 0;
            AngularVelocityDegreesPerSecond = 0;
            EncoderDegrees = 0;
            LoadPercent = 0;
            StallThresholdPercent = 90;
            Stalled = false;
            positionRemainder = 0;
        }

        public byte TypeId => 48;
        public string Name => "SPIKE Medium Motor";
        public IReadOnlyList<Lpf2Mode> Modes { get; }
        public int Power { get; private set; }
        public int SpeedPercent { get; private set; }
        public int AngularVelocityDegreesPerSecond { get; private set; }
        public int EncoderDegrees { get; private set; }
        public byte LoadPercent { get; private set; }
        public byte StallThresholdPercent { get; private set; }
        public bool Stalled { get; private set; }

        private void UpdateMotionState()
        {
            Stalled = Power != 0 && LoadPercent >= StallThresholdPercent;
            SpeedPercent = Stalled ? 0 : Power * (100 - LoadPercent) / 100;
            AngularVelocityDegreesPerSecond = SpeedPercent * MaximumDegreesPerSecond / 100;
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static byte[] EncodeLittleEndian(int value, int size)
        {
            var result = new byte[size];
            for(var i = 0; i < size; ++i)
            {
                result[i] = (byte)(value >> (8 * i));
            }
            return result;
        }

        private const int MaximumDegreesPerSecond = 1000;
        private long positionRemainder;
    }
}
