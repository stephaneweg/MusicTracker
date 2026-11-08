---
name: musescore-export-tag-order
description: "In MuseScoreExporter .mscx output, <dots> must be written BEFORE <durationType>"
metadata: 
  node_type: memory
  type: project
  originSessionId: 4eb9a348-dcc8-4f05-a40f-1133df0c0831
---

In the MuseScore `.mscx` exporter (`MusicTracker/Engine/Timeline/MuseScoreExporter.cs`, methods `EmitChord` and `EmitRest`), the dotted-note point must be emitted as `<dots>` **before** `<durationType>` inside each `<Chord>` / `<Rest>`. That is the order MuseScore itself uses in its saved files; the user diffed our export against an original MuseScore file and this tag order was the only remaining difference.

**Why:** MuseScore renders both orders the same, but matching its canonical order keeps our export byte-clean against real MuseScore files (and avoids spurious diffs).

**How to apply:** When editing the exporter, keep `<dots>` first. Do NOT reorder to `<durationType>` then `<dots>` — that was the earlier (wrong) order, fixed 2026-06-18. Note `<dots>` is a separate element from `<durationType>`, so a dotted note spans two lines; when reading durations from an .mscx, count both together (a "quarter" with `<dots>1</dots>` is a dotted quarter = 1.5 beats). Related: [[score-view]].
