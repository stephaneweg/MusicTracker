---
name: website-deploy
description: "déploiement du site vitrine OVH — FTP simple obligatoire (FTPS refusé), mot de passe DPAPI, script installer/deploy-website.ps1"
metadata: 
  node_type: memory
  type: project
  originSessionId: 358b357f-06c6-4108-9fb9-cf5c4bf4da89
  modified: 2026-07-29T19:15:42.870Z
---

Le site vitrine ([[website-vitrine]]) se déploie via `installer/deploy-website.ps1` (FTP/FTPS natif — **SFTP ABANDONNÉ le 2026-07-29 sur décision de l'user, ne pas le réintroduire ni le reproposer** ; la branche WinSCP a été retirée du script, `Protocol: "sftp"` lève désormais une erreur explicite). Config hors dépôt : `installer/website-ftp.local.json` (gitignoré par `*.local.json`, `*.local.json.*`, `*website-ftp*` ; le `.example` reste versionné par négation). Hébergeur = OVH, `ftp.cluster129.hosting.ovh.net`, compte `kotonss`, racine `/www`. **Le site est en ligne sur https://kotonstudio.com** (routes `/fr/`, `/en/`, `/fr/telecharger`, `/fr/captures`) — l'URL n'est écrite NULLE PART dans le dépôt (`config.php` a `site_url` vide), c'est pourquoi elle est notée ici : sans elle, impossible de vérifier une publication. État validé le 2026-07-28 : `Protocol: "ftp"`, `PasswordEncoding: "dpapi"`, upload réel de 28 fichiers confirmé.

**Why:** l'user voulait un déploiement du site automatisé en fin de pipeline ([[daily-feature-automation]]).

**How to apply:**
- ⚠️ **OVH refuse `AUTH TLS` sur cet endpoint** : le FTPS renvoie « 500 Syntax error, command unrecognized » quel que soit le mot de passe. Ne pas rediagnostiquer ça comme un problème d'identifiants. Le FTP simple est donc la SEULE voie qui marche, et c'est acté.
- **SFTP = sujet clos (2026-07-29).** Le port 22 répond bien en OpenSSH 7.9, mais aucune des deux voies ne valait le coût : `Renci.SshNet.dll` v2024.1 exige `Microsoft.Bcl.AsyncInterfaces` 1.0.0.0 alors que `packages/` fournit la 9.0.0.0 (PowerShell n'ayant pas de binding redirect, il faudrait un handler `AssemblyResolve`), et WinSCP est une dépendance externe à installer. `Renci.SshNet.dll` traîne encore à la racine du repo (gitignorée, déposée par l'user) — inutilisée, supprimable.
- Conséquence acceptée : en FTP simple le mot de passe circule EN CLAIR sur le réseau. Le DPAPI ne protège que le stockage sur disque.
- DPAPI est lié au compte Windows + à la machine : après réinstallation/changement de compte, régénérer avec `ConvertTo-SecureString '<mdp>' -AsPlainText -Force | ConvertFrom-SecureString`.
- Une DLL téléchargée doit être `Unblock-File`-ée, sinon `LoadFrom` échoue en 0x80131515.
- Leçon 2026-07-28 : un mot de passe encodé peut être FAUX sans qu'aucun décodage ne le signale (le base64 stocké décodait 12 caractères, le vrai en faisait 16 → 530). Toujours valider un identifiant par une CONNEXION RÉELLE, jamais par le seul succès du décodage.
