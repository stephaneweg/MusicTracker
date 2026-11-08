---
name: licence-policy
description: Politique licences pour dépendances Koton Studio — tout est OK sauf payant
metadata: 
  node_type: memory
  type: feedback
  originSessionId: da5caa5c-1b54-4450-91c2-c367312ffcde
  modified: 2026-07-30T12:40:19.060Z
---

Pour le choix des dépendances/libs tierces intégrées dans Koton Studio, **n'importe quelle licence libre est acceptable** : MIT, BSD, Apache, LGPL, **GPL pur inclus** (même viral). Seule contrainte : **pas de licence payante / commerciale**.

**Why:** l'user a explicitement levé cette contrainte le 2026-07-30 après le blocage de l'agent VST hosting qui n'avait trouvé QUE des libs LGPL-2.1. Message textuel : « on peut autoriser du GPL pur, n'importe quelle license, je m'en fou pour l'instant, tant que c'est pas payant ». Koton est gratuit, distribué à titre personnel, pas de vente — la contamination virale du GPL n'est pas un problème pratique dans ce contexte.

**How to apply:** quand tu choisis une lib pour Koton (VST hosting, moteur audio, parser, ML, quoi que ce soit), ne l'écarte JAMAIS pour raison de licence libre. Prends la mieux maintenue / la plus adaptée techniquement. Signale la licence dans le rapport (pour info) mais n'en fais pas un critère d'exclusion. La seule question à te poser : est-ce que le fournisseur demande de l'argent ? Si oui = non. Si non (même GPL/AGPL) = OK.

Note liée : les crédits "À propos" et l'ajout au `THIRD-PARTY-NOTICES.md` restent une bonne pratique (respecter les auteurs), mais ce sont des livrables — pas un critère de sélection.

Voir aussi [[release-update-infra]] pour comment les libs finissent packagées dans l'installeur/portable.
