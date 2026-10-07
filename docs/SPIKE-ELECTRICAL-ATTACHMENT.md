<!-- SPDX-License-Identifier: BSD-3-Clause -->
<!-- Copyright (c) 2026 Brickwright contributors -->

# Synthetic electrical attachment contract

This candidate closes retained GPIO input levels after logical LPF2 detach and
adds bridge-demand observations independent of a motor object. It does not
automatically clear firmware PWM or qualify physical unplug safety.

## Inputs and sampled levels

The [owned firmware board contract](https://github.com/CrispStrobe/brickwright-spike-prime-fw/blob/main/docs/en/drivers/port-detection.md)
connects TX/ID1 to connector pin 5 and RX/ID2 to pin 6. The model updates only
GPIOs in input mode on its existing 1 kHz feeder; host output/alternate/analog
modes and register contents are left under guest control.

| State | ID1 input | ID2 input | RX input |
|---|---|---|---|
| Attached UART device | Host TX GPIO level, otherwise high | Low | Low |
| Detached | Host TX GPIO level, otherwise high | Host RX GPIO output level, otherwise high | Host ID2 GPIO output level, otherwise high |

The undriven high level is an explicit deterministic virtual UART idle policy,
not an inferred physical pull resistor or measured floating voltage. RX/ID2
coupling follows the documented shared signal. There is no TX-to-ID2 coupling.
Changes become visible on the next tick, at most one simulated millisecond after
the input change. Existing logical attachment flags and topology generation
remain distinct from the real guest DCM type and event counter.

A detached port emits no discovery/data bytes. When TX is UART alternate mode,
its protocol time still advances one millisecond per tick. A new same-type motor
has fresh mechanical state; it receives whatever bridge demand the guest actually
leaves in its registers. A removed motor object's cached power is not evidence
of current bridge demand.

## Bridge observations

`LegoLpf2ElectricalPort.BridgeDrive` reads signed demand in 1/10,000 full-duty
units, including when `Device` is null. It retains the existing CEN, timer-enable,
advanced-timer MOE, GPIO mode/level, AF, channel-enable and active-low PWM gates.
`BridgeBraking` reads the existing two-high-GPIO braking condition. A port without
a bridge reports zero drive and false braking.

These properties read existing registers and store no second hub/motor state.
They do not coast, brake or rewrite PWM on detach. Sample a paused machine for
a coherent multi-register observation; they are not atomic hardware snapshots.
Guest detach qualification must observe the firmware releasing demand and a
fresh replacement request succeeding without an old job stopping it.

## Qualification boundary

The old compiled model fails the detached idle-input comparison. Source-compiled
candidate controls pass both the existing 35 electrical/mechanical checks and
the new attachment checks. Two freshly compiled mutations are detected:
retaining old detached inputs and hiding powered demand when no device exists.
The new fixture is registered in both NUnit project profiles.

Reproduce the bounded local control run with a compatible existing Runtime:

```sh
python3 tools/check_electrical_attachment_qualification.py \
  --renode RENODE_EXECUTABLE --output NEW_PRIVATE_OUTPUT_DIRECTORY
python3 tools/check_pybricks_references.py
```

The qualification stager retains source notices, records input/generated hashes
and uses a separate namespace and topology entry point. It changes no model
behavior and avoids shadowing compiled installed types. Its runner keeps stdin
open and bounds only its own process group. It is not a complete Infrastructure
build, complete NUnit suite, Runtime consumer adoption or release qualification.
Raw paths, debug layouts and execution logs remain private.

An actual own-firmware ARM guest with these source-compiled candidate models
passed one Classic detach/reconnect sequence: the running job returned
`-19` (`ENODEV`), the guest confirmed NONE and cleared CONNECTED after 767,803
simulated microseconds, and bridge demand reached zero. A request while absent
also returned `ENODEV`. After same-type reattachment, the guest counter advanced
again and a fresh -90-degree request completed at -94.7464 degrees, inside the
fixture's 20-degree tolerance. The fixture observes the actual guest DCM state
read-only using an exact own-kernel debug layout; it does not write guest state
or clear PWM. This is one bounded sequence, not arbitrary hotplug coverage.
The tested retained firmware source is
[`4ca642c`](https://github.com/CrispStrobe/brickwright-spike-prime-fw/commit/4ca642c).

Before merging/adopting, run the canonical Runtime build and complete affected
peripheral/board suites with the exact Infrastructure revision, followed by
canonical-consumer own-firmware detach/reconnect, Classic motor and native/Python
regressions. Firmware's mandatory TI fingerprint gate remains separate; an
unavailable official endpoint does not waive it. The read-time encoder edge
guard, atomic admission and arbitrary hotplug/transport ownership interleavings
are distinct firmware contracts.

This work modifies the existing BSD-3-Clause model and adds BSD-3-Clause tests
and tools. Existing underlying Renode/component notices and discovery-fixture
attribution remain applicable. No whole-firmware clean-room or blanket licence
clearance claim is made.
