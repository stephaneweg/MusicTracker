---
name: custom-file-dialog
description: "On-brand FileBrowserDialog replacing native Win32 open/save dialogs (API-compatible), for theme consistency"
metadata: 
  node_type: memory
  type: project
  originSessionId: 5fb908b2-3cfc-4ea2-8f9e-423d71503dd6
---

Feature (2026-07-21) : **`Dialogs/FileBrowserDialog`** — remplaçant maison des dialogues natifs Windows d'ouverture/sauvegarde, pour cohérence avec le thème dark/teal ([[ui-redesign-ntrack]]). Choix assumé de l'utilisateur ("tout maison") MALGRÉ l'avertissement que le natif est plus robuste (perte de l'intégration shell : accès rapide, OneDrive, réseau, recherche, miniatures).

**API calquée sur `Microsoft.Win32.FileDialog`** pour migration sans friction : props `Filter`, `DefaultExt`, `FileName`, `InitialDirectory`, `Title` + `SaveMode` (bool) ; `ShowDialog()` renvoie `bool?`, `FileName` = chemin choisi. Remplacement = juste `new Microsoft.Win32.SaveFileDialog {…}` → `new Dialogs.FileBrowserDialog { SaveMode = true, Owner = …, …}`.

Contenu : volet latéral (Récents via `RecentFiles` + Bureau/Documents/Musique + lecteurs), barre de chemin éditable + Précédent/Parent, liste dossiers-puis-fichiers (icône/date/taille) filtrée, parse du format filtre Win32 (`"desc (*.x)|*.x|…"` split sur `|` puis `;`, wildcard→Regex). Barre de titre déplaçable via [[dialogs-draggable]] `ctl:WindowDrag.Enable`.

Gotchas rencontrés : binding WPF ignore les CHAMPS (Name/Icon/Detail passés en propriétés) ; respecter un `Title` fourni par l'appelant (ne pas l'écraser dans Loaded).

Sites migrés (8) : MainWindow (open/save projet), TimelineScreen (export WAV/MP3, MIDI, mscx, import), RiffGridControl (import riff), WaveExporter. **NON migré** : `NewModelDialog` = sélecteur de DOSSIER WinForms (mode folder-pick pas encore construit) + son OpenFileDialog. Pas encore ear/eye-testé en live (dialogue codé main).
