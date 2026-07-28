# Zoom horizontal de la timeline — analyse technique

Référence fonctionnelle : `01-fonctionnel.md` (même dossier). Ce document décrit **comment** implémenter,
et n'ajoute aucune exigence fonctionnelle nouvelle. Les numéros de ligne ont été relevés dans les sources
au moment de l'analyse ; ils peuvent avoir bougé de quelques lignes, les extraits cités permettent de
retrouver l'emplacement exact.

Contraintes de contexte à ne pas oublier : .NET Framework 4.8, csproj ancien format, build **msbuild**
(jamais `dotnet build`) ; ne jamais éditer un source avec `Get-Content`/`Set-Content` PowerShell.

---

## 1. Ce que le code fait déjà (vérifié dans les sources)

### 1.1 L'échelle du temps est déjà centralisée en un seul symbole

`MusicTracker\Screens\TimelineScreen.xaml.cs`, l. 31 :

```csharp
const double PxPerBeat = 60; // box width per beat (a 4/4 measure ≈ 240 px); RiffThumbnail must match
```

**C'est le point unique de la fonctionnalité.** Absolument tout ce qui est dessiné ou mesuré contre le
temps dans l'éditeur de morceau passe par ce symbole. Relevé exhaustif des 24 sites d'usage :

| Ligne(s) | Rôle | Sens |
|---|---|---|
| 552, 555 | `MoveCursor` : X du curseur jaune, de la poignée bleue, de la poignée orange | temps → px |
| 681 | `SetStartFromX` : clic/glisser sur la règle → `startBeat` (arrondi au temps) | px → temps |
| 708, 711 | `SetLoopEndFromX` : poignée B de la boucle A-B | px → temps, temps → px |
| 1117, 1119 | `Render` : `laneWidth = TotalBeats() * PxPerBeat`, puis `measureRuler.Configure(...)` | temps → px |
| 1174-1175, 1196 | `RenderBatched` (chargement progressif) : idem + `VolumeLaneControl.Configure` | temps → px |
| 1264 | `MakeTrackRow` : `VolumeLaneControl.Configure` | temps → px |
| 1381 | `MakeTempoLane` : `TempoLaneControl.Configure` | temps → px |
| 1489 | `MakeChordLane` : `ChordLaneControl.Configure` (trame d'accords) | temps → px |
| 1545-1548 | `MakeTrackLane` : graduations de barre du fond de piste | temps → px |
| 1572-1575, 1585-1588 | `MakeCollapsedLane` : graduations + mini-rectangles d'une piste repliée | temps → px |
| 1617 | `AddItem` : callback de dépose `nl => MoveInList(..., nl / PxPerBeat)` | px → temps |
| 1715, 1759 | `MakeLeafBox` : largeur et `Canvas.Left` d'une boîte de module | temps → px |
| 1988 | `LocateRiffAtBeat` : défilement vers un module depuis la partition | temps → px |
| 2382-2383, 2399, 2402 | `RefreshTrackLane` : re-rendu d'une seule piste | temps → px |

**Toutes ces méthodes sont des méthodes d'instance** (vérifié une par une : `MakeLeafBox`,
`MakeTrackLane`, `MakeCollapsedLane`, `MakeTempoLane`, `MakeChordLane`, `AddItem`, `MoveCursor`,
`SetStartFromX`, `SetLoopEndFromX`, `Render`, `RenderBatched`, `RefreshTrackLane`, `LocateRiffAtBeat`).
Aucune n'est `static`. **Transformer la constante en propriété d'instance ne casse donc aucun site
d'appel** — c'est le pivot de l'approche retenue (§2.1).

### 1.2 Les contrôles de piste sont déjà paramétrés en pixels-par-temps

Aucun des cinq contrôles de la timeline ne code l'échelle en dur : chacun la reçoit dans son
`Configure(...)` et la range dans un champ `pxPerBeat`.

| Contrôle | Fichier | Signature |
|---|---|---|
| Règle de mesures | `Controls\TimelineEditor\MeasureRulerControl.xaml.cs` l. 22 | `Configure(width, height, pxPerBeat, beatsPerBar, pickupBeats)` |
| Ligne de tempo | `TempoLaneControl.xaml.cs` l. 41 | `Configure(width, height, pxPerBeat, tempo)` |
| Ligne de volume | `VolumeLaneControl.xaml.cs` l. 35 | `Configure(track, pxPerBeat, laneHeight, width)` |
| Trame d'accords | `ChordLaneControl.xaml.cs` l. 38 | `Configure(width, height, pxPerBeat, arrangement, tonic, mode, pickupBeats)` |
| Boîte de module | `ModuleBoxControl.xaml.cs` l. 93 | `Configure(title, info, width, height, selected, interactive, opacity, fill, border)` (reçoit une largeur déjà calculée) |

`RepeatItemControl` existe encore mais **n'est plus instancié nulle part** (vérifié : aucune référence
hors de sa propre définition) — code mort, ne pas s'en occuper.

Conséquence importante : ces contrôles n'ont **rien** à changer pour que le zoom fonctionne
géométriquement. Les seules retouches demandées les concernant sont des règles de **lisibilité aux
extrêmes** (§3.6 du fonctionnel), traitées en §5.4 et §5.5.

### 1.3 Le nombre d'éléments dessinés est INDÉPENDANT du niveau de zoom

Point vérifié et décisif pour la réactivité (§3.7, critère 21). Toutes les boucles de dessin ont la forme
`for (b = 0; b * pxPerBeat < width; b += k)` avec `width = TotalBeats() * pxPerBeat` — la condition se
simplifie en `b < TotalBeats()`, donc **le zoom ne change pas le nombre de rectangles, de numéros ni de
boîtes créés**, seulement leurs coordonnées.

- `MeasureRulerControl` l. 51 et 58 : `(phase + m*beatsPerBar) * pxPerBeat < width` ⇔ `< TotalBeats()`.
- `TempoLaneControl` l. 55, `MakeTrackLane` l. 1545, `MakeCollapsedLane` l. 1572, `ChordLaneControl`
  l. 103 : même forme.

