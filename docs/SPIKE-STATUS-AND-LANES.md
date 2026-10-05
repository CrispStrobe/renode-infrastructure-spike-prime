# SPIKE hardware models: state and next lanes

Recorded 2026-10-05. This repository owns hardware models, not the editor's robot
program runner. Read the current source/tests before claiming work. The
[Runtime handover](https://github.com/CrispStrobe/renode-spike-prime/blob/main/docs/SPIKE-STATUS-AND-LANES.md)
owns board wiring, staging and guest checks; the
[firmware handover](https://github.com/CrispStrobe/brickwright-spike-prime-fw/blob/main/docs/project/next-steps.md)
owns drivers and controller semantics. Proposed tasks below are not active claims.

## Current evidence boundary

The fork includes LPF2 UART/electrical adapters and device fixtures, SPIKE display,
IMU, flash and other board models plus generic STM32 corrections. Tests cover
specified packet, register and deterministic scheduling contracts; that does not
establish complete device discovery, physical timing or an entire reference boot.

[Main merge](https://github.com/CrispStrobe/renode-infrastructure-spike-prime/commit/4d4fefee12af854c124a85960d445e012e870bea)
records the reviewed ADC/DMA adoption. Firmware qualification consumed
`fe4ad383c7392527433783fcec455daa7ddc2bb7`; the separate upstream MicroPython
support validates its own source closure. Do not assume every consumer uses the
same revision or all files in this repository.

DMA receive readiness retained during synchronous callbacks, descriptor restart
and ADC request suppression/rearm have bounded regression evidence. This is a
single readiness-bit model, not a counted FIFO or asynchronous hardware handshake
qualification. Software-interruption status, FIFO draining, full channel mux,
double buffering/error behavior, unequal-width transfers and SYSCFG memory
remapping remain separate contracts. Preserve attribution and scoped modification
credits; source review is not blanket licence clearance or clean-room provenance.

## Rules for every lane

Cite public primary register/device specifications, define inputs/units, reset and
failure semantics, then write a regression failing the old behavior. Keep fixture
stimuli explicit. Test at the model boundary before guest integration; successful
synthetic tests alone do not prove a real firmware driver uses the new behavior.
Land in this fork, then have Runtime consume an exact reviewed revision and run
its complete affected model/build/board tests; firmware/Lite adopt pins afterward.
Preserve all rights-holder notices. Do not inspect private application
implementation, patch reference images or fabricate persisted contents to pass.

## M01 — Specify interrupted DMA and FIFO behavior

**Start:** `src/Emulator/Peripherals/Peripherals/DMA/STM32DMA.cs` and
`src/Emulator/Peripherals/Test/PeripheralsTests/STM32DMAAbortRearmTests.cs`,
`STM32DMAReceivePulseTests.cs`, `STM32DMATests.cs` in the same test directory.

Separate manual disable, automatic completion, reset, residual count and pending
request behavior. Implement one public contract at a time; do not treat a
successful-buffer ADC acknowledgement as interrupted-transfer TCIF semantics.
Add partial transfer/rearm, held/pulsed requests and old/new descriptor cases.
FIFO, double-buffer, channel mux and unequal-width transfer support require their
own explicit specifications, not an inferred generalization.
**Done:** old model fails, new model passes focused and complete peripheral tests,
with Runtime native/staged board regressions and unchanged existing acknowledgements.
Doable now from public specifications; physical timing remains unqualified.

## M02 — Complete bounded ADC conversion and request contracts

**Start:** `src/Emulator/Peripherals/Peripherals/Analog/STM32_ADC.cs` and
`src/Emulator/Peripherals/Test/PeripheralsTests/STM32ADCDMATests.cs`,
`STM32ADCDMAInitialRequestsTests.cs`, `STM32ADCTriggerTests.cs`.

Specify overrun, data-register read, trigger/sequence selection and DMA request
rules from public STM32 documentation. Preserve EOCS, DDS, request-stop/rearm and
stream-reuse regressions. Supply declared raw ADC stimuli for actual guest battery,
temperature and button-driver conversions; unknown readings must not become
synthetic healthy defaults. **Done:** model edge/reset tests plus guest conversion
observations pass with units/bounds documented. Doable now; electrical calibration
and power safety are hardware gates.

## M03 — Add independently specified SYSCFG/clock/timer slices

**Start:** `src/Emulator/Peripherals/Peripherals/Miscellaneous/STM32_SYSCFG.cs`,
`src/Emulator/Peripherals/Test/PeripheralsTests/STM32SYSCFGTests.cs` and Runtime
platforms selecting those models.

Choose one demonstrated missing register contract, initially memory remapping if
required by a recorded guest milestone. Define bank/alias/reset behavior and
conflicting access before implementation. Do not disturb existing routing/reset
support. Clock/timer/USB additions each need their own source-backed contract and
fixtures. **Done:** reset/valid/invalid access tests, affected managed suite and
Runtime board/guest evidence establish only the selected slice. Public model work
is doable now; complete reference boot is a separate input-gated result.

## M04 — Generalize evidenced LPF2 attachment and device coverage

**Start:** `src/Emulator/Peripherals/Peripherals/UART/LegoLpf2Port.cs`,
`LegoLpf2ElectricalPort.cs` in that directory and their corresponding tests under
`src/Emulator/Peripherals/Test/PeripheralsTests/`; Runtime
[device catalog](https://github.com/CrispStrobe/renode-spike-prime/blob/main/docs/platforms/lpf2-device-catalog.md).

Inventory implemented identities separately from catalogued candidates. Admit new
models only with complete public discovery bytes, modes, units and output semantics.
Define detach/replacement generation, wrong mode, damaged frames and motor/sensor
exclusion. Coordinate Runtime R02 and firmware L01/L02; model attachment must not
become an editor-only fabricated success. **Done:** exact wire/error fixtures,
reset/hotplug tests and representative actual guest observations pass for each
advertised identity. Existing identity tests run now; missing source facts gate
new device support.

## M05 — Make board I/O fidelity observable without calibration claims

**Start:** SPIKE display/IMU/flash model sources selected by Runtime's Prime
platform and their peripheral tests; firmware L04/L05/L07.

Choose a bounded slice: display latch/brightness phase, button input, scheduled
sound/DAC, flash operation/failure boundary or IMU sampling/reset/overflow. Trace
request to state and clocked observable output. Keep raw IMU samples distinct from
orientation and modern Bluetooth wire conversion. Use independent paired reference
observations for any new conversion, never guessed synthetic matching.
**Done:** deterministic reset/boundary/timing/error tests plus real guest output
prove the selected feature. Public register work is doable now; modern IMU mapping
requires additional independent observations, physical audio/LED accuracy hardware.

## Qualification and handoff

The managed fixtures are in
`src/Emulator/Peripherals/Test/PeripheralsTests/`. Use their actual solution/project
and the consumer Runtime's `.github/workflows/model-tests.yml` for supported build
commands; do not run a stubbed miniature model and describe it as the full suite.
Include a meaningful negative/mutation check and relevant guest regressions. Record
model commit, Runtime consumed pin, fixture limits and remaining gaps. Test source
and guest time separately. A lack of CPU fault is not proof of successful boot.

Public changes and evidence contain public source paths/URLs only. External images,
raw logs/transcripts and operational access are not published here. This work does
not authorize upstream communication or physical hardware flashing.
