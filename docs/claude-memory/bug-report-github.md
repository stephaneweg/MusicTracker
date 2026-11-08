---
name: bug-report-github
description: "Signaler un bug" menu → crée une issue GitHub (+ 3e type Exception alimenté par le garde-crash); token injecté au build (XOR+base64), jamais committé; futur = clé de licence
metadata:
  type: project
---

Menu « Signaler un bug » (bouton 🗨 MDL2 E939 dans la top-bar, à gauche de l'engrenage) → crée une **issue GitHub** sur `stephaneweg/MusicTracker` (clé `GitHubRepo` dans App.config). Dialogue `ReportBugDialog` : **Type (Bug/Suggestion)** + titre + description + cases « joindre projet »/« joindre modèle ». Type → titre préfixé `[Bug]`/`[Suggestion]` + label GitHub (`bug` / `enhancement`, + `in-app`). Contexte = résumé + JSON `.sq` du projet et le `TemplateSpec` (si `FromTemplate`) dans des `<details>` repliés, tronqué à 60k car (limite GitHub 65536). Après envoi : message de confirmation + boutons **OK** / **Ouvrir dans le navigateur**. Token de build réel déjà en place (`bugreport.token`, gitignoré).

Skill projet `.claude/skills/fix-issue/SKILL.md` (invocable `/fix-issue`) : liste les issues via `gh` (authentifié, scope repo+workflow), demande laquelle traiter, extrait le JSON `.sq` joint comme cas de repro (scratchpad), explore+corrige, build (MSBuild via vswhere), grill seulement si bloqué, puis branche `fix/issue-<N>` + commit `Fixes #<N>` + push + `gh pr create`. Peut déléguer les étapes fix→push à un Agent en arrière-plan.

**Token GitHub = injecté à la COMPILATION, jamais committé.** Cible MSBuild `GenerateBuildSecrets` (tâche inline CodeTaskFactory dans le .csproj) lit `MusicTracker/bugreport.token` (gitignoré) ou l'env `MUSICTRACKER_GH_TOKEN`, XOR (clé `MusicTracker-BugReport-XOR-v1`) + base64 → génère `obj/…/BuildSecrets.g.cs` (`BuildSecrets.GitHubIssueTokenXorB64`, ajouté à @(Compile) BeforeTargets=CoreCompile). Build sans token compile → constante vide → reporter « non configuré ». Décodage runtime dans `BugReportConfig.Token` (**clé XOR dupliquée ici, garder synchro**). XOR+base64 = casse le secret-scanning GitHub (détection par format) + pas de token en clair dans `strings`; PAS une vraie sécurité (extractible du binaire) — OK pour PAT fine-grained Issues:write sur repo public. Voir `bugreport.token.example`.

Fichiers: `Engine/BugReport/{GitHubIssueClient,BugReportConfig}.cs`, `Dialogs/ReportBugDialog.xaml{,.cs}`, wiring `MainWindow.btnReportBug_Click` (passe `current as TimelineScreen`), helpers `TimelineScreen.BugReport{ProjectJson,TemplateJson,Summary}()`.

**Garde-crash (2026-07-30, commit a8637f4).** `Engine/BugReport/CrashGuard.cs` branché depuis `App.OnStartup` : Dispatcher (marque `Handled` → l'app SURVIT) + AppDomain (fatal) + TaskScheduler (journal SEULEMENT, pas de dialogue : ça se déclenche au GC, longtemps après le fait). Journal **`%AppData%\MusicTracker\crash.log`** écrit AVANT toute UI, borné à 512 Ko. Puis `ReportBugDialog(editor, ex, fatal)` = 3e type **Exception** (verrouillé, titre pré-rempli, détail affiché tel quel, labels `bug`+`crash`+`in-app`).

⚠️ **Piège WPF vérifié à l'exécution** : ouvrir une fenêtre **modale** depuis un gestionnaire `DispatcherUnhandledException` fait **redélivrer la MÊME instance d'exception** au gestionnaire une fois la modale fermée → deux entrées de journal et deux dialogues pour un seul plantage. D'où le dédoublonnage par `ReferenceEquals` dans une fenêtre de 30 s recomptée à la fermeture. Ne pas « simplifier » ce filtre.

**Roadmap (user, « plus tard ») :** le token deviendra une **clé de licence** paramétrable (dans les Paramètres) qui débloquera : voir + installer les mises à jour ET envoyer des issues. TODO déjà posé dans `BugReportConfig.Token` : lire d'abord la clé des settings, fallback token de build. Lié à [[home-redesign-templates]] (widget Nouveautés/updates).
