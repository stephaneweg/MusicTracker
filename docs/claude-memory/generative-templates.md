---
name: generative-templates
description: "Refonte des Modèles de projet en système GÉNÉRATIF : banques de matière par section, l'app pioche (seed+Régénérer) et transpose modalement les riffs"
metadata:
  type: project
  originSessionId: 5fb908b2-3cfc-4ea2-8f9e-423d71503dd6
  modified: 2026-07-23T18:33:43.056Z
---

**2026-07-23** — Le système de template (`TemplateSpec`, cartes « Modèles de projet » de l'accueil) a été refondu en **GÉNÉRATIF**, REMPLAÇANT l'ancien format (degré+rythme). **Pas de rétro-compatibilité** ; les anciens .json (bin/Debug/Data/templates générés par l'IA) sont morts → à regénérer. Concerne UNIQUEMENT les modèles, pas « Composer avec l'IA » ([[mistral-ai-arrangement]] inchangé).

**Principe** : un modèle = des **BANQUES de matière par section** (plusieurs progressions d'accords, articulations, cellules mélodiques, phrases de riff par piste, grooves batterie). À l'ouverture, l'app **PIOCHE** une option par banque (aléatoire, seed affiché dans la barre d'outils + bouton « 🎲 Régénérer »). Sections de **même nom = même pioche** (forme cohérente : refrain identique à chaque retour) — cache de pioche par nom. Seed en RAM (pas sauvé au projet ; régénérer = nouveau seed → reconstruit).

**Schéma** (`Engine/Timeline/TemplateSpec.cs`, remplace l'ancien) : `measure{num,denom,bpm,count}` · `tonality{note 0-11, mode 0-8}` (les 9 modes 7-notes de `MusicalMode` ; PAS pentatonique/exotique car la machinerie suppose `%7`) · `tracks[{name,program}]` (l'ORDRE compte, riffs par index) · `sections[{name, chordProgressions[][TplChord], chordArticulations[], chordMelodicCells[], riffs[{trackNum,phrases[]}], drums[]}]`. Tableaux plats de triplets : articulation `[voix,début,durée,...]`, cellule `[degré,début,durée,...]` (diatonique 1-14), phrase riff `[note,début,durée,...]` (note=**demi-tons chromatiques au-dessus de la tonique**, sur 4 mesures de degré 1 ; durée négative=silence ; polyphonie), groove `[noteGM,début,durée,...]`. `slicesPerBeat` par motif.

**Accord = 3 axes** : `mode`(0maj/1min/2dim) × `quality`(0triade/1·7e/2·9e/3add9) × `color`(-1/0sus2/1sus4) → mappés vers l'index `QualityNames` existant via `TemplateChords.QualityIndex` (+ `PatternGenerator.IndexOfQuality`, tolérant ♭♯). **maj+7e → Maj7** (dom7 pas exprimable sur ces axes ; TODO : valeur mode dédiée).

**Moteur** (`Engine/Timeline/TemplateComposer.cs`, pur/statique) : `Pick` seedé ; parseurs triplets ; **`RenderRiff`** = transposition MODALE par accord (décale tonique→fondamentale de l'accord) + **SNAP** accord (sur temps) / gamme (hors temps) + voice-leading (repli d'octave ±7 demi-tons). Réutilise `AiTranslate.{RootPc,ScalePcs,SnapMidiToPcs}` + `PatternGenerator.ChordNotes`. VALIDÉ (harness réflexion sur l'exe) : toutes les notes tombent dans la gamme et suivent les accords.

**Assemblage** : `TimelineScreen.BuildFromTemplate` (remplace `LoadTemplateSpec`) construit la forme (sections répétées jusqu'à `count` mesures), pose accords via `AddAiChord` (+ articulation + cellule, cf. [[chord-melodic-cell]]), grooves via `DrumPatternModule` custom (`SetCustomNotes`), riffs via `Riff`+`PlayRiffModule`. Champs `templateSpec`/`templateMeasures`/`TemplateSeed`, chip `tplBar` (XAML, visible si `FromTemplate`), `btnRegen_Click`→`RegenerateFromTemplate`. Nouveau prompt `TemplatePrompt.Build` (schéma génératif complet). Exemple : `Data/templates/ghibli_pastoral.json`.

**Reste à faire / affiner** : ear-test dans l'app ; registre des riffs (peut monter un peu haut) et voice-leading à peaufiner ; le `measures` du dialogue prime sur `measure.count` ; `TimelineScreen.DrumStyleIndex` devenu inutilisé. Sonde de test : `scratchpad/tpl_test2.ps1` (réflexion, Func<double,ChordSlot> via scriptblock cast).
