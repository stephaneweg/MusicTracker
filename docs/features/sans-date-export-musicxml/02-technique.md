# Export MusicXML — analyse technique

Référence fonctionnelle : `01-fonctionnel.md` (même dossier). Ce document décrit **comment** l'implémenter,
et n'ajoute aucune exigence fonctionnelle nouvelle.

---

## 1. Ce que le code fait déjà (vérifié dans les sources)

La chaîne « morceau → partition » existe et n'est **pas** à réinventer.

| Étage | Fichier | Rôle |
|---|---|---|
| Aplatissement d'une piste en notation | `MusicTracker\Engine\Score\ScoreModel.cs` → `ScoreBuilder.Build(project, track, resolveRiff)` | déroule repeats/riffs/patterns/cadences/lignes mélodiques, applique `TimeSigScale`, snappe sur la grille 1/8 ou 1/6 de temps, choisit la clé et la transposition, renvoie un `TrackScore` |
| Modèle de notation | idem (`ScoreNote`, `TrackScore`, `KeySignature`, `KeySig.Derive`, `ScoreClef.ForTrack`) | notes en **beats d'affichage** (quart de ronde), hauteur **sonnante**, `Transpose` = sonnant → écrit, `Clef`, `Key` |
| Sortie MuseScore | `MusicTracker\Engine\Timeline\MuseScoreExporter.cs` | `TrackScore[]` → `.mscx` |
| Sortie PDF | `MusicTracker\Controls\Score\ScorePdfExporter.cs` | `TrackScore[]` → `FixedDocument` |
| Déclenchement | `MusicTracker\Screens\TimelineScreen.xaml.cs` → `btnExportMuseScore_Click` (l. 3943-3966) + `MusicTracker\Screens\TimelineScreen.xaml` l. 359-364 (menu `Export`) | sélection des pistes, dialogue de fichier, messages |

Points relevés en lisant le code, qui conditionnent la conception :

1. **`ScoreBuilder` est déjà le point unique** où une piste devient de la notation. Le PDF, le `.mscx` et la
   vue partition partent tous du même `TrackScore`. Une **troisième** sortie n'a donc **rien** à ajouter en
   amont : elle consomme `TrackScore`, point.
2. `ScoreBuilder.Build` multiplie déjà les temps par `project.TimeSigScale` (le ×1.5 du 4/4-en-triolets
   affiché en 12/8). Les `TrackScore` sont donc en **espace d'affichage** — c'est exactement ce que la
   partition doit écrire (`01-fonctionnel.md` §5, ligne « mesure ternaire affichée »).
3. La **seule** faiblesse à corriger est localisée dans `MuseScoreExporter.WriteStaff` :
   ```csharp
   if (c.start + c.len > me) break;     // chord runs past the bar line: truncate here (no tie)
   ```
   et dans `EmitChord`, qui ré-attaque chaque figure de la décomposition au lieu de les lier. Le nouvel
   export ne « corrige » pas ce fichier (interdit par §7 du fonctionnel) : il écrit **sa propre** boucle
   mesure/liaison.
4. `TimelineScreen` n'a **aucun indicateur « projet modifié »** (aucun champ `Dirty`/`IsModified` ; le seul
   `riffDirty` concerne l'éditeur de riff inline). Le critère 16 « projet intact » est donc satisfait par
   construction dès lors que le gestionnaire ne mute rien et n'appelle ni `PushUndo`/`BeginUndo` ni `Render`.
5. `Dialogs\FileBrowserDialog` (`SaveMode = true`) gère déjà l'ajout de `DefaultExt` et la **confirmation
   d'écrasement** (l. 228-240). Rien à faire pour le cas limite « fichier déjà existant ».
6. Les clés de localisation `AucunePisteAExporter`, `AucunePisteMelodiqueAExporterCoche`, `Partition` et
   `Voix2` (« Voix », avec espace final) **existent déjà dans les 7 fichiers** — à réutiliser, pas à recréer.

---

## 2. Approche retenue

### 2.1 Le point unique

> **Un nouveau fichier `MusicTracker\Engine\Timeline\MusicXmlExporter.cs`, jumeau de `MuseScoreExporter`,
> alimenté par `ScoreBuilder.Build`, et **un seul** gestionnaire `btnExportMusicXml_Click` dans
> `TimelineScreen.xaml.cs` calqué sur `btnExportMuseScore_Click`.**

Rien d'autre ne bouge. En particulier **on ne touche pas** : `ScoreModel.cs`/`ScoreBuilder`,
`MuseScoreExporter.cs`, `ScorePdfExporter.cs`, `TimelineProject.cs`, `TimelineDocument`, le format `.sq`,
`ApplyDocument`, l'undo, `FileAssociations.cs`.

### 2.2 Alternatives écartées

