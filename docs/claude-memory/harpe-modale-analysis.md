---
name: harpe-modale-analysis
description: "Analysis of the user's harp MIDI (f14482127) → modal-mediant cadence style 27 + \"Harpe (arpèges)\" composer style"
metadata: 
  node_type: memory
  type: project
  originSessionId: 146ef62c-1122-45dc-af55-2d32b4a88818
---

The user shared a HARP MIDI (`f14482127.mid`, copied to repo `midi\harpe\harpe1.mid`) — harp + bass + "progressive arpeggiation" — to base a CADENCE style or a full STYLE on. Analyzed via the script suite (80 bpm, 4/4, ~50 bars; GM 46 = orchestral harp; tracks: melody + two arpeggiating harp voices (16ths ×368, 8ths ×376) + a dotted-eighth inner voice + a SUSTAINED bass of ~whole notes low 45-52).

**The distinctive profile = MODAL / MEDIANT, not functional:**
- Harmony: V→I only 7%! Instead **I↔vi (vi→I = 45% of tonic approaches), I↔♭III (♭III→I = 34%)**, plus IV, ♭VII↔ii. Closing 100% vi→I. Functional flow TT 51% (tonic-prolonging). The ♭III (flat-mediant) is the signature "new-age/celtic/cinematic" colour. Tonic gets 6/add9 colour.
- Very consonant & open: 5ths 34%, oct/unis 32%, dissonant 1%, contrary motion 3%. Bass extremely smooth (92% stepwise lowest-voice).
- Texture: continuous arpeggiation (16th 40% + 8th 38% + dotted-8th 16%), broken-chord LEAPS by 3rds/4ths (step 40% / leap 54%), over a SUSTAINED bass.

**IMPLEMENTED (2026-06-15):**
- **Cadence style 27 "Modale / médiantes (harpe)"** (MusicTheory.cs): `Tile(I, vi, ♭III, IV)` — tonic-prolonging modal oscillation, NO dominant; works in major & minor; AutoRhythmStyle(27)=arpège. Reusable in the chord-editor cadence generator too. Verified: C major → Do6 / Am7 / E♭maj(♭III) / FM7 / … 
- **Composer style 8 "Harpe (arpèges modaux)"** (Composer.cs, `cfg.harp`): cadence 27, SLOW harmony (cpb=1, no modulation), SUSTAINED figured bass (`GenerateFiguredBass(..., sustain:true)` = one held root/bar), arpeggiated chord accompaniment (chordArt=3), harp ensemble (GM 46), lyrical period melody. Verified: no drums, bass ~whole-note/bar. TODO refine later: two arpeggio layers at different rates; open-5th voicings; faster intra-bar arpeggio motion.

**(2026-07-08) Roulis harpe sensible à la métrique** : le style/articulation **27** (`PatternGenerator.BuildVoiceEvents` case 27) émettait toujours des doubles-croches binaires (`q/4 = 6` slices). Ajouté flag statique **`PatternGenerator.Ternary`** (le temps fait toujours 24 slices) → en ternaire la double-croche du roulis = `q/6 = 4` slices (÷2 = 2). Propagé via param `ternary` à `BuildVoiceEvents` (seul le case 27 l'utilise ; `GenerateCadence` hérite via `Generate`). Flag posé depuis `project.TimeSigDen==8` dans `SyncKeyToolbar` (UI), `ScoreBuilder.Build` (score==lecture, off-thread) et `TimelineImporter` (import). Voir [[meter-and-snap-rules]].
