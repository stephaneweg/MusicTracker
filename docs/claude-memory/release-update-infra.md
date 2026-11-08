---
name: release-update-infra
description: "Infra de release + mise à jour auto (repo Releases privé, manifest latest.json, agent de build à venir)"
metadata: 
  node_type: memory
  type: project
  originSessionId: 40a83290-5f51-4e89-a486-52209c92af2f
  modified: 2026-07-27T20:00:20.193Z
---

Direction "distribution" de MusicTracker (démarrée 2026-07-25) :

- **Dépôt séparé `MusicTracker_Releases`** (rendu **PUBLIC** le 2026-07-27 → le site kotonstudio.com télécharge l'installeur en direct via l'URL `raw`) qui héberge les installeurs. Cloné localement dans `releases/` (dépôt git imbriqué, gitignoré par le repo principal — voir `.gitignore` ligne `releases/`). `releases.token` reste **gitignoré** (donc PAS exposé malgré le repo public).
- **Mise à jour auto** (FAIT, commit 4deed28) : au démarrage l'app lit `latest.json` à la racine de `MusicTracker_Releases` via l'API contents GitHub (raw), compare à `AssemblyVersion` ; si plus récent → popup → download installeur → lance + quitte. Code : `Engine/Update/UpdateChecker.cs` (+ `UpdateInfo`), `Dialogs/UpdateDialog`, clé App.config `GitHubReleasesRepo`. Format manifeste : `{ "version": "1.1.0.0", "installer": "MusicTrackerSetup-1.1.0.exe", "notes": "…" }` (installer = chemin relatif dans le repo Releases).
- **Auth** : réutilise le jeton injecté au build du bug-report (`BugReportConfig.Token`, voir [[bug-report-github]]). ⚠️ Ce jeton doit maintenant AUSSI avoir **Contents:read sur MusicTracker_Releases** (sinon la vérif de màj échoue silencieusement). Le commentaire de `BugReportConfig` prévoit qu'à terme ce sera une **clé de licence** utilisateur unique (issues + updates).
- **Versioning** : la source de vérité est `AssemblyVersion` (Properties/AssemblyInfo.cs, actuellement 1.0.0.0) — à bumper à chaque release et à refléter dans `latest.json`.
- **Build agent = FAIT** (Inno Setup choisi) : `installer/MusicTracker.iss` (install per-user `PrivilegesRequired=lowest` → update silencieux sans UAC ; AppId stable `A3F1C9E2-7B4D-4E6A-9C21-5D8E0F3B2A17` ; SoundFont + caches exclus) + `installer/release.ps1` (bump AssemblyVersion patch/minor/major ou `-Version`, build Release, ISCC, copie dans releases/ + latest.json, commit bump sur main + commit/push releases ; `-NoPush` = dry-run) + skill locale `/release` (`.claude/skills/release`, non versionnée). Inno Setup 6.7.3 installé via winget dans `%LocalAppData%\Programs\Inno Setup 6\ISCC.exe`.
- **Releases publiées** : v1.1.0.0 (1re, valide bout-en-bout : détecte→download→install→relance) puis v1.1.1.0. Flux de màj auto CONFIRMÉ côté utilisateur (icônes bureau + fichiers .sq rafraîchies auto au relancement).
- **v1.4.0.0 (2026-07-27)** : rebrand **Koton Studio** + **binaire renommé `KotonStudio.exe`** et **installeur `KotonStudioSetup-*.exe`** (voir [[koton-studio-rebrand]] — SÛR car l'updater lit le nom de l'installeur dans le champ `installer` de `latest.json`, pas en dur ; `[InstallDelete]` supprime l'ancien `MusicTracker.exe` à l'upgrade). Anciens installeurs 1.1→1.3 **purgés** du repo Releases. Repo rendu public. `release.ps1 $setupName` mis à jour en `KotonStudioSetup-`.
- ⚠️ **Piège `release.ps1`** : il ne `git add` QUE `AssemblyInfo.cs` sur main. Toute autre modif source (feature, fix) doit être **committée sur main AVANT** de lancer le script — sinon elle part dans l'installeur mais pas dans l'historique. Le `git push` final du script pousse tous les commits en attente (le mien + le bump).
- ⚠️ **1re release du repo Releases** : `main` n'avait pas d'upstream → le `git push` du script échoue ("no upstream branch"). Fix une fois : `git -C releases push --set-upstream origin main` (fait par le dev ; le classifier bloque ce push côté agent).
- **Icônes** (v1.1.1.0, commit 19f8029) : `app.ico` (piano-roll teal sur tuile sombre) embarquée via `<ApplicationIcon>` dans le csproj ; `document.ico` (même motif sur feuille cornée) livrée à côté de l'exe (`<None>` CopyToOutputDirectory) et pointée par `FileAssociations.DefaultIcon` du ProgId `.sq` (repli sur l'exe si absente). Générées par GDI+ (voir historique) ; la fenêtre WPF hérite de l'icône exe (pas d'`Icon=` sur MainWindow).
- **Droits du token embarqué** (`releases.token`, injecté dans l'exe → extractible) : fine-grained PAT **Contents:Read + Issues:Write** UNIQUEMENT (jamais Contents:Write : sinon un utilisateur pourrait publier). La PUBLICATION des releases (`release.ps1 git push`) utilise les creds git du dev, PAS le token embarqué.
- Écran **À propos** (menu Aide) affiche la version live + crédits ([[meltysynth-audio-engine]] MIT, NAudio MIT — fichiers LICENSE ajoutés).
- **Sémantique `-Bump`** : `patch` incrémente le **3e** chiffre et remet le 4e à 0 (2.0.0.2 → 2.0.1.0). Pour toucher le 4e chiffre, passer `-Version x.y.z.w`.
- **Version portable** (fait) : le script produit aussi `KotonStudioPortable-<ver>.zip` (marqueur `.portable` + `KotonStudioUpdater.exe`), référencé dans `latest.json.portable` + `config.php portable_url` du site. Le script met à jour ET déploie le site (FTP) par défaut ; `-NoWebsite` pour sauter.
- **Dernière release : v2.0.1.0 (2026-11-08)**, clavier d'accords + plugin Arabesque. Installeur + portable poussés, site déployé.