Un re-rendu à 400 % coûte donc exactement ce que coûte un re-rendu à 100 % — c'est-à-dire le coût
d'un `Render()` normal, que l'application paie déjà à chaque insertion, suppression, undo/redo et
changement de BPM (58 sites d'appel de `Render()`). Le seul travail supplémentaire est le débounce
décrit en §2.3, qui sert à ne pas payer ce coût **une fois par cran** pendant une rafale de molette.

### 1.4 Les trois viewports horizontaux et leur synchronisation

`TimelineScreen.xaml` l. 376-441 : la zone temporelle est faite de **trois** `ScrollViewer` qui doivent
rester alignés au pixel près :

| Nom | Contenu | Rôle |
|---|---|---|
| `rulerScroll` (l. 401) | `measureRuler` + `startCanvas` (calque des poignées bleue/orange) | règle de mesures, hauteur 20, barres masquées |
| `laneScroll` (l. 419) | `lanePanel` (tempo + trame + pistes) + `cursorCanvas` (curseur jaune) | **le seul qui porte les barres de défilement** |
| `chordScroll` (l. 438) | `chordLanePanel` (piste d'accords ancrée en bas) | barres masquées |

La synchronisation se fait en **un seul endroit**, `laneScroll_ScrollChanged` (l. 4036-4042) :

```csharp
headerScroll.ScrollToVerticalOffset(laneScroll.VerticalOffset);
rulerScroll.ScrollToHorizontalOffset(laneScroll.HorizontalOffset);   // keep the measure ruler aligned
chordScroll?.ScrollToHorizontalOffset(laneScroll.HorizontalOffset);  // keep the docked chords lane aligned
```

et le constructeur (l. 88-91) réserve une gouttière de 18 px à droite de `rulerScroll` et `chordScroll`
pour que les trois viewports aient **exactement la même largeur utile**. **Ne pas toucher à ce
mécanisme, et ne pas en créer un quatrième exemplaire.** Le zoom se contente de piloter
`laneScroll.ScrollToHorizontalOffset(...)` : les deux autres suivent gratuitement. Un changement de
largeur de contenu émet lui aussi un `ScrollChanged` (`ExtentWidthChange`), donc la resynchronisation
après re-rendu est automatique.

⚠️ **Piège confirmé** : `Render()` fait `lanePanel.Children.Clear()`. Pendant l'instant où le panneau est
vide, `laneScroll.ScrollableWidth` tombe à 0 et **l'offset horizontal est écrasé à 0**. Toute
restauration d'offset après un `Render()` doit donc être précédée d'un `laneScroll.UpdateLayout()`
(mesure/arrangement synchrones) — sans quoi `ScrollToHorizontalOffset` est silencieusement borné à 0.

### 1.5 Les conversions px → temps de l'interaction (à ne PAS toucher)

Toutes passent déjà par `PxPerBeat` et restent donc exactes à n'importe quelle échelle. Aucune ne code
un pas en pixels.

| Geste | Code | Calage |
|---|---|---|
| Glisser-déposer d'un module | `AddItem` l. 1617 → `MoveInList` l. 1779, `dropStart = Math.Round(dropStart)` l. 1804 | au temps entier |
| Clic sur la règle / poignée bleue | `SetStartFromX` l. 679-686, `Math.Round(x / PxPerBeat)` | au temps entier |
| Poignée de boucle B | `SetLoopEndFromX` l. 706-713 | continu |
| Point d'automation de volume | `VolumeLaneControl` l. 139, 148 : `pos.X / pxPerBeat` | continu |
| Point de tempo (double-clic) | `TempoLaneControl` l. 134 : `Math.Round(X / pxPerBeat)` | au temps entier |

Le callback de dépose est une **lambda** (`nl => MoveInList(track, track.Items, item, nl / PxPerBeat)`) :
elle lira la propriété au moment du **lâcher**, donc toujours à l'échelle courante. Comme un changement
de zoom déclenche un `Render()` complet qui recrée toutes les boîtes, il n'existe de toute façon jamais de
boîte « rendue à un zoom, lâchée à un autre ».

### 1.6 Persistance et undo — le point critique à vérifier

- **Sauvegarde** : `Save(string path)` (l. 750-757) sérialise un `TimelineDocument { Project, Riffs }`.
- **Undo/redo** : `UndoManager` stocke des **snapshots** = exactement le même JSON (`SnapshotState()`,
  l. 919-924).
- `ApplyDocument(doc, path)` (l. 880-914) est **l'unique point de recopie champ par champ** du document
  vers le projet vivant. Il est appelé par l'ouverture d'un `.sq` (l. 862), par `LoadDocument` (l. 875 :
  import, IA, orchestrateur, modèles) **et par `RestoreState` (l. 977), c'est-à-dire par chaque
  Ctrl+Z / Ctrl+Y**.

**Vérification explicite demandée par la consigne** : le zoom **ne doit PAS** devenir un champ de
projet, donc `ApplyDocument` **n'est pas modifié** et il n'y a rien à y recopier. C'est un choix imposé
par le fonctionnel §4 et §5, et le code le confirme comme étant la bonne décision :

1. `RestoreState` (l. 970-983) fait `ApplyDocument(doc, …)` puis `Render()`. Si le zoom était un champ de
   projet, **chaque Ctrl+Z restaurerait le zoom d'avant** — exactement le scénario inacceptable décrit au
   §4 du fonctionnel.
2. Le zoom vivant sera un **champ d'instance de `TimelineScreen`** (comme `startBeat`, `loopEndBeat`,
   `loopEnabled`, `scoreTracks`, `viewScore`, `autoTransposeChords`, qui sont déjà de l'état d'interface
   non sérialisé). `ApplyDocument` ne le touche pas, `RestoreState` appelle `Render()` qui relit la
   propriété → **le zoom survit intact à undo/redo, sans une ligne de code** (critère 16).
3. Le format `.sq` ne change pas d'un octet (critère 19).

**Ne créer aucune entrée d'undo pour un changement de zoom** : `PushUndo` / `BeginUndo` ne doivent pas
apparaître dans le code de zoom.

### 1.7 Ce qui n'existe pas (et n'est donc pas à honorer)

- **Aucun indicateur « projet modifié »** dans l'application : pas de champ `Dirty`/`IsModified`, pas
  d'astérisque, pas de confirmation à la fermeture d'onglet (le seul `riffDirty`, l. 44, concerne le
  rafraîchissement d'une vignette de riff). Le critère 15 (« changer de zoom ne marque pas le morceau
  comme modifié ») est donc satisfait **par construction, sans code**. Ne pas inventer un dirty-flag ici.
- **Aucune gestion de Ctrl+molette** nulle part dans le projet (vérifié : le seul `OnMouseWheel` est
  celui de `SyncScrollViewer`, l. 4053, qui ne fait rien et ne pose pas `Handled`). Il n'y a donc aucun
  conflit à arbitrer.
- **Aucun débordement/repli de la barre d'outils** : `TimelineScreen.xaml` l. 251 est un
  `StackPanel Orientation="Horizontal"` nu, sans `ScrollViewer` ni `ReflowButtons`. Voir le risque R6.

### 1.8 Ce qui existe et se réutilise tel quel

| Besoin | Existant | Où |
|---|---|---|
| pastille de barre d'outils | `Style x:Key="ToolChip"` (Border arrondi, fond `LightBackground`, bordure discrète) | `TimelineScreen.xaml` l. 76-84 |
| libellé de pastille | `Style x:Key="ToolLabel"` | `TimelineScreen.xaml` l. 43-48 |
| bouton plat d'icône | `Style x:Key="iconButton"` — **attention : aucun déclencheur `IsEnabled=False`** | `Theme\Button.xaml` l. 219-243 |
| bouton normal (grisé quand désactivé) | style implicite `Button` | `Theme\Button.xaml` l. ~78 |
| réglages persistés app | `AppSettings.Instance` (singleton JSON, `Save()` best-effort) | `AppSettings.cs` l. 17-22, 163-180 |
| onglets indépendants | `MainWindow` ouvre un `TimelineScreen` par onglet | `MainWindow.xaml.cs` l. 257+ |
| identification par l'automate | `AutomationProperties.AutomationId="BtnXxx"` + `FindFirstDescendant(cf => cf.ByAutomationId(...))` | `TimelineScreen.xaml` l. 252-258 ; `AutoTest\Program.cs` l. 138-142 |
| localisation | `{loc:Tr 'Cle'}` en XAML, `Loc.T("Cle")` en C#, 7 fichiers `lang.xx.json` | `Localization\Loc.cs` |

---

## 2. Approche retenue

### 2.1 Le point unique : `PxPerBeat` devient une propriété d'instance

```csharp
// remplace : const double PxPerBeat = 60;
const double BasePxPerBeat = 60;   // échelle de référence = 100 % (RiffThumbnail doit garder cette valeur)

/// <summary>Crans de zoom horizontal (facteurs de BasePxPerBeat). 100 % = index 6 = l'affichage historique.</summary>
static readonly double[] ZoomLevels = { 0.10, 0.15, 0.25, 0.35, 0.50, 0.75, 1.00, 1.50, 2.00, 3.00, 4.00 };
const int ZoomDefaultIdx = 6;      // 100 %
int zoomIdx = ZoomDefaultIdx;      // état D'INTERFACE : ni sérialisé, ni annulable
double Zoom => ZoomLevels[zoomIdx];
double PxPerBeat => BasePxPerBeat * Zoom;   // <- les 24 sites d'usage existants deviennent zoomables sans être touchés
```

C'est **toute** la mécanique géométrique. Les 24 sites listés en §1.1 continuent de compiler sans une
modification, et `Render()` (déjà appelé depuis 58 endroits) redessine à la bonne échelle.

Il reste ensuite trois familles de travail, et trois seulement :

1. **piloter** `zoomIdx` (commandes de la barre d'outils + Ctrl+molette) et **préserver l'ancrage**
   (§2.3) ;
2. **mémoriser** le niveau au niveau application (`AppSettings`, §3) ;
3. **corriger la lisibilité aux extrêmes** — trois endroits seulement où le code actuel suppose
   implicitement l'échelle 60 (§2.4).

### 2.2 Alternatives envisagées et pourquoi elles sont écartées

| Approche | Pourquoi elle est écartée |
|---|---|
| **`LayoutTransform`/`RenderTransform` `ScaleTransform(zoom, 1)` sur `lanePanel` + la règle + la piste d'accords** | La plus rapide à écrire, mais elle déforme **tout** : les titres de boîtes, les numéros de mesure, les boutons ✕, les bordures (1 px → 0,1 px à 10 %). Le fonctionnel §3.6 l'interdit explicitement (« les textes gardent leur taille de police normale, le zoom est un zoom de temps, pas un zoom d'interface »). Elle obligerait de plus à inverser la transformation dans **chaque** conversion px → temps (§1.5), là où la propriété n'en demande aucune. |
| **Zoom continu (facteur libre) + molette** | Explicitement hors périmètre (§6 du fonctionnel), et les crans garantissent le retour à un repère connu. |
| **Re-générer les vignettes (`RiffThumbnail`) à chaque niveau de zoom** | Le cache est indexé par signature de contenu × couleur (`RiffThumbnail.cs` l. 31-34) ; y ajouter le zoom multiplierait par 11 le nombre de bitmaps et ferait payer un re-rendu `RenderTargetBitmap` par module à chaque cran — l'exact contraire du critère 21. Retenu à la place : une mise à l'échelle horizontale d'affichage (§2.4). |
| **Un `ZoomManager` / service partagé entre onglets** | Le fonctionnel exige un zoom **par onglet** (§4, critère 18). Un champ d'instance de `TimelineScreen` le donne gratuitement ; un service partagé demanderait au contraire du travail pour les désolidariser. |
| **Champ `Zoom` dans `TimelineProject`** | Interdit par §4 et §5 du fonctionnel, et cassé en pratique : `RestoreState` → `ApplyDocument` le restaurerait à chaque Ctrl+Z (§1.6). |

### 2.3 Le pipeline de zoom : ancrage + débounce

Toutes les commandes convergent vers **une seule méthode**, `RequestZoom(newIdx, anchorBeat,
anchorViewportX)`, dont le contrat est : « après application, le temps musical `anchorBeat` sera à
`anchorViewportX` pixels du bord gauche de la zone visible ».

| Commande | `anchorBeat` | `anchorViewportX` |
|---|---|---|
| Ctrl + molette | `(sv.HorizontalOffset + e.GetPosition(sv).X) / PxPerBeat` | `e.GetPosition(sv).X` (position du pointeur dans le viewport) |
| Boutons − / + | `(laneScroll.HorizontalOffset + laneScroll.ViewportWidth / 2) / PxPerBeat` | `laneScroll.ViewportWidth / 2` |
| Clic sur le libellé (retour 100 %) | idem (centre) | idem (centre) |
| « Ajuster » | `0` | `0` (retour au début, §3.3 du fonctionnel) |

**Débounce.** `RequestZoom` met à jour `zoomIdx` **immédiatement** (donc le libellé « 150 % » et l'état
grisé des boutons répondent au premier cran) mais **diffère le `Render()` de 50 ms** via un
`DispatcherTimer` redémarré à chaque cran. Une rafale de molette ne provoque donc **qu'un seul** re-rendu.

**Subtilité à respecter absolument** : l'ancre n'est capturée qu'au **premier** cran de la rafale (avant
tout changement d'échelle). Aux crans suivants, l'appelant calcule un `anchorBeat` avec un `PxPerBeat`
déjà modifié mais **pas encore rendu** — cette valeur est fausse et doit être **ignorée**. Le calcul
final `anchorBeat * PxPerBeat(final) − anchorViewportX` reste exact quel que soit le nombre de crans de
la rafale.

Séquence d'application (l'ordre est obligatoire) :

```csharp
void ApplyPendingZoom()
{
    zoomTimer.Stop();
    if (pendingZoomIdx < 0) return;
    pendingZoomIdx = -1;

    Render();                       // redessine tout à la nouvelle échelle
    laneScroll.UpdateLayout();      // OBLIGATOIRE : sans ça ScrollableWidth vaut encore l'ancienne valeur (§1.4)
    laneScroll.ScrollToHorizontalOffset(zoomAnchorBeat * PxPerBeat - zoomAnchorViewX); // borné par WPF à [0, ScrollableWidth]
    // rulerScroll + chordScroll suivent via laneScroll_ScrollChanged — ne rien y ajouter

    AppSettings.Instance.TimelineZoom = Zoom;   // mémorisation app (§3)
    AppSettings.Instance.Save();
}
```

`ScrollToHorizontalOffset` borne lui-même l'offset dans `[0, ScrollableWidth]` : l'exigence « le
défilement est borné, jamais avant le début ni au-delà de la fin » (§3.3) est satisfaite sans code.

**Pendant la lecture** : `Render()` ne détruit ni `cursorCanvas` ni `startCanvas` (seul `lanePanel` et
`headerPanel` sont vidés), donc le curseur jaune et les poignées survivent. Le `DispatcherTimer` de
lecture (33 ms, l. 425-427) rappelle `MoveCursor(PlayedBeat())` qui recalcule `x = beat * PxPerBeat` et
reprend le suivi automatique — c'est exactement ce que demande §3.3, et cela ne demande **aucun** code
spécifique. Le son n'est pas touché : `TimelinePlayer` / `LookaheadBuffer` ignorent totalement
l'affichage.

### 2.4 Les trois endroits qui supposent implicitement l'échelle 60

Ce sont les seules corrections « métier » à faire au-delà du pilotage.

1. **Largeur plancher d'une boîte de module** — `MakeLeafBox` l. 1715 :
   ```csharp
   double w = Math.Max(40, len * PxPerBeat - 2);
   ```
   Ce plancher de 40 px **élargit artificiellement** les boîtes courtes et décale visuellement toute la
   piste : à 10 %, un accord de 4 temps mesure 22 px et serait dessiné à 40 px → chevauchement et
   désalignement avec la règle. Le §3.6 tranche : « l'exactitude de position et de largeur prime sur la
   lisibilité du contenu ». → `Math.Max(2, len * PxPerBeat - 2)`.
   Voir le risque **R1** : cela change aussi l'affichage à 100 % pour les modules de moins de 0,7 temps.

2. **Vignettes** (`RiffThumbnail`) : bitmaps rendus à 60 px/temps, affichés `Stretch="None"` par
   `ModuleBoxControl` — donc trop larges à zoom < 100 %, trop étroites au-delà. → mise à l'échelle
   **horizontale d'affichage** par `RenderTransform = new ScaleTransform(zoom, 1)` sur l'`Image`, plus
   `ClipToBounds="True"` sur la bordure de la boîte. `RiffThumbnail.PxPerBeat` reste à **60** : c'est la
   résolution de rendu, pas l'échelle d'affichage (mettre à jour son commentaire l. 21 en conséquence).

3. **Densité des libellés** : numéros de mesure (`MeasureRulerControl`) et étiquettes d'accords
   (`ChordLaneControl`) se chevauchent aux petits niveaux. → espacement adaptatif (§5.4 et §5.5).

---

## 3. Modèle de données et persistance

### 3.1 État vivant (par onglet)

Tout dans `TimelineScreen` — **rien** dans `TimelineProject`, `TimelineDocument` ni `TimelineTrack`.

```csharp
int zoomIdx = ZoomDefaultIdx;   // niveau courant
int pendingZoomIdx = -1;        // cran demandé, en attente du re-rendu débounce (-1 = aucun)
double zoomAnchorBeat;          // temps musical à conserver sous le point d'ancrage
double zoomAnchorViewX;         // sa position, en px, dans le viewport de laneScroll
System.Windows.Threading.DispatcherTimer zoomTimer;  // débounce 50 ms
```

Un `TimelineScreen` par onglet (`MainWindow.OpenEditor`, l. 257) ⇒ **zoom indépendant par onglet**
(critère 18) sans code supplémentaire.

### 3.2 Persistance application

`MusicTracker\AppSettings.cs`, à ajouter au corps de la classe `AppSettings` :

```csharp
/// <summary>Dernier niveau de zoom horizontal de l'éditeur de morceau, en FACTEUR (1.0 = 100 %).
/// Réglage de confort d'affichage : il n'appartient PAS au morceau (jamais écrit dans un .sq).
/// Sert de niveau de départ aux onglets ouverts ensuite. Valeur inconnue → ramenée au cran le plus proche.</summary>
public double TimelineZoom { get; set; } = 1.0;
```

- On stocke le **facteur**, pas l'index : si la table `ZoomLevels` change un jour, un ancien
  `settings.json` reste interprétable (on retombe sur le cran le plus proche).
- Lecture : dans le **constructeur** de `TimelineScreen`, `zoomIdx = NearestZoomIdx(AppSettings.Instance.TimelineZoom);`
  avec un `NearestZoomIdx` qui renvoie `ZoomDefaultIdx` pour une valeur ≤ 0, NaN ou absente (fichier de
  réglages neuf ou corrompu → 100 %, §4 du fonctionnel).
- Écriture : dans `ApplyPendingZoom` uniquement (donc **une fois par rafale**, pas une fois par cran).
  `AppSettings.Save()` est déjà « best-effort » (try/catch silencieux) et écrit un petit fichier ; c'est
  le même usage que `RiffGridControl` (l. 196, 249, 257).

### 3.3 Persistance projet : AUCUNE — vérification `ApplyDocument`

**Vérifié explicitement.** `ApplyDocument` (l. 880-914) recopie champ par champ : `Tempo`, `Key`,
`TimeSigNum`, `TimeSigDen`, `TimeSigScale`, `Arrangement`, `UserChordStyles`, `UserMelodicLines`,
`UserDrumStyles`, `PickupBeats`, `MinBeats`, `SwingPercent`, `Tracks`.

**Aucun champ n'est ajouté à cette liste par cette fonctionnalité.** Le piège classique (« un champ
oublié dans `ApplyDocument` est perdu à l'ouverture et vidé à chaque undo ») ne s'applique pas, puisque
le zoom n'est pas une donnée de projet. Inversement, le comportement correct découle du même mécanisme :

| Événement | Chemin | Effet sur le zoom |
|---|---|---|
| Ouverture d'un `.sq` | `LoadSqFile` → `ApplyDocument` → `RenderBatched` | inchangé (niveau mémorisé de l'onglet) ✔ |
| Import / IA / orchestrateur / modèle | `LoadDocument` → `ApplyDocument` → `Render` | inchangé ✔ |
| Ctrl+Z / Ctrl+Y | `RestoreState` → `ApplyDocument` → `Render` | **inchangé** ✔ (critère 16) |
| Enregistrement | `Save` → `TimelineDocument` | aucune donnée d'affichage écrite ✔ (critère 19) |
| Ajout/suppression de piste ou de module, tonalité, mesure, tempo | `Render()` | inchangé ✔ (§3.5) |

⚠️ Le développeur doit vérifier que **`RenderBatched`** (l. 1165-1225) utilise bien `PxPerBeat` partout
comme `Render` — c'est déjà le cas (l. 1174-1175, 1196), il n'y a rien à ajouter, mais c'est le chemin
qu'on oublie de tester (ouverture d'un gros `.sq`).

