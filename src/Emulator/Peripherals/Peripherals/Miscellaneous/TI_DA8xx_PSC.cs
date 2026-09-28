//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // TI DA8xx Power and Sleep Controller. Transitions are deliberately
    // synchronous: software still observes the documented command interface,
    // but no host-time latency is introduced into deterministic simulation.
    public class TI_DA8xx_PSC : IDoubleWordPeripheral, IKnownSize
    {
        public TI_DA8xx_PSC(int moduleCount, long initiallyEnabledModules = 0,
            int powerDomain1Module = -1, bool postBootloaderState = false)
        {
            if(moduleCount < 1 || moduleCount > MaximumModuleCount)
            {
                throw new ArgumentOutOfRangeException(nameof(moduleCount));
            }
            if(powerDomain1Module >= moduleCount)
            {
                throw new ArgumentOutOfRangeException(nameof(powerDomain1Module));
            }
            this.moduleCount = moduleCount;
            this.initiallyEnabledModules = unchecked((ulong)initiallyEnabledModules);
            this.powerDomain1Module = powerDomain1Module;
            this.postBootloaderState = postBootloaderState;
            moduleControl = new uint[moduleCount];
            moduleState = new uint[moduleCount];
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(IsIndexed(offset, ModuleStatusBase, moduleCount, out var module))
            {
                return moduleState[module];
            }
            if(IsIndexed(offset, ModuleControlBase, moduleCount, out module))
            {
                return moduleControl[module];
            }
            switch(offset)
            {
            case Revision: return RevisionValue;
            case TransitionCommand: return 0;
            case TransitionStatus: return 0;
            case PowerDomainStatus0: return powerDomainState[0];
            case PowerDomainStatus1: return powerDomainState[1];
            case PowerDomainControl0: return powerDomainControl[0];
            case PowerDomainControl1: return powerDomainControl[1];
            default: return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(IsIndexed(offset, ModuleControlBase, moduleCount, out var module))
            {
                moduleControl[module] = value & ModuleControlMask;
                UpdateLocalReset(module);
                return;
            }
            switch(offset)
            {
            case TransitionCommand:
                if((value & 1) != 0) CompleteTransition(0);
                if((value & 2) != 0) CompleteTransition(1);
                break;
            case PowerDomainControl0:
                powerDomainControl[0] = (value & PowerDomainControlMask) | PowerDomainReservedResetBit;
                break;
            case PowerDomainControl1:
                powerDomainControl[1] = (value & PowerDomainControlMask) | PowerDomainReservedResetBit;
                break;
            }
        }

        public void Reset()
        {
            powerDomainControl = new[] { PowerDomainControlReset, PowerDomainControlReset };
            powerDomainState = new uint[2];
            for(var i = 0; i < moduleCount; i++)
            {
                moduleControl[i] = 0;
                moduleState[i] = ModuleStatusReset;
            }
            if(!postBootloaderState)
            {
                return;
            }
            powerDomainState[0] = PoweredPowerDomainStatus;
            if(powerDomain1Module >= 0)
            {
                powerDomainState[1] = PoweredPowerDomainStatus;
            }
            for(var i = 0; i < moduleCount; i++)
            {
                if((initiallyEnabledModules & (1UL << i)) == 0) continue;
                moduleControl[i] = EnabledState;
                ApplyModuleState(i, EnabledState);
            }
        }

        public long Size => 0x1000;

        private void CompleteTransition(int domain)
        {
            powerDomainState[domain] = (powerDomainControl[domain] & 1) != 0 ? PoweredPowerDomainStatus : 0;
            for(var i = 0; i < moduleCount; i++)
            {
                if(GetDomain(i) == domain) ApplyModuleState(i, moduleControl[i] & StateMask);
            }
        }

        private void ApplyModuleState(int module, uint state)
        {
            var result = ModuleStatusReset | state;
            if(state == SyncResetState || state == EnabledState) result |= ModuleClockOutput;
            if(state == EnabledState) result |= ModuleResetStatus;
            if((moduleControl[module] & LocalReset) != 0) result |= LocalReset;
            else result &= ~LocalReset;
            moduleState[module] = result;
        }

        private void UpdateLocalReset(int module)
        {
            moduleState[module] = (moduleState[module] & ~LocalReset) | (moduleControl[module] & LocalReset);
        }

        private int GetDomain(int module) => module == powerDomain1Module ? 1 : 0;

        private static bool IsIndexed(long offset, long start, int count, out int index)
        {
            index = (int)((offset - start) / 4);
            return offset >= start && offset < start + count * 4 && (offset & 3) == 0;
        }

        private readonly int moduleCount;
        private readonly ulong initiallyEnabledModules;
        private readonly int powerDomain1Module;
        private readonly bool postBootloaderState;
        private readonly uint[] moduleControl;
        private readonly uint[] moduleState;
        private uint[] powerDomainControl;
        private uint[] powerDomainState;

        private const int MaximumModuleCount = 32;
        private const long Revision = 0x0, TransitionCommand = 0x120, TransitionStatus = 0x128;
        private const long PowerDomainStatus0 = 0x200, PowerDomainStatus1 = 0x204;
        private const long PowerDomainControl0 = 0x300, PowerDomainControl1 = 0x304;
        private const long ModuleStatusBase = 0x800, ModuleControlBase = 0xA00;
        private const uint RevisionValue = 0x44825A00;
        private const uint PowerDomainControlReset = 0x001FF101;
        private const uint PowerDomainReservedResetBit = 1u << 8;
        private const uint PowerDomainControlMask = 0x001FFF01;
        private const uint PoweredPowerDomainStatus = 0x301;
        private const uint ModuleStatusReset = 0xB00;
        private const uint ModuleControlMask = 0x80000307;
        private const uint StateMask = 0x7;
        private const uint SyncResetState = 1, EnabledState = 3;
        private const uint LocalReset = 1u << 8, ModuleResetStatus = 1u << 10, ModuleClockOutput = 1u << 12;
    }
}
