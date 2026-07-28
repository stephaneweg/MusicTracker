# Organiser les pistes : dupliquer, réordonner, suppression annulable — analyse technique

Référence fonctionnelle : `01-fonctionnel.md` (même dossier). Ce document décrit **comment** implémenter,
et n'ajoute aucune exigence fonctionnelle nouvelle. Les numéros de ligne ont été relevés dans les sources au
moment de l'analyse (dépôt en cours de modification par un autre run) ; ils peuvent avoir bougé de quelques
lignes — les extraits cités permettent de retrouver l'emplacement exact.

---

## 1. Ce que le code fait déjà (vérifié dans les sources)

### 1.1 Les en-têtes de pistes : `MakeHeader`, un point unique

`MusicTracker\Screens\TimelineScreen.xaml.cs` → **`Border MakeHeader(string fixedTitle, double height,
TimelineTrack track)`** (l. 1270-1376). C'est **le seul** constructeur d'en-tête de l'application. Il sert :

| Appelant | Ligne | Ce qu'il produit |
|---|---|---|
| `Render()` | 1124 | l'en-tête **Tempo** (`track == null`, retour anticipé l. 1275-1279) |
| `Render()` | 1139 | l'en-tête de chaque piste instrument / batterie |
| `RenderBatched()` | 1192 | idem, chemin « gros fichier » |
| `RenderChordDock()` | 1159 | l'en-tête de la **piste d'accords**, posée dans `chordHeaderHost` (zone dockée en bas, hors `headerPanel`) |

`MakeHeader` a **deux formes et deux `return`** :

- **replié** (l. 1302-1310) : bouton ▸ + pastille de famille + zone de nom, puis
  `border.PreviewMouseLeftButtonDown += (s, e) => SelectTrack(track); return border;` ;
- **déplié** (l. 1311-1375) : ajoute la case ♫, la croix ✕ (sauf piste d'accords, l. 1315), le combo
  instrument **ou** kit, le volume, M/S, puis le même `PreviewMouseLeftButtonDown`.

> **Conséquence pour cette feature :** un handler posé **avant** le premier `return` (donc juste après le
> bloc `if (track == null)`) couvre d'un coup l'en-tête déplié, l'en-tête replié **et** celui de la piste
> d'accords. C'est le point unique demandé (critères 3 et 15).

La croix de suppression, aujourd'hui (l. 1317-1319) :

```csharp
var del = new Button { Content = "✕", …, ToolTip = Loc.T("SupprimerLaPiste") };
del.Click += (s, e) => { project.Tracks.Remove(track); scoreTracks.Remove(track); if (selectedTrack == track) selectedTrack = null; Render(); RefreshScore(); };
```

Trois défauts relevés, tous corrigés par cette feature :

1. **aucun `PushUndo`** → c'est bien la seule mutation de structure non annulable (constat du §1 fonctionnel) ;
2. `selectedItem` n'est **pas** remis à null : après la suppression, l'éditeur du bas continue d'afficher un
   bloc appartenant à une piste qui n'existe plus (et `riffEditItem` / `riffEditTrack` restent branchés
   dessus) ;
3. le **mixeur** ouvert n'est pas prévenu (voir §1.7).

### 1.2 Le modèle de piste — ce qu'une copie doit emporter

`MusicTracker\Engine\Timeline\TimelineProject.cs`, `class TimelineTrack` (l. 60-85) :

| Membre | Nature | Couvert par le §3.1 fonctionnel |
|---|---|---|
| `Name` | **propriété** (notifie) | remplacé par « … (copie) » |
| `Type`, `Instrument`, `DrumKit`, `Clef` | **champs publics** | oui (instrument / kit / clé de notation) |
| `Volume`, `Pan`, `Mute`, `Solo` | **propriétés** (notifient, liées au mixeur) | oui |
| `Collapsed` | **champ public** | oui (état replié) |
| `VolumeAutomation` (`List<VolumePoint>`) | champ | oui (courbe de volume) |
| `Items` (`List<TimelineItem>`) | champ | oui (tous les blocs, avec leurs `SilenceBefore`) |

`TimelineItem` (l. 20-24) est fait de **deux champs publics** : `double SilenceBefore` et `FlowModule Module`.

> ⚠️ **Piège de sérialisation n° 1.** L'essentiel du modèle est en **champs publics**, que
> `System.Text.Json` **ignore par défaut**. Toute copie par sérialisation doit passer
> `new JsonSerializerOptions { IncludeFields = true }` — exactement l'option `JsonOpts` utilisée par la
> sauvegarde `.sq` (`TimelineScreen.xaml.cs` l. 364). Sans elle, on obtient une piste vide **sans erreur**.

L'état **« afficher dans la partition » (♫) n'est PAS dans ce modèle** : voir §1.6, c'est le point dur.

### 1.3 Le seul lien externe d'un bloc : le riff

Les cinq types de modules (`Engine\Flow\FlowModule.cs`, polymorphisme déclaré l. 12-16 par
`[JsonDerivedType]`) sont **autonomes** — `PatternGeneratorModule`, `DrumPatternModule`, `CadenceModule` et
`MelodicLineModule` portent leur contenu en propre (`CustomSlices` / `CustomNotes` / `Chords` / `Notes`) —
**sauf** `PlayRiffModule`, qui ne contient qu'un `Guid RiffId` (l. 44-49) pointant vers un `Riff` de la
bibliothèque globale `RiffLibrary.Instance.Riffs`.

> **Conséquence :** dupliquer une piste sans recréer les riffs donnerait deux pistes **partageant** leurs
> notes — éditer la copie modifierait l'original. C'est exactement ce qu'interdit le §3.1 (« le point le
> plus important de la commande »), et le critère 7.

Le geste correct existe déjà, dans `PasteAtCursor` (l. 3530-3549) :

```csharp
var r = clipRiff.Clone();
r.Id = Guid.NewGuid();                       // Clone() preserves the Id → give the copy a new one
r.Name = clipRiff.Name + " (copie)";
RiffLibrary.Instance.Riffs.Add(r);
pr.RiffId = r.Id;
```

`Riff.Clone()` (`Engine\Riff.cs` l. 67-70) est un aller-retour JSON qui **conserve l'`Id`** : le
`r.Id = Guid.NewGuid()` est obligatoire.

⚠️ **Différence assumée avec le collage** : `PasteAtCursor` suffixe le **nom du riff** par « (copie) », et ce
nom est le **titre affiché sur la boîte** (`MakeLeafBox` l. 1721, `title = ItemTitle(item)`). Le §3.1
fonctionnel dit l'inverse pour la duplication de piste : « Les étiquettes des blocs copiés sont identiques à
celles des blocs d'origine ». La duplication de piste **conserve donc le nom des riffs tel quel**.
(Le « (copie) » en dur de `PasteAtCursor` est une chaîne française non localisée **préexistante** ; hors
périmètre, ne pas y toucher.)

### 1.4 La piste d'accords