| Option | Pourquoi non |
|---|---|
| **Généraliser `MuseScoreExporter`** (un moteur commun `.mscx` + MusicXML, ou une couche « notation intermédiaire » partagée) | Toucher `MuseScoreExporter` c'est risquer une régression sur un export existant, explicitement exclu par §6 et §7 du fonctionnel. La factorisation deviendra pertinente le jour où l'on ajoutera les liaisons au `.mscx` (session dédiée prévue) — c'est **là** qu'il faudra fusionner les deux, pas maintenant. |
| **Écrire du `.mxl`** (MusicXML zippé) | Hors périmètre §7. `System.IO.Compression` n'est d'ailleurs pas référencé par le csproj. |
| **Passer par `MuseScoreExporter` puis convertir** (XSLT, appel externe à MuseScore) | Dépendance externe, et on hériterait des notes tronquées — c'est exactement ce que la feature veut corriger. |
| **Réutiliser le découpage de `ScorePdfExporter`** | Il découpe en **systèmes de page**, pas en mesures notées, et ne produit ni durées symboliques ni liaisons. Rien à récupérer. |
| **Factoriser l'orthographe des hauteurs (`Tpc`) dans un helper partagé** | Modifierait `MuseScoreExporter`. Les ~12 lignes sont **recopiées** dans le nouveau fichier, avec un commentaire `<remarks>` qui pointe vers l'original et signale qu'un futur travail les fusionnera. Duplication assumée, risque nul. |

### 2.3 Format cible

`score-partwise` **MusicXML 4.0** (partwise = une `<part>` par portée, chaque part contenant ses
`<measure>` — c'est la forme que produisent MuseScore/Finale et celle qui se génère le plus simplement en
un seul passage).

En-tête :

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE score-partwise PUBLIC "-//Recordare//DTD MusicXML 4.0 Partwise//EN"
                                "http://www.musicxml.org/dtds/partwise.dtd">
<score-partwise version="4.0">
```

Écriture `File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false))` — **UTF-8 sans BOM**, comme le
`.mscx`. Les accents/kanji/`&`/`<` passent par le même `Esc()` que `MuseScoreExporter` (recopié).

> ⚠️ **Piège pour les scripts de test** : le `<!DOCTYPE>` fait qu'un `XmlDocument.Load` naïf tente d'aller
> chercher la DTD sur `musicxml.org`. Les scripts de vérification doivent utiliser
> `XmlReaderSettings { DtdProcessing = Parse; XmlResolver = $null }` (voir §7).

---

## 3. Conception détaillée de `MusicXmlExporter`

Fichier : `MusicTracker\Engine\Timeline\MusicXmlExporter.cs`, namespace `MusicTracker.Engine.Timeline`,
classe `public static class MusicXmlExporter`.

### 3.1 API

```csharp
public struct Part { public string Name; public int Program; public TrackScore Score; }

public static void Export(string path, List<Part> parts, int num, int den,
                          double timeSigScale, double bpm, string title);
