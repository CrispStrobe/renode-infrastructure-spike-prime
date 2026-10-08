<!-- SPDX-License-Identifier: BSD-3-Clause -->
<!-- Copyright (c) 2026 Brickwright contributors -->

# Bounded external LPF2 DATA reports

This candidate adds device-side fixture controls to `LegoLpf2Port`, inherited by
`LegoLpf2ElectricalPort`. It is preparation for the firmware's
[active-session syscall qualification](https://github.com/CrispStrobe/brickwright-spike-prime-fw/blob/main/docs/project/lump-guest-probe.md).
No firmware dependency pin, guest queue, session identity, PWM register or physical
hub is changed. Compiled execution and consumer adoption are pending.

## Interface and timing

Call these fixture controls while emulation is paused; concurrent host mutation
is unsupported. Existing default behavior is unlimited reporting.

| Interface | Meaning |
|---|---|
| `SetDataReportBudget(uint reports)` | Replace the remaining DATA-frame allowance. Zero suppresses future DATA attempts. The call emits no bytes itself. |
| `ResumeDataReports()` | Return to unlimited DATA reports without resetting the protocol or device. |
| `DataReportsLimited` | True while a finite budget applies, including after exhaustion. |
| `DataReportsRemaining` | Remaining allowance while limited; zero in unlimited mode. |

Every DATA emission path uses the same budget: ACK, NACK, mode selection and
periodic reporting. A valid report reserves one allowance before delivering its
first byte, so synchronous subscriber reentry cannot exceed the limit. Invalid
payload validation does not consume an allowance. Discovery, control/error bytes,
mode/output handling, device physics and simulated protocol time continue. A
budget is a test-device emission limit, not a motor owner or UART session token.

Negotiation, attachment and detachment preserve the chosen budget and remainder.
An explicit whole-model `Reset()` restores unlimited reports; a newly constructed
model also starts unlimited. Replacing or resuming a budget does not replay
suppressed frames, clear already buffered UART bytes or reset the report cadence.
Queue capacity, existing drop diagnostics and large-time-jump coalescing remain
unchanged. A permitted report can still encounter existing transport drops;
its allowance counts the emission attempt, not guaranteed guest receipt.

Suppressing NACK responses deliberately withholds device DATA and can eventually
trigger the guest's watchdog. This control does not promise keepalive continuity
or physical-device behavior. Bound guest experiments within observed timing and
record any session loss explicitly.

## Qualification plan and reproducible controls

`LegoLpf2DataBudgetTests` compares actual UART byte output for each producer and
mixed producers, discovery preservation, continued clock/motor movement,
synchronous subscriber reentry, attachment/reset policy, maximum allowance,
pre-existing queued output, suppressed output/mode/error handling, invalid payloads,
exhausted reconnection and cadence-preserving resume. Eighteen cases are declared. The retained LPF2, electrical and complete peripheral
suites must also pass against the exact source-built Runtime/Infrastructure pair.
The new fixture is included by the SDK project and listed in the retained NUnit
project; that listing alone is not a claim of a complete Mono build.

After building the owning Runtime with this candidate Infrastructure revision:

```sh
dotnet test src/Infrastructure/src/Emulator/Peripherals/Test/PeripheralsTests/PeripheralsTests_NET.csproj \
  --configuration Release --no-build --no-restore \
  -p:GUI_DISABLED=true -p:CurrentPlatform=Linux -p:NET=true \
  --filter 'FullyQualifiedName~LegoLpf2DataBudgetTests' \
  --logger 'console;verbosity=normal'
python3 src/Infrastructure/tools/check_lpf2_data_budget_mutations.py \
  --runtime-root . --results-directory .local/lpf2-budget-results
```

The mutation tool recompiles the actual model and requires executed NUnit
assertion failures for ignoring a zero budget, never decrementing it and
reserving only after byte delivery. Build/setup errors are not detections. It
requires the same executed test identities, restores exact source bytes and
recompiles/retests the baseline afterward. Fresh result directories retain raw
TRX and separate stdout/stderr streams, including partial timeout output; existing
results are never overwritten. Raw results stay runner-local and are not uploaded
publicly by this workflow; hosted runner cleanup still limits their lifetime.
Console diagnostics retain result hashes and failed test names. Run
engine builds/tests on the hosted qualification runner. No compiled result is
claimed before those runs finish. Thirteen lightweight host controls run
with `python3 tools/test_lpf2_data_budget_mutations.py` from the Infrastructure
root: eight use mocked compiler results, and five check the small Python process
supervisor, including actual owned parent/child timeout and successful-exit
cleanup. They exercise failure classification, source restoration and evidence retention without invoking a C#
compiler or an engine. The supervisor retires the invocation's owned POSIX
process group on normal return, timeout or a caught Python exception before source restoration. Shared
C# compilation and MSBuild node reuse are disabled for mutation invocations.
Parent-only termination and omitted success-path cleanup mutations must fail
the live-descendant controls; the control cleans up only its own process group.
This does not guarantee cleanup of a descendant deliberately escaping its session,
or preservation after unhandled termination of the supervisor or whole hosted job.

For the subsequent actual firmware experiment, stop DATA, drain previously
received frames through real guest ioctls and account for pending UART bytes.
Permit one known DATA frame, observe it crossing the external UART boundary,
then run the invalid and valid output calls through the actual protected guest.
Check mode/payload/session and unchanged refusal buffers. Do not infer exact
non-consumption from repeated identical samples or read/write engine internals.
Active/reset/session qualification and atomic motor authority remain separate.

The existing adapter and device components retain their MIT grants and notices.
New test, mutation tool and documentation components use BSD-3-Clause, with the
full grant in `licenses/BSD-3-Clause.txt`. Retained discovery attribution and byte
fixtures are unchanged. This is not whole-firmware independence or blanket licence
clearance.

## First compiled model result and host-observer correction

[Runtime qualification](https://github.com/CrispStrobe/renode-spike-prime/actions/runs/37764701627)
at Runtime `8f7696aac606d8de90c1a6a0930e48655f03530a` and Infrastructure
`1253d925accca23dfda66d5bca61e78498dcb64f` passed the source-built native/runtime,
focused and complete managed peripheral checks. The complete suite reported
584 passed and five skipped cases; those skips are not executed coverage.
The dedicated baseline and restored baseline each passed all eighteen cases.
Ignoring the zero budget, retaining its allowance and reserving after delivery
were detected by fifteen, eleven and one failed assertions respectively.

One separate PR host-control run failed because Linux returned `ProcessLookupError`
while reading a disappearing `/proc` entry. Its source-attribution check passed.
The host observer now accepts that absence as stopped, alongside
`FileNotFoundError`; other errors and live processes still fail. A deterministic
regression reproduces the original exception before the correction. This changes
only host test code and this evidence, with unchanged C# model and mutation runner.
Affected actual firmware and separate MicroPython guest qualification remain
required before consumer adoption; model-suite success alone does not close them.