`TimelineHelper.EnsureChordTrack(project)` (`Engine\Timeline\TimelineHelper.cs` l. 151-165) garantit
l'invariant : **exactement une** piste `TimelineTrackType.Chord`, **toujours en dernière position** de
`project.Tracks` (l. 164 : si elle n'est pas la dernière, elle est retirée puis rajoutée en fin). Elle est
appelée notamment en tête de `Render()` (l. 1109) et par `AddTrack` (l. 3334).

Elle n'est **pas** rendue dans `headerPanel` / `lanePanel` (l. 1137 : `continue`) mais dans la zone dockée
(`RenderChordDock`, l. 1151-1161). Elle n'a pas de croix ✕ (l. 1315).

> **Conséquence :** « la piste la plus basse » du §4.1 = la piste dont le **successeur immédiat** dans
> `project.Tracks` est de type `Chord`. Un simple test d'index suffit, aucun cas particulier de plus.

### 1.5 Undo / redo : snapshots, et `ApplyDocument` comme goulot

- `Engine\Timeline\UndoManager.cs` stocke des **snapshots** = le JSON complet du document.
- `SnapshotState()` (l. 919-924) sérialise `TimelineDocument { Project, Riffs }` avec `JsonOpts`.
- `PushUndo(opKey)` (l. 927-932) capture l'état **AVANT** la mutation ; `BeginUndo()` / `CommitUndo(pre, key)`
  (l. 935-936) font la même chose en deux temps, pour les insertions dont l'identifiant n'est connu qu'après.
- `RestoreState(json)` (l. 970-983) : `StopPlayback()` → désérialise → **`ApplyDocument(doc, CurrentPath)`** →
  `Render()` → `RefreshScore()`.
- **`ApplyDocument(doc, path)` (l. 880-914) est l'unique recopie champ par champ** du document vers le projet
  vivant. Appelée par l'ouverture d'un `.sq` (l. 862), par `LoadDocument` (l. 875 : import, IA, orchestrateur,
  modèles) **et par chaque Ctrl+Z / Ctrl+Y**.

Règles de consolidation de `UndoManager.Push` (l. 37-67), **à connaître avant de choisir une clé d'op** :

- **neutralisation** : une clé `delete:X` juste après `insert:X` → les **deux** entrées disparaissent ;
- **coalescence** : `IsCoalescable` = préfixes `move:`, `edit:`, `vol:` → une série de clés identiques ne
  compte que pour une entrée.

> **Conséquence :** les clés de cette feature ne doivent **pas** commencer par `move:` / `insert:` /
> `delete:` / `edit:` / `vol:`, sinon deux montées successives de deux pistes différentes fusionneraient en
> une seule entrée (critère 17 : « **un seul** Ctrl+Z suffit » — un seul, mais pas moins).

**Bonne nouvelle offerte par les snapshots (critère 19).** Annuler une duplication restaure aussi
`RiffLibrary` : `ApplyDocument` fait `RiffLibrary.Instance.Riffs.Clear()` puis recharge `doc.Riffs`
(l. 883-884). Les riffs créés par la copie **disparaissent** — le fichier réenregistré n'est pas plus lourd.
**À condition** que le snapshot soit pris **avant** la création des riffs (d'où `BeginUndo()` en tout premier,
§4.3).

### 1.6 `scoreTracks` : l'état ♫ n'est ni dans le projet, ni dans le snapshot — **le point dur**

`TimelineScreen.xaml.cs` l. 103 :

```csharp
readonly HashSet<TimelineTrack> scoreTracks = new HashSet<TimelineTrack>(); // tracks INCLUDED in the score (♫)
```

C'est un ensemble de **références d'objets**, purement mémoire : il n'est **pas** sérialisé dans le `.sq`, et
`ApplyDocument` le **vide** (l. 905 : `scoreTracks.Clear(); activeScore = null;`).

Conséquences, vérifiées par lecture :

1. **aujourd'hui déjà**, chaque Ctrl+Z décoche toutes les cases ♫ (les pistes restaurées sont de **nouveaux
   objets** après désérialisation, donc absents du `HashSet`) ;
2. sans traitement, le critère 18 (« Ctrl+Z … la piste revient avec … son état *afficher dans la partition* »)
   et le §3.3 sont **impossibles** à satisfaire.

Un ré-appariement par **nom** ou par **index** après restauration ne marche pas : au moment du Ctrl+Z, l'état
vivant est l'état **d'après** la suppression, la piste supprimée n'y figure plus. L'information doit venir de
l'entrée d'undo elle-même → §3.3.

`RefreshScore()` (l. 611-627) construit la partition en parcourant `project.Tracks` **filtré** par
`scoreTracks` (l. 627) : l'**ordre des portées suit donc l'ordre des pistes**, ce que le §4.8 autorise
explicitement.

### 1.7 Le mixeur

`MusicTracker\Dialogs\MixerDialog.xaml.cs` :

```csharp
public MixerDialog(TimelineProject project, Window owner)
{ InitializeComponent(); Owner = owner; list.ItemsSource = project?.Tracks; }
```

`list` est une `ListBox` (`MixerDialog.xaml` l. 53) liée à un **`List<TimelineTrack>` nu** : ni
`ObservableCollection`, ni `INotifyCollectionChanged`. Les propriétés d'une piste (Volume/Pan/Mute/Solo)
notifient, **pas la liste**.

