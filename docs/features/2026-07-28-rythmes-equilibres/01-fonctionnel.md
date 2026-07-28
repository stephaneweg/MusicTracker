# Rythmes équilibrés — analyse fonctionnelle

Évolution du module **batterie polyrythmique** (livré). Source : transcription vidéo fournie par l'utilisateur, dont le contenu mathématique a été **vérifié numériquement** avant rédaction (§5).

## 1. De quoi s'agit-il

Un rythme se représente comme un polygone inscrit dans un cercle : le cercle est le cycle, chaque coup un sommet. On peut alors regarder la **forme** du polygone.

- Un rythme **euclidien** — ce que le module fait aujourd'hui — *approxime un polygone régulier* : les coups sont aussi également espacés que possible.
- Un rythme **équilibré** est autre chose : son **centre de masse coïncide avec le centre du cercle**. Le polygone tient en équilibre sur une pointe.

Ce ne sont **pas** les mêmes rythmes, et c'est ce qui rend l'idée intéressante. Vérification faite : E(3,8), le tresillo, a un centre de masse à 0,414 du centre — il est euclidien mais **pas** équilibré. Aucun des motifs euclidiens testés ne l'est.

Le résultat central (2018) est qu'un rythme équilibré est toujours une **somme signée de polygones réguliers** : on en additionne, et on peut aussi en **soustraire**. Soustraire signifie musicalement qu'on entend un polygone régulier *à travers les silences* plutôt que dans les coups.

## 2. Pourquoi c'est intéressant pour l'application

L'argument n'est pas mathématique, il est **créatif** : la contrainte. Un logiciel qui n'autorise *que* des rythmes équilibrés force à composer autrement — comme Steve Reich s'était contraint à des motifs très simples et en a tiré le déphasage. Des albums ont été faits ainsi.

Le module polyrythmique actuel n'a aucune contrainte : chaque calque a son K, son N et son unité, indépendamment des autres. C'est très libre, donc parfois informe. Un **mode équilibré** apporterait la contrainte qui structure — et il se greffe sur la roue déjà construite, qui est précisément la représentation circulaire dont cette théorie a besoin.

## 3. Comportement proposé

### 3.1 Un mode, pas un module de plus

Le module polyrythmique gagne un choix de **mode** :

- **Libre** (actuel) — chaque calque a son propre cycle E(K,N) ; les cycles se déphasent.
- **Équilibré** (nouveau) — tous les calques partagent **une même subdivision n** du cercle, et chaque calque est un **polygone régulier** de k côtés (k devant diviser n), avec une rotation, et un **signe + ou −**.

En mode équilibré, le résultat est équilibré **par construction** : l'utilisateur ne peut pas se tromper, c'est le principe même de la contrainte.

### 3.2 Ce qu'un calque devient

| Réglage | Libre (actuel) | Équilibré |
|---|---|---|
| Instrument | oui | oui |
| Coups / Pas | K et N libres | **k côtés**, parmi les diviseurs de n |
| Décalage | oui | oui (toute rotation préserve l'équilibre) |
| Unité | par calque | **commune** : n est partagé |
| Signe | — | **+ ajoute, − retire** |

Un calque négatif ne peut retirer que des coups effectivement posés par les calques positifs ; l'interface le signale quand ce n'est pas le cas.

### 3.3 La roue montre l'équilibre

La roue existante gagne deux éléments :

- le **centre de masse** du rythme, sous forme d'un point ; en mode équilibré il est au centre, ce qui se voit immédiatement ;
- les calques négatifs dessinés **en creux** (contour seul), pour qu'on lise « ce polygone est entendu à travers les silences ».

### 3.4 Guider vers les n qui valent le coup

Tous les n ne se valent pas, et c'est contre-intuitif :

- pour que des rythmes équilibrés **non périodiques** existent, n doit avoir au moins 3 facteurs premiers dont 2 distincts → **12, 18, 20, 24, 28, 30…** ;
- un n en puissance de 2 (16, 32, 64) ne donne **que** des rythmes périodiques : la première moitié répète la seconde, sans intérêt ;
- les rythmes à **somme négative** n'apparaissent qu'à **n = 30 = 2×3×5**, le premier produit de trois premiers distincts, puis à n = 42.

Le sélecteur de n doit donc **dire ce qu'on peut espérer** de chaque valeur, plutôt que de proposer une liste muette où 16 et 30 se ressembleraient.

## 4. Hors périmètre

- Le calcul exhaustif de *tous* les rythmes équilibrés d'un n donné (recherche coûteuse) : on construit par polygones, on ne cherche pas.
- Les hauteurs (le module reste percussif). L'idée « un polygone = une note », qui fait émerger une mélodie, relèverait de la ligne mélodique.
- Import/export du format d'un autre logiciel.

## 5. Ce qui a été vérifié avant d'écrire ce document

Calculs faits sur les définitions, pas sur la foi de la transcription :

| Affirmation | Résultat |
|---|---|
| Les motifs euclidiens ne sont pas équilibrés | confirmé : E(3,8) et E(5,8) → centre à 0,414 ; E(2,5) → 0,618 ; E(7,16) → 0,199 |
| Un polygone régulier est équilibré | confirmé : centre exactement 0 pour 3, 4 et 6 côtés sur n = 12 |
| n = 12 : triangle + bipoint tourné donne un équilibré **non périodique** | confirmé : {0,1,4,7,8}, centre 0, aucune symétrie de rotation |
| n = 30 : il existe des équilibrés à **somme négative** | confirmé par recherche exhaustive sur pentagone + triangle − bipoint : **30 motifs**, soit **6 à rotation près** — exactement le nombre annoncé |

Exemple concret trouvé : **{5, 6, 12, 18, 24, 25}** sur n = 30 — pentagone + triangle décalé de 5, moins le bipoint. Centre de masse : 0.

**Réserve sur les noms.** La transcription est automatique et déforme les noms propres. Le chercheur à l'origine du concept est vraisemblablement **Andrew J. Milne** (Australie), avec **David Bulger** pour le théorème, et **Emmanuel Amiot** côté français ; le logiciel cité est vraisemblablement **XronoMorph**. À confirmer avant toute mention publique — le contenu mathématique, lui, est vérifié.

## 6. Critères d'acceptation

1. En mode équilibré, le centre de masse du rythme produit est nul (à 10⁻⁹ près) **quels que soient** les calques posés.
2. Toute rotation d'un calque préserve cette propriété.
3. Un polygone de k côtés n'est proposé que si k divise n.
4. Un calque négatif ne retire que des coups présents ; sinon l'interface le signale sans planter.
5. n = 16 (puissance de 2) n'expose que des combinaisons périodiques, et l'interface le dit.
6. Le point de centre de masse est visible sur la roue et se déplace en mode libre, reste au centre en mode équilibré.
7. Basculer libre ↔ équilibré ne perd pas les calques (conversion documentée, ou avertissement).
8. Un `.sq` enregistré en mode équilibré se recharge à l'identique.
9. Les libellés existent dans les 7 langues.

## 7. Pourquoi cette feature est un bon candidat automatisable

Elle a une propriété rare ici : **son critère de succès est numérique**. « Le centre de masse est nul » se vérifie par le calcul, sans oreille ni œil — contrairement à la plupart des features audio de ce projet, où le run automatisé doit conclure « non vérifié ». Le jugement humain ne reste requis que pour l'intérêt musical du résultat.
