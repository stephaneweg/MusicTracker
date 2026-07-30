# MusicTracker — What's New

This file feeds the **"What's New"** widget on the app's home screen.
MusicTracker downloads it straight from GitHub (raw URL), so news can be announced
**without shipping a new build**: just edit this file and push it.

The app fetches a language-specific file — `CHANGELOG.md` (French, the source) or `CHANGELOG.<code>.md`
(e.g. `CHANGELOG.en.md`) — based on the UI language. The base URL is configured in
`MusicTracker/App.config`, key `ChangelogUrl`.

## Expected format

One entry per line: `- <emoji> <text>`.
The first "thing" after the dash is taken as the icon, the rest as the text.
Everything else (headings, blank lines, paragraphs) is **ignored** — so this file stays readable on GitHub.
The most **recent** entries go **first**; the app only shows the first few.

## Entries

- 🎚 VST3 (beta): Koton now also hosts **VST3** plugins — insert effects AND instruments — alongside existing VST2. The scanner picks up `.vst3` (plain files or `Foo.vst3/Contents/x86_64-win/` bundles) from `%CommonProgramFiles%\VST3` and the local `vst\` folder. Automatic routing: `.vst3` → in-house Steinberg P/Invoke host, `.dll` → VST.NET as before. Beta: MIDI CC / pitch-bend / program change are not wired for VST3 instruments yet — notes are enough to try a melodic synth.
- 📁 Portable `vst\` folder: the VST plugin scanner now also inspects a `vst\` folder next to `KotonStudio.exe` (recursive, in addition to the usual Windows paths) — bundle your favourite plugins with a portable install, or try a plugin without installing it system-wide. A local plugin takes precedence over a system plugin with the same name.
- 🎹 VSTi instruments (beta): in a track's header, a new "VSTi instrument…" button replaces the GM MeltySynth patch with a third-party VST2 synth — Vital, Surge XT, TAL-U-No-LX, Spitfire LABS, etc. Menu → filtered list (VSTi only, effects excluded) → click a name; "Edit VSTi…" opens the native GUI, the loaded patch is persisted in the project. The pipeline (mixer, inserts, automation, audio export) is untouched — the VSTi just replaces the sound source. Beta: a crashing plugin can take Koton down, no VST3 or sandboxing yet.
- 🎛 VST plugins (beta): mixer inserts can now host third-party VST2 plugins in addition to the four built-in effects. Menu "+ FX ▸ VST…" scans `%ProgramFiles%\VstPlugins` and `%CommonProgramFiles%\VST2` and lists what it finds; clicking the plugin's name opens its native GUI, and the patch is persisted with your project. Beta: a crashing plugin can take Koton down with it — use at your own risk.
- 🎚 Console-style mixer + per-track insert effects: open the mixer to get a vertical strip per track (fader, pan, mute/solo, level meter) plus a Master strip. Each track (and the master bus) has its own insert chain to fill in as needed: 3-band EQ, Compressor, Delay (with ping-pong) and Saturation. Under the hood, each track now has its own synth and is rendered in parallel — that's what makes per-track processing possible.
- 🎛️ Per-track MIDI automation: right-click a track header → "Add automation" to draw a curve for Pan, Expression, Modulation, Sustain, Reverb, Chorus or Pitch bend alongside the volume lane — click the curve to add points, drag to move, right-click to delete. Double-click a point to reset it to its neutral value.
- 🎼 MusicXML export (Export menu): save your score in the interchange format every notation program reads — Finale, Sibelius, Dorico, MuseScore, online editors and tablet score readers. Notes crossing a barline are now written tied instead of being truncated.
- 🎵 Swing: under "Bar", pick uneven eighths (light, medium, swing, triplet). Playback and audio export follow the groove; the score and MIDI export keep straight eighths.
- 🎶 "Add an instrument / drums with AI" (Track menu): describe an intention; the AI receives the WHOLE piece (every track + the chords) and composes a new track — melodic line (rhythm only) or full melody, or a groove — that fits the existing arrangement.
- 🌍 Language selection (French, English, German, Italian, Spanish, Dutch, Portuguese) in Settings: the interface, the what's-new feed and AI-generated titles all follow the chosen language. More languages can be added by dropping a `lang.xx.json` file.
- 🎲 Generative project templates: a template now holds banks of material per section (progressions, accompaniment patterns, melodic cells, per-instrument phrases, grooves). On opening, the app draws from them and assembles the piece — a "Regenerate" button pulls a new version.
- 🤖 Generate your own styles with AI: describe an intention and the AI builds the template. Right-click a card to regenerate it reusing your intention.
- 🎻 A template's phrases are transposed modally over each chord (with voice leading), and the AI can leave an instrument silent on a section to give the arrangement air.
- 🎹 Much-improved audio rendering: instrument balance fixed (de-duplicated SoundFont modulators and filter-modulator support), no more gap on the piano.
- 🎚️ Velocity-based dynamics: strong beats stand out, off-beats recede — playback breathes instead of being flat.
- 🎶 The AI generates a melodic cell on top of the accompaniment pattern; it is transposed modally over each chord.
- 🏷️ Chord patterns produced by the AI are saved under a name: editable and reusable from the styles picker.
- ↔️ The timeline scrolls continuously during playback, cursor kept centred.
- 🔊 A clear message when no SoundFont is found, at startup as well as on playback.
- 🎛️ Project templates: from a file, with AI, or added to the folder — with deletion.
- 🥁 Catalogue of drum patterns (Standard, Africa, Australia) + your saved, reusable patterns.
- 🔑 Several API keys per provider, chosen by name in the compose screens.
- 🎼 Structured AI templates (intro/theme/development/outro), expanded to the desired length.
- 🎨 Dark & teal interface, movable dialogs, richer editors.
