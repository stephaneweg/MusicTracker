---
name: ghibli-melody-rhythm-feedback
description: "User ear-validation of full-algo Ghibli (2026-07-05): harmony+melodic-intention GOOD, melody RHYTHM is the weak point (jerky, too many rests, no coherent rhythmic cells/phrase)"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 28d7fe65-4a3a-4950-9868-dbadfe0ac8d1
---

**User ear-test of the full-algo Ghibli (catalog off, Viterbi + interval/contour active), 2026-07-05:**
- Harmony = "joli" ✅
- Melody pitch = "ça me semble avoir une intention" ✅ → **items 1 (Viterbi) + 2 (viewpoints) are working on PITCH.**
- **Melody RHYTHM = the problem:** "brouillon", "saccadé", "trop de silence", "rythme pas équilibré". Wants rhythmic CLUSTERS that associate well (e.g. `2 croches + noire`, `4 croches`, `noire pointée + croche`) forming a **coherent PHRASE** — not per-beat randomness.

**Why:** the default melody-rhythm generator `BaseComposerV3.GenerateMelodyRhythm` draws a rhythm cell **independently per beat** from the RhythmCell Markov chain (bounded holds/rests) → variety but no phrase-level coherence + scattered mid-phrase rests. The alt `GeneratePhraseMotifRhythm` (UseMotifRhythm=true) tiles exactly 2 cells → coherent but too rigid, no variation/cadence.

**How to apply:** rhythm needs PHRASE/BAR-level structure (Nierhaus item 4 "états = motifs, pas notes"). Fix in progress = new `GeneratePhraseRhythm`: sample ONE bar of note-bearing cells (full corpus palette via PickCellNoteBearing → allows q.+8 etc.), REUSE it across the phrase with light last-cell variation, NO mid-phrase rests (phrase-end breath stays, handled by GenerateLine's hold/rest). Made the default melody-rhythm path. NOT yet ear-validated. Next levers if still off: antecedent/consequent rhythm (AAB'), cadential lengthening, or a real bar-level rhythm Markov. See [[algorithmic-composition-nierhaus]], [[hisaishi-ghibli-analysis]] (Ghibli = square phrases, flowing 8ths).
