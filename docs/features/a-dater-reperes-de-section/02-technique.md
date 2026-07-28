# Repères de section sur la timeline — analyse technique

Référence fonctionnelle : `01-fonctionnel.md` (même dossier). Ce document décrit **comment** implémenter,
et n'ajoute aucune exigence fonctionnelle nouvelle. Tous les numéros de ligne ont été relevés dans les
sources au moment de l'analyse ; ils peuvent avoir bougé de quelques lignes, les extraits cités permettent
de retrouver l'emplacement exact.

---

## 1. Ce que le code fait déjà (vérifié dans les sources)

### 1.1 La zone « règle de mesures »

`MusicTracker\Screens\TimelineScreen.xaml`, l. 384-428 : la zone des pistes est une `Grid` à **2 lignes ×
2 colonnes**.

| Cellule | Contenu actuel |
|---|---|
| ligne 0, col. 0 (l. 395-398) | `Border` de 20 px de haut = le « coin » au-dessus de la colonne des en-têtes, avec le libellé `{loc:Tr 'BARS'}` |
| ligne 0, col. 1 (l. 401-408) | `ScrollViewer x:Name="rulerScroll"` `Height="20"`, scrollbars masquées, contenant une `Grid` qui **superpose** `measureRuler` (la règle) et `startCanvas` (calque transparent qui porte la poignée bleue de départ et la poignée orange de boucle) |
| ligne 1, col. 0 (l. 413-418) | `headerScroll` / `headerPanel` — les en-têtes de pistes |
| ligne 1, col. 1 (l. 419-427) | `laneScroll` / `lanePanel` — les pistes + le calque `cursorCanvas` du curseur jaune |

La synchronisation horizontale est faite **en un seul endroit**,
`TimelineScreen.xaml.cs` → `laneScroll_ScrollChanged` (l. 4036-4042) :

```csharp
rulerScroll.ScrollToHorizontalOffset(laneScroll.HorizontalOffset);   // keep the measure ruler aligned
chordScroll?.ScrollToHorizontalOffset(laneScroll.HorizontalOffset);  // keep the docked chords lane aligned
```

et le constructeur (l. 89-91) réserve une gouttière de 18 px à droite de `rulerScroll` et `chordScroll`
pour que les trois viewports aient **exactement** la même largeur utile — sinon les offsets divergent en
fin de course. **Ce mécanisme est fragile et il ne faut pas en créer un quatrième exemplaire** (voir §2.2).

### 1.2 La grille de barres de mesure (levée comprise)

`MusicTracker\Controls\TimelineEditor\MeasureRulerControl.xaml.cs`, l. 22-60. Règle exacte, l. 28 :

```csharp
double phase = pickupBeats > 1e-6 ? pickupBeats % beatsPerBar : 0; // fold a >1-bar levée into one bar
```

- si `phase > 0` : une barre (sans numéro) en 0 = **barre de levée** ;
- puis les barres **numérotées** en `phase + m·beatsPerBar`, numéro affiché `m + 1`.

`beatsPerBar` vient de `TimelineHelper.RulerBeatsPerBar(project)` (`TimelineHelper.cs` l. 79) et
`pickupBeats` de `project.PickupBeats`. C'est **la** définition de la grille visible ; le calage des
repères doit la reproduire à l'identique (critères 5, 21).

### 1.3 Poignée de départ, boucle A-B, curseur

Tout est dans `TimelineScreen.xaml.cs` :

| Élément | Champ / méthode | Ligne |
|---|---|---|
| point de départ de lecture (poignée bleue) | `double startBeat`, `SetStartFromX(double x)` | 59, 679-686 |
| fin de boucle B (poignée orange) | `double loopEndBeat`, `SetLoopEndFromX` | 63, 706-713 |
| activation de la boucle | `bool loopEnabled`, `btnLoop_Click` | 62, 733-747 |
| création/positionnement des poignées | `EnsureCursor`, `MoveCursor` | 504-548, 550-581 |

Points relevés :

- `SetStartFromX` **n'arrête pas** la lecture : il change `startBeat` puis appelle `MoveCursor`. Pendant la
  lecture, le timer (33 ms) réécrit aussitôt le curseur à la position audible — donc l'effet visible est
  bien « seul le point de départ bouge », ce que demande §3.3. **Le clic sur un repère doit copier ce
  comportement, surtout pas celui de `SeekTo` (l. 495-500) qui, lui, détruit le player.**
- `btnLoop_Click` est le modèle exact pour « Boucler cette section » : il pose une valeur par défaut de B,
  appelle `EnsureCursor()`, pousse `Loop`/`LoopEndBeat` dans le player s'il tourne (`player.ApplyLoop()`),
  puis `MoveCursor`.
- `startBeat`, `loopEndBeat`, `loopEnabled` sont de **l'état d'interface**, pas de l'état de projet : ils
  ne sont ni sérialisés ni annulables. « Boucler cette section » ne doit donc **pas** créer d'entrée
  d'undo.

### 1.4 Persistance et undo — le point critique

- Sauvegarde : `Save(string path)` (l. 750-757) sérialise un `TimelineDocument { Project, Riffs }` avec
  `JsonOpts = new JsonSerializerOptions { IncludeFields = true }` (l. 364). Les **champs publics** comme les
  **propriétés publiques** sont donc sérialisés, et `System.Text.Json` **ignore silencieusement** les
  propriétés inconnues à la lecture → compatibilité ascendante ET descendante gratuite (§4.1).
- Undo/redo : `UndoManager` (`Engine\Timeline\UndoManager.cs`) stocke des **snapshots** = exactement le
  même JSON (`SnapshotState()`, l. 919-924). **Tout champ ajouté au projet est donc automatiquement
  couvert par l'undo… à condition d'être recopié dans `ApplyDocument`.**
- `ApplyDocument(doc, path)` (l. 880-914) est **l'unique point de recopie champ par champ** du document
  vers le projet vivant. Il est appelé par : l'ouverture d'un `.sq` (`LoadSqFile`, l. 862), `LoadDocument`
  (l. 875, utilisé par l'import, l'IA, l'orchestrateur, les modèles) **et par `RestoreState`** (l. 977,
  c'est-à-dire par **chaque Ctrl+Z / Ctrl+Y**). Un champ oublié ici est perdu à l'ouverture **et remis à
  vide à chaque annulation** — le piège annoncé dans la consigne, confirmé par lecture du code.
- Règles de consolidation de `UndoManager.Push` (l. 37-67) :
  - **neutralisation** si la clé commence par `delete:` et que l'entrée précédente est le `insert:` du même
    id ;
  - **coalescence** si la clé commence par `move:`, `edit:` ou `vol:` **et** est identique à la précédente.

  ⚠️ Conséquence directe pour les repères : **ne pas utiliser les préfixes `move:` / `delete:` / `insert:`**.
  Deux glissements successifs de **deux repères différents** partageraient la clé `move:…` seulement si
  l'identifiant était identique, mais le risque de collision avec les modules (l'id est un
  `RuntimeHelpers.GetHashCode`) et la sémantique « un glissement = une entrée » se gèrent bien plus sûrement
  avec `BeginUndo`/`CommitUndo` (l. 935-936) et une clé **non coalescable** (voir §4.4).

