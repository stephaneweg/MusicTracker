---
name: koton-studio-rebrand
description: "Le logiciel est « Koton Studio » (marque Koton, ex-« Aria Studio ») : affichage + binaire + installeur renommés en v1.4 ; namespace/dossier/AppId internes restent MusicTracker"
metadata: 
  node_type: memory
  type: project
  originSessionId: 07f39978-3ab3-4d2e-ac37-474e06a11356
  modified: 2026-07-27T19:33:53.497Z
---

Produit = **« Koton Studio »**, partie de la **marque ombrelle Koton** de l'utilisateur : **Koton Violins** (lutherie, `kotonviolins.com`) + **Koton Studio** (le logiciel, `kotonstudio.com` acheté). Historique des noms : MusicTracker → Aria Studio → **Koton Studio** (décidé + expédié en **v1.4.0.0**, 2026-07-27).

**FAIT en v1.4 — affichage** : « Koton Studio » dans MainWindow `Title`, AboutDialog, `AssemblyTitle`/`AssemblyProduct` (+ `AssemblyCompany`="Stephan WEGENER", Copyright idem), HomeScreen, FileAssociations `.sq`, clé loc `AboutMusicTracker` (les 8 langues ; élision FR corrigée « de Koton Studio »), installeur `#define MyAppDisplayName`, README, et tout le site `WebSite/`.

**FAIT en v1.4 — binaire + installeur renommés** (revient sur l'ancienne consigne « garder le nom exe ») :
- `<AssemblyName>` = **KotonStudio** → l'exe est **KotonStudio.exe** (l'`App.config` devient `KotonStudio.exe.config`).
- Installeur = **KotonStudioSetup-<ver>.exe** (`OutputBaseFilename` + `release.ps1 $setupName` + `WebSite/download.php`).
- **Pourquoi c'est SANS DANGER pour l'auto-update** : `UpdateChecker.cs` lit le nom de l'installeur dans le champ `"installer"` de `latest.json` (PAS codé en dur) ; tant que `latest.json` reste au même endroit et que l'AppId Inno est stable, un renommage passe. Ajout d'un `[InstallDelete]` dans l'.iss qui supprime l'ancien `MusicTracker.exe`(+`.config`) à l'upgrade.
- ⚠️ ~50 scripts d'analyse `*.ps1` à la RACINE (gitignorés) chargeaient `$bin\MusicTracker.exe` par réflexion → mis à jour en `KotonStudio.exe`.

**GARDÉ tel quel (identité/infra interne — NE PAS toucher)** : namespace C# `MusicTracker`, `RootNamespace` MusicTracker, dossier install `{autopf}\MusicTracker` (`MyAppName`), AppId Inno `A3F1C9E2-…`, ProgId `MusicTracker.MusicFile`, dossier AppData `MusicTracker`, repos GitHub `stephaneweg/MusicTracker(_Releases)`, clés App.config.

**Logo** : reprend le monogramme de Koton Violins — marqueterie **K + U entrelacés avec 木 (moku = bois/arbre)** sur tuile arrondie — **recoloré teal (#1FB6C3) sur fond foncé** (bois foncé→teal, bois clair→fond foncé) au lieu du bois. Source de référence : `kotonviolins.com/assets/images/koton_logo.png`. Génération = recolorage par seuil de luminance (script scratchpad `recolor_logo.ps1`, GDI+). (Câblage app.ico/PNG/favicon = à finaliser.)

Voir [[release-update-infra]] et [[website-vitrine]].