```

Même forme que `MuseScoreExporter.Export`, plus deux paramètres que le `.mscx` n'écrit pas :
`timeSigScale` et `bpm` (nécessaires à l'indication métronomique, §3.6).

### 3.2 Unités et grille — identiques au `.mscx`

* `const int U = 24;` unités entières par noire ⇒ `<divisions>24</divisions>` (une unité = une quadruple
  croche de triolet ; c'est le PPCM des grilles 1/8 et 1/6 de temps utilisées par `ScoreBuilder`).
* `sigN = max(1, num)`, `sigD = (den == 8 ? 8 : 4)` — **exactement** la règle du `.mscx` (`TimeSigDen` ne
  vaut jamais autre chose que 4 ou 8 dans le projet).
* `barUnits = sigN * 96 / sigD` (4/4 → 96, 3/4 → 72, 6/8 → 72, 12/8 → 144). Toujours un multiple de 3.
* `R3(beats)` : arrondi sur la grille de 3 unités (1/32), recopié du `.mscx`. Conséquence connue et
  assumée : les triolets sont approximés (§3.4 et §7 du fonctionnel).
* `beatUnits` (pour la décomposition métrique) : `sigD == 8 && sigN % 3 == 0 ? 36 : 24` — le temps réel
  est la noire pointée en mesure composée.

### 3.3 Nombre de mesures — corrige une perte de notes

```csharp
int measures = 1;
foreach (var p in parts)
{
    int endU = (int)Math.Round((p.Score?.TotalBeats ?? 0) * U);
    foreach (var n in p.Score.Notes)                     // ← en plus du .mscx
        endU = Math.Max(endU, R3(n.StartBeat) + Math.Max(3, R3(n.Beats)));
    measures = Math.Max(measures, Math.Max(1, (endU + barUnits - 1) / barUnits));
}
```

Le `.mscx` ne regarde que `TotalBeats` ; prendre aussi la fin réelle des notes garantit le cas limite
« piste dont les notes dépassent la fin théorique du morceau » (§5). Le `max` sur **toutes** les parts
donne l'alignement des portées (§3.5) : les portées courtes se terminent en mesures de silence.

### 3.4 Des `ScoreNote` aux « événements » monophoniques

Reprise **à l'identique** de la fusion du `.mscx` (§7 du fonctionnel : pas de voix multiples) :

1. Grouper les notes par **onset arrondi** `R3(n.StartBeat)` dans une `SortedDictionary<int, …>` ;
   `len = max(R3(n.Beats))` du groupe ; `pitches` = liste des hauteurs **écrites** (`n.Midi + Transpose`,
   ignorer hors 0..127) ; `arp |= n.Arpeggio`.
   *(Le champ `ScoreNote.Voice` posé par `MarkBassVoice`/`MarkSustainVoice` est ignoré, comme dans le
   `.mscx` : une note tenue sur une figure rapide sera fusionnée en accord. Limite connue, identique à
   l'existant.)*
2. Aplatir en une liste ordonnée d'**événements** `(start, end, pitches, arp)` avec
   `end = min(start + len, startDuSuivant)` — la clôture au prochain onset garantit une portée
   strictement monophonique (au sens « une seule voix »), donc des mesures qui se remplissent exactement.
   Les onsets distincts diffèrent d'au moins 3 unités (tous multiples de 3), donc `end > start` toujours.
   **Différence avec le `.mscx` : pas de plafonnement à la barre de mesure** — c'est ici que naissent les
   liaisons.

### 3.5 Boucle mesure + liaisons — le cœur de la feature

Pour chaque part, on parcourt les mesures `m = 0 … measures-1`, `ms = m*barUnits`, `me = ms+barUnits`,
`pos = ms`, en gardant l'indice de l'événement courant :

```
pour chaque événement e chevauchant [ms, me) :
    si e.start > pos            → EmitRests(pos - ms, e.start - pos)     // silences bouche-trou
    segStart = max(e.start, ms) ; segEnd = min(e.end, me)
    EmitChordSegment(pitches, segStart - ms, segEnd - segStart,
                     tieStop  : e.start < ms,        // continuation d'une mesure précédente
                     tieStart : e.end   > me,        // se prolonge dans la mesure suivante
                     arp      : e.arp && e.start >= ms)   // le signe d'arpège ne va que sur l'attaque
    pos = segEnd
    si e.end > me → sortir (l'événement reprendra à la mesure suivante) sinon passer au suivant
si pos < me → EmitRests(pos - ms, me - pos)
```

`EmitChordSegment` décompose la longueur du segment en figures standard (§3.7) et **lie chaque figure à la
suivante** :

* figure `i` reçoit `tie type="stop"` si (`i > 0`) **ou** `tieStop` ;
* figure `i` reçoit `tie type="start"` si (`i < dernière`) **ou** `tieStart`.

Cela couvre d'un coup les deux exigences §3.4 : la note de cinq croches (plusieurs figures **liées** dans
la même mesure) et la note à cheval sur la barre (segments liés de part et d'autre).

Chaque figure produit un `<note>` **par hauteur** de l'accord ; les hauteurs 2..n portent `<chord/>` en
premier enfant. La liaison se porte en MusicXML **note par note** : `<tie>`/`<tied>` sont donc écrits sur
**toutes** les hauteurs de l'accord, pas seulement sur la première.

**Invariant garanti par construction** : à la fin de chaque mesure, la somme des `<duration>` des notes
non-`<chord/>` vaut exactement `barUnits`. C'est le critère d'acceptation 8, et il est **vérifiable par
script** (§7).

**Mesure entièrement silencieuse** → une seule note `<note><rest measure="yes"/><duration>barUnits</duration>
<voice>1</voice></note>`. Cela règle proprement le silence de mesure entière dans tous les chiffrages
(y compris 12/8, où `barUnits = 144` n'est pas une figure standard).

### 3.6 Contenu de la première mesure de chaque part

```xml
<attributes>
  <divisions>24</divisions>
  <key><fifths>F</fifths><mode>major|minor</mode></key>
  <time><beats>sigN</beats><beat-type>sigD</beat-type></time>
  <clef><sign>G|F|C</sign><line>2|4|3</line></clef>
  <transpose><diatonic>-D</diatonic><chromatic>-T</chromatic></transpose>   <!-- si Transpose != 0 -->
</attributes>
```

* `F` = `KeySig.Derive(ts.Key ?? new KeySignature(), ts.Transpose)` → `dk.Flats ? -dk.Count : dk.Count`.
  **Exactement** l'appel du `.mscx` : l'armure écrite tient compte de la transposition de l'instrument.
* `<mode>` : `ts.Key.Mode == 1 ? "minor" : "major"`.
* Clé : `Treble → (G,2)`, `Bass → (F,4)`, `Alto → (C,3)`, `Tenor → (C,4)`, `GrandStaff → (G,2)`
  (le `.mscx` fait le même repli par `default`).
* Transposition : `TrackScore.Transpose` = demi-tons **ajoutés au sonnant pour obtenir l'écrit**.
  MusicXML `<transpose>` va dans l'autre sens (écrit → sonnant) : `chromatic = -Transpose`,
  `diatonic = -round(Transpose * 7.0 / 12.0)`. Vérifié sur les 5 valeurs que `ScoreClef.ForTrack` peut
  produire : 2 → (-1,-2) si♭ ; 7 → (-4,-7) fa ; 9 → (-5,-9) mi♭ ; 14 → (-8,-14) ténor si♭ ;
  21 → (-12,-21) baryton mi♭. Balise omise si `Transpose == 0`.
* Tempo, uniquement sur la **première mesure de la première part** :
  ```xml
  <direction placement="above">
    <direction-type><metronome><beat-unit>quarter</beat-unit><per-minute>N</per-minute></metronome></direction-type>
    <sound tempo="N"/>
  </direction>
  ```
  avec `N = round(project.MainBpm * TimeSigScale)`. **La mise à l'échelle est indispensable** : `Bpm` est
  exprimé en noires de l'espace **réel** (`TimelinePlayer` : `60.0 / BpmAt(...)`), alors que les durées
  écrites sont en espace **d'affichage** (multiplié par `TimeSigScale`). Sur un 4/4-en-triolets affiché en
  12/8 (`scale = 1.5`), écrire `Bpm` brut ferait sonner la partition 1,5 fois trop lentement ; avec le
  facteur, la noire pointée du 12/8 retombe exactement sur le temps d'origine.

### 3.7 Décomposition métrique des durées

Table des figures (recopiée du `.mscx`, en unités) :
`96 whole`, `72 half·`, `48 half`, `36 quarter·`, `24 quarter`, `18 eighth·`, `12 eighth`, `9 16th·`,
`6 16th`, `3 32nd`.

Décomposition **position-consciente** `Split(posDansLaMesure, len)` :

```
tant que len > 0 :
    off = pos % beatUnits
    si off != 0 ou len < beatUnits :          // finir le temps entamé
        take = min(len, beatUnits - off) ; greedy plus-grande-figure-d'abord sur take
    sinon :                                    // sur un temps : ne prendre que des multiples de temps
        v = plus grande figure ≤ len telle que (v % beatUnits == 0)
            et (pos % v == 0 pour une figure simple, pos % (2v/3) == 0 pour une pointée)
        émettre v
```

Ce raffinement (le `.mscx` fait un greedy pur) évite les figures illisibles du type
« noire pointée + croche + noire » là où « noire + blanche » s'impose. Il n'est pas un critère
d'acceptation en soi ; **si la mise au point coûte trop cher, le greedy pur du `.mscx` reste acceptable**
(les mesures restent complètes, seule la lisibilité souffre) — mais le coût est d'une quinzaine de lignes.

Correspondance `type` MusicXML : `whole | half | quarter | eighth | 16th | 32nd`, plus `<dot/>` pour les
pointées. (Ce sont littéralement les mêmes noms que dans `MuseScoreExporter.Vals` — pratique.)

### 3.8 Orthographe des hauteurs

Recopier `Tpc(midi, spellCenter)` et le calcul de `spellCenter` du `.mscx` :

```csharp
int fifths = dk.Flats ? -dk.Count : dk.Count;
int spellCenter = fifths + ((ts.Key != null && ts.Key.Mode == 1) ? 3 : 0);
```

puis convertir le TPC en `step`/`alter`/`octave` MusicXML :

* `alter = (tpc - 6) / 7 - 1` (division entière) → -1 / 0 / +1 ; la table `sharp[]`/`flat[]` du `.mscx`
  ne produit jamais de double altération (bornes tpc 8..24), donc `alter ∈ {-1,0,+1}`. **Vérifié.**
* `step = "FCGDAEB"[(tpc - 6) % 7]` (la ligne des quintes : tpc 13..19 = F C G D A E B naturels).
* `octave = (midi - alter) / 12 - 1` (division euclidienne) — traite correctement si♯3 (midi 60 → octave 3)
  et do♭4 (midi 59 → octave 4).

C'est bien ce mécanisme qui donne `do♯` et non `ré♭` en ré mineur (critère 10) : le `+3` du `spellCenter`
en mode mineur pousse la sensible vers les dièses.

`<accidental>` : écrit **uniquement** quand `alter` diffère de l'altération de l'armure pour cette lettre
(`dk.Acc[letterIndex]`, avec `letterIndex` 0=C..6=B). Les lecteurs qui déduisent l'altération de
`step`/`alter` (MuseScore, Finale) et ceux qui n'affichent que `<accidental>` sont ainsi tous servis, sans
jamais afficher de bécarre parasite.

### 3.9 Ordre des enfants de `<note>` — non négociable

La DTD MusicXML impose l'ordre suivant ; s'en écarter produit des avertissements à l'ouverture
(critère 4) :

```
<chord/>? , (<pitch>|<rest>) , <duration> , <tie/>* , <voice> , <type> , <dot/>* ,
<accidental>? , <notations>?
```

Attention au doublon voulu du modèle MusicXML : `<tie>` (élément **sonore**, avant `<voice>`) **et**
`<notations><tied/></notations>` (élément **graphique**, après `<accidental>`) doivent être écrits tous
les deux. `<arpeggiate/>` va dans `<notations>`, sur la première figure de l'attaque seulement.

`<voice>1</voice>` sur toutes les notes (une seule voix par portée, cf. §7 du fonctionnel).

### 3.10 `<part-list>`

```xml
<part-list>
  <score-part id="P1">
    <part-name>…</part-name>
    <score-instrument id="P1-I1"><instrument-name>…</instrument-name></score-instrument>
    <midi-instrument id="P1-I1">
      <midi-channel>c</midi-channel>
      <midi-program>Program + 1</midi-program>
    </midi-instrument>
  </score-part>
  …
</part-list>
```

* Nom : `Part.Name`, ou `Loc.T("Voix2") + id` si vide — la clé **existe déjà** dans les 7 fichiers
  (« Voix », « Voice », « Stimme »…). Le `.mscx` utilise un littéral français ; on fait mieux ici sans
  toucher à l'existant.
* `<midi-program>` est **1-based** en MusicXML alors que `TimelineTrack.Instrument` est le programme GM
  0-based ⇒ `Program + 1`, borné 1..128.
* `<midi-channel>` : `1 + (i % 16)`, en **sautant le canal 10** (percussion GM) — les pistes de batterie
  sont exclues, on n'a donc jamais besoin du 10 et l'éviter empêche un lecteur de croire à une portée de
  percussion.

---

## 4. Le gestionnaire d'interface

### 4.1 `MusicTracker\Screens\TimelineScreen.xaml` (une ligne)

Dans le `MenuItem Header="Export"` (l. 359-364), **juste après** la ligne `MuseScore (.mscx)…` :

```xml
<MenuItem Header="MusicXML (.musicxml)…" Click="btnExportMusicXml_Click"
          ToolTip="{loc:Tr 'ExportTheScoreAsMusicXML'}"/>
```

Le libellé reste un nom de format, non traduit, comme `MIDI…` et `MuseScore (.mscx)…` (§2 du fonctionnel).

### 4.2 `MusicTracker\Screens\TimelineScreen.xaml.cs` (un gestionnaire, ~25 lignes)

À placer immédiatement après `btnExportMuseScore_Click` (l. 3966). Structure calquée, aux différences
près :

```csharp
private void btnExportMusicXml_Click(object sender, RoutedEventArgs e)
{
    if (project.Tracks.Count == 0) { MessageBox.Show(Loc.T("AucunePisteAExporter")); return; }   // §5

    var src = new List<TimelineTrack>();
    foreach (var t in project.Tracks) if (scoreTracks.Contains(t)) src.Add(t);
    if (src.Count == 0) foreach (var t in project.Tracks) if (t.Type != TimelineTrackType.Drum) src.Add(t);

    var parts = new List<Engine.Timeline.MusicXmlExporter.Part>();
    foreach (var t in src)
    {
        if (t.Type == TimelineTrackType.Drum) continue;                                          // §3.1-3
        parts.Add(new Engine.Timeline.MusicXmlExporter.Part {
            Name = t.Name, Program = t.Instrument,
            Score = Engine.Score.ScoreBuilder.Build(project, t, TimelineHelper.RiffById) });
    }
    if (parts.Count == 0) { MessageBox.Show(Loc.T("AucunePisteMelodiqueAExporterCoche")); return; }

    string title = string.IsNullOrEmpty(CurrentPath)
        ? Loc.T("Partition")
        : System.IO.Path.GetFileNameWithoutExtension(CurrentPath).Replace('_', ' ');
    var sfd = new Dialogs.FileBrowserDialog {
        SaveMode = true, Owner = Window.GetWindow(this),
        Filter = "MusicXML (*.musicxml)|*.musicxml", DefaultExt = ".musicxml", FileName = title };
    if (sfd.ShowDialog() != true) return;                                                        // §3.2-5
    try
    {
        Engine.Timeline.MusicXmlExporter.Export(sfd.FileName, parts,
            project.TimeSigNum, project.TimeSigDen,
            project.TimeSigScale > 0 ? project.TimeSigScale : 1.0,
            project.MainBpm, title);
        MessageBox.Show(Loc.T("ExportMusicXMLTermine") + sfd.FileName);
    }
    catch (Exception ex) { MessageBox.Show(Loc.T("ErreurDExportMusicXML") + ex.Message); }
}
```

Points de vigilance :

* la sélection des pistes est **le copier-coller exact** de `btnExportMuseScore_Click` (règle §3.1 :
  « l'utilisateur ne doit pas avoir à apprendre deux conventions ») ;
* aucune écriture dans `project`, aucun `PushUndo`/`BeginUndo`/`FlushPending`, aucun `Render()`,
  aucun `scoreTracks.Add/Remove` ⇒ critère 16 satisfait ;
* le `try` n'englobe **que** l'écriture, pas le dialogue — annuler ne déclenche donc aucun message
  (critère 14) ;
* `ScoreBuilder.Build` appelle `TimelineProject.ResolveLoops`, qui **mute** le projet (dimensionne les
  Repeats en boucle). Deux conséquences : (a) garder la surcharge `Build(project, t, RiffById)` —
  `resolveLoops = true` — et rester **séquentiel** (ne surtout pas copier le `Parallel.For` de
  `RefreshScore`, qui n'est valable que parce qu'il appelle `ResolveLoops` une fois avant) ;
  (b) c'est déjà exactement ce que font l'export `.mscx` et le PDF, et l'opération est idempotente — donc
  le test 14 (`.sq` inchangé) reste valide, à condition de le mesurer **après** avoir déjà affiché la
  partition ou exporté une fois, comme le ferait un utilisateur.

### 4.3 `MusicTracker\MusicTracker.csproj`

Le csproj est en **ancien format** (pas de glob) : ajouter, à côté de la ligne 190 :

```xml
<Compile Include="Engine\Timeline\MusicXmlExporter.cs" />
```

Sans cette ligne, le fichier n'est pas compilé et le build échoue sur le gestionnaire.

---

## 5. Modèle de données et persistance

**Aucun nouveau champ de projet.** Vérification explicite demandée :

* `TimelineProject` (`Engine\Timeline\TimelineProject.cs`) : **inchangé**. La feature ne lit que
  `Tracks`, `Tempo`/`MainBpm`, `TimeSigNum`, `TimeSigDen`, `TimeSigScale`, `Key` — tous déjà présents et
  tous déjà recopiés par `ApplyDocument`.
* `TimelineScreen.ApplyDocument` (l. 880-914) : **rien à ajouter**. La recopie champ-par-champ actuelle
  couvre déjà `Tempo`, `Key`, `TimeSigNum`, `TimeSigDen`, `TimeSigScale`, `Arrangement`,
  `UserChordStyles`, `UserMelodicLines`, `UserDrumStyles`, `PickupBeats`, `MinBeats`, `SwingPercent`,
  `Tracks`. Comme la feature n'introduit aucun champ, il n'y a **rien à perdre** ni à l'ouverture ni à
  l'undo/redo (qui repasse par `RestoreState` → `ApplyDocument`).
  *Si l'implémentation dérivait vers un réglage persistant (p. ex. « mémoriser le dernier dossier
  d'export »), il faudrait l'ajouter là et le développeur doit s'y refuser : hors périmètre §6.*
* Le format `.sq` (`TimelineDocument`) : **inchangé**, donc rétro- et avant-compatibilité triviales
  (critères §6 du fonctionnel).
* `scoreTracks` est un `HashSet` **non persisté** de l'écran ; l'export le lit sans le modifier.

---

## 6. Localisation — 3 clés à créer dans les **7** fichiers

Fichiers : `MusicTracker\Localization\lang.{fr,en,de,it,es,nl,pt}.json`.
Les clés sont insérées en respectant l'ordre alphabétique du fichier (c'est la convention observée).

| Clé | fr | en |
|---|---|---|
| `ExportTheScoreAsMusicXML` | `Exporter la partition dans le format d'échange lu par tous les logiciels de notation (.musicxml)` | `Export the score in the exchange format read by every notation program (.musicxml)` |
| `ExportMusicXMLTermine` | `Export MusicXML terminé :\n` | `MusicXML export finished:\n` |
| `ErreurDExportMusicXML` | `Erreur d'export MusicXML : ` | `MusicXML export error: ` |

À traduire également en **de / it / es / nl / pt** (calquer le ton des voisines existantes
`ExportMuseScoreTermine`, `ErreurDExportMuseScore`, `ExportTheScoreAsANative`, déjà présentes dans les 7
fichiers).

Clés **réutilisées, à ne pas recréer** : `AucunePisteAExporter`, `AucunePisteMelodiqueAExporterCoche`,
`Partition`, `Voix2`.

⚠️ **Édition** : uniquement avec les outils `Read`/`Edit`/`Write`. Jamais
`Get-Content`/`Set-Content` (mojibake cp1252 sur les accents et sur `♫`).
⚠️ **Validation** : ne pas valider avec `ConvertFrom-Json` (insensible à la casse, échoue à tort sur le
couple préexistant `AUDIO`/`Audio`). Utiliser `JavaScriptSerializer` :
```powershell
Add-Type -AssemblyName System.Web.Extensions
$s = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$s.MaxJsonLength = 20MB
$s.DeserializeObject([IO.File]::ReadAllText($p))  # lève si le JSON est invalide
```

---

## 7. Risques, régressions, et ce qui les empêche

| Risque | Empêché par |
|---|---|
| **Régression sur `.mscx` / PDF / MIDI** (critère 17) | Aucun de ces fichiers n'est modifié. Vérifiable par diff binaire d'exports avant/après (§8). |
| **Régression sur le format `.sq`** | `TimelineProject`/`TimelineDocument`/`ApplyDocument` inchangés. Vérifiable par hash du `.sq` avant/après export. |
| **Mesure incomplète** → avertissement à l'ouverture (critère 8) | Invariant structurel : `pos` avance de `ms` à `me` sans discontinuité, silences bouche-trou compris. Vérifiable par script (somme des `<duration>` par mesure). |
| **Note perdue en fin de morceau** | `measures` calculé sur la fin réelle des notes, pas seulement `TotalBeats` (§3.3). |
| **Boucle infinie / explosion mémoire** sur une durée aberrante | Garde de la décomposition (`guard < 64`, recopiée du `.mscx`) + `len` toujours ≤ `barUnits` par construction + `measures` borné par la plus longue part. Ajouter un plafond dur `measures = min(measures, 20000)` par prudence. |
| **Ordre des enfants de `<note>` incorrect** → avertissements MuseScore | §3.9 ; à contrôler d'abord sur un fichier de 2 mesures avant d'industrialiser. |
| **DTD résolue sur le réseau** par un outil de test → faux échec / gel | Documenté §2.3 ; les scripts utilisent `XmlResolver = $null`. |
| **`<midi-program>` décalé d'un cran** (timbre faux à la relecture) | Conversion explicite `Program + 1` documentée §3.10 ; contrôlable à l'oreille (§8). |
| **Tempo faux d'un facteur 1,5** sur un morceau ternaire | Multiplication par `TimeSigScale` documentée §3.6 ; test dédié sur un morceau 12/8 issu d'un import ternaire. |
| **Armure fausse pour un instrument transpositeur** | `KeySig.Derive(key, Transpose)` — le même appel que le `.mscx`, dont le résultat est déjà validé en production. |
| **Chevauchement de notes mal résolu** (accords fusionnés, voix tenues écrasées) | Comportement **identique** au `.mscx` (§3.4) : ce n'est pas une régression, c'est la limite existante, explicitée §7 du fonctionnel. |
| **Levée : barres de mesure décalées** vs la vue partition | Connu et accepté (§5 et §7 du fonctionnel) : `PickupBeats` est un simple déphasage de grille (`TimelineProject.cs` l. 112-114) que ni le `.mscx` ni cet export n'appliquent. À noter dans le commentaire de tête du fichier. |
| **Divergence avec la vue partition** sur une piste d'accords portant des cellules mélodiques | La vue ajoute une portée « mélodique » supplémentaire (`ScoreBuilder.TrackHasMelodic`, `RefreshScore` l. 645) ; le `.mscx` non, et cet export non plus (§3.1-4 du fonctionnel : « comme dans l'export `.mscx` »). À documenter dans le `<summary>` du fichier. |
| **Fichier verrouillé / disque plein** | `try/catch` autour de l'écriture seule → `ErreurDExportMusicXML` + message système (critère 15). |
| **Export long qui semble figer l'app** | L'export est synchrone, comme le `.mscx` ; le coût est celui de `ScoreBuilder.Build` déjà payé par le PDF. Si un morceau réel dépasse ~1 s, envisager `ExportProgressDialog` — **hors scope**, à mesurer d'abord. |

