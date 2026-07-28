# Zoom horizontal de la timeline — analyse fonctionnelle

## 1. Le besoin

L'éditeur de morceau affiche le temps à une **échelle fixe** : un temps occupe toujours la même largeur
à l'écran, quel que soit le morceau, quelle que soit la taille de la fenêtre. Une mesure à 4/4 fait donc
toujours la même largeur, et un morceau de 64 mesures — la longueur par défaut d'une composition assistée —
mesure une dizaine d'écrans de large.

Conséquences, vécues sur tout morceau dépassant une vingtaine de mesures :

- **On ne voit jamais le morceau entier.** L'utilisateur travaille en permanence à travers une fenêtre
  d'une dizaine de mesures. La forme du morceau (où sont les blocs, quelles pistes sont vides à quel
  moment, où le morceau se densifie) n'est jamais visible d'un seul coup d'œil.
- **Se déplacer coûte du défilement.** Comparer le début et la fin, vérifier que la piste de basse
  couvre bien toute la durée, repérer un trou dans une piste : tout se fait en faisant défiler
  horizontalement, sans vue d'ensemble pour se repérer.
- **À l'inverse, on manque parfois de précision.** Sur des modules courts (un accord d'un temps, un
  motif de batterie d'une mesure), les boîtes deviennent étroites : les attraper, les déplacer, ou
  poser un point de tempo/volume au bon endroit se fait sur quelques pixels.
- **Le confort dépend de l'écran.** Sur un grand écran, la place disponible est gâchée (on voit
  toujours le même nombre de mesures) ; sur un portable, l'affichage est vite saturé.

**Ce qu'on ajoute.** Un **zoom horizontal** de l'éditeur de morceau : l'utilisateur choisit combien de
place le temps occupe à l'écran. Dézoomé, il voit la structure entière ; zoomé, il travaille au détail.
Rien d'autre ne change : la musique, les positions, les durées, le son sont strictement identiques —
seule la largeur d'affichage varie.

**À qui ça sert.**

- À **tout utilisateur qui arrange** : c'est la commande la plus utilisée d'un éditeur multipiste,
  sollicitée plusieurs fois par minute de travail.
- À celui qui **relit une composition générée** (structure, orchestrateur, composition assistée) :
  ces morceaux font 32 à 64 mesures et sont aujourd'hui illisibles autrement qu'en défilant.
- À celui qui **édite finement** : placer un module d'un demi-temps, ajuster un point d'automation de
  volume, poser le point de départ de lecture pile au bon endroit.
- À l'utilisateur sur **petit écran** comme sur **grand écran** : l'affichage s'adapte enfin à la place
  disponible.

C'est une fonctionnalité de confort pur, mais permanente et sans contrepartie : qui ne touche pas au
zoom retrouve exactement l'affichage actuel.

## 2. Où ça vit dans l'interface

Dans l'**éditeur de morceau** (la timeline), et nulle part ailleurs.

Deux points d'entrée, complémentaires :

