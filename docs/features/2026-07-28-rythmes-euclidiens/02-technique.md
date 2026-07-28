# Rythmes euclidiens — analyse technique

Référence : `01-fonctionnel.md`. Effort estimé : **moyen** (générateur trivial, l'essentiel est le panneau d'interface et l'i18n).

## 1. Le point d'insertion, et pourquoi il est idéal

Un module de batterie possède déjà exactement ce qu'il faut :

```csharp
// FlowModule.cs:203
/// NOTE-LIST form of the drum motif (Note = drum LANE index) […]
/// When non-empty it is the SOURCE OF TRUTH
public List<RiffNote> CustomNotes { get; set; }

// FlowModule.cs:218
public void SetCustomNotes(List<RiffNote> notes, int slicesPerQuarter, int lengthSlices)
```

`CustomNotes` est une **liste de notes libre**, source de vérité, déjà persistée, déjà éditable dans la grille, déjà lue par `DrumPattern.Generate`. Générer un rythme euclidien revient donc à **produire une liste de notes et appeler `SetCustomNotes`** — rien d'autre.

Conséquences directes, toutes vérifiables :

- **aucun changement de modèle de projet** → rien à ajouter dans `TimelineScreen.ApplyDocument`, donc aucun risque de perte silencieuse à l'ouverture ou à l'undo (le piège habituel de ce projet ne s'applique pas ici) ;
- **aucun changement du format `.sq`** → compatibilité ascendante et descendante par construction ;
- **le motif reste éditable à la main**, gratuitement, puisqu'il devient un motif personnalisé ordinaire.

Le précédent exact existe : `TimelineHelper.CustomizeDrum` (l. 472-488) convertit un motif du catalogue en motif personnalisé éditable. Le générateur euclidien est le même geste, avec une autre source.

## 2. L'algorithme

Inutile d'implémenter la récursion de Bjorklund. La **formule de répartition maximale** donne le même collier, en une ligne :

```csharp
// Le pas i porte un coup  ⟺  (i * k) mod n < k
```

**Vérifié numériquement** avant rédaction :

| Motif | Résultat | Coups | Intervalles distincts |
|---|---|---|---|
| E(3,8) | `@..@..@.` | 3 | 2 (2 et 3) — *le tresillo, exactement* |
| E(5,8) | `@.@.@@.@` | 5 | 2 (1 et 2) |
| E(2,5) | `@..@.` | 2 | 2 (2 et 3) |
| E(7,16) | `@..@.@.@..@.@.@.` | 7 | 2 (2 et 3) |
| E(9,16) | `@.@.@.@.@@.@.@.@` | 9 | 2 (1 et 2) |
| E(4,4) | `@@@@` | 4 | 1 |

L'invariant « au plus deux longueurs d'intervalle, différant de 1 » — la définition même de la régularité maximale, et le critère d'acceptation n° 3 — est respecté partout.

> **Piège à connaître.** La formule produit le bon *collier* mais pas toujours à la même **rotation** que la forme canonique citée dans la littérature. E(5,8) sort en `@.@.@@.@` alors que le cinquillo s'écrit `@.@@.@@.` : ce sont les mêmes intervalles, décalés de 2 pas. Deux conséquences :
> 1. le réglage « Décalage » de l'interface couvre le cas — l'utilisateur retrouve la forme voulue ;
> 2. la **détection du nom traditionnel doit comparer à rotation près**, pas les positions brutes. Comparer les positions telles quelles ne reconnaîtrait jamais le cinquillo.

## 3. Fichiers touchés

| Fichier | Changement |
|---|---|
| `Engine/Flow/EuclideanRhythm.cs` | **NOUVEAU**, ~60 lignes. Statique pur, sans dépendance : `bool[] Pattern(int k, int n)`, `bool[] Rotate(bool[] p, int r)`, `string NameFor(int k, int n, int rot)` (reconnaissance à rotation près). Testable isolément. |
| `MusicTracker.csproj` | `<Compile Include="Engine\Flow\EuclideanRhythm.cs" />`. **Csproj ancien format, sans glob : sans cette ligne le build échoue.** |
| `Engine/Timeline/TimelineHelper.cs` | `ApplyEuclidean(...)` à côté de `CustomizeDrum` (l. 472). Aucune entrée csproj (fichier existant). |
| `Screens/TimelineScreen.xaml.cs` | Panneau dans `BuildDrumEditor` (l. 2983), après le bouton « Personnaliser » (l. 3035). Contrôles construits **en code**, comme tout le reste de cette méthode. |
| `Localization/lang.{fr,en,de,it,es,nl,pt}.json` | ~10 clés × 7 fichiers. |

## 4. La fonction d'application

```csharp
// TimelineHelper — produit le motif et le pose, sans toucher aux autres lignes.
public static void ApplyEuclidean(DrumPatternModule dp, int lane, int k, int n, int rotation, int stepSlices)
```

Déroulé :

1. **Basculer en mode personnalisé** si nécessaire — reprendre exactement le geste de `CustomizeDrum` : `dp.Style = DrumPattern.CustomStyle`, `CatCategory = CatMotif = "Personnalisé"`. **Indispensable** : `RefreshDrumGrid` (l. 3038) affiche un simple message d'invite au lieu de la grille tant que `DrumIsCustom(dp)` est faux — sans cette bascule, l'utilisateur ne verrait pas son motif.
2. **Repartir des notes existantes**, en retirant celles de la ligne visée : `notes.RemoveAll(x => x.Note == lane)`. C'est ce qui garantit le critère 5 (superposition non destructive).
3. **Calculer le motif** : `Rotate(Pattern(clamp(k,0,n), n), rotation)`.
4. **Le dérouler** sur toute la longueur du module : longueur totale = `BeatsPerBar × Repeats × 24` slices ; cycle = `n × stepSlices`. On avance de cycle en cycle jusqu'à la fin, **sans recalage sur la mesure** — c'est ce qui produit le décalage voulu au §3.5 du fonctionnel quand le cycle ne divise pas la mesure.
5. **Émettre les notes** : `new RiffNote(lane, position, stepSlices)`. Attention, `Note` est un **index de ligne de percussion**, pas une note MIDI (`DrumPattern.Generate` fait la conversion : `row = LaneKeys[n.Note] - 12`). La longueur ne sert qu'à l'édition — la percussion est déclenchée une fois au début (« single trigger at the start », `DrumPattern.cs:118`).
6. `dp.SetCustomNotes(notes, 24, longueurTotale)`.

**Grille et unités.** `DrumPattern.SlicesPerQuarter = 24`, divisible par 2, 3, 4, 6, 8 et 12 — les trois unités proposées tombent donc juste, sans arrondi :

| Unité | Slices par pas |
|---|---|
| Croche | 12 |
| Double-croche | 6 |
| Triolet de croche | 8 |

C'est la raison pour laquelle le fonctionnel exprime le cycle en **N × unité** plutôt qu'en « N pas dans la mesure » : cette seconde formulation obligerait à répartir 5 ou 7 pas sur 96 slices, donc à arrondir, donc à produire un rythme faux à quelques millisecondes près.

## 4 bis. Le décalage générique

Le §3.6 du fonctionnel demande un décalage applicable à **tout** motif, pas seulement aux motifs générés. C'est une transformation de la liste de notes, indépendante de l'euclidien :

```csharp
// TimelineHelper — fait tourner UNE ligne de percussion dans son cycle.
public static void RotateLane(DrumPatternModule dp, int lane, int deltaSlices)
```

Le cycle est déjà défini par le moteur, il ne faut surtout pas en inventer un autre :

```csharp
// DrumPattern.cs:109 — la maille effectivement répétée
int unit = (m.CustomSlices != null && m.CustomSlices.Length > 0)
         ? m.CustomSlices.Length
         : Math.Max(1, RiffNotes.LengthOf(m.CustomNotes));
```

La transformation se réduit alors à `start' = ((start + delta) mod unit + unit) mod unit` sur les seules notes dont `Note == lane`, puis `SetCustomNotes`. Le double modulo gère les décalages négatifs, que le fonctionnel autorise.

Trois propriétés en découlent gratuitement, et couvrent les critères 14 à 18 :

- le **nombre de coups est invariant** (on déplace, on ne crée ni ne supprime) ;
- une rotation de `unit` est l'**identité** ;
- comme `unit` est précisément la maille que `DrumPattern.Generate` répète (`Repeats` fois, l. 112-120), le décalage s'entend sur **toutes** les répétitions, sans code supplémentaire.

Deux points d'attention :

1. **Réutiliser `unit` tel quel.** Prendre à la place `BeatsPerBar × 24` donnerait un résultat faux pour tout motif dont la longueur stockée diffère de la mesure — c'est le cas des motifs du catalogue multi-mesures.
2. **Même bascule en mode personnalisé** que pour la génération (§4, étape 1) : un motif du catalogue non personnalisé n'a pas de `CustomNotes` à faire tourner.

Cette fonction est indépendante de `EuclideanRhythm.cs` : le décalage reste disponible même si la génération euclidienne était retirée. Le panneau euclidien s'en sert pour sa propre rotation, plutôt que de dupliquer la logique.

## 5. Interface

Deux blocs distincts, tous deux construits **en code** comme le reste de `BuildDrumEditor` :

**a) Le décalage** — toujours visible sous « Personnaliser », car il s'applique à n'importe quel motif : un `ComboBox` d'instrument, et deux boutons `◀` / `▶` qui décalent d'un pas dans l'unité choisie. Des boutons plutôt qu'un champ numérique : le décalage se cherche à l'oreille, par essais successifs, et non en saisissant une valeur connue d'avance. Chaque clic est une application immédiate, donc **une entrée d'annulation** — d'où l'importance du §6.

**b) La génération euclidienne** — un `Expander` replié, contenant : un `ComboBox` d'instrument, trois champs numériques (Coups, Pas, Décalage initial), un `ComboBox` d'unité, un `TextBlock` d'aperçu en police à chasse fixe, et un bouton « Appliquer ».

