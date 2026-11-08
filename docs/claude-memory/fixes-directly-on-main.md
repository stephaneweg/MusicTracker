---
name: fixes-directly-on-main
description: "Pour les corrections d'issues, travailler directement sur main (pas de branche/PR)"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 40a83290-5f51-4e89-a486-52209c92af2f
  modified: 2026-07-24T22:22:13.165Z
---

Quand je corrige une issue (manuellement ou via le cron auto-fix), l'utilisateur veut que je travaille **directement dans `main`** : commit + push sur `main`, sans créer de branche `fix/issue-N` ni de Pull Request.

**Why:** dépôt solo, l'utilisateur relit après coup ; les branches + PR sont une friction inutile qu'il finit de toute façon par merger/supprimer lui-même (demandé le 2026-07-25 après avoir mergé et supprimé les branches de #4 et #5).

**How to apply:** rester sur `main`, commit avec message référençant l'issue (`Fix #N : …` + ligne Co-Authored-By Claude), `git push origin main`. Build pour vérifier avant de pousser. Met à jour le prompt du cron auto-fix en conséquence. Voir [[bug-report-github]] pour le flux issues.
