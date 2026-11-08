---
name: localization
description: Multilingue (FR/EN/DE/IT/ES/NL/PT) — système à clés + dictionnaires lang.xx.json découverts au runtime
metadata: 
  node_type: memory
  type: project
  originSessionId: 37ba4655-81fc-4658-8716-8cdfcb00590b
  modified: 2026-07-25T23:30:56.125Z
---

Localisation multilingue livrée en v1.2.0.0 (issue #8, 2026-07-26). Direction : distribution grand public → l'app doit parler plusieurs langues.

**Architecture (à CLÉS stables, jamais de texte traduisible en clé)** :
- `MusicTracker/Localization/Loc.cs` : `LocalizationManager` (singleton, INotifyPropertyChanged) + markup XAML `{loc:Tr 'MaCle'}` (binding live via `TrConverter` sur la prop `Language`) + `Loc.T("MaCle")` pour le code. Repli : langue active → `fr` (source) → la clé elle-même.
- Un dictionnaire par langue : `Localization/lang.<code>.json` = `{ "Cle": "texte", … }` (369 clés). **Fichiers LOOSE à côté de l'exe (Build Action = Content, PAS EmbeddedResource)** — l'embed via `<EmbeddedResource>` n'a jamais pris (resource absente de l'assembly), d'où le passage en Content.
- **Langues DÉCOUVERTES au démarrage** en scannant `AppPaths.Local("Localization")` pour `lang.*.json` ; nom d'affichage = endonyme via `CultureInfo.GetCultureInfo(code).NativeName`. ⇒ **ajouter une langue = déposer un `lang.xx.json`, AUCUN rebuild**. `LocalizationManager.Instance.Available` pilote le combo Paramètres.
- `AppSettings.Language` (persisté) appliqué dans `App.OnStartup` AVANT la fenêtre.

**Pour ajouter/étendre une langue** : copier `lang.en.json`, traduire les VALEURS (garder les clés identiques, préserver espaces/`\n`/emojis/globs `*.mid`/CAPS), déposer dans `Localization/` (+ `<Content>` dans le csproj pour l'expédier). Les 7 fichiers actuels ont exactement les mêmes 369 clés.

**Changelog localisé (2 surfaces distinctes)** :
1. Popup de mise à jour : `latest.json` porte `notesFr`/`notesEn` (fallback `notes`), lu par `UpdateChecker` selon `Loc.IsEnglish`. `release.ps1` accepte `-NotesEn` (écrit notes+notesFr+notesEn). Voir [[release-update-infra]].
2. Widget « Nouveautés » de l'accueil : `HomeScreen` télécharge `CHANGELOG.md` (fr, source) ou `CHANGELOG.<code>.md` (ex. `CHANGELOG.en.md`) selon la langue, cache par langue. Repli hors-ligne = tableau `News` (localisé par clés). Seuls `CHANGELOG.md`+`CHANGELOG.en.md` existent ; autres langues → repli offline localisé.

**Prompts IA** : `AiArrangementPrompt.SystemPrompt` injecte une directive « rédige les libellés lisibles (noms de sections/articulations, titres) en {langue} » selon `Loc.IsEnglish` — l'app garde le prompt d'instructions en français, seul l'OUTPUT suit la langue (choix validé par l'user, couvre le besoin).

**Gotchas** :
- Outillage de génération (scripts jetables en scratchpad) : `extract_keys`/`gen_keys`/`rewrite_files` ont servi une fois à passer de french-as-key → clés ; les clés sont dérivées de l'anglais en PascalCase (ex. `ComposeWithAI`, `StartFromABlankTimeline`).
- `Loc.T(variable)` : la var doit être une CLÉ, pas du français (ex. `SoundFontGuard.EnsureReady(action)` prend "Playback"/"Export"). Les noms de notes solfège Do/Ré… ne sont PAS localisés.
- Clés sensibles à la casse (`AUDIO` ≠ `Audio`) : OK pour System.Text.Json/StringComparer.Ordinal ; PowerShell ConvertFrom-Json les confond (juste un souci d'outil).
- Couverture = **697 clés** (v1.2.1, 2 passes) : menu, accueil, paramètres, TOUS les dialogues, l'éditeur timeline et les contrôles, dans les 7 langues. Restent hors périmètre (volontairement) : le texte du prompt IA (français, lu par le modèle) et les données/terminologie du moteur (noms de styles/formes des composers). Extensible en enveloppant + ajoutant la clé aux 7 dicos.
- Workflow 2e passe (rejouable) : agents enveloppent avec le TEXTE FR comme placeholder `{loc:Tr 'français'}` → `round2_keyify.py` (scratchpad) extrait les nouveaux FR (≠ clés existantes), génère des clés PascalCase dérivées du FR, les ajoute à lang.fr.json, réécrit les fichiers FR→clé, émet `newfrench.json` (clé→FR) → 6 agents traduisent (add.<code>.json) → `merge_round2.py` fusionne. ⚠️ crée des clés dupliquées pour des mots déjà traduits en passe 1 (ex. "Accords" ↔ clé "Chords") — bénin.
