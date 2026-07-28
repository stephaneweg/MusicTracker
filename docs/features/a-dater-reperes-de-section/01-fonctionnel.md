# Repères de section sur la timeline — analyse fonctionnelle

## 1. Le besoin

Un morceau un peu long, dans l'éditeur de timeline, est aujourd'hui un **ruban indifférencié** :
des boîtes de modules alignées sur une règle de mesures numérotées. Rien n'indique où commence
l'intro, où revient le thème, où se trouve le pont ou la coda. L'utilisateur n'a que deux repères
visuels : les numéros de mesure et sa propre mémoire.

Conséquences concrètes, toutes vécues dès qu'un morceau dépasse une trentaine de mesures :

- **Se déplacer coûte cher.** Pour réécouter « le refrain », il faut se souvenir qu'il commence
  mesure 33, faire défiler jusque-là, puis cliquer au bon endroit sur la règle pour poser le point
  de départ de lecture. À chaque essai.
- **Travailler une section en boucle est fastidieux.** La boucle A-B existe (poignée bleue = A,
  poignée orange = B), mais il faut repositionner les deux poignées à la main à chaque changement
  de section travaillée.
- **La structure musicale est invisible.** Un morceau construit en intro / thème / développement /
  reprise / coda n'affiche nulle part cette architecture, alors que c'est l'information n° 1 quand on
  arrange.
- **La reprise du travail est laborieuse.** En rouvrant un projet quelques semaines plus tard,
  l'utilisateur doit re-déchiffrer sa propre structure en réécoutant.

**Ce qu'on ajoute.** Des **repères** (marqueurs) nommés, posés par l'utilisateur à des positions
précises du morceau : une petite étiquette « Intro », « Thème A », « Pont », « Coda »… visible en
permanence au-dessus de la règle de mesures, cliquable pour s'y rendre, et enregistrée avec le projet.

**À qui ça sert.**

- Au **compositeur/arrangeur** qui construit une forme : les repères matérialisent le plan du morceau
  et se déplacent avec lui pendant qu'il l'écrit.
