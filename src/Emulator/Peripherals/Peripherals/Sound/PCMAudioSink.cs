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
    public class PCMAudioSink : IBytePeripheral, IKnownSize, IGPIOReceiver
    {
        public PCMAudioSink(int capacity = 65536)
        {
            if(capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            samples = new Queue<byte>(capacity);
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

        public void OnGPIO(int number, bool value)
        {
            if(number == EnableGPIO)
            {
                Enabled = value;
            }
        }

        public void Reset()
        {
            samples.Clear();
            TotalBytes = 0;
            DroppedBytes = 0;
            DisabledBytes = 0;
            LastSample = 0;
            Enabled = false;
        }

        public byte[] Snapshot => samples.ToArray();
        public int Capacity { get; }
        public int BufferedBytes => samples.Count;
        public byte LastSample { get; private set; }
        public ulong TotalBytes { get; private set; }
        public ulong DroppedBytes { get; private set; }
        public ulong DisabledBytes { get; private set; }
        public bool Enabled { get; private set; }
        public long Size => 1;

        public const int EnableGPIO = 0;

        private void ValidateOffset(long offset)
        {
            if(offset != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
        }

        private readonly Queue<byte> samples;
    }
}
