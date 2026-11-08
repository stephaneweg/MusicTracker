---
name: ghibli-corpus-mood-layout
description: "corpus/Ghibli reorganized into 6 mood folders; audio features don't predict mood for Hisaishi"
metadata: 
  node_type: memory
  type: project
  originSessionId: e2aba0d4-4cbb-41b8-acf5-67b824ad3f7d
---

`corpus\Ghibli\` was restructured (2026-07-05) from film subfolders into 6 mood folders: **Calme_Nostalgique, Épique_Majestueux, Enjoué_Léger, Valse_Dansant, Sombre_Dramatique, Solennel_Requiem**. Files pulled from the old film subfolders (Laputa/Mononoke/Totoro/Nausicaa) were renamed with a `Film__` prefix (e.g. `Totoro__kaze.mid`) to avoid name collisions; loose root files + `.mscz` kept their names. The old film folders were deleted once emptied. `corpus/Ghibli/restore_ghibli.sh` reverses the whole move (film layout). `.mscz` were classified by title only (not machine-analyzed).

Classification is **by title**, deliberately. I built [analyze_ghibli_mood.ps1](../../analyze_ghibli_mood.ps1) (repo root) to verify moods objectively via the real MidiImporter (tempo / Krumhansl-Schmuckler mode / notes-per-bar density / meter / range).

**Why:** The objective run proved audio features do NOT map to mood for Hisaishi — minor key is usually *nostalgic* not dark (e.g. "Nostalgia", Kimi wo Nosete), arpeggiated accompaniment inflates note-density independent of energy (Totoro "kaze"/Path of Wind reads 70 notes/bar), and lyrical major pieces aren't "joyful". A naive minor→Sombre / dense→Enjoué heuristic mis-flagged ~50 files.

**How to apply:** Trust titles/known-piece knowledge over tempo/mode/density for this composer — do NOT feature-override. Proof: I moved 5 cryptic bpm≥120 & density≥20 files out of Calme→Sombre; the user (who knows the pieces) corrected **4 of 5** straight back — `Laputa__lapued/laputa_sc/sorakara` are just fast *Kimi wo Nosete* (Carrying You) variations = Calme, `Laputa__laputa1` = Enjoué. Only `Nausicaa__kite` stayed in Sombre. So fast+dense+minor does NOT mean dramatic here. See [hisaishi-ghibli-analysis](hisaishi-ghibli-analysis.md) for the corpus's musical fingerprint (pentatonic, modal, colour harmony).
