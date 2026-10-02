// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    [NonParallelizable]
    public class STM32TLCClockTests
    {
        [SetUp]
        public void SetUp()
        {
            EmulationManager.Instance.Clear();
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
            display = new TLC5955();
            timer = new STM32TLCClock(machine, 96000000, display);
        }

        [TearDown]
        public void TearDown() { EmulationManager.Instance.Clear(); }

        [Test]
        public void ShouldCountPhysicalFrequencyAndCounterWithoutMillionsOfCallbacks()
        {
            Configure(10, 5);
            var initial = display.GrayscaleClockEdges;
            Advance(1000000000);
            Equal(initial + 96000000UL / 11, display.GrayscaleClockEdges, "96MHz / (ARR+1) edges");
            Equal(96000000UL % 11, timer.ReadDoubleWord(0x24), "exact counter phase");
            Equal(display.GrayscaleClockEdges & 0xFFFF, (ulong)display.GrayscalePhase, "TLC phase");
            Check(timer.SynchronizationCallbacks <= 1001, "observer work bounded at 1kHz");
        }

        [Test]
        public void ShouldPreserveFractionAcrossShortReadsAndPartitionedTime()
        {
            Configure(10, 5);
            var initial = display.GrayscaleClockEdges;
            for(var i = 0; i < 1000; i++)
            {
                Advance(7);
                timer.ReadDoubleWord(0x24);
            }
            Equal(672UL % 11, timer.ReadDoubleWord(0x24), "7ns reads retain source remainder");
            Equal(initial + 672UL / 11, display.GrayscaleClockEdges, "partitioned edges");
            var edges = display.GrayscaleClockEdges;
            timer.Synchronize();timer.Synchronize();
            Equal(edges, display.GrayscaleClockEdges, "same-time reads do not add edges");
        }

        [Test]
        public void ShouldKeepSourceClockPhaseWhileCounterIsPaused()
        {
            Configure(10, 5);
            Advance(6);Equal(0, timer.ReadDoubleWord(0x24), "less than one source tick");
            timer.WriteDoubleWord(0, 0);Advance(5);timer.WriteDoubleWord(0, 1);
            Advance(6);Equal(0, timer.ReadDoubleWord(0x24), "paused interval does not freeze oscillator phase");
            Advance(4);Equal(1, timer.ReadDoubleWord(0x24), "next actual oscillator edge increments counter");
        }

        [Test]
        public void ShouldPauseCounterAndHonorZeroFullMissingAndDisabledDuty()
        {
            Configure(10, 0);
            Advance(1000000);
            Equal(0, display.GrayscaleClockEdges, "zero duty does not fabricate pulses");
            timer.WriteDoubleWord(0x38, 11);
            var full = display.GrayscaleClockEdges;
            Advance(1000000);
            Equal(full, display.GrayscaleClockEdges, "full duty constant high");
            timer.WriteDoubleWord(0x38, 5);
            timer.WriteDoubleWord(0, 0);
            var counter = timer.ReadDoubleWord(0x24);
            var paused = display.GrayscaleClockEdges;
            Advance(1000000);
            Equal(counter, timer.ReadDoubleWord(0x24), "CEN pause freezes phase");
            Equal(paused, display.GrayscaleClockEdges, "CEN pause freezes edges");
            timer.WriteDoubleWord(0, 1);
            timer.WriteDoubleWord(0x20, 0);
            var disabled = display.GrayscaleClockEdges;
            Advance(1000000);
            Equal(disabled, display.GrayscaleClockEdges, "CC2E gates output");
            timer.WriteDoubleWord(0x2C, 0);
            counter = timer.ReadDoubleWord(0x24);
            Advance(1000000);
            Equal(counter, timer.ReadDoubleWord(0x24), "ARR zero blocks counter");
            timer.Reset();timer.WriteDoubleWord(0x18, 6u << 12);
            timer.WriteDoubleWord(0x20, 0x10);timer.WriteDoubleWord(0x2C, 10);timer.WriteDoubleWord(0, 1);
            disabled = display.GrayscaleClockEdges;Advance(1000000);
            Equal(disabled, display.GrayscaleClockEdges, "missing CCR reset value is zero");
        }

        [Test]
        public void ShouldTransferPrescalerArrAndCcrAtUpdateBoundary()
        {
            Configure(9, 5);
            timer.WriteDoubleWord(0, 0x81); // ARPE+CEN
            Advance(50); // 4 source ticks + 0.8 source tick
            Equal(4, timer.ReadDoubleWord(0x24), "pre-update count");
            timer.WriteDoubleWord(0x18, 0x6800); // OC2PE
            timer.WriteDoubleWord(0x28, 1);timer.WriteDoubleWord(0x2C, 19);timer.WriteDoubleWord(0x38, 8);
            Advance(55); // total 105ns => 10 source cycles, update exactly once
            Equal(0, timer.ReadDoubleWord(0x24), "first old period finishes before transfer");
            Advance(100); // 9 new source cycles, divisor 2
            Equal(4, timer.ReadDoubleWord(0x24), "new PSC after overflow");
            timer.WriteDoubleWord(0x14, 1);
            Equal(0, timer.ReadDoubleWord(0x24), "UG resets CNT");
            Advance(1000);
            Equal(48UL % 20, timer.ReadDoubleWord(0x24), "UG resets prescaler phase");
        }

        [Test]
        public void ShouldHandlePolarityPwm2AndStatusWithoutEnablingInterrupts()
        {
            Configure(10, 5);
            Advance(60); // CNT 5, PWM1 low
            var edges = display.GrayscaleClockEdges;
            timer.WriteDoubleWord(0x20, 0x30); // inverted: goes high at CNT5
            Equal(edges + 1, display.GrayscaleClockEdges, "polarity write has physical edge");
            timer.WriteDoubleWord(0x18, 7u << 12); // inverted PWM2 goes low
            edges = display.GrayscaleClockEdges;
            Advance(1000);
            Equal(edges + 9, display.GrayscaleClockEdges, "PWM2 inverted period count");
            Check((timer.ReadDoubleWord(0x10) & 5) == 5, "UIF/CC2IF without DIER");
            timer.WriteDoubleWord(0x10, 0);
            Equal(0, timer.ReadDoubleWord(0x10), "rc_w0 flags");
        }

        [Test]
        public void ShouldRejectUnsupportedModesBeforeChangingTheirRegisters()
        {
            Configure(10, 5);
            foreach(var pair in new[] {new uint[] {0x0C, 1}, new uint[] {0x0C, 0x100},
                new uint[] {0, 0x11}, new uint[] {0, 0x21}, new uint[] {0, 9},
                new uint[] {0x08, 1}, new uint[] {0x04, 0x20}, new uint[] {0x20, 0x11},
                new uint[] {0x18, 3u << 12}, new uint[] {0x44, 0x8000}})
            {
                var before = timer.ReadDoubleWord(pair[0]);
                var rejected = false;
                try { timer.WriteDoubleWord(pair[0], pair[1]); }
                catch(RecoverableException) { rejected = true; }
                Check(rejected, "unsupported register configuration rejected");
                Equal(before, timer.ReadDoubleWord(pair[0]), "rejected write leaves register unchanged");
            }
        }

        [Test]
        public void ShouldRetainTlcLatchAndResetSemanticsWithAggregateClock()
        {
            Configure(10, 5);Advance(100000);
            display.Transmit(0xAA);display.OnGPIO(0, true);display.OnGPIO(0, false);
            Equal(1, display.LatchedFrames, "SPI LAT still latches");
            var phase = timer.ReadDoubleWord(0x24);
            display.Reset();Equal(0, display.GrayscaleClockEdges, "TLC reset drains prior time first");
            Advance(100000);Equal((phase + 9600UL) / 11, display.GrayscaleClockEdges, "post-reset edges only");
            timer.Reset();var count = display.GrayscaleClockEdges;Advance(100000);
            Equal(count, display.GrayscaleClockEdges, "timer reset disables observer");
            var rejected = false;
            try { display.OnGPIO(1, true); } catch(InvalidOperationException) { rejected = true; }
            Check(rejected, "mixed GPIO and aggregate clock rejected");
        }

        [Test]
        public void ShouldAvoidShadowTransferGlitchesAndHonorUpdateDisable()
        {
            Configure(10, 5);
            timer.WriteDoubleWord(0x18, 0x6800);
            Advance(60); // CNT5 low
            var edges = display.GrayscaleClockEdges;
            timer.WriteDoubleWord(0x38, 0); // buffered zero duty
            Advance(60); // cross overflow
            Equal(edges, display.GrayscaleClockEdges, "no obsolete-CCR rising edge at buffer transfer");
            timer.WriteDoubleWord(0, 3); // CEN+UDIS
            timer.WriteDoubleWord(0x28, 1);timer.WriteDoubleWord(0x38, 5);
            timer.WriteDoubleWord(0x10, 0);
            Advance(1000);
            Equal(edges, display.GrayscaleClockEdges, "UDIS retains zero-duty shadow");
            Check((timer.ReadDoubleWord(0x10) & 1) == 0, "UDIS suppresses UIF");
            timer.WriteDoubleWord(0, 5); // CEN+URS, updates enabled
            timer.WriteDoubleWord(0x14, 1);
            Check((timer.ReadDoubleWord(0x10) & 1) == 0, "URS suppresses UG UIF");
            Advance(1000);
            Check(display.GrayscaleClockEdges > edges, "UG transfers new duty and PSC");
            timer.WriteDoubleWord(0, 0);
            timer.WriteDoubleWord(0x24, 100);
            timer.WriteDoubleWord(0x2C, 9);
            timer.WriteDoubleWord(0x28, 0);timer.WriteDoubleWord(0x14, 1);
            timer.WriteDoubleWord(0x24, 100);timer.WriteDoubleWord(0, 1);
            Advance(1000);
            Equal(196, timer.ReadDoubleWord(0x24), "CNT above ARR waits for natural 16-bit overflow");
        }

        private void Configure(uint arr, uint ccr)
        {
            timer.WriteDoubleWord(0x18, 6u << 12);
            timer.WriteDoubleWord(0x2C, arr);timer.WriteDoubleWord(0x38, ccr);
            timer.WriteDoubleWord(0x20, 0x10);timer.WriteDoubleWord(0x14, 1);timer.WriteDoubleWord(0, 1);
        }
        private void Advance(ulong nanoseconds)
        {
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromNanoseconds(nanoseconds), true);
        }
        private static void Check(bool result, string message)
        {
            if(!result) throw new Exception(message);
        }
        private static void Equal(ulong expected, ulong actual, string message)
        {
            if(expected != actual) throw new Exception(String.Format("{0}: expected {1}, actual {2}", message, expected, actual));
        }
        private Machine machine;
        private TLC5955 display;
        private STM32TLCClock timer;
    }
}
