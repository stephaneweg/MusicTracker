---
name: ui-redesign-ntrack
description: "Pro UI refonte (dark + teal, flat, Segoe UI) modeled on n-Track Studio 10 + full drum-model rework"
metadata: 
  node_type: memory
  type: project
  originSessionId: 5fb908b2-3cfc-4ea2-8f9e-423d71503dd6
---

Refonte UI + batterie démarrée 2026-07-20. Référence visuelle du user = **n-Track Studio 10** (il a envoyé des screenshots). Décision : **garder le thème sombre** mais adopter les qualités « pro » de n-Track — tout **plat** (dégradés Vista aplatis en stops identiques), accent **teal `#1FB6C3`** (bright `#3BCEDA`, deep `#178C97`) à la place du bleu `#224488`, gris neutres, **Segoe UI** partout.

Fait (compile EXIT=0, build final bloqué tant que l'app tourne / est ouverte dans VS — verrou `obj\Debug\MusicTracker.exe`) :
- `Theme/Colors.xaml` réécrit en gardant TOUS les noms de clés (les consommateurs se mettent à jour seuls). Nouveaux tokens additifs: `AccentBrush/AccentBrightBrush/AccentDeepBrush/SecondaryForeground/SurfaceHoverBackground/SubtleBorderBrush`.
- Fonts Arial/Verdana→Segoe UI ; accents bleus inline→teal (Button/Menu/TabItem/RiffGridControl) ; `HomeScreen` revu (wordmark pastille accent, 1 action primaire teal + secondaires, récents en cartes).

Couleurs par famille de percussion (façon GarageBand Beat Sequencer), demande explicite du user : [[drum-model-rework]].

Contrôles rendus + lisibles (2026-07-20) : toggle segmenté `RiffSegToggle` (AiComposeDialog) = segment actif en **teal** (avant 2 gris indistincts) ; `Theme/Slider.xaml` thumb clair `#C7CBD1` + teal au survol/drag (avant quasi invisible) ; nouveau `Theme/CheckBox.xaml` global = case **teal + coche blanche** quand cochée (mergé dans App.xaml + Page dans csproj).

Teinte de lane par **famille d'instrument** (2026-07-21) : `Controls/InstrumentColors.cs` (GM program → famille cordes/vents/cuivres/orgues/claviers/guitares/basse/synthé/autres/batterie → hue vif + teinte sombre). `MakeTrackLane` teinte le fond de lane (`LaneBgColor`, sauf piste Accords neutre) ; `MakeHeader` a une pastille couleur famille (`HeaderFamilyColor`, Accords=bleu), maj live au changement d'instrument. Prompt IA batterie enrichi : couche de percussion SECONDAIRE (tambourin/shaker/congas/claves… selon le style) pour la texture — cf [[drum-model-rework]].

Lot UX 1-8 (2026-07-21) : sélection de module → teal (`ModuleBoxControl.SelBorder`), toggle Partition (`toolToggle`) rouge→teal+arrondi, ♯/♭ en `toolToggleBlue`, boutons « supprimer » unifiés (`deleteIconButton`, survol rouge), coin MESURES harmonisé. **MUTE/SOLO ajoutés** (vraie feature) : `TimelineTrack.Mute`/`Solo` + `TimelinePlayer` (champ `Track.Mix` = 0 si mute ou (solo actif ailleurs & pas solo), multiplié au gain final ; `anySolo` calculé au flatten) — **prend effet à la lecture suivante** (pas live). Boutons M(rouge)/S(teal) dans l'en-tête de piste (styles `MuteToggle`/`SoloToggle`). États vides : placeholder récents (déjà là) + placeholder éditeur « aucun module ». Scrollbars déjà OK.

TODO proposé (pas encore fait) : thème des **dialogues** (Settings/Composer/AiCompose) + **barre de transport** timeline. Ajuster intensité teal + teintes familles si besoin (dans `Controls/DrumColors.cs`).
