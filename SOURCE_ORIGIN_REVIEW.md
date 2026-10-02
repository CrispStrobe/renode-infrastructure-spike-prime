# Pybricks source comparison, 2 October 2026

The current tracked text at `3055d665135c1cdb5acb13c64954aa6684d4421f`
was compared against Pybricks
`101c6babb592148bda9a8fd912b7953c7d561c0a`, including tests and supporting
files outside the existing reference inventory. The review used exact lexical
and identifier-normalized sequences, followed by review of candidate context.

The matches include the already inventoried LPF2 discovery fixture and
TLC5955 channel mapping. The remaining candidates are licence text and generic
declaration patterns. A repeated all-`0xff` sequence in PacketTests also matches
a TI array; that generic repetition provides no evidence of payload import.
No additional uncredited Pybricks implementation was identified in the
reviewed current tree. The existing attribution/fixture check passed.

The method, thresholds, pins, raw report hash and candidate review are recorded
in the firmware fork's
[source-origin review](https://github.com/CrispStrobe/brickwright-spike-prime-fw/blob/main/policy/source-origin-review.json).
The reproducible scanner is `tools/audit_source_similarity.py` in that fork.

This comparison does not prove the absence of all short or paraphrased copying,
and does not audit every Renode component's licensing or historical commit.
Original third-party licences remain applicable. The separate firmware review
has now replaced the inherited host and firmware orientation bodies with
credited MIT Fusion adapters and validated a rebuilt configured firmware.
The approved firmware history cleanup now retires the old ancestry and 19
non-main public branch refs. Those findings are resolved for advertised firmware
branch/tag reachability, as recorded in its `policy/public-history-review.json`.
Retired private archives remain uncleared; GitHub caches and other clones are
outside that scope. This does not extend the scope of the Renode comparison.
