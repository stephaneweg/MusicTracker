# Export MusicXML — analyse fonctionnelle

## 1. Le besoin

Aujourd'hui, pour sortir une **partition** d'un morceau, l'utilisateur a deux portes :

- **MuseScore (.mscx)** — un format **natif et propriétaire à MuseScore**. Il ne s'ouvre nulle part
  ailleurs : ni Finale, ni Sibelius, ni Dorico, ni les éditeurs en ligne, ni les liseuses de partition
  sur tablette, ni les outils de gravure des éditeurs de musique.
- **PDF** — une image figée : lisible et imprimable, mais impossible à retoucher, à transposer ou à
  extraire en parties séparées.

Il manque le format d'**échange** universel de la notation musicale : **MusicXML**. C'est le format que
tous les logiciels de partition savent lire et écrire, celui qu'on envoie à un copiste, à un professeur,
à un autre musicien, ou qu'on dépose sur un site de partitions.

**À qui ça sert.** À l'utilisateur qui compose ou arrange dans l'application et qui veut ensuite :

- graver proprement sa partition dans le logiciel de notation de son choix (pas seulement MuseScore) ;
- envoyer une partie séparée à un autre instrumentiste qui n'utilise pas le même logiciel ;
- lire sa partition sur une liseuse ou une tablette qui accepte le MusicXML ;
- archiver son travail dans un format ouvert, documenté et pérenne — plutôt que dans le format interne
  d'une seule application.

Le gain est disproportionné par rapport à l'effort : l'application **sait déjà** transformer une piste en
partition (elle l'affiche à l'écran, l'imprime en PDF et l'écrit en `.mscx`). Il s'agit d'écrire cette
même partition dans un troisième habillage, universellement lu.

Au passage, cette nouvelle sortie doit corriger une **faiblesse musicale** de l'export `.mscx` actuel :
une note qui dépasse la barre de mesure y est **coupée** (une blanche commencée au 4e temps d'une mesure
à 4/4 devient une noire, la suite disparaît). Sur un format d'échange destiné à être lu par un
instrumentiste, ce n'est pas acceptable : la note doit être **liée** par-dessus la barre.

## 2. Où ça vit dans l'interface

Dans l'éditeur de morceau (la timeline), menu **Export**, qui contient déjà `MIDI…`,
`MuseScore (.mscx)…`, `Audio…` et l'export PDF de la partition.

Une **entrée supplémentaire** y est ajoutée : **`MusicXML (.musicxml)…`**, placée juste après
`MuseScore (.mscx)…` (les deux exports de partition symbolique restent voisins).

