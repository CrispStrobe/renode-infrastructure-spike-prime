// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Timers
{
    // Functional base-resolution PWM. TI SPRUH82C chapter 16 defines the
    // halfword register map, counter divisors, qualifier priorities and IRQs.
    public class TI_DA8xx_EHRPWM : IWordPeripheral, IBytePeripheral, IDoubleWordPeripheral, IKnownSize, IPWMDutyCycleSource
    {
        public TI_DA8xx_EHRPWM(IMachine machine, ulong frequency = 100000000, uint maximumCycleFrequency = 100000)
        {
            if(frequency == 0 || maximumCycleFrequency == 0 || maximumCycleFrequency > 200000)
            {
                throw new ConstructionException("PWM frequency must be positive; cycle bound must be 1..200000 Hz");
            }
            this.frequency = frequency;
            this.maximumCycleFrequency = maximumCycleFrequency;
            timer = new LimitTimer(machine.ClockSource, frequency, this, "PWM events", direction: Direction.Ascending,
                workMode: WorkMode.OneShot, eventEnabled: true);
            timer.LimitReached += OnEvent;
            OutputA = new GPIO(); OutputB = new GPIO(); IRQ = new GPIO(); TripIRQ = new GPIO();
            Reset();
        }

        public ushort ReadWord(long offset)
        {
            if(offset == TBCNT) return (ushort)CounterAt(CurrentPhase);
            if(offset == TBSTS) return (ushort)((registers[TBSTS / 2] & 6) | (IsDown(CurrentPhase) ? 0 : 1));
            if(offset == CMPCTL) return (ushort)(registers[CMPCTL / 2] | (shadowFull[0] ? 0x100 : 0) | (shadowFull[1] ? 0x200 : 0));
            if(offset == ETPS) return (ushort)((registers[ETPS / 2] & 3) | (interruptCount << 2));
            if(offset == TZCLR || offset == TZFRC || offset == ETCLR || offset == ETFRC) return 0;
            return offset >= 0 && offset < registers.Length * 2 && (offset & 1) == 0 ? registers[offset / 2] : (ushort)0;
        }

        public byte ReadByte(long offset) => (byte)(ReadWord(offset & ~1L) >> ((int)(offset & 1) * 8));
        public uint ReadDoubleWord(long offset) => (uint)(ReadWord(offset) | ((uint)ReadWord(offset + 2) << 16));
        public void WriteDoubleWord(long offset, uint value) { WriteWord(offset, (ushort)value); WriteWord(offset + 2, (ushort)(value >> 16)); }
        public void WriteByte(long offset, byte value)
        {
            var aligned = offset & ~1L;
            var shift = (int)(offset & 1) * 8;
            var data = (ushort)(value << shift);
            if(aligned != TBSTS && aligned != TZCLR && aligned != TZFRC && aligned != ETCLR && aligned != ETFRC && aligned != AQSFRC)
            {
                data |= (ushort)(ReadWord(aligned) & ~(0xFF << shift));
            }
            WriteWord(aligned, data);
        }

        public void WriteWord(long offset, ushort value)
        {
            var wasRunning = timer.Enabled;
            phase = CurrentPhase;
            var currentCounter = CounterAt(phase);
            var wasDown = IsDown(phase);
            timer.Enabled = false;
            switch(offset)
            {
            case TBCTL:
                registers[offset / 2] = (ushort)(value & ~0x40);
                if((value & 0x40) != 0) registers[TBSTS / 2] |= 2;
                phase = Mode == 2 && wasDown ? 2 * period - Math.Min(period, currentCounter) : PhaseForCounter(currentCounter);
                break;
            case TBSTS: registers[offset / 2] &= (ushort)~(value & 6); break;
            case TBCNT: phase = PhaseForCounter(value); break;
            case TBPRD:
                registers[offset / 2] = value;
                if((registers[TBCTL / 2] & 8) != 0)
                {
                    period = value;
                    phase = Mode == 2 && wasDown ? 2 * period - Math.Min(period, currentCounter) : PhaseForCounter(currentCounter);
                }
                break;
            case CMPA: case CMPB:
                var channel = offset == CMPA ? 0 : 1;
                registers[offset / 2] = value;
                if(ImmediateCompare(channel)) compare[channel] = value;
                else shadowFull[channel] = true;
                break;
            case CMPCTL: registers[offset / 2] = (ushort)(value & 0x5F); break;
            case AQCTLA: case AQCTLB: registers[offset / 2] = (ushort)(value & 0xFFF); break;
            case AQSFRC:
                registers[offset / 2] = (ushort)(value & 0xDB);
                if((value & 4) != 0) rawLevel[0] = ApplyAction(rawLevel[0], value & 3);
                if((value & 0x20) != 0) rawLevel[1] = ApplyAction(rawLevel[1], (value >> 3) & 3);
                break;
            case AQCSFRC: registers[offset / 2] = (ushort)(value & 0xF); break;
            case TZCTL: registers[offset / 2] = (ushort)(value & 0xF); break;
            case TZEINT: registers[offset / 2] = (ushort)(value & 6); break;
            case TZCLR:
                registers[TZFLG / 2] &= (ushort)~(value & 7);
                if((value & 4) != 0) oneShotTrip = false;
                break;
            case TZFRC:
                if((value & 4) != 0) { oneShotTrip = true; registers[TZFLG / 2] |= 4; }
                if((value & 2) != 0) { cycleTrip = true; registers[TZFLG / 2] |= 2; }
                break;
            case ETSEL: registers[offset / 2] = (ushort)(value & 0xF); break;
            case ETPS: registers[offset / 2] = (ushort)(value & 3); MaybeInterrupt(); break;
            case ETCLR: registers[ETFLG / 2] &= (ushort)~(value & 1); break;
            case ETFRC: if((value & 1) != 0) InterruptEvent(); break;
            case TZFLG: case ETFLG: break;
            default:
                if(offset >= 0 && offset < registers.Length * 2 && (offset & 1) == 0) registers[offset / 2] = value;
                else if(value != 0) extraFeatureWritten = true;
                break;
            }
            if(!wasRunning && Mode != 3)
            {
                LoadShadow(currentCounter == 0, currentCounter == period);
                phase = PhaseForCounter(currentCounter);
                Validate();
                if(Operational && currentCounter == 0) Dispatch();
            }
            Validate();
            DriveOutputs();
            UpdateInterrupts();
            Schedule();
        }

        public void Reset()
        {
            timer.Reset(); Array.Clear(registers, 0, registers.Length);
            Array.Clear(compare, 0, compare.Length); Array.Clear(shadowFull, 0, shadowFull.Length);
            Array.Clear(rawLevel, 0, rawLevel.Length);
            registers[TBCTL / 2] = 0x83;
            period = phase = nextPhase = 0; interruptCount = 0;
            oneShotTrip = cycleTrip = extraFeatureWritten = false;
            UnsupportedConfiguration = null;
            OutputA.Unset(); OutputB.Unset(); IRQ.Unset(); TripIRQ.Unset();
        }

        public double GetDutyCycle(int channel)
        {
            if(channel < 0 || channel > 1) throw new ArgumentOutOfRangeException(nameof(channel));
            if(UnsupportedConfiguration != null) return 0;
            var forced = ForcedLevel(channel);
            if(forced.HasValue) return forced.Value ? 1 : 0;
            if(Mode == 3) return rawLevel[channel] ? 1 : 0;
            var events = EventPhases();
            var level = rawLevel[channel];
            double highTicks = 0;
            // Two cycles handle toggle-only qualifiers with a two-cycle period.
            for(var cycle = 0; cycle < 4; cycle++)
            {
                for(var i = 0; i < events.Count; i++)
                {
                    level = QualifiedLevel(channel, events[i], level);
                    if(cycle >= 2 && level) highTicks += (i + 1 < events.Count ? events[i + 1] : CycleLength) - events[i];
                }
            }
            return highTicks / (2 * (double)CycleLength);
        }

        public double DutyCycleA => GetDutyCycle(0);
        public double DutyCycleB => GetDutyCycle(1);
        public double CycleFrequencyHz => frequency / ((double)Divider * CycleLength);
        public bool Operational => UnsupportedConfiguration == null;
        public bool CounterRunning => Mode != 3 && Operational;
        public string UnsupportedConfiguration { get; private set; }
        public GPIO OutputA { get; }
        public GPIO OutputB { get; }
        public GPIO IRQ { get; }
        public GPIO TripIRQ { get; }
        public long Size => 0x2000;

        private void Validate()
        {
            var diagnostic = (Mode == 2 && period == 0) ? "Up-down zero period is unsupported" :
                (Mode != 3 && CycleFrequencyHz > maximumCycleFrequency) ? "PWM cycle rate exceeds configured bound" :
                ((registers[TBCTL / 2] & 4) != 0 || registers[DBCTL / 2] != 0 || registers[PCCTL / 2] != 0
                    || registers[TZSEL / 2] != 0 || registers[CMPAHR / 2] != 0 || registers[TBPHSHR / 2] != 0 || extraFeatureWritten)
                    ? "Phase synchronization, deadband, chopper, external trip and HRPWM extensions are unsupported" :
                (registers[AQCSFRC / 2] != 0 && (registers[AQSFRC / 2] & 0xC0) != 0xC0)
                    ? "Shadow continuous software force is unsupported; select immediate load" : null;
            if(diagnostic != null && diagnostic != UnsupportedConfiguration) this.Log(LogLevel.Warning, diagnostic);
            UnsupportedConfiguration = diagnostic;
        }

        private void OnEvent()
        {
            phase = nextPhase;
            Dispatch(); Validate(); DriveOutputs(); UpdateInterrupts(); Schedule();
        }

        private void Dispatch()
        {
            var zero = CounterAt(phase) == 0;
            var atPeriod = CounterAt(phase) == period;
            LoadShadow(zero, atPeriod);
            if(zero && Mode == 1) phase = period;
            Validate();
            if(!Operational) return;
            if(zero) cycleTrip = false;
            if(CounterAt(phase) == ushort.MaxValue) registers[TBSTS / 2] |= 4;
            for(var channel = 0; channel < 2; channel++) rawLevel[channel] = QualifiedLevel(channel, phase, rawLevel[channel]);
            var selection = registers[ETSEL / 2] & 7;
            if((selection == 1 && zero) || (selection == 2 && atPeriod) || (selection == 3 && (zero || atPeriod))
                || (selection >= 4 && HasEvent(phase, selection - 2))) InterruptEvent();
        }

        private void LoadShadow(bool zero, bool atPeriod)
        {
            if(zero && (registers[TBCTL / 2] & 8) == 0) period = registers[TBPRD / 2];
            for(var channel = 0; channel < 2; channel++)
            {
                var load = (registers[CMPCTL / 2] >> (channel * 2)) & 3;
                if(!ImmediateCompare(channel) && ((zero && (load == 0 || load == 2)) || (atPeriod && (load == 1 || load == 2))))
                {
                    compare[channel] = registers[(channel == 0 ? CMPA : CMPB) / 2]; shadowFull[channel] = false;
                }
            }
        }

        private void Schedule()
        {
            timer.Enabled = false;
            if(!CounterRunning) return;
            phase %= CycleLength;
            var next = EventPhases().FirstOrDefault(x => x > phase);
            nextPhase = next;
            var distance = next > phase ? next - phase : CycleLength - phase;
            timer.Divider = Divider; timer.Limit = Math.Max(1u, distance); timer.ResetValue(); timer.Enabled = true;
        }

        private List<uint> EventPhases()
        {
            var events = new SortedSet<uint> { 0, period };
            if(Mode == 2) events.Add(period);
            foreach(var cmp in compare)
            {
                if(cmp > period) continue;
                events.Add(Mode == 1 ? period - cmp : cmp);
                if(Mode == 2) events.Add((2 * period - cmp) % CycleLength);
            }
            return events.Where(x => x < CycleLength).ToList();
        }

        private bool QualifiedLevel(int channel, uint eventPhase, bool initial)
        {
            var priority = Mode == 0 ? new[] { 1, 4, 2, 0 } : Mode == 1 ? new[] { 0, 5, 3, 1 }
                : IsDown(eventPhase) ? new[] { 5, 3, 1, 4, 2, 0 } : new[] { 4, 2, 0, 5, 3, 1 };
            var qualifier = registers[(channel == 0 ? AQCTLA : AQCTLB) / 2];
            foreach(var evt in priority)
            {
                var action = (qualifier >> (evt * 2)) & 3;
                if(action != 0 && HasEvent(eventPhase, evt)) return ApplyAction(initial, action);
            }
            return initial;
        }

        private bool HasEvent(uint eventPhase, int evt)
        {
            var counter = CounterAt(eventPhase);
            if(evt == 0) return counter == 0;
            if(evt == 1) return counter == period;
            var channel = evt >= 4 ? 1 : 0;
            if(counter != compare[channel]) return false;
            var downEvent = (evt & 1) != 0;
            // Both compare directions can occur at PRD in up-down mode.
            return (Mode == 2 && counter == period) || downEvent == IsDown(eventPhase);
        }

        private bool? ForcedLevel(int channel)
        {
            if(oneShotTrip || cycleTrip)
            {
                var action = (registers[TZCTL / 2] >> (channel * 2)) & 3;
                if(action != 3) return action == 2; // High-Z represented as undriven/low.
            }
            var force = (registers[AQCSFRC / 2] >> (channel * 2)) & 3;
            return force == 1 ? false : force == 2 ? true : (bool?)null;
        }

        private void DriveOutputs()
        {
            OutputA.Set(UnsupportedConfiguration == null && (ForcedLevel(0) ?? rawLevel[0]));
            OutputB.Set(UnsupportedConfiguration == null && (ForcedLevel(1) ?? rawLevel[1]));
        }

        private void InterruptEvent() { if((registers[ETPS / 2] & 3) != 0 && interruptCount < 3) interruptCount++; MaybeInterrupt(); }
        private void MaybeInterrupt()
        {
            var threshold = registers[ETPS / 2] & 3;
            if(threshold != 0 && interruptCount == threshold && (registers[ETSEL / 2] & 8) != 0 && registers[ETFLG / 2] == 0)
            {
                registers[ETFLG / 2] = 1; interruptCount = 0;
            }
        }
        private void UpdateInterrupts()
        {
            if((registers[TZFLG / 2] & registers[TZEINT / 2] & 6) != 0) registers[TZFLG / 2] |= 1;
            IRQ.Set(UnsupportedConfiguration == null && registers[ETFLG / 2] != 0);
            TripIRQ.Set(UnsupportedConfiguration == null && (registers[TZFLG / 2] & 1) != 0);
        }
        private bool ImmediateCompare(int channel) => (registers[CMPCTL / 2] & (channel == 0 ? 0x10 : 0x40)) != 0;
        private static bool ApplyAction(bool level, int action) => action == 1 ? false : action == 2 ? true : action == 3 ? !level : level;
        private int Mode => registers[TBCTL / 2] & 3;
        private uint CycleLength => Mode == 2 ? Math.Max(1u, 2 * period) : period + 1;
        private ulong Divider => (ulong)(1 << ((registers[TBCTL / 2] >> 10) & 7)) * (ulong)(((registers[TBCTL / 2] >> 7) & 7) == 0 ? 1 : 2 * ((registers[TBCTL / 2] >> 7) & 7));
        private uint CurrentPhase => timer.Enabled ? (phase + (uint)timer.Value) % CycleLength : phase;
        private bool IsDown(uint value) => Mode == 1 || (Mode == 2 && value >= period);
        private uint CounterAt(uint value) => Mode == 1 ? period - Math.Min(period, value) : Mode == 2 && value >= period ? 2 * period - Math.Min(2 * period, value) : value;
        private uint PhaseForCounter(uint value) => Mode == 1 ? period - Math.Min(period, value) : Math.Min(period, value);

        private readonly LimitTimer timer;
        private readonly ulong frequency;
        private readonly uint maximumCycleFrequency;
        private readonly ushort[] registers = new ushort[0x40 / 2];
        private readonly uint[] compare = new uint[2];
        private readonly bool[] shadowFull = new bool[2], rawLevel = new bool[2];
        private uint period, phase, nextPhase;
        private int interruptCount;
        private bool oneShotTrip, cycleTrip, extraFeatureWritten;
        private const int TBCTL=0, TBSTS=2, TBPHSHR=4, TBCNT=8, TBPRD=0xA, CMPCTL=0xE, CMPAHR=0x10, CMPA=0x12, CMPB=0x14;
        private const int AQCTLA=0x16, AQCTLB=0x18, AQSFRC=0x1A, AQCSFRC=0x1C, DBCTL=0x1E, TZSEL=0x24, TZCTL=0x28;
        private const int TZEINT=0x2A, TZFLG=0x2C, TZCLR=0x2E, TZFRC=0x30, ETSEL=0x32, ETPS=0x34, ETFLG=0x36, ETCLR=0x38, ETFRC=0x3A, PCCTL=0x3C;
    }
}