### 1.5 Ce qui existe et se réutilise tel quel

| Besoin | Existant | Où |
|---|---|---|
| petit dialogue de saisie thémé | `TimelineHelper.PromptText(string title, string initial)` → `null` si Annuler, le texte sinon | `TimelineHelper.cs` l. 1247-1272 |
| menu contextuel sur un objet de la timeline | `ShowItemContextMenu` (`new ContextMenu()` + `MenuItem` + `menu.IsOpen = true`) | `TimelineScreen.xaml.cs` l. 3431+ |
| geste « clic vs glisser » avec seuil | `ModuleBoxControl` : `pressPos` / `pressLeft` / `DragThreshold = 4` / `CaptureMouse` / événement `Dropped(newLeft)` | `Controls\TimelineEditor\ModuleBoxControl.xaml.cs` l. 39-91 |
| double-clic sur une piste avec calage au temps | `TempoLaneControl.Canvas_MouseLeftButtonDown` (`e.ClickCount != 2` → return, puis `IndexOfBeat` → éditer sinon créer) | `Controls\TimelineEditor\TempoLaneControl.xaml.cs` l. 131-145 |
| contrôle de piste « dessiné à la main » | `TempoLaneControl`, `MeasureRulerControl` : une `Canvas`, une méthode `Configure(...)`, un `Redraw()` | idem |
| couleur d'accent turquoise | `AccentBrush` = `#1FB6C3`, `AccentBrightBrush` = `#3BCEDA` | `Theme\Colors.xaml` l. 115-117 |
| clé « Annuler » | déjà présente dans les 7 fichiers | `Localization\lang.*.json` |

### 1.6 Ce qui n'existe pas (et n'est donc pas à honorer)

L'application **n'a aucun indicateur « projet modifié »** : pas de champ `Dirty`/`IsModified`, pas
d'astérisque de titre, pas de confirmation à la fermeture (le seul `riffDirty`, l. 44, concerne le
rafraîchissement de la vignette d'un riff en cours d'édition). Le §3.7 du fonctionnel — « marque le projet
comme modifié … *selon le comportement existant* » — est donc satisfait **par construction, sans code** :
les repères se comportent exactement comme toutes les autres éditions. **Ne pas inventer un mécanisme de
dirty-flag à cette occasion.**

---

## 2. Approche retenue

### 2.1 Le point unique

> **Le bandeau des repères s'insère à l'intérieur du `ScrollViewer` qui porte déjà la règle
> (`rulerScroll`), au-dessus d'elle, dans un `StackPanel` vertical.**
> Les données vivent dans `TimelineProject.Markers` ; toute la logique (créer / renommer / déplacer /
> supprimer / naviguer / boucler / annuler) reste dans `TimelineScreen.xaml.cs`, à côté du code qui gère
> déjà `startBeat` et la boucle A-B. Le nouveau contrôle `MarkerLaneControl` ne fait que **dessiner et
> détecter des gestes** ; il ne connaît ni le projet, ni la localisation, ni l'undo.

Concrètement, `rulerScroll` passe de `Height="20"` à `Height="38"` et son contenu devient :

```xml
<StackPanel HorizontalAlignment="Left">
    <tle:MarkerLaneControl x:Name="markerLane" Height="18"/>
    <Grid>
        <tle:MeasureRulerControl x:Name="measureRuler" HorizontalAlignment="Left"/>
        <Canvas x:Name="startCanvas" Height="20" Background="Transparent"
                MouseLeftButtonDown="startCanvas_MouseLeftButtonDown"/>
    </Grid>
</StackPanel>
```

Pourquoi c'est le bon endroit :

1. **L'alignement pixel du critère 3 est acquis sans une ligne de code.** Le bandeau et la règle partagent
   le même `ScrollViewer`, donc le même `HorizontalOffset`, la même origine et la même largeur de contenu.
   Aucune nouvelle ligne dans `laneScroll_ScrollChanged`, aucune nouvelle gouttière de 18 px à réserver,
   aucun risque de dérive en fin de course.
2. `startCanvas` (poignées bleue/orange) reste **exactement** dans la même `Grid` avec la même origine :
   `e.GetPosition(startCanvas).X` est inchangé, `SetStartFromX` / `SetLoopEndFromX` / `MoveCursor` ne sont
   pas touchés.
3. Les coordonnées X du bandeau et de la règle sont identiques (`beat * PxPerBeat`, `PxPerBeat = 60`,
   l. 31), donc un fanion tombe pile sur sa barre.

### 2.2 Alternatives écartées

| Option | Pourquoi non |
|---|---|
| **Une 3ᵉ ligne de `Grid` avec son propre `ScrollViewer`** (`markerScroll`), synchronisé dans `laneScroll_ScrollChanged` | C'est un 4ᵉ viewport à garder aligné : il faut refaire la gouttière `sbW = 18` du constructeur (l. 89-91) et ajouter une ligne de synchro. Le commentaire du constructeur explique que cet alignement s'est déjà mal passé une fois (« at the far right the ruler/chords froze a scrollbar-width short of the lanes »). Aucun bénéfice en échange. |
| **Dessiner les repères dans `startCanvas`** (au-dessus des poignées) | Le bandeau doit être une zone de dépôt à part entière avec son propre double-clic ; `startCanvas` a déjà `MouseLeftButtonDown` → poser le point de départ. Les deux gestes se marcheraient dessus, et le §3.1 demande explicitement que le fanion ne se confonde pas avec les poignées. |
| **Ajouter les repères à `MeasureRulerControl`** | Ce contrôle est un dessin pur, sans état ni interaction, réutilisable ; y greffer glisser/menu/dialogue le transformerait en contrôle métier. Et le fonctionnel demande un **bandeau distinct** avec son propre libellé de colonne. |
| **Une piste `TimelineTrack` de type `Marker`** | Les repères sont des points sans durée, globaux au morceau. Les faire passer par `TimelineItem`/`FlowModule` les enverrait dans le player, les exports, `ScoreBuilder`, `ResolveLoops`… soit exactement les régressions que §4.1 interdit. |
| **Un contrôle `MarkerLaneControl.xaml` + code-behind** (paire XAML) | Deux entrées de plus dans le `.csproj` (`<Compile>` + `<Page>`) pour un contrôle qui n'a **aucun** arbre visuel statique : tout est construit en code (polygones + textes positionnés). Le dépôt a déjà des contrôles 100 % code (`Controls\ReflowButtons.cs`, `Controls\GuidedTour.cs`, `Dialogs\NewModelDialog.cs`). Un seul fichier `.cs` dérivant de `Canvas` suffit. |
| **Un dialogue de saisie dédié** | `TimelineHelper.PromptText` est déjà le « petit dialogue thémé » cité par le fonctionnel §3.2. |

