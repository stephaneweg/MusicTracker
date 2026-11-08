---
name: melodic-line-improvements
description: "Melodic-line engine upgrades — cross-module continuity/voice-leading, tension slope, and reuse variations (Split/Gate/Retrograde/Mirror)"
metadata: 
  node_type: memory
  type: project
  originSessionId: f952e927-6dc4-4278-9d24-2ab81936122d
  modified: 2026-07-24T15:10:19.010Z
---

Built 2026-07-17 from a user doc (Gemini spec, only Feature 1 arrived) + user's own list. Improves [[melodic-line-feature]] generation. All in `MelodicLineEngine.GenerateLine`, additive, defaults = old behavior.

New `MelodicLineModule` fields:
- **Continuity** (0-100): voice-leading attraction. After the contour picks a note, if the leap from the previous note exceeds `maxLeap = 12 - Continuity*11/100` (0→12 free, 100→1 stepwise), pull to the nearest allowed tone (naturally favors the COMMON tone at a chord change = Feature 1's smoothing). Also gates the cross-module join.
- **Variation** (0-4, `VariationNames`): 0 none · 1 **Split** (cut notes ≥ a quarter into two halves, a fill) · 2 **Gate** (drop every other note → aerate) · 3 **Rétrograde** (reverse in time) · 4 **Miroir** (invert intervals around the first note's pitch). Split/Gate act on the rhythm SKELETON before pitch gen; Retrograde/Mirror transform the generated notes THEN `Resnap` each to the chord/scale at its (new) beat — so they stay consonant with the FIXED progression (user-confirmed "inverse puis re-snappe").
- **TensionSlope** (± semitones): the register band drifts linearly from the first note to the last across the module.

**Cross-module continuity (Feature 1a)**: `GenerateLine` gained optional `int[] carry` (per-voice last pitch). Threaded per-track in BOTH `TimelinePlayer` (audio) and `ScoreModel` (score — must match audio) so a new module's line STARTS near where the previous ended. The 2 preview call sites in TimelineScreen pass none (carry=null). Each track resets `carry={-1,-1,-1}`.

UI: 3 controls added to `BuildMelodicLineEditor` (TimelineScreen) after Ancrage — Continuité (ParamNum 0-100), Variation (ParamCombo), Tension (ParamNum ±). Builds; arithmetic Python-verified; NOT ear-tested in-app.

NOTE: this is the SECOND melody-related build after the big voice-leading/fingerstyle system was fully REVERTED (user disliked it). Kept: S1/S2 chord-style bug fix. Deferred (user "de côté"): melody fret-playability (hand-position). Feature 2 of the Gemini doc never arrived.

**2026-07-24 — harmonic-by-metric rules + HIERARCHICAL MULTI-PASS rewrite** (commits ebd5d58, 6b7ccb1). `GenerateLine` no longer runs one left-to-right pass; it fills by metric level so each note sees BOTH fixed neighbours:
- `ClassifyMetric(phased,num,den)` → 0 fort · 1 demi-fort (3rd beat of a QUADRUPLE bar only) · 2 faible · 3 entre-deux. Compound /8 (num%3==0) uses the dotted-quarter as the main pulse (`beatSlices`). Verified across 2/4·3/4·4/4·6/8·12/8.
- **Pass 1** downbeats = harmonic skeleton (carries the CONTOUR, anchor on the 1st, voice-leads from the previous fort). **Pass 2/3** demi then faible = chord tones via `ChordToneConnect(L,R,pcs,band)` (nearest to the L/R midpoint, EXCLUDING both endpoints so the line moves). **Pass 4** entre-deux = `PassingBetween` (real passing tone strictly between L and R, else a/b broderie/appoggiatura) or, if the note is a SYNCOPE (contretemps held over the next beat), a chord tone of the current-or-next chord.
- **Anti-repeat** compares by ROOT only (Sol7==Sol=="V"). `Ornaments` field (0-100, default 0 = OFF) gates opt-in suspension/appoggiatura (appoggiatura = step ABOVE the chord tone resolving DOWN; earlier bug had it resolving up → "Do Ré" instead of "Si Do").
- **Cross-module downbeat carry**: the `carry` int[] was widened 3→9 per voice: [0..2] last pitch (continuity), [3..5] last DOWNBEAT pitch, [6..8] its chord root. Lets a fort avoid repeating the previous bar's fort ACROSS a module boundary (bar4 fort==bar5 fort==Ré under V was the reported bug). Both `ScoreModel` and `TimelinePlayer` allocate length-9; the 2 TimelineScreen preview sites still pass null. Engine falls back gracefully if carry.Length<9.
- Removed the old sequential helpers (`PickBetween`, `forceMidi`/`nextTarget` guessing). Verified on `~/Desktop/poc.sq` (C major 2/4, I vi ii V / V IV V/V V): every on-beat note is a chord tone, passing tones correct (Sol Fa Mi, Do Re Fa), no bar-to-bar bounce. NOT yet ear-tested in-app by the user.