L'aperçu se recalcule à **chaque changement de réglage** — c'est lui qui rend la feature utilisable sans connaître la théorie (§3.3). Il affiche le motif en `●`/`·`, le nom traditionnel s'il existe, et l'avertissement de décalage quand `n × stepSlices` ne divise pas `BeatsPerBar × 24`.

Après « Appliquer » : `editorHost.Content = BuildDrumEditor(track, item, dp); Render();` — la reconstruction déjà utilisée par les listes du catalogue (`rebuild`, l. 3027).

## 6. Annulation

Le critère 7 exige **une seule** entrée d'annulation par application. L'éditeur de batterie passe par le mécanisme de diff de session d'édition (`BeginEditSessionFor` / `FlushPending`), qui compare l'état avant/après édition d'un module.

⚠️ **À vérifier explicitement au développement** : que ce mécanisme capture bien une mutation faite par du code (et non par la grille), et qu'il n'en produit qu'une. Si ce n'est pas le cas, encadrer par `PushUndo("euclid")` — clé volontairement hors des préfixes coalescables `move:` / `edit:` / `vol:` et des préfixes neutralisables `insert:` / `delete:` de `UndoManager`.

## 7. Risques

| Risque | Ce qui l'empêche |
|---|---|
| Écraser les autres lignes | filtrage sur `x.Note == lane` uniquement (critère 5) |
| Motif invisible après génération | bascule en mode personnalisé, sans quoi `RefreshDrumGrid` masque la grille |
| Confusion ligne / note MIDI | `RiffNote.Note` = index de ligne ; la conversion appartient à `DrumPattern.Generate`, ne pas la dupliquer |
| Build cassé | la ligne `<Compile>` du csproj — c'est ce qui a manqué de peu sur l'export MusicXML |
| Nom traditionnel jamais reconnu | comparaison à rotation près (§2) |
| Rythme faux de quelques ms | cycle en N × unité, jamais N réparti sur la mesure |
| `n = 0` → division par zéro | borner `n ≥ 1` à la saisie **et** dans `Pattern` |

