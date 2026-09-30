// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.SPI;

namespace Antmicro.Renode.Peripherals.Analog
{
    // Functional manual-mode ADS7957. Each active-low CS transaction is one
    // 16-bit, MSB-first frame. A channel requested in frame N is returned in
    // frame N+2. Raw input codes are injected, not inferred from host time.
    public class ADS7957 : ISPIPeripheral
    {
        public ADS7957()
        {
            samples = new ushort[16];
            Reset();
        }

        public void SetChannelValue(int channel, ushort value)
        {
            CheckChannel(channel);
            if(value > 1023) throw new RecoverableException("ADS7957 samples must fit 10 bits.");
            samples[channel] = value;
        }

        public ushort GetChannelValue(int channel)
        {
            CheckChannel(channel);
            return samples[channel];
        }

        public byte Transmit(byte data)
        {
            if(bytesReceived == 0)
            {
                response = (ushort)((conversionChannel << 12) | (samples[conversionChannel] << 2));
                command = (ushort)(data << 8);
                bytesReceived = 1;
                return (byte)(response >> 8);
            }
            if(bytesReceived == 1)
            {
                command |= data;
                bytesReceived = 2;
                return (byte)response;
            }
            // The converter requires a CS edge before the next frame; extra
            // clocks within one selected transaction do not start a frame.
            return 0;
        }

        public void FinishTransmission()
        {
            if(bytesReceived == 2)
            {
                conversionChannel = acquisitionChannel;
                var mode = command >> 12;
                if(mode == 1)
                {
                    acquisitionChannel = (command >> 7) & 0xF;
                    if((command & 0x800) != 0) TwoTimesReference = (command & 0x40) != 0;
                }
                else if(mode != 0)
                {
                    UnsupportedCommands++;
                }
                CompletedFrames++;
            }
            bytesReceived = 0;
            command = 0;
        }

        public void Reset()
        {
            conversionChannel = acquisitionChannel = bytesReceived = 0;
            command = response = 0;
            CompletedFrames = UnsupportedCommands = 0;
            TwoTimesReference = false;
            // Reset protocol state; injected physical input levels survive.
        }

        public bool TwoTimesReference { get; private set; }
        public ulong CompletedFrames { get; private set; }
        public ulong UnsupportedCommands { get; private set; }

        private static void CheckChannel(int channel)
        {
            if(channel < 0 || channel >= 16) throw new RecoverableException("ADS7957 channel must be in [0,15].");
        }

        private readonly ushort[] samples;
        private int conversionChannel;
        private int acquisitionChannel;
        private int bytesReceived;
        private ushort command;
        private ushort response;
    }
}
