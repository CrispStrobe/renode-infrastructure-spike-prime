//
// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
//
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    // TI DA8xx/AM1808 GPIO controller. Pins are numbered as bank * 16 + pin;
    // the inherited Connections collection exposes the corresponding physical
    // pin levels, while OnGPIO injects levels driven by external devices.
    public class TI_DA8xx_GPIO : BaseGPIOPort, IDoubleWordPeripheral, IKnownSize
    {
        public TI_DA8xx_GPIO(IMachine machine) : base(machine, NumberOfPins)
        {
            direction = new uint[NumberOfRegisterGroups];
            output = new uint[NumberOfRegisterGroups];
            input = new uint[NumberOfRegisterGroups];
            risingEdge = new uint[NumberOfRegisterGroups];
            fallingEdge = new uint[NumberOfRegisterGroups];
            interruptStatus = new uint[NumberOfRegisterGroups];
            bankInterrupts = new GPIO[NumberOfBanks];
            for(var bank = 0; bank < NumberOfBanks; bank++) bankInterrupts[bank] = new GPIO();
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset == Revision) return RevisionValue;
            if(offset == BankInterruptEnable) return bankInterruptEnable;
            if(!TryDecode(offset, out var group, out var register)) return 0;
            var mask = GroupMask(group);
            switch(register)
            {
            case GroupRegister.Direction: return direction[group] & mask;
            case GroupRegister.Output:
            case GroupRegister.SetOutput:
            case GroupRegister.ClearOutput: return output[group] & mask;
            case GroupRegister.Input: return GetPhysicalLevels(group) & mask;
            case GroupRegister.SetRisingEdge:
            case GroupRegister.ClearRisingEdge: return risingEdge[group] & mask;
            case GroupRegister.SetFallingEdge:
            case GroupRegister.ClearFallingEdge: return fallingEdge[group] & mask;
            case GroupRegister.InterruptStatus: return interruptStatus[group] & mask;
            default: return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset == BankInterruptEnable)
            {
                bankInterruptEnable = value & 0x1FF;
                UpdateInterrupts();
                return;
            }
            if(!TryDecode(offset, out var group, out var register)) return;
            var mask = GroupMask(group);
            value &= mask;
            switch(register)
            {
            case GroupRegister.Direction:
                SetDirection(group, value);
                break;
            case GroupRegister.Output:
                SetOutput(group, value, mask);
                break;
            case GroupRegister.SetOutput:
                SetOutput(group, output[group] | value, value);
                break;
            case GroupRegister.ClearOutput:
                SetOutput(group, output[group] & ~value, value);
                break;
            case GroupRegister.SetRisingEdge:
                risingEdge[group] |= value;
                break;
            case GroupRegister.ClearRisingEdge:
                risingEdge[group] &= ~value;
                break;
            case GroupRegister.SetFallingEdge:
                fallingEdge[group] |= value;
                break;
            case GroupRegister.ClearFallingEdge:
                fallingEdge[group] &= ~value;
                break;
            case GroupRegister.InterruptStatus:
                interruptStatus[group] &= ~value;
                UpdateInterrupts();
                break;
            }
        }

        public override void OnGPIO(int number, bool value)
        {
            if(!CheckPinNumber(number)) return;
            var group = number / PinsPerGroup;
            var bit = 1u << (number % PinsPerGroup);
            if(value) input[group] |= bit;
            else input[group] &= ~bit;
            if((direction[group] & bit) != 0) SetPhysicalLevel(number, value);
        }

        public override void Reset()
        {
            base.Reset();
            bankInterruptEnable = 0;
            for(var group = 0; group < NumberOfRegisterGroups; group++)
            {
                direction[group] = GroupMask(group);
                output[group] = input[group] = risingEdge[group] = fallingEdge[group] = interruptStatus[group] = 0;
            }
            if(bankInterrupts != null)
            {
                foreach(var irq in bankInterrupts) irq.Set(false);
            }
        }

        public GPIO Bank0IRQ => bankInterrupts[0];
        public GPIO Bank1IRQ => bankInterrupts[1];
        public GPIO Bank2IRQ => bankInterrupts[2];
        public GPIO Bank3IRQ => bankInterrupts[3];
        public GPIO Bank4IRQ => bankInterrupts[4];
        public GPIO Bank5IRQ => bankInterrupts[5];
        public GPIO Bank6IRQ => bankInterrupts[6];
        public GPIO Bank7IRQ => bankInterrupts[7];
        public GPIO Bank8IRQ => bankInterrupts[8];
        public long Size => 0x1000;

        private void SetDirection(int group, uint value)
        {
            var changed = direction[group] ^ value;
            direction[group] = value;
            for(var bitIndex = 0; bitIndex < PinsPerGroup; bitIndex++)
            {
                var bit = 1u << bitIndex;
                if((changed & bit) == 0 || (GroupMask(group) & bit) == 0) continue;
                var physical = (direction[group] & bit) != 0 ? (input[group] & bit) != 0 : (output[group] & bit) != 0;
                SetPhysicalLevel(group * PinsPerGroup + bitIndex, physical);
            }
        }

        private void SetOutput(int group, uint value, uint selectedBits)
        {
            var writable = ~direction[group] & GroupMask(group) & selectedBits;
            var changed = (output[group] ^ value) & writable;
            output[group] = (output[group] & ~writable) | (value & writable);
            for(var bitIndex = 0; bitIndex < PinsPerGroup; bitIndex++)
            {
                var bit = 1u << bitIndex;
                if((changed & bit) != 0) SetPhysicalLevel(group * PinsPerGroup + bitIndex, (output[group] & bit) != 0);
            }
        }

        private void SetPhysicalLevel(int pin, bool value)
        {
            var previous = State[pin];
            State[pin] = value;
            Connections[pin].Set(value);
            if(previous == value) return;
            var group = pin / PinsPerGroup;
            var bit = 1u << (pin % PinsPerGroup);
            if((value && (risingEdge[group] & bit) != 0) || (!value && (fallingEdge[group] & bit) != 0))
            {
                interruptStatus[group] |= bit;
                UpdateInterrupts();
            }
        }

        private uint GetPhysicalLevels(int group)
        {
            uint result = 0;
            for(var bitIndex = 0; bitIndex < PinsPerGroup; bitIndex++)
            {
                var pin = group * PinsPerGroup + bitIndex;
                if(pin < NumberOfPins && State[pin]) result |= 1u << bitIndex;
            }
            return result;
        }

        private void UpdateInterrupts()
        {
            for(var bank = 0; bank < NumberOfBanks; bank++)
            {
                var group = bank / 2;
                var shift = (bank % 2) * PinsPerBank;
                var pending = ((interruptStatus[group] >> shift) & 0xFFFF) != 0;
                bankInterrupts[bank].Set(pending && (bankInterruptEnable & (1u << bank)) != 0);
            }
        }

        private static bool TryDecode(long offset, out int group, out GroupRegister register)
        {
            group = 0;
            register = 0;
            if(offset < GroupBase || offset > LastRegister || (offset & 3) != 0) return false;
            var relative = offset - GroupBase;
            group = (int)(relative / GroupStride);
            var withinGroup = relative % GroupStride;
            if(group >= NumberOfRegisterGroups || withinGroup > (long)GroupRegister.InterruptStatus) return false;
            register = (GroupRegister)withinGroup;
            return Enum.IsDefined(typeof(GroupRegister), register);
        }

        private static uint GroupMask(int group) => group == NumberOfRegisterGroups - 1 ? 0xFFFFu : UInt32.MaxValue;

        private readonly uint[] direction, output, input, risingEdge, fallingEdge, interruptStatus;
        private readonly GPIO[] bankInterrupts;
        private uint bankInterruptEnable;

        private const int NumberOfBanks=9, PinsPerBank=16, PinsPerGroup=32, NumberOfRegisterGroups=5;
        private const int NumberOfPins=NumberOfBanks * PinsPerBank;
        private const long Revision=0x00, BankInterruptEnable=0x08, GroupBase=0x10, GroupStride=0x28, LastRegister=0xD4;
        private const uint RevisionValue=0x44830105;

        private enum GroupRegister : long
        {
            Direction = 0x00,
            Output = 0x04,
            SetOutput = 0x08,
            ClearOutput = 0x0C,
            Input = 0x10,
            SetRisingEdge = 0x14,
            ClearRisingEdge = 0x18,
            SetFallingEdge = 0x1C,
            ClearFallingEdge = 0x20,
            InterruptStatus = 0x24,
        }
    }
}