1. **Une commande de zoom dans la barre d'outils du haut**, à droite du groupe « Mesure », dans le même
   style que les autres pastilles de la barre (« BPM », « Tonalité », « Mesure »). Elle contient :
   - un bouton **−** (dézoomer d'un cran) ;
   - un **libellé de niveau** affichant le zoom courant en pourcentage (par ex. « 100 % ») ;
   - un bouton **+** (zoomer d'un cran) ;
   - un bouton **« Ajuster »**, qui choisit le niveau permettant de voir le morceau entier.

   Chaque élément porte une infobulle. Le libellé de niveau est **cliquable** et remet le zoom à 100 %
   (niveau de référence, celui de l'affichage actuel) — un moyen de revenir au repère connu.

2. **Ctrl + molette** au-dessus de la zone temporelle (règle de mesures, pistes, piste d'accords,
   lignes de tempo et de volume) : molette vers l'avant = zoomer, vers l'arrière = dézoomer, un cran
   par déclic.

Aucune autre entrée de menu n'est ajoutée. La commande est visible en permanence, y compris quand
aucun module n'est sélectionné.

## 3. Comportement attendu, vu de l'utilisateur

### 3.1 Les niveaux de zoom

Le zoom se règle par **crans prédéfinis**, pas en continu. Cela évite les niveaux « bâtards » où rien
n'est net, garantit qu'on retombe toujours sur un repère connu, et rend le réglage reproductible.

Niveaux, exprimés en pourcentage de l'échelle actuelle de l'application (100 % = exactement l'affichage
d'aujourd'hui) :

**10 % · 15 % · 25 % · 35 % · 50 % · 75 % · 100 % · 150 % · 200 % · 300 % · 400 %**

- Le niveau par défaut est **100 %**.
- Aux extrémités, le bouton correspondant (**−** à 10 %, **+** à 400 %) est **désactivé** (grisé) :
  l'utilisateur voit qu'il est au bout, plutôt que de cliquer dans le vide.
- À 10 %, un morceau de 64 mesures à 4/4 tient dans la largeur d'une fenêtre maximisée sur un écran
  courant : c'est la vue « structure du morceau ».
- À 400 %, un temps est assez large pour attraper confortablement un module d'un demi-temps.

### 3.2 Ce qui change quand on zoome

**Tout ce qui est dessiné contre le temps suit exactement la même échelle**, sans exception :

- la règle de mesures numérotées (et donc l'espacement des numéros et des graduations de temps) ;
- les boîtes de modules de toutes les pistes (riff, accords, cadence, ligne mélodique, batterie,
  ainsi que les blocs de répétition et leur contenu) ;
- la piste d'accords ancrée en bas ;
- la ligne de tempo et ses points, la ligne de volume et sa courbe d'automation ;
- le curseur de lecture, la poignée de départ, la poignée de fin de boucle.

**Ce qui ne change pas** :

- les **hauteurs** : hauteur des pistes, des lignes de tempo/volume, de la règle. Le zoom est purement
  horizontal (le réglage vertical existe déjà sous forme de repli de piste).
- la **colonne des en-têtes de pistes** à gauche (nom, instrument, volume, muet/solo) : largeur fixe,
  contenu inchangé.
- l'**éditeur du bas** (grille de riff, éditeur d'accords, éditeur de batterie, vue partition) : il a
  sa propre logique d'affichage et n'est pas concerné.
- le **son**, les **exports**, la **partition**, le **fichier enregistré**.

### 3.3 Où se retrouve-t-on après un changement de zoom ?

Changer de zoom ne doit jamais « perdre » l'utilisateur. Règle : **un point musical de référence reste
au même endroit à l'écran**.

- **Ctrl + molette** : la position musicale **sous le pointeur de la souris** reste sous le pointeur.
  C'est le geste naturel — on vise l'endroit qui intéresse et on zoome dessus.
- **Boutons − / + / libellé 100 %** : la position musicale **au centre de la zone visible** reste au
  centre.
- **Pendant la lecture** : le suivi automatique du curseur reprend la main immédiatement après le
  changement de zoom ; le curseur reste visible et continue de défiler normalement.
- **« Ajuster »** : le morceau entier devient visible, donc le défilement horizontal revient au début.

Dans tous les cas, le défilement est borné : on ne se retrouve jamais avant le début du morceau ni
au-delà de sa fin.

### 3.4 « Ajuster »

« Ajuster » choisit **le plus grand niveau prédéfini pour lequel le morceau entier tient dans la largeur
visible** des pistes.

- Si même le plus petit niveau (10 %) ne suffit pas (morceau très long, fenêtre étroite), on se place au
  plus petit niveau et on l'annonce simplement en affichant « 10 % » — pas de message d'erreur.
- Si le morceau est court au point de tenir même très zoomé, « Ajuster » ne dépasse pas 100 % : on ne
  grossit pas artificiellement un morceau de quatre mesures pour remplir l'écran.
- Sur un projet vide (aucune piste, aucun module), « Ajuster » ne fait rien de visible et laisse le
  niveau courant.

### 3.5 Ce qui doit continuer de marcher, à tous les niveaux de zoom

Le zoom est un changement d'affichage : **toutes les interactions existantes restent exactes**.

- **Déplacer un module** par glisser-déposer : le calage se fait toujours sur le temps musical, jamais
  sur un nombre de pixels. Un module lâché « au début de la mesure 9 » atterrit au début de la mesure 9
  aussi bien à 10 % qu'à 400 %.
- **Cliquer sur la règle** pour poser le point de départ de lecture, **glisser la poignée bleue** de
  départ et la **poignée de boucle** : mêmes positions musicales obtenues.
- **Poser / déplacer** un point de tempo, un point d'automation de volume : même exactitude.
- **Sélectionner** un module, ouvrir son menu contextuel, le supprimer, le copier/coller, le fusionner.
- **Lecture** : curseur à la bonne position, suivi automatique, boucle A-B, barre de progression.
- Le **zoom courant est conservé** quand on change de sélection, qu'on ajoute ou supprime une piste ou
  un module, qu'on annule/rétablit (Ctrl+Z / Ctrl+Y), qu'on change de tonalité, de mesure ou de tempo.

### 3.6 Lisibilité aux extrêmes

Aux petits niveaux, une boîte de module peut devenir plus étroite que son texte.

Règle de priorité : **l'exactitude de position et de largeur prime sur la lisibilité du contenu**. Une
boîte doit toujours commencer et finir pile là où le module commence et finit sur la règle — jamais
élargie artificiellement pour qu'un libellé tienne, car cela décalerait visuellement toute la piste.

En conséquence, quand la place manque :

- le **titre** puis les **informations secondaires** de la boîte disparaissent (dans cet ordre) ;
- l'**aperçu mélodique** miniature reste affiché tant qu'il apporte de l'information, et disparaît
  ensuite ; il ne doit jamais déborder de la boîte ni la déformer ;
- la **couleur de fond et la bordure** de la boîte, elles, restent toujours visibles : même réduite à
  quelques pixels, une boîte reste repérable, et une boîte sélectionnée reste distinguable des autres ;
- les **numéros de mesure** de la règle s'espacent (par exemple un numéro toutes les 2, 4 ou 8 mesures)
  plutôt que de se chevaucher ; les graduations de temps intermédiaires peuvent disparaître aux petits
  niveaux.

Aux grands niveaux, symétriquement, rien ne doit être étiré de façon disgracieuse : les textes gardent
leur taille de police normale (le zoom est un zoom de temps, pas un zoom d'interface).

### 3.7 Réactivité

Changer de niveau de zoom sur un projet lourd (de l'ordre de 200 mesures et 8 pistes) doit rester une
opération immédiate du point de vue de l'utilisateur : pas de gel perceptible de la fenêtre, pas de
clignotement de l'affichage. Un enchaînement rapide de crans (molette tenue) doit rester fluide.

## 4. Mémorisation du réglage

Le zoom est un **réglage de confort d'affichage**, pas une propriété du morceau.

- Il **n'est pas enregistré dans le fichier du morceau**. Deux raisons : le fichier ne doit contenir que
  de la musique (partager un morceau ne doit pas imposer le zoom de son auteur), et surtout un réglage
  d'affichage stocké dans le morceau serait restauré par l'annulation (Ctrl+Z) — un utilisateur qui
  annule une suppression de note verrait son zoom changer sous ses yeux, ce qui est inacceptable.
- Il est **mémorisé au niveau de l'application** : le dernier niveau utilisé est retenu et sert de
  niveau de départ aux morceaux ouverts ensuite, y compris après avoir quitté et relancé l'application.
- Chaque **onglet de morceau ouvert a son propre zoom** : zoomer dans un morceau ne touche pas aux
  autres onglets déjà ouverts. Un onglet nouvellement ouvert démarre au dernier niveau utilisé.
- Si aucun réglage n'a jamais été mémorisé (première utilisation, réglages perdus), le niveau de départ
  est **100 %** — c'est-à-dire l'affichage actuel de l'application, à l'identique.

## 5. Cas limites et compatibilité

- **Fichiers de morceaux existants** : aucun changement de format. Un morceau enregistré avant cette
  fonctionnalité s'ouvre exactement comme avant, au niveau de zoom mémorisé de l'application (100 % par
  défaut). Un morceau enregistré après reste lisible par une version antérieure de l'application.
- **Annuler / rétablir** : le zoom n'entre pas dans l'historique. Enchaîner des annulations ne le
  modifie jamais, et changer de zoom ne crée aucune entrée d'historique (ce n'est pas une modification
  du morceau — le morceau n'est d'ailleurs pas marqué comme modifié, donc zoomer seul ne doit pas
  déclencher la demande d'enregistrement à la fermeture).
- **Projet vide** (aucune piste) : les commandes de zoom restent actives et sans effet visible ;
  rien ne doit provoquer d'erreur.
- **Morceau à une seule mesure** : zoomer à 400 % ne doit pas produire de zone de défilement absurde ;
  dézoomer à 10 % doit laisser la mesure visible et cliquable.
- **Morceau très long** (plusieurs centaines de mesures) à 400 % : le défilement horizontal doit rester
  fonctionnel et l'application stable. Si une limite technique de largeur d'affichage devait être
  atteinte, la fonctionnalité doit dégrader proprement (limiter le zoom disponible) et jamais planter.
- **Mesure composée, levée, changements de mesure** : le zoom ne change rien à la façon dont les
  barres de mesure et la levée sont calculées ; il ne fait qu'en changer l'espacement. La levée reste
  alignée avec la règle à tous les niveaux.
- **Ctrl + molette ailleurs** : au-dessus des en-têtes de pistes, de l'éditeur du bas ou de la barre
  d'outils, Ctrl + molette ne zoome pas la timeline (et ne doit rien casser). La molette **sans** Ctrl
  garde son comportement actuel (défilement).
- **Changement de langue à chaud** : les libellés et infobulles des commandes de zoom suivent la langue,
  comme le reste de l'interface. Le pourcentage reste un nombre suivi de « % » dans toutes les langues.
- **Redimensionnement de la fenêtre** : le niveau de zoom ne change pas tout seul (seul « Ajuster » le
  change). L'affichage se réadapte simplement à la nouvelle largeur visible.

## 6. Hors périmètre

Explicitement **non** couvert par cette fonctionnalité :

- le **zoom vertical** (hauteur des pistes) : le repli de piste répond déjà à ce besoin ;
- le zoom de la **grille de riff**, de l'**éditeur de batterie**, de l'**éditeur d'accords** et de la
  **vue partition**, qui ont leur propre logique d'affichage ;
- une **vue d'ensemble / minimap** du morceau (bandeau miniature de navigation) ;
- un zoom **continu** (sans crans), ainsi que les gestes de pincement de pavé tactile ;
- l'enregistrement du zoom **dans le fichier du morceau** (voir section 4) ;
- toute modification du **son**, des **exports** (MIDI, audio, partition, PDF) ou du **contenu musical** ;
- la **mise à l'échelle générale de l'interface** (taille des polices, des boutons) ;
- le **redimensionnement d'un module** par glissement de son bord, qui reste une fonctionnalité
  distincte non traitée ici.

## 7. Critères d'acceptation

Vérifications observables, sur l'éditeur de morceau :

1. **Présence.** La barre d'outils affiche une commande de zoom comportant −, un niveau en pourcentage,
   +, et « Ajuster ». Au premier lancement, le niveau affiché est « 100 % ».
2. **Affichage identique à 100 %.** À 100 %, la largeur des boîtes et l'espacement des mesures sont
   identiques à ceux de la version précédente de l'application (le zoom n'introduit aucun décalage par
   défaut).
3. **Zoom avant.** Un clic sur **+** passe au cran supérieur : le libellé change, et la largeur à
   l'écran d'une mesure donnée augmente dans le rapport annoncé (par exemple ×1,5 en passant de 100 % à
   150 %), à quelques pixels d'arrondi près.
4. **Zoom arrière.** Un clic sur **−** passe au cran inférieur, avec le rapport symétrique.
5. **Bornes.** À 10 %, **−** est désactivé ; à 400 %, **+** est désactivé. Aucun clic ne peut sortir de
   la plage.
6. **Retour au repère.** Un clic sur le libellé de niveau ramène à 100 % depuis n'importe quel cran.
7. **Molette.** Ctrl + molette au-dessus des pistes change le niveau d'un cran par déclic ; la molette
   seule continue de faire défiler comme avant.
8. **Point d'ancrage (molette).** En plaçant le pointeur sur le début d'une mesure donnée puis en
   zoomant, cette même mesure reste sous le pointeur (à quelques pixels près).
9. **Point d'ancrage (boutons).** En zoomant avec **+** ou **−**, la mesure qui était au centre de la
   zone visible y reste.
10. **Alignement.** À chaque niveau, le début de chaque boîte de module coïncide avec la position de son
    module sur la règle de mesures ; la piste d'accords, la ligne de tempo et la ligne de volume restent
    alignées avec la règle et avec les pistes.
11. **Ajuster.** Sur un morceau de 64 mesures, « Ajuster » amène à un niveau où la dernière mesure est
    visible sans défilement horizontal ; sur un morceau de 4 mesures, il ne dépasse pas 100 %.
12. **Exactitude de l'édition.** À 25 % puis à 300 %, déplacer un module par glisser-déposer le place au
    même temps musical qu'à 100 % (vérifiable en comparant sa position sur la règle avant/après un
    changement de zoom).
13. **Poignées.** À 25 % et à 300 %, cliquer sur la règle à une mesure donnée place le point de départ de
    lecture à cette mesure ; la poignée de boucle se pose et se relit à la même position musicale.
14. **Lecture.** Pendant la lecture, le curseur reste calé sur la bonne position à tous les niveaux et le
    suivi automatique continue de fonctionner ; changer de zoom en cours de lecture n'interrompt pas le
    son et ne décale pas le curseur.
15. **Indépendance du morceau.** Changer de zoom ne marque pas le morceau comme modifié : fermer l'onglet
    juste après ne déclenche aucune demande d'enregistrement.
16. **Indépendance de l'historique.** Une annulation (Ctrl+Z) puis un rétablissement (Ctrl+Y) laissent le
    niveau de zoom inchangé.
17. **Mémorisation.** Après avoir réglé le zoom à 50 %, fermé puis rouvert l'application, un morceau
    ouvert s'affiche à 50 %.
18. **Onglets indépendants.** Avec deux morceaux ouverts, changer le zoom de l'un ne change pas celui de
    l'autre.
19. **Compatibilité des fichiers.** Un morceau enregistré avant la fonctionnalité s'ouvre sans erreur et
    sans perte ; un morceau enregistré après contient exactement les mêmes informations qu'avant
    (aucune donnée d'affichage ajoutée).
20. **Lisibilité.** À 10 %, aucune boîte ne déborde sur la suivante, les numéros de mesure ne se
    chevauchent pas, et une boîte sélectionnée reste visuellement distinguable des autres.
21. **Réactivité.** Sur un projet d'environ 200 mesures et 8 pistes, un changement de cran s'applique
    sans gel perceptible de la fenêtre.
22. **Localisation.** Les infobulles et le bouton « Ajuster » s'affichent dans la langue courante pour
    les 7 langues, sans clé manquante ni texte non traduit.
23. **Pilotable par un automate d'interface.** Les commandes −, +, « Ajuster » et le libellé de niveau
    sont identifiables et actionnables par le harnais de test automatisé, et le niveau courant y est
    lisible.