---

## 3. Modèle de données et persistance

### 3.1 `MusicTracker\Engine\Timeline\TimelineProject.cs`

Ajouter, à côté de `TempoChange` (l. 8-12) et `VolumePoint` (l. 46-50) — mêmes conventions : classe simple,
champs publics :

```csharp
/// <summary>A named position marker ("Intro", "Thème A", "Coda") shown in the timeline's marker band, above
/// the measure ruler. A POINT, not a range: the section it opens runs to the next marker. Purely editorial —
/// playback, score and exports ignore it. Serialized with the project (.sq).</summary>
public class SectionMarker
{
    public double Beat;            // position in raw quarter-beats, same axis as the ruler / PxPerBeat
    public string Name = "";
}
```

et sur `TimelineProject`, près de `MinBeats` (l. 122) / `SwingPercent` (l. 127) :

```csharp
/// <summary>Named section markers (see <see cref="SectionMarker"/>), kept sorted by Beat. Empty by default:
/// a project created, imported or generated before/without this feature simply has none.</summary>
public List<SectionMarker> Markers { get; set; } = new List<SectionMarker>();
```

Justification du typage : `IncludeFields = true` sérialise les deux formes, mais une **propriété avec
initialiseur** garantit que l'ouverture d'un `.sq` antérieur laisse une liste **vide et non nulle**
(l'initialiseur est exécuté à la construction, `System.Text.Json` ne remet rien à `null` pour une propriété
absente). Même schéma que `UserChordStyles` / `UserMelodicLines`.

JSON produit :

```json
"Markers": [ { "Beat": 16, "Name": "Thème A — reprise l'octave" }, { "Beat": 48, "Name": "Coda" } ]
```

### 3.2 `ApplyDocument` — LE point à ne pas rater

`MusicTracker\Screens\TimelineScreen.xaml.cs`, dans `ApplyDocument` (l. 880-914), à la suite immédiate de
`project.SwingPercent = …` (l. 899) :

```csharp
project.Markers = dp.Markers ?? new System.Collections.Generic.List<SectionMarker>();
```

**Vérification explicite demandée par la consigne :** cette ligne est indispensable et suffisante.
`ApplyDocument` est l'unique recopie champ-par-champ (`git grep "project.MinBeats ="` ne renvoie que cet
endroit et `TemplateProjectBuilder`, qui construit un projet neuf). Sans elle :

- ouverture d'un `.sq` → bandeau vide alors que le fichier contient des repères (critère 18 rouge) ;
- **et surtout** : `RestoreState` (l. 970-983) passe par `ApplyDocument`, donc **chaque Ctrl+Z / Ctrl+Y
  effacerait tous les repères** (critères 14, 16, 17 rouges).

Le `?? new List<>()` couvre le fichier édité à la main contenant `"Markers": null`.

### 3.3 Ce qu'il ne faut **pas** toucher

`Save` / `SnapshotState` (sérialisation générique du `TimelineDocument`), `LoadSqFile`, `LoadDocument`,
`TimelineImporter`, `TemplateProjectBuilder`, `MidiTimelineExporter`, `MuseScoreExporter`,
`MusicXmlExporter`, `WaveExporter`, `ScoreBuilder`, `TimelinePlayer`, `BugReportContext` : aucun ne lit ni
n'écrit `Markers`. Un morceau importé ou généré a donc 0 repère (§3.9) et les exports sont bit-à-bit
identiques (critère 20) **par construction**.

---

## 4. Conception détaillée

### 4.1 Nouveau fichier `MusicTracker\Controls\TimelineEditor\MarkerLaneControl.cs`

Contrôle **100 % code**, dérivant de `Canvas`, sans dépendance à `Loc`, au projet ni à l'undo. Il reçoit
tout par `Configure` et remonte des événements.

```csharp
namespace MusicTracker.Controls.TimelineEditor
{
    /// <summary>The section-marker band drawn just above the measure ruler: one teal pennant + name per
    /// marker, snapped to the ruler's barlines. Pure view + gesture detection — the host (TimelineScreen)
    /// owns the model, the undo and the localized strings.</summary>
    public sealed class MarkerLaneControl : Canvas
    {
        public event Action<double> CreateRequested;                        // double-click on empty space (beat already snapped)
        public event Action<SectionMarker> MarkerClicked;                   // single click (no drag)
        public event Action<SectionMarker> MarkerDoubleClicked;             // rename
        public event Action<SectionMarker> DragStarted;                     // host captures its undo pre-state
        public event Action<SectionMarker, double> MarkerDropped;           // drop (beat already snapped + clamped)
        public event Action<SectionMarker, FrameworkElement> ContextRequested;

        public void Configure(double width, double height, double pxPerBeat,
                              IList<SectionMarker> markers,
                              Func<double, double> snap,        // beat -> nearest barline beat (clamped)
                              Func<SectionMarker, string> tooltip,
                              string emptyHint);
    }
}
```

Règles de dessin (`Redraw()` — vider `Children` puis reconstruire) :

- fond **non nul** (obligatoire : une `Canvas` sans `Background` ne reçoit aucun événement souris) —
  `#1B1B22`, légèrement plus sombre que la règle (`#1F1F28`) pour lire la séparation ;
- `ToolTip = emptyHint` sur la bande elle-même (§2 du fonctionnel : « une infobulle sur la bande explique
  comment en ajouter ») ;
- travailler sur une **copie triée par `Beat`** pour calculer le voisin de droite ;
- un repère n'est dessiné **que si** `beat * pxPerBeat < width` → un repère au-delà de la fin du morceau
  reste dans le modèle mais disparaît de l'écran (§4.5, critère 22) ;
- chaque repère = **un seul** élément cliquable, `Background = Brushes.Transparent`, `Cursor = Hand`,
  positionné par `Canvas.SetLeft(el, beat * pxPerBeat)` :
  - un trait vertical de 1 px sur toute la hauteur, à x = 0 de l'élément (repère visuel exact de la barre),
  - un `Polygon` « fanion » (p. ex. `(1,1) (10,1) (10,6) (5.5,11) (1,6)`) en `AccentBrush` `#1FB6C3`,
  - un `TextBlock` (FontSize 10, `TextTrimming = CharacterEllipsis`, `Foreground` clair) à droite ;
- **largeur du libellé** = `next.Beat * pxPerBeat - beat * pxPerBeat - 12` (ou jusqu'à `width` pour le
  dernier), **passée par `Math.Max(0, …)`** : une largeur négative sur un `FrameworkElement.Width` lève
  `ArgumentException`, et deux repères à la même position (§4.6, fichier édité à la main) produisent
  exactement ce cas ;
- `ToolTip = tooltip(marker)` sur l'élément (nom complet + mesure, §3.1) ;
- pas de redessin pendant le glissement : seul `Canvas.SetLeft` de l'élément tiré bouge, et
  `Panel.SetZIndex(el, 50)` le passe au premier plan (comme `ModuleBoxControl`).