---

## 8. Plan de test

### 8.1 Vérifiable automatiquement (script / build)

Matériel : un `.sq` de test à fabriquer (**T1**) — 4/4, ré mineur, 3 pistes mélodiques (dont une
transpositrice, p. ex. clarinette GM 71) + 1 piste de batterie, contenant :
une note commencée au 4e temps et durant 2 temps ; une note de 5 croches ; une piste 2 fois plus courte
qu'une autre ; un nom de piste avec `&`, `<`, un accent et un kanji.

1. **Build** : `MSBuild.exe MusicTracker\MusicTracker.csproj /t:Build /p:Configuration=Debug /v:minimal /nologo /m` → 0 erreur, 0 nouvel avertissement.
2. **7 fichiers de langue** : chacun se désérialise via `JavaScriptSerializer`, et contient les 3 clés
   `ExportTheScoreAsMusicXML`, `ExportMusicXMLTermine`, `ErreurDExportMusicXML` avec une valeur non vide et
   ≠ de la clé.
3. **XML bien formé** : charger le `.musicxml` produit avec
   `XmlReaderSettings{ DtdProcessing=Parse; XmlResolver=$null }` → pas d'exception.
4. **Complétude des mesures (critère 8)** : pour chaque `<part>` et chaque `<measure>`, somme des
   `<duration>` des `<note>` **sans** `<chord/>` == `divisions * 4 * beats / beat-type`
   (96 pour 4/4 avec `divisions=24`). Zéro écart toléré.