---

## 4. Fichiers à toucher

| # | Fichier | Nature de la modification |
|---|---|---|
| 1 | `MusicTracker\Screens\TimelineScreen.xaml.cs` | **cœur** : `BasePxPerBeat` + `ZoomLevels` + `PxPerBeat` propriété ; pipeline `RequestZoom`/`ApplyPendingZoom`/`ZoomFit`/`UpdateZoomUi` ; 3 gestionnaires de clic + 1 gestionnaire `PreviewMouseWheel` ; correction du plancher de largeur l. 1715 ; passage du facteur de vignette à `ModuleBoxControl` |
| 2 | `MusicTracker\Screens\TimelineScreen.xaml` | pastille de zoom dans la barre d'outils + un style local `ZoomStepBtn` (état désactivé visible) |
| 3 | `MusicTracker\Controls\TimelineEditor\ModuleBoxControl.xaml` | `ClipToBounds="True"` sur la bordure racine |
| 4 | `MusicTracker\Controls\TimelineEditor\ModuleBoxControl.xaml.cs` | `SetThumbnailScale(double)` ; masquage progressif titre / info / ✕ / gros libellé selon la largeur |
| 5 | `MusicTracker\Controls\TimelineEditor\MeasureRulerControl.xaml.cs` | espacement adaptatif des numéros de mesure + suppression des graduations de temps aux petits niveaux |
| 6 | `MusicTracker\Controls\TimelineEditor\ChordLaneControl.xaml.cs` | masquage des étiquettes d'accords quand l'espacement est trop faible |
| 7 | `MusicTracker\AppSettings.cs` | propriété `TimelineZoom` |
| 8 | `MusicTracker\Controls\RiffThumbnail.cs` | **commentaire seulement** (l. 19-21) : préciser que 60 est la résolution de rendu, mise à l'échelle à l'affichage |
| 9 | `MusicTracker\Localization\lang.{fr,en,de,it,es,nl,pt}.json` | 6 clés nouvelles × 7 fichiers (§6) |