Comme les entrées voisines, elle porte une **infobulle** expliquant à quoi sert le format (« Exporter la
partition dans le format d'échange lu par tous les logiciels de notation »). L'infobulle et tous les
messages de l'action suivent la langue de l'application (les 7 langues). Le libellé du menu, lui, est un
nom de format et reste tel quel, comme `MIDI…` et `MuseScore (.mscx)…`.

## 3. Comportement attendu, vu de l'utilisateur

### 3.1 Quelles pistes sont exportées

**Exactement la même règle que l'export MuseScore existant** — l'utilisateur ne doit pas avoir à
apprendre deux conventions :

1. Si des pistes sont **cochées pour la partition** (la case ♫ de l'en-tête de piste), ce sont
   celles-là, dans l'ordre de la timeline.
2. Si **aucune** n'est cochée, toutes les pistes mélodiques du morceau sont exportées, dans l'ordre.
3. Les pistes de **batterie** sont exclues dans les deux cas (la percussion demande une portée de
   percussion, qui n'est pas au programme ici — voir *Hors périmètre*).
4. La piste d'**accords** est traitée comme une piste mélodique normale : elle donne une portée avec ses
   notes, comme elle apparaît déjà dans la vue partition et dans l'export `.mscx`.

### 3.2 Le geste

1. L'utilisateur clique **Export ▸ MusicXML (.musicxml)…**.
2. Le sélecteur de fichier de l'application (celui déjà utilisé partout, au thème de l'app) s'ouvre en
   mode enregistrement, filtré sur les fichiers MusicXML, avec l'extension `.musicxml` par défaut.
3. Le **nom proposé** est le nom du fichier du morceau, sans extension et sans dossier — même
   convention que l'export MuseScore. Si le morceau n'a jamais été enregistré, un nom générique
   traduit (« Partition ») est proposé.
4. L'utilisateur valide → le fichier est écrit, et un message de confirmation indique le chemin
   complet du fichier créé.
5. S'il annule → **rien** ne se passe : aucun fichier écrit, aucun message.

Le morceau lui-même n'est **pas modifié** : exporter ne salit pas le projet (pas d'astérisque « modifié »
qui apparaît), ne crée pas d'entrée d'annulation, et ne change ni le fichier `.sq`, ni les pistes cochées,
ni la position de lecture.

### 3.3 Ce que contient la partition exportée

Ouverte dans un logiciel de notation, la partition doit être **la même** que celle que l'application
affiche à l'écran et imprime en PDF. Concrètement, le fichier porte :

- **Le titre** du morceau, en haut de la première page : le nom du fichier, sans extension, les `_`
  remplacés par des espaces (même règle que le PDF et le `.mscx`).
- **Une portée par piste exportée**, dans l'ordre de la timeline, avec le **nom de la piste** en tête de
  système (nom long) et, à défaut de nom, une numérotation lisible (« Voix 1 », « Voix 2 »…).
- **L'instrument** de chaque piste, pour que le logiciel destinataire joue le bon timbre à la relecture.
- **La clé** de chaque portée, celle que l'application a choisie ou que l'utilisateur a fixée
  (sol / fa / ut 3e / ut 4e).
- **L'armure** du morceau, et pour les instruments transpositeurs, l'armure **écrite** de l'instrument,
  avec la transposition déclarée pour que le logiciel destinataire sonne juste.
- **Le chiffrage de mesure** du morceau (4/4, 3/4, 6/8…), tel qu'affiché dans l'application — un morceau
  en 6/8 s'exporte en 6/8, pas en 4/4 déguisé.
- **Le tempo initial** du morceau, sous forme d'indication métronomique sur la première mesure.
- **Les notes, accords et silences** de chaque piste, mesure par mesure, jusqu'à la fin du morceau.
- **Les altérations correctement orthographiées** selon la tonalité : en ré mineur, la sensible s'écrit
  `do♯` et non `ré♭` — même orthographe que dans la vue partition et l'export `.mscx`.
- **Les accords roulés** (arpégés) détectés par l'application portent leur signe d'arpège, comme dans
  l'export `.mscx`.

### 3.4 Les durées et les liaisons

C'est le point de qualité de cette feature.

- Une note dont la durée correspond à une **figure standard** (ronde, blanche, noire, croche, double,
  triple, avec ou sans point) s'écrit comme **une seule note**.
- Une note dont la durée **ne** correspond à aucune figure standard (par exemple cinq croches) s'écrit
  comme une **suite de figures reliées par des liaisons de prolongation** — pas comme plusieurs notes
  réattaquées.
- Une note qui **traverse une barre de mesure** est écrite comme deux notes (ou plus) **liées**
  par-dessus la barre. Elle n'est ni tronquée, ni réattaquée. *(C'est ici que le nouvel export dépasse
  l'export `.mscx` actuel.)*
- Les **silences** remplissent tous les creux : chaque mesure de chaque portée est complète, sans trou ni
  débordement — condition pour que le fichier s'ouvre sans avertissement dans un logiciel de notation.
- Les **triolets et autres divisions irrégulières** sont approchés sur la grille des figures standard
  (jusqu'à la triple croche), sans crochet de triolet — même limite que l'export `.mscx`, assumée et
  documentée ci-dessous.

### 3.5 Alignement des portées

Toutes les portées ont le **même nombre de mesures** : celui de la piste la plus longue. Les pistes plus
courtes sont complétées par des **mesures de silence**, de sorte que les systèmes restent alignés et que
la fin du morceau tombe au même endroit sur toutes les portées.

## 4. Valeurs par défaut, récapitulées

| Élément | Défaut |
|---|---|
| Pistes exportées | Les pistes cochées ♫ ; sinon toutes les pistes non-batterie |
| Nom de fichier proposé | Nom du morceau sans extension ; « Partition » si jamais enregistré |
| Extension | `.musicxml` |
| Titre dans la partition | Nom du fichier, `_` → espaces |
| Nom de portée | Nom de la piste ; « Voix N » si vide |
| Nombre de mesures | Celui de la piste la plus longue |
| Tempo | Tempo initial du morceau |

## 5. Cas limites

| Situation | Comportement attendu |
|---|---|
| Morceau **sans aucune piste** | Message explicite (« aucune piste à exporter »), pas de sélecteur de fichier, pas de fichier écrit |
| **Seules des pistes de batterie** sont cochées (ou le morceau n'a que de la batterie) | Message explicite « aucune piste mélodique à exporter », pas de fichier écrit — même message et même comportement que l'export MuseScore |
| Piste cochée mais **vide** (aucune note) | Une portée entièrement en silences, du bon nombre de mesures — pas de portée manquante, pas d'erreur |
| Morceau **vide côté notes** (toutes les pistes vides) | Un fichier valide contenant au moins une mesure de silence par portée |
| Piste dont les notes **dépassent** la fin théorique du morceau | La partition va jusqu'à la dernière note ; aucune note perdue |
| Nom de piste ou nom de fichier contenant des caractères spéciaux (`&`, `<`, `"`, accents, kanji) | Le fichier reste valide et s'ouvre correctement ; les caractères apparaissent tels quels |
| Fichier **déjà existant** | Le sélecteur d'enregistrement demande confirmation avant écrasement, comme pour les autres exports |
| Chemin **non inscriptible** (disque plein, fichier verrouillé, dossier protégé) | Message d'erreur lisible mentionnant la cause ; l'application ne se ferme pas et le projet reste intact |
| Morceau avec **swing** activé | La partition garde des croches **égales** — le swing est une interprétation, pas une écriture. Même règle que l'export MIDI et que la vue partition |
| Morceau avec **levée** (anacrouse) | La levée est écrite comme une **mesure complète**, silences en tête — même comportement que l'export MuseScore actuel. Ce n'est pas idéal ; c'est cohérent, et c'est noté comme limite connue |
| Morceau avec **changements de tempo** | Seul le tempo initial est écrit ; les changements ultérieurs ne sont pas notés (voir *Hors périmètre*) |
| Morceau à **mesure ternaire affichée** (4/4 en triolets affiché en 12/8) | La partition exporte l'affichage, comme le PDF et le `.mscx` |
| **Instrument transpositeur** (clarinette, trompette…) | Les notes écrites correspondent à ce que voit l'instrumentiste ; la transposition est déclarée pour que la relecture sonne à la bonne hauteur |
| Morceau très long / beaucoup de pistes | L'export reste une opération courte ; s'il devait durer, l'application ne doit pas paraître figée sans explication |

## 6. Compatibilité avec les projets existants

**Le format `.sq` n'est pas modifié** : cette feature n'ajoute aucun réglage à sauvegarder, aucun champ,
aucune migration. C'est une **sortie** en lecture seule du morceau.

Conséquences vérifiables :

- Un `.sq` créé avant cette feature s'ouvre à l'identique et s'exporte en MusicXML sans rien demander de
  plus à l'utilisateur.
- Un `.sq` enregistré après cette feature reste lisible par une version antérieure de l'application.
- Les exports existants (MIDI, MuseScore, PDF, audio) ne changent **ni de comportement ni de résultat**.
  En particulier, l'export `.mscx` n'est pas retouché dans cette session — les deux exports coexistent, le
  `.mscx` restant la voie directe vers MuseScore et le MusicXML la voie portable.

## 7. Hors périmètre (explicitement)

Ne fait **pas** partie de cette feature :

- **Import** MusicXML (lire un fichier MusicXML dans l'application). Uniquement l'export.
- **MusicXML compressé** (`.mxl`, l'archive zip). Seul le fichier texte est produit.
- **Portées de percussion** : les pistes de batterie restent exclues, comme dans l'export `.mscx`.
- **Triolets et divisions irrégulières notés** (crochets de nolet) : approximés, comme aujourd'hui.
- **Nuances, articulations, phrasés, pédale, reprises, barres de reprise, renvois, indications de
  section** : non écrits.
- **Changements de tempo et de chiffrage de mesure en cours de morceau** : seuls les réglages initiaux
  sont écrits.
- **Paroles**, **chiffrages d'accords**, **tablature**.
- **Mise en page** (sauts de système, de page, largeurs) : laissée au logiciel destinataire.
- **Voix multiples sur une portée** : les notes simultanées d'une piste sont écrites comme des accords,
  comme dans l'export `.mscx`.
- **Correction de la levée** dans les exports de partition : c'est une amélioration à part entière, à
  traiter pour les trois sorties (PDF, `.mscx`, MusicXML) en même temps.
- **Ajout des liaisons de prolongation à l'export `.mscx`** : souhaitable, mais c'est une modification
  d'un export existant et donc un risque de régression ; à faire dans une session dédiée.

## 8. Critères d'acceptation

Vérifications observables, dans l'ordre où on les ferait à la main.

1. **L'entrée existe.** Le menu Export de l'éditeur de morceau contient `MusicXML (.musicxml)…` juste
   après `MuseScore (.mscx)…`, avec une infobulle.
2. **Traduction.** En basculant l'application dans chacune des 7 langues, l'infobulle et tous les
   messages liés à l'action (confirmation, erreurs, « aucune piste… ») s'affichent traduits, sans clé
   brute ni texte anglais résiduel.
3. **Le fichier se crée.** Sur un morceau à au moins deux pistes mélodiques, l'action propose un nom
   dérivé du morceau, et après validation le fichier existe sur le disque et le message de confirmation
   affiche son chemin.
4. **Le fichier est valide.** Le fichier produit est un XML bien formé et conforme au schéma MusicXML ;
   il s'ouvre **sans message d'erreur ni d'avertissement** dans MuseScore.
5. **Fidélité.** Ouvert dans un logiciel de notation, il montre le **même nombre de portées** que de
   pistes exportées, avec les mêmes **noms**, les mêmes **clés**, la même **armure** et le même
   **chiffrage de mesure** que la vue partition de l'application, et **les mêmes notes aux mêmes
   endroits** (contrôle mesure par mesure sur un morceau court).
6. **Liaison par-dessus la barre.** Sur un morceau à 4/4 contenant une note commencée au 4e temps et
   durant deux temps : la partition exportée montre **une noire liée à une noire** par-dessus la barre —
   pas une note tronquée, pas deux notes réattaquées.
7. **Durée non standard.** Une note de cinq croches apparaît comme des figures **liées** totalisant cinq
   croches, et non comme plusieurs attaques.
8. **Mesures complètes.** Aucune mesure de la partition n'est signalée comme trop courte ou trop longue
   par le logiciel destinataire.
9. **Alignement.** Avec une piste deux fois plus courte qu'une autre, les deux portées ont le même
   nombre de mesures, la plus courte se terminant par des mesures de silence.
10. **Orthographe des altérations.** Sur un morceau en ré mineur contenant la sensible, celle-ci est
    écrite `do♯` et non `ré♭`.
11. **Mesure composée.** Un morceau en 6/8 s'exporte avec un chiffrage 6/8 et des mesures de la bonne
    longueur.
12. **Sélection des pistes.** (a) Avec deux pistes cochées ♫ sur quatre, seules ces deux-là sont dans le
    fichier. (b) Sans aucune coche, toutes les pistes non-batterie y sont. (c) Une piste de batterie
    n'apparaît jamais.
13. **Refus propre.** Sur un morceau ne contenant que de la batterie, l'action affiche le message
    « aucune piste mélodique » et n'écrit aucun fichier.
14. **Annulation propre.** En annulant le sélecteur de fichier, aucun fichier n'est créé et aucun message
    n'apparaît.
15. **Erreur propre.** En visant un chemin non inscriptible, un message d'erreur lisible s'affiche et
    l'application reste utilisable.
16. **Projet intact.** Après un export, le morceau n'est pas marqué comme modifié, l'annulation (Ctrl+Z)
    ne propose rien de nouveau, et réenregistrer le `.sq` produit un fichier identique à celui d'avant
    l'export.
17. **Pas de régression.** Les exports MIDI, MuseScore et PDF du même morceau donnent exactement le même
    résultat qu'avant la feature.
