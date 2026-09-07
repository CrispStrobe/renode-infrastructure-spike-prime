//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Core;

namespace Antmicro.Renode.Peripherals.SPI
{
    // A byte-oriented model of the TLC5955's 769-bit serial data path.
    // The leading byte contains the single latch-select bit in bit 0; the
    // remaining seven bits are padding used by byte-oriented SPI hosts.
    public class TLC5955 : ISPIPeripheral, IGPIOReceiver
    {
        public TLC5955()
        {
            shiftRegister = new byte[FrameSize];
            latchedRegister = new byte[FrameSize];
            Reset();
        }

        public byte Transmit(byte data)
        {
            Array.Copy(shiftRegister, 1, shiftRegister, 0, FrameSize - 1);
            shiftRegister[FrameSize - 1] = data;
            TotalBytes++;
            return 0;
        }

        // TLC5955 has no chip-select input. Ending an SPI transaction does not
        // update its outputs; a falling LAT edge does.
        public void FinishTransmission()
        {
        }

        public void OnGPIO(int number, bool value)
        {
            if(number != LatchGPIO)
            {
                return;
            }

            if(latch && !value)
            {
                Array.Copy(shiftRegister, latchedRegister, FrameSize);
                LatchedFrames++;
            }
            latch = value;
        }

        public void Reset()
        {
            Array.Clear(shiftRegister, 0, shiftRegister.Length);
            Array.Clear(latchedRegister, 0, latchedRegister.Length);
            latch = false;
            TotalBytes = 0;
            LatchedFrames = 0;
        }

        public byte[] ShiftRegister => (byte[])shiftRegister.Clone();

        public byte[] LatchedRegister => (byte[])latchedRegister.Clone();

        public ulong TotalBytes { get; private set; }

        public ulong LatchedFrames { get; private set; }

        public const int FrameSize = 97;
        public const int LatchGPIO = 0;

        private readonly byte[] shiftRegister;
        private readonly byte[] latchedRegister;
        private bool latch;
    }
}
