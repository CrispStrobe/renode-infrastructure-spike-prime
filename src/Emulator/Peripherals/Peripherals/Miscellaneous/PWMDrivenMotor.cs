// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.Timers;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // Deterministic ideal H-bridge/tacho fixture, not an inertia/load model.
    // Every emitted quadrature edge reaches a physical GPIO receiver.
    public class PWMDrivenMotor : IPeripheral, IGPIOReceiver
    {
        public PWMDrivenMotor(IMachine machine, IPWMDutyCycleSource pwm, int channel = 0,
            uint maximumEdgesPerSecond = 1440, uint updateFrequency = 1000)
        {
            if(pwm == null || channel < 0 || channel > 1 || maximumEdgesPerSecond == 0 || maximumEdgesPerSecond > 4096
                || updateFrequency == 0 || updateFrequency > 4096)
            {
                throw new ConstructionException("Motor needs a PWM source/channel 0..1 and rates in 1..4096 Hz");
            }
            this.pwm = pwm; this.channel = channel;
            MaximumEdgesPerSecond = maximumEdgesPerSecond; this.updateFrequency = updateFrequency;
            TachoA = new GPIO(); TachoB = new GPIO();
            timer = new LimitTimer(machine.ClockSource, updateFrequency, this, "motor tacho", limit: 1, eventEnabled: true);
            timer.LimitReached += Update;
            Reset();
        }

        public void OnGPIO(int number, bool value)
        {
            if(number == 0) inputA = value;
            else if(number == 1) inputB = value;
            else throw new ArgumentOutOfRangeException(nameof(number));
            if(Direction == 0) accumulatedEdges = 0;
        }

        public void Reset()
        {
            timer.Reset(); timer.Enabled = true;
            inputA = inputB = false; phase = 0; accumulatedEdges = 0;
            TachometerCount = 0; EmittedEdges = 0;
            TachoA.Unset(); TachoB.Unset();
        }

        public int Direction => inputA == inputB ? 0 : inputA ? 1 : -1;
        public string State => inputA && inputB ? "Brake" : !inputA && !inputB ? "Coast" : inputA ? "Forward" : "Reverse";
        public double DutyCycle => pwm.Operational ? pwm.GetDutyCycle(channel) : 0;
        public long TachometerCount { get; private set; }
        public ulong EmittedEdges { get; private set; }
        public uint MaximumEdgesPerSecond { get; }
        public GPIO TachoA { get; }
        public GPIO TachoB { get; }

        private void Update()
        {
            var direction = Direction;
            var duty = DutyCycle;
            if(direction == 0 || duty <= 0) { accumulatedEdges = 0; return; }
            accumulatedEdges += Math.Min(1, duty) * MaximumEdgesPerSecond / updateFrequency;
            var edges = (int)Math.Floor(accumulatedEdges + 1e-10);
            accumulatedEdges -= edges;
            for(var edge = 0; edge < edges; edge++)
            {
                phase = (phase + (direction > 0 ? 1 : 3)) & 3;
                TachoA.Set(phase == 1 || phase == 2);
                TachoB.Set(phase == 2 || phase == 3);
                TachometerCount += direction; EmittedEdges++;
            }
        }

        private readonly IPWMDutyCycleSource pwm;
        private readonly int channel;
        private readonly uint updateFrequency;
        private readonly LimitTimer timer;
        private bool inputA, inputB;
        private int phase;
        private double accumulatedEdges;
    }
}
