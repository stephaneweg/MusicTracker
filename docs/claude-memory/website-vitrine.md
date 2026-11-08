---
name: website-vitrine
description: "Site web PHP de présentation/distribution du logiciel, dans WebSite/ (gitignoré, déployé à la main)"
metadata: 
  node_type: memory
  type: project
  originSessionId: 07f39978-3ab3-4d2e-ac37-474e06a11356
  modified: 2026-07-27T20:00:28.160Z
---

Site vitrine **PHP** de MusicTracker, dans `WebSite/` à la racine du repo.
**GITIGNORÉ** (`/WebSite/` ajouté au .gitignore) → absent d'un clone frais ; l'utilisateur le déploie lui-même sur son hébergeur.

- **Stack** : PHP 7.4+ pur, aucune dépendance ni BDD. Thème sombre & teal (#1FB6C3) à l'image de l'appli, bilingue **FR/EN**, responsive, police Inter.
- **Pages** : `index.php` (hero + stats + fonctionnalités + CTA), `download.php` (téléchargement + prérequis + installation + changelog), `screenshots.php` (galerie auto-découverte de `assets/img/screenshots/`, repli maquette SVG).
- **À maintenir à chaque release** : `includes/config.php` (version, `download_url` = `github.com/stephaneweg/MusicTracker/releases/latest`) et `includes/changelog.php` (notes FR/EN).
- **i18n** : `includes/lang.php` + `lang/fr.php`/`lang/en.php` (ajouter une langue = copier le fichier + l'ajouter dans config). Même esprit que les [[localization]] lang.xx.json du logiciel.
- **Distribution choisie** : lien GitHub public (pas de proxy token). Si releases privées → rendre public ou héberger l'.exe et pointer `download_url` dessus.
- Icône réutilisée : `MusicTracker/app.ico` → `WebSite/assets/img/favicon.ico`. Pas de vrai logo dans le repo → logo SVG maison (note musicale) dans header.php.
- **PHP installé pour IIS** : `C:\PHP` (php-8.4.23-nts-Win32-vs17-x64, php.ini configuré cgi.fix_pathinfo=0). Site lint-clean + rendu vérifié via `C:\PHP\php.exe -S`. Branchement IIS (module CGI + handler FastCGI) = script admin `WebSite/setup-iis.ps1` (le shell Claude n'est PAS élevé → l'utilisateur le lance en admin). Site créé sur port 8080.
- **Rebrand « Koton Studio »** (marque Koton, ex-« Aria Studio ») à appliquer au site (marque header/footer, titres, chaînes fr/en) — voir [[koton-studio-rebrand]]. URLs GitHub + nom `MusicTrackerSetup-*.exe` conservés.
- **URLs propres + SEO** : front controller `router.php` → URLs `/<lang>/<slug>` localisées (`/fr/telecharger`, `/en/download`, `/fr/captures`) via table `config['routes']` + helpers `mt_url`/`mt_switch_url`/`mt_origin` (lang.php). Apache = `.htaccess` (DirectoryIndex router.php + rewrite). IIS SANS module URL Rewrite = `web.config` : defaultDocument router.php + `httpErrors 404 → ExecuteURL /router.php` (router lit UNENCODED_URL/X-Original-URL, force 200 ; sous IIS un slug inconnu redirige 302 pour éviter la boucle). SEO : canonical + hreflang fr/en/x-default + OG/Twitter (og:image=01-accueil) + JSON-LD SoftwareApplication + `/robots.txt` & `/sitemap.xml` dynamiques (routeur). Assets/liens en chemin RACINE `/assets/...` (obligatoire avec les URLs à segments). Test local sans IIS : `C:\PHP\php.exe -S 127.0.0.1:PORT router.php`.
- **Déploiement (fait 2026-07-27)** : **FTP OVH** `ftp.cluster129.hosting.ovh.net:21` (user `kotonss`, mot de passe NON stocké ici) → dossier **`/www`** de kotonstudio.com. 32 fichiers poussés via boucle `curl -T --ftp-create-dirs` (FTPS `--ssl-reqd` échoue avec le curl Schannel de Windows → erreur 64 ; rester en FTP simple, réessayer les timeouts transitoires). Lire les identifiants FileZilla (`sitemanager.xml`) est **bloqué par le classifier** → demander le user/mdp à l'utilisateur. Téléchargement du site = URL `raw` du repo **public** `MusicTracker_Releases` (plus les GitHub releases). ⚠️ **SSL du domaine à activer dans le panel OVH** (le serveur présente le cert `cluster129...` par défaut → avertissement HTTPS tant que Let's Encrypt kotonstudio.com pas activé). Favicon/logo = `koton_logo.png` recoloré teal (voir [[koton-studio-rebrand]]).
- **Vraies captures** dans assets/img/screenshots/ (01-accueil recadrée sans la colonne Récents perso, 02-timeline-arrangement, 03-timeline-multipistes) prises via build isolé `bin\AriaPreview` + PrintWindow (script scratchpad capture-app.ps1). Ouvrir un .sq en argument mène direct à la timeline.
