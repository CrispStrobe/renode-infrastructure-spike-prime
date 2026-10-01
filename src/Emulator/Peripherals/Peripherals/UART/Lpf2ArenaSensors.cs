// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
// Synthetic arena inputs, expressed through the public ILpf2Device interface.
// These are deterministic protocol models, not optical or force calibration.
using System;
using System.Collections.Generic;

namespace Antmicro.Renode.Peripherals.UART
{
    public sealed class Lpf2ArenaColorSensor : ILpf2Device
    {
        public Lpf2ArenaColorSensor()
        {
            Modes = new[] {new Lpf2Mode(0, "COLOR", 1, Lpf2DataType.Int8),
                new Lpf2Mode(1, "REFLT", 1, Lpf2DataType.Int8), new Lpf2Mode(2, "AMBI", 1, Lpf2DataType.Int8)};
            Reset();
        }
        public void SetReading(byte colorId, byte reflection, byte ambient)
        {
            if(reflection > 100 || ambient > 100) throw new ArgumentOutOfRangeException();
            ColorId = colorId; ReflectionPercent = reflection; AmbientPercent = ambient;
        }
        public byte[] ReadMode(byte mode)
        {
            switch(mode)
            {
                case 0: return new[] {ColorId};
                case 1: return new[] {ReflectionPercent};
                case 2: return new[] {AmbientPercent};
                default: throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }
        public void AcceptOutput(byte mode, byte[] payload) { }
        public void Advance(uint milliseconds) { }
        public void Reset() {ColorId = 255; ReflectionPercent = 0; AmbientPercent = 0;}
        public byte TypeId => 61;
        public string Name => "Arena Color Sensor";
        public IReadOnlyList<Lpf2Mode> Modes { get; }
        public byte ColorId { get; private set; }
        public byte ReflectionPercent { get; private set; }
        public byte AmbientPercent { get; private set; }
        public uint ReportIntervalMicroseconds => 100000;
    }

    public sealed class Lpf2ArenaForceSensor : ILpf2Device
    {
        public Lpf2ArenaForceSensor()
        {
            Modes = new[] {new Lpf2Mode(0, "FORCE", 1, Lpf2DataType.Int8),
                new Lpf2Mode(1, "TOUCH", 1, Lpf2DataType.Int8)};
            Reset();
        }
        public void SetReading(byte forcePercent, bool pressed)
        {
            if(forcePercent > 100) throw new ArgumentOutOfRangeException(nameof(forcePercent));
            ForcePercent = forcePercent; Pressed = pressed;
        }
        public byte[] ReadMode(byte mode)
        {
            switch(mode)
            {
                case 0: return new[] {ForcePercent};
                case 1: return new[] {(byte)(Pressed ? 1 : 0)};
                default: throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }
        public void AcceptOutput(byte mode, byte[] payload) { }
        public void Advance(uint milliseconds) { }
        public void Reset() {ForcePercent = 0; Pressed = false;}
        public byte TypeId => 63;
        public string Name => "Arena Force Sensor";
        public IReadOnlyList<Lpf2Mode> Modes { get; }
        public byte ForcePercent { get; private set; }
        public bool Pressed { get; private set; }
        public uint ReportIntervalMicroseconds => 100000;
    }
}
