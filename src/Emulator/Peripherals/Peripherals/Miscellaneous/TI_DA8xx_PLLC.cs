//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class TI_DA8xx_PLLC : IDoubleWordPeripheral, IKnownSize
    {
        public TI_DA8xx_PLLC(bool primary, bool postBootloaderState = false)
        {
            this.primary = primary;
            this.postBootloaderState = postBootloaderState;
            dividers = new uint[7];
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(TryGetDivider(offset, out var index)) return dividers[index];
            switch(offset)
            {
            case Revision: return primary ? PrimaryRevision : SecondaryRevision;
            case PLLControl: return pllControl;
            case Multiplier: return multiplier;
            case PreDivider: return primary ? preDivider : 0;
            case OscillatorDivider: return oscillatorDivider;
            case PostDivider: return postDivider;
            case Command: return 0;
            case Status: return pllStatus;
            case AlignmentControl: return primary ? 0x1FFu : 0x7u;
            case DividerChange: return dividerChange;
            case ClockEnable: return clockEnable;
            case ClockStatus: return primary ? clockEnable & 0x3u : clockEnable & 0x2u;
            case SystemStatus: return GetSystemStatus();
            default: return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(TryGetDivider(offset, out var index))
            {
                var updated = value & DividerMask;
                if(updated != dividers[index]) dividerChange |= 1u << index;
                dividers[index] = updated;
                return;
            }
            switch(offset)
            {
            case PLLControl: pllControl = (value & PLLControlMask) | PLLControlReserved; break;
            case Multiplier: multiplier = value & 0x1F; break;
            case PreDivider: if(primary) preDivider = value & DividerMask; break;
            case OscillatorDivider: oscillatorDivider = value & DividerMask; break;
            case PostDivider: postDivider = value & DividerMask; break;
            case Command: if((value & 1) != 0) dividerChange = 0; break;
            case ClockEnable: clockEnable = value & (primary ? 0x3u : 0x2u); break;
            }
        }

        public void Reset()
        {
            pllControl = 0x72;
            multiplier = 0x13;
            preDivider = 0x8000;
            oscillatorDivider = 0x8000;
            postDivider = 0x8001;
            pllStatus = 0;
            dividerChange = 0;
            clockEnable = primary ? 3u : 0u;
            if(primary)
            {
                var values = new uint[] { 0x8000, 0x8001, 0x8002, 0x8003, 0x8002, 0x8000, 0x8005 };
                values.CopyTo(dividers, 0);
            }
            else
            {
                dividers[0] = 0; dividers[1] = 1; dividers[2] = 2;
                for(var i = 3; i < dividers.Length; i++) dividers[i] = 0;
            }
            if(postBootloaderState && primary)
            {
                pllControl = 0x59;
                multiplier = 0x18;
                preDivider = 0x8000;
                postDivider = 0x8001;
                dividers[1] = 0x8001;
                dividers[5] = 0x8000;
                pllStatus = 1u << 2;
            }
        }

        public long Size => 0x1000;

        private bool TryGetDivider(long offset, out int index)
        {
            if(offset >= Divider1 && offset <= Divider3 && (offset & 3) == 0)
            {
                index = (int)((offset - Divider1) / 4); return true;
            }
            if(primary && offset >= Divider4 && offset <= Divider7 && (offset & 3) == 0)
            {
                index = 3 + (int)((offset - Divider4) / 4); return true;
            }
            index = -1; return false;
        }

        private uint GetSystemStatus()
        {
            uint value = 0;
            for(var i = 0; i < (primary ? 7 : 3); i++) if((dividers[i] & 0x8000) != 0) value |= 1u << i;
            return value;
        }

        private readonly bool primary, postBootloaderState;
        private readonly uint[] dividers;
        private uint pllControl, multiplier, preDivider, oscillatorDivider, postDivider, pllStatus, dividerChange, clockEnable;

        private const long Revision=0x0, PLLControl=0x100, Multiplier=0x110, PreDivider=0x114;
        private const long Divider1=0x118, Divider3=0x120, OscillatorDivider=0x124, PostDivider=0x128;
        private const long Command=0x138, Status=0x13C, AlignmentControl=0x140, DividerChange=0x144;
        private const long ClockEnable=0x148, ClockStatus=0x14C, SystemStatus=0x150, Divider4=0x160, Divider7=0x16C;
        private const uint PrimaryRevision=0x44813C00, SecondaryRevision=0x44814400;
        private const uint DividerMask=0x801F, PLLControlMask=0x32B, PLLControlReserved=0x10;
    }
}
