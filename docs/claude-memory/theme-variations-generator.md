---
name: theme-variations-generator
description: gen_variations_mscx.ps1 — turns a theme.mscz into ~31 variations exported as a 2-staff .mscx
metadata: 
  node_type: memory
  type: project
  originSessionId: 4eb9a348-dcc8-4f05-a40f-1133df0c0831
---

Repo-root script `gen_variations_mscx.ps1` (run via reflection on the compiled `bin/Debug/MusicTracker.exe`). It imports the user's theme from `C:\Users\swe\Documents\MuseScore3\Partitions\theme.mscz` (4-bar theme, G minor, chords i-V-i-V = Gm/D/Gm/D hard-coded), generates ~31 melodic+harmonic variations, lays them on two aligned staves (Melodie treble + Accords bass), and exports `theme_variations.mscx` via [[musescore-export-tag-order]]'s `MuseScoreExporter`. Units: 24/quarter, 96/bar.

**Design rules (the user cares about these):**
- Every variation must span the theme length (4 bars) **or a whole multiple**. `FillVar` repeats short ones up to 4 bars and legato-fills to the bar line (no unwanted trailing rests).
- **Register control** (added 2026-06-18): `FitRange` octave-shifts each variation by a whole number of octaves so its MEAN pitch lands on the middle line of the treble staff (`$STAFF_MID = 71` = B4 = "zone canonique"). A high source theme (it sits at MIDI ~79-87, Sol5-Mi♭6) is therefore pulled down an octave automatically; contour preserved. Replaced the old "drop notes > MIDI 96" filter. Because centering nullifies any deliberate octave displacement, BOTH `Octave aigu` and `Octave grave` variations were removed. (Earlier interim version centered on the theme's own tessitura with a themeHi+5 ceiling — superseded by the staff-middle centering at the user's request.)
- **Ritournelle / figural variations** (added 2026-06-18): `Figuration $baseT $baseChords $pattern` = rhythm becomes a continuous 16th arpeggio, each beat anchored on the THEME note sounding then (the strong-beat "idea") and spun from that bar's chord tones via a pattern of chord-step offsets (e.g. @(0,1,2,3) montant, @(0,2,1,2) Alberti, @(0,1,2,1) vague, @(0,2,1,3) brisé). Always exactly 4 bars.

**Variation families (43 total as of 2026-06-18):** augmentation/diminution, inversion, rétrograde, rétro-inversion, transpositions (5th/4th/3rd), octave grave, mode majeur, broderies, rythme pointé, syncope, triolets, séquence, fragmentation, arpégiation, expansion/compression intervallique, inversion+transpose, 2 réharmonisations, 4 modulating V1 GhibliComposer.BuildVariations (sol/re/la/mi m), 4 ritournelles, + (added all 8 requested families): 4 modes (`ToMode` with $DOR/$PHR/$HARM/$MEL), gammes de liaison (`ScaleRun`), pédale/bourdon ($drone = tonic+5th per bar), canon (`Canon`, leader + delayed octave-below copy, truncated to 4 bars), choral homophone (`Choral`, ≤3 chord tones below each theme note), déplacement métrique (`Displace`, rotate theme in the 4-bar window), ornementation baroque (`Ornee`, grupetto on long notes, over the ×2-augmented theme = 8 bars). Per-variation BASS STYLE: AddV's 4th arg ('block'|'alberti'|'walking'); the layout loop renders the Accords staff accordingly — basse d'Alberti (8 eighths) and basse marchante (4 quarters).

**Two PowerShell gotchas hit while writing this (watch for them on future edits):**
1. Variable names are CASE-INSENSITIVE: a local `$bar` silently overwrote the global `$BAR` (=96 units/bar) → division by zero → "∞". Use distinct names (`$br`).
2. The comma operator has HIGHER precedence than `*`/`+`, so `@(a, b+2*c, d)` mis-parses (treats `a,b` as an array, then multiplies) and throws "Object[] does not contain op_Multiply". Always wrap arithmetic array-elements in parens: `@(a,($b+2*$c),$d)` — the rest of the script already uses the `[int](...)` idiom.
