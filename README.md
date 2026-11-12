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

## Trademarks and affiliation

This project is independent. It is not affiliated with, endorsed by or sponsored by the LEGO Group, the Scratch Foundation/MIT, or the TurboWarp project.

LEGO®, MINDSTORMS®, SPIKE™, Boost, Powered Up, WeDo, NXT and EV3 are trademarks of the LEGO Group, which does not sponsor, authorize or endorse this project. Scratch is a trademark of the Scratch Foundation/MIT; TurboWarp is an independent open-source project. Other hardware and software names are trademarks of their respective owners. All names are used only to identify compatible hardware, software and file formats; all trademarks remain the property of their respective owners.