> **Conséquence :** ajouter, retirer ou réordonner des pistes pendant que le mixeur est ouvert **ne
> rafraîchit rien** — le critère 11 (« l'ordre du mixeur reflète le changement ») échoue, et une piste
> supprimée laisse une ligne fantôme. Il faut une méthode de rafraîchissement explicite (§4.6). Le dialogue
> est non modal (`Show()`, l. 391) : le cas se produit vraiment.

### 1.8 Le player pendant la lecture (§4.6 du fonctionnel)

`Engine\Timeline\TimelinePlayer.cs` fige les **notes** dans son constructeur (l. 163-187 : un `Track[]`
interne d'événements par tranche) — ajouter/déplacer une piste en cours de lecture n'a donc effectivement
d'effet qu'à la lecture suivante, comme le demande le §4.6.

**Mais** deux endroits relisent `project.Tracks` **par index** après coup :

```csharp
// l. 493-518, ApplyChannelVolumes — appelé à CHAQUE buffer, sur le thread audio
var pt = melodyProject?.Tracks;
…
var p = (pt != null && ti < pt.Count) ? pt[ti] : null;   // volume / pan / mute / solo LUS EN DIRECT
```

```csharp
// l. 239-258, ApplyPrograms(project) — appelé au setup et à chaque Start()
var tr = project.Tracks[i];                               // i < tracks.Length
```

Deux problèmes réels :

1. **Mauvais mixage** : après une insertion (duplication) ou un échange (monter/descendre), l'index `ti` du
   player ne désigne plus la même piste → les volumes/pan/mute/solo sont appliqués aux mauvaises voix
   **immédiatement**, en pleine lecture. Le §3.2 exige « ne change **rien** au son ».
2. **Course de threads** : `pt.Count` est lu puis `pt[ti]` indexé, pendant que le thread UI fait un
   `Insert`/`RemoveAt` sur la même `List<T>`. Une suppression entre les deux lève
   `ArgumentOutOfRangeException` **sur le thread producteur** de `LookaheadBuffer.Produce()` (l. 71-93), où
   `inner.Read(...)` (l. 81) **n'est pas protégé par un try/catch** → exception non gérée sur un thread de
   fond = **arrêt du processus**.

Le correctif est petit et local (§4.7) : capturer la `TimelineTrack` source dans le `Track` interne du player
et indexer **celle-là**, plus jamais la liste vivante.

### 1.9 Les commandes qui visent une piste par son nom (§4.3 du fonctionnel)

Deux seulement, toutes deux en **premier match** :

- `TimelineHelper.cs` l. 971 : `project.Tracks[t].Name == "Accompagnement"` ;
- `TimelineScreen.xaml.cs` l. 2538 : idem, pour le preset d'accompagnement ;
- `FullLineOfTrack(string name)` (l. 1405-1422) : `foreach … if (t.Name == name) { tr = t; break; }`.

> **Conséquence :** comme la copie porte un nom **différent** *et* est insérée **après** l'original, le
> premier match reste l'original. Le §4.3 et le critère 21 sont satisfaits **par construction**, sans une
> ligne de code.

Petite note sans gravité : `AddMelodicLine` (l. 1436) compte les pistes dont le nom **commence par**
« Ligne mélodique » ; une copie (« Ligne mélodique 1 (copie) ») entre dans ce compte et décale d'une octave la
prochaine ligne générée. Inoffensif, aucun code à écrire.

### 1.10 Ce qui existe et se réutilise tel quel

| Besoin | Existant | Où |
|---|---|---|
| menu contextuel d'un objet de la timeline | `ShowItemContextMenu` : `new ContextMenu()` + `MenuItem` + `Separator` + `menu.PlacementTarget = anchor; menu.IsOpen = true;` | `TimelineScreen.xaml.cs` l. 3431-3510 |
| clic droit → sélectionner puis ouvrir | `ModuleBoxControl` ctor : `MouseRightButtonUp += (s,e) => { e.Handled = true; Selected?.Invoke(); ContextRequested?.Invoke(); }` | `Controls\TimelineEditor\ModuleBoxControl.xaml.cs` l. 50 |
| copie profonde d'un module | `CloneModule` (aller-retour JSON polymorphe) | `TimelineScreen.xaml.cs` l. 3516-3518 |
| copie d'un riff + nouvel `Id` | `PasteAtCursor` | l. 3530-3549 |
| capturer l'état avant une insertion | `BeginUndo()` / `CommitUndo(pre, key)` | l. 935-936 |
| valider l'éditeur ouvert avant une opération | `CommitRiffEditor()` | l. 2346-2353 |
| faire défiler la timeline vers un élément | `LocateRiffAtBeat` (l. 1988-1990) : on pilote **`laneScroll`**, jamais `headerScroll` | l. 1964-1991 |
| synchro verticale en-têtes ↔ lanes | `laneScroll_ScrollChanged` → `headerScroll.ScrollToVerticalOffset(laneScroll.VerticalOffset)` | l. 4036-4042 |
| clé « Supprimer la piste » déjà traduite en 7 langues | `SupprimerLaPiste` | `Localization\lang.*.json` |

---

## 2. Approche retenue

### 2.1 Le point unique

> **Un seul handler de clic droit, posé dans `MakeHeader` avant ses deux `return`, ouvre un menu construit
> par une nouvelle méthode `ShowTrackContextMenu` de `TimelineScreen`. Les quatre commandes sont quatre
> méthodes de `TimelineScreen` (orchestration : undo, sélection, rendu, mixeur, partition) qui délèguent
> **toute la logique de modèle** à trois helpers statiques ajoutés à `TimelineHelper` (`CloneTrack`,
> `CanMoveTrack`/`MoveTrack`, `CopyName`).**
> **Aucun nouveau fichier, donc aucune modification du `.csproj`.**

Pourquoi c'est le bon découpage :

1. `MakeHeader` est **le seul** endroit qui fabrique un en-tête ; le poser avant les `return` couvre
   mécaniquement l'en-tête déplié, l'en-tête replié (critère 3) et la piste d'accords (critère 15). Aucun
   risque d'oubli, aucun code dupliqué.
2. `TimelineHelper` est déjà le domicile des opérations de modèle partagées
   (`EnsureChordTrack`, `InsertTopLevel`, `MergeWithNext`, `ConvertRiffToDrums`, `PlaceAtCursor`) : y mettre
   `CloneTrack` et `MoveTrack` les rend testables et réutilisables (un futur glisser-déposer, §5 du
   fonctionnel, n'aura qu'à appeler `MoveTrack`), **et** évite une entrée `<Compile>` dans un `.csproj` que
   d'autres runs modifient en parallèle.
3. L'undo est déjà **global et par snapshot** : il n'y a rien à inventer, juste des `PushUndo` /
   `BeginUndo`+`CommitUndo` bien placés — c'est ce qui rend la suppression annulable « au régime commun »
   (§3.4) sans toucher `UndoManager`.

### 2.2 Alternatives écartées

| Option | Pourquoi non |
|---|---|
| **Trois boutons de plus dans l'en-tête** (⧉ ▲ ▼) | Le §2 fonctionnel l'exclut explicitement (« Aucun bouton n'est ajouté dans l'en-tête, déjà dense »). L'en-tête déplié fait 4 contrôles sur 160 px de large (`HeaderW`, l. 29). |
| **Glisser-déposer de l'en-tête pour réordonner** | Hors périmètre (§5). Coûteux : `headerPanel` est un `StackPanel` sans support de drop, et la synchro verticale en-têtes/lanes rendrait le retour visuel pendant le glissement délicat. |
| **Rendre `project.Tracks` `ObservableCollection<TimelineTrack>`** pour que le mixeur suive tout seul | Régression garantie : `TimelineHelper` utilise `Tracks.Find(...)` (l. 157, 160), du code fait `.Sort` / indexation ; et le type est **sérialisé** dans tous les `.sq`. Un rafraîchissement explicite du mixeur coûte 3 lignes (§4.6). |
| **Recopier la piste champ par champ à la main** (`new TimelineTrack { Instrument = src.Instrument, … }`) | Fragile dans le temps : tout champ ajouté plus tard à `TimelineTrack` (comme `Pan`, `DrumKit`, `Clef` l'ont été) serait silencieusement oublié par la copie. L'aller-retour JSON copie tout ce que le `.sq` sait déjà écrire. |
| **Nouveau fichier `Engine\Timeline\TrackOps.cs`** | Cohérent, mais impose une ligne dans `MusicTracker.csproj`, fichier actuellement modifié par un autre run → conflit probable pour un gain nul (≈ 60 lignes qui ont leur place dans `TimelineHelper`). |
| **Persister l'état ♫ dans un nouveau champ `TimelineTrack.InScore`** (pour l'undo) | Ce serait la solution la plus simple, mais elle contredit le §4.8 (« Aucune information nouvelle n'est enregistrée dans le morceau ») et **changerait un comportement existant** : à la réouverture d'un `.sq`, les cases ♫ reviendraient cochées, ce que personne n'a demandé. La solution retenue (§3.3) garde l'information **hors du fichier**, dans l'entrée d'undo. |
| **Ré-apparier `scoreTracks` par nom/index après un `RestoreState`** | Ne peut pas fonctionner : au moment de l'annulation d'une suppression, la piste à recocher n'existe plus dans l'état vivant (§1.6). |
| **Confirmer la suppression par une boîte de dialogue** | Explicitement hors périmètre (§5) : « l'annulation fait office de filet ». |

---

## 3. Modèle de données et persistance

### 3.1 Aucun champ nouveau dans le projet

La duplication ajoute une `TimelineTrack` **ordinaire** et des `Riff` ordinaires ; le déplacement ne fait que
permuter deux éléments de `project.Tracks` ; la suppression en retire un. **Rien n'est ajouté au format
`.sq`** — §4.8 et critère 20 satisfaits par construction, dans les deux sens de compatibilité.

### 3.2 `ApplyDocument` — vérification explicite demandée par la consigne

**Aucune ligne n'est à ajouter dans `ApplyDocument`**, et c'est un résultat, pas un oubli :

- l'ordre des pistes est porté par `project.Tracks`, déjà recopié l. 900-901
  (`project.Tracks.Clear(); foreach (var t in dp.Tracks) project.Tracks.Add(t);`) — la **liste est
  reconstruite dans l'ordre du document**, donc l'ordre est annulable et persistant sans rien faire ;
- le contenu d'une piste dupliquée voyage dans `dp.Tracks` et ses riffs dans `doc.Riffs` (l. 883-884) ;
- `TimelineHelper.EnsureChordTrack(project)` est rappelée l. 903 : l'invariant « accords en dernier » est
  restauré même sur un `.sq` bricolé à la main.

**Le piège de la consigne est bien réel ici, mais il porte sur l'état ♫, pas sur un champ de projet** : c'est
le §3.3 ci-dessous. À la relecture, si un implémenteur ajoutait malgré tout un champ à `TimelineProject` ou à
`TimelineTrack`, il **devrait** l'ajouter à `ApplyDocument` — sinon il serait perdu à l'ouverture **et à
chaque Ctrl+Z**.

### 3.3 L'état ♫ doit entrer dans l'unité d'annulation

Sans ceci, le critère 18 et le §3.3 fonctionnel ne peuvent pas passer (démonstration au §1.6).

**`TimelineScreen.xaml.cs`**, dans la région « Undo / redo » (l. 916-983) :

```csharp
// The undo unit = the .sq document PLUS the editor's ♫ selection. That selection lives in `scoreTracks`,
// a set of OBJECT references that ApplyDocument clears (l. 905) and that deserialization invalidates
// (restored tracks are new objects) — so it has to travel INSIDE the snapshot, by track index.
sealed class UndoSnapshot
{
    public TimelineDocument Doc { get; set; }
    public System.Collections.Generic.List<int> Score { get; set; } // indices into Doc.Project.Tracks
}

string SnapshotState()
{
    var doc = new TimelineDocument { Project = project };
    doc.Riffs.AddRange(RiffLibrary.Instance.Riffs);
    var score = new System.Collections.Generic.List<int>();
    for (int i = 0; i < project.Tracks.Count; i++)
        if (scoreTracks.Contains(project.Tracks[i])) score.Add(i);
    return System.Text.Json.JsonSerializer.Serialize(new UndoSnapshot { Doc = doc, Score = score }, JsonOpts);
}

void RestoreState(string json)
{
    restoringUndo = true;
    try
    {
        StopPlayback();
        var snap = System.Text.Json.JsonSerializer.Deserialize<UndoSnapshot>(json, JsonOpts) ?? new UndoSnapshot();
        ApplyDocument(snap.Doc ?? new TimelineDocument(), CurrentPath);   // clears scoreTracks
        if (snap.Score != null)
            foreach (int i in snap.Score)
                if (i >= 0 && i < project.Tracks.Count) scoreTracks.Add(project.Tracks[i]);
        Render();
        if (ScoreVisible) RefreshScore();
        RefreshMixer();                       // §4.6
    }
    finally { restoringUndo = false; }
    UpdateUndoButtons();
}
```

Points de vigilance :

- **`Save(path)` (l. 750-757) n'est PAS modifié** : le `.sq` reste un `TimelineDocument` nu. Le format de
  fichier ne bouge pas (critère 20, §4.8) ; seul le format **en mémoire** des entrées d'undo change, et rien
  ne le persiste.
- `EnsureChordTrack` (appelée dans `ApplyDocument`) peut **déplacer la piste d'accords en dernier** si le
  document ne l'y avait pas ; les index enregistrés viennent d'un état où l'invariant était déjà vrai, donc
  ils restent valides. Le double garde-fou `i >= 0 && i < Count` couvre le reste.
- `FlushPending()` (l. 947-953) compare deux `SnapshotState()` : la sélection ♫ fait désormais partie de la
  comparaison. Effet de bord **voulu** : cocher/décocher ♫ pendant qu'un éditeur de module est ouvert devient
  annulable. Aucune régression (au pire une entrée d'undo de plus, qui restitue un état visible).
- Effet de bord bénéfique : le bug préexistant « chaque Ctrl+Z décoche toutes les cases ♫ » disparaît pour
  **toutes** les opérations, pas seulement les nôtres. À signaler au testeur, car c'est un changement visible
  sur des scénarios existants.

**Variante minimale** si l'on veut absolument ne pas toucher au format de snapshot : ne pas faire ce §3.3.
Conséquence à assumer et à écrire dans `03-tests.md` : le critère 18 ne passe que partiellement (la piste, ses
blocs et ses réglages reviennent ; sa case ♫ reste décochée), et le comportement actuel « Ctrl+Z décoche
tout » persiste. **Recommandation : le faire.** C'est ~15 lignes, très localisées.

---

## 4. Conception détaillée

### 4.1 `MusicTracker\Engine\Timeline\TimelineHelper.cs` — trois helpers de modèle (≈ 60 lignes)

À ajouter à la suite de `EnsureChordTrack` (l. 165), même style (classe statique de service, aucun texte
d'interface, aucune dépendance à `Loc`).

```csharp
// Deep-copy options: TimelineTrack / TimelineItem / the modules keep their data in PUBLIC FIELDS
// (Type, Instrument, Items, SilenceBefore, Module…), which System.Text.Json ignores by DEFAULT.
// IncludeFields = true is MANDATORY — the very option the .sq save uses (TimelineScreen.JsonOpts).
static readonly System.Text.Json.JsonSerializerOptions CloneOpts =
    new System.Text.Json.JsonSerializerOptions { IncludeFields = true };

/// <summary>Deep, INDEPENDENT copy of a track: every module at the same position, every setting, and a
/// FRESH riff (new Id, SAME name) for each Play-riff module — so editing the copy never touches the
/// original (the point of the command). The copy is NOT inserted in the project; the caller places it and
/// names it. Returns null for a null track.</summary>
public static TimelineTrack CloneTrack(TimelineTrack src)
{
    if (src == null) return null;
    var json = System.Text.Json.JsonSerializer.Serialize(src, CloneOpts);
    var copy = System.Text.Json.JsonSerializer.Deserialize<TimelineTrack>(json, CloneOpts);
    if (copy?.Items == null) return copy;
    foreach (var it in copy.Items)
    {
        if (it?.Module == null) continue;
        it.Module.Id = Guid.NewGuid();                  // keep module ids unique across the project
        if (!(it.Module is PlayRiffModule pr)) continue;
        var srcRiff = RiffById(pr.RiffId);
        if (srcRiff == null) continue;                  // dangling reference: leave it as it was
        var r = srcRiff.Clone();                        // Clone() PRESERVES the Id …
        r.Id = Guid.NewGuid();                          // … so give the copy its own
        r.Name = srcRiff.Name;                          // block labels stay IDENTICAL (§3.1) — no " (copie)"
        RiffLibrary.Instance.Riffs.Add(r);
        pr.RiffId = r.Id;
    }
    return copy;
}

/// <summary>Can this track swap with its neighbour (delta = -1 up, +1 down)? False for the pinned chords
/// track, at the ends of the list, and when the neighbour below IS the chords track (§4.1: nothing ever
/// goes under it). Single source of truth: the menu greys out with it, the mutation guards with it.</summary>
public static bool CanMoveTrack(TimelineProject p, TimelineTrack t, int delta)
{
    if (p?.Tracks == null || t == null || t.Type == TimelineTrackType.Chord) return false;
    int i = p.Tracks.IndexOf(t), j = i + Math.Sign(delta);
    return i >= 0 && j >= 0 && j < p.Tracks.Count && p.Tracks[j].Type != TimelineTrackType.Chord;
}

/// <summary>Swap the track with its neighbour. Returns false (and changes nothing) when not allowed.</summary>
public static bool MoveTrack(TimelineProject p, TimelineTrack t, int delta)
{
    if (!CanMoveTrack(p, t, delta)) return false;
    int i = p.Tracks.IndexOf(t), j = i + Math.Sign(delta);
    p.Tracks[i] = p.Tracks[j];
    p.Tracks[j] = t;
    return true;
}

/// <summary>"Mélodie" → "Mélodie (copie)", then "(copie 2)", "(copie 3)"… until the name is free in the
/// project. <paramref name="word"/> is the LOCALIZED word ("copie" / "copy" / "Kopie"…), passed in by the
/// caller so this file stays free of UI strings.</summary>
public static string CopyName(TimelineProject p, string baseName, string word)
{
    string b = baseName ?? "";
    string candidate = b + " (" + word + ")";
    for (int n = 2; NameTaken(p, candidate); n++) candidate = b + " (" + word + " " + n + ")";
    return candidate;
}

static bool NameTaken(TimelineProject p, string name)
{
    if (p?.Tracks == null) return false;
    foreach (var t in p.Tracks)
        if (t != null && string.Equals(t.Name, name, StringComparison.Ordinal)) return true;
    return false;
}
```

Notes :

- `Guid`, `Math`, `StringComparison` : `System` est déjà importé (l. 3) ; `PlayRiffModule` vient de
  `MusicTracker.Engine.Flow` (l. 2). Rien à ajouter en `using`.
- La boucle de `CopyName` termine toujours : au pire elle épuise le nombre de pistes.
- **Ne pas** appeler `EnsureChordTrack` depuis `MoveTrack` : `CanMoveTrack` garantit déjà que la piste
  d'accords ne bouge pas, et `Render()` la rappelle de toute façon (l. 1109).

### 4.2 `MakeHeader` — le câblage unique (2 lignes)

`TimelineScreen.xaml.cs`, **juste après** le bloc `if (track == null) { … return border; }` (l. 1275-1279),
c'est-à-dire **avant** les deux `return` de la méthode :

```csharp
// Right-click anywhere on a track header: select that track, then open the organisation menu (§2 of the
// functional spec). Placed here so it covers BOTH shapes below (collapsed l. 1302 and expanded) and the
// docked chords header built by RenderChordDock. PREVIEW + Handled so a child (name TextBox, combo) can't
// swallow it or show its own menu; on button UP, like ModuleBoxControl, so the menu doesn't close at once.
border.PreviewMouseRightButtonUp += (s, e) => { e.Handled = true; SelectTrack(track); ShowTrackContextMenu(track, border); };
```

- `SelectTrack` (l. 1495-1509) sort immédiatement si la piste est déjà sélectionnée : aucun re-rendu inutile.
- **Garde optionnelle** si, au test, la zone de nom affiche encore le menu système de `TextBox` :
  `name.ContextMenuOpening += (s, e) => e.Handled = true;` juste après sa création (l. 1297). À ne poser que
  si le cas se constate.

### 4.3 Les quatre commandes (`TimelineScreen.xaml.cs`, ≈ 110 lignes, une seule région)

À placer en une région `// ===== Track organisation (duplicate / move / delete) =====`, de préférence à côté
de `AddTrack` (l. 3330) qui est déjà le pendant « ajouter ».

```csharp
// Right-click on a track header -> the four organisation commands. The chords track gets the menu too, all
// four GREYED (§4.1, criterion 15): showing them disabled is clearer than an empty menu.
void ShowTrackContextMenu(TimelineTrack track, FrameworkElement anchor)
{
    if (track == null) return;
    bool organisable = track.Type != TimelineTrackType.Chord;
    var menu = new ContextMenu();

    var dup  = new MenuItem { Header = Loc.T("DupliquerLaPiste"), IsEnabled = organisable };
    dup.Click  += (s, e) => DuplicateTrack(track);
    var up   = new MenuItem { Header = Loc.T("MonterLaPiste"),    IsEnabled = TimelineHelper.CanMoveTrack(project, track, -1) };
    up.Click   += (s, e) => MoveTrackBy(track, -1);
    var down = new MenuItem { Header = Loc.T("DescendreLaPiste"), IsEnabled = TimelineHelper.CanMoveTrack(project, track, +1) };
    down.Click += (s, e) => MoveTrackBy(track, +1);
    var del  = new MenuItem { Header = Loc.T("SupprimerLaPiste"), IsEnabled = organisable };
    del.Click  += (s, e) => DeleteTrack(track);

    menu.Items.Add(dup);
    menu.Items.Add(new Separator());
    menu.Items.Add(up); menu.Items.Add(down);
    menu.Items.Add(new Separator());
    menu.Items.Add(del);
    menu.PlacementTarget = anchor; menu.IsOpen = true;
}

// "Dupliquer la piste": a complete, INDEPENDENT copy inserted right below, and selected.
void DuplicateTrack(TimelineTrack track)
{
    if (track == null || track.Type == TimelineTrackType.Chord) return;
    CommitRiffEditor();                       // §4.7: whatever is being edited is committed first
    string pre = BeginUndo();                 // BEFORE creating the riffs -> undo also drops them (criterion 19)
    var copy = TimelineHelper.CloneTrack(track);
    if (copy == null) return;
    copy.Name = TimelineHelper.CopyName(project, track.Name, Loc.T("TrackCopySuffix"));
    project.Tracks.Insert(project.Tracks.IndexOf(track) + 1, copy);
    TimelineHelper.EnsureChordTrack(project);           // the chords track stays pinned last
    if (scoreTracks.Contains(track)) scoreTracks.Add(copy);   // §3.1: the ♫ state follows the copy
    selectedTrack = copy;                     // §3.5: the copy becomes the selected track…
    CommitUndo(pre, "track:dup");             // (selectedItem is left alone: §4.7, the editor keeps showing
    Render();                                 //  the ORIGINAL block it was on)
    ScrollTrackIntoViewLater(copy);
    RefreshMixer();
    if (ScoreVisible) RefreshScore();
}

// "Monter" / "Descendre": swap with the neighbour. Audio-neutral (§3.2).
void MoveTrackBy(TimelineTrack track, int delta)
{
    if (!TimelineHelper.CanMoveTrack(project, track, delta)) return;
    CommitRiffEditor();
    PushUndo("track:move");                   // one entry per move; key deliberately NOT prefixed "move:"
    TimelineHelper.MoveTrack(project, track, delta);
    selectedTrack = track;                    // §3.2: stays selected
    Render();
    ScrollTrackIntoViewLater(track);
    RefreshMixer();
    if (ScoreVisible) RefreshScore();         // staff order follows track order
}

// "Supprimer la piste" — shared by the ✕ button and the menu. NOW UNDOABLE (§3.3).
void DeleteTrack(TimelineTrack track)
{
    if (track == null || track.Type == TimelineTrackType.Chord) return;
    CommitRiffEditor();
    PushUndo("track:del");                    // capture BEFORE the removal
    project.Tracks.Remove(track);
    scoreTracks.Remove(track);
    if (selectedTrack == track) selectedTrack = null;
    // The bottom editor may be open on a block of the deleted track -> empty it (§4.7) and unhook the riff
    // editor, otherwise RefreshEditedRiffBox would later re-lay-out a track that no longer exists.
    if (selectedItem != null && track.Items != null && track.Items.Contains(selectedItem))
    {
        selectedItem = null;
        riffEditItem = null; riffEditTrack = null; riffDirty = false;
        editorHost.Content = null;
        txtEditorTitle.Text = Loc.T("Editeur");
    }
    Render();
    RefreshMixer();
    RefreshScore();                           // also brings the module editor back when the score isn't shown
}
```

Et la croix existante (l. 1318) devient :

```csharp
del.Click += (s, e) => DeleteTrack(track);
```

> **Sur la sélection après duplication.** Le §3.5 demande « sélection après duplication : la copie », le §4.7
> demande que l'éditeur « continue d'afficher le bloc d'origine ». Les deux sont tenus en ne touchant **que**
> `selectedTrack` : l'en-tête de la copie s'allume (`MakeHeader` l. 1273 teste `track == selectedTrack`), et
> `selectedItem` / `editorHost` / `riffEditItem` restent sur le bloc d'origine, dont l'édition continue de
> fonctionner. L'état est volontairement « mixte » (piste sélectionnée ≠ piste du bloc sélectionné) ; c'est
> déjà le cas ailleurs et aucun code ne suppose l'inverse (`DeleteItem` fait `track.Items.IndexOf(item)` et
> ne fait rien si l'item n'y est pas).

### 4.4 Défilement vers la piste (§3.1 et §3.2 : « l'affichage se déplace pour la montrer »)

```csharp
// Bring a track's row into view VERTICALLY. Always drive laneScroll: laneScroll_ScrollChanged (l. 4036)
// mirrors its offset onto headerScroll — scrolling headerScroll directly would desync the two halves.
void ScrollTrackIntoView(TimelineTrack track)
{
    if (track == null || track.Type == TimelineTrackType.Chord) return;  // the chords lane is docked, always visible
    double y = TempoH + (IsComposedArrangement() ? ChordH : 0);          // rows drawn before the tracks
    foreach (var t in project.Tracks)
    {
        if (t == track) break;
        if (t.Type == TimelineTrackType.Chord) continue;
        y += TrackRowH(t);
    }
    double h = TrackRowH(track), top = laneScroll.VerticalOffset, view = laneScroll.ViewportHeight;
    if (y < top) laneScroll.ScrollToVerticalOffset(y);
    else if (y + h > top + view) laneScroll.ScrollToVerticalOffset(Math.Max(0, y + h - view));
}

// Right after Render() the ScrollViewer's extent is still the OLD one (layout hasn't run), so a scroll
// request would be clamped to a stale maximum. Defer to after layout.
void ScrollTrackIntoViewLater(TimelineTrack track)
    => Dispatcher.BeginInvoke(new Action(() => ScrollTrackIntoView(track)),
                              System.Windows.Threading.DispatcherPriority.Loaded);
```

`TempoH`, `ChordH`, `TrackRowH` sont déjà définis (l. 29 et 1255) — les mêmes valeurs que `Render` empile
dans `lanePanel`, donc le calcul ne peut pas diverger de l'affichage.

### 4.5 Le fait que « rien ne change au son » (§3.2)

Aucun code : `TimelinePlayer` reconstruit tout à la lecture suivante à partir de `project.Tracks` dans
l'ordre, et l'ordre n'a d'incidence que sur **l'affectation des canaux MIDI** internes du synthé
(`TrySetupMeltySynth`, l. 214-224), pas sur les notes ni sur leurs instants. Idem pour les exports, qui
parcourent `project.Tracks` (l'ordre des pistes/portées exportées suit le nouvel ordre — explicitement
autorisé par le §4.8).

### 4.6 `MusicTracker\Dialogs\MixerDialog.xaml.cs` + `TimelineScreen` — rafraîchir le mixeur (critère 11)

Dans `MixerDialog` (garder le projet en champ, ~4 lignes) :

```csharp
readonly TimelineProject project;

public MixerDialog(TimelineProject project, Window owner)
{
    InitializeComponent();
    Owner = owner;
    this.project = project;
    list.ItemsSource = project?.Tracks;
}

/// <summary>Re-read the track list after the timeline added / removed / reordered tracks. `Tracks` is a plain
/// List (no change notification), so the ItemsSource has to be re-assigned.</summary>
public void RefreshTracks()
{
    list.ItemsSource = null;
    list.ItemsSource = project?.Tracks;
}
```

Dans `TimelineScreen` :

```csharp
// The mixer is NON-modal: track add/remove/reorder can happen while it is open.
void RefreshMixer() { try { mixerWindow?.RefreshTracks(); } catch { mixerWindow = null; } }
```

Appelée par `DuplicateTrack`, `MoveTrackBy`, `DeleteTrack`, `RestoreState` — et, tant qu'à faire, par
`AddTrack` (l. 3330-3339), qui souffre aujourd'hui du même défaut.

### 4.7 `MusicTracker\Engine\Timeline\TimelinePlayer.cs` — figer l'appariement piste ↔ voix (§4.6)

Sans ce correctif, dupliquer/déplacer/supprimer **pendant la lecture** applique les volumes/pan/mute/solo aux
mauvaises voix, et une suppression peut faire tomber le thread de pré-rendu (démonstration §1.8).

1. Dans `sealed class Track` (l. 23-35), ajouter :

```csharp
public TimelineTrack Src;   // the project track this voice was built from (mixer values are read LIVE from it)
```

2. Dans la boucle de construction (l. 165-186), poser `Src = tr` dans l'initialiseur du `Track`.

3. Dans `ApplyChannelVolumes` (l. 493-518), remplacer la lecture par index :

```csharp
bool anySolo = false;
for (int i = 0; i < tracks.Length; i++) if (tracks[i].Src != null && tracks[i].Src.Solo) { anySolo = true; break; }
for (int ti = 0; ti < tracks.Length; ti++)
{
    var p = tracks[ti].Src;      // no more melodyProject.Tracks[ti]: the list can be mutated by the UI thread
    …
}
```

4. Dans `ApplyPrograms` (l. 239-258), remplacer `var tr = project.Tracks[i];` par `var tr = tracks[i].Src;`
   (et laisser le paramètre, ou le supprimer — au choix, il n'a plus d'utilité).

Le réglage en direct depuis le mixeur continue de fonctionner (mêmes **objets** `TimelineTrack`, propriétés
lues à chaque buffer) ; c'est seulement l'appariement **par index** qui disparaît.

### 4.8 Ce qui n'est **pas** touché

`UndoManager`, `TimelineProject` (le modèle), `TimelineImporter`, `TemplateProjectBuilder`, `Orchestrateur`,
`MidiTimelineExporter`, `MuseScoreExporter`, `MusicXmlExporter`, `WaveExporter`, `ScoreBuilder` / `ScoreView`,
`LookaheadBuffer`, `MeasureRulerControl` / `ModuleBoxControl` / `VolumeLaneControl`, `MainWindow`,
`GuidedTour` (le fonctionnel ne demande pas d'étape de visite guidée), `MusicTracker.csproj` (aucun fichier
nouveau), `Save` / `LoadSqFile` / `LoadDocument`. Le `CHANGELOG` est mis à jour par le run de publication.

---

## 5. Localisation — 4 clés à créer dans les **7** fichiers

`MusicTracker\Localization\lang.{fr,en,de,it,es,nl,pt}.json` (dictionnaire plat ; ajouter les clés dans le
même ordre dans chaque fichier, en pratique à la fin avant l'accolade fermante).

⚠️ **Éditer ces fichiers avec Write/Edit, JAMAIS via `Get-Content`/`Set-Content` PowerShell** : cela
transforme les accents en mojibake (note du dépôt).

| Clé | fr | en | de | it |
|---|---|---|---|---|
| `DupliquerLaPiste` | Dupliquer la piste | Duplicate the track | Spur duplizieren | Duplica la traccia |
| `MonterLaPiste` | Monter | Move up | Nach oben | Sposta su |
| `DescendreLaPiste` | Descendre | Move down | Nach unten | Sposta giù |
| `TrackCopySuffix` | copie | copy | Kopie | copia |

| Clé | es | nl | pt |
|---|---|---|---|
| `DupliquerLaPiste` | Duplicar la pista | De track dupliceren | Duplicar a faixa |
| `MonterLaPiste` | Subir | Omhoog | Subir |
| `DescendreLaPiste` | Bajar | Omlaag | Descer |
| `TrackCopySuffix` | copia | kopie | cópia |

**Clé réutilisée, à ne pas recréer :** `SupprimerLaPiste` existe déjà dans les 7 fichiers (fr « Supprimer la
piste », en « Delete the track », de « Spur löschen », it « Elimina la traccia », es « Eliminar la pista »,
nl « De track verwijderen », pt « Eliminar a faixa ») — c'est l'infobulle actuelle de la croix ✕ ; le menu
l'emploie telle quelle, ce qui garantit que les deux chemins portent le même mot (§2 fonctionnel).

Notes :

- `TrackCopySuffix` est le **mot seul** ; le code assemble `nom + " (" + mot + ")"` puis
  `nom + " (" + mot + " " + N + ")"`. Volontairement pas de gabarit `{0}` dans le JSON : un traducteur ne peut
  pas casser un espace réservé qui n'existe pas. Les sept langues acceptent la forme « (mot N) ».
- Le suffixe est **figé au moment de la duplication** : une copie faite en français garde « (copie) » si
  l'utilisateur passe ensuite en anglais. C'est un **nom de données**, pas un libellé d'interface — conforme
  au §4.9, qui ne parle que des textes affichés par l'application.
- Les entrées existantes `Copier` (« 📋 Copier ») et `Supprimer` (« 🗑 Supprimer ») du menu des **blocs**
  portent des émojis ; le menu des **pistes** n'en met pas — le fonctionnel ne l'exige pas et le libellé
  réutilisé `SupprimerLaPiste` n'en a pas. Cohérence à valider à l'œil (test H1).
- Aucune chaîne visible ne doit rester en dur : `TimelineHelper` ne référence pas `Loc` (le mot « copie » lui
  est **passé** par `TimelineScreen`).

---

## 6. Risques, régressions, et ce qui les empêche

| # | Risque | Gravité | Ce qui l'empêche |
|---|---|---|---|
| 1 | **Copie non indépendante** : éditer un bloc de la copie modifie l'original (riff partagé) | bloquant — c'est **le** point de la commande (critère 7) | `CloneTrack` recrée un `Riff` avec un **nouveau `Guid`** pour chaque `PlayRiffModule` (§4.1). Test H4 dans les deux sens. |
| 2 | **Copie vide sans erreur** : `IncludeFields` oublié dans les options de clonage | bloquant, très sournois (aucune exception, une piste blanche) | Commentaire explicite + `CloneOpts` unique dans `TimelineHelper` (§4.1). Test A2 compare le nombre de blocs. |
| 3 | **État ♫ perdu au Ctrl+Z** | critère 18 | §3.3 : la sélection ♫ voyage dans l'entrée d'undo. Sans ce point, le critère ne peut pas passer (§1.6). |
| 4 | **Riffs orphelins** : le `.sq` grossit après duplication + annulation | critère 19 | `BeginUndo()` est appelé **avant** `CloneTrack`, donc le snapshot pré-duplication ne contient pas les nouveaux riffs et `ApplyDocument` reconstruit `RiffLibrary` à partir de lui (§1.5). Test A4 compare les tailles. |
| 5 | **Coalescence d'undo inattendue** (deux déplacements fusionnés) | critère 17 | Clés `track:dup` / `track:move` / `track:del` : aucune ne commence par `move:`, `edit:`, `vol:`, `insert:`, `delete:` → `IsCoalescable` (l. 66-67) est faux et la neutralisation ne se déclenche jamais. |
| 6 | **Une piste passe sous la piste d'accords** ou la piste d'accords se duplique | critères 13, 15 | `CanMoveTrack` refuse le voisin de type `Chord` et toute piste `Chord` ; `DuplicateTrack` / `DeleteTrack` refusent le type `Chord` ; `EnsureChordTrack` (Render l. 1109) repose l'invariant en dernier recours. |
| 7 | **Menu absent sur un en-tête replié** | critère 3 | Le handler est posé **avant** les deux `return` de `MakeHeader` (§4.2), pas dans la branche dépliée. |
| 8 | **Le menu système de la zone de nom (TextBox) l'emporte** | moyen, visuel | `PreviewMouseRightButtonUp` (tunneling) + `e.Handled = true` sur le `Border`. Garde de secours en une ligne (`ContextMenuOpening`) documentée §4.2. Test H2. |
| 9 | **Éditeur du bas resté sur un bloc supprimé** (état actuel, aggravé si on ne le corrige pas) | haute | `DeleteTrack` vide `selectedItem`, `riffEditItem`, `riffEditTrack`, `editorHost` quand le bloc édité appartenait à la piste (§4.3). Test H8. |
| 10 | **Mixeur désynchronisé** (ordre faux, ligne fantôme) | critère 11 | `RefreshMixer()` sur les quatre commandes + `RestoreState` + `AddTrack` (§4.6). Test H6. |
| 11 | **Mixage appliqué aux mauvaises voix pendant la lecture** après duplication/déplacement | §3.2 (« ne change rien au son ») | §4.7 : le player indexe son propre `Track.Src`, plus `melodyProject.Tracks[ti]`. Test H10 (à l'oreille). |
| 12 | **Crash du thread de pré-rendu** si l'on supprime une piste en pleine lecture | crash | Même correctif §4.7 : plus aucune indexation d'une `List<T>` mutée par le thread UI depuis le thread audio (`LookaheadBuffer.Produce` n'a pas de try/catch, l. 81). Test H11. |
| 13 | **La régénération d'un morceau généré vise la copie** | critère 21 | §1.9 : les trois recherches par nom prennent le **premier** match ; la copie a un autre nom **et** vient après l'original. Aucun code. Test H12. |
| 14 | **Défilement qui ne suit pas** (offset calculé sur une extension périmée) | cosmétique | `ScrollTrackIntoViewLater` diffère à `DispatcherPriority.Loaded` (§4.4) ; on pilote `laneScroll`, jamais `headerScroll`. Test H5. |
| 15 | **Nom en collision** (« Mélodie (copie) » créé deux fois) | critère 8 | `CopyName` boucle tant que le nom est pris. Test H3. |
| 16 | **Accents détruits dans les `lang.*.json`** | bloquant | Interdiction de `Get-Content`/`Set-Content` (§5) ; test A5 relit les 7 fichiers en UTF-8. |
| 17 | Duplication d'une piste de plusieurs centaines de blocs perçue comme un gel | §4.5 | Un clone = une sérialisation JSON de la seule piste + N `Riff.Clone()` ; le coût dominant reste `Render()`, déjà celui d'une frappe ordinaire. Test H9 chronométré à la main. |
| 18 | Changement visible **hors périmètre** : les cases ♫ ne se décochent plus au Ctrl+Z | faible, à signaler | Conséquence assumée du §3.3 ; c'est la correction d'un défaut, pas une régression. À écrire dans `03-tests.md` pour que le testeur ne le prenne pas pour un bug. |

---

## 7. Plan de test

**Préparation, AVANT d'appliquer le correctif :** compiler la version actuelle, construire un morceau
d'au moins **trois pistes dont une batterie** (plus la piste Accords), avec des réglages **distincts** par
piste (instrument, volume, pan, un muet, une courbe de volume, une piste repliée, une case ♫ cochée), et
l'enregistrer sous `temoin-avant.sq`. Exporter MIDI + `.mscx` + audio → `temoin-avant.*` (références des
critères 20 et 22). Le dépôt ne contient aucun témoin de ce genre.

Build : `"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
MusicTracker.sln /p:Configuration=Debug` — **jamais `dotnet build`** (csproj ancien format, .NET Framework 4.8).

### 7.1 Vérifiable automatiquement (build, script, inspection de fichier)

| # | Vérification | Méthode | Critère |
|---|---|---|---|
| A1 | La solution compile sans erreur ni nouvel avertissement | MSBuild | — |
| A2 | Après duplication + enregistrement, le `.sq` contient **deux** pistes de même contenu : même nombre d'`Items`, mêmes `SilenceBefore`, mêmes longueurs de riff — et des `RiffId` **tous différents** entre les deux pistes | script : charger le JSON, comparer piste N et N+1, vérifier l'intersection vide des `RiffId` | 5, 7 |
| A3 | Le nombre de `Riffs` du document a augmenté d'exactement le nombre de `PlayRiffModule` de la piste d'origine | idem | 7 |
| A4 | **Dupliquer → Ctrl+Z → enregistrer** produit un fichier de taille ≤ celle de `temoin-avant.sq`, et le même nombre d'entrées `Riffs` | comparer les deux JSON | 19 |
| A5 | Les 4 clés existent dans les **7** `lang.*.json`, valeurs non vides ; `SupprimerLaPiste` reste présente partout | script : charger les 7 JSON en UTF-8, vérifier les clés et l'intégrité des accents (« cópia », « Spur löschen ») | 23 |
| A6 | Un `.sq` dont on a seulement **déplacé** les pistes rejoue les mêmes notes : exports MIDI et `.mscx` contenant les **mêmes notes** que `temoin-avant.*` (l'ordre des pistes/portées peut différer) | comparer, piste par piste après ré-appariement par nom, la liste (instant, hauteur, durée) | 22, §4.8 |
| A7 | Ouverture de `temoin-avant.sq` (créé avant la feature) : aucune exception | ouvrir le fichier, ou désérialiser le JSON | 20 |
| A8 | Aucune chaîne française littérale ajoutée hors `lang.fr.json` | `git grep` sur « copie », « Monter », « Descendre » dans `Engine\` et `Screens\` | 23 |
| A9 | Non-régression du harnais FlaUI : l'application démarre, les scénarios existants restent verts | `AutoTest\run.ps1` | — |

### 7.2 Exige un jugement humain (rendu visuel, rendu sonore, ressenti)

| # | Vérification | Attendu | Critère |
|---|---|---|---|
| H1 | Clic droit sur l'en-tête d'une piste | la piste **se sélectionne** (fond de l'en-tête) puis un menu à 4 entrées s'ouvre : Dupliquer la piste / Monter / Descendre / Supprimer la piste ; lisible, thémé comme celui des blocs | 1 |
| H2 | Clic droit sur la **zone de nom**, sur le combo instrument, sur le curseur de volume | le même menu de piste s'ouvre (pas le menu système de la zone de texte) | 1 |
| H3 | Clic droit sur la **première** piste, puis sur la **dernière piste déplaçable** | « Monter » grisé dans le premier cas, « Descendre » grisé dans le second — **grisés, pas absents** | 2, 13 |
| H4 | Clic droit sur un en-tête **replié** | même menu, mêmes 4 commandes | 3 |
| H5 | Dupliquer « Mélodie » | « Mélodie (copie) » apparaît **juste en dessous**, sélectionnée, visible à l'écran (défilement si nécessaire) ; les boîtes des deux lanes sont **alignées** ; les étiquettes des blocs sont **identiques** | 4, 5 |
| H6 | Comparer les deux en-têtes | même instrument, même volume, même pan (via le mixeur), mêmes M/S, même courbe de volume, même état replié, même case ♫ | 6 |
| H7 | **Indépendance** : ouvrir un bloc de la copie, changer une note, revenir sur le bloc correspondant de l'original ; puis l'inverse | aucun des deux ne suit l'autre | 7 |
| H8 | Dupliquer deux fois la même piste | « (copie) » puis « (copie 2) » | 8 |
| H9 | Dupliquer une piste de **batterie** | reste une piste de batterie, même kit dans le combo, mêmes motifs ; **à l'oreille**, la copie sonne comme l'original | 9 |
| H10 | Enregistrer, fermer l'onglet, rouvrir | la copie est là, à sa place, avec son nom et son contenu | 10 |
| H11 | Ouvrir le **mixeur**, puis descendre la première piste | l'ordre change dans la timeline **et** dans le mixeur, sans fermer/rouvrir le dialogue ; supprimer une piste : sa ligne disparaît du mixeur | 11 |
| H12 | Monter puis descendre la même piste | retour exact à la position de départ ; **à l'oreille**, lecture identique à avant | 12, 14 |
| H13 | Clic droit sur la piste **Accords** | les 4 commandes sont grisées ; impossible d'obtenir deux pistes d'accords | 15 |
| H14 | Ctrl+Z juste après une duplication, puis Ctrl+Y | la copie disparaît, puis revient à l'identique (nom, contenu, réglages, case ♫) | 16 |
| H15 | Ctrl+Z juste après un déplacement | **un seul** Ctrl+Z ramène la piste à sa position précédente | 17 |
| H16 | Supprimer une piste **pleine** par la croix ✕, Ctrl+Z, Ctrl+Y ; refaire depuis le menu contextuel | la piste revient **à sa place d'origine**, avec ses blocs, ses réglages et sa case ♫ ; Ctrl+Y la resupprime ; identique par les deux chemins | 18 |
| H17 | Supprimer la piste dont un bloc est **ouvert dans l'éditeur du bas** | l'éditeur se vide, aucune erreur, aucun résidu cliquable | §4.7 |
| H18 | Dupliquer une piste dont un bloc est ouvert dans l'éditeur | l'éditeur continue d'afficher le **bloc d'origine** et reste éditable ; rien de perdu | §4.7 |
| H19 | Dupliquer une piste **vide** | piste vide aux mêmes réglages, aucun message | §4.4 |
| H20 | Sur un morceau **généré** (structure / orchestrateur), dupliquer « Accompagnement » puis relancer la régénération | c'est l'**original** qui est régénéré ; la copie reste intacte ; grille d'accords, thème et sections inchangés | 21, §4.3 |
| H21 | **Pendant la lecture** : dupliquer, puis monter une piste | la lecture ne s'interrompt pas ; **à l'oreille**, aucun changement de niveau, de panoramique ni de muet sur les autres pistes ; la piste ajoutée n'est entendue qu'à la lecture suivante | §4.6, §3.2 |
| H22 | **Pendant la lecture** : supprimer une piste | aucun plantage, aucune coupure ; la lecture continue | §4.6 |
| H23 | Morceau **lourd** (plusieurs centaines de blocs sur la piste) : dupliquer | pas de gel long ni d'écran blanc ; l'opération reste perceptiblement immédiate | §4.5 |
| H24 | Basculer l'application dans les **7 langues** | les 4 entrées du menu et le suffixe du nom de copie sont traduits ; aucune clé brute, aucun français résiduel | 23 |
| H25 | Non-régression : la croix ✕ supprime toujours immédiatement, sans confirmation ; ajouter une piste, la replier, la déplier fonctionne comme avant | — | §3.3, §5 |

### 7.3 Ce qui ne pourra pas être vérifié par un run automatisé

Le menu contextuel n'est pas pilotable de façon fiable par UIA : les en-têtes de pistes sont construits en
code, sans `AutomationProperties.AutomationId` (seuls la barre d'outils et le transport en ont, cf.
`TimelineScreen.xaml` l. 252-258). H1 à H25 sont donc **tous manuels**, sauf à instrumenter les en-têtes —
ce qui n'est **pas** demandé. Les points sonores (H9, H12, H21) et visuels (H5, H6) le resteront de toute
façon. À noter tel quel dans `03-tests.md`.

---

## 8. Estimation

**Moyen** — une session.

| Poste | Volume |
|---|---|
| `Engine\Timeline\TimelineHelper.cs` | ~60 lignes ajoutées (`CloneTrack`, `CanMoveTrack`, `MoveTrack`, `CopyName`, `NameTaken`, `CloneOpts`) |
| `Screens\TimelineScreen.xaml.cs` | ~110 lignes ajoutées (menu + 3 commandes + défilement + `RefreshMixer`), **2 lignes** dans `MakeHeader`, **1 ligne** réécrite sur la croix ✕, ~15 lignes remaniées dans `SnapshotState`/`RestoreState` (§3.3) |
| `Dialogs\MixerDialog.xaml.cs` | ~8 lignes (champ + `RefreshTracks`) |
| `Engine\Timeline\TimelinePlayer.cs` | ~6 lignes (champ `Src` + 2 lectures réécrites) |
| `Localization\lang.{fr,en,de,it,es,nl,pt}.json` | 4 clés × 7 fichiers |
| `MusicTracker.csproj` | **rien** (aucun fichier nouveau) |
| `Screens\TimelineScreen.xaml` | **rien** |
| `Engine\Timeline\TimelineProject.cs` | **rien** (aucun champ nouveau) |

Le risque n'est pas dans le volume mais dans quatre points, à traiter en premier :
`IncludeFields` du clone (risque 2), le nouveau `Guid` des riffs clonés (risque 1), le passage de l'état ♫
dans le snapshot d'undo (risque 3, §3.3), et le `BeginUndo()` **avant** la création des riffs (risque 4).
Le correctif du player (§4.7) est indépendant des trois autres et peut se faire en premier ou en dernier.
