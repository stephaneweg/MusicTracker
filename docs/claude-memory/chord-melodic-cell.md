---
name: chord-melodic-cell
description: "Optional MELODIC CELL attached to a chord object (2nd voice/staff, diatonic-degree grid, transposes per chord)"
metadata: 
  node_type: memory
  type: project
  originSessionId: f952e927-6dc4-4278-9d24-2ab81936122d
---

**Cellule mélodique = 2ᵉ voix attachée à un accord** (`PatternGeneratorModule`), pas un track séparé. Décidée + spec par l'utilisateur (2026-07-08). Éditée dans l'éditeur d'accord ([[generators-and-cadence]] `ChordEditorControl` MVVM), onglet « Cellule mélodique ».

**Spec validée** : grille = **14 rangées = degrés diatoniques 1-7 × 2 octaves** (toutes notes de gamme, accord ET hors-accord, indép. de couleur/suspension), **polyphonique**. **Octave** de la mélodie **indépendante** de l'accord. **Ancrage** (option, les deux testables) : `MelodicAnchor` 0 = degré 1 = **tonique** (fondamentale de l'accord) / 1 = **selon le renversement** (basse = fond./3ce/5te/7e… via QualityIntervals[inversion]). Transposition **modale** (relative à la racine de l'accord, dans la gamme du mode). Options open-voicing + voice-leading demandées (⏳ PAS encore rendues).

**Modèle** (`Engine/Flow/FlowModule.cs`) : `MelodicNotes`/`MelodicSlices`/`MelodicSlicesPerQuarter`, `MelodicOctave=5`, `MelodicAnchor`, `MelodicOpenVoicing`, `MelodicVoiceLead`, `HasMelodic`, `SetMelodicNotes`.

**Générateur** : `PatternGenerator.GenerateMelodic(m, key)` → un `Riff` (Note = ligne 0..13 mappée à la hauteur via `MelodicPitch` : marche la gamme du mode depuis l'ancre). `MelodicRowCount=14`. Vérifié headless `test_melodic.ps1` : C maj ancre tonique = gamme Do sur 2 oct ; ancre renversement 1 = depuis Mi ; V(Sol) = gamme Do depuis Sol (modal, Fa naturel). ⚠ `GenerateMelodic` a besoin de la CLÉ (Engine.Score usage ajouté à PatternGenerator).

**Intégration** :
- ✅ Éditeur : `ChordEditorControl.xaml` TabControl (Accompagnement | Cellule mélodique) + `RefreshMelodicGrid` (2ᵉ `RhythmGridControl`, 14 labels de degrés) ; VM `MelodicOctave`/`MelodicAnchorIndex`/`MelodicAnchorNames`, event `MelodicRefreshNeeded` (rebuild quand l'accord change).
- ✅ **Lecture** : `TimelinePlayer.PlaceLeaf` place le riff d'accord PUIS, si `HasMelodic`, `GenerateMelodic(pg, melodyKey)` (champ = project.Key) sur la MÊME piste (2 voix, même instrument, même temps). Refactor `PlaceRiffNotes`.
- ✅ **« Appliquer le motif »** : `PropagateMelodic(name, src)` copie la cellule (grille+octave+ancrage+open/VL) à tous les accords du même `UserStyleName` (chacun transpose sur sa racine). Appelé dans `ApplyMotifToSection`.
- ✅ **Thumbnail** : `RiffThumbnail.GetCombined(accord, Chords, mélodie, Melodic)` superpose la mélodie en **bleu** (couleur `Melodic=#4D9BFF`), même échelle/plage. Câblé au box PatternGenerator si `HasMelodic`.
- ✅ **Score** (2026-07-08) : portée mélodique SUPPLÉMENTAIRE au-dessus de la portée d'accords. `ScoreBuilder.Build(..., melodic:true)` (param propagé PlaceItem→PlaceLeaf→`RiffForModule(m,resolve,melodic,key)` : en mode melodic, seuls les `PatternGeneratorModule.HasMelodic` → `GenerateMelodic`) ; `ScoreBuilder.TrackHasMelodic(track)` ; `RefreshScore` insère `[mélodie, accords]` par track dans la liste (le curseur couvre toutes les portées → « voit » les 2 tracks).
- ❌ **open-voicing de la cellule** : implémenté puis RETIRÉ (utilisateur : « n'apporte pas de plus-value »). Champ `MelodicOpenVoicing` conservé mais inutilisé.
- ⏳ **voice-leading de la cellule** : PAS fait — c'est CHAÎNE-niveau (comme `RevoiceChordChain`) : passe sur la suite d'accords ajustant l'OCTAVE de chaque cellule pour mouvement mini, appliquée AVANT le flatten (lecture+score+thumbnail). `MelodicVoiceLead` field prêt, pas câblé.

**VALIDÉ à l'oreille (2026-07-08)** : l'utilisateur construit une articulation d'accord sur 2 mesures + sa cellule mélodique → mélodie **cohérente sur 8 mesures (4 accords)**. L'ancrage « selon le renversement » donne exactement le comportement attendu. Feature jugée « fonctionne vraiment bien ».