**Ne pas toucher** : `TimelineProject.cs`, `TimelineDocument`, `UndoManager.cs`, `TimelinePlayer.cs`,
`LookaheadBuffer.cs`, `MainWindow.xaml(.cs)`, `TempoLaneControl`, `VolumeLaneControl`,
`RiffGridControl`, `ChordEditorControl`, `ScoreView`, les exports.

**Attention** : `TimelineScreen.xaml.cs`, `TimelineScreen.xaml`, `LookaheadBuffer.cs`,
`TimelinePlayer.cs`, `TimelineProject.cs` et `lang.fr.json` étaient **modifiés (non commités)** au moment
de l'analyse par un autre chantier (mixer, transport). Rebaser/relire avant d'éditer.

---

## 5. Détail par fichier

### 5.1 `TimelineScreen.xaml.cs`

**a) Champs et constantes** — remplacer la l. 31 par le bloc du §2.1 + les champs du §3.1.

**b) Bornes et garde de largeur** (fonctionnel §5 : « si une limite technique de largeur d'affichage
devait être atteinte, limiter le zoom disponible et jamais planter ») :

```csharp
const double MaxLaneWidth = 1_000_000; // garde-fou de largeur de canevas WPF

// Le cran le plus haut atteignable sans dépasser MaxLaneWidth (le morceau le plus long possible).
int MaxZoomIdx()
{
    double beats = Math.Max(1, TotalBeats());
    for (int i = ZoomLevels.Length - 1; i > 0; i--)
        if (beats * BasePxPerBeat * ZoomLevels[i] <= MaxLaneWidth) return i;
    return 0;
}
int ClampZoomIdx(int i) => Math.Max(0, Math.Min(MaxZoomIdx(), i));
```

