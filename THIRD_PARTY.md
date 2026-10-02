# SPIKE model source/reference inventory

The custom LPF2 model includes 530 discovery bytes copied from Pybricks
`lib/pbio/test/src/test_uartdev.c` at the commit recorded in
`pybricks-references.json`. They match the prefix of the concatenated
`test_technic_large_motor` messages. This is acknowledged MIT data reuse,
not an independent fixture or clean-room claim. The original copyright
notice is retained beside the array and the full upstream MIT grant is
in `licenses/Pybricks-MIT.txt`.

The TLC5955 display mapping references MIT Pybricks Prime platform data.
Its source/reference notice and immutable upstream path are recorded too.
Neither addition links the Pybricks firmware or interpreter. The fixture
can be replaced with independently constructed valid protocol messages;
that is separate work requiring equivalent handshake tests.

This inventory covers these additions, not all Renode components. Renode
and bundled dependencies retain their own MIT, BSD, LGPL and other
licences under `licenses/` and individual file notices.

Run `python3 tools/check_pybricks_references.py` before publishing changes
to the inventoried model code.
