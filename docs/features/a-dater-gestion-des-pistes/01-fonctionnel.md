# Organiser les pistes : dupliquer, réordonner, suppression annulable — analyse fonctionnelle

## 1. Le besoin

Dans l'éditeur de morceau, une piste se **crée** et se **supprime**, et c'est tout : on ne peut ni la
**dupliquer**, ni la **déplacer** dans la pile des pistes. Et sa
suppression est **définitive** — c'est aujourd'hui la seule action de l'éditeur qui ne se rattrape pas
par Ctrl+Z, alors qu'un clic sur la petite croix de l'en-tête peut emporter des dizaines de mesures de
travail sans un mot d'avertissement.

Ce que ça coûte à l'usage :

- **Essayer une variante est décourageant.** Pour tester une autre articulation, un autre registre ou
  un autre instrument sur une partie déjà écrite, il faudrait recréer une piste vide et y recoller les
  blocs un par un (le copier/coller existe, mais module par module). Résultat : on modifie l'original
  et on perd la version précédente.
- **Les doublures se font à la main.** Doubler une mélodie à l'octave, ou la faire jouer par un second
  instrument, est un geste d'arrangement banal ; ici c'est un travail de recopie.
- **L'ordre des pistes est figé à l'ordre de création.** Or cet ordre est la lecture visuelle du
  morceau (mélodie en haut, accompagnement, basse, batterie) et il conditionne aussi l'ordre des
  pistes dans le mixeur. Une piste ajoutée en cours de route atterrit toujours en bas, loin de la
  famille à laquelle elle appartient.
- **Une suppression accidentelle est irrattrapable.** La croix de suppression est à quelques pixels de
  la case « afficher dans la partition » et du champ de nom. Après elle, ni annulation, ni message :
  la seule issue est de rouvrir le fichier enregistré, donc de perdre tout le travail non enregistré.

**Ce qu'on ajoute.** Trois commandes sur l'en-tête d'une piste — **Dupliquer**, **Monter**,
**Descendre** — et le passage de la **suppression d'une piste au régime commun de l'annulation**
(Ctrl+Z / Ctrl+Y), comme toutes les autres modifications du morceau.

**À qui ça sert.**

- À l'**arrangeur** qui empile les voix : dupliquer puis transposer, dupliquer puis changer
  d'instrument, dupliquer pour comparer deux versions d'une même partie côte à côte.
- À celui qui **range son morceau** : regrouper les cordes, remonter la mélodie en tête, descendre une
  nappe.
- À **tout le monde**, pour la suppression annulable : c'est un filet de sécurité, pas une
  fonctionnalité qu'on va chercher.

C'est une fonctionnalité de manipulation courante : peu spectaculaire, mais utilisée à chaque session
dès que le morceau dépasse deux ou trois pistes.

## 2. Où ça vit dans l'interface

Dans l'**éditeur de morceau**, sur la **colonne des en-têtes de pistes** (à gauche des lanes).

Un **clic droit sur l'en-tête d'une piste** ouvre un menu contextuel — même présentation que le menu
contextuel déjà disponible sur les boîtes de modules — contenant quatre commandes :

| Commande | Effet |
|---|---|
| **Dupliquer la piste** | crée une copie complète et indépendante, insérée juste en dessous |
| **Monter** | échange la piste avec celle qui la précède |
| **Descendre** | échange la piste avec celle qui la suit |
| **Supprimer la piste** | retire la piste (désormais annulable) |

Le clic droit **sélectionne** d'abord la piste visée (comme le clic gauche le fait déjà), puis ouvre
le menu : la commande porte toujours sur la piste sous le curseur, jamais sur une autre.

Le menu s'ouvre aussi sur un en-tête **replié** (piste en mode compact), avec les mêmes commandes.

Aucun bouton n'est ajouté dans l'en-tête (déjà dense : replier, nom, partition, supprimer) et aucune
entrée n'est ajoutée dans la barre de menus. La croix de suppression existante reste en place et fait
exactement ce que fait « Supprimer la piste » du menu.

