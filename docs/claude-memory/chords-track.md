---
name: chords-track
description: "Dedicated permanent \"Accords\" timeline track — chords-only, bottom-pinned, non-deletable; chord/cadence insert always routes there"
metadata: 
  node_type: memory
  type: project
  originSessionId: f952e927-6dc4-4278-9d24-2ab81936122d
---

Added 2026-07-17. New `TimelineTrackType.Chord` (enum now Instrument/Drum/Chord). The timeline ALWAYS has exactly ONE chords track, pinned LAST (bottom of timeline + score), non-deletable.

Core in `TimelineScreen.xaml.cs`:
- `EnsureChordTrack()`: if no Chord-type track, ADOPT the first `TrackIsAllChords` track (keep its instrument+name) else create "Accords" (piano); then move it to the end. Called in the constructor, on load (after SyncUserStyleRefs — migrates existing pieces), in AddTrack, and at the TOP of `Render()` (the invariant holds no matter which path added tracks — composer/nappe/etc.). MIXED tracks (chords + riffs/lines) are left untouched — only a 100%-chord track is adopted; multiple chord tracks are NOT merged (collision risk) — only the first is adopted (told user).
- Insert routing: `btnAddPattern_Click` (Accords) and `btnAddCadence_Click` route to `ChordTrack` (via `AppendChord`), NOT the selected track — no need to select it. Context (prev chord/degree, preview instrument) comes from `LastChordOn(ChordTrack)` + the chord track's instrument.
- `UpdateToolbar`: Riff + `miInsertMelodicLine` = instrument-only; Accords + Cadence = ALWAYS visible (route to chords track); Batterie = drum-only; menu always enabled.
- Row build: no ✕ delete button when `Type==Chord`; instrument selector shown when `Type!=Drum` (so the chords track picks its accompaniment instrument).
- Pitched-track checks flipped from `==Instrument` to `!=Drum` so the Chord track is included: mode-change degree recalc, transpose, `scoreTracks` add (composer), MuseScore export default. Score/audio already iterate all tracks in project order → chord track renders last = bottom.

Helpers: `IsChordModule` (PatternGenerator|Cadence), `TrackIsAllChords`, `ChordTrack` prop, `LastChordOn`, `AppendChord`. Builds; NOT tested in-app.
