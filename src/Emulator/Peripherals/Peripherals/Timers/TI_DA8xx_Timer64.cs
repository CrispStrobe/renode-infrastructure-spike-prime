//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Timers
{
    // TI AM1808 64-Bit Timer Plus, SPRUH82C chapter 30.  The model covers the
    // internal-clock general-purpose modes used by the EV3 boot path.  External
    // pin, capture, compare-event and watchdog operation remain explicit
    // boundaries; their registers still expose the documented reset/RW shape.
    public class TI_DA8xx_Timer64 : IDoubleWordPeripheral, IKnownSize
    {
        public TI_DA8xx_Timer64(IMachine machine, ulong frequency = 24000000)
        {
            IRQ12 = new GPIO();
            IRQ34 = new GPIO();

            timer12 = new LimitTimer(machine.ClockSource, frequency, this, nameof(timer12),
                direction: Direction.Ascending, eventEnabled: true, autoUpdate: true);
            timer34 = new LimitTimer(machine.ClockSource, frequency, this, nameof(timer34),
                direction: Direction.Ascending, eventEnabled: true, autoUpdate: true);
            timer12.LimitReached += () => OnLimitReached(false);
            timer34.LimitReached += () => OnLimitReached(true);
            compare = new uint[8];
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset >= (long)Registers.Compare0 && offset <= (long)Registers.Compare7
                && (offset & 3) == 0)
            {
                return compare[(offset - (long)Registers.Compare0) / 4];
            }

            switch((Registers)offset)
            {
            case Registers.Revision:
                return Revision;
            case Registers.EmulationManagement:
                return emulationManagement;
            case Registers.GPIOInterruptAndEnable:
                return gpioInterruptAndEnable;
            case Registers.GPIODataAndDirection:
                return gpioDataAndDirection;
            case Registers.Counter12:
                if(IsCombinedMode)
                {
                    counter34Shadow = (uint)(timer12.Value >> 32);
                }
                var counter12 = (uint)timer12.Value;
                if(IsUnchainedMode && (timerControl & ReadResetMode12) != 0)
                {
                    timer12.Value = 0;
                }
                return counter12;
            case Registers.Counter34:
                if(IsCombinedMode)
                {
                    return counter34Shadow;
                }
                var counter34 = (uint)timer34.Value;
                if(IsUnchainedMode && (timerControl & ReadResetMode34) != 0)
                {
                    timer34.Value = 0;
                }
                return counter34;
            case Registers.Period12:
                return period12;
            case Registers.Period34:
                return period34;
            case Registers.TimerControl:
                return timerControl;
            case Registers.TimerGlobalControl:
                return timerGlobalControl;
            case Registers.WatchdogControl:
                return watchdogControl;
            case Registers.Reload12:
                return reload12;
            case Registers.Reload34:
                return reload34;
            case Registers.Capture12:
                return capture12;
            case Registers.Capture34:
                return capture34;
            case Registers.InterruptControlAndStatus:
                return interruptControlAndStatus;
            default:
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset >= (long)Registers.Compare0 && offset <= (long)Registers.Compare7
                && (offset & 3) == 0)
            {
                compare[(offset - (long)Registers.Compare0) / 4] = value;
                return;
            }

            switch((Registers)offset)
            {
            case Registers.Revision:
                break;
            case Registers.EmulationManagement:
                emulationManagement = value & 0x3;
                break;
            case Registers.GPIOInterruptAndEnable:
                gpioInterruptAndEnable = value & GPIOInterruptAndEnableMask;
                ApplyConfiguration();
                break;
            case Registers.GPIODataAndDirection:
                gpioDataAndDirection = value & GPIODataAndDirectionMask;
                break;
            case Registers.Counter12:
                timer12.Value = IsCombinedMode
                    ? (timer12.Value & 0xFFFFFFFF00000000UL) | value
                    : value;
                break;
            case Registers.Counter34:
                if(IsCombinedMode)
                {
                    timer12.Value = (timer12.Value & 0xFFFFFFFFUL) | ((ulong)value << 32);
                }
                else
                {
                    timer34.Value = value;
                }
                break;
            case Registers.Period12:
                period12 = value;
                ApplyConfiguration();
                break;
            case Registers.Period34:
                period34 = value;
                ApplyConfiguration();
                break;
            case Registers.TimerControl:
                timerControl = value & TimerControlMask;
                if((timerControl & ExternalClock12) != 0)
                {
                    this.Log(LogLevel.Warning, "External Timer64P input clock is not modeled; the counter is stopped");
                }
                ApplyConfiguration();
                break;
            case Registers.TimerGlobalControl:
                var oldGlobalControl = timerGlobalControl;
                timerGlobalControl = value & TimerGlobalControlMask;
                if((timerGlobalControl & Timer12ResetRelease) == 0
                    && (oldGlobalControl & Timer12ResetRelease) != 0)
                {
                    timer12.Value = 0;
                }
                if((timerGlobalControl & Timer34ResetRelease) == 0
                    && (oldGlobalControl & Timer34ResetRelease) != 0)
                {
                    if(IsCombinedMode)
                    {
                        timer12.Value = 0;
                    }
                    timer34.Value = 0;
                }
                ApplyConfiguration();
                break;
            case Registers.WatchdogControl:
                // Preserve documented writable fields. Watchdog mode is not an
                // EV3 boot dependency and intentionally has no reset output.
                watchdogControl = value & 0xFFFFC000;
                break;
            case Registers.Reload12:
                reload12 = value;
                break;
            case Registers.Reload34:
                reload34 = value;
                break;
            case Registers.Capture12:
                capture12 = value;
                break;
            case Registers.Capture34:
                capture34 = value;
                break;
            case Registers.InterruptControlAndStatus:
                interruptControlAndStatus &= ~(value & InterruptStatusMask);
                interruptControlAndStatus = (interruptControlAndStatus & ~InterruptEnableMask)
                    | (value & InterruptEnableMask);
                UpdateInterrupts();
                break;
            default:
                this.LogUnhandledWrite(offset, value);
                break;
            }
        }

        public void Reset()
        {
            timer12.Reset();
            timer34.Reset();
            emulationManagement = 0;
            gpioInterruptAndEnable = 0;
            gpioDataAndDirection = 0;
            period12 = 0;
            period34 = 0;
            timerControl = 0;
            timerGlobalControl = 0;
            watchdogControl = 0;
            reload12 = 0;
            reload34 = 0;
            capture12 = 0;
            capture34 = 0;
            counter34Shadow = 0;
            interruptControlAndStatus = 0;
            Array.Clear(compare, 0, compare.Length);
            UpdateInterrupts();
        }

        public GPIO IRQ12 { get; }
        public GPIO IRQ34 { get; }
        public long Size => 0x80;

        private bool IsUnchainedMode => ((timerGlobalControl >> 2) & 0x3) == 1;
        private bool IsCombinedMode => !IsUnchainedMode;

        private void OnLimitReached(bool upper)
        {
            var enableMode = GetEnableMode(upper);
            if(upper)
            {
                interruptControlAndStatus |= PeriodInterruptStatus34;
                if(enableMode == OperationMode.ContinuousReload)
                {
                    period34 = reload34;
                }
            }
            else
            {
                interruptControlAndStatus |= PeriodInterruptStatus12;
                if(enableMode == OperationMode.ContinuousReload)
                {
                    period12 = reload12;
                    if(IsCombinedMode)
                    {
                        period34 = reload34;
                    }
                }
            }
            if(enableMode == OperationMode.Once)
            {
                timerControl &= ~(3u << (upper ? 22 : 6));
            }
            ApplyConfiguration();
            UpdateInterrupts();
        }

        private void ApplyConfiguration()
        {
            var mode = (TimerMode)((timerGlobalControl >> 2) & 0x3);
            var gpioMode = (gpioInterruptAndEnable & GPIOEnableMask) != 0;
            var externalClock = (timerControl & ExternalClock12) != 0;

            if(mode == TimerMode.Unchained32)
            {
                timer12.Limit = period12;
                timer34.Limit = period34;
                timer34.Divider = ((timerGlobalControl >> 8) & 0xF) + 1;
                ConfigureTimer(timer12, GetEnableMode(false),
                    (timerGlobalControl & Timer12ResetRelease) != 0 && !gpioMode && !externalClock && period12 != 0);
                ConfigureTimer(timer34, GetEnableMode(true),
                    (timerGlobalControl & Timer34ResetRelease) != 0 && period34 != 0);
                return;
            }

            timer34.Enabled = false;
            if(mode == TimerMode.Watchdog64)
            {
                timer12.Enabled = false;
                this.Log(LogLevel.Warning, "Timer64P watchdog mode is not modeled");
                return;
            }

            timer12.Limit = ((ulong)period34 << 32) | period12;
            ConfigureTimer(timer12, GetEnableMode(false),
                (timerGlobalControl & (Timer12ResetRelease | Timer34ResetRelease))
                    == (Timer12ResetRelease | Timer34ResetRelease)
                && !gpioMode && !externalClock && timer12.Limit != 0);
        }

        private static void ConfigureTimer(LimitTimer timer, OperationMode mode, bool canRun)
        {
            timer.Mode = mode == OperationMode.Once ? WorkMode.OneShot : WorkMode.Periodic;
            timer.Enabled = canRun && mode != OperationMode.Disabled;
        }

        private OperationMode GetEnableMode(bool upper)
        {
            return (OperationMode)((timerControl >> (upper ? 22 : 6)) & 0x3);
        }

        private void UpdateInterrupts()
        {
            IRQ12.Set((interruptControlAndStatus & (PeriodInterruptEnable12 | PeriodInterruptStatus12))
                == (PeriodInterruptEnable12 | PeriodInterruptStatus12));
            IRQ34.Set((interruptControlAndStatus & (PeriodInterruptEnable34 | PeriodInterruptStatus34))
                == (PeriodInterruptEnable34 | PeriodInterruptStatus34));
        }

        private readonly LimitTimer timer12;
        private readonly LimitTimer timer34;
        private readonly uint[] compare;
        private uint emulationManagement;
        private uint gpioInterruptAndEnable;
        private uint gpioDataAndDirection;
        private uint period12;
        private uint period34;
        private uint timerControl;
        private uint timerGlobalControl;
        private uint watchdogControl;
        private uint reload12;
        private uint reload34;
        private uint capture12;
        private uint capture34;
        private uint counter34Shadow;
        private uint interruptControlAndStatus;

        private const uint Revision = 0x4472020C;
        private const uint GPIOInterruptAndEnableMask = 0x00030033;
        private const uint GPIODataAndDirectionMask = 0x00030003;
        private const uint GPIOEnableMask = 0x00030000;
        private const uint TimerControlMask = 0x04C03FFE;
        private const uint TimerGlobalControlMask = 0x0000FF1F;
        private const uint InterruptEnableMask = 0x00050005;
        private const uint InterruptStatusMask = 0x000A000A;
        private const uint Timer12ResetRelease = 1u << 0;
        private const uint Timer34ResetRelease = 1u << 1;
        private const uint ExternalClock12 = 1u << 8;
        private const uint ReadResetMode12 = 1u << 10;
        private const uint ReadResetMode34 = 1u << 26;
        private const uint PeriodInterruptEnable12 = 1u << 0;
        private const uint PeriodInterruptStatus12 = 1u << 1;
        private const uint PeriodInterruptEnable34 = 1u << 16;
        private const uint PeriodInterruptStatus34 = 1u << 17;

        private enum TimerMode
        {
            GeneralPurpose64 = 0,
            Unchained32 = 1,
            Watchdog64 = 2,
            Chained32 = 3,
        }

        private enum OperationMode
        {
            Disabled = 0,
            Once = 1,
            Continuous = 2,
            ContinuousReload = 3,
        }

        private enum Registers : long
        {
            Revision = 0x00,
            EmulationManagement = 0x04,
            GPIOInterruptAndEnable = 0x08,
            GPIODataAndDirection = 0x0C,
            Counter12 = 0x10,
            Counter34 = 0x14,
            Period12 = 0x18,
            Period34 = 0x1C,
            TimerControl = 0x20,
            TimerGlobalControl = 0x24,
            WatchdogControl = 0x28,
            Reload12 = 0x34,
            Reload34 = 0x38,
            Capture12 = 0x3C,
            Capture34 = 0x40,
            InterruptControlAndStatus = 0x44,
            Compare0 = 0x60,
            Compare7 = 0x7C,
        }
    }
}
