---
name: daily-feature-automation
description: "pipeline quotidien multi-agents \"musictracker-daily-feature\" (analyse→dev→test→release) et ses points de configuration"
metadata: 
  node_type: memory
  type: project
  originSessionId: 358b357f-06c6-4108-9fb9-cf5c4bf4da89
  modified: 2026-07-28T10:37:04.931Z
---

Scheduled task local (pas cloud) `musictracker-daily-feature`, cron `0 20 * * *`, prompt dans `C:\Users\swe\.claude\scheduled-tasks\musictracker-daily-feature\SKILL.md`. Refondu le 2026-07-28 en PIPELINE MULTI-AGENTS à la demande de l'user : la tâche est un chef d'orchestre qui appelle le workflow `feature-pipeline` (`.claude/workflows/feature-pipeline.js`) = analyste fonctionnel → architecte technique → développeur → testeur, avec boucle « diagnostic du bug → correctif → re-test » bornée à 3 tours. Documents produits dans `docs/features/<date>-<slug>/` (01-fonctionnel / 02-technique / 03-tests), conservés même si le run échoue. Si vert : commit+push main, CHANGELOG(.en).md, `installer\release.ps1 -Bump patch` (release AUTO à chaque feature verte — choix explicite de l'user), puis site (`WebSite/includes/config.php` + `changelog.php`) et `installer\deploy-website.ps1`. Si rouge : revert du code, mais on garde et commit les docs.

**Why:** l'user veut un agent qui trouve/implémente/teste des features en autonomie avec rapport journalier, et a explicitement demandé un agent distinct par étape + release auto à chaque feature verte.

**How to apply:**
- ⚠️ Le workflow s'invoque par `Workflow({ scriptPath: "...\.claude\workflows\feature-pipeline.js" })`, PAS par `{ name: "feature-pipeline" }` : seuls les workflows intégrés (deep-research, code-review) sont découverts par nom, un `.claude/workflows/*.js` ne l'est pas et échoue en « Workflow not found ».
- `.claude/` est GITIGNORÉ (comme les skills) → le script du workflow n'est PAS versionné, il vit uniquement sur cette machine. Le perdre = perdre le pipeline.
- Le dossier docs du repo est `docs/` MINUSCULE (déjà suivi par git, contient docs/images/) ; Windows étant insensible à la casse, écrire `DOCS/` crée silencieusement dedans. Toujours écrire `docs/features/`.
- Déploiement du site = FTP/SFTP via `installer/deploy-website.ps1`, identifiants dans `installer/website-ftp.local.json` (gitignoré via `*.local.json`, format dans le `.example`). **L'user doit créer ce fichier lui-même** — tant qu'il manque, le script s'arrête proprement et le site n'est pas déployé (ce n'est pas une erreur du run).
- L'automation ne tourne QUE si l'app Claude Code est ouverte à l'heure prévue (sinon au prochain lancement — ce n'est pas un cron OS). Si l'user dit « ça n'a pas tourné », vérifier ça en premier.
- Voir aussi [[daily-ui-test-automation]] (tâche sœur à 21h30 qui lance AutoTest/ et ouvre des issues) : le pipeline feature lance AUSSI AutoTest, risque de chevauchement si le run de 20h déborde.
