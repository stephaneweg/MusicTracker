# MusicTracker — Kanban

## À faire

### Cadence éditable comme un riff (degrés) — *idée*
Le module Cadence devrait pouvoir se **sauver/éditer comme un riff** pour personnalisation manuelle. Ajouter un éditeur (à côté, comme l'éditeur de riff) mais **limité à l'ambitus nécessaire**, et affichant **les degrés** (pas les noms de notes), en **chromatique**. Permet de retoucher les accords générés un par un.

### Rendu partition « comme MuseScore » — Étape 1 ciblée
Rapprocher le rendu du **score view** + **export PDF** de celui de MuseScore (sans porter `libmscore`, infaisable). Étape 1 (la plus rentable, contenue) :
- **Charger `bravura_metadata.json`** (livré avec Bravura, licence **MIT** — pas GPL) : points d'ancrage des hampes (`stemUpSE` / `stemDownNW`), bounding boxes des glyphes, et surtout le bloc **`engravingDefaults`** (épaisseurs : `staffLineThickness`, `stemThickness`, `beamThickness`, `legerLineThickness`, `barlineThickness`, `tieMidpointThickness`…).
- **Passer toute la géométrie en unités de « spatium »** (1 interligne = 1 sp) comme MuseScore, dérivées d'un seul facteur d'échelle → proportions identiques.
- Appliquer ces ancres/épaisseurs dans `ScoreView` (têtes, hampes, ligatures, lignes de portée, barres) au lieu des constantes actuelles (`StaffGap`, épaisseurs codées en dur).
- *Suite possible (étapes ultérieures, non décidées)* : espacement horizontal optique (distance min par durée + justification), pentes de ligatures/hampes (*Behind Bars*), liaisons/ties en Bézier, crochets de triolets, sauts de système + justification verticale, mise en page PDF.
- Re-coder les **règles** (pas copier le C++ GPL verbatim) → reste propre côté licence pour une app perso.

### Choix du kit de batterie par piste (timeline)
Aujourd'hui une piste batterie joue **toujours le kit Standard**, quel que soit le kit souhaité : `TimelinePlayer.ApplyPrograms` fait `if (isDrum) continue;` → aucun program change n'est envoyé sur le canal percussion, qui reste donc sur son défaut (banque 128, patch 0). Le son sort bien, mais Room / Power / Jazz / TR-808 sont inaccessibles.

- **Blocage modèle** : `tr.Instrument = InstrumentCatalog.DrumIndex` (128) sert de simple **marqueur** « c'est une piste batterie », pas de numéro de kit → il n'y a nulle part où stocker le kit choisi.
- **À faire** : ajouter un champ kit à la piste (p.ex. `DrumKitPatch`, défaut 0 = Standard) + persistance projet + sélecteur dans l'en-tête de piste (la liste existe déjà : `InstrumentCatalog.DrumKits()` / `DrumKitNames()` / `DrumKitPatch(kitIndex)`), puis envoyer `ProcessMidiMessage(ch, 0xC0, kitPatch, 0)` sur le canal percussion dans `ApplyPrograms` (ne pas toucher la banque : `Channel.SetPatch` la laisse à 128).
- *Déjà fait côté éditeur* : `MeltyRiffPlayer` envoie le program change du kit, donc la **preview** de l'éditeur de batterie respecte le kit sélectionné.

### Générateur de cadences
Générer automatiquement des progressions d'accords (cadences) — p.ex. à partir d'une tonalité/mode, produire une suite d'accords (II-V-I, anatole, etc.) à poser dans une piste d'accords.

### Générateurs procéduraux
Générer de la musique à partir de **formules** (fractales, automates, suites mathématiques, bruit, L-systems…) : la formule pilote les notes / rythmes / dynamiques pour produire riffs ou pistes de façon procédurale.

### Application multilingue FR/EN
Rendre l'app multilingue **français / anglais** : externaliser **tous les textes et libellés** (UI XAML + messages code) dans des **fichiers ressource** (`.resx` : `Strings.resx` FR par défaut + `Strings.en.resx`), et permettre à l'utilisateur de **choisir la langue** (dans les réglages, persistée dans `AppSettings`). Appliquer la culture au démarrage ; idéalement bascule à chaud.

### Conversion Timeline ↔ Graph (+ format unifié ?) — *idée, plus tard*
Pouvoir convertir un morceau entre le mode **Séquenceur/Timeline** et le mode **Graph**.

- **Timeline → Graph** : simple. Les modules d'une piste se suivent → une chaîne de nodes (Play-riff / Accords / Drum / Repeat). Plusieurs pistes = branches parallèles depuis Start/Tempo. Les silences (`SilenceBefore`) → nodes Pause. Le volume (automation) → nodes Set-volume.
- **Graph → Timeline** : plus complexe. Quand un node se **divise en 2 branches**, il faut **ajouter une piste** et **combler par du silence** (`SilenceBefore`) avant le point de divergence.
  - *Alternative envisagée* : dans la timeline, une **piste qui démarre à un point précis en référençant la piste précédente** (au lieu de tout décaler par du silence).
- **Objectif possible** : un **format de fichier unique** partagé entre les deux modes (un seul modèle, deux vues).
- Faisabilité : oui — les deux modèles manipulent les mêmes modules (PlayRiff/Pattern/Drum/Repeat) + riffs ; le point délicat est la divergence/parallélisme du graph qui n'a pas d'équivalent direct dans la timeline linéaire par pistes.

### Drag & drop des modules — *en partie fait*
**Fait** : drag horizontal des items **de premier niveau** (feuilles + blocs Repeat) **et des modules à l'intérieur d'un Repeat** (`MoveInList` générique : règle d'overlap 2e/1re moitié + cascade, gap conservé, snap au temps). Le re-clic d'un module déjà sélectionné ne recharge plus l'éditeur.
**Reste** : faire **sortir** un module d'un Repeat / l'y faire **entrer** depuis la piste (cross-conteneur) ; **redimensionner** un module.

### Nettoyer les warnings
Variable `ex` inutilisée (NodeLink). (Aussi : `SequencerWaveProvider`/`SequencerLayerWaveProvider`/`SequencerProject` orphelins depuis la suppression de `SequencerScreen` — à supprimer si confirmé inutilisés.)

## En cours
- (rien)

## Fait
- **Compositeur auto** (`Engine/Timeline/Composer.cs`, menu Piste ▸ « 🎲 Composer un morceau (auto)… ») : génère 3 pistes cohérentes dans la tonalité/mesure courante — **Accords** (cadence `MusicTheory` + conduite des voix + articulation idiomatique), **Mélodie** (ligne diatonique : notes d'accord sur les temps forts, pas de gamme/broderies sur les faibles, intervalles petits, motif rythmique répété, résolution sur la tonique), **Batterie** (groove). Pseudo-aléatoire (seed) mais règles musicales. Changement de mesure : `TimelineImporter.ReSegment` saute les pistes contenant un module Pattern/Cadence (préserve la cadence, ne la transforme pas en riff).
- **Latence de lecture (mode Graph)** : `FlowPlayer`/`RiffPlayer` derrière le même `LookaheadBuffer` (généralisé pour tout `WaveProvider16`) + voix recyclées ; surbrillance des nœuds calée sur les samples consommés (`ActiveNodeIdsAt`). **Lecture MIDI Windows retirée** (`FlowMidiSink`, case « Synthé Windows », `MidiOutPlayer` orphelin supprimé).
- **Latence de lecture (Séquenceur)** : lecture fluide sur morceaux lourds (ex. Toccata MIDI). `LookaheadBuffer` (thread producteur + pré-amorçage avant de jouer) + `TimelinePlayer.Read` **parallélisé par piste** + voix recyclées (pas de clone JSON par note). Curseur/fin calés sur les samples consommés.
- **Poignée de départ déplaçable** (triangle bleu, pointe en bas) sur le curseur jaune : éditeurs riff/rythme (`RiffGridControl`/`RhythmGridControl`) **et** player de la Timeline (`TimelineScreen` + `TimelinePlayer.StartBeat`) — glisser pour fixer où démarre la lecture.
- **Aperçu mélodique des vignettes Play-riff** : mini piano-roll de la ligne mélodique (centré sur la plage de notes utilisée, mis en cache `Controls/RiffThumbnail`), dans le **mode graph** (nœud réduit) **et** dans les boîtes de la **Timeline** (`ModuleBoxControl`).
- **`RiffEditorWindow` restylé** comme `PatternRhythmDialog` (chrome custom : `WindowStyle=None` + bordure arrondie `CommonBackground`/`OutlineColorBrush`, barre de titre déplaçable).
- **Grille du `RiffGridControl`** au même aspect que `RhythmGridControl` : pads arrondis espacés, colonne de début de temps plus claire, do plus clairs, dièses plus foncés.
- **Éditeur de riff du Graph** : `RiffEditorWindow` utilise `RiffGridControl` (même style que la Timeline) ; **`SequencerScreen` supprimé**.
- Pause/resume + transport ▶/⏸/⏹ (graph + timeline) ; `IMusicEditor.LoadFile` unifié (OpenPath allégé).
- **Export audio de la Timeline** (WAV/MP3 via `WaveExporter.RenderProvider`, bouton « Export audio… »).
- **Player du mode Timeline** : curseur global + tempo map + mix multi-pistes (`SilenceBefore`, volume de base × automation, Repeats ×N expansés) ; bouton ▶ Lire + curseur de lecture.
- Timeline : modèle + affichage (pistes, lanes, règle de mesures, lane de tempo éditable, lane de volume avec spline).
- Éditeurs : riff piano-roll (`RiffGridControl`), accords/rythme (`RhythmGridControl`), repeat.
- Ajout/suppression de modules (report du silence), Repeat tuilé ×N.
- Import MIDI/MuseScore → Timeline ; format `.sq` (sauvegarde/ouverture).
- Réglages (SoundFont + fréquence) avec hot-reload ; corrections du parsing SoundFont.
