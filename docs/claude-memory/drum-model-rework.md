---
name: drum-model-rework
description: "Drums = full GM percussion, riff-like note editor, DrumPatternModule holds a note-list phrase; AI emits drum modules"
metadata: 
  node_type: memory
  type: project
  originSessionId: 5fb908b2-3cfc-4ea2-8f9e-423d71503dd6
---

Refonte batterie 2026-07-20 (3 demandes user, toutes faites, compile EXIT=0). Voir aussi [[ui-redesign-ntrack]].

1. **Toutes les percussions GM** : `DrumPattern.LaneNames/LaneKeys` étendus de 12 → **47 lanes** (35..81 : bongos, congas, timbales, agogo, cabasa, maracas, guiro, claves, wood block, cuica, triangle, tambourin, cowbell, whistle…). Les **12 premières lanes gardent leurs index** (les style-writers `RenderStyle` + grilles custom sauvegardées en dépendent). `LaneForKey` = maintenant dict `KeyToLane` (quasi-bijection) + nearest fallback. Audio déjà OK : une note batterie joue sa touche GM sur le patch SoundFont bank-128.
2. **Éditeur = riff** : l'éditeur batterie (`RefreshDrumGrid` dans TimelineScreen) passe en **mode note** (`noteList:true`, note+durée, dessin/gomme) au lieu du mode cellule. `RhythmGridControl` colore notes+fond par famille via `rowColor`. Couleurs = `Controls/DrumColors.cs` (8 familles, variation de teinte intra-famille).
3. **`DrumPatternModule` porte une phrase** : nouveau `CustomNotes` (Note = lane index) + `SetCustomNotes`. `DrumPattern.Generate` : si `CustomNotes` présent → **chemin phrase** prioritaire, déclenche chaque note UNE fois à son `Start` (one-shot ; la durée sert à l'édition seulement — cohérent avec « flag one-shot sur l'instrument »). Seed non-fusionné = `DrumPattern.LaneNotesForStyle` (chaque coup = note length-1, sinon les hats 16th fusionnent). Preview de l'éditeur (`mk`) lit `rg.CurrentNotes()` (pas la grille OR-mergée).
4. **IA** : `ApplyAiArrangement` pose un `DrumPatternModule` (phrase, `Style=CustomStyle`, `Repeats=1`, `BeatsPerBar=totalBeats`, spq=4 pour un éditeur léger) sur la piste Drum — plus de `PlayRiffModule`+Riff. Vignette drum = `RiffThumbnail.GetDrums` (mini beat-grid épais coloré par famille).

5. **« Convertir en batterie »** (menu contextuel d'un `PlayRiffModule`, `ShowItemContextMenu`→`ConvertRiffToDrums`) : remplace le riff par un `DrumPatternModule` (phrase) reprenant start/length tels quels, hauteur lue comme touche GM (`LaneForKey(Note+12)` → round-trip exact pour un riff de contenu batterie). Même position/longueur. Avertit si la piste n'est pas de type Drum (sinon joue avec l'instrument de la piste).

6. **Repeats bouclé + période** (2026-07-20 suite feedback) : le chemin phrase de `Generate` **tuile l'unité `Repeats` fois** (unit = `CustomSlices.Length`, cohérent avec `ModuleDuration`) — avant il ignorait Repeats (thumbnail court + audio 1×). `CompressPeriodic(notes,total,spq)` détecte une période en temps entiers → la conversion riff→drum ne stocke que l'unité utile + règle `Repeats`. Bug corrigé au passage : `RiffThumbnail.GetDrums` triait par `LaneRank[12]` → IndexOutOfRange dès une percu étendue ; remplacé par tri sur `KeyForLane` décroissant.

7. **Perf édition** (2026-07-20) : (a) l'éditeur batterie ne fait plus `Render()` complet à chaque coup — `GridChanged` pose `riffDirty`, la vignette de la box se rafraîchit à la sortie (`BuildDrumEditor(track,item,dp)` pose la baseline `riffEditItem`; `RefreshEditedRiffBox` gère `DrumPatternModule` via `GetDrums`). (b) `RhythmGridControl` refait sur une `GridSurface` (2 `DrawingVisual` retenus, comme `RiffGridControl`) : fond OFF tuilé dessiné une fois (`BuildOffTile`), ON cellules/notes dans un visual retenu redessiné seulement au changement (`RedrawContent`) — plus de `Rectangle` par cellule (47 lanes). Hit-test = calcul de coordonnées.

8. **Motif IA = 1 motif court bouclé** (2026-07-20) : l'IA écrivait un groove court (2 mes.) mais déclarait une section longue (4/8) → reste vide, pas de répétition. Fix double : (a) PROMPT — `AiRiff.motifBars` + `repeats` ajoutés au contrat ; l'IA écrit UN motif (`motifBars` mesures, start relatif au motif) + `repeats` (motifBars×repeats=measures). (b) FALLBACK dans `ApplyAiArrangement` si absents : détecte la longueur réelle (arrondie mesures entières) + `CompressPeriodic`. Le module stocke l'UNITÉ + `Repeats` (jamais de duplication). Info box drum affiche « Style · N temps ×R » quand Repeats>1.

Heads-up / limites : en mode phrase le champ **« Répétitions »** de l'éditeur est inerte (le loop 1-mesure×N du custom cell-mode disparaît — voulu, édition riff-like). Vieux projets : `CustomSlices` seul se convertit en notes à l'ouverture (`FromSlices`, coups adjacents même-lane fusionnés — migration mineure).
