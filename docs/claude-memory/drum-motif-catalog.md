---
name: drum-motif-catalog
description: JSON catalogue of named drum motifs by category (seeded from user pieces) + DrumCatalog loader + template integration
metadata: 
  node_type: memory
  type: project
  originSessionId: 5fb908b2-3cfc-4ea2-8f9e-423d71503dd6
---

Feature (2026-07-21) : **catalogue de motifs batterie** par catégorie+nom, en JSON, réutilisable.

- **`Data/drums/catalog.json`** (bundlé, `<None>`+CopyToOutputDirectory dans csproj) — schéma `{ categories:[ { name, motifs:[ { name, spq, beats, notes:[[lane,start,length],…] } ] } ] }`. Les `notes` sont en index de LANE (0-46, cf `DrumPattern.LaneNames`), pas des clés GM. Seedé en extrayant les `DrumKit` custom de `~/Desktop/africa.sq` + `magie_d_australie.sq` (script PowerShell : ConvertFrom-Json → Tracks Type=1 → Module `$type`="DrumKit" → CustomNotes). Catégories : **Afrique** (5 : congas/maracas/afrobeat), **Australie** (3 : tribal toms/clap, pulsation).
- **`Engine/Flow/DrumCatalog`** — loader singleton (`Instance`, `FindPath("Catégorie/Nom")` insensible à la casse), `Motif.ToNotes()` → `List<RiffNote>`, `LengthSlices`. JSON lu via `AppPaths.Local("Data/drums/catalog.json")`, `PropertyNameCaseInsensitive`.
- **Intégration templates** ([[home-redesign-templates]]) : `ProjectTemplates.Info.DrumCatalog = "Catégorie/Nom"` (override de `DrumStyle`) ; `TimelineScreen.GenerateTemplateDrums` pose alors `DrumPatternModule` avec `SetCustomNotes(motif)` + `Style=CustomStyle` + `Repeats = chunkBeats/motif.beats` (tuilé sur les segments de 4 mes.). 2 nouveaux modèles : **Afro** (Afrique/Afrobeat 2 mes.) + **Aborigène** (Australie/Tribal toms) → 11 modèles.

Format DrumKit .sq rappel : `$type`="DrumKit", Style=16=CustomStyle, CustomNotes=[{Note=lane,Start,Length,…}], CustomSlicesPerQuarter=4.

**Éditeur batterie refondu (2026-07-22)** : le catalogue est la SOURCE UNIQUE. `DrumCatalog.Load` préfixe une catégorie synthétique **"Standard"** (les 16 styles intégrés `DrumPattern.StyleNames`, `Motif.Builtin`=index, procédural) devant les catégories JSON. `Motif.Builtin` (int, -1=note-list, >=0=réf style intégré). L'éditeur (`BuildDrumEditor`) : combos **Catégorie + Motif** (au lieu du combo Style) + bouton **« Personnaliser »**. `ApplyDrumCatalog(dp,cat,motif)` : Builtin→`dp.Style`=index (CustomNotes null) ; note-list→`SetCustomNotes` ; "Personnalisé"→grille éditable. `CustomizeDrum(dp)` copie le motif courant (built-in via `LaneNotesForStyle`, ou note-list) en CustomNotes + catégorie="Personnalisé" (remplace "Copier depuis"). Module retient `CatCategory`/`CatMotif` (FlowModule, persistés). `RhythmGridControl` : "Copier :" (seedSep/lblSeed/cboSeedStyle/btnSeed) masqué si `seedStyleNames` null/vide — l'éditeur batterie passe null ; les éditeurs d'accords gardent le seed. Grille visible seulement si `DrumIsCustom(dp)`.

**Motifs perso nommés + répétitions adaptées (2026-07-22)** : `TimelineProject.UserDrumStyles` (List<UserChordStyle>, Notes=note-list de lanes, sérialisé/restauré dans LoadSqFile). Éditeur batterie en mode Personnalisé : bouton « Enregistrer ce style » de la grille (`onSaveStyle` + `PromptText`) → sauve dans le projet, apparaît dans la catégorie Personnalisé (FillMotifs liste "Personnalisé" + UserDrumStyles.Name). Sélectionner un motif sauvé = charge une COPIE (ConvertAll). CHANGEMENT DE MOTIF (ApplyDrumCatalog, catalogue OU sauvé) : `dp.BeatsPerBar = motif.Beats` + `dp.Repeats = round(ancien BeatsPerBar×Repeats / nouveaux Beats)` → durée totale préservée. Combo change reconstruit l'éditeur (`rebuild = BuildDrumEditor + Render`) pour rafraîchir les champs Temps/Répétitions. `RefreshDrumGrid` prend maintenant `item` (pour rebuild). Vrai fix « sons coupés » = piste batterie template avait Instrument=0 (piano) → `InstrumentCatalog.DrumIndex` (voir [[home-redesign-templates]]).

**Fix batterie sons coupés — FAUSSE PISTE annulée (2026-07-22)** : le playback fait `DoRelease` dès note OFF ([[procedural-instrument-synthesis]] SequencerWaveProvider), or Generate ne posait chaque frappe que 1 slice → coupé. `DrumPattern.HoldHits(outp)` prolonge chaque frappe jusqu'à juste avant la suivante sur la même row (1 slice de gap pour re-trigger) ; appliqué avant les 2 returns de Generate. One-shot tenu = pas de loop. Pas encore ear-testé.
