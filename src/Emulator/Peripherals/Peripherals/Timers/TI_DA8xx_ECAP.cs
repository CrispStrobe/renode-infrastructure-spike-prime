// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Timers
{
    // APWM subset of the DA8xx eCAP; capture and synchronization are explicit
    // unsupported configurations. Register facts: TI SPRUH82C chapter 15.
    public class TI_DA8xx_ECAP : IDoubleWordPeripheral, IWordPeripheral, IBytePeripheral, IKnownSize, IPWMDutyCycleSource
    {
        public TI_DA8xx_ECAP(IMachine machine, ulong frequency = 100000000, uint maximumCycleFrequency = 100000)
        {
            if(frequency == 0 || maximumCycleFrequency == 0 || maximumCycleFrequency > 200000)
                throw new ConstructionException("APWM needs positive frequency and cycle bound 1..200000 Hz");
            this.frequency = frequency; this.maximumCycleFrequency = maximumCycleFrequency;
            timer = new LimitTimer(machine.ClockSource, frequency, this, "APWM events", direction: Direction.Ascending,
                workMode: WorkMode.OneShot, eventEnabled: true);
            timer.LimitReached += OnEvent;
            Output = new GPIO(); IRQ = new GPIO(); Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
            case 0: return (uint)CurrentCounter;
            case 4: return phaseRegister;
            case 8: return period;
            case 0xC: return compare;
            case 0x10: return shadowPeriod;
            case 0x14: return shadowCompare;
            case 0x5C: return 0x44D22100;
            default: return (uint)(ReadControl(offset) | ((uint)ReadControl(offset + 2) << 16));
            }
        }
        public ushort ReadWord(long offset) => (ushort)(ReadDoubleWord(offset & ~3L) >> ((int)(offset & 2) * 8));
        public byte ReadByte(long offset) => (byte)(ReadDoubleWord(offset & ~3L) >> ((int)(offset & 3) * 8));

        public void WriteDoubleWord(long offset, uint value)
        {
            var wasRunning = timer.Enabled;
            counter = CurrentCounter; timer.Enabled = false;
            switch(offset)
            {
            case 0: counter = value; break;
            case 4: phaseRegister = value; break;
            case 8: period = shadowPeriod = value; break;
            case 0xC: compare = shadowCompare = value; break;
            case 0x10: shadowPeriod = value; break;
            case 0x14: shadowCompare = value; break;
            default: WriteControl(offset, (ushort)value); WriteControl(offset + 2, (ushort)(value >> 16)); break;
            }
            Validate();
            if(!wasRunning && CounterRunning && counter == 0) Drive();
            if(!Operational || (control2 & 0x200) == 0) Output.Unset();
            UpdateInterrupts(); Schedule();
        }

        public void WriteWord(long offset, ushort value)
        {
            if(offset >= 0x28 && offset <= 0x32)
            {
                var wasRunning = timer.Enabled;
                counter = CurrentCounter; timer.Enabled = false;
                WriteControl(offset, value); Validate();
                if(CounterRunning && (offset == 0x2A || (!wasRunning && counter == 0))) Drive();
                if(!Operational || (control2 & 0x200) == 0) Output.Unset();
                UpdateInterrupts(); Schedule();
                return;
            }
            var aligned = offset & ~3L;
            var shift = (int)(offset & 2) * 8;
            WriteDoubleWord(aligned, (ReadDoubleWord(aligned) & ~(0xFFFFu << shift)) | ((uint)value << shift));
        }
        public void WriteByte(long offset, byte value)
        {
            var aligned = offset & ~1L;
            var shift = (int)(offset & 1) * 8;
            var data = (ushort)(value << shift);
            if(aligned != 0x30 && aligned != 0x32) data |= (ushort)(ReadWord(aligned) & ~(0xFF << shift));
            WriteWord(aligned, data);
        }

        public void Reset()
        {
            timer.Reset(); counter = nextCounter = 0;
            period = compare = shadowPeriod = shadowCompare = phaseRegister = 0;
            control1 = interruptEnable = flags = 0; control2 = 6;
            boundaryPending = false; UnsupportedConfiguration = null;
            Output.Unset(); IRQ.Unset();
        }

        public double GetDutyCycle(int channel)
        {
            if(channel != 0) throw new ArgumentOutOfRangeException(nameof(channel));
            if(!Operational || (control2 & 0x200) == 0) return 0;
            if(!CounterRunning) return Output.IsSet ? 1 : 0;
            var fraction = Math.Min((double)compare, CycleLength) / CycleLength;
            return Inverted ? 1 - fraction : fraction;
        }
        public double DutyCycle => GetDutyCycle(0);
        public double CycleFrequencyHz => frequency / (double)CycleLength;
        public bool Operational => UnsupportedConfiguration == null;
        public bool CounterRunning => Operational && (control2 & 0x210) == 0x210;
        public string UnsupportedConfiguration { get; private set; }
        public GPIO Output { get; }
        public GPIO IRQ { get; }
        public long Size => 0x1000;

        private ushort ReadControl(long offset)
        {
            switch(offset)
            {
            case 0x28: return control1;
            case 0x2A: return control2;
            case 0x2C: return interruptEnable;
            case 0x2E: return flags;
            default: return 0;
            }
        }
        private void WriteControl(long offset, ushort value)
        {
            switch(offset)
            {
            case 0x28: control1 = value; break;
            case 0x2A: control2 = (ushort)(value & 0x7FF); break;
            case 0x2C: interruptEnable = (ushort)(value & 0xFE); break;
            case 0x30: flags &= (ushort)~(value & 0xFF); break;
            case 0x32: flags |= (ushort)(value & 0xFE); break;
            }
        }

        private void Validate()
        {
            var diagnostic = (control2 & 0x120) != 0 ? "APWM phase synchronization is unsupported" :
                (control2 & 0x210) == 0x10 ? "eCAP capture mode is unsupported" :
                (control2 & 0x210) == 0x210 && CycleFrequencyHz > maximumCycleFrequency ? "APWM cycle rate exceeds configured bound" :
                counter > period && !boundaryPending && (control2 & 0x210) == 0x210 ? "APWM counter above period is unsupported" : null;
            if(diagnostic != null && diagnostic != UnsupportedConfiguration) this.Log(LogLevel.Warning, diagnostic);
            UnsupportedConfiguration = diagnostic;
        }

        private void OnEvent()
        {
            counter = nextCounter;
            if(boundaryPending)
            {
                boundaryPending = false; counter = 0;
                Drive();
                if(compare == 0) flags |= 0x80;
            }
            else
            {
                if(counter == compare) flags |= 0x80;
                Drive();
                if(counter == period)
                {
                    flags |= 0x40;
                    if(period == uint.MaxValue) flags |= 0x20;
                    var oldPeriod = period;
                    period = shadowPeriod; compare = shadowCompare;
                    boundaryPending = oldPeriod != 0;
                }
            }
            Validate(); if(!Operational) Output.Unset(); UpdateInterrupts(); Schedule();
        }

        private void Schedule()
        {
            timer.Enabled = false;
            if(!CounterRunning) return;
            ulong distance;
            if(boundaryPending) { distance = 1; nextCounter = 0; }
            else
            {
                nextCounter = compare > counter && compare <= period ? compare : period;
                // Zero-period APWM requires a one-tick cycle. The frequency bound
                // prevents runaway callbacks in this otherwise valid case.
                distance = nextCounter > counter ? nextCounter - counter : 1;
            }
            timer.Limit = distance; timer.ResetValue(); timer.Enabled = true;
        }

        private void Drive() => Output.Set((counter < compare) != Inverted);
        private void UpdateInterrupts()
        {
            if((flags & interruptEnable & 0xFE) != 0) flags |= 1;
            IRQ.Set(Operational && (flags & 1) != 0);
        }
        private bool Inverted => (control2 & 0x400) != 0;
        private ulong CycleLength => (ulong)period + 1;
        private ulong CurrentCounter => timer.Enabled ? counter + timer.Value : counter;
        private readonly LimitTimer timer;
        private readonly ulong frequency;
        private readonly uint maximumCycleFrequency;
        private uint period, compare, shadowPeriod, shadowCompare, phaseRegister;
        private ulong counter, nextCounter;
        private ushort control1, control2, interruptEnable, flags;
        private bool boundaryPending;
    }
}
