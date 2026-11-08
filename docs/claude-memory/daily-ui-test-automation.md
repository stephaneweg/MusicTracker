---
name: daily-ui-test-automation
description: "tâche planifiée locale \"musictracker-daily-test\" (21h30) qui build + teste l'UI via FlaUI et ouvre des issues GitHub labellisées auto-test"
metadata: 
  node_type: memory
  type: project
  originSessionId: ecb90573-8767-4c25-b440-32fc1c58d4be
  modified: 2026-07-28T09:23:02.202Z
---

Tâche planifiée locale créée le 2026-07-28 : `musictracker-daily-test`, cron `30 21 * * *` (21h30 locale, soit **après** [[daily-feature-automation]] qui tourne à 20h — le but est d'attraper les régressions introduites par la feature du jour). Fichier `C:\Users\swe\.claude\scheduled-tasks\musictracker-daily-test\SKILL.md`.

Chaîne de test **dans le repo** (donc versionnée) : projet `AutoTest/` (net48, FlaUI.Core + FlaUI.UIA3, ajouté à la .sln) + `AutoTest/run.ps1` qui build l'app en Release via MSBuild 2022, build le harness via `dotnet build`, tue les `KotonStudio.exe` résiduels, lance les scénarios et écrit `AutoTest/results.json`. Codes de sortie : 0 vert / 1 échec scénario / 2 MSBuild absent / 3 build app KO / 4 build harness KO. Scénarios v1 (tous verts au 2026-07-28) : AppLaunch, NewMusicOpensEditor, PlayThenStop, SaveDialogOpensAndCancels, OpenTemplateProject.

**Why:** demande explicite user (2026-07-28) — « un agent qui teste automatiquement l'app et crée des issues dans github ». Choix validés avec lui : scénarios UI réels (pas juste un smoke build), planifié quotidien, issues via le CLI `gh` déjà authentifié (pas via le `GitHubIssueClient` de l'app / token XOR de [[bug-report-github]]).

**How to apply:** garde-fous inscrits dans le SKILL — si le working tree est sale le run teste mais **ne crée aucune issue** (échec sur du WIP ≠ régression) ; dédoublonnage obligatoire sur le label `auto-test` avant création ; **plafond de 3 issues par run** ; fermeture auto des issues `auto-test` dont le scénario repasse au vert ; rapport dans `REPORTS/<date>-test.md`. Le run est **lecture seule** sur les sources (ajouter un scénario = session de dev normale, pas un run de test).

**Deux pièges UIA découverts à la mise au point** (documentés aussi dans le SKILL, à respecter pour tout nouveau scénario) : (1) `Invoke()` d'UIA est **synchrone** — sur un bouton qui ouvre un `ShowDialog()` modal il bloque jusqu'à fermeture, donc le dialogue n'est jamais observable ; utiliser un vrai clic souris (`ClickModal`). (2) Les dialogues de l'app (`WindowStyle="None"` + `AllowsTransparency="True"`, cf. [[custom-file-dialog]]) apparaissent comme fenêtres **enfants du main window** dans l'arbre UIA, pas comme fenêtres top-level — d'où `WaitForNewWindow` qui cherche d'abord dans les enfants.

Comme [[daily-feature-automation]], ça ne tourne QUE si l'app Claude Code est ouverte à l'heure prévue. `AutoTest/results.json` a dû être détracké (commit `5f90034`) : suivi par git, il salissait le repo à chaque passe et faisait s'auto-annuler le run daily-feature.