À titre indicatif : 400 mesures 4/4 à 400 % = 385 920 px, très en dessous de la garde ; celle-ci ne
mord qu'au-delà d'environ 1 000 mesures à 400 %.

**c) Pilotage** :

```csharp
void RequestZoom(int newIdx, double anchorBeat, double anchorViewX)
{
    newIdx = ClampZoomIdx(newIdx);
    if (newIdx == zoomIdx && pendingZoomIdx < 0) return;
    if (pendingZoomIdx < 0) { zoomAnchorBeat = anchorBeat; zoomAnchorViewX = anchorViewX; } // 1er cran de la rafale SEULEMENT
    pendingZoomIdx = newIdx;
    zoomIdx = newIdx;          // l'échelle change tout de suite -> libellé + boutons réactifs
    UpdateZoomUi();
    if (zoomTimer == null)
    {
        zoomTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        zoomTimer.Tick += (s, e) => ApplyPendingZoom();
    }
    zoomTimer.Stop(); zoomTimer.Start();
}
```

plus `ApplyPendingZoom()` (§2.3) et :

```csharp
double CentreBeat() => (laneScroll.HorizontalOffset + laneScroll.ViewportWidth / 2) / PxPerBeat;

void btnZoomOut_Click(object s, RoutedEventArgs e)  => RequestZoom(zoomIdx - 1, CentreBeat(), laneScroll.ViewportWidth / 2);
void btnZoomIn_Click(object s, RoutedEventArgs e)   => RequestZoom(zoomIdx + 1, CentreBeat(), laneScroll.ViewportWidth / 2);
void btnZoomLevel_Click(object s, RoutedEventArgs e)=> RequestZoom(ZoomDefaultIdx, CentreBeat(), laneScroll.ViewportWidth / 2);
```

**d) « Ajuster »** (fonctionnel §3.4) :

```csharp
void btnZoomFit_Click(object sender, RoutedEventArgs e)
{
    // Projet vide (aucun module sur aucune piste) : ne rien faire de visible.
    bool empty = true;
    foreach (var t in project.Tracks) if (t.Items.Count > 0) { empty = false; break; }
    if (empty) return;

    double avail = laneScroll.ViewportWidth;
    if (avail < 50) return;                       // pas encore mis en page
    double beats = Math.Max(1, TotalBeats());     // inclut déjà le +8 temps de marge de TotalBeats()

    int idx = 0;                                  // aucun cran ne suffit -> 10 %, sans message d'erreur
    for (int i = ZoomDefaultIdx; i >= 0; i--)     // ne dépasse JAMAIS 100 % (§3.4)
        if (beats * BasePxPerBeat * ZoomLevels[i] <= avail) { idx = i; break; }

    RequestZoom(idx, 0, 0);                       // ancre = début du morceau
}
```

**e) Affichage de l'état** — appelé depuis `UpdateToolbar()` (l. 1229, elle-même appelée par chaque
`Render`), pour que la borne haute suive la longueur du morceau :

```csharp
void UpdateZoomUi()
{
    if (txtZoomLevel == null) return;
    txtZoomLevel.Content = (int)Math.Round(Zoom * 100) + " %";   // format identique dans les 7 langues (§5 du fonctionnel)
    btnZoomOut.IsEnabled = zoomIdx > 0;
    btnZoomIn.IsEnabled  = zoomIdx < MaxZoomIdx();
}
```

**f) Ctrl + molette** — abonner **exactement trois** éléments dans le constructeur, après
`InitializeComponent()` :

```csharp
rulerScroll.PreviewMouseWheel += Timeline_PreviewMouseWheel;   // règle de mesures
laneScroll.PreviewMouseWheel  += Timeline_PreviewMouseWheel;   // tempo + trame + pistes + lignes de volume
chordScroll.PreviewMouseWheel += Timeline_PreviewMouseWheel;   // piste d'accords ancrée
```

```csharp
void Timeline_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
{
    if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return; // molette seule : comportement actuel intact
    var sv = sender as ScrollViewer; if (sv == null) return;
    e.Handled = true;                                             // ne pas laisser le ScrollViewer défiler aussi
    double vx = e.GetPosition(sv).X;                              // px depuis le bord gauche du viewport
    double beat = (sv.HorizontalOffset + vx) / PxPerBeat;         // le temps sous le pointeur
    RequestZoom(zoomIdx + (e.Delta > 0 ? 1 : -1), beat, vx);
}
```

Les trois viewports partagent le même offset horizontal et la même largeur utile (§1.4), donc la formule
est valable pour les trois. Les **en-têtes de pistes** (`headerScroll`) ne sont **pas** abonnés :
Ctrl+molette n'y zoome pas (§5 du fonctionnel). L'éditeur du bas (`editorScroll`) et la barre d'outils ne
sont pas concernés non plus. `SyncScrollViewer.OnMouseWheel` ne pose pas `Handled`, donc l'événement des
en-têtes remonte comme aujourd'hui — ne rien y changer.

**g) Largeur de boîte** — l. 1715, `Math.Max(40, …)` → `Math.Max(2, …)` (§2.4, risque R1).

**h) Vignette** — dans `MakeLeafBox`, après `box.SetThumbnail(...)` (ou juste après `Configure`), ajouter
`box.SetThumbnailScale(Zoom);`. Le plus simple est de le poser une fois **après** le `switch`
(l. 1737-1758), puisque `SetThumbnailScale` ne dépend pas du type de module.

### 5.2 `TimelineScreen.xaml`

Insérer la pastille **juste après le `Popup` de la pastille « Mesure »** (après la l. 332) et **avant**
le séparateur `<Border Width="1" …/>` de la l. 333 — c'est-à-dire « à droite du groupe Mesure », comme
demandé au §2 du fonctionnel.

```xml
<!-- Zoom horizontal de la timeline (affichage seul : rien n'est écrit dans le morceau). -->
<Border Style="{StaticResource ToolChip}" Padding="4,1">
    <StackPanel Orientation="Horizontal">
        <TextBlock Text="{loc:Tr 'Zoom'}" Style="{StaticResource ToolLabel}"/>
        <Button x:Name="btnZoomOut" Content="−" Style="{StaticResource ZoomStepBtn}" Click="btnZoomOut_Click"
                ToolTip="{loc:Tr 'ZoomOutTip'}" AutomationProperties.AutomationId="BtnZoomOut"/>
        <Button x:Name="txtZoomLevel" Content="100 %" Style="{StaticResource ZoomStepBtn}" MinWidth="46"
                Click="btnZoomLevel_Click" ToolTip="{loc:Tr 'ZoomResetTip'}"
                AutomationProperties.AutomationId="BtnZoomLevel"/>
        <Button x:Name="btnZoomIn" Content="+" Style="{StaticResource ZoomStepBtn}" Click="btnZoomIn_Click"
                ToolTip="{loc:Tr 'ZoomInTip'}" AutomationProperties.AutomationId="BtnZoomIn"/>
        <Button x:Name="btnZoomFit" Content="{loc:Tr 'ZoomFit'}" Style="{StaticResource ZoomStepBtn}" Margin="4,0,0,0"
                Click="btnZoomFit_Click" ToolTip="{loc:Tr 'ZoomFitTip'}"
                AutomationProperties.AutomationId="BtnZoomFit"/>
    </StackPanel>
</Border>
```

Et, dans `UserControl.Resources`, un style local — **nécessaire** parce que `iconButton`
(`Theme\Button.xaml` l. 219-243) **n'a aucun déclencheur `IsEnabled=False`** : sans ce style, les
boutons − / + désactivés aux bornes ne seraient **pas** grisés, contrairement au §3.1 du fonctionnel et
au critère 5.