## 3. Comportement attendu, vu de l'utilisateur

### 3.1 Dupliquer une piste

La copie est **complète et indépendante** :

- elle contient **tous les blocs** de la piste d'origine, aux **mêmes positions** (silences avant les
  blocs compris), y compris le contenu **à l'intérieur des blocs de répétition** ;
- elle reprend les **réglages de la piste** : instrument (ou kit de batterie), volume de base,
  panoramique, muet, solo, courbe de volume, état replié/déplié, clé de notation choisie ;
- elle reprend l'état de la case **« afficher dans la partition »** de la piste d'origine ;
- **modifier la copie ne modifie jamais l'original**, et réciproquement : éditer les notes d'un bloc
  de la copie ne doit pas changer le bloc correspondant de la piste d'origine. C'est le point le plus
  important de la commande, et le comportement qu'a déjà le collage d'un bloc.

**Nom de la copie** : le nom d'origine suivi de « (copie) » — par exemple « Mélodie » → « Mélodie
(copie) ». Si ce nom existe déjà dans le morceau, on numérote : « (copie 2) », « (copie 3) »…

**Position** : la copie est insérée **immédiatement en dessous** de la piste d'origine, et devient la
**piste sélectionnée**. Si elle n'est pas visible, l'affichage se déplace pour la montrer.

Les **étiquettes des blocs** copiés sont identiques à celles des blocs d'origine : c'est le nom de la
piste qui distingue la copie, pas un suffixe répété sur chaque boîte.

### 3.2 Monter / Descendre

La piste échange sa place avec sa voisine du dessus (Monter) ou du dessous (Descendre). Elle **reste
sélectionnée** après le déplacement, et l'affichage la suit si nécessaire.