Gestes (recopier la mécanique de `ModuleBoxControl` l. 55-91) :

| Geste | Traitement |
|---|---|
| `MouseLeftButtonDown` sur un repère, `ClickCount == 2` | `e.Handled = true`, annuler le glissement en cours (`pressed = false`, `ReleaseMouseCapture`), `MarkerDoubleClicked` |
| `MouseLeftButtonDown` sur un repère, `ClickCount == 1` | `e.Handled = true`, mémoriser `pressPos`/`pressLeft`, `CaptureMouse`, `DragStarted` **non** émis ici |
| `MouseMove` avec bouton enfoncé | au-delà de `DragThreshold = 4` px : première fois → `dragging = true` + `DragStarted(m)` ; puis `Canvas.SetLeft(el, Math.Max(0, pressLeft + dx))` |
| `MouseLeftButtonUp` | si `dragging` → `MarkerDropped(m, snap(Canvas.GetLeft(el) / pxPerBeat))` ; sinon → `MarkerClicked(m)` |
| `MouseRightButtonUp` sur un repère | `e.Handled = true`, `ContextRequested(m, el)` |
| `MouseLeftButtonDown` sur le fond, `ClickCount == 2` | `CreateRequested(snap(e.GetPosition(this).X / pxPerBeat))` |
| clic droit sur le fond | **aucun handler** → aucun menu (§3.6) |

Le `e.Handled = true` des éléments-repères est ce qui garantit que le double-clic « zone vide » ne se
déclenche pas quand on double-clique un fanion.

> **Comportement assumé et à documenter :** WPF émet `ClickCount == 1` puis `ClickCount == 2`. Un
> double-clic sur un repère commence donc par un `MarkerClicked` → le point de départ de lecture se pose
> sur ce repère avant l'ouverture du renommage. C'est inoffensif (§3.3 fait exactement cela) et évite
> d'introduire un timer de temporisation du simple clic. Ne **pas** « corriger » cela avec un
> `DispatcherTimer` : le retard perçu au clic simple coûterait plus que le bénéfice.

### 4.2 `MusicTracker\Engine\Timeline\TimelineHelper.cs` — 3 helpers de grille

À ajouter à côté de `RulerBeatsPerBar` (l. 79). Ce sont les **seules** nouvelles règles métier ; elles
reproduisent mot pour mot la l. 28 de `MeasureRulerControl` :

```csharp
// Phase of the barline grid = the anacrusis remainder folded into one bar (mirrors MeasureRulerControl.Configure).
public static double BarPhase(TimelineProject p)
{
    int bpb = Math.Max(1, RulerBeatsPerBar(p));
    return p != null && p.PickupBeats > 1e-6 ? p.PickupBeats % bpb : 0;
}

// Nearest barline of the RULER grid (pickup bar included, at beat 0). Never negative.
public static double SnapToBarline(TimelineProject p, double beat)
{
    int bpb = Math.Max(1, RulerBeatsPerBar(p));
    double phase = BarPhase(p);
    if (beat < 0) beat = 0;
    if (phase > 1e-6 && beat < phase * 0.5) return 0;      // closer to the pickup barline
    double m = Math.Round((beat - phase) / bpb);
    if (m < 0) m = 0;
    return phase + m * bpb;
}

// Bar index at a beat: -1 = the pickup bar, else the 0-based full-bar index (displayed number = +1).
public static int BarIndexAt(TimelineProject p, double beat)
{
    int bpb = Math.Max(1, RulerBeatsPerBar(p));
    double phase = BarPhase(p);
    if (phase > 1e-6 && beat < phase - 1e-6) return -1;
    return (int)Math.Floor((beat - phase) / bpb + 1e-6);
}
```

`MeasureRulerControl` n'est **pas** modifié (il recalcule `phase` en interne). La duplication de la formule
est assumée : la factoriser obligerait à toucher un contrôle de dessin stable, pour zéro gain fonctionnel.
Mettre un commentaire `// keep in sync with MeasureRulerControl.Configure` des deux côtés — non, **d'un
seul côté** (dans `BarPhase`), pour ne modifier aucun fichier existant sans nécessité.

### 4.3 `MusicTracker\Screens\TimelineScreen.xaml`

Deux modifications, toutes deux dans le bloc l. 394-408.

1. **Le coin** (l. 395-398) devient deux bandes empilées, 18 + 20 px :

```xml
<Grid Grid.Row="0" Grid.Column="0">
    <Grid.RowDefinitions>
        <RowDefinition Height="18"/>
        <RowDefinition Height="20"/>
    </Grid.RowDefinitions>
    <Border Grid.Row="0" Background="{StaticResource DarkBackground}"
            BorderBrush="{StaticResource CommonBorderBrush}" BorderThickness="0,0,1,1">
        <TextBlock Text="{loc:Tr 'MarkersLaneTitle'}" Foreground="{StaticResource SecondaryForeground}"
                   FontSize="10" FontWeight="SemiBold" VerticalAlignment="Center" Margin="8,0,0,0"/>
    </Border>
    <!-- le Border "BARS" existant, inchangé, passe en Grid.Row="1" (retirer son Height="20") -->
</Grid>
```

2. **`rulerScroll`** (l. 401-408) : `Height="20"` → `Height="38"`, et son contenu passe de la `Grid` unique
   au `StackPanel` montré au §2.1. **Ne pas toucher** à `VerticalScrollBarVisibility="Disabled"`,
   `HorizontalScrollBarVisibility="Hidden"`, ni au `x:Name`.

Rien d'autre ne bouge dans le XAML : pas de menu, pas de bouton (§2 du fonctionnel).

### 4.4 `MusicTracker\Screens\TimelineScreen.xaml.cs`

Environ 150 lignes, regroupées dans **une seule région** placée juste après le bloc « A-B loop end (B)
marker » (après la l. 747), là où vivent déjà `startBeat`, `loopEndBeat` et `loopEnabled`.

**a) Constantes / champs** (à côté de `LaneH, TempoH, …` l. 29) :

```csharp
const double MarkerLaneH = 18;   // the section-marker band, above the 20px ruler
string markerDragPre;            // undo pre-state captured when a marker drag starts
```

**b) Câblage — UNE SEULE FOIS, dans le constructeur** (après `InitializeComponent()`, l. 76) :

```csharp
markerLane.CreateRequested     += MarkerCreateAt;
markerLane.MarkerClicked       += MarkerGoTo;
markerLane.MarkerDoubleClicked += MarkerRename;
markerLane.DragStarted         += m => markerDragPre = BeginUndo();
markerLane.MarkerDropped       += MarkerDrop;
markerLane.ContextRequested    += ShowMarkerContextMenu;
```

