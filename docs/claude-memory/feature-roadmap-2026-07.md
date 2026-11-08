---
name: feature-roadmap-2026-07
description: "Roadmap de features décidée avec l'user (nuit du 2026-07-26) : ce qui est fait, retenu, abandonné, et l'approche Undo"
metadata: 
  node_type: memory
  type: project
  originSessionId: 7c6b1e69-f0bc-40d7-9c40-13f39d234091
  modified: 2026-07-27T22:02:36.658Z
---

Nuit du 2026-07-26 : après une session d'idéation (proposition de features), l'user a trié. État :

**FAIT (implémenté + build vert, NON committé — l'user relit puis committe sur main façon [[fixes-directly-on-main]]) :**
- **Insertion au curseur** : les actions « Ajouter riff / xxx » n'ajoutent plus en FIN de ligne mais **juste après le bloc situé au curseur** (`startBeat`) sur la piste sélectionnée, en **remplissant l'écart** avant le bloc suivant et en **tronquant** le nouveau module si l'écart est trop petit (sinon décalage si écart nul). Nouveau `TimelineHelper.PlaceAtCursor(track,item,len,cursorBeat,resolveRiff)` + `TruncateModule`/`ClampBarsRepeats` (troncature par type : riff→LengthSlices, Pattern/Drum→BeatsPerBar×Repeats, Cadence→drop chords, Melodic→BeatsPerBar). Branché dans `btnAddRiff_Click` + `AppendModule` (drum). Chords (piste dédiée qui chaîne) et ligne mélodique (piste dédiée) NON changés — à étendre si voulu.
- **Copier / coller de modules** : menu contextuel d'un bloc → « 📋 Copier » / « 📌 Coller ici ». Presse-papier statique (`clipModule` cloné via `JsonSerializer<FlowModule>` polymorphe [JsonDerivedType] + `JsonOpts` IncludeFields, `clipRiff` = riff cloné). Coller = clone indépendant (PlayRiff obtient un NOUVEAU riff, `r.Id=Guid.NewGuid()` car `Riff.Clone()` préserve l'Id) placé au curseur via PlaceAtCursor. Clés loc `Copier`/`CollerIci` ajoutées à lang.fr.json (fallback→fr pour les 6 autres langues).
- **Basse aiguë des TEMPLATES corrigée** : cause = format génératif, notes de riff = offsets CHROMATIQUES relatifs à la tonique (pas MIDI), et `TemplateComposer.RenderRiff` place tout autour de MIDI 60 via son param `register` qui était **codé à 0** pour toutes les pistes → la basse jouait au niveau de la mélodie. Fix DÉTERMINISTE (pas le prompt, qui ne répare pas les templates déjà générés + risque de cumul) : `TemplateProjectBuilder.RegisterForProgram(gm)` (basses 32-39 & contrebasse 43 & tuba 58 → -24 ; violoncelle 42/trombone 57/basson 70/sax bary 67 → -12 ; sinon 0), passé à RenderRiff ET au fallback ligne-mélodique (`RegisterShift = line.Register!=0 ? line.Register : RegisterForProgram`). Prompt template INCHANGÉ.
- **Transport ▶/⏸/⏹** (`TimelineScreen`) : Play est un TOGGLE lecture/pause (icône ⏸ en lecture ; pause = gèle le curseur, ▶ reprend là) ; Stop = VRAI stop (curseur→0). `StopPlayback` scindé en `PausePlayback` (gel, ancien comportement) + `StopPlayback` (→0) ; `StopAudio` (changement d'onglet) appelle PausePlayback (garde la position). Infobulles + clés loc `PlayOrPause`/`StopReturnToStart`.
- **« Composer avec l'IA » — case « corriger notes hors accord » RETIRÉE** ; correction TOUJOURS appliquée (`AiComposeDialog.FixNotes => true`, case + refs `chkFixNotes` supprimées ; `AppSettings.AiFixNotes` conservé mais inutilisé).
- **Kit de batterie PAR PISTE** : `TimelineTrack.DrumKit` (index kit, persisté) + combo de kit dans l'en-tête de piste batterie (remplace le label « kit auto », liste `InstrumentCatalog.DrumKitNames()`) ; APPLIQUÉ à la lecture — `TimelinePlayer.ApplyPrograms` envoie `ProcessMidiMessage(percChan, 0xC0, DrumKitProgram(DrumKit), 0)` (avant: `if(isDrum) continue` → toujours Standard). Kit = source unique par piste : le combo par-module `dp.Kit` retiré de l'éditeur, la preview suit `track.DrumKit`. Limite: toutes les pistes batterie partagent le canal 9 → kits différents simultanés = le dernier gagne (cas courant 1 piste = OK).
- **Boucle A-B** (sans coupure) : `TimelinePlayer` région `[StartBeat(A), LoopEndBeat(B)]` — `Loop`/`LoopEndBeat`/`ApplyLoop()` ; le rendu reboucle sur `loopEndSlice` via `NoteOffAll(false)`+retour `loopStartSlice` (seamless car render avant bookkeeping → A re-dispatch immédiat) ; `PlayheadBeat(consumed)`=curseur loop-aware (`consumed % loopSamples`). UI: poignée B orange (miroir de la bleue A) + toggle `btnLoop` (⟳, style TernaryToggle) ; B défaut = A+4 mesures ; live (toggle/déplacement B pendant lecture). ⚠️ FIX: B ne borne QUE si `Loop` actif (`RecomputeLoop`: `Loop && LoopEndBeat>A`), sinon décocher en cours laissait la lecture bloquée/tenue en B. Clés loc `LoopTheABRegion`/`GlisserPourDefinirLaFinDeBoucle`.
- **« 🔗 Fusionner avec le suivant »** (menu contextuel d'un riff, si le bloc suivant est aussi un riff) : `TimelineHelper.MergeWithNext` concatène les 2 riffs en 1 (notes du 2e décalées de longueur1+silence, voix/bend préservés via copie de struct, rescale si spq différents), supprime le 2e bloc. Sert à reconstituer un thème éclaté en 1 riff pour le « Varier avec l'IA » d'un coup.

**NOTE — « Varier le thème avec l'IA »** (`TimelineHelper.VaryThemeWithAi`) prend UNIQUEMENT le riff sélectionné (pas la ligne) → d'où l'intérêt de MergeWithNext. Variante « fusionner toute la ligne » proposée, pas encore demandée.

**VÉRIFIÉ (pas de bug) :** le chemin « Composer avec l'IA » n'applique PAS de voice-leading entre riffs — `AiArrangementPlacer.CorrectRiffHarmony` (= le FixNotes) ne fait que snapper les notes HORS accord/gamme vers la plus proche (±6 demi-tons), les notes justes restent intactes (user a confirmé de son côté).

**FAIT par l'user (2026-07-28) :**
- **Undo/Redo** — implémenté par l'user (`Engine/Timeline/UndoManager.cs`, câblé dans TimelineScreen ; Ctrl+Z / Ctrl+Y ou Ctrl+Maj+Z ; clés loc `UndoCtrlZ`/`RedoCtrlYCtrlShiftZ`). Approche SNAPSHOTS (cohérent avec [[undo-redo]] / issue #9), pas l'approche command-per-piste que j'avais proposée. NE PAS re-implémenter.
- **Visite guidée / tutoriel** ajoutée par l'user (clés `Tour*` dans les dicos de langue ; tuile « Tutoriel » sur l'accueil, étapes pistes/insert/tonalité/mesure/lecture/undo/import).

**RETENU (à faire, validé) :**
- **2.7 Swing / groove** : réglage swing (retard des contretemps) global/par piste ; débloque un jazz crédible (aujourd'hui straight). Feature moteur déjà notée « deferred » dans [[theme-library-seed-engine]].
- **2.8 Exports** : (a) export MuseScore .mscx SAUTE les pistes batterie (« pas de portée de perc ») → ajouter une portée batterie ; (b) PDF passe par l'impression système → ajouter une **sauvegarde PDF directe** ; (c) ajouter **MusicXML** (interop universelle).

**REFORMULÉ — 1.2** : ma proposition « armer une piste et enregistrer le violon directement dans la timeline (décompte+métronome) » n'a pas convaincu ; l'user veut plutôt **détecter le BPM JOUÉ et re-quantifier selon le BPM du morceau**. À cadrer (temps réel vs après-coup) avant de coder. Lié au use-case phare [[violin-audio-capture]].

**ABANDONNÉ :**
- **2.6** (rebrancher le compositeur orphelin `Engine/Timeline/Composer.cs` = styles Mozart/jazz/pop/harpe/Hisaishi + formes) → NON, l'user va **supprimer** cette fonction (donc ne pas investir dedans ; candidat à suppression comme dead code).

**Contexte confirmé cette nuit** : AUCUN undo/redo n'existe (vérifié). Les modules d'une piste sont séquentiels (position cumulée = Σ SilenceBefore+ItemLength) ; `ModuleDuration.Beats` = longueur en temps ; curseur de lecture/départ = champ `startBeat` (TimelineScreen). Le compositeur orphelin + `RiffEditorWindow`/`NumberDialog` sont du dead code (agents d'explo).
