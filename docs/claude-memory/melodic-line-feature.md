---
name: ""
metadata: 
  node_type: memory
  originSessionId: f952e927-6dc4-4278-9d24-2ab81936122d
---

"Ligne mélodique" feature (built this session, verified by ear). Distinct from the chord's [[chord-melodic-cell]].

- Insert via menu "Insérer ▸ Ligne mélodique (rythme)" → `btnInsertMelodicLine_Click`/`InsertMelodicLine()` — finds/creates track "Ligne (rythme)" (flute, Instrument=73), appends a `MelodicLineModule` copying the previous item's rhythm. (Do NOT confuse with the OLDER `AddMelodicLine()` = "Piste ▸ Ajouter une ligne mélodique" via ArrangementEngine.BuildExtraVoice.)
- You draw ONLY the rhythm (≤3 voices, one grid row per voice, same staff). `MelodicLineEngine.GenerateLine` picks pitches from the chord in effect (`Harmony.ChordAt`: arrangement grid in structure mode, else the chord track's PatternGeneratorModule at beat T): strong beat → chord tone (all voices), weak → chord or passing (scale) tone; voice 0 leads; register bands {74,66,58}; directional contour.
- Editor `BuildMelodicLineEditor`/`RefreshMelodicLineGrid` (TimelineScreen.xaml.cs): Voix combo, Nombre de temps, `RhythmGridControl` (noteList).
- Save/reapply "like chords": `project.UserMelodicLines` (List<UserChordStyle>). Grid wired with styleNames (the "Copier" combo), `onSaveStyle` (prompt → save + set `ml.LineName` → rebuild editor), `onApplyToSection` → `PropagateMelodicLine(name, src)` copies Notes/Slices/SlicesPerQuarter/BeatsPerBar/VoiceCount to every same-name line.
- **Préserver** checkbox (2026-07-13): on BOTH the line (`MelodicLineModule.Preserve`) and the chord cell (`PatternGeneratorModule.MelodicPreserve`, VM `MelodicPreserve` + checkbox in ChordEditorControl.xaml). `PropagateMelodicLine` skips `Preserve`; `PropagateMelodic` (cell) skips `MelodicPreserve`.
- Rendered in playback (TimelinePlayer) + score (ScoreModel.RiffForModule) as an extra generated riff.
- Left-column picker (2026-07-13): combo of saved lines + "Appliquer" button → `ApplyExistingLine(ml, u)` loads a UserMelodicLines entry into the module (exact note-list, spq, length, name, inferred VoiceCount). Timeline box now has a BLUE thumbnail (case MelodicLineModule in MakeLeafBox): the engine-generated line when chords exist, else the raw rhythm skeleton.
- GOTCHA fixed: seeding a saved rhythm via the grid's "Copier" combo used `RiffNotes.FromSlices`, which MERGES contiguous same-voice notes into one long note ("fills everything, ignores durations"). Fix: `RhythmGridControl.seedNotesFunc` (+ `LoadNotes`) — in note mode the seed reloads the style's exact `Notes` list instead of FromSlices. Same latent bug would hit any note-mode grid seeded from Slices without notes.
- 2026-07-13 UI: left panel now has an ALWAYS-shown "Motif" dropdown = saved motifs + "Personnalisé…" (last); picking a saved motif loads it (ApplyExistingLine), "Personnalisé…" clears LineName. Left "Appliquer le motif à ceux du même nom" button (enabled only for a named motif) → PropagateMelodicLine. The grid's own "Copier" combo + "Appliquer à la section" button were REMOVED (pass empty styleNames + null onApplyToSection); grid keeps only "Enregistrer".
- PERSISTENCE BUG fixed: the .sq LOAD path (TimelineScreen ~613) copied doc.Project field-by-field and never restored `project.UserMelodicLines` NOR `project.PickupBeats` → saved motif library + anacrusis were lost on reload. Both now restored. (Serialization itself was fine: UserMelodicLines is a property, MelodicLineModule is a registered JsonDerivedType.)
