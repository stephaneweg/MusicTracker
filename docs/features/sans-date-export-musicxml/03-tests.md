# Export MusicXML — compte rendu de tests

Rôle : testeur (chercher la faille, pas confirmer que ça marche).
Références : `01-fonctionnel.md` (critères d'acceptation), `02-technique.md` (plan de test §8).

**Verdict : VERT** — tout ce qui est vérifiable par exécution passe. Une réserve importante mais
**hors feature** est signalée au §8 (modification non liée présente dans l'arbre de travail).

---

## 1. Ce qui a été exécuté

| # | Test | Résultat |
|---|---|---|
| 1 | `MSBuild /t:Rebuild /p:Configuration=Debug` | **0 erreur, 0 avertissement** |
| 2 | `AutoTest\run.ps1 -Configuration Debug` (FlaUI) | **5/5 pass**, `AppCrashed: false` |
| 3 | 7 × `lang.xx.json` via `JavaScriptSerializer` | **7/7 parsent**, 3 clés neuves + 4 réutilisées présentes partout |
| 4 | Exporteur piloté directement (56 assertions structurelles) | **tout vert** |
| 5 | Balayage exhaustif 13 728 couples (attaque, durée) × 6 chiffrages | **tout vert** |
| 6 | Orthographe : 5 376 couples (tonalité, hauteur) | **tout vert** |
| 7 | Import réel dans MuseScore 3 (6 fichiers, sans tête) | **6/6 « … success! »**, aucun avertissement |
| 8 | Fidélité : relecture indépendante par MuseScore | **notes et durées identiques** |
| 9 | Compatibilité `.sq` ascendante **et** descendante vs binaire pré-feature | **octet pour octet identique** |
| 10 | Non-régression `.mscx` / `.mid` / sortie `ScoreBuilder` vs binaire pré-feature | **octet pour octet identique** |
| 11 | Performance (jusqu'à 192 000 notes) | 708 ms au pire, 169 ms sur un cas déjà énorme |

Méthode : l'exporteur n'a pas été simulé. Des pilotes C# ont été compilés **contre le
`KotonStudio.exe` réellement construit** et appellent `MusicXmlExporter.Export` ; les fichiers produits
sont ensuite relus avec `XmlReaderSettings { DtdProcessing = Parse; XmlResolver = null }` (la DTD n'est
donc jamais cherchée sur le réseau, cf. §2.3 de l'analyse technique).

---

## 2. Détail — plan de test §8.1

### 8.1.1 Build
`MSBuild.exe MusicTracker\MusicTracker.csproj /t:Rebuild /p:Configuration=Debug /v:minimal /nologo /m`
→ compile `MeltySynth.dll` puis `KotonStudio.exe`, **sans une seule ligne d'erreur ni d'avertissement**.

### 8.1.2 Les 7 fichiers de langue
Validés avec `JavaScriptSerializer` (et **non** `ConvertFrom-Json`, qui échoue à tort sur le couple
préexistant `AUDIO`/`Audio`). Les 7 se désérialisent ; aucun n'a de BOM.

Les 3 clés neuves — `ExportTheScoreAsMusicXML`, `ExportMusicXMLTermine`, `ErreurDExportMusicXML` — sont
présentes dans **fr en de it es nl pt**, avec une valeur non vide et différente de la clé, insérées
à leur place alphabétique. Les 4 clés réutilisées (`AucunePisteAExporter`,
`AucunePisteMelodiqueAExporterCoche`, `Partition`, `Voix2`) existaient déjà et n'ont pas été recréées.

Vérification **au-delà du fichier** : un pilote appelle `Loc.T(...)` sur les 7 clés dans les 7 langues via
le `LocalizationManager` réel. Les 49 résultats sont des traductions effectives — aucune clé brute
affichée, aucun résidu français dans les 6 autres langues. Le repli `Voix2` conserve bien son espace
final dans les 7 (`"Voix "`, `"Voice "`, `"Stimme "`, `"Voce "`, `"Voz "`, `"Stem "`, `"Voz "`), donc la
portée sans nom s'appelle « Voix 2 » et non « Voix2 ».

> Faux positif rencontré : au premier essai les 49 lookups renvoyaient la clé brute. Cause = mon pilote
> console n'avait pas le `.exe.config` de l'application, donc les redirections d'assemblage de
> `System.Text.Json` échouaient et `LoadMapFromFile` avalait l'exception. **Ce n'est pas un défaut du
> produit** ; avec le `.config` copié, tout se résout. À retenir pour les futurs harnais.

### 8.1.3 XML bien formé
Tous les fichiers produits (≈ 20, dont un de 44 Mo) se chargent sans exception. Ordre des éléments de
tête conforme : `work` → `identification` → `part-list` → `part+`. Aucun BOM (`UTF8Encoding(false)`).

### 8.1.4 Complétude des mesures — critère 8
**C'est le test le plus important, et il a été poussé jusqu'à l'exhaustivité.**

Pour chaque chiffrage, un balayage génère **une part par couple (attaque, durée)** sur toute la grille de
3 unités, sur deux mesures :

| Chiffrage | unités/mesure | couples testés |
|---|---|---|
| 4/4 | 96 | 2 048 |
| 3/4 | 72 | 1 152 |
| 6/8 | 72 | 1 152 |
| 12/8 | 144 | 4 608 |
| 5/4 | 120 | 3 200 |
| 7/8 | 84 | 1 568 |
| | | **13 728** |

Pour chacun des 13 728 cas, quatre invariants sont contrôlés :

1. somme des `<duration>` des notes **sans** `<chord/>` == `barUnits`, **dans chaque mesure** — zéro écart ;
2. la durée totale sonnante de la note == la durée demandée (**aucune note tronquée**) ;
3. exactement **une attaque**, tous les autres segments portent `tie type="stop"` ;
4. liaisons équilibrées (aucun `stop` orphelin, aucun `start` resté ouvert).

**Aucun échec.** L'invariant structurel annoncé au §3.5 de l'analyse technique est donc démontré, pas
supposé.

Cas ajoutés à la main, tous verts : projet dont toutes les pistes sont vides (1 mesure de silence par
part), liste de parts vide, `Part.Score == null`, 400 notes aléatoires qui se chevauchent, notes sur la
grille de triolet (1/6 de temps), chiffrages 1/4, 2/4, 9/8.

### 8.1.5 Alignement — critère 9
Toutes les `<part>` ont le même nombre de `<measure>` dans tous les tests. Sur T1, la piste 2 (moitié plus
courte) et la piste 4 (vide) se terminent bien par des mesures de silence, sur 5 mesures comme les autres.

Le nombre de mesures est bien piloté par la **fin réelle des notes** et pas seulement par `TotalBeats` :
une piste déclarée `TotalBeats = 4` mais portant une note aux temps 18-20 produit 5 mesures et **la note
n'est pas perdue** (correction annoncée au §3.3, vérifiée).

### 8.1.6 Liaisons appariées
Vérifié par part sur tous les fichiers, hauteur par hauteur. De plus, `<tie>` (sonore, avant `<voice>`) et
`<notations><tied>` (graphique) **concordent sur chaque note** — aucun cas où l'un est écrit sans l'autre.

L'ordre des enfants de `<note>` est contrôlé contre la séquence de la DTD
(`chord? , (pitch|rest) , duration , tie* , voice , type , dot* , accidental? , notations?`) sur
**toutes** les notes de tous les fichiers : aucune inversion, aucun enfant inconnu.

### 8.1.7 Liaison par-dessus la barre — critère 6
T1, 4/4 : note commencée au 4ᵉ temps et durant 2 temps.
→ dernière note de la mesure 1 = `<type>quarter</type>` + `tie type="start"` ;
→ première note de la mesure 2 = `<type>quarter</type>` + `tie type="stop"`, **même hauteur**.
Une noire liée à une noire. Ni troncature, ni réattaque. **C'est le gain de la feature sur le `.mscx`,
et il est effectif.**

Poussé plus loin : une note de 2, 5, 17 puis 64 mesures produit respectivement 2, 5, 17 et 64 segments
liés avec **une seule attaque** et la durée totale exacte.

### 8.1.8 Durée non standard — critère 7
Note de 5 croches depuis le temps fort → chaîne liée totalisant **60 unités** avec **une seule attaque**.
Après relecture par MuseScore : **`half + eighth`**, soit une blanche liée à une croche. C'est
exactement ce qu'un musicien écrirait.

### 8.1.9 Orthographe des altérations — critère 10
T1 en ré mineur : la sensible sort en `<step>C</step><alter>1</alter>` (2 occurrences, les deux moitiés
de la note liée) et **jamais** `<step>D</step><alter>-1</alter>`. Confirmé côté MuseScore : `tpc 21` (do♯)
présent, `tpc 10` (ré♭) absent.

Contrôle exhaustif en plus du critère : **les 128 hauteurs MIDI × 42 tonalités** (7 lettres × 3
altérations × majeur/mineur) = **5 376 orthographes**. Pour chacune :
`(octave+1)*12 + demi-tons(step) + alter` reconstitue **exactement** la hauteur MIDI d'origine,
`alter ∈ {-1, 0, +1}` (jamais de double altération), et `step` est une lettre légale.
Aucun décalage d'octave aux frontières si♯ / do♭ — le point le plus casse-gueule de la conversion TPC.

### 8.1.10 Mesure composée — critère 11
6/8 → `<beats>6</beats><beat-type>8</beat-type>`, mesures de 72 unités, note à cheval sur la barre liée
correctement. 12/8 → 144 unités par mesure, silence de mesure entière écrit
`<rest measure="yes"/><duration>144</duration>` (aucune figure standard ne vaut 144, la balise
`measure="yes"` règle le cas proprement).

### 8.1.11 Sélection des pistes — critères 12 et 13
**Vérifié par lecture du code, pas par exécution** (voir §7). Le bloc de sélection de
`btnExportMusicXml_Click` est le **copier-coller littéral** de `btnExportMuseScore_Click` (l. 3945-3955
vs 3969-3982) : pistes cochées ♫ d'abord, sinon toutes les pistes non-batterie, `continue` sur
`TimelineTrackType.Drum` dans les deux cas, puis message `AucunePisteMelodiqueAExporterCoche` si
`parts.Count == 0`. La piste d'accords (`TimelineTrackType.Chord`) n'est pas exclue : elle donne bien une
portée, conforme au §3.1-4.

Un garde **supplémentaire** existe au début (`project.Tracks.Count == 0` → `AucunePisteAExporter`), que
l'export `.mscx` n'a pas : c'est le cas limite « morceau sans aucune piste » du §5, correctement traité.

### 8.1.12 Transposition
Part clarinette (`Transpose = 2`) :
`<transpose><diatonic>-1</diatonic><chromatic>-2</chromatic></transpose>`, et armure écrite décalée de
2 dièses par rapport aux parts concertantes (`fifths` -1 → +1). Balise **absente** quand
`Transpose == 0`.

**Validé de bout en bout par MuseScore** : nous écrivons `<step>E</step><octave>4</octave>` (mi4, ce que
lit l'instrumentiste) ; MuseScore stocke `<pitch>62</pitch>` (ré4 concertant, la hauteur sonnante
d'origine) avec `<tpc2>18</tpc2>` (le mi écrit). Écrit un ton plus haut, sonnant juste. Exactement
l'exigence du §5.

> Faux positif rencontré : mon comparateur de fidélité a d'abord signalé « ours=[64,64]
> museScore=[62,62] » sur cette portée. C'était **le comparateur** qui confrontait hauteur écrite et
> hauteur concertante — le produit a raison.

### 8.1.13 Caractères spéciaux
Nom de piste `Flûte & <cor> 琴` et titre `Mon & "morceau" <test> 琴` :
`&amp;` / `&lt;` / `&gt;` présents dans le fichier brut, accents et kanji **intacts** après relecture
UTF-8, **pas de BOM**. Ils traversent aussi MuseScore sans dommage (`<trackName>Flûte &amp; &lt;cor&gt; 琴`).

### 8.1.14 Projet intact — critère 16
`TimelineProject.cs`, `TimelineDocument`, `ApplyDocument`, `MuseScoreExporter.cs`,
`ScorePdfExporter.cs`, `MidiTimelineExporter.cs`, `ScoreModel.cs` : **aucun n'est modifié**
(`git status` sur `MusicTracker/Engine/` et `MusicTracker/Controls/` ne montre que l'ajout non suivi de
`MusicXmlExporter.cs`).

Le gestionnaire n'écrit rien dans `project`, n'appelle ni `PushUndo`/`BeginUndo`/`FlushPending`, ni
`Render()`, ni `scoreTracks.Add/Remove`. Le `try` n'englobe **que** l'écriture, jamais le dialogue : une
annulation ne peut donc produire aucun message (critère 14 satisfait par construction).

### 8.1.15 Non-régression — critère 17
Contrôle **fort**, celui que réclame le §8.1.15 : un worktree du commit `9486e31` (pré-feature) a été
construit, puis le **même** `.sq` a été exporté par les deux binaires via le même pilote.

| Sortie | pré-feature vs post-feature |
|---|---|
| `.mscx` (ScoreBuilder + MuseScoreExporter) | **identique octet pour octet** |
| `.mid` (MidiTimelineExporter) | **identique octet pour octet** |
| vidage du `TrackScore` (ce que dessinent la vue partition et le PDF) | **identique octet pour octet** |

### 8.1.16 Robustesse
Projet entièrement vide → fichier valide, 1 mesure de silence par part. Liste de parts vide → XML bien
formé, pas d'exception. `Part.Score` à `null` → pas d'exception, portée de silences.

---

## 3. Compatibilité ascendante du `.sq` (demandée explicitement)

Testée **contre un vrai binaire d'avant la feature**, pas par raisonnement.

1. Le binaire pré-feature (`HEAD` = `9486e31`) écrit un `.sq` représentatif : 3 pistes
   (Instrument / Drum / Chord), 1 riff à 3 notes, module `PlayRiff`, ré mineur, 3/4, `PickupBeats = 1`,
   `SwingPercent = 62`, `MinBeats = 48`, volume et panoramique non par défaut.
2. Le binaire **post-feature** l'ouvre : tous les champs sont restitués
   (`Bpm=132 Sig=3/4 Scale=1 Pickup=1 Swing=62 MinBeats=48 Key=1/0/1 Tracks=3 Riffs=1`, les 3 pistes avec
   leur type, volume, panoramique et module). Il le réenregistre :
   **hash SHA-256 identique à l'original.**
3. Sens inverse (avant-compatibilité, §6 du fonctionnel) : un `.sq` écrit par le binaire post-feature est
   relu par le binaire pré-feature et réenregistré → **hash identique** lui aussi.

Le format `.sq` est donc inchangé dans les deux sens, ce qui était l'objectif : aucun champ ajouté,
aucune migration.

---

## 4. Validation par un logiciel de notation réel — critère 4

MuseScore 3 est installé sur la machine ; il a été utilisé **sans tête** pour convertir les fichiers.

```
MuseScore3.exe -o <sortie>.mscx <entrée>.musicxml
```

6 fichiers convertis (4/4 multi-portées, 5 croches, 6/8, 12/8 ternaire, 400 notes aléatoires, grille de
triolets) : **exit code 0 sur les 6**, et la trace est `... success!` — **aucun avertissement**, aucun
message « mesure trop courte/longue », aucun « fichier corrompu ».

### Fidélité mécanique (critère 5, partie vérifiable)
Un comparateur confronte les notes que **nous** écrivons aux notes que **MuseScore** a comprises
(fusion des segments liés des deux côtés) :

* nombre de portées identique ;
* **séquence de hauteurs identique** ;
* **séquence de durées identique** ;
* clés respectées (sol → `G`, fa → `F`), armures respectées, chiffrage respecté ;
* titre, noms de portée, `divisions=24`, mode `minor` : tous relus correctement ;
* arpège : les 3 hauteurs de l'accord roulé portent `<arpeggiate/>`, MuseScore en fait 1 objet `Arpeggio`.

Seul écart : la portée transpositrice, expliqué au §8.1.12 — c'est le comparateur qui avait tort.

### Tempo, y compris ternaire
* 4/4, tempo appli 120, `TimeSigScale = 1.0` → MuseScore lit **120 qpm**.
* 12/8 issu d'un 4/4 en triolets, tempo appli 100, `TimeSigScale = 1.5` → nous écrivons 150, MuseScore lit
  **150 qpm**, soit une **noire pointée à 100** = exactement le temps de l'application. La mise à
  l'échelle du §3.6 est donc juste ; sans elle la partition sonnerait 1,5 fois trop lentement.
* Le tempo n'est écrit **qu'une fois**, sur la première mesure de la première part.

`<midi-program>` : 73 → **74** (conversion 1-based correcte). Le canal 10 (percussion GM) n'est jamais
attribué. `TimelineTrack.Instrument` est bien traité comme un programme GM 0-based, cohérent avec
l'export MIDI de l'application (`PatchChangeEvent(0, ch, t.Instrument)`).

---

## 5. Performance — cas limite « morceau très long »

| Taille | Temps | Fichier |
|---|---|---|
| 4 parts × 64 mesures (2 048 notes) | 35 ms | < 1 Mo |
| 16 parts × 400 mesures (51 200 notes) | 169 ms | 11 Mo |
| 24 parts × 1 000 mesures (192 000 notes) | 708 ms | 44 Mo |

Un morceau réaliste s'exporte en quelques dizaines de millisecondes. Aucun risque de gel, aucun besoin
d'`ExportProgressDialog`. Le plafond dur `measures = min(measures, 20000)` protège du cas aberrant.

---

## 6. Échecs

**Aucun.** Les deux « échecs » apparus en cours de route étaient des défauts de mes propres harnais
(redirections d'assemblage manquantes ; comparaison hauteur écrite / hauteur concertante) et sont
documentés là où ils se sont produits, pour que personne ne les re-découvre comme des bugs.

---

## 7. Ce qui n'a PAS pu être vérifié

Un run automatisé n'a **ni oreille ni yeux**. Les points suivants restent ouverts et demandent un
passage humain.

### Rendu visuel
1. **Aspect de la partition ouverte dans MuseScore** (critère 5, partie visuelle). Le fichier s'importe
   sans avertissement et les notes/durées sont identiques *dans le modèle* ; que la page **ressemble**
   à la vue partition de l'application (côte à côte, mesure par mesure) n'a pas été constaté de visu.
2. **Les liaisons sont-elles dessinées ?** On a prouvé que `<tie>` et `<tied>` sont écrits et appariés, et
   que MuseScore crée les `Spanner type="Tie"` correspondants. Que l'arc soit **tracé** à l'écran n'a pas
   été vu.
3. **Lisibilité rythmique** (§8.2.3). La décomposition « position-consciente » donne bien
   `half + eighth` pour 5 croches, mais aucun jugement musical global n'a été porté sur l'ensemble des
   figures produites sur une vraie pièce.
4. **Le signe d'arpège** (la vaguelette) : `<arpeggiate/>` est écrit et MuseScore crée l'objet
   `Arpeggio`, mais le symbole n'a pas été vu dessiné.
5. **L'infobulle du menu**, dans les 7 langues, au survol réel de `MusicXML (.musicxml)…`. Les chaînes
   sont prouvées correctes ; leur **affichage** ne l'est pas.

### Rendu sonore
6. **Relecture audio dans MuseScore** : que le timbre soit le bon (`<midi-program>`) et que la clarinette
   **sonne** à la bonne hauteur relève de l'oreille. Le modèle est juste (MuseScore a bien récupéré la
   hauteur concertante 62) ; l'écoute reste à faire.
7. **Tempo à l'oreille sur un morceau ternaire 12/8** — la valeur 150 qpm est prouvée, la sensation ne
   l'est pas.

### Parcours d'interface non couverts par un scénario automatisé
Aucun scénario AutoTest ne pilote `Export ▸ MusicXML (.musicxml)…`. Les 5 scénarios existants
(`AppLaunch`, `NewMusicOpensEditor`, `PlayThenStop`, `SaveDialogOpensAndCancels`, `OpenTemplateProject`)
passent mais ne touchent pas la feature. Restent donc **vérifiés par lecture de code seulement** :

8. **Critère 3** — nom proposé dérivé du morceau, message de confirmation affichant le chemin.
9. **Critères 12 et 13** — les trois combinaisons de cases ♫ (2 cochées sur 4 / aucune cochée / batterie
   seule) réellement cliquées dans l'application.
10. **Critère 14** — annulation du sélecteur : silence total.
11. **Critère 15** — chemin non inscriptible : message d'erreur lisible, application toujours utilisable.
12. **Critère 16, volet interactif** — absence d'entrée d'annulation nouvelle après un export (Ctrl+Z).

Le risque est faible (ce code est le calque littéral d'un gestionnaire en production), mais il n'est pas
nul et il n'est pas mesuré. **Une passe manuelle sur ces 5 points est recommandée avant publication.**

### Autres
13. **PDF** : `ScorePdfExporter.cs` est intact et la sortie de `ScoreBuilder` est prouvée identique à
    l'octet — mais aucun PDF n'a été rendu ni comparé visuellement.
14. **Portabilité réelle** (§8.2.8) : un **second** logiciel (Finale, Dorico, OpenSheetMusicDisplay,
    Soundslice) n'était pas disponible. Validé sur MuseScore 3 uniquement — or MuseScore 3 vise
    MusicXML 3.1 alors que le fichier se déclare `version="4.0"` ; il l'accepte, mais un lecteur
    strictement 4.0 n'a pas été essayé.
15. **Conformité au schéma XSD** : aucun `.xsd` MusicXML n'est disponible hors ligne. L'ordre des
    enfants de `<note>` et l'ordre `work/identification/part-list/part` ont été vérifiés **à la main
    contre la DTD**, et l'import MuseScore sans avertissement est un bon indice — ce n'est pas une
    validation formelle.
16. **Levée (anacrouse)** : le comportement (écrite comme une mesure complète, `PickupBeats` non
    appliqué) est cohérent avec le `.mscx` et documenté comme limite connue en tête de
    `MusicXmlExporter.cs`. Non testé sur un morceau à levée réel.

---

## 8. Réserve hors feature — à trancher avant de committer

L'arbre de travail contient une modification **sans rapport avec l'export MusicXML**, qui partirait dans
le même commit si l'on faisait un `git add -A` :

* `MusicTracker/Screens/HomeScreen.xaml` — le wordmark 木 (deux `TextBlock`, dont un flouté) est remplacé
  par deux `<Image Source="/Images/logo-mark.png">` ;
* `MusicTracker/Images/logo-mark.png` — fichier neuf, non suivi (10,6 Ko) ;
* `MusicTracker.csproj` — `<Resource Include="Images\logo-mark.png" />`.

Ce n'est pas un échec : le build passe et le scénario `AppLaunch` (qui rend l'écran d'accueil) passe, donc
la ressource se charge sans exception. Mais c'est un **changement visuel de la page d'accueil que ce run
n'a pas pu juger** (taille, flou du halo, fond transparent, rendu en thème clair). À séparer du commit de
la feature, ou à faire valider de visu.

Les répertoires `docs/features/a-dater-*` non suivis relèvent d'autres features et sont sans effet.

---

## 9. Conclusion

Le cœur de la feature — l'invariant « chaque mesure est exactement pleine » et les liaisons
par-dessus la barre, qui sont sa raison d'être — n'est pas seulement testé sur des exemples : il est
**démontré sur 13 728 combinaisons** d'attaque et de durée réparties sur 6 chiffrages, et l'orthographe
des hauteurs sur **5 376 combinaisons** de tonalité et de hauteur. Un logiciel de notation réel importe
les fichiers sans un seul avertissement et en restitue les mêmes notes.

La non-régression et la compatibilité `.sq` ne sont pas argumentées : elles sont **mesurées octet pour
octet contre le binaire d'avant la feature**.

**vert = true**, avec deux réserves à traiter par un humain : la passe manuelle sur les 5 points
d'interface du §7 (8 à 12), et l'arbitrage du §8 sur la modification non liée de l'écran d'accueil.