```xml
<Style x:Key="ZoomStepBtn" TargetType="Button" BasedOn="{StaticResource iconButton}">
    <Setter Property="Padding" Value="6,2"/>
    <Style.Triggers>
        <Trigger Property="IsEnabled" Value="False">
            <Setter Property="Opacity" Value="0.3"/>
        </Trigger>
    </Style.Triggers>
</Style>
```

Le niveau est un **`Button`** (et non un `TextBlock`) pour deux raisons : il est cliquable (retour à
100 %, critère 6) et son `Content` textuel est exposé comme `Name` UIA, donc **lisible par l'automate**
(critère 23). Le nom de champ `txtZoomLevel` est conservé pour la lisibilité du code C#.

### 5.3 `ModuleBoxControl` (XAML + code-behind)

**XAML** : ajouter `ClipToBounds="True"` sur `<Border x:Name="box" …>`. Sans cela, le gros libellé
d'accord (`txtBig`, FontSize 28) et le bouton ✕ débordent sur les boîtes voisines dès que la boîte
devient étroite.

**Code-behind** : mémoriser la largeur et appliquer la dégressivité du §3.6 du fonctionnel — dans
l'ordre imposé : **titre**, puis **informations secondaires**.

```csharp
// Seuils de lisibilité (px) : en dessous, l'élément disparaît plutôt que de déborder.
// L'ordre suit le fonctionnel §3.6 : le titre part en premier, puis l'info secondaire.
const double MinTitlePx = 60, MinInfoPx = 34, MinDelPx = 46, MinBigLabelPx = 26, MinThumbPx = 14;
double lastWidth = double.MaxValue;
```

- dans `Configure(...)` : `lastWidth = width;` puis, après les affectations de texte existantes,
  ```csharp
  txtTitle.Visibility = width >= MinTitlePx ? Visibility.Visible : Visibility.Collapsed;
  txtInfo.Visibility  = width >= MinInfoPx  ? Visibility.Visible : Visibility.Collapsed;
  btnDel.Visibility   = (interactive && width >= MinDelPx) ? Visibility.Visible : Visibility.Collapsed;
  ```
- dans `SetThumbnail(...)` : `thumb.Visibility = (img != null && lastWidth >= MinThumbPx) ? Visible : Collapsed;`
- dans `SetBigLabel(...)` : ajouter la condition `lastWidth >= MinBigLabelPx`.
- nouvelle méthode :
  ```csharp
  /// <summary>Facteur d'échelle HORIZONTAL de la vignette. Le bitmap est rendu une fois pour toutes à
  /// l'échelle de référence (60 px/temps) et mis à l'échelle à l'affichage, pour ne pas re-générer
  /// (ni re-cacher) une image par niveau de zoom. Vertical inchangé : le zoom est purement horizontal.</summary>
  public void SetThumbnailScale(double sx)
      => thumb.RenderTransform = (Math.Abs(sx - 1) < 1e-6) ? null : new ScaleTransform(sx, 1);
  ```
  (`thumb` est `HorizontalAlignment="Left"`, `RenderTransformOrigin` par défaut `0,0` → l'échelle part du
  bord gauche, donc de la position du début du module.)

**Ce qui ne change pas** : le fond et la bordure de la boîte, y compris la bordure de sélection turquoise
(`SelBorder`, `BorderThickness = 2` quand sélectionné) — le fonctionnel exige qu'une boîte réduite à
quelques pixels reste repérable et qu'une boîte sélectionnée reste distinguable (critère 20). C'est
automatique puisque `Configure`/`SetSelected` n'ont pas de seuil.

### 5.4 `MeasureRulerControl.xaml.cs`

Ajouter en tête de `Configure`, après le calcul de `phase` :

```csharp
// Aux petits niveaux de zoom, les numéros de mesure se chevaucheraient : on n'en affiche qu'un sur `stride`
// (1, 2, 4, 8, …) et on supprime les graduations de temps intermédiaires. Purement visuel : la GRILLE
// (position des barres) est inchangée — c'est ce qui garantit l'alignement avec les pistes.
const double MinLabelPx = 34;   // largeur mini réservée à un numéro de mesure
const double MinBeatTickPx = 12; // en dessous, les graduations de temps deviennent du bruit
double barPx = beatsPerBar * pxPerBeat;
int stride = 1;
while (stride * barPx < MinLabelPx && stride < 4096) stride *= 2;
bool beatTicks = pxPerBeat >= MinBeatTickPx;
bool allBarTicks = barPx >= 4;
```

puis dans la boucle des barres (l. 51-59) :

- graduation de barre : `if (allBarTicks || m % stride == 0) Tick(barStart, true);`
- numéro : `if (m % stride == 0) { … }` (le texte reste `(m + 1).ToString()`, donc 1, 3, 5… ou 1, 5, 9…) ;
- graduations de temps : encadrer la boucle interne par `if (beatTicks)` ;
- graduations de temps de la barre de levée (l. 47) : même garde `if (beatTicks)`.

