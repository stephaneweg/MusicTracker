---
name: ai-per-element-generation
description: "Per-module \"🤖 Générer (IA)…\" — groove/riff/melodic-line generators reusing a shared provider helper + dialog"
metadata: 
  node_type: memory
  type: project
  originSessionId: 5fb908b2-3cfc-4ea2-8f9e-423d71503dd6
  modified: 2026-07-26T00:50:36.658Z
---

Feature (2026-07-21, EN COURS) : un bouton **« 🤖 Générer (IA)… »** par éditeur de module, qui génère juste CET élément via IA et l'applique. Distinct de la compo complète ([[mistral-ai-arrangement]]).

Briques partagées :
- **`Engine/AI/AiProviders.cs`** — registre fournisseurs (Mistral/Gemini/Groq/DeepSeek/Claude/Grok/Qwen), modèles suggérés, clés/modèles persistés dans `AppSettings`, `CompleteJsonAsync(provider,key,model,sys,usr,thinking)`. (TODO confirmé : refactorer `AiComposeDialog` pour passer dessus — pas encore fait.)
- **`Dialogs/AiElementDialog`** — dialogue réutilisable : fournisseur repliable (fermé si clé présente) + Intention + Générer(accent) + **Copier le prompt / Coller la réponse** + JSON repliable. Ctor `(title, context, Func<string,string[]> buildPrompt)` (intention→[sys,usr]) ; expose `ResultJson` ; auto-applique (DialogResult=true) → le CALLER parse/applique. `AiArrangement.CleanJson()` exposé pour extraire l'objet.

Fait :
- **Batterie** : bouton dans l'éditeur batterie → prompt (tonalité+métrique+intention+percus GM) → `{motifBars,repeats,notes}` → `ApplyAiDrum` (LaneForKey + CompressPeriodic → `dp.SetCustomNotes`+Repeats).
- **Riff** : bouton dans l'éditeur riff → `ChordsUnder(startBeat,len,barTemps)` lit les accords de la piste Accords sous le riff. Si accords → prompt les transmet, IA renvoie `{notes}` (mélodie). Si AUCUN accord → IA renvoie `{notes, chords, articulation}` ; `ApplyAiRiff` pose la mélodie (`BuildRiffNotes`) ET les accords via `AddAiChord` (aligné au riff, `Revoice`). Résumés tonalité/mesure lus depuis `txtKeySummary`/`txtMeterSummary`.

TODO : **ligne mélodique** per-module (même principe : intention → rythme+contour/ancrage → `ml.SetNotes`) ; refactor AiComposeDialog→AiProviders.

**Ajouter une PISTE entière avec l'IA (v1.3.0.0, menu Piste)** — distinct du per-module : « 🎶 Ajouter un instrument (IA)… » (case « Mélodie complète » = riffs notes explicites, sinon ligne mélodique rythme-seul) + « 🥁 Ajouter une batterie (IA)… » (groove). Le prompt transmet TOUT le morceau : `AiArrangement.BuildFullPieceContext(project, out measures)` dumpe chaque piste aplatie via `TimelineImporter.FlattenForExport` (pitch@temps ×durée, répétitions de module expandées, PAS de ResolveLoops → projet non muté) + accords (`ChordsUnder`) + tonalité/mesure/tempo. `BuildAddTrackPrompt`/`BuildAddDrumsPrompt` (l'IA choisit l'instrument GM + nomme la piste dans la langue courante) → `ParseTrack` (comme Parse mais SANS exiger d'accords) → `AiArrangementPlacer.AddTrack` = nouveau mode **overlay** de `Place` (append:false, overlay:true → keepExisting sans clear, baseBeats=0, tracks TOUJOURS neufs pour que le curseur parte à 0). Réutilise `AiElementDialog` (closure capture `dlg` pour lire `OptionChecked`). Pas de correction d'harmonie (a.chords vide) — l'IA écrit d'après les accords du contexte.
