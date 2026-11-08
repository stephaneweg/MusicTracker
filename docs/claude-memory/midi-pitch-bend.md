---
name: midi-pitch-bend
description: "Pitch-bend support — detect MIDI pitch wheel on import, store a bend curve per RiffNote, glide the synth frequency on playback"
metadata: 
  node_type: memory
  type: project
  originSessionId: 146ef62c-1122-45dc-af55-2d32b4a88818
---

Pitch bends (note bends) imported from MIDI and rendered in audio (user-requested 2026-06-16). End-to-end, verified (a written-then-imported MIDI with a 0→+2-semitone ramp → 5-point curve → carried to the RiffNote → interpolates correctly).

**Model** ([Engine/RiffNote.cs](MusicTracker/Engine/RiffNote.cs)): new `BendPoint {int Off; float Semis;}` (Off = slices from note start, Semis = signed semitones) + `RiffNote.Bend` (`BendPoint[]`, null = none). Curve interpolated linearly via `RiffNote.BendValue(BendPoint[] b, int off)` (static) / `BendAt(off)` (instance). RiffNote is a struct; the Bend reference is shared on value-copy but never mutated in place. NB the composer/score/transpose/meter pipeline reconstructs RiffNotes via `new RiffNote(note,start,length)` → drops Bend (acceptable: bends only come from MIDI import, and import→timeline→playback doesn't reconstruct).

**Detection (import)** ([Engine/MidiImporter.cs](MusicTracker/Engine/MidiImporter.cs)): per track, collect `NAudio.Midi.PitchWheelChangeEvent` into `pwList {sliceAbs, channel, milli-semitones}` (semis = (Pitch-8192)/8192·2; default bend range ±2). `BuildBend(pwList, channel, startSlice, endSlice)` → a point at offset 0 (the wheel value active at note start) + a point per wheel change during the note; null if all |semis|<0.02. Stored on `MuseScoreImporter.Note.Bend`. (MuseScore .mscx import doesn't populate bends yet.)

**Carry to riff** ([Engine/Timeline/ScoreSlicing.cs](MusicTracker/Engine/Timeline/ScoreSlicing.cs) GetOrCreateMeasureRiff): builds RiffNotes DIRECTLY (not a binary slice grid), so it copies Note.Bend → RiffNote.Bend with offsets scaled by src→dst spq; the measure-dedup cache KEY now includes the bend (so bent vs unbent identical notes don't collide).

**Render** ([Engine/Flow/FlowPlayer.cs](MusicTracker/Engine/Flow/FlowPlayer.cs) RiffPlayer): the synth already passes the frequency PER SAMPLE per note (`voice.GetNext(freq, ...)`), so bend = `freq *= 2^(semis/12)`. `attackAt` changed from `List<int>` to `List<RiffNote>` (carries the curve); on attack, `bendOf[note]=n.Bend`, `bendStart[note]=slice`; ReadNext reads `RiffNote.BendValue(bendOf[note], noteIndex - bendStart[note])` each sample. No WaveFunction change. (Reverse playback doesn't reverse the bend curve — rare edge case, left as-is.)
