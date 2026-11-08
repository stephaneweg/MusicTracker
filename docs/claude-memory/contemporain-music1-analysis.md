---
name: contemporain-music1-analysis
description: "Analysis of the user's MIDI \"music1.mid\" (a target style to compose in) — slow tonal layered ballad, no drums"
metadata: 
  node_type: memory
  type: project
  originSessionId: 146ef62c-1122-45dc-af55-2d32b4a88818
---

The user shared `music1.mid` (copied to repo `midi\contemporain\`) as a "more contemporary" style to eventually COMPOSE IN. Analyzed via the full script suite. Profile:

- **Structure**: slow **57 bpm, 4/4, ~71 bars, NO drums** (no channel-10 track). 8 tracks, heavily DOUBLED (several identical parts): acoustic bass (low, 24-53) + 2 acoustic pianos + 2 nylon guitars (mid chordal body) + a sparse high lead ("Distortion", 72 notes) + 2 honky-tonk (high). Instruments enter/exit over the piece (layered build-up; voice-entry stagger ~139 qtr).
- **Harmony**: NOT atonal/"contemporary-classical" — it's strongly TONAL/FUNCTIONAL major. V→I 92% of tonic approaches, closing cadence 100% V→I, trigrams IV-V-I (62%) / iii-V-I (31%). Uses iii a lot + secondary dominants (II = V/V, #iv° = leading-tone of V). Harmonic rhythm ~2.2/bar. Closest to a film/game/folk BALLAD, not avant-garde.
- **Rhythm**: VERY varied / through-composed — rhythm self-similarity only **5%** (vs Bach/Vivaldi 38-67%): NO dominant pulse. All durations mixed (16th/8th/32nd/half/qtr + 10% quarter-triplets + dotted). Expressive/rubato feel. The 32nds (13%) are **100% isolated** = articulation/micro-timing, NOT virtuosic runs/trills/tremolos.
- **Melody**: 40% step + 30% repeated + 29% leap (mostly 4ths/5ths); run length ~1.9; motif repetition 64% (motivic, recurring cells).
- **Texture/dynamics**: BASS very stepwise (79% — a melodic walking bass, more than Bach/Vivaldi); outer voices 3rds 29% + open 5ths 29% (folk/open colour); contrary motion only 22% (homophonic/parallel); detached 27% (small 32nd gaps = plucked articulation), few real rests.

**IMPLEMENTED (2026-06-15) — "Ballade / film" composer style (Composer.cs, style index 7)** (first version, to refine): `cfg.ballad` → no drums, functional tonal cadences {4,5,0} (Anatole/Pop/Auto), period phrasing, wide-arpeggio accompaniment (chordArt 24), warm ensemble (violin/strings/nylon/flute) + acoustic bass; `parallel`=true so extra voices DOUBLE the lead (ParallelVoice, 3rds/6ths); a new FREE rhythm profile 4 (`CellsBallad` — mixed quarters/eighths/dotted/16ths/eighth-triplets, no dominant value). Dialog defaults it to 2 voices. Verified: no drums, rhythm self-similarity 10% (free, vs 5% source / 38-67% baroque), bass 66% stepwise, voices 85% 6ths + 94% parallel motion. TODO refine: true rubato (cells can't make long held notes / sub-5% self-similarity), open-5ths accompaniment, progressive layered entries, slow tempo (composer doesn't set tempo — user sets ~57 bpm).

**To compose in this style**: slow (~55-60 bpm) 4/4, no drums; tonal functional major harmony (I-IV-V, ii-V-I, IV-V-I + iii + secondary dominants II/#iv°), ~2 chords/bar; layered & doubled texture (bass + several mid chordal instruments + sparse high lead, progressive entries); melodic stepwise bass; FREE/expressive rhythm (no fixed pulse — mix durations, triplets, dotted, long notes; detached articulation); recurring stepwise motifs with occasional 4th/5th leaps; open 5ths in the accompaniment. A potential new composer style (distinct from the baroque/classical ones — its signature is the LACK of a motoric pulse + the layered doubled no-drum texture).
