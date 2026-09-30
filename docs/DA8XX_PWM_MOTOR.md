# DA8xx PWM and motor fixture

This functional slice is original MIT code. Register facts come from the TI
[AM1808 technical reference manual](https://www.ti.com/lit/ug/spruh82c/spruh82c.pdf),
chapters 15 and 16. Hardware output routing is documented by the
[EV3 PWM reference](https://www.ev3dev.org/docs/kernel-hackers-notebook/ev3-pwm/)
and [LEGO hardware schematics](https://www.lego.com/cdn/cs/set/assets/blt86e79bb287a0d0b1/Appendix_LEGO_MINDSTORMS_EV3_programmable_brick_main_hardware_schematics.pdf).
No firmware, GPL implementation, or real motor calibration data is included.

## PWM sources

`Timers.TI_DA8xx_EHRPWM` provides physical `OutputA`/`OutputB`, event `IRQ`,
and `TripIRQ`. Its native 16-bit registers also support byte and doubleword
accesses. Implemented behavior includes up/down/up-down count, both clock
divisors, period/compare shadow loading, action qualifier collision priority,
software force, software one-shot/cycle trips, and event IRQ prescaling/W1C.
`DutyCycleA` and `DutyCycleB` are fractions of high time, from 0 to 1.

`Timers.TI_DA8xx_ECAP` supplies the APWM source for EV3 motor C/D through its
physical `Output`. CAP1/CAP2 are active period/compare; active writes also mirror
CAP3/CAP4. Shadow writes load on period match. The APWM cycle has CAP1+1 clock
ticks. Polarity inversion, frozen counter, period/compare flags, interrupt
masking, W1C and software interrupt forcing are implemented. `DutyCycle` is
the high-time fraction. Capture mode and external/phase synchronization are
explicitly unsupported.

Both implement `IPWMDutyCycleSource.GetDutyCycle(channel)`. eHRPWM accepts
channels 0/1; eCAP accepts channel 0. `CounterRunning`, `Operational`,
`CycleFrequencyHz` and `UnsupportedConfiguration` expose their status.
`Operational` indicates a supported configuration, including a frozen counter.
A frozen PWM retains its output level and reports a static 0/1 duty, so motor
behavior matches a bridge connected to that physical level.

Constructor `frequency` is the peripheral input clock, not CPU clock. Platform
integration must configure the correct source frequency and PSC/pinmux setup.
Default input is 100 MHz. Each source refuses a running cycle rate above
`maximumCycleFrequency` (default 100 kHz, constructor range 1..200000 Hz).
Refusal stops scheduled events, drives output low, reports an explicit
diagnostic and returns zero duty; it does not silently approximate a higher
frequency. eHRPWM additionally rejects enabled deadband, chopper, HRPWM,
external trip and phase synchronization. Continuous software force requires
AQSFRC immediate loading; shadow software force is unsupported.

## H-bridge and tacho fixture

`Miscellaneous.PWMDrivenMotor` takes a PWM source and channel. GPIO inputs
0 and 1 select the ideal bridge state:

| Input 0 | Input 1 | State | Direction |
| --- | --- | --- | --- |
| 0 | 0 | Coast | 0 |
| 1 | 0 | Forward | +1 |
| 0 | 1 | Reverse | -1 |
| 1 | 1 | Brake | 0 |

`TachoA`/`TachoB` emit actual quadrature GPIO transitions. Forward cycles
00 → 10 → 11 → 01 → 00; reverse traverses the same sequence backward.
`TachometerCount` is a signed count of quadrature edges; `EmittedEdges` counts
all transitions. These are modeled counts, not measured shaft degrees.
`State`, `Direction` and `DutyCycle` are observable through the monitor/debugger.

At each virtual-clock sample the fixture accumulates
`duty * maximumEdgesPerSecond / updateFrequency` and emits only whole edges.
The fractional remainder makes runs deterministic. Constructor bounds both
rates to 1..4096 Hz; defaults are 1440 edges/s and 1000 updates/s. A finite run
can therefore emit only a finite bounded number of edges. Coast/brake stop
immediately. There is no inertia, load, torque, slip or electrical simulation.

Motor A uses eHRPWM1B, bridge GPIO63/54, encoder GPIO91/4, and detection GPIO84
plus ADC1. Motor B uses eHRPWM1A; C/D require their own eCAP APWM instances.
The generic fixture supports each source but platform wiring and ADC motor
identification must be supplied explicitly. It does not synthesize a detected
motor or replace actual GPIO interrupt routing with a direct counter shortcut.

For a 100 MHz test source, set eHRPWM1 TBCTL=0x83 (freeze, divide by 2),
TBPRD=49999, CMPCTL=0x50, CMPB=25000, AQCTLB=0x102, then TBCTL=0x80.
This produces a 1 kHz, 50% waveform on B. With maximum 1440 tacho edges/s,
100 ms of ideal forward motion produces 72 signed edges, delivered to GPIO.
