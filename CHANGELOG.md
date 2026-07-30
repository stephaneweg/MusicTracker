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

- 📁 Dossier `vst\` portable : le scanner de plugins VST inspecte désormais aussi un dossier `vst\` posé à côté de `KotonStudio.exe` (récursif, en plus des chemins Windows habituels) — bundle tes plugins préférés avec un install portable, ou teste un plugin sans l'installer système-wide. Un plugin local prime sur un plugin système du même nom.
- 🎹 Instruments VSTi (bêta) : dans l'en-tête d'une piste, un bouton « Instrument VSTi… » remplace le patch GM MeltySynth par un synthé VST2 tiers — Vital, Surge XT, TAL-U-No-LX, Spitfire LABS, etc. Menu → liste filtrée (seuls les VSTi apparaissent, effets exclus) → clic sur un nom ; « Éditer le VSTi… » ouvre la GUI native, le patch chargé se persiste dans le projet. Le pipeline (mixeur, inserts, automation, export audio) reste identique — le VSTi ne fait que remplacer la source sonore. Bêta : un plugin qui explose peut faire tomber Koton, aucun VST3 ni sandboxing pour l'instant.
- 🎛 Plugins VST (bêta) : les inserts du mixeur peuvent désormais héberger des plugins VST2 tiers en plus des quatre effets maison. Menu « + Effet ▸ VST… » → un scan automatique de `%ProgramFiles%\VstPlugins` et `%CommonProgramFiles%\VST2` propose la liste, un clic sur le nom ouvre la GUI native du plugin, le patch se persiste dans le projet. Bêta : un plugin qui explose peut faire tomber Koton — usage à ses risques.
- 🎚 Mixeur type console + effets d'insert par piste : ouvre le mixeur pour voir une strip verticale par piste (fader, panoramique, muet/solo, vu-mètre) et une strip Master. Chaque piste (et le master) reçoit sa chaîne d'effets à ajouter à volonté : Égaliseur 3 bandes, Compresseur, Delay (avec ping-pong) et Saturation. Sous le capot, chaque piste a désormais son propre synthé et est rendue en parallèle — c'est ce qui rend le traitement par piste possible.
- 🎛️ Automation MIDI par piste : clic droit sur l'en-tête d'une piste → « Ajouter une automation » pour poser une courbe de Pan, Expression, Modulation, Sustain, Réverbe, Chorus ou Pitch bend en plus du volume — dessine les points à la souris comme la lane de volume. Double-clic sur un point pour le ramener à sa valeur neutre.
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
