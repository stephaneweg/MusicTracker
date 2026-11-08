---
name: hand-composed-mscx-pieces
description: "Claude-as-composer scripts that write a complete Ghibli-style piece straight to .mscx (no engine), incl. a 6/8 orchestral one"
metadata: 
  node_type: memory
  type: project
  originSessionId: 7e3e9e06-fd16-434f-8088-c87af4ab2c9d
---

When the user asks Claude to "composer une musique" from a text brief (style/mood prompt) and export to MuseScore — distinct from the generative composer engine — the established workflow is a **repo-root PowerShell script that emits the .mscx by hand**, Claude acting as the composer (writing the actual notes), NOT calling GhibliComposer/the corpus model. The two pieces the user means by "les deux musiques":
- `compose_ghibli.ps1` -> `ghibli_lullaby.mscx` (solo piano berceuse, C major 4/4, 2 staves)
- `compose_ghibli2.ps1` -> `ghibli_romance.mscx` (violin + piano, C major 4/4, 3 staves)
- `compose_ghibli_orchestral.ps1` -> `ghibli_orchestral.mscx` (added 2026-06-19, from a 6/8 orchestral-fantasy brief): **D major, 6/8, 7 staves** (Violon/Flute/Cordes/Harpe/Piano RH/Piano LH/Contrebasse), 34 bars, form Intro4 | Verse8 (violin+piano duo) | Midpoint8 (strings+harp+woodwind open up + 2-bar build) | Climax8 (tutti, theme +8ve soaring to F#6, bVII Cmaj9 "ocean swell", low swells = timpani foundation) | Outro6 resolving to Dadd9.

**Shared .mscx-emit recipe (copy from any of the three):** token = `"<midi[+midi...] or r>/<dur>"`; the orchestral one EXTENDED dur to support dotted + 16ths (`8` `4` `4.` `2.` `16`, trailing `.` = dotted) and emits `<dots>1</dots>` BEFORE `<durationType>` (see [[musescore-export-tag-order]]). 6/8 bar = 12 sixteenths; the script self-validates every bar sums to 12 and every staff has N bars (throws otherwise — catches hand-entry slips). `Tpc($pc)` = line-of-fifths spelling map per key (D major: F#=20, C#=21, C nat=14). KeySig `<accidental>` = sharps count (D major = 2). Multi-instrument = one `<Part>` per staff (programs e.g. Violon 40, Flute 73, slow-strings/"choeur" 49, Harpe 46, Piano 0, Contrebasse 43). Title via `<VBox>` on staff 1; `<Tempo>` value = quarters/sec.

**Gotchas:** keep the script **ASCII-only** (no accents/em-dash) — PS 5.1 mangles non-BOM UTF-8 and breaks parse/equality; use ASCII labels ("Flute" not "Flute"-accented). Run with `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <script>`. Verify by re-importing: `MusicTracker.Engine.MuseScoreImporter.Load(path)` (load NAudio dlls + the bin\Debug exe first) — confirms MuseScore can read it + prints track/note/range/SliceCount (slices/bar in 6/8 = 72, so bars = SliceCount/72). Engine-generated alternative for the same style = [[hisaishi-ghibli-analysis]] (GhibliComposer) + [[theme-variations-generator]].
