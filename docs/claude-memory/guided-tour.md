---
name: guided-tour
description: "Mode tutoriel (#11) — visite guidée interactive coach-marks ; où sont les étapes, comment l'étendre"
metadata: 
  node_type: memory
  type: project
  originSessionId: 40a83290-5f51-4e89-a486-52209c92af2f
  modified: 2026-07-27T22:18:33.920Z
---

Mode tutoriel (issue #11, commit 7aed80e) = **visite guidée interactive** (coach-marks), pas un projet démo.

- **`Controls/GuidedTour.cs`** : moteur générique. `GuidedTour.Run(Window owner, IList<TourStep> steps)`. Overlay = fenêtre séparée transparente `Topmost` calée sur `owner` (WindowStyle=None → Left/Top = client area). Assombrit tout via un `Path` rempli en noir semi-transparent avec un trou arrondi (GeometryGroup FillRule=EvenOdd : full-rect + rect cible) = spotlight. Carte thémée (titre/texte/compteur + Passer/Précédent/Suivant) placée sous la cible (sinon au-dessus / centrée). Clavier ← → Échap. Suit LocationChanged/SizeChanged de l'owner. `TourStep(Func<FrameworkElement> target, title, text)` — target lazy (null = carte centrée sans spotlight).
- **Étapes** : `TimelineScreen.StartTutorial()` (public) construit 9 `TourStep` ciblant les VRAIS contrôles nommés de la barre d'outils : `menuTracks`, `menuInsert`, `tglKeyMenu`, `tglMeterMenu`, `btnPlay`, `btnUndo`, `btnImport` (+ accueil/fin sans cible). **Étendre** = ajouter un TourStep ici. Les sous-items de menu ne sont pas ciblables (menu fermé) → on cible l'en-tête du menu.
- **Déclencheurs** : 1er lancement (MainWindow.Window_Loaded, après SoundFont+update : si `!UserData.TutorialShown` → popup Oui/Plus tard, met le flag à true+Save pour ne plus redemander) ET tuile « Tutoriel » sur l'accueil (`HomeScreen.TutorialRequested` → `MainWindow.StartTutorialInNewTab` : ouvre une TimelineScreen puis `editor.StartTutorial()` en DispatcherPriority.Loaded).
- **Projet démo** (commit 89ed7d0) : `BuildTutorialDemo()` peuple l'éditeur (frais, nouvel onglet) d'un accord (piste Accords) + un riff vide (piste instrument) + un module batterie (nouvelle piste) = de quoi ouvrir chaque éditeur. `TourStep.Before` (Action optionnelle exécutée au Show, + owner.UpdateLayout()) sélectionne le bloc voulu → l'éditeur du bas s'affiche → on spotlight `editorHost`. Étapes éditeurs accords/riff/batterie ajoutées.
- **Dialogue 1er lancement** : `Dialogs/ConfirmDialog` (code-only, thémé comme les autres — Border CommonBackground/OutlineColorBrush, styles okButton/cancelButton) au lieu de MessageBox. `ConfirmDialog.Ask(owner, title, msg, okText, cancelText)`.
- **Loc** : clés `Tour*` + `UndoCtrlZ`/`RedoCtrlYCtrlShiftZ` traduites dans **les 7 langues** (fr, en, de, it, es, nl, pt), pas seulement fr/en. Voir [[localization]].
- Flag persistant : `UserData.TutorialShown` (userdata.json roaming).