> **Piège :** `markerLane` est une instance unique créée par `InitializeComponent`. Câbler dans `Render()`
> empilerait un handler de plus à chaque rendu → au bout de dix rendus, un double-clic ouvrirait dix
> dialogues. C'est l'erreur d'implémentation la plus probable de cette feature.
>
> **Piège symétrique :** la *liste* `project.Markers`, elle, doit être **repassée à chaque
> `RefreshMarkers()`** et surtout pas mémorisée une fois pour toutes. `ApplyDocument` fait
> `project.Markers = dp.Markers ?? …`, c'est-à-dire qu'il **remplace l'instance de liste** (comme il le fait
> déjà pour `Tempo`, `UserChordStyles`, etc.). Un contrôle qui aurait gardé la référence reçue au démarrage
> dessinerait éternellement les repères de l'ancien document après une ouverture ou un Ctrl+Z. Le
> `Configure(…, project.Markers, …)` de `RefreshMarkers` (appelé depuis `Render`, lui-même appelé par
> `RestoreState`) règle le problème — à condition que `MarkerLaneControl` ne conserve la liste que jusqu'au
> `Configure` suivant.

**c) Rafraîchissement** — une méthode, appelée **partout où la largeur de la règle est recalculée**, plus
après chaque mutation de repère. La règle mnémotechnique est simple et vérifiable au `grep` :

> **partout où le code fait `startCanvas.Width = laneWidth;`, ajouter `RefreshMarkers();` juste après.**

`git grep "startCanvas.Width = laneWidth"` donne **exactement trois** sites, et il n'y en a pas d'autre :

| Site | Ligne | Quand |
|---|---|---|
| `Render()` | 1120 (après `measureRuler.Configure(…)` l. 1119) | rendu complet (ouverture, undo/redo, toute mutation structurelle) |
| `RenderBatched()` | 1176 | ouverture d'un `.sq` (rendu progressif) |
| `RefreshTrackLane(track)` | 2384 | **rafraîchissement EN PLACE d'une seule piste** — utilisé quand l'édition d'un riff change sa longueur (`CommitRiffEditor` l. 2368) ; il élargit la règle **sans** passer par `Render()` |

⚠️ Le troisième est celui qu'on oublie. Sans lui, éditer un riff pour rallonger le morceau élargit la règle
mais **pas** le bandeau : un repère situé au-delà de l'ancienne fin ne réapparaît pas (critère 22 en échec
sur ce chemin-là), et l'étendue scrollable de `rulerScroll` reste calée sur l'ancienne largeur.

```csharp
void RefreshMarkers()
{
    if (markerLane == null) return;
    markerLane.Configure(TotalBeats() * PxPerBeat, MarkerLaneH, PxPerBeat, project.Markers,
                         b => TimelineHelper.SnapToBarline(project, ClampBeat(b)),
                         MarkerTooltip, Loc.T("MarkersLaneHint"));
}

// Clamp to the drawable timeline, then let SnapToBarline round to a real barline (§3.5: dropped out of
// bounds -> nearest valid bar).
double ClampBeat(double b) => Math.Max(0, Math.Min(TotalBeats(), b));

string MarkerTooltip(SectionMarker m)
{
    int i = TimelineHelper.BarIndexAt(project, m.Beat);
    string bar = i < 0 ? Loc.T("MarkerPickupBar") : Loc.T("MarkerBar") + " " + (i + 1);
    return (m.Name ?? "") + " — " + bar;
}
```

**d) Création / renommage**

```csharp
void MarkerCreateAt(double beat)
{
    var existing = MarkerAt(beat);
    if (existing != null) { MarkerRename(existing); return; }        // §3.2: no duplicate, rename instead
    string name = TimelineHelper.PromptText(Loc.T("MarkerNewTitle"), NextMarkerName());
    if (string.IsNullOrWhiteSpace(name)) return;                     // Cancel or blank = no marker
    PushUndo("marker:add");
    project.Markers.Add(new SectionMarker { Beat = beat, Name = name.Trim() });
    SortMarkers();
    RefreshMarkers();
}

void MarkerRename(SectionMarker m)
{
    string name = TimelineHelper.PromptText(Loc.T("MarkerRenameTitle"), m.Name);
    if (string.IsNullOrWhiteSpace(name)) return;
    if (name.Trim() == m.Name) return;                               // no-op: don't pollute the history
    PushUndo("marker:rename");
    m.Name = name.Trim();
    RefreshMarkers();
}

SectionMarker MarkerAt(double beat)
{
    foreach (var m in project.Markers) if (Math.Abs(m.Beat - beat) < 1e-6) return m;
    return null;
}

void SortMarkers() => project.Markers.Sort((a, b) => a.Beat.CompareTo(b.Beat));

// "Repère N" with the smallest free N (localized prefix).
string NextMarkerName()
{
    string prefix = Loc.T("MarkerDefaultName");
    var used = new System.Collections.Generic.HashSet<int>();
    foreach (var m in project.Markers)
    {
        string s = (m.Name ?? "").Trim();
        if (s.StartsWith(prefix + " ", StringComparison.Ordinal)
            && int.TryParse(s.Substring(prefix.Length + 1).Trim(), out int n)) used.Add(n);
    }
    int k = 1; while (used.Contains(k)) k++;
    return prefix + " " + k;
}
```

**e) Navigation** — copie conforme de `SetStartFromX` sans l'arrondi :

```csharp
void MarkerGoTo(SectionMarker m)
{
    startBeat = ClampBeat(m.Beat);
    MoveCursor(startBeat);          // playback, if any, is NOT interrupted (see §1.3)
}
```

**f) Déplacement** — **une seule** entrée d'undo par glissement, sans dépendre de la coalescence :

```csharp
void MarkerDrop(SectionMarker m, double beat)
{
    string pre = markerDragPre; markerDragPre = null;
    var occupant = MarkerAt(beat);
    if (Math.Abs(beat - m.Beat) < 1e-6 || (occupant != null && occupant != m))
    {
        RefreshMarkers();           // §3.5: unchanged, or target bar taken -> snap back, no history entry
        return;
    }
    CommitUndo(pre, "marker:move"); // key deliberately NOT prefixed "move:" -> never coalesced with another marker's drag
    m.Beat = beat;
    SortMarkers();
    RefreshMarkers();
}
```

`BeginUndo()` (émis par `DragStarted`) appelle `FlushPending()` puis capture le snapshot ; `CommitUndo`
ignore un `pre` nul (cas `restoringUndo`). Le redessin depuis le modèle suffit à ramener visuellement le
fanion à sa place quand le déplacement est refusé.

**g) Menu contextuel** — même forme que `ShowItemContextMenu` (l. 3431) :

```csharp
void ShowMarkerContextMenu(SectionMarker m, FrameworkElement anchor)
{
    var menu = new ContextMenu();
    var ren  = new MenuItem { Header = Loc.T("MarkerMenuRename") }; ren.Click  += (s, e) => MarkerRename(m);
    var loop = new MenuItem { Header = Loc.T("MarkerMenuLoop")   }; loop.Click += (s, e) => MarkerLoopSection(m);
    var del  = new MenuItem { Header = Loc.T("MarkerMenuDelete") }; del.Click  += (s, e) => MarkerDelete(m);
    menu.Items.Add(ren); menu.Items.Add(loop); menu.Items.Add(new Separator()); menu.Items.Add(del);
    menu.PlacementTarget = anchor; menu.IsOpen = true;
}

void MarkerDelete(SectionMarker m)
{
    PushUndo("marker:del");         // NOT "delete:" -> no accidental neutralization against a module insert
    project.Markers.Remove(m);
    RefreshMarkers();
}
```

