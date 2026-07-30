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

- 🎛️ Automation MIDI par piste : clic droit sur l'en-tête d'une piste → « Ajouter une automation » pour poser une courbe de Pan, Expression, Modulation, Sustain, Réverbe, Chorus ou Pitch bend en plus du volume — dessine les points à la souris comme la lane de volume.
- 🎼 Export MusicXML (menu Export) : sors ta partition dans le format d'échange lu par tous les logiciels de notation — Finale, Sibelius, Dorico, MuseScore, les éditeurs en ligne et les liseuses sur tablette. Les notes qui traversent une barre de mesure sont désormais écrites liées, au lieu d'être tronquées.
- 🎵 Swing : dans « Mesure », choisis des croches inégales (léger, moyen, swing, ternaire). La lecture et l'export audio suivent le groove ; la partition et l'export MIDI gardent des croches égales.
- 🎶 « Ajouter un instrument / une batterie avec l'IA » (menu Piste) : décris une intention, l'IA reçoit TOUT le morceau (toutes les pistes + les accords) et compose une nouvelle piste — ligne mélodique (rythme seul) ou mélodie complète, ou un groove — qui s'intègre à l'arrangement existant.
- 🌍 Choix de la langue (Français, English, Deutsch, Italiano, Español, Nederlands, Português) dans les Paramètres : l'interface, le fil des nouveautés et les titres générés par l'IA suivent la langue choisie. D'autres langues s'ajoutent en déposant un fichier `lang.xx.json`.
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