- La piste la plus **haute** ne peut pas monter, la plus **basse** (hors piste d'accords, voir § 4.1)
  ne peut pas descendre : dans ces cas la commande est **grisée** dans le menu, pas absente.
- Le déplacement est **immédiat**, sans confirmation, et ne change **rien au son** : mêmes blocs,
  mêmes positions, même rendu à la lecture et à l'export.
- L'**ordre du mixeur** suit l'ordre des pistes : après un déplacement, la piste apparaît à sa
  nouvelle place dans le mixeur.

### 3.3 Supprimer une piste

Comportement inchangé — retrait immédiat, sans confirmation — à une différence près : la suppression
devient **annulable** par Ctrl+Z, qui restitue la piste **à sa place d'origine**, avec tous ses blocs,
ses réglages et son état « afficher dans la partition ». Ctrl+Y la resupprime.

Cela vaut que la suppression parte de la croix de l'en-tête ou du menu contextuel.

### 3.4 Annuler / rétablir

Dupliquer, monter, descendre et supprimer une piste sont des modifications du morceau au même titre
que les autres : chacune compte pour **une seule** entrée d'annulation, est annulable par Ctrl+Z,
rétablissable par Ctrl+Y, et marque le morceau comme modifié.

Annuler une duplication doit faire disparaître la copie **et** son contenu propre : après annulation
puis réenregistrement, le fichier ne doit pas être plus lourd qu'avant la duplication.

### 3.5 Valeurs par défaut

| Élément | Valeur |
|---|---|
| Nom de la copie | « <nom d'origine> (copie) », numéroté en cas de collision |
| Emplacement de la copie | juste en dessous de la piste d'origine |
| Sélection après duplication | la copie |
| Sélection après montée/descente | la piste déplacée |
| Confirmation avant suppression | aucune (l'action est annulable) |
| Nombre de pistes maximum | aucune limite imposée |

## 4. Cas limites et compatibilité

### 4.1 La piste d'accords

La piste « Accords » est **permanente et épinglée en bas** du morceau. Elle est traitée à part :

- elle ne peut être **ni dupliquée** (il ne doit jamais y avoir deux pistes d'accords), **ni
  supprimée**, **ni déplacée** ;
- aucune autre piste ne peut passer **en dessous** d'elle : « Descendre » est grisé pour la piste
  immédiatement au-dessus d'elle, exactement comme pour la dernière piste d'un morceau sans piste
  d'accords.

### 4.2 Pistes de batterie

Une piste de batterie se duplique comme les autres : la copie garde le **kit** choisi et tous ses
motifs. Elle reste une piste de batterie (on ne convertit pas de type au passage).

### 4.3 Morceaux générés (structure / orchestrateur / IA)

Certaines commandes de régénération visent une piste **par son nom** (« Accompagnement », par
exemple). La copie portant un nom différent (« Accompagnement (copie) »), ces commandes continuent de
viser l'**original** : la copie devient une piste ordinaire, non pilotée par le générateur. C'est le
comportement voulu — la copie sert justement à figer une version.

De même, dupliquer ou déplacer une piste ne modifie **ni la grille d'accords**, ni le thème, ni les
sections d'un morceau généré.

### 4.4 Piste vide

Dupliquer une piste sans aucun bloc produit une piste vide portant les mêmes réglages : c'est un moyen
légitime de créer une piste « préréglée ». Aucun message.

### 4.5 Morceau très lourd

Dupliquer une piste de plusieurs centaines de blocs peut prendre un instant. L'opération doit rester
**perceptiblement immédiate** (pas de figeage long, pas d'écran blanc) et ne doit pas dégrader la
lecture si elle est déclenchée pendant que le morceau joue.

### 4.6 Duplication pendant la lecture

Les commandes restent utilisables pendant la lecture. Elles ne l'interrompent pas ; la piste ajoutée
ou déplacée est prise en compte à la **lecture suivante** (même règle que les autres modifications de
structure en cours de lecture).

### 4.7 Édition en cours

Si l'éditeur du bas est ouvert sur un bloc de la piste concernée, la modification en cours est
**validée** avant l'opération (rien n'est perdu). Après une suppression, l'éditeur se vide ; après une
duplication ou un déplacement, il continue d'afficher le bloc d'origine.

### 4.8 Fichiers existants (compatibilité ascendante des `.sq`)

Aucune information nouvelle n'est enregistrée dans le morceau : la duplication ajoute une piste
ordinaire, le déplacement change seulement l'ordre des pistes déjà enregistrées.

- Un `.sq` créé **avant** cette fonctionnalité s'ouvre exactement comme aujourd'hui.
- Un `.sq` enregistré **après** l'avoir utilisée reste lisible par une version antérieure de
  l'application.
- La lecture, la partition et tous les exports (MIDI, MuseScore, MusicXML, PDF, audio) d'un morceau
  dont on a seulement **déplacé** les pistes doivent produire le même rendu sonore qu'avant (l'ordre
  des portées ou des pistes exportées peut suivre le nouvel ordre, mais aucune note ne change).

### 4.9 Langues

Tous les textes visibles introduits (les quatre entrées du menu contextuel, le suffixe « (copie) »,
les éventuelles infobulles) doivent exister dans les **sept langues** de l'application. Aucun texte
codé en dur, aucune clé brute affichée à l'écran.

## 5. Hors périmètre

Explicitement **non traité** dans cette version — à conserver comme suites possibles :

- **Réordonner par glisser-déposer** de l'en-tête de piste : seules les commandes Monter / Descendre
  sont fournies.
- **Dupliquer avec transposition** (« doubler à l'octave inférieure ») : la copie est à l'identique,
  l'utilisateur transpose ensuite avec les outils existants.
- **Copier une piste d'un morceau à un autre** (entre onglets), et copier/coller une piste par le
  presse-papiers.
- **Grouper / plier plusieurs pistes** en une famille, dossiers de pistes.
- **Réordonner depuis le mixeur** : le mixeur reflète l'ordre, il ne le change pas.
- **Confirmation de suppression** : la suppression reste immédiate, l'annulation fait office de filet.
- **Rendre annulables** d'autres opérations qui ne le seraient pas encore ailleurs dans l'application :
  seule la suppression de piste est traitée ici.
- **Duplication de la piste d'accords** et pistes d'accords multiples.

## 6. Critères d'acceptation

Vérifications observables, à conduire dans l'éditeur de morceau, sur un morceau comportant au moins
trois pistes dont une batterie.

**Menu**

1. Un clic droit sur l'en-tête d'une piste la sélectionne et ouvre un menu à quatre commandes :
   Dupliquer la piste, Monter, Descendre, Supprimer la piste.
2. Sur la **première** piste, « Monter » est grisé ; sur la **dernière** piste déplaçable,
   « Descendre » est grisé.
3. Le menu s'ouvre également sur un en-tête replié, avec les mêmes commandes.

**Duplication**

4. « Dupliquer la piste » sur une piste nommée « Mélodie » fait apparaître, **juste en dessous**, une
   piste « Mélodie (copie) » sélectionnée.
5. La copie contient **le même nombre de blocs**, aux **mêmes mesures**, avec les mêmes longueurs que
   l'original (comparaison visuelle des deux lanes : les boîtes sont alignées).
6. La copie a le même instrument, le même volume, le même panoramique, les mêmes états muet/solo, la
   même courbe de volume, le même état replié et la même case « partition » que l'original.
7. **Indépendance** : ouvrir un bloc de la copie, y modifier une note, revenir sur le bloc
   correspondant de l'original → l'original est **inchangé** ; et réciproquement.
8. Dupliquer deux fois la même piste produit « Mélodie (copie) » puis « Mélodie (copie 2) ».
9. Dupliquer une piste de **batterie** produit une piste de batterie conservant le même kit et les
   mêmes motifs.
10. Enregistrer, fermer, rouvrir : la copie est là, à sa place, avec son nom et son contenu.

**Ordre**

11. « Descendre » sur la première piste l'échange avec la deuxième ; l'ordre affiché et l'ordre du
    **mixeur** reflètent tous deux le changement.
12. Monter puis descendre la même piste la ramène exactement à sa position de départ.
13. Aucune piste ne peut être placée **sous la piste d'accords** : « Descendre » est indisponible pour
    la piste qui la précède immédiatement.
14. Après un déplacement, la lecture produit le même résultat qu'avant (mêmes blocs joués aux mêmes
    endroits).

**Piste d'accords**

15. Le menu contextuel de la piste d'accords ne propose ni duplication, ni suppression, ni
    déplacement (ou les propose grisés) ; il n'existe jamais deux pistes d'accords.

**Annulation**

16. Ctrl+Z juste après une duplication supprime la copie ; Ctrl+Y la remet à l'identique (nom,
    contenu, réglages).
17. Ctrl+Z juste après un déplacement remet la piste à sa position précédente ; **un seul** Ctrl+Z
    suffit.
18. Supprimer une piste contenant des blocs, puis Ctrl+Z : la piste **revient à sa place d'origine**
    avec tous ses blocs et ses réglages ; Ctrl+Y la resupprime. Vérifier depuis la croix de l'en-tête
    **et** depuis le menu contextuel.
19. Dupliquer, annuler, puis enregistrer : le fichier obtenu n'est pas plus lourd que le même morceau
    enregistré avant la duplication (aucun contenu orphelin laissé derrière).

**Compatibilité**

20. Ouvrir un `.sq` créé avant la fonctionnalité : aucune erreur, aucun message, comportement
    identique.
21. Sur un morceau **généré** (structure / orchestrateur), dupliquer la piste d'accompagnement puis
    relancer la régénération correspondante : c'est la piste **d'origine** qui est régénérée, la copie
    reste intacte.
22. Les exports (MIDI, MuseScore, MusicXML, audio, PDF) d'un morceau dont on a seulement déplacé les
    pistes contiennent les mêmes notes qu'avant le déplacement.

**Langues**

23. Basculer l'application dans les sept langues : les quatre entrées du menu contextuel et le suffixe
    du nom de copie sont traduits (aucune clé brute, aucun texte français résiduel dans les autres
    langues).