**h) « Boucler cette section »** — calqué sur `btnLoop_Click` (l. 733-747), **sans undo** (état d'interface) :

```csharp
void MarkerLoopSection(SectionMarker m)
{
    int bpb = Math.Max(1, TimelineHelper.RulerBeatsPerBar(project));
    double a = ClampBeat(m.Beat);
    double b = double.NaN;
    foreach (var o in project.Markers) if (o.Beat > a + 1e-6 && (double.IsNaN(b) || o.Beat < b)) b = o.Beat;
    if (double.IsNaN(b)) b = PieceEndBeats();                 // last marker -> end of the piece (§4.8)
    if (b <= a + 1e-6) b = a + bpb;                            // never empty nor inverted: at least one bar
    startBeat = a; loopEndBeat = b; loopEnabled = true;
    if (btnLoop != null) btnLoop.IsChecked = true;
    EnsureCursor();
    if (player != null) { player.Loop = true; player.LoopEndBeat = loopEndBeat; player.ApplyLoop(); }
    MoveCursor(player != null ? PlayedBeat() : startBeat);
}

// Musical end of the piece = the latest track end, floored by MinBeats — WITHOUT the +8 beats of display
// slack that TotalBeats() adds (l. 1081-1086).
double PieceEndBeats()
{
    double end = project.MinBeats;
    foreach (var t in project.Tracks) end = Math.Max(end, SeqDispLen(t.Items));
    return end;
}
```

**i) `ApplyDocument`** : la ligne du §3.2. C'est tout.

### 4.5 `MusicTracker\MusicTracker.csproj`

Une seule ligne, dans le `ItemGroup` des `<Compile>`, à côté des autres contrôles code-only (l. 319-324) :

```xml
<Compile Include="Controls\TimelineEditor\MarkerLaneControl.cs" />
```

Aucune entrée `<Page>` (pas de XAML), aucune entrée pour les `lang.*.json` (déjà déclarés en `<Content>`,
l. 610-632).

### 4.6 Ce qui n'est **pas** touché

