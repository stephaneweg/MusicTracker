---
name: project-state-2026-11
description: Point de reprise du projet au 2026-11-08 — dernière release v2.0.1.0, arbre propre, où reprendre
metadata:
  type: project
---

État du projet MusicTracker / Koton Studio au **2026-11-08** (point de sauvegarde demandé par l'user pour reprendre plus tard) :

- **Version publiée : v2.0.1.0** (installeur `KotonStudioSetup-2.0.1.0.exe` + portable `KotonStudioPortable-2.0.1.0.zip` dans `releases/`, `latest.json` à jour, site kotonstudio.com déployé). SHA-256 de l'installeur : `C7BD3383D83CD52FAEFD724B7FAAB3DB898032420716E17D212600C2E7CF1189`. Voir [[release-update-infra]].
- **Dépôts** : `main` du repo principal et du repo releases propres et poussés (commits 4e98d5a feature + 06929f8 bump ; releases 2537601).
- **Dernières features livrées** : `Controls/ChordKeyboard.cs` intégré dans `ChordEditorControl` (clavier d'accords) ; nouveau plugin générateur `Plugins/Generators/KotonPluginArabesque/` (ArabesqueCore/Generator/Editor) ajouté à la solution ; retouches HarmonySuggest, ChordArticulation, TimelineHelper, AiArrangementPlacer ; 7 clés de loc ajoutées (voir [[localization]]).
- **Historique récent** : une 2.0.0.3 « de vérification » a été publiée puis retirée du repo releases (manifeste ramené à 2.0.0.2) avant la 2.0.1.0.
- **Chantiers ouverts à reprendre** (voir leurs fiches) : [[composerv3-orchestrateur-rewrite]] (phase 1 faite), [[theme-library-seed-engine]] (phase D), [[melody-chord-degree-context]] (résolveur Viterbi), [[structure-melodic-line-refactor]] (planifié), [[meltysynth-audio-engine]] (branchements 2-4), [[ai-per-element-generation]] (ligne mélodique TODO), [[mistral-ai-arrangement]] (pas testé à l'oreille), [[feature-roadmap-2026-07]], backlog [[kanban-backlog]].
- ⚠️ Le `.exe` n'est pas signé : soumettre le faux positif (Google Safe Browsing + Microsoft WDSI) à chaque release.

**How to apply:** à la reprise, partir de cette fiche + MEMORY.md, vérifier `git status`/`git log` (l'état a pu bouger) et choisir un chantier dans la liste ci-dessus ou dans KANBAN.md.
