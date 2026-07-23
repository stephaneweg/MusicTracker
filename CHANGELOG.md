# Nouveautés de MusicTracker

Ce fichier alimente le widget **« Nouveautés »** de l'écran d'accueil de l'application.
MusicTracker le télécharge directement depuis GitHub (URL brute), ce qui permet de communiquer
les nouveautés **sans publier une nouvelle version** : il suffit de modifier ce fichier et de le pousser.

L'URL est configurable dans `MusicTracker/App.config`, clé `ChangelogUrl`.

## Format attendu

Une entrée par ligne : `- <emoji> <texte>`.
La première « chose » après le tiret est prise comme icône, le reste comme texte.
Tout le reste (titres, lignes vides, paragraphes) est **ignoré** — ce fichier reste donc lisible sur GitHub.
Les entrées les plus **récentes** vont **en premier** ; l'application n'en affiche que les premières.

## Entrées

- 🎲 Modèles de projet génératifs : un modèle contient désormais des banques de matière par section (progressions, motifs d'accompagnement, cellules mélodiques, phrases par instrument, grooves). À l'ouverture l'app y pioche et assemble le morceau — un bouton « Régénérer » retire une nouvelle version.
- 🤖 Génère tes propres styles avec l'IA : décris une intention, l'IA fabrique le modèle. Clic droit sur une carte pour le régénérer en réutilisant ton intention.
- 🎻 Les phrases d'un modèle sont transposées modalement sur chaque accord (avec conduite des voix), et l'IA peut laisser un instrument silencieux sur une section pour aérer l'arrangement.
- 🎹 Rendu audio nettement amélioré : équilibre des instruments corrigé (dé-duplication des modulateurs SoundFont et prise en compte des modulateurs de filtre), plus de trou sur le piano.
- 🎚️ Dynamique par vélocité : les temps forts ressortent, les contretemps s'effacent — la lecture respire au lieu d'être uniforme.
- 🎶 L'IA génère une cellule mélodique en plus du motif d'accompagnement ; elle est transposée modalement sur chaque accord.
- 🏷️ Les motifs d'accords produits par l'IA sont enregistrés sous un nom : modifiables et réutilisables depuis le sélecteur de styles.
- ↔️ La timeline défile en continu pendant la lecture, curseur maintenu au centre.
- 🔊 Message explicite quand aucun SoundFont n'est trouvé, au démarrage comme à la lecture.
- 🎛️ Modèles de projet : depuis un fichier, avec l'IA, ou à ajouter dans le dossier — avec suppression.
- 🥁 Catalogue de motifs batterie (Standard, Afrique, Australie) + tes motifs enregistrés, réutilisables.
- 🔑 Plusieurs clés API par fournisseur, choisies par nom dans les écrans de composition.
- 🎼 Templates IA structurés (intro/thème/développement/outro), étendus à la longueur voulue.
- 🎨 Interface sombre & teal, dialogues déplaçables, éditeurs enrichis.