`MeasureRulerControl`, `TempoLaneControl`, `ChordLaneControl`, `VolumeLaneControl`, `ModuleBoxControl`,
`UndoManager`, `TimelinePlayer`, `LookaheadBuffer`, `ScoreBuilder`/`ScoreView`, tous les exporteurs,
`TimelineImporter`, `TemplateProjectBuilder`, `Orchestrateur`, `GuidedTour` (pas de nouvelle étape de
visite guidée : §5 du fonctionnel n'en demande pas), `MainWindow`, `AppSettings`, `UserData`.
Le `CHANGELOG` est mis à jour par le run de publication, pas ici.

---

## 5. Localisation — 10 clés à créer dans les **7** fichiers

`MusicTracker\Localization\lang.{fr,en,de,it,es,nl,pt}.json`. Ajouter les 10 clés **dans le même ordre**
dans chaque fichier (n'importe où : le fichier est un dictionnaire plat ; en pratique, à la fin, avant
l'accolade fermante). ⚠️ **Éditer ces fichiers avec l'outil Write/Edit, jamais via
`Get-Content`/`Set-Content` PowerShell** — cela transforme les accents en mojibake (voir la note du dépôt).

| Clé | fr | en | de |
|---|---|---|---|
| `MarkersLaneTitle` | Repères | Markers | Marken |
| `MarkersLaneHint` | Double-cliquez pour ajouter un repère de section. | Double-click to add a section marker. | Doppelklick, um eine Abschnittsmarke hinzuzufügen. |
| `MarkerDefaultName` | Repère | Marker | Marke |
| `MarkerNewTitle` | Nouveau repère | New marker | Neue Marke |
| `MarkerRenameTitle` | Renommer le repère | Rename marker | Marke umbenennen |
| `MarkerMenuRename` | Renommer… | Rename… | Umbenennen… |
| `MarkerMenuLoop` | Boucler cette section | Loop this section | Diesen Abschnitt loopen |
| `MarkerMenuDelete` | Supprimer | Delete | Löschen |
| `MarkerBar` | mesure | bar | Takt |
| `MarkerPickupBar` | levée | pickup | Auftakt |

| Clé | it | es | nl | pt |
|---|---|---|---|---|
| `MarkersLaneTitle` | Marcatori | Marcadores | Markeringen | Marcadores |
| `MarkersLaneHint` | Doppio clic per aggiungere un marcatore di sezione. | Haz doble clic para añadir un marcador de sección. | Dubbelklik om een sectiemarkering toe te voegen. | Faça duplo clique para adicionar um marcador de secção. |
| `MarkerDefaultName` | Marcatore | Marcador | Markering | Marcador |
| `MarkerNewTitle` | Nuovo marcatore | Nuevo marcador | Nieuwe markering | Novo marcador |
| `MarkerRenameTitle` | Rinomina marcatore | Renombrar marcador | Markering hernoemen | Renomear marcador |
| `MarkerMenuRename` | Rinomina… | Renombrar… | Hernoemen… | Renomear… |
| `MarkerMenuLoop` | Loop di questa sezione | Repetir esta sección en bucle | Deze sectie herhalen | Repetir esta secção em ciclo |
| `MarkerMenuDelete` | Elimina | Eliminar | Verwijderen | Eliminar |
| `MarkerBar` | misura | compás | maat | compasso |
| `MarkerPickupBar` | anacrusi | anacrusa | opmaat | anacruse |

Notes :

- `MarkerDefaultName` est un **préfixe** : le nom proposé est `préfixe + " " + N`. `NextMarkerName()`
  n'inspecte que les noms qui commencent par le préfixe **de la langue courante** ; changer de langue en
  cours de projet fait donc repartir la numérotation à 1 pour la nouvelle langue. Comportement acceptable
  (les noms déjà posés ne sont jamais retraduits) et conforme au fonctionnel, qui ne dit rien de ce cas.
- `TimelineHelper.PromptText` (l. 1260-1261) code en dur le libellé du bouton **« Annuler »** (le bouton OK
  est neutre). C'est un défaut **préexistant**, partagé avec les dialogues « enregistrer le style
  d'accompagnement » et « enregistrer le motif batterie ». Correctif optionnel d'une ligne
  (`Content = Loc.T("Annuler")`, clé déjà présente dans les 7 fichiers) — à faire **ou pas**, mais si on le
  fait, le signaler au testeur car cela change aussi deux dialogues existants.
- Aucune chaîne visible ne doit rester en dur dans `MarkerLaneControl.cs` : ce contrôle ne référence pas
  `Loc`, tous ses textes arrivent par `Configure`.

---

## 6. Risques, régressions, et ce qui les empêche

| # | Risque | Gravité | Ce qui l'empêche |
|---|---|---|---|
| 1 | **Repères perdus à l'ouverture et à chaque Ctrl+Z** (champ oublié dans `ApplyDocument`) | bloquant | §3.2 : la ligne est explicitée ; tests 17 et 18 la couvrent. C'est le risque n° 1. |
| 2 | **Handlers empilés** (câblage dans `Render()` au lieu du constructeur) → N dialogues sur un double-clic | bloquant, sournois (n'apparaît qu'après plusieurs rendus) | §4.4 b : câblage unique dans le constructeur ; test 6 bis (double-cliquer après avoir joué/annulé plusieurs fois). |
| 2bis | **Bandeau non rafraîchi par `RefreshTrackLane`** (3ᵉ site de recalcul de largeur, l. 2384) : après une édition de riff qui rallonge le morceau, un repère hors champ ne réapparaît pas | moyen, très facile à manquer | §4.4 c : la règle « un `RefreshMarkers()` derrière chaque `startCanvas.Width = laneWidth` », vérifiable au `grep` (3 occurrences) ; test H20 bis. |
| 2ter | **Liste `Markers` mémorisée** par le contrôle : `ApplyDocument` remplace l'instance de liste, le bandeau continue d'afficher les repères de l'ancien document (visible après un Ctrl+Z ou une ouverture) | haute | §4.4 b : `Configure` repasse `project.Markers` à chaque rafraîchissement ; le contrôle ne garde la référence que jusqu'au `Configure` suivant. Test H18 enchaîné après H17. |
| 3 | **Désalignement bandeau / règle au défilement** | critère 3 | Approche §2.1 : même `ScrollViewer`, donc même offset. Aucun code de synchro ajouté. |
| 4 | Les poignées bleue/orange cessent de fonctionner (coordonnées décalées par le nouveau `StackPanel`) | haute | `startCanvas` garde sa `Grid` parente et son origine ; les handlers utilisent `e.GetPosition(startCanvas)`, pas la fenêtre. Test 10 + test de non-régression sur le glissement des deux poignées. |
| 5 | `ArgumentException` sur `Width` négative quand deux repères sont confondus ou très proches | crash | `Math.Max(0, …)` sur la largeur du libellé (§4.1) ; test 15 et cas §4.6. |
| 6 | La bande avale le clic droit sur zone vide et ouvre un menu vide | critère §3.6 | Aucun handler de clic droit sur le fond — seuls les éléments-repères en ont un. |
| 7 | Le clic droit sur un repère ouvre **aussi** le menu d'un module (bubbling) | moyen | `e.Handled = true` dans le handler du repère ; de toute façon la bande n'est pas au-dessus des pistes. |
| 8 | Coalescence d'undo inattendue : deux glissements de repères différents fusionnés en une entrée | critère 14 | Clés `marker:move` / `marker:add` / `marker:del` / `marker:rename` : aucune ne commence par `move:`, `edit:`, `vol:`, `insert:` ou `delete:` → `UndoManager.IsCoalescable` (l. 66-67) renvoie faux et la neutralisation ne se déclenche jamais. |
| 9 | Snapshots d'undo coûteux si l'on pousse à chaque pixel du glissement | perf | `BeginUndo` **au franchissement du seuil**, `CommitUndo` **au lâcher** : exactement 1 sérialisation par glissement (contre N dans `MoveInList`). |
| 10 | Ralentissement du défilement avec beaucoup de repères (§4.7) | faible | `Redraw` n'est appelé que par `RefreshMarkers` (rendu / mutation), jamais pendant le glissement ni pendant le défilement ; les repères hors largeur ne sont pas créés. |
| 11 | Levée modifiée après coup : repères entre deux barres | assumé (§4.2) | Aucun recalage automatique. Le dessin utilise `beat * PxPerBeat` brut, donc l'affichage reste exact ; l'infobulle donne la mesure recalculée via `BarIndexAt`. À mentionner dans la doc utilisateur. |
| 12 | Un `.sq` récent ouvert par une version antérieure | §4.1 | `System.Text.Json` ignore les propriétés inconnues par défaut : rien à faire, mais **à vérifier** (test A4). |
| 13 | Exports modifiés | critère 20 | Aucun exporteur ne lit `Markers` ; test A3 compare les fichiers produits. |
| 14 | La zone des pistes perd 18 px de hauteur | cosmétique | Ligne 1 de la `Grid` racine est en `*` avec `MinHeight="120"` ; le `GridSplitter` continue de fonctionner. À regarder à l'œil (test H1). |
| 15 | Accents détruits dans les `lang.*.json` | bloquant | Interdiction d'utiliser `Get-Content`/`Set-Content` (§5) ; test A5 relit les 7 fichiers en UTF-8 et compare les valeurs attendues. |

---

## 7. Plan de test

**Préparation obligatoire, à faire AVANT d'appliquer le correctif :** compiler la version actuelle,
créer un morceau de quelques dizaines de mesures (au moins une piste d'instrument + la piste Accords) et
l'enregistrer sous `temoin-avant.sq`. Ce fichier est le **témoin « fichier antérieur »** du critère 19 ;
le dépôt n'en contient aucun. Exporter aussi ce morceau en MIDI, `.mscx` et audio → `temoin-avant.*`
(référence du critère 20).

Build : `"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
MusicTracker.sln /p:Configuration=Debug` — **jamais `dotnet build`** (csproj ancien format, .NET Framework 4.8).

### 7.1 Vérifiable automatiquement (build / script / inspection de fichier)

| # | Vérification | Méthode | Critère couvert |
|---|---|---|---|
| A1 | La solution compile sans erreur ni nouvel avertissement | MSBuild | — |
| A2 | Un `.sq` enregistré avec 3 repères contient bien `"Markers":[…]` avec les 3 objets `Beat`/`Name`, accents et apostrophes intacts (« Thème A — reprise l'octave ») | lire le JSON du `.sq` (UTF-8) et comparer | 18 |
| A3 | Les exports MIDI / `.mscx` / audio du morceau **avec** repères sont identiques à `temoin-avant.*` | comparaison binaire (le MIDI et le `.mscx` sont déterministes ; pour l'audio, comparer la taille + un hash) | 20 |
| A4 | Un `.sq` **contenant** des repères, ouvert par un binaire antérieur, ne lève rien | lancer l'ancien exe sur le fichier ; à défaut, désérialiser le JSON avec un `TimelineDocument` privé de `Markers` | §4.1 |
| A5 | Les 10 clés existent dans les **7** `lang.*.json`, avec des valeurs non vides et **différentes du français** pour les 6 autres langues | script : charger les 7 JSON en UTF-8, vérifier la présence des clés et l'absence de valeur identique au fr (hors homographes légitimes : `MarkerMenuDelete` es/pt = « Eliminar ») | 23 (partiel) |
| A6 | Aucune régression du harnais FlaUI (`AutoTest\run.ps1`) : l'application démarre et les scénarios existants restent verts | `AutoTest` | — |
| A7 | Nombre de repères invariant après un glissement refusé | ouvrir le `.sq` réenregistré et compter les entrées de `Markers` | 15 |
| A8 | `git grep` ne trouve aucune chaîne française littérale dans `MarkerLaneControl.cs` | inspection | 23 |

### 7.2 Exige un jugement humain (rendu visuel, rendu sonore, ressenti du geste)

| # | Vérification | Attendu | Critère |
|---|---|---|---|
| H1 | Ouvrir n'importe quel morceau | bandeau fin visible **au-dessus** de la règle, libellé « Repères » à gauche ; la zone des pistes reste confortable, le splitter fonctionne | 1, 2 |
| H2 | Morceau sans repère | bandeau vide, infobulle explicative au survol ; rien d'autre n'a changé | 2 |
| H3 | Défiler horizontalement jusqu'au bout | le fanion reste **pile** sur sa barre à toutes les positions, y compris en fin de course | 3 |
| H4 | Double-clic zone vide vers la mesure 5 | dialogue pré-rempli « Repère 1 » ; OK → fanion **turquoise** aligné sur la barre 5 ; Annuler / nom vide → rien | 4, 5, 6, 7 |
| H5 | Créer un 2ᵉ repère | proposé « Repère 2 » ; supprimer le 1er puis créer → « Repère 1 » | 8 |
| H6 | Double-clic zone vide sur une mesure déjà occupée | ouvre le **renommage** du repère existant, pas de doublon | 9 |
| H7 | Clic simple sur un repère, puis ▶ | poignée bleue et curseur sur la mesure du repère ; la lecture démarre bien là | 10 |
| H8 | Clic droit ▸ Boucler cette section, sur un repère suivi d'un autre | ⟳ s'active, A sur ce repère, B sur le suivant ; **à l'oreille** : la boucle tourne sur la bonne section, sans clic ni trou | 11 |
| H9 | Idem sur le **dernier** repère | B en fin de morceau, boucle non vide ; à l'oreille : elle boucle | 12 |
| H10 | Faire la manœuvre H8 **pendant** la lecture | la boucle est prise en compte immédiatement, sans coupure audio | §3.6 |
| H11 | Double-clic sur un repère | dialogue pré-rempli ; OK renomme ; deux repères peuvent porter le même nom | 13, §3.4 |
| H12 | Glisser un repère de la mesure 5 à la 9 | il se cale sur les barres pendant le geste (pas de position intermédiaire flottante à l'arrêt) ; **un seul** Ctrl+Z le ramène à la 5 | 14 |
| H13 | Glisser un repère sur une mesure occupée | retour à l'origine, aucun repère perdu ni dupliqué | 15 |
| H14 | Glisser un repère au-delà des bornes | ramené à la mesure valide la plus proche | §3.5 |
| H15 | Clic droit ▸ Supprimer, puis Ctrl+Z | suppression sans confirmation ; Ctrl+Z restaure le repère **avec son nom** | 16 |
| H16 | Ctrl+Z après une création, puis Ctrl+Y | le repère disparaît puis revient | 17 |
| H17 | Enregistrer 3 repères (dont un nom accentué et apostrophé), fermer, rouvrir | les 3 sont là, mêmes mesures, noms **exactement** identiques | 18 |
| H18 | Ouvrir `temoin-avant.sq` | aucune erreur, aucun message, bandeau vide, morceau lu comme avant | 19 |
| H19 | Morceau **avec levée** : poser un repère au tout début et un plus loin | fanions alignés sur les barres décalées de la règle ; l'infobulle du premier indique la **levée**, les autres « mesure N » | 21 |
| H20 | Supprimer des modules jusqu'à raccourcir le morceau au-delà d'un repère, puis rallonger | aucune erreur ; le repère disparaît puis **réapparaît à sa mesure d'origine** | 22 |
| H20 bis | Même chose mais en rallongeant **par l'éditeur de riff** (allonger le riff le plus tardif) au lieu d'ajouter un module — c'est le chemin `RefreshTrackLane` (§4.4 c) | le repère réapparaît aussi ; le bandeau s'élargit avec la règle | 22 |
| H21 | Beaucoup de repères rapprochés | noms tronqués avec « … », tous les fanions restent cliquables, le défilement ne rame pas | §4.7 |
| H22 | Basculer dans les 7 langues | libellé de la bande, nom par défaut, titres de dialogue, entrées du menu contextuel et infobulles traduits ; aucune clé brute, aucun français résiduel | 23 |
| H23 | Non-régression des poignées | la poignée bleue et la poignée orange se glissent toujours, le clic sur la règle pose toujours le départ | — |

### 7.3 Ce qui ne pourra pas être vérifié par un run automatisé

Les gestes souris fins (double-clic, glisser-déposer sur une `Canvas`, menu contextuel) ne sont pas
pilotables de façon fiable par UIA : le bandeau ne contient pas d'éléments d'automatisation nommés.
Les points H1 à H23 ci-dessus sont donc **tous** à considérer comme manuels, sauf si l'implémenteur ajoute
un `AutomationProperties.AutomationId` sur le bandeau et sur chaque fanion — ce qui n'est **pas** demandé
et alourdirait le contrôle. À noter tel quel dans `03-tests.md`.

---

## 8. Estimation

**Moyen** — une session.

| Poste | Volume |
|---|---|
| `TimelineProject.cs` | ~12 lignes (classe `SectionMarker` + propriété `Markers`) |
| `TimelineHelper.cs` | ~25 lignes (3 helpers de grille) |
| `Controls\TimelineEditor\MarkerLaneControl.cs` (nouveau) | ~180 lignes |
| `TimelineScreen.xaml` | ~15 lignes modifiées (coin + `rulerScroll`) |
| `TimelineScreen.xaml.cs` | ~150 lignes ajoutées + **1 ligne dans `ApplyDocument`** + **3** appels à `RefreshMarkers()` (`Render`, `RenderBatched`, `RefreshTrackLane`) + 6 lignes de câblage dans le constructeur |
| `MusicTracker.csproj` | 1 ligne |
| `lang.{fr,en,de,it,es,nl,pt}.json` | 10 clés × 7 fichiers |

Le risque n'est pas dans le volume mais dans les **quatre** pièges identifiés :

1. la ligne dans `ApplyDocument` (§3.2) — sans elle, les repères disparaissent à l'ouverture **et à chaque
   Ctrl+Z** ;
2. le câblage des événements **une seule fois, dans le constructeur** (§4.4 b) ;
3. les **trois** appels à `RefreshMarkers()`, dont celui de `RefreshTrackLane` (§4.4 c), et le fait de
   repasser `project.Markers` à chaque fois plutôt que de le mémoriser ;
4. le choix des clés d'undo, qui ne doivent commencer ni par `move:`, ni par `edit:`, ni par `vol:`, ni par
   `insert:`, ni par `delete:` (§1.4).

Un implémenteur qui traite ces quatre points en premier a fait le plus dur ; le reste est du dessin et du
câblage sans piège.
