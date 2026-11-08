---
name: undo-redo
description: "Undo/redo timeline (snapshots + coalescing/neutralization) — design, couverture, comment l'étendre"
metadata: 
  node_type: memory
  type: project
  originSessionId: 40a83290-5f51-4e89-a486-52209c92af2f
  modified: 2026-07-27T21:46:57.075Z
---

Undo/redo de la timeline (issue #9, commit c194d9c). **Approche SNAPSHOTS** : chaque entrée = état projet sérialisé (même JSON `.sq` que Save, via `SnapshotState()`). Restaurer = désérialiser → `ApplyDocument` → `Render` (`RestoreState`, sous garde `restoringUndo`). Moteur = `Engine/Timeline/UndoManager.cs` (2 piles, plafond 50).

**Règles de consolidation (demandées par l'utilisateur) :**
- **Coalescing** : pushes consécutifs de même `opKey` fusionnés (un drag = plusieurs `move:X` → 1 entrée ; garde le 1er pré-état). Coalescables = `move:` / `edit:` / `vol:`.
- **Neutralisation** : `delete:X` juste après `insert:X` (même objet) → retire l'insert, ne pousse rien (net = zéro).
- `opKey` = `"<op>:" + Id(obj)` où `Id` = `RuntimeHelpers.GetHashCode` (identité live, suffit pour coalesce/neutralize dans une session).

**Deux mécanismes dans TimelineScreen :**
1. **Structural** : `PushUndo(opKey)` (capture le pré-état AVANT la mutation) ou `BeginUndo()`/`CommitUndo(pre, "insert:"+Id(newItem))` pour les inserts (id connu après). Instrumenté sur : AppendModule/AppendChord/AddTrack/btnAddRiff (insert), DeleteItem (delete), MoveInList (move), ConvertRiffToDrums/ConvertMelodicLineToRiff (convert), SuggestNextAfter/ChainProgression (insert accords), btnTranspose (transpose), ApplyComposeResult (generate — couvre structure+IA).
2. **Édition de module** : `BeginEditSessionFor(item)` dans `OpenModuleEditor` capture un pré-état "pending" ; `FlushPending()` le pousse SEULEMENT si l'état a changé (diff sérialisé) → 1 entrée par session d'édition (accords/riff/batterie/ligne) sans toucher aux sous-contrôles. FlushPending appelé par tout PushUndo/BeginEditSession/DoUndo/DoRedo.

**Clavier** : `TimelineKeyDown` (PreviewKeyDown sur le UserControl) Ctrl+Z / Ctrl+Y / Ctrl+Maj+Z ; ignoré si focus dans un TextBox (undo natif du champ). Boutons ↶ ↷ dans la barre de transport (clés loc `UndoCtrlZ`/`RedoCtrlYCtrlShiftZ`).

**PAS encore couvert (extension = 1 ligne `PushUndo(...)` au bon endroit)** : automation de volume, changement de mesure/tonalité/tempo via la barre d'outils, opérations sur les Repeat, copier/coller de modules, edits partition. Après restore, la sélection est perdue (`ApplyDocument` remet selectedItem=null) — améliorable. Perf : `SnapshotState()` sérialise tout le projet à chaque sélection (BeginEditSessionFor) — OK pour tailles normales, à rendre lazy si gros projets.