- À celui qui **travaille un passage** (répétition instrumentale, réglage d'un groove) : deux clics
  pour boucler exactement la section voulue.
- À celui qui **revient sur un ancien projet** : la structure se lit d'un coup d'œil.
- Aux morceaux **générés automatiquement** (structure/orchestrateur), dont le plan formel n'est
  actuellement lisible nulle part une fois la génération terminée.

C'est une fonctionnalité de confort, mais de confort permanent : elle est utilisée à chaque session
de travail, sur chaque morceau, et ne coûte rien à qui ne s'en sert pas (aucun repère = un bandeau vide).

## 2. Où ça vit dans l'interface

Dans l'**éditeur de morceau** (la timeline), tout en haut de la zone d'arrangement.

Un **bandeau horizontal de repères** est ajouté, fin (de l'ordre de la hauteur de la règle de mesures),
**immédiatement au-dessus de la règle de mesures numérotées**, sur toute la largeur de la zone des
pistes. Il défile **horizontalement en synchronisation** avec la règle et les pistes : un repère reste
toujours pile au-dessus de la mesure qu'il désigne.

À gauche du bandeau, dans la colonne fixe qui surplombe les en-têtes de pistes (là où il n'y a
aujourd'hui rien), une étiquette discrète **« Repères »** identifie la bande.

Le bandeau est **toujours présent**, même sans aucun repère — c'est lui qui sert de zone de dépôt pour
en créer. Vide, il n'affiche rien d'autre que son fond ; une infobulle sur la bande explique comment
en ajouter.

Aucun autre point d'entrée n'est ajouté : pas de nouvelle entrée de menu, pas de nouveau bouton dans
la barre d'outils. Tout se fait sur le bandeau.

## 3. Comportement attendu, vu de l'utilisateur

### 3.1 Ce qu'on voit

Chaque repère s'affiche comme un **fanion** : un petit triangle/onglet posé sur la ligne de mesure
concernée, suivi de son **nom** écrit horizontalement vers la droite. Le fanion est dans la **couleur
d'accent de l'application** (le turquoise), afin de ne se confondre :

- ni avec la **poignée bleue** de départ de lecture (qui vit sur la règle, juste en dessous),
- ni avec la **poignée orange** de fin de boucle A-B,
- ni avec le **curseur jaune** de lecture.

Le nom est tronqué avec des points de suspension s'il n'y a pas la place jusqu'au repère suivant ;
l'infobulle du fanion donne toujours le **nom complet** et le **numéro de mesure**.

Le repère marque **une position**, pas une plage : il n'a pas de longueur. La « section » qu'il ouvre
est implicitement comprise entre lui et le repère suivant (ou la fin du morceau s'il est le dernier) —
cette notion ne sert qu'à la commande de mise en boucle décrite plus bas.

### 3.2 Créer un repère

**Double-clic sur une zone vide du bandeau.**

1. La position est **calée sur la barre de mesure la plus proche** du point cliqué (avec les mêmes
   barres de mesure que celles dessinées par la règle, levée comprise — voir cas limites).
2. Une petite **fenêtre de saisie de texte** s'ouvre (le même petit dialogue thémé que celui déjà
   utilisé pour nommer un style d'accompagnement ou un motif de batterie), pré-remplie avec un nom par
   défaut : **« Repère 1 »**, « Repère 2 »… — le plus petit numéro non encore utilisé dans le morceau.
3. **OK** → le repère apparaît immédiatement à sa mesure. **Annuler** → aucun repère n'est créé, rien
   ne change.

Un nom **vide** (ou uniquement des espaces) validé par OK est traité comme une annulation : pas de
repère anonyme.

Si la mesure visée porte **déjà** un repère, on ne crée pas de doublon : c'est le **renommage** du
repère existant qui s'ouvre (même dialogue, pré-rempli avec son nom actuel).

### 3.3 Se déplacer avec un repère

**Simple clic sur un repère** → le **point de départ de lecture** (la poignée bleue) saute exactement
sur la mesure de ce repère, et le curseur de lecture s'y place. C'est le prolongement naturel du geste
existant « cliquer la règle pour poser le départ », mais au repère près plutôt qu'au pixel près.

Si la lecture est **en cours**, le simple clic ne l'interrompt pas et ne saute pas en cours de route :
il repositionne uniquement le point de départ, qui sera pris en compte à la lecture suivante
(comportement identique à celui du clic sur la règle aujourd'hui).

### 3.4 Renommer

**Double-clic sur un repère** → dialogue de saisie pré-rempli avec le nom actuel. OK renomme,
Annuler ne change rien, un nom vide est refusé (traité comme Annuler).

Deux repères peuvent porter **le même nom** (« Refrain » deux fois, c'est légitime) : aucune unicité
n'est imposée sur les noms, seulement sur les positions.

### 3.5 Déplacer

**Glisser** un repère horizontalement le long du bandeau :

- il suit la souris et se **cale sur les barres de mesure** pendant le glissement ;
- lâché **hors des bornes du morceau**, il est ramené à la mesure valide la plus proche ;
- lâché sur une mesure **déjà occupée** par un autre repère, le déplacement est **annulé** : le repère
  revient à sa position d'origine (on ne fusionne ni n'empile deux repères) ;
- l'ensemble du glissement compte comme **une seule** action annulable, pas une par pixel parcouru
  (même règle que le déplacement des modules aujourd'hui).

### 3.6 Supprimer / commandes

**Clic droit sur un repère** ouvre un petit menu contextuel (même présentation que le menu contextuel
déjà disponible sur les boîtes de modules), avec trois commandes :

| Commande | Effet |
|---|---|
| **Renommer…** | identique au double-clic |
| **Boucler cette section** | active la boucle A-B, pose A sur ce repère et B sur le **repère suivant** — ou sur la **fin du morceau** si c'est le dernier repère |
| **Supprimer** | retire le repère, sans confirmation (l'action est annulable) |

Le clic droit sur une **zone vide** du bandeau n'ouvre aucun menu.

### 3.7 Annuler / rétablir

Créer, renommer, déplacer et supprimer un repère sont des modifications du morceau au même titre que
les autres : chacune est **annulable par Ctrl+Z** et **rétablissable par Ctrl+Y**, et chacune marque
le projet comme modifié (astérisque de titre / demande d'enregistrement à la fermeture, selon le
comportement existant).

### 3.8 Persistance

Les repères font partie du morceau : ils sont **enregistrés dans le fichier `.sq`** et restitués
tels quels à la réouverture (position et nom, y compris les accents et les apostrophes).

### 3.9 Valeurs par défaut

| Élément | Valeur par défaut |
|---|---|
| Nombre de repères d'un nouveau morceau | 0 (bandeau vide) |
| Nombre de repères d'un morceau importé (MIDI/MuseScore) ou généré | 0 |
| Nom proposé à la création | « Repère N », N = plus petit numéro libre |
| Calage à la création et au déplacement | barre de mesure la plus proche |
| Visibilité du bandeau | toujours affiché |

## 4. Cas limites et compatibilité

### 4.1 Fichiers existants (compatibilité ascendante des `.sq`)

Un `.sq` enregistré **avant** cette fonctionnalité ne contient aucune information de repères. Il doit
s'ouvrir **exactement comme aujourd'hui**, sans message ni avertissement, avec un bandeau **vide**.
Réciproquement, un `.sq` contenant des repères et rouvert par une version antérieure de l'application
ne doit pas empêcher l'ouverture — les repères y sont simplement ignorés.

L'ajout des repères ne doit **rien changer** à ce qui existe : lecture, exports (MIDI, MuseScore, PDF,
audio, MusicXML), partition, undo/redo des autres actions, import.

### 4.2 Levée (anacrouse)

Quand le morceau a une levée, les barres de mesure de la règle sont décalées et la mesure « 1 »
commence **après** la levée. Le calage des repères doit utiliser **ces mêmes barres** : un repère posé
au tout début du morceau désigne la **mesure de levée** (son infobulle l'indique comme mesure de
levée, pas comme mesure 1) ; les suivants tombent sur les barres numérotées.

Changer la levée d'un morceau qui contient déjà des repères **ne déplace pas** les repères : ils
gardent leur position temporelle et peuvent donc se retrouver **entre** deux barres de mesure. Ils
restent affichés à leur position exacte (le fanion n'est plus aligné sur une barre) et le prochain
déplacement les recale. C'est une limite assumée, à mentionner dans la documentation utilisateur.

### 4.3 Changement de mesure (chiffrage) ou de tempo

Même règle : un repère conserve sa **position temporelle** dans le morceau ; c'est le numéro de mesure
affiché dans son infobulle qui est recalculé. Un changement de tempo n'a aucun effet sur les repères
(ils sont posés en mesures/temps, pas en secondes).

### 4.4 Édition du contenu

Insérer ou supprimer des modules **ne déplace pas** les repères : ils sont attachés à une position du
morceau, pas à un contenu. Après une grosse réorganisation, l'utilisateur peut avoir à les remettre en
place. Hors périmètre pour cette version (voir § 5).

### 4.5 Repère au-delà de la fin du morceau

Si le morceau raccourcit (suppression de modules) au point qu'un repère se retrouve après la fin :

- le repère **n'est pas supprimé** — il reste dans le fichier ;
- il **n'est plus visible** tant que la timeline ne s'étend pas jusque-là ;
- il **réapparaît** à sa position d'origine dès que le morceau redevient assez long.

Aucun message n'est affiché : une suppression silencieuse serait pire qu'un repère momentanément hors
champ.

### 4.6 Positions confondues

Deux repères ne peuvent pas occuper la même position (§ 3.2 et § 3.5). Si un fichier en contient
malgré tout (édité à la main), l'ouverture les conserve tous et les affiche superposés, sans planter.

### 4.7 Morceau très long / nombreux repères

Aucune limite de nombre n'est imposée. Avec beaucoup de repères rapprochés, les noms se tronquent
(§ 3.1) mais les fanions restent tous cliquables ; l'affichage ne doit pas ralentir le défilement de
la timeline de façon perceptible.

### 4.8 Boucle A-B sur le dernier repère

« Boucler cette section » sur le dernier repère pose B à la **fin du morceau**. Si le repère est déjà
sur la dernière mesure, la boucle couvre au minimum une mesure (la commande ne doit jamais produire
une boucle vide ou inversée).

### 4.9 Langues

Tous les textes visibles introduits (étiquette « Repères », nom par défaut « Repère N », titres des
dialogues de création/renommage, entrées du menu contextuel, infobulles) doivent exister dans les
**sept langues** de l'application. Aucun texte codé en dur, aucune clé brute affichée à l'écran.

## 5. Hors périmètre

Explicitement **non traité** dans cette version — à conserver comme suites possibles :

- **Repères dans les exports** : ils n'apparaissent ni sur la partition à l'écran, ni dans le PDF, ni
  dans les exports MuseScore, MusicXML ou MIDI. (Les repères de répétition en notation sont une
  fonctionnalité à part entière, et l'export MusicXML fait l'objet d'un travail séparé.)
- **Repères qui suivent le contenu** : les repères ne se décalent pas quand on insère ou supprime des
  modules avant eux.
- **Repères de plage** (avec un début *et* une fin, couleur de section, zone teintée derrière les
  pistes) : un repère est un point.
- **Création automatique** de repères à partir de la structure des morceaux générés
  (intro / thème / développement / coda) : la génération ne pose aucun repère.
- **Navigation au clavier** (aller au repère suivant/précédent par raccourci) et **liste des repères**
  (panneau listant tous les repères pour naviguer).
- **Couleur ou icône par repère**, catégories de repères.
- **Repères par piste** : les repères sont globaux au morceau.
- **Copier/coller** un repère, dupliquer une section entre deux repères.

## 6. Critères d'acceptation

Vérifications observables, à conduire dans l'éditeur de morceau.

**Affichage**

1. À l'ouverture d'un morceau quelconque, un bandeau fin est visible **au-dessus** de la règle de
   mesures, avec l'étiquette « Repères » dans la colonne de gauche fixe.
2. Sur un morceau sans repère, le bandeau est vide et l'application se comporte comme avant.
3. En faisant défiler la timeline horizontalement, un repère reste **exactement aligné** sur la barre
   de mesure qu'il désigne, à toutes les positions de défilement.

**Création**

4. Un double-clic sur une zone vide du bandeau, vers la mesure 5, ouvre une fenêtre de saisie
   pré-remplie « Repère 1 ».
5. Valider par OK fait apparaître un fanion turquoise nommé, **à la mesure 5** (aligné sur la barre de
   mesure 5 de la règle).
6. Annuler la fenêtre de saisie ne crée aucun repère.
7. Valider un nom vide ne crée aucun repère.
8. Un second repère créé ensuite propose par défaut « Repère 2 ».
9. Un double-clic sur une zone vide vers une mesure **déjà occupée** ouvre le renommage du repère
   existant (pré-rempli avec son nom), et ne crée pas de second repère.

**Navigation et boucle**

10. Un simple clic sur un repère place la poignée bleue de départ et le curseur de lecture sur la
    mesure de ce repère ; lancer la lecture démarre bien à cet endroit.
11. Clic droit ▸ « Boucler cette section » sur un repère qui en précède un autre : la boucle A-B
    s'active, A est sur ce repère, B sur le repère suivant.
12. La même commande sur le **dernier** repère pose B à la fin du morceau, et la boucle obtenue est
    non vide.

**Édition**

13. Double-clic sur un repère : la fenêtre de renommage s'ouvre pré-remplie ; OK change le nom affiché.
14. Glisser un repère de la mesure 5 à la mesure 9 : il se cale sur la mesure 9 ; **un seul** Ctrl+Z le
    ramène à la mesure 5.
15. Glisser un repère sur une mesure occupée par un autre : il revient à sa position d'origine, et il
    y a toujours autant de repères qu'avant.
16. Clic droit ▸ « Supprimer » retire le repère sans confirmation ; Ctrl+Z le restaure avec son nom.
17. Ctrl+Z juste après une création supprime le repère ; Ctrl+Y le remet.

**Persistance et compatibilité**

18. Enregistrer un morceau contenant trois repères (dont un nom accentué et apostrophé, p. ex.
    « Thème A — reprise l'octave »), fermer, rouvrir : les trois repères sont là, aux mêmes mesures,
    avec les noms **exactement** identiques.
19. Ouvrir un `.sq` créé **avant** la fonctionnalité : aucune erreur, aucun message, bandeau vide, et
    le morceau se lit comme avant.
20. Les exports (MIDI, MuseScore, audio, PDF) d'un morceau avec repères produisent des fichiers
    identiques à ceux du même morceau sans repères.

**Cas limites**

21. Poser un repère sur un morceau avec levée : les fanions s'alignent sur les barres décalées de la
    règle, et l'infobulle d'un repère posé au tout début indique la mesure de levée.
22. Supprimer suffisamment de modules pour que le morceau se termine avant un repère : aucune erreur ;
    en rallongeant à nouveau le morceau, le repère réapparaît à sa mesure d'origine.
23. Basculer l'application dans les sept langues : l'étiquette du bandeau, le nom par défaut, les
    titres des dialogues, les entrées du menu contextuel et les infobulles sont traduits (aucune clé
    brute, aucun texte français résiduel dans les autres langues).
