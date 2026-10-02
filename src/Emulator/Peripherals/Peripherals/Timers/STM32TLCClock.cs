// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Timers
{
    // Specialized 16-bit TIM12 channel-2 GSCLK source, not a general timer.
    // Counts every physical source-clock tick and PWM rising edge analytically
    // from machine virtual nanoseconds; it does not reduce the PWM frequency.
    // A 1 kHz observer bounds notification work, not hardware clock rate.
    // Register accesses and TLC observations synchronize at their exact time.
    // No individual GPIO pulse trace is exposed. Only the TLC aggregate sink
    // may consume this clock. IRQ/DMA/TRGO, channel 1, input/slave, one-pulse,
    // down/center-aligned, and non-PWM output modes reject nonzero requests.
    // PSC is buffered; ARR/CCR2 honor ARPE/OC2PE. UG resets CNT/prescaler and
    // transfers buffers. UDIS suppresses transfers/UIF, URS suppresses UG UIF.
    // This TIM12 subset has no advanced-timer BDTR/MOE or RCC clock gating;
    // its constructor frequency must be the actual enabled peripheral clock.
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class STM32TLCClock : IDoubleWordPeripheral, IKnownSize, IGPIOReceiver
    {
        public STM32TLCClock(IMachine machine, ulong frequency, TLC5955 display, uint initialLimit = 0xFFFF)
        {
            if(frequency == 0 || frequency > TimeInterval.TicksPerSecond || initialLimit == 0 || initialLimit > 0xFFFF)
            {
                throw new ConstructionException("GSCLK requires a 1..1GHz source and 16-bit nonzero initial ARR");
            }
            this.machine = machine;
            sysbus = machine.GetSystemBus(this);
            this.frequency = frequency;
            this.display = display ?? throw new ConstructionException("GSCLK requires a TLC5955 aggregate sink");
            this.initialLimit = initialLimit;
            observer = new LimitTimer(machine.ClockSource, 1000, this, "GSCLK observer", limit: 1,
                direction: Direction.Ascending, eventEnabled: true, autoUpdate: true);
            observer.LimitReached += Synchronize;
            Reset();
            display.SetGrayscaleClockSynchronizer(Synchronize);
        }

        public uint ReadDoubleWord(long offset)
        {
            SynchronizeCpuTime();
            Synchronize();
            switch(offset)
            {
            case 0x00: return control;
            case 0x10: return status;
            case 0x18: return compareMode;
            case 0x20: return compareEnable;
            case 0x24: return counter;
            case 0x28: return prescaler;
            case 0x2C: return reload;
            case 0x38: return compare;
            default: return 0; // absent/reserved TIM12 registers
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            SynchronizeCpuTime();
            Synchronize();
            switch(offset)
            {
            case 0x00:
                RejectBits(value, 0x387, "CR1 direction/center alignment/one-pulse");
                control = value;
                break;
            case 0x10:
                status &= value; // rc_w0
                break;
            case 0x14:
                RejectBits(value, 1, "EGR capture/trigger generation");
                if((value & 1) != 0)
                {
                    counter = 0;
                    prescalerPhase = 0;
                    if(!UpdatesDisabled)
                    {
                        TransferBuffers();
                        if((control & 4) == 0) status |= 1;
                    }
                }
                break;
            case 0x18:
                RejectBits(value, 0x7C00, "CCMR1 channel-1/input/clear mode");
                ValidatePwm(value, compareEnable);
                compareMode = value;
                if((value & 0x800) == 0) activeCompare = compare;
                break;
            case 0x20:
                RejectBits(value, 0x30, "CCER channel-1/complementary outputs");
                ValidatePwm(compareMode, value);
                compareEnable = value;
                break;
            case 0x24:
                counter = value & 0xFFFF;
                break;
            case 0x28:
                prescaler = value & 0xFFFF;
                break;
            case 0x2C:
                reload = value & 0xFFFF;
                if((control & 0x80) == 0) activeReload = reload;
                break;
            case 0x38:
                compare = value & 0xFFFF;
                if((compareMode & 0x800) == 0) activeCompare = compare;
                break;
            default:
                if(value != 0) throw new RecoverableException(String.Format("Analytical TIM12 does not support register 0x{0:X} value 0x{1:X}; use STM32_Timer for other functions", offset, value));
                break;
            }
            PublishLevel();
            observer.Enabled = Counting;
        }

        public void OnGPIO(int number, bool value)
        {
            if(number == 0xFF && value) Reset();
            else if(value) throw new RecoverableException("Analytical TIM12 has no external clock/trigger input");
        }

        public void Reset()
        {
            if(observer != null) observer.Reset();
            control = status = compareMode = compareEnable = counter = prescaler = compare = 0;
            activePrescaler = activeCompare = 0;
            reload = activeReload = initialLimit;
            prescalerPhase = 0;
            lastTime = machine.ClockSource.CurrentValue.Ticks;
            sourceFraction = (ulong)((decimal)lastTime * frequency % TimeInterval.TicksPerSecond);
            outputLevel = false;
            RisingEdges = SynchronizationCallbacks = 0;
            IRQ.Unset();
            display.ObserveGrayscaleClock(0, false);
        }

        public void Synchronize()
        {
            var now = machine.ClockSource.CurrentValue.Ticks;
            if(now < lastTime) throw new RecoverableException("GSCLK virtual clock moved backward without reset");
            var elapsed = now - lastTime;
            lastTime = now;
            if(elapsed == 0) return;
            // Decimal integer arithmetic prevents elapsed*frequency overflow.
            // Keep sub-source-tick remainder, so short/repeated reads cannot
            // discard phase or round an 8.727MHz clock to an integer rate.
            var scaled = (decimal)elapsed * frequency + sourceFraction;
            sourceFraction = (ulong)(scaled % TimeInterval.TicksPerSecond);
            var cycles = (ulong)((scaled - sourceFraction) / TimeInterval.TicksPerSecond);
            if(!Counting) return;
            SynchronizationCallbacks++;
            AdvanceSourceCycles(cycles);
            if(!Counting) observer.Enabled = false;
        }

        [DefaultInterrupt]
        public GPIO IRQ { get; } = new GPIO(); // DIER requests are rejected; this remains low.

        public long Size => 0x400;
        public ulong RisingEdges { get; private set; }
        public ulong SynchronizationCallbacks { get; private set; }

        private bool Counting => (control & 1) != 0 && activeReload != 0;
        private bool UpdatesDisabled => (control & 2) != 0;
        private bool OutputEnabled => (compareEnable & 0x10) != 0;
        private bool HighBelowCompare => (((compareMode >> 12) & 7) == 6) ^ ((compareEnable & 0x20) != 0);
        private bool LevelAt(uint position) => OutputEnabled &&
            (HighBelowCompare ? position < activeCompare : position >= activeCompare);
        private ulong TicksToWrap => (counter > activeReload ? 0x10000UL : (ulong)activeReload + 1) - counter;
        private bool BuffersPending => !UpdatesDisabled && (activePrescaler != prescaler ||
            activeReload != reload || activeCompare != compare);

        private void SynchronizeCpuTime()
        {
            if(sysbus.TryGetCurrentCPU(out var cpu)) cpu.SyncTime();
        }

        private void AdvanceSourceCycles(ulong cycles)
        {
            // At most one split is needed: the next update transfers all
            // pending buffers; the remaining interval has constant settings.
            var divisor = (ulong)activePrescaler + 1;
            var untilUpdate = TicksToWrap * divisor - prescalerPhase;
            if(BuffersPending && cycles >= untilUpdate)
            {
                cycles -= untilUpdate;
                // Transfer before evaluating the output at CNT=0: a new
                // zero-duty CCR must not create an old-duty rollover glitch.
                AdvanceCounter(TicksToWrap - 1);
                counter = 0;
                prescalerPhase = 0;
                TransferBuffers();
                status |= 1;
                if(activeCompare == 0) status |= 4;
                PublishLevel();
                divisor = (ulong)activePrescaler + 1;
            }
            if(!Counting) return;
            var ticks = cycles / divisor;
            var remainder = cycles % divisor + prescalerPhase;
            ticks += remainder / divisor;
            prescalerPhase = remainder % divisor;
            AdvanceCounter(ticks);
        }

        private void AdvanceCounter(ulong ticks)
        {
            if(ticks == 0) return;
            var untilWrap = TicksToWrap;
            ulong edges = 0;
            if(ticks < untilWrap)
            {
                var end = counter + (uint)ticks;
                edges += IncreasingSegment(counter, end);
                counter = end;
            }
            else
            {
                // Handle a CNT above a newly reduced ARR by counting through
                // the 16-bit natural overflow before ordinary ARR periods.
                var last = counter + (uint)untilWrap - 1;
                edges += IncreasingSegment(counter, last);
                if(OutputEnabled && !LevelAt(last) && LevelAt(0)) edges++;
                ticks -= untilWrap;
                var period = (ulong)activeReload + 1;
                var fullPeriods = ticks / period;
                if(OutputEnabled && activeCompare > 0 && activeCompare < period) edges += fullPeriods;
                if(fullPeriods != 0 && activeCompare <= activeReload) status |= 4;
                if(activeCompare == 0) status |= 4;
                if(!UpdatesDisabled) status |= 1;
                counter = (uint)(ticks % period);
                edges += IncreasingSegment(0, counter);
            }
            RisingEdges += edges;
            outputLevel = LevelAt(counter);
            display.ObserveGrayscaleClock(edges, outputLevel);
        }

        private ulong IncreasingSegment(uint start, uint end)
        {
            if(activeCompare > start && activeCompare <= end)
            {
                status |= 4;
                if(OutputEnabled && !HighBelowCompare) return 1;
            }
            return 0;
        }

        private void PublishLevel()
        {
            var level = LevelAt(counter);
            var edge = !outputLevel && level ? 1UL : 0;
            RisingEdges += edge;
            outputLevel = level;
            display.ObserveGrayscaleClock(edge, level);
        }

        private void TransferBuffers()
        {
            activePrescaler = prescaler;
            activeReload = reload;
            activeCompare = compare;
        }

        private static void RejectBits(uint value, uint allowed, string feature)
        {
            if((value & ~allowed) != 0) throw new RecoverableException("Analytical TIM12 does not support " + feature + "; use STM32_Timer for this configuration");
        }

        private static void ValidatePwm(uint mode, uint enable)
        {
            var pwm = (mode >> 12) & 7;
            if((enable & 0x10) != 0 && pwm != 6 && pwm != 7)
            {
                throw new RecoverableException("Analytical TIM12 requires PWM1 or PWM2 on enabled channel 2");
            }
        }

        private readonly IMachine machine;
        private readonly IBusController sysbus;
        private readonly ulong frequency;
        private readonly TLC5955 display;
        private readonly uint initialLimit;
        private readonly LimitTimer observer;
        private uint control, status, compareMode, compareEnable, counter;
        private uint prescaler, reload, compare, activePrescaler, activeReload, activeCompare;
        private ulong lastTime, sourceFraction, prescalerPhase;
        private bool outputLevel;
    }
}