Vérification d'ordre de grandeur : à 10 % en 4/4, `pxPerBeat = 6`, `barPx = 24` → `stride = 2`, pas de
graduations de temps ; 64 mesures = 1 536 px, plus la marge de `TotalBeats()` (+8 temps = 48 px), tiennent
dans une fenêtre maximisée en 1920 (1920 − 200 d'en-têtes − 18 de gouttière = 1 702 px utiles) — conforme
à l'affirmation du §3.1 du fonctionnel. À 400 %, `barPx = 960` → `stride = 1` et toutes les graduations
de temps sont visibles.

**Ne pas toucher** au calcul de `phase` (levée) : le fonctionnel §5 exige que la levée reste alignée à
tous les niveaux, ce qui est acquis puisque seule la **densité d'affichage** change, jamais la grille.

### 5.5 `ChordLaneControl.xaml.cs`

Dans `DrawChord(int i)`, la barre verticale (`mark`) reste toujours dessinée ; seule l'étiquette est
conditionnée :

```csharp
// Espacement entre deux accords, en px : en dessous d'un seuil, les étiquettes se chevaucheraient.
double stepBeats = (arr.SlicesPerQuarter > 0) ? (double)arr.ChordSlices / arr.SlicesPerQuarter : 1;
bool showLabel = stepBeats * pxPerBeat >= 20;
```

(calculable une fois dans `Redraw` et rangé dans un champ, plutôt qu'à chaque accord). Le double-clic
d'édition reste possible même sans étiquette (la zone cliquable est le `TextBlock` — donc quand
l'étiquette est masquée, l'édition d'accord n'est plus accessible **à ce niveau de zoom** ; c'est
acceptable et cohérent avec « on dézoome pour lire la structure, on zoome pour éditer »). À signaler dans
la note de version.

### 5.6 `AppSettings.cs`

Une seule propriété (§3.2). Rien d'autre : `Save()`/`Load()` sont génériques.

---

## 6. Localisation — 6 clés à créer dans les **7** fichiers

`MusicTracker\Localization\lang.{fr,en,de,it,es,nl,pt}.json`. Aucune clé existante ne collisionne
(vérifié : seule `ZoomDefilementBoutonImprimerPourLe` commence par « Zoom », et aucune clé « Ajuster »
n'existe).

| Clé | fr | en | de |
|---|---|---|---|
| `Zoom` | Zoom | Zoom | Zoom |
| `ZoomOutTip` | Dézoomer (Ctrl + molette) | Zoom out (Ctrl + wheel) | Verkleinern (Strg + Mausrad) |
| `ZoomInTip` | Zoomer (Ctrl + molette) | Zoom in (Ctrl + wheel) | Vergrößern (Strg + Mausrad) |
| `ZoomResetTip` | Revenir à 100 % | Back to 100% | Zurück auf 100 % |
| `ZoomFit` | Ajuster | Fit | Anpassen |
| `ZoomFitTip` | Ajuster le zoom pour voir tout le morceau | Fit the zoom to show the whole piece | Zoom anpassen, um das ganze Stück zu sehen |

| Clé | it | es | nl | pt |
|---|---|---|---|---|
| `Zoom` | Zoom | Zoom | Zoom | Zoom |
| `ZoomOutTip` | Riduci (Ctrl + rotellina) | Alejar (Ctrl + rueda) | Uitzoomen (Ctrl + muiswiel) | Reduzir (Ctrl + roda) |
| `ZoomInTip` | Ingrandisci (Ctrl + rotellina) | Acercar (Ctrl + rueda) | Inzoomen (Ctrl + muiswiel) | Ampliar (Ctrl + roda) |
| `ZoomResetTip` | Torna al 100% | Volver al 100% | Terug naar 100% | Voltar a 100% |
| `ZoomFit` | Adatta | Ajustar | Passend | Ajustar |
| `ZoomFitTip` | Adatta lo zoom per vedere tutto il brano | Ajustar el zoom para ver toda la pieza | Zoom aanpassen om het hele stuk te zien | Ajustar o zoom para ver a peça inteira |

Notes :

- Le **pourcentage n'est pas une clé** : il est construit en code (`nombre + " %"`, §5.1e), donc identique
  dans les 7 langues, conformément au §5 du fonctionnel.
- Les fichiers sont éditables uniquement avec les outils d'édition de fichiers (jamais
  `Get-Content`/`Set-Content` PowerShell : cela corrompt les accents — voir `ZoomFitTip` en fr/de).
- `lang.fr.json` contient 765 clés, les six autres 744 : c'est l'état existant, la nouvelle
  fonctionnalité doit alimenter **les sept**.

---

## 7. Risques et régressions

| # | Risque | Ce qui l'empêche / décision |
|---|---|---|
| **R1** | **Changement d'affichage visible à 100 %** : supprimer le plancher `Math.Max(40, …)` (§2.4-1) rétrécit, **même à 100 %**, les boîtes de moins de 0,7 temps (un accord d'un demi-temps passe de 40 px à 28 px). Cela heurte littéralement le critère 2 (« affichage identique à 100 % »). | **Décision d'architecture assumée** : le §3.6 est normatif et prioritaire (« l'exactitude de position et de largeur prime sur la lisibilité du contenu ; jamais élargie artificiellement… car cela décalerait visuellement toute la piste »). Le plancher actuel est un **bug d'alignement préexistant** que le zoom rendrait criant. Le critère 2 est honoré sur ce qu'il vise réellement (espacement des mesures, largeur des boîtes de durée normale). À signaler dans la note de version. Si le commanditaire refuse ce changement à 100 %, le repli est un plancher proportionnel `Math.Max(2, Math.Min(40 * Zoom, len * PxPerBeat - 2))` — moins propre, à n'adopter que sur demande explicite. |
| **R2** | **Offset horizontal perdu après le re-rendu** : `Render()` vide `lanePanel`, ce qui écrase l'offset à 0 (§1.4). Symptôme : chaque zoom ramènerait au début du morceau. | `laneScroll.UpdateLayout()` **obligatoire** entre `Render()` et `ScrollToHorizontalOffset` (§2.3). C'est l'erreur la plus probable de l'implémentation ; le test T5/T6 la détecte immédiatement. |
| **R3** | **Ancre fausse pendant une rafale de molette** : recalculer l'ancre à chaque cran utilise un `PxPerBeat` déjà changé mais pas encore rendu → la vue dérive au fil de la rafale. | L'ancre n'est capturée qu'au premier cran (`if (pendingZoomIdx < 0)`, §5.1c). Test T6 avec 5 crans d'affilée. |
| **R4** | **Désynchronisation règle / pistes / piste d'accords** aux extrémités de défilement. | On ne touche ni à la gouttière de 18 px du constructeur (l. 88-91) ni à `laneScroll_ScrollChanged` : le zoom ne pilote **que** `laneScroll`. Interdiction explicite d'ajouter un quatrième point de synchronisation. |
| **R5** | **Gel de l'interface** sur un gros projet en rafale de molette. | (a) le nombre d'éléments dessinés est indépendant du zoom (§1.3) : un re-rendu zoomé coûte ce que coûte un `Render()` déjà courant ; (b) débounce 50 ms → **un seul** re-rendu par rafale ; (c) les vignettes ne sont pas re-générées (mise à l'échelle d'affichage). Test T14. |
| **R6** | **Débordement de la barre d'outils** : elle est un `StackPanel` horizontal nu, sans repli (§1.7). La pastille ajoute ~150-170 px et peut pousser « Importer » hors de la fenêtre sur un écran étroit. | Pastille compacte (libellé court « Ajuster », boutons `iconButton` en `Padding 6,2`). Un mécanisme de repli de la barre d'outils est **hors périmètre** ; à mesurer en test T15 (fenêtre 1280) et à traiter séparément si nécessaire. |
| **R7** | **Boutons désactivés non grisés** aux bornes : `iconButton` n'a pas de déclencheur `IsEnabled=False`. | Style local `ZoomStepBtn` avec le déclencheur d'opacité (§5.2). Test T4. |
| **R8** | **Débordement visuel des boîtes étroites** (gros libellé d'accord, bouton ✕) sur les boîtes voisines. | `ClipToBounds="True"` sur la bordure racine de `ModuleBoxControl` + seuils de masquage (§5.3). Test T10. |
| **R9** | **Largeur de canevas déraisonnable** sur un morceau de plusieurs centaines de mesures à 400 %. | Garde `MaxLaneWidth` + `MaxZoomIdx()` : le bouton **+** se désactive proprement au lieu de produire un canevas monstrueux (exigence explicite du §5 du fonctionnel). Test T13. |
| **R10** | **Ctrl+molette capté au mauvais endroit** (en-têtes, éditeur du bas, barre d'outils) ou molette simple cassée. | Abonnement à **exactement trois** `ScrollViewer` nommés, et sortie immédiate si `Control` n'est pas enfoncé, **avant** tout `e.Handled = true`. Tests T7 et T8. |
| **R11** | **Le zoom modifié par un undo** ou écrit dans le `.sq`. | Le zoom n'est ni un champ de projet ni recopié dans `ApplyDocument` (§3.3). Tests T11 et T12 (comparaison binaire du `.sq`). |
| **R12** | **Curseur de lecture décalé** après un zoom en cours de lecture. | `MoveCursor` recalcule `beat * PxPerBeat` toutes les 33 ms ; `Render()` ne vide ni `cursorCanvas` ni `startCanvas`. Aucun code spécifique, mais test humain T16 obligatoire. |
| **R13** | **Édition d'accord inaccessible** aux petits niveaux (étiquette masquée = plus de cible de double-clic). | Comportement accepté et documenté (§5.5) : on dézoome pour lire, on zoome pour éditer. |
| **R14** | **Seuil de glisser trop grossier aux petits niveaux** : `ModuleBoxControl.DragThreshold = 4 px` vaut 0,67 temps à 10 %, donc un micro-tremblement peut déplacer un module d'un temps. | Comportement préexistant, non aggravé par un bug : le calage reste exact (`Math.Round`). Ne pas « corriger » le seuil dans ce chantier ; le signaler si un utilisateur le remonte. |
| **R15** | **Conflit avec le chantier en cours** (mixer / transport) qui modifie les mêmes fichiers. | Rebaser sur `main` et relire `TimelineScreen.xaml`/`.xaml.cs` avant d'éditer ; la pastille s'insère dans une zone (après le popup « Mesure ») qui n'est pas celle du mixer. |

---

## 8. Plan de test

### 8.1 Vérifiable automatiquement (AutoTest / FlaUI, ou assertions de code)

Les quatre commandes exposent un `AutomationId` (`BtnZoomOut`, `BtnZoomLevel`, `BtnZoomIn`,
`BtnZoomFit`) ; le niveau courant se lit dans le `Name` UIA de `BtnZoomLevel` (critère 23).

| # | Vérification | Attendu |
|---|---|---|
| T1 | Ouvrir l'éditeur, lire `BtnZoomLevel` | « 100 % » au premier lancement (critères 1, 2) |
| T2 | Clic sur `BtnZoomIn` | le libellé passe à « 150 % » (critère 3) |
| T3 | Clic sur `BtnZoomOut` deux fois depuis 150 % | « 100 % » puis « 75 % » (critère 4) |
| T4 | Clics répétés sur `BtnZoomOut` jusqu'à « 10 % » | `BtnZoomOut.IsEnabled == false` ; idem `BtnZoomIn` à « 400 % » ; aucun clic ne sort de la plage (critère 5) |
| T5 | Depuis « 25 % », clic sur `BtnZoomLevel` | « 100 % » (critère 6) |
| T9 | Sur un morceau de 64 mesures : `BtnZoomFit`, puis lire `laneScroll.ScrollableWidth` | ≈ 0 (tout tient) ; sur un morceau de 4 mesures, le libellé ne dépasse pas « 100 % » (critère 11) |
| T11 | Zoom à 50 %, `Ctrl+Z`, `Ctrl+Y`, relire le libellé | « 50 % » inchangé (critère 16) |
| T12 | Enregistrer un `.sq` avant et après un changement de zoom, comparer les octets | fichiers identiques ; ouvrir un `.sq` antérieur à la fonctionnalité → aucune erreur (critères 15, 19) |
| T13 | `MaxZoomIdx()` sur un projet de 1 200 mesures | renvoie un index < 10 (le **+** se désactive avant la garde de largeur) |
| T17 | Régler 50 %, fermer l'application, relancer, ouvrir un morceau | libellé « 50 % » (critère 17) |
| T18 | Ouvrir deux morceaux dans deux onglets, zoomer dans l'un | l'autre garde son niveau (critère 18) |
| T22 | Pour chaque langue des 7 : basculer la langue, relire les infobulles et le libellé « Ajuster » | texte traduit, jamais la clé brute ; le libellé de niveau reste « N % » (critère 22) |
| T-geo | Assertions de géométrie (test unitaire ou instrumentation) : à zoom `z`, `Canvas.GetLeft(box) == item.AbsStart * 60 * z` et `laneWidth == TotalBeats() * 60 * z` | égalité à 1e-6 près (critère 10) |

### 8.2 Exige un jugement humain (rendu visuel / sonore)

| # | Vérification | Ce qu'on regarde |
|---|---|---|
| T6 | Placer le pointeur pile sur le début de la mesure 9, faire **5 crans** de Ctrl+molette vers l'avant puis 5 vers l'arrière | la mesure 9 reste sous le pointeur à chaque cran (critère 8), et la vue ne dérive pas au fil de la rafale (R3) |
| T7 | Ctrl+molette au-dessus de la règle, d'une piste, de la ligne de tempo, d'une ligne de volume, de la piste d'accords | zoome dans les cinq cas (critère 7) |
| T8 | Ctrl+molette au-dessus des **en-têtes de pistes**, de l'**éditeur du bas**, de la **barre d'outils** ; puis molette **sans** Ctrl partout | aucun zoom dans les trois premiers cas ; la molette seule défile exactement comme avant (§5 du fonctionnel) |
| T10 | À 10 % sur un morceau dense | aucune boîte ne déborde sur la suivante ; les numéros de mesure ne se chevauchent pas ; la boîte sélectionnée reste identifiable (turquoise) ; les vignettes ne débordent pas des boîtes (critère 20) |
| T10b | À 400 % | textes à taille de police normale (pas d'étirement), vignettes non déformées verticalement |
| T14 | Projet ~200 mesures × 8 pistes : maintenir la molette Ctrl enfoncée sur une dizaine de crans | pas de gel perceptible, pas de clignotement (critère 21) |
| T15 | Fenêtre réduite à 1280 de large | la pastille de zoom et le bouton « Importer » restent visibles (risque R6) |
| T16 | **Pendant la lecture**, changer de zoom (bouton puis molette) | le son ne s'interrompt pas, le curseur reste à la bonne position, le suivi automatique reprend aussitôt et le curseur reste visible (critère 14) |
| T19 | À 25 % puis à 300 % : déplacer un module par glisser-déposer sur « début de la mesure 9 » | même temps musical qu'à 100 %, vérifié en revenant à 100 % (critère 12) |
| T20 | À 25 % puis à 300 % : clic sur la règle, glisser la poignée bleue, poser la poignée de boucle, poser un point de tempo et un point de volume | positions musicales identiques (critère 13) |
| T21 | Morceau à levée (`PickupBeats > 0`), mesure composée (6/8), changement de mesure | la levée et les barres restent alignées avec les pistes à 10 %, 100 % et 400 % (§5 du fonctionnel) |
| T23 | Morceau d'**une seule mesure** à 400 %, puis à 10 % | pas de zone de défilement absurde ; la mesure reste visible et cliquable (§5) |
| T24 | Projet **vide** (aucune piste, aucun module) : cliquer − , +, le libellé, « Ajuster » | aucune erreur ; « Ajuster » ne change rien (§3.4, §5) |
| T25 | Redimensionner la fenêtre | le niveau de zoom ne bouge pas tout seul (§5) |

### 8.3 Non-régression à ne pas oublier

- Ouvrir un gros `.sq` (chemin `RenderBatched`, différent de `Render`) et vérifier que l'affichage est à
  la bonne échelle dès l'ouverture.
- Replier / déplier une piste (`MakeCollapsedLane`) à 10 % et à 400 %.
- Piste d'accords ancrée + trame d'accords d'un morceau généré (`IsComposedArrangement`) à tous les
  niveaux.
- Clic sur une mesure dans la **vue partition** → `LocateRiffAtBeat` doit défiler au bon endroit à
  n'importe quel zoom.
- Éditer un riff puis fermer l'éditeur (`RefreshTrackLane`, chemin de re-rendu partiel) à un zoom ≠ 100 %.
- La **visite guidée** (`StartTutorial`) : elle cible des éléments par référence, elle doit toujours
  passer malgré la barre d'outils élargie.

---

## 9. Estimation d'effort

**Moyen.** Environ une journée de développement pour un développeur qui n'a pas participé à l'analyse,
plus une demi-journée de tests manuels (§8.2, qui est la partie la plus longue).

Répartition indicative :

| Lot | Charge | Contenu |
|---|---|---|
| A | ~1 h | `BasePxPerBeat` / `ZoomLevels` / propriété `PxPerBeat` + `AppSettings.TimelineZoom` — le zoom fonctionne déjà géométriquement, sans interface |
| B | ~2 h | Pastille de barre d'outils + style `ZoomStepBtn` + `RequestZoom`/`ApplyPendingZoom`/`UpdateZoomUi` + « Ajuster » |
| C | ~1 h | Ctrl+molette et ancrage (la partie où les erreurs R2/R3 se logent) |
| D | ~2 h | Lisibilité aux extrêmes : plancher de largeur, vignettes, seuils de `ModuleBoxControl`, règle, trame d'accords |
| E | ~1 h | 6 clés × 7 fichiers de langue + relecture des accents |
| F | ~4 h | Tests (dont T6, T14, T16, T19-T21 qui demandent un vrai morceau et une écoute) |

Ce qui rend l'effort raisonnable malgré l'étendue apparente : **l'échelle était déjà centralisée**
(un symbole, cinq contrôles déjà paramétrés) et **le nombre d'éléments dessinés ne dépend pas du zoom**.
La feature est bien placée : elle ne s'éparpille pas, elle se loge dans le symbole qui existait déjà pour
elle.
