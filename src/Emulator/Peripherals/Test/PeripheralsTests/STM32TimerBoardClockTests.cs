// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Sound;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    [NonParallelizable]
    public class STM32TimerBoardClockTests
    {
        [SetUp]
        public void SetUp()
        {
            EmulationManager.Instance.Clear();
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
            timer = new STM32_Timer(machine, 1000, 0xFFFF);
        }

        [TearDown]
        public void TearDown()
        {
            EmulationManager.Instance.Clear();
        }

        [Test]
        public void ShouldEmitBoundedTrgoAndDmaPulsesFromEmulatedClock()
        {
            var sink = new PCMAudioSink();
            var dma = new RisingEdgeCounter();
            sink.OnGPIO(0, true);
            sink.WriteWord(0, 0x123);
            sink.WriteWord(0, 0x456);
            timer.TriggerOutput.Connect(sink, 1);
            timer.UpdateDMARequest.Connect(dma, 0);

            timer.WriteDoubleWord(0x04, 2 << 4); // CR2 MMS=update
            timer.WriteDoubleWord(0x0C, 1 << 8); // DIER UDE
            timer.WriteDoubleWord(0x2C, 4);      // ARR
            timer.WriteDoubleWord(0x00, 1);      // CR1 CEN
            AdvanceMicroseconds(20000);

            Assert.AreEqual(2, sink.EmittedSamples);
            Assert.That(dma.Edges, Is.GreaterThanOrEqualTo(2).And.LessThanOrEqualTo(6));
        }

        [Test]
        public void ShouldClockTlcFromChannelTwoPwm()
        {
            var display = new TLC5955();
            timer.Connections[1].Connect(display, TLC5955.GrayscaleClockGPIO);
            timer.WriteDoubleWord(0x18, 6 << 12); // CCMR1 OC2M=PWM1
            timer.WriteDoubleWord(0x20, 1 << 4);  // CCER CC2E
            timer.WriteDoubleWord(0x38, 2);       // CCR2
            timer.WriteDoubleWord(0x2C, 4);       // ARR
            timer.WriteDoubleWord(0x00, 1);       // CR1 CEN
            AdvanceMicroseconds(20000);

            Assert.That(display.GrayscaleClockEdges, Is.GreaterThanOrEqualTo(2).And.LessThanOrEqualTo(6));
        }

        private void AdvanceMicroseconds(ulong microseconds)
        {
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(microseconds), true);
        }

        private Machine machine;
        private STM32_Timer timer;

        private sealed class RisingEdgeCounter : IGPIOReceiver
        {
            public void Reset()
            {
                Edges = 0;
            }

            public void OnGPIO(int number, bool value)
            {
                if(value)
                {
                    Edges++;
                }
            }

            public int Edges { get; private set; }
        }
    }
}
