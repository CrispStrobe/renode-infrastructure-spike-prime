//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.IRQControllers
{
    // ARM Interrupt Controller used by TI DA8xx/OMAP-L1x devices, including
    // the AM1808 in LEGO MINDSTORMS EV3. The AM1808 exposes 101 system events;
    // channel 0 drives FIQ and channel 1 drives IRQ.
    public class TI_DA8xx_AINTC : IDoubleWordPeripheral, IIRQController, IKnownSize
    {
        public TI_DA8xx_AINTC()
        {
            IRQ = new GPIO();
            FIQ = new GPIO();
            Reset();
        }

        public void Reset()
        {
            Array.Clear(rawStatus, 0, rawStatus.Length);
            Array.Clear(enabled, 0, enabled.Length);
            Array.Clear(channel, 0, channel.Length);
            globalEnable = false;
            hostEnable = 0;
            vectorBase = 0;
            vectorSize = 0;
            vectorNull = 0;
            Update();
        }

        public void OnGPIO(int number, bool value)
        {
            if(number < 0 || number >= NumberOfSystemInterrupts)
            {
                return;
            }
            // AINTC latches incoming events. Software acknowledges them through
            // SICR or SECR; lowering a peripheral line must not lose an event.
            if(value)
            {
                rawStatus[number] = true;
                Update();
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset >= (long)Registers.SystemRawStatus1 && offset <= (long)Registers.SystemRawStatus4 && (offset & 3) == 0)
            {
                return ReadBitmap(rawStatus, (int)((offset - (long)Registers.SystemRawStatus1) / 4));
            }
            if(offset >= (long)Registers.SystemEnabledStatus1 && offset <= (long)Registers.SystemEnabledStatus4 && (offset & 3) == 0)
            {
                return ReadEnabledStatus((int)((offset - (long)Registers.SystemEnabledStatus1) / 4));
            }
            if(offset >= (long)Registers.EnableSet1 && offset <= (long)Registers.EnableSet4 && (offset & 3) == 0)
            {
                return ReadBitmap(enabled, (int)((offset - (long)Registers.EnableSet1) / 4));
            }
            if(offset >= (long)Registers.EnableClear1 && offset <= (long)Registers.EnableClear4 && (offset & 3) == 0)
            {
                return ReadBitmap(enabled, (int)((offset - (long)Registers.EnableClear1) / 4));
            }
            if(offset >= (long)Registers.ChannelMap0 && offset <= (long)Registers.ChannelMap25 && (offset & 3) == 0)
            {
                var first = (int)((offset - (long)Registers.ChannelMap0));
                uint result = 0;
                for(var i = 0; i < 4; i++)
                {
                    var interrupt = first + i;
                    if(interrupt < NumberOfSystemInterrupts)
                    {
                        result |= (uint)channel[interrupt] << (i * 8);
                    }
                }
                return result;
            }

            switch((Registers)offset)
            {
            case Registers.Revision: return 0x4E82A900;
            case Registers.GlobalEnable: return globalEnable ? 1u : 0u;
            case Registers.VectorBase: return vectorBase;
            case Registers.VectorSize: return vectorSize;
            case Registers.VectorNull: return vectorNull;
            case Registers.GlobalPrioritizedIndex: return ReadPrioritizedIndex(null);
            case Registers.GlobalPrioritizedVector: return ReadPrioritizedVector(null);
            case Registers.HostPrioritizedIndex1: return ReadPrioritizedIndex(0);
            case Registers.HostPrioritizedIndex2: return ReadPrioritizedIndex(1);
            case Registers.HostEnable: return hostEnable;
            case Registers.HostPrioritizedVector1: return ReadPrioritizedVector(0);
            case Registers.HostPrioritizedVector2: return ReadPrioritizedVector(1);
            default: return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset >= (long)Registers.SystemRawStatus1 && offset <= (long)Registers.SystemRawStatus4 && (offset & 3) == 0)
            {
                SetBitmap(rawStatus, (int)((offset - (long)Registers.SystemRawStatus1) / 4), value, true);
                Update();
                return;
            }
            if(offset >= (long)Registers.SystemEnabledStatus1 && offset <= (long)Registers.SystemEnabledStatus4 && (offset & 3) == 0)
            {
                SetBitmap(rawStatus, (int)((offset - (long)Registers.SystemEnabledStatus1) / 4), value, false);
                Update();
                return;
            }
            if(offset >= (long)Registers.EnableSet1 && offset <= (long)Registers.EnableSet4 && (offset & 3) == 0)
            {
                SetBitmap(enabled, (int)((offset - (long)Registers.EnableSet1) / 4), value, true);
                Update();
                return;
            }
            if(offset >= (long)Registers.EnableClear1 && offset <= (long)Registers.EnableClear4 && (offset & 3) == 0)
            {
                SetBitmap(enabled, (int)((offset - (long)Registers.EnableClear1) / 4), value, false);
                Update();
                return;
            }
            if(offset >= (long)Registers.ChannelMap0 && offset <= (long)Registers.ChannelMap25 && (offset & 3) == 0)
            {
                var first = (int)(offset - (long)Registers.ChannelMap0);
                for(var i = 0; i < 4; i++)
                {
                    var interrupt = first + i;
                    if(interrupt < NumberOfSystemInterrupts)
                    {
                        channel[interrupt] = (byte)(value >> (i * 8));
                    }
                }
                Update();
                return;
            }

            switch((Registers)offset)
            {
            case Registers.GlobalEnable:
                globalEnable = (value & 1) != 0;
                break;
            case Registers.StatusIndexedSet:
                SetIndexed(rawStatus, value, true);
                break;
            case Registers.StatusIndexedClear:
                SetIndexed(rawStatus, value, false);
                break;
            case Registers.EnableIndexedSet:
                SetIndexed(enabled, value, true);
                break;
            case Registers.EnableIndexedClear:
                SetIndexed(enabled, value, false);
                break;
            case Registers.HostEnableIndexedSet:
                hostEnable |= 1u << (int)(value & 1);
                break;
            case Registers.HostEnableIndexedClear:
                hostEnable &= ~(1u << (int)(value & 1));
                break;
            case Registers.VectorBase:
                vectorBase = value;
                break;
            case Registers.VectorSize:
                vectorSize = value & 0x7;
                break;
            case Registers.VectorNull:
                vectorNull = value;
                break;
            case Registers.HostEnable:
                hostEnable = value & 0x3;
                break;
            }
            Update();
        }

        public long Size => 0x2000;
        public GPIO IRQ { get; }
        public GPIO FIQ { get; }

        private void SetIndexed(bool[] target, uint value, bool state)
        {
            var index = (int)(value & 0x7F);
            if(index < NumberOfSystemInterrupts)
            {
                target[index] = state;
            }
        }

        private static uint ReadBitmap(bool[] source, int bank)
        {
            uint result = 0;
            for(var bit = 0; bit < 32; bit++)
            {
                var index = bank * 32 + bit;
                if(index < source.Length && source[index])
                {
                    result |= 1u << bit;
                }
            }
            return result;
        }

        private uint ReadEnabledStatus(int bank)
        {
            uint result = 0;
            for(var bit = 0; bit < 32; bit++)
            {
                var index = bank * 32 + bit;
                if(index < NumberOfSystemInterrupts && rawStatus[index] && enabled[index])
                {
                    result |= 1u << bit;
                }
            }
            return result;
        }

        private static void SetBitmap(bool[] target, int bank, uint value, bool state)
        {
            for(var bit = 0; bit < 32; bit++)
            {
                var index = bank * 32 + bit;
                if(index < target.Length && (value & (1u << bit)) != 0)
                {
                    target[index] = state;
                }
            }
        }

        private int FindPending(int? host)
        {
            var best = -1;
            var bestChannel = int.MaxValue;
            for(var i = 0; i < NumberOfSystemInterrupts; i++)
            {
                if(!rawStatus[i] || !enabled[i])
                {
                    continue;
                }
                var mappedChannel = channel[i];
                if(host.HasValue && mappedChannel != host.Value)
                {
                    continue;
                }
                if(mappedChannel < bestChannel)
                {
                    bestChannel = mappedChannel;
                    best = i;
                }
            }
            return best;
        }

        private uint ReadPrioritizedIndex(int? host)
        {
            var pending = FindPending(host);
            return pending < 0 ? 1u << 31 : (uint)pending;
        }

        private uint ReadPrioritizedVector(int? host)
        {
            var pending = FindPending(host);
            if(pending < 0)
            {
                return vectorNull;
            }
            // VSR 0 means 4-byte slots, 1 means 8 bytes, and so on.
            return vectorBase + ((uint)pending << (int)(vectorSize + 2));
        }

        private void Update()
        {
            FIQ.Set(globalEnable && (hostEnable & 1) != 0 && FindPending(0) >= 0);
            IRQ.Set(globalEnable && (hostEnable & 2) != 0 && FindPending(1) >= 0);
        }

        private readonly bool[] rawStatus = new bool[NumberOfSystemInterrupts];
        private readonly bool[] enabled = new bool[NumberOfSystemInterrupts];
        private readonly byte[] channel = new byte[NumberOfSystemInterrupts];
        private bool globalEnable;
        private uint hostEnable;
        private uint vectorBase;
        private uint vectorSize;
        private uint vectorNull;

        private const int NumberOfSystemInterrupts = 101;

        private enum Registers : long
        {
            Revision = 0x000,
            GlobalEnable = 0x010,
            StatusIndexedSet = 0x020,
            StatusIndexedClear = 0x024,
            EnableIndexedSet = 0x028,
            EnableIndexedClear = 0x02C,
            HostEnableIndexedSet = 0x034,
            HostEnableIndexedClear = 0x038,
            VectorBase = 0x050,
            VectorSize = 0x054,
            VectorNull = 0x058,
            GlobalPrioritizedIndex = 0x080,
            GlobalPrioritizedVector = 0x084,
            SystemRawStatus1 = 0x200,
            SystemRawStatus4 = 0x20C,
            SystemEnabledStatus1 = 0x280,
            SystemEnabledStatus4 = 0x28C,
            EnableSet1 = 0x300,
            EnableSet4 = 0x30C,
            EnableClear1 = 0x380,
            EnableClear4 = 0x38C,
            ChannelMap0 = 0x400,
            ChannelMap25 = 0x464,
            HostPrioritizedIndex1 = 0x900,
            HostPrioritizedIndex2 = 0x904,
            HostEnable = 0x1500,
            HostPrioritizedVector1 = 0x1600,
            HostPrioritizedVector2 = 0x1604,
        }
    }
}
