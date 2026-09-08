//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Sound
{
    // A DMA-addressable observation sink. It records PCM bytes without using
    // host audio, making snapshots and CI runs deterministic.
    public class PCMAudioSink : IBytePeripheral, IWordPeripheral, IKnownSize, IGPIOReceiver
    {
        public PCMAudioSink(int capacity = 65536)
        {
            if(capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            samples = new Queue<byte>(capacity);
            pendingSamples = new Queue<ushort>(capacity);
            Capacity = capacity;
        }

        public byte ReadByte(long offset)
        {
            ValidateOffset(offset);
            return samples.Count == 0 ? (byte)0 : samples.Dequeue();
        }

        public void WriteByte(long offset, byte value)
        {
            ValidateOffset(offset);
            if(!Enabled)
            {
                DisabledBytes++;
                return;
            }
            if(samples.Count == Capacity)
            {
                samples.Dequeue();
                DroppedBytes++;
            }
            samples.Enqueue(value);
            TotalBytes++;
            LastSample = value;
        }

        public ushort ReadWord(long offset)
        {
            ValidateOffset(offset);
            return pendingSamples.Count == 0 ? (ushort)0 : pendingSamples.Dequeue();
        }

        // DAC1 DHR12R1 consumes the low twelve bits. Samples remain pending
        // until the deterministic TIM6-equivalent clock is advanced.
        public void WriteWord(long offset, ushort value)
        {
            ValidateOffset(offset);
            if(!Enabled)
            {
                DisabledSamples++;
                return;
            }
            if(pendingSamples.Count == Capacity)
            {
                pendingSamples.Dequeue();
                DroppedSamples++;
            }
            pendingSamples.Enqueue((ushort)(value & 0xFFF));
        }

        public void AdvanceSampleClock(int ticks = 1)
        {
            if(ticks < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ticks));
            }
            while(ticks-- != 0 && pendingSamples.Count != 0)
            {
                LastDacSample = pendingSamples.Dequeue();
                EmittedSamples++;
            }
        }

        public void OnGPIO(int number, bool value)
        {
            if(number == EnableGPIO)
            {
                Enabled = value;
            }
            else if(number == SampleClockGPIO && value)
            {
                AdvanceSampleClock();
            }
        }

        public void Reset()
        {
            samples.Clear();
            pendingSamples.Clear();
            TotalBytes = 0;
            DroppedBytes = 0;
            DisabledBytes = 0;
            LastSample = 0;
            Enabled = false;
            LastDacSample = 0;
            EmittedSamples = 0;
            DroppedSamples = 0;
            DisabledSamples = 0;
        }

        public byte[] Snapshot => samples.ToArray();
        public int Capacity { get; }
        public int BufferedBytes => samples.Count;
        public byte LastSample { get; private set; }
        public ulong TotalBytes { get; private set; }
        public ulong DroppedBytes { get; private set; }
        public ulong DisabledBytes { get; private set; }
        public bool Enabled { get; private set; }
        public ushort LastDacSample { get; private set; }
        public int PendingSamples => pendingSamples.Count;
        public ulong EmittedSamples { get; private set; }
        public ulong DroppedSamples { get; private set; }
        public ulong DisabledSamples { get; private set; }
        public long Size => 2;

        public const int EnableGPIO = 0;
        public const int SampleClockGPIO = 1;

        private void ValidateOffset(long offset)
        {
            if(offset != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
        }

        private readonly Queue<byte> samples;
        private readonly Queue<ushort> pendingSamples;
    }
}
