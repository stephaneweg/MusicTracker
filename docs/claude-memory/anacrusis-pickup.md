---
name: anacrusis-pickup
description: anacrouse/levée support — barPhase model shifts the score barline grid + engines; audio unchanged
metadata: 
  node_type: memory
  type: project
  originSessionId: f952e927-6dc4-4278-9d24-2ab81936122d
---

Anacrusis ("levée") = incomplete first measure. Added 2026-07-13.

- Model: `TimelineProject.PickupBeats` (QUARTER-beats; croche=0.5), saved with project. Toolbar combo `cboPickup` ("Levée", `PickupOpts`/`SyncPickupCombo`/`Pickup_Changed` in TimelineScreen.xaml.cs) — notation-only, calls RefreshScore.
- ScoreView: field `barPhase` (= `pickupBeats*scale % beatsPerBar`, display-beats) passed via `Configure(..., pickupBeats)`. ALL measure math routed through helpers `MeasureIndex / MeasureStartBeat / MeasureCount / NextBarline` + phased `IsBar`. barPhase==0 reduces EXACTLY to the old floor(beat/bpb) grid (no regression). Full barlines fall at `barPhase + k·bpb`; measure 0 = the short pickup bar.
- Engines (user chose "align them too"): `Harmony.ChordAt` phases the STRUCTURE chord grid by `project.PickupBeats` (pickup borrows chord[0]); `MelodicLineEngine` strong-beat test = `(absBeat - pickup)` integer. Both read `project.PickupBeats` directly (no new params). **Audio playback timing is unchanged** — purely metric/notation.
- Timeline measure ruler IS phased (2026-07-13): `MeasureRulerControl.Configure(..., pickupBeats)` shifts barlines+numbers right by the levée (pickup bar drawn at x=0, no number; bar "1" at phase). Fed `project.PickupBeats` (same "temps" unit as `RulerBeatsPerBar`); `Pickup_Changed` now calls Render()+RefreshScore(). Exact for x/4; x/8 ruler-vs-score units may differ slightly.
- Anacrusis-aware motif DUPLICATION (2026-07-13): `Engine/Timeline/MotifCopy.cs` (LeadRemainder / TrimNotes / TrimSlices) + `TimelineScreen.CopyLeadRem(totalBeats)` (gated on PickupBeats>0). Rule: user manually sizes the FIRST module to N·bar+pickup (e.g. 7 in 3/4); every COPY drops the leading remainder (7→6, bar-aligned, shifted left). Applied in: PropagateMelodicLine, InsertMelodicLine, PropagateUserStyle+SyncUserStyleRefs (chord rhythm — ANCHOR = first referencing chord in document order keeps full, rest trimmed; anchor logic identical in propagate & load-sync so anacrusis survives save/reload), PropagateMelodic (chord cell — anchor keeps its own, rest trimmed), NewChordLike (append chord). Melodic line & cell persist per-module (no re-sync); chord rhythm re-syncs from shared UserChordStyle so needs the anchor logic. No levée ⇒ CopyLeadRem=0 ⇒ zero change.
- NOT yet phased (known limitation): PDF export (`ScorePdfExporter`) still counts from beat 0.

Related: [[score-view]], [[meter-and-snap-rules]].
