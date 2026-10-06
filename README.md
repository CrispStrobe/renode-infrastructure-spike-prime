# README

For the current SPIKE model scope and concrete follow-up work, see
[the state and task lanes](docs/SPIKE-STATUS-AND-LANES.md). The short LPF2
checkpoint below is historical; it is not the complete current fork inventory.

This fork adds the MIT-licensed `LegoLpf2Port` UART adapter and the
transport-neutral `ILpf2Device` contract. Current deterministic devices are a
type-62 ultrasonic sensor and type-48 medium motor. NUnit fixtures verify exact
handshake bytes, checksums, mode/output handling, encoder/load/stall behavior
and parser recovery. Electrical attachment signaling, scheduler timing and the
full device catalog remain outside this checkpoint.

See [Renode README.md](https://www.github.com/renode/renode/blob/master/README.md).
