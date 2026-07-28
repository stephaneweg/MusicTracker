# Rythmes euclidiens — analyse fonctionnelle

## 1. Le besoin

L'éditeur de batterie propose aujourd'hui deux voies, et rien entre les deux :

- **le catalogue** — des motifs figés (Rock basique, Bossa nova, Reggae one-drop…) qu'on applique tels quels ;
- **le dessin à la main** — chaque coup placé un par un dans la grille.

Il manque le registre intermédiaire : **générer un rythme à partir de quelques nombres, puis l'explorer**. C'est précisément ce que donne la répartition euclidienne — placer K coups aussi régulièrement que possible sur N pas. Avec trois réglages seulement (K, N, décalage), on couvre une famille étonnamment large de rythmes traditionnels : E(3,8) est le *tresillo* cubain, E(5,8) le *cinquillo*, E(2,5) une cellule de tango, E(7,16) un motif ouest-africain. Ce n'est pas une curiosité mathématique : c'est un raccourci vers des rythmes que l'oreille reconnaît.

L'intérêt pratique est l'**exploration**. Changer K de 3 à 5, ou décaler le motif d'un pas, produit instantanément une variante cohérente. Obtenir la même chose au dessin demande de repositionner chaque coup.

## 2. À qui ça sert

À l'utilisateur qui compose une batterie et cherche un groove sans savoir précisément lequel. Aussi à celui qui veut sortir des grooves binaires occidentaux du catalogue : les rythmes euclidiens excellent sur les cellules impaires (5, 7, 9 pas) difficiles à écrire à la main.

Aucune connaissance du mot « euclidien » n'est requise, et c'est une contrainte de conception : **l'interface parle de coups et de pas, jamais d'algorithme**.

## 3. Comportement attendu

### 3.1 Où ça vit

Dans l'**éditeur de batterie**, sous les listes Catégorie / Motif, à côté du bouton « Personnaliser » : un bloc **« Répartir régulièrement… »** qui ouvre un petit panneau de génération.

Le choix est délibéré : la génération produit un motif *modifiable*, exactement comme « Personnaliser » aujourd'hui. Elle ne crée pas une catégorie de motifs figés de plus.

### 3.2 Les réglages

| Réglage | Rôle | Défaut |
|---|---|---|
| **Instrument** | la ligne de percussion à remplir (grosse caisse, caisse claire, charleston…) | Grosse caisse |
| **Coups** (K) | combien de frappes | 3 |
| **Pas** (N) | sur combien de positions elles se répartissent | 8 |
| **Décalage** | fait tourner le motif de 0 à N−1 pas | 0 |
| **Unité** | durée d'un pas : croche, double-croche, ou triolet de croche | Croche |

### 3.3 L'aperçu, élément central

Le panneau affiche le motif en clair, **avant** de l'appliquer :

```
● · · ● · · ● ·        3 coups sur 8 pas — « tresillo »
```

Les positions pleines et vides sont lisibles d'un coup d'œil, et le nom traditionnel s'affiche quand le motif en a un. C'est ce qui rend la feature utilisable sans rien connaître à la théorie : on tourne les molettes, on regarde, on écoute.

### 3.4 Ce que fait « Appliquer »

Le motif est écrit **dans la ligne choisie uniquement**. Les autres lignes ne bougent pas. Le motif obtenu est ensuite **modifiable à la main** dans la grille, comme n'importe quel motif personnalisé.

Cette combinaison est le cœur de la feature : on **superpose** en répétant l'opération sur d'autres lignes — grosse caisse E(3,8), caisse claire E(2,8) décalée de 4, charleston E(7,16) — puis on retouche à la main ce qui doit l'être. C'est ainsi qu'on construit un groove euclidien en pratique.

### 3.5 Longueur et cycles décalés

Le cycle généré dure **N × unité**. Il se répète pour remplir toute la longueur du module.

Quand cette durée ne tombe pas juste sur la mesure — E(2,5) en croches fait 2,5 temps —, le motif **se décale d'une mesure à l'autre** au lieu de se recaler. Ce n'est pas un défaut : c'est le comportement attendu d'un séquenceur euclidien, et la source de motifs tournants impossibles à écrire autrement. L'aperçu doit signaler ce cas (« ce motif se décale d'une mesure à l'autre »), pour que ce soit un choix et non une surprise.

## 4. Cas limites

| Situation | Comportement |
|---|---|
| K = 0 | la ligne choisie est vidée ; les autres restent |
| K ≥ N | tous les pas sont frappés (K ramené à N) |
| N = 1 | un seul coup, au début du cycle |
| Décalage ≥ N | ramené modulo N |
| La ligne contient déjà des coups | ils sont **remplacés** sur cette ligne (annulable par Ctrl+Z) |
| Module issu du catalogue, non personnalisé | il bascule en motif personnalisé, comme le fait « Personnaliser » |
| Mesure ternaire ou à 3 temps | rien de particulier : le cycle reste N × unité et se décale s'il ne tombe pas juste |

## 5. Compatibilité

**Aucun changement de format de fichier.** Le résultat est un motif de batterie ordinaire, du même type que ceux dessinés à la main. Un projet enregistré après usage de la génération s'ouvre dans une version antérieure de l'application sans perte ni message : rien ne distingue un motif généré d'un motif dessiné.

## 6. Hors périmètre

- **Rythme d'accords euclidien** — placer les attaques d'un accompagnement selon E(K,N). Intéressant, mais l'accompagnement d'accords repose sur une liste d'articulations figées, d'une autre nature que la liste de notes libre de la batterie : c'est un travail distinct. *Voir §8.*
- **Temps forts de ligne mélodique** par E(K,N) — même raison.
- Motifs euclidiens **enregistrés dans le catalogue** en tant que tels (avec leurs paramètres réédilables) : le motif généré devient une liste de coups, il perd ses K/N. Acceptable pour une première version.
- Génération **multi-lignes en une fois** : on répète l'opération ligne par ligne.
- Accentuation (vélocité) des coups générés : l'application ne stocke pas de vélocité par note.

## 7. Critères d'acceptation

1. E(3,8) en croches sur la grosse caisse produit des coups aux temps 1, 2½ et 4 — le tresillo.
2. E(5,8) produit 5 coups, E(2,5) en produit 2 : le nombre de coups par cycle vaut toujours K.
3. Les intervalles entre coups consécutifs ne prennent que **deux valeurs**, différant de 1 pas (c'est la définition de la répartition régulière) — vérifiable pour tout couple (K,N).
4. Un décalage de r fait tourner le motif de r pas, sans changer le nombre de coups.
5. Appliquer sur la caisse claire ne modifie aucun coup de grosse caisse.
6. Après application, chaque coup reste effaçable et déplaçable à la main dans la grille.
7. Ctrl+Z restaure exactement l'état antérieur, en **une seule** entrée d'annulation.
8. Un projet enregistré après génération se recharge à l'identique.
9. E(2,5) en croches sur un module de 4 mesures à 4 temps : le motif se décale visiblement de mesure en mesure, et l'aperçu l'a annoncé.
10. K = 0 vide la ligne visée et laisse les autres intactes.
11. L'aperçu affiche le nom traditionnel pour E(3,8) et E(5,8).
12. Les libellés sont traduits dans les 7 langues.

## 8. Suite possible

Si la génération euclidienne convainc à l'usage sur la batterie, l'étendre au **rythme d'accords** est la suite naturelle : une articulation « Réparti (euclidien) » où K attaques se répartissent sur la durée de l'accord. C'est ce que suggérait la note de projet d'origine. À traiter séparément, une fois le vocabulaire d'interface validé sur le cas le plus audible.