5. **Alignement (critère 9)** : toutes les `<part>` ont le **même** nombre de `<measure>`, et ce nombre
   est ≥ `ceil(finDeLaDernièreNote / barUnits)`.
6. **Liaisons appariées** : dans chaque `<part>`, tout `<tie type="stop">` est précédé, sur la même
   hauteur, d'un `<tie type="start">` non encore refermé ; aucun `start` ne reste ouvert en fin de part.
7. **Liaison par-dessus la barre (critère 6)** : sur T1, la dernière `<note>` de la mesure contenant le
   4e temps porte `tie type="start"` **et** `<type>quarter</type>`, et la première note de la mesure
   suivante porte `tie type="stop"` avec la même hauteur. Assertion scriptable.
8. **Durée non standard (critère 7)** : la note de 5 croches donne une chaîne de `<note>` liées dont la
   somme des `<duration>` vaut `5 * 12 = 60` unités, et **aucune** attaque intermédiaire (chaque figure
   sauf la première porte `tie type="stop"`).
9. **Orthographe (critère 10)** : sur T1 en ré mineur, toute note de hauteur `pc = 1` (do♯) sort en
   `<step>C</step><alter>1</alter>` et **jamais** `<step>D</step><alter>-1</alter>`.
10. **Chiffrage (critère 11)** : variante de T1 en 6/8 → `<beats>6</beats><beat-type>8</beat-type>` et
    somme des durées par mesure == 72.
