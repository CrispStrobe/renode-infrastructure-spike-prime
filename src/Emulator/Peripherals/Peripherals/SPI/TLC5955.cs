//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Linq;

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
            if(number == GrayscaleClockGPIO)
            {
                if(grayscaleClockSynchronizer != null) throw new InvalidOperationException("TLC5955 cannot mix analytical and GPIO GSCLK sources");
                if(!grayscaleClock && value)
                {
                    grayscaleClockEdges++;
                    grayscalePhase = (grayscalePhase + 1) & 0xFFFF;
                }
                grayscaleClock = value;
                return;
            }
            if(number != LatchGPIO)
            {
                return;
            }

            grayscaleClockSynchronizer?.Invoke();
            if(latch && !value)
            {
                Array.Copy(shiftRegister, latchedRegister, FrameSize);
                LatchedFrames++;
                DecodeGrayscaleLatch();
            }
            latch = value;
        }

        public void Reset()
        {
            grayscaleClockSynchronizer?.Invoke();
            Array.Clear(shiftRegister, 0, shiftRegister.Length);
            Array.Clear(latchedRegister, 0, latchedRegister.Length);
            latch = false;
            TotalBytes = 0;
            LatchedFrames = 0;
            Array.Clear(channels, 0, channels.Length);
            grayscaleClock = false;
            grayscaleClockEdges = 0;
            grayscalePhase = 0;
        }

        public byte[] ShiftRegister => (byte[])shiftRegister.Clone();

        public byte[] LatchedRegister => (byte[])latchedRegister.Clone();

        public ulong TotalBytes { get; private set; }

        public ulong LatchedFrames { get; private set; }

        // Channel order follows the MIT-licensed Pybricks Prime platform data:
        // bytes 1..96 contain channels 0..47 as big-endian 16-bit values.
        public ushort[] Channels => (ushort[])channels.Clone();

        public ushort[] Matrix => MatrixChannels.Select(channel => channels[channel]).ToArray();

        // SPDX-License-Identifier: BSD-3-Clause
        // Aggregate-clock additions Copyright (c) 2026 Brickwright contributors.
        // Edges include all transitions already accounted for by the source;
        // the final level must not add another rising edge.
        public void ObserveGrayscaleClock(ulong risingEdges, bool finalLevel)
        {
            grayscaleClockEdges += risingEdges;
            grayscalePhase = (grayscalePhase + (int)(risingEdges & 0xFFFF)) & 0xFFFF;
            grayscaleClock = finalLevel;
        }

        public void SetGrayscaleClockSynchronizer(Action synchronizer)
        {
            if(grayscaleClockSynchronizer != null && synchronizer != grayscaleClockSynchronizer)
            {
                throw new InvalidOperationException("TLC5955 already has an analytical GSCLK source");
            }
            grayscaleClockSynchronizer = synchronizer;
        }

        public ulong GrayscaleClockEdges
        {
            get { grayscaleClockSynchronizer?.Invoke(); return grayscaleClockEdges; }
        }

        public int GrayscalePhase
        {
            get { grayscaleClockSynchronizer?.Invoke(); return grayscalePhase; }
        }

        public const int FrameSize = 97;
        public const int LatchGPIO = 0;
        public const int GrayscaleClockGPIO = 1;

        private void DecodeGrayscaleLatch()
        {
            if((latchedRegister[0] & 1) != 0)
            {
                return; // control latch, not grayscale data
            }
            for(var channel = 0; channel < channels.Length; ++channel)
            {
                channels[channel] = (ushort)((latchedRegister[channel * 2 + 1] << 8)
                    | latchedRegister[channel * 2 + 2]);
            }
        }

        private static readonly int[] MatrixChannels =
        {
            38, 36, 41, 46, 33, 37, 28, 39, 47, 21, 24, 29, 31,
            45, 23, 26, 27, 32, 34, 22, 25, 40, 30, 35, 9,
        };

        private readonly byte[] shiftRegister;
        private readonly byte[] latchedRegister;
        private readonly ushort[] channels = new ushort[48];
        private bool latch;
        private bool grayscaleClock;
        private ulong grayscaleClockEdges;
        private int grayscalePhase;
        private Action grayscaleClockSynchronizer;
    }
}