## 8. Plan de test

**Automatisable, sans lancer l'application** (le générateur est statique et pur — un pilote compilé contre `KotonStudio.exe` suffit, comme pour l'export MusicXML) :

1. `Pattern(k,n)` renvoie exactement `k` coups, pour tout `1 ≤ k ≤ n ≤ 32`.
2. Les intervalles cycliques ne prennent **jamais plus de deux valeurs distinctes**, et elles diffèrent de 1 — balayage exhaustif sur le même domaine.
3. `Pattern(3,8)` = positions {0,3,6} ; `Pattern(2,5)` = {0,3}.
4. `Rotate(p,r)` conserve le nombre de coups ; `Rotate(p,n)` == `p`.
5. `k=0` → aucun coup ; `k≥n` → tous ; `n=1` → un seul.
6. `ApplyEuclidean` sur la caisse claire laisse **inchangées** toutes les notes des autres lignes (comparaison avant/après).
7. Le nombre de coups posés vaut `k × nombre de cycles` sur la longueur du module.
8. Aller-retour `.sq` : enregistrer après génération, recharger, motif identique.
9. Un `.sq` antérieur se recharge inchangé (aucun champ nouveau).
10. Les 7 `lang.xx.json` parsent (via `JavaScriptSerializer`, **pas** `ConvertFrom-Json` qui échoue à tort sur `AUDIO`/`Audio`), et chaque clé neuve existe dans les 7.
11. Build : `msbuild`, 0 erreur, 0 avertissement — **et en configuration Release**, pas seulement Debug.
12. `RotateLane` conserve exactement le nombre de coups de la ligne, pour tout décalage de −64 à +64.
13. `RotateLane(dp, lane, unit)` est l'**identité** ; `+d` suivi de `−d` aussi (aller-retour exact).
14. `RotateLane` sur une ligne ne modifie **aucune** note des autres lignes (comparaison avant/après).
15. Décalage sur un motif du catalogue **multi-mesures** : la rotation utilise bien `unit` et non `BeatsPerBar × 24` — un motif de 2 mesures décalé de sa longueur redonne l'original.
16. Décalage d'une ligne vide : aucun changement, aucune exception.

**Automatisable via l'interface** (FlaUI) : ouvrir l'éditeur de batterie, déplier le panneau, appliquer, vérifier que la grille s'affiche et contient des coups ; Ctrl+Z, vérifier le retour à l'état antérieur en une fois.

**Exige un jugement humain** — à ne pas présenter comme validé par un run automatisé :

- que le groove obtenu **sonne** juste, et que les motifs nommés soient reconnaissables à l'oreille ;
- que le décalage polymétrique soit musicalement intéressant plutôt que désordonné ;
- la lisibilité de l'aperçu et la clarté des libellés pour un musicien ignorant le mot « euclidien » — qui est l'enjeu d'adoption principal de cette feature.
