---
name: home-redesign-templates
description: Home screen concept redesign — crossfade banner backdrop + code-gen project templates + (planned) resources widget & multi API keys
metadata: 
  node_type: memory
  type: project
  originSessionId: 5fb908b2-3cfc-4ea2-8f9e-423d71503dd6
---

Refonte écran d'accueil (2026-07-21) suivant une image concept fournie par l'utilisateur ([[ui-redesign-ntrack]]).

**Fait :**
- **Fond de bannière crossfade** (HomeScreen.xaml `banner` Border + code-behind) : à GAUCHE un piano-roll = grille de carrés gris (`empty` ~0x14 alpha) + rectangles teal spanning 1–3 cases (longueur des notes, contour sin) ; à DROITE une grille batterie colorée par famille (`DrumFamily` palette kick/snare/hat/tom/perc/crash, motif `DrumLit`). Crossfade horizontal via `OpacityMask` LinearGradient sur CHAQUE canvas + fondu vertical global via un `Grid` wrapper avec OpacityMask verticale (les masques se composent en descendant l'arbre). Dessiné en code sur `bannerRiff.SizeChanged`. Bouton doublon "Paramètres" du bas retiré (le gear en haut suffit).
- **Templates code-gen** : `Engine/Timeline/ProjectTemplates.cs` (`All` infos + `Create(id)` → TimelineProject). `TimelineTrack.Instrument` = programme GM 0-127. Batterie = `TimelineTrackType.Drum`. `TimelineScreen.LoadTemplate(proj, measures, info)` mute `project` en place, `CurrentPath=null` (Save As), `project.MinBeats = measures*RulerBeatsPerBar()` (nouveau champ; TotalBeats plancher). Cartes code dans `templatesPanel` (WrapPanel), style `TemplateCard`. `HomeScreen.TemplateRequested(id)` → `MainWindow.OpenTemplate` (dialogue `TemplateMeasuresDialog` presets 16/32/48/64/96/128 → OpenEditor null + Defer LoadTemplate). **9 modèles** : piano/band/jazz/hiphop/cinematic/orchestral/chiptune/exotic/ambient. Info REFACTORISÉ : porte `Tracks` (`TrackSpec[]` name/gm/type/**Role**), `Bpm/Num/Den`, `Chords`+`Articulation`+`Sevenths`, `DrumStyle`+`DrumDensity` (index DrumPattern.StyleNames/DensityNames), `Melody`(lead)+`Contour`+`Anchor`. `Create(id)` construit depuis `Info.Tracks` (plus de switch). `Role` enum {Lead,Bass,Pad,Arp,Counter} → `RoleMotif(role,info)` = (durées, registerShift, contour, anchor). Génération (TimelineScreen) : `GenerateTemplateChords` (1 accord/mesure via AddAiChord, qualités diatoniques `Quality(deg,seventh)`) + `GenerateTemplateDrums` (DrumPatternModule Style/BeatsPerBar/Repeats) + `GenerateTemplateMelody` (UNE ligne par piste instrument, motif par rôle, registre). **Riffs en segments de 4 mesures** (`TemplateChunkBars=4`, modules bout à bout) pour mélodie ET batterie. Batterie sur TOUS les templates. Chord track reste audible (option future: silencieux si trop dense).
- **Boutons bannière responsive** = `Controls/ReflowButtons.cs` (Panel custom : reflow 1/2/3 lignes selon largeur). **Equalizer** bas de page = `eqCanvas` (span 2 col, barres teal faibles) rétabli.

**TODO (plan "enchaîne les trois", ordre initial clés API→templates→ressources mais banner+templates faits d'abord) :**
- Widget **Ressources** = Astuce du jour (carousel) + Nouveautés (changelog), sous les récents/à droite.
- **Clés API multiples** : dialogue Paramètres `[Fournisseur][Nom][Clé]` (liste ajout/suppr) ; dropdown "Clé" filtré par fournisseur (par nom) dans AiComposeDialog + AiElementDialog ; migration clé unique actuelle → entrée "Défaut". AppSettings a aujourd'hui UNE clé par fournisseur (GeminiApiKey…) + AiProviders.GetKey/SetKey ; à étendre en liste `ApiKeyEntry`.

**BUG batterie corrigé (2026-07-22)** : `Drum()` mettait Instrument=0 (piano) → percus jouées au piano = « coupé ». Fix = `InstrumentCatalog.DrumIndex` (=128, comme les pistes batterie normales via AddDrumTrack). Ne PAS toucher au playback/note-length (fausse piste).

**CODE-GEN SUPPRIMÉ (2026-07-22)** : `ProjectTemplates.cs` supprimé + LoadTemplate/GenerateTemplateChords/Drums/Melody/RoleForTrack/TemplateChunkBars (TimelineScreen) + OpenTemplate/TemplateRequested (MainWindow/HomeScreen) retirés. Les modèles viennent UNIQUEMENT de Data/templates/*.json (TemplateSpec), les rythmes batterie UNIQUEMENT du DrumCatalog. Ci-dessous = historique.

**Templates IA section-based (2026-07-22)** : format JSON riche produit par le prompt « génère un template » (voir conversation). `Engine/Timeline/TemplateSpec.cs` = classes (name/icon/tags/bpm/meter/instruments[{name,gm,role}]/sections{intro,theme,devA,devB,outro}) + `TemplateLibrary` (charge `Data/templates/*.json`, bundlé) + mappings ContourIndex/AnchorIndex/RegisterForRole. Section = {bars, drum (nom style DrumPattern), chords[{degree,quality}], parts[{instrument,durations,contour,anchor}]}. `TimelineScreen.LoadTemplateSpec(spec,measures)` : forme = intro+theme+[devA/devB alternés]+outro développée à N mes. (`BuildSpecForm`), Accords AUDIBLES (AddAiChord style 0, 1/mesure), batterie 1 module/section (Style via `DrumStyleIndex(nom)`), UNE ligne mélodique/instrument (part→MelodicLineModule, silences pour alignement). Cartes accueil : ProjectTemplates + TemplateLibrary fusionnés (`AddTemplateCard`), event `TemplateSpecRequested` → `MainWindow.OpenTemplateSpec` (dialogue mesures → LoadTemplateSpec). 1er template : ghibli_pastoral.json. Déposer un JSON dans Data/templates/ = nouvelle carte. TODO éventuel : "Coller un template…".

**Widget Ressources FAIT (2026-07-22)** : section RESSOURCES sous les templates (`resourcesHost`, `BuildResources`) — carte « 💡 Astuce du jour » (rotation via `DayOfYear % Tips.Length` + boutons ‹ ›) + carte « 🆕 Nouveautés » (changelog). Contenu = `Tips[]`/`News[]` en dur dans HomeScreen.xaml.cs. Plan initial (banner + templates + ressources + clés API) COMPLET.

Pas encore ear/eye-testé en live (banner validé ; templates + spec codés main non testés).
