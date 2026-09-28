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
        uint ReportIntervalMicroseconds { get; }
    }

    public interface IExactLpf2Discovery
    {
        IReadOnlyList<byte> DiscoveryBytes { get; }
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
        public uint ReportIntervalMicroseconds => 100000;

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
        public uint ReportIntervalMicroseconds => 100000;

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

    // This is type 46, the Technic Large Linear Motor for which Pybricks has
    // an SPDX-MIT logic-analyzer fixture. It is not the SPIKE Large Motor
    // (type 49), whose complete discovery contract is still pending.
    public sealed class Lpf2TechnicLargeMotor : ILpf2Device, IExactLpf2Discovery
    {
        public Lpf2TechnicLargeMotor()
        {
            Modes = new[]
            {
                new Lpf2Mode(0, "POWER", 1, Lpf2DataType.Int8),
                new Lpf2Mode(1, "SPEED", 1, Lpf2DataType.Int8),
                new Lpf2Mode(2, "POS", 1, Lpf2DataType.Int32),
                new Lpf2Mode(3, "APOS", 1, Lpf2DataType.Int16),
                new Lpf2Mode(4, "CALIB", 2, Lpf2DataType.Int16),
                new Lpf2Mode(5, "STATS", 14, Lpf2DataType.Int16),
            };
            Reset();
        }

        public byte[] ReadMode(byte mode)
        {
            switch(mode)
            {
                case 0: return new[] { unchecked((byte)Power) };
                case 1: return new[] { unchecked((byte)SpeedPercent) };
                case 2: return EncodeLittleEndian(PositionDegrees, 4);
                case 3: return EncodeLittleEndian(NormalizeAbsolutePosition(PositionDegrees), 2);
                case 4: return new byte[4];
                case 5: return new byte[28];
                default: throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        public void AcceptOutput(byte mode, byte[] payload)
        {
            // Only POWER has a source-backed command meaning in this checkpoint.
            if(mode != 0 || payload.Length != 1)
            {
                return;
            }
            Power = Math.Max(-100, Math.Min(100, (int)unchecked((sbyte)payload[0])));
            UpdateMotionState();
        }

        public void SetLoad(byte percent)
        {
            LoadPercent = (byte)Math.Max(0, Math.Min(100, (int)percent));
            UpdateMotionState();
        }

        public void Advance(uint milliseconds)
        {
            UpdateMotionState();
            if(Stalled || SpeedPercent == 0)
            {
                return;
            }
            positionRemainder += (long)SpeedPercent * MaximumDegreesPerSecond * milliseconds;
            PositionDegrees += (int)(positionRemainder / 100000);
            positionRemainder %= 100000;
        }

        public void Reset()
        {
            Power = 0;
            SpeedPercent = 0;
            PositionDegrees = 0;
            LoadPercent = 0;
            Stalled = false;
            positionRemainder = 0;
        }

        public byte TypeId => 46;
        public string Name => "Technic Large Linear Motor";
        public IReadOnlyList<Lpf2Mode> Modes { get; }
        public int Power { get; private set; }
        public int SpeedPercent { get; private set; }
        public int PositionDegrees { get; private set; }
        public byte LoadPercent { get; private set; }
        public bool Stalled { get; private set; }
        public uint ReportIntervalMicroseconds => 100000;
        public IReadOnlyList<byte> DiscoveryBytes => discoveryBytes;

        private void UpdateMotionState()
        {
            Stalled = Power != 0 && LoadPercent >= StallThresholdPercent;
            SpeedPercent = Stalled ? 0 : Power * (100 - LoadPercent) / 100;
        }

        private static int NormalizeAbsolutePosition(int degrees)
        {
            var normalized = degrees % 360;
            return normalized < 0 ? normalized + 360 : normalized;
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

        private const int MaximumDegreesPerSecond = 1050;
        private const int StallThresholdPercent = 90;
        // Copied from Pybricks test_uartdev.c at commit 101c6babb592148bda9a8fd912b7953c7d561c0a.
        // SPDX-License-Identifier: MIT
        // Copyright (c) 2019-2023 The Pybricks Authors
        private static readonly byte[] discoveryBytes =
        {
            0x40, 0x2e, 0x91, 0x49, 0x05, 0x03, 0xb0, 0x52, 0x00, 0xc2, 0x01, 0x00, 0x6e, 0x5f, 0x04, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0xb4, 0xa5, 0x00, 0x53, 0x54, 0x41, 0x54, 0x53, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x05, 0x04, 0x00, 0x00, 0x00, 0x00, 0x1a, 0x9d, 0x01, 0x00, 0x00, 0x00, 0x00,
            0x00, 0xff, 0x7f, 0x47, 0xa4, 0x9d, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xc8, 0x42, 0xea,
            0x9d, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0xff, 0x7f, 0x47, 0xa6, 0x95, 0x04, 0x4d, 0x49, 0x4e,
            0x00, 0x24, 0x8d, 0x05, 0x00, 0x00, 0x77, 0x95, 0x80, 0x0e, 0x01, 0x05, 0x00, 0xe0, 0xa4, 0x00,
            0x43, 0x41, 0x4c, 0x49, 0x42, 0x00, 0x22, 0x40, 0x00, 0x00, 0x05, 0x04, 0x00, 0x00, 0x00, 0x00,
            0x7d, 0x9c, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x61, 0x45, 0x46, 0x9c, 0x02, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0xc8, 0x42, 0xeb, 0x9c, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x61,
            0x45, 0x44, 0x94, 0x04, 0x43, 0x41, 0x4c, 0x00, 0x21, 0x8c, 0x05, 0x00, 0x00, 0x76, 0x94, 0x80,
            0x02, 0x01, 0x05, 0x00, 0xed, 0xa3, 0x00, 0x41, 0x50, 0x4f, 0x53, 0x00, 0x00, 0x22, 0x00, 0x00,
            0x00, 0x05, 0x04, 0x00, 0x00, 0x00, 0x00, 0x72, 0x9b, 0x01, 0x00, 0x00, 0x34, 0xc3, 0x00, 0x00,
            0x33, 0x43, 0xe2, 0x9b, 0x02, 0x00, 0x00, 0x48, 0xc3, 0x00, 0x00, 0x48, 0x43, 0xe6, 0x9b, 0x03,
            0x00, 0x00, 0x34, 0xc3, 0x00, 0x00, 0x33, 0x43, 0xe0, 0x93, 0x04, 0x44, 0x45, 0x47, 0x00, 0x2e,
            0x8b, 0x05, 0x32, 0x32, 0x71, 0x93, 0x80, 0x01, 0x01, 0x03, 0x00, 0xef, 0xa2, 0x00, 0x50, 0x4f,
            0x53, 0x00, 0x00, 0x00, 0x24, 0x00, 0x00, 0x00, 0x05, 0x04, 0x00, 0x00, 0x00, 0x00, 0x34, 0x9a,
            0x01, 0x00, 0x00, 0xb4, 0xc3, 0x00, 0x00, 0xb4, 0x43, 0xe4, 0x9a, 0x02, 0x00, 0x00, 0xc8, 0xc2,
            0x00, 0x00, 0xc8, 0x42, 0xe7, 0x9a, 0x03, 0x00, 0x00, 0xb4, 0xc3, 0x00, 0x00, 0xb4, 0x43, 0xe6,
            0x92, 0x04, 0x44, 0x45, 0x47, 0x00, 0x2f, 0x8a, 0x05, 0x28, 0x68, 0x30, 0x92, 0x80, 0x01, 0x02,
            0x0b, 0x00, 0xe5, 0xa1, 0x00, 0x53, 0x50, 0x45, 0x45, 0x44, 0x00, 0x21, 0x00, 0x00, 0x00, 0x05,
            0x04, 0x00, 0x00, 0x00, 0x00, 0x39, 0x99, 0x01, 0x00, 0x00, 0xc8, 0xc2, 0x00, 0x00, 0xc8, 0x42,
            0xe7, 0x99, 0x02, 0x00, 0x00, 0xc8, 0xc2, 0x00, 0x00, 0xc8, 0x42, 0xe4, 0x99, 0x03, 0x00, 0x00,
            0xc8, 0xc2, 0x00, 0x00, 0xc8, 0x42, 0xe5, 0x91, 0x04, 0x50, 0x43, 0x54, 0x00, 0x2d, 0x89, 0x05,
            0x30, 0x70, 0x33, 0x91, 0x80, 0x01, 0x00, 0x04, 0x00, 0xeb, 0xa0, 0x00, 0x50, 0x4f, 0x57, 0x45,
            0x52, 0x00, 0x30, 0x00, 0x00, 0x00, 0x05, 0x04, 0x00, 0x00, 0x00, 0x00, 0x31, 0x98, 0x01, 0x00,
            0x00, 0xc8, 0xc2, 0x00, 0x00, 0xc8, 0x42, 0xe6, 0x98, 0x02, 0x00, 0x00, 0xc8, 0xc2, 0x00, 0x00,
            0xc8, 0x42, 0xe5, 0x98, 0x03, 0x00, 0x00, 0xc8, 0xc2, 0x00, 0x00, 0xc8, 0x42, 0xe4, 0x90, 0x04,
            0x50, 0x43, 0x54, 0x00, 0x2c, 0x88, 0x05, 0x00, 0x50, 0x22, 0x90, 0x80, 0x01, 0x00, 0x04, 0x00,
            0xea, 0x88, 0x06, 0x0e, 0x00, 0x7f, 0xa0, 0x08, 0x00, 0x40, 0x00, 0x2e, 0x09, 0x47, 0x38, 0x33,
            0x36, 0x36, 0x36, 0x30, 0x00, 0x00, 0x00, 0x00, 0x7a, 0xa0, 0x09, 0x88, 0x13, 0x00, 0x00, 0xfa,
            0x00, 0x00, 0x00, 0x10, 0x27, 0x00, 0x00, 0xbe, 0x05, 0x00, 0x00, 0xbb, 0xa0, 0x0a, 0x98, 0x3a,
            0x00, 0x00, 0x96, 0x00, 0x00, 0x00, 0x98, 0x3a, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xc3, 0x98,
            0x0b, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x6c, 0x90, 0x0c, 0x00, 0x00, 0x00, 0x00,
            0x63, 0x04,
        };
        private long positionRemainder;
    }
}