11. **Sélection des pistes (critère 12)** : trois exports scriptés / manuels (2 pistes cochées ; aucune
    cochée ; batterie cochée) → nombre et noms de `<score-part>` attendus ; la batterie n'apparaît jamais.
12. **Transposition** : la part clarinette porte `<transpose><diatonic>-1</diatonic><chromatic>-2</chromatic></transpose>`
    et une armure décalée de 2 dièses par rapport aux autres parts.
13. **Caractères spéciaux** : `&amp;`/`&lt;` présents dans `<part-name>`, accents et kanji lisibles après
    relecture UTF-8 ; le fichier n'a **pas** de BOM.
14. **Projet intact (critère 16)** : `hash(.sq)` avant export == après export **après réenregistrement**.
15. **Non-régression (critère 17)** : exporter `.mscx`, `.mid` et le PDF (impression fichier) sur un même
    projet **avant** et **après** la feature → `.mscx` et `.mid` **binairement identiques**.
    (Contrôle fort, et bon marché : les deux exporteurs ne sont pas touchés.)
16. **Robustesse** : export sur un projet dont toutes les pistes sont vides → fichier valide, ≥ 1 mesure de
    silence par part (test 4 s'applique).

### 8.2 Exige un jugement humain

1. **Ouverture dans MuseScore (critère 4)** : le fichier s'ouvre **sans boîte d'avertissement**, sans
   message « mesure trop courte/longue » ni « fichier corrompu ». *(Un script ne peut pas constater
   l'absence de dialogue.)*
2. **Fidélité visuelle (critère 5)** : côte à côte avec la vue partition de l'application sur un morceau
   court — même nombre de portées, mêmes noms, mêmes clés, même armure, mêmes notes aux mêmes mesures.
3. **Lisibilité rythmique** : les figures produites par la décomposition métrique (§3.7) sont celles qu'un
   musicien écrirait (pas de « noire pointée + croche » là où « noire + blanche » s'impose) ; les liaisons
   sont dessinées, pas des notes ré-attaquées.
4. **Relecture sonore** : lecture dans MuseScore — le tempo est celui de l'application (à vérifier
   **spécialement** sur un morceau ternaire 12/8), et la clarinette **sonne** à la bonne hauteur alors
   qu'elle est **écrite** un ton plus haut.
5. **Signe d'arpège** : sur un morceau avec la case « Arpégiato » cochée, les accords roulés portent bien
   la vaguelette.
6. **Les 7 langues (critère 2)** : basculer la langue dans les réglages et survoler l'entrée de menu ;
   déclencher les 3 messages (succès, « aucune piste mélodique », erreur en visant un dossier protégé).
   Aucune clé brute affichée, aucun résidu français dans les 6 autres langues.
7. **Ergonomie du geste (critère 3, 14, 15)** : nom proposé correct, annulation silencieuse, message
   d'erreur lisible sur chemin non inscriptible, application toujours utilisable ensuite.
8. **Portabilité réelle** : ouvrir le même fichier dans un **second** logiciel (MuseScore + un lecteur en
   ligne type OpenSheetMusicDisplay/Soundslice) — c'est tout l'intérêt du format.

---

## 9. Estimation

| Lot | Volume |
|---|---|
| `Engine\Timeline\MusicXmlExporter.cs` | ~330 lignes neuves |
| `TimelineScreen.xaml.cs` (1 gestionnaire) | ~28 lignes |
| `TimelineScreen.xaml` (1 `MenuItem`) | 2 lignes |
| `MusicTracker.csproj` (1 `Compile Include`) | 1 ligne |
| 7 × `lang.xx.json` (3 clés) | 21 lignes |

Une session. Aucun refactoring, aucune migration, aucune dépendance nouvelle.

**Ordre de travail conseillé** : (1) l'exporteur avec un `main` mental sur un morceau de 2 mesures
monophonique ; (2) contrôle immédiat de l'ordre des balises dans MuseScore ; (3) ties intra-mesure ;
(4) ties inter-mesure ; (5) décomposition métrique ; (6) UI + localisation en dernier.
