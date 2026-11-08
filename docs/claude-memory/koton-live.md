---
name: koton-live
description: "Koton Live = 2e exécutable (rack temps réel micro/MIDI → instrument → effets), fenêtre partagée avec le séquenceur ; contraintes d'architecture à respecter"
metadata: 
  node_type: memory
  type: project
  originSessionId: 68030524-5efc-41a1-b081-249004bc5185
  modified: 2026-08-23T19:45:41.633Z
---

**Koton Live** (demandé 2026-08-23) : programme autonome `KotonLive.exe` en plus de `KotonStudio.exe`.
Deux modes — « Effets » (entrée → chaîne d'inserts → carte son) et « Instrument » (micro à hauteur
détectée ou clavier MIDI → SoundFont/VSTi/plugin Koton → mêmes inserts). Backends WASAPI (défaut) et
ASIO full-duplex.

**Why:** trois contraintes d'architecture non évidentes, faciles à casser sans le savoir :

1. **La fenêtre vit dans l'assembly du séquenceur** (`MusicTracker/Live/LiveWindow.xaml`), pas dans
   KotonLive. C'est ce qui évite une référence circulaire : Koton Studio ouvre la même fenêtre depuis
   son bouton 🎤, KotonLive.exe n'est qu'une coque de ~20 lignes.
2. **`MusicTracker/Theme/AppResources.xaml`** porte TOUTES les ressources d'application (ex-contenu de
   `App.xaml`). Une Window résout ses `StaticResource` dans l'Application QUI L'HÉBERGE : une ressource
   ajoutée seulement dans `App.xaml` manquerait à KotonLive et ferait planter le XAML au chargement.
   Ses `Source` sont en pack URI absolu (`/KotonStudio;component/...`) pour la même raison.
3. **`AppBootstrap.Initialize()`** (garde-crash, langue, scan des plugins `.ksl`) est appelé par les
   DEUX exécutables. Toute init ajoutée à `App.OnStartup` seule manquerait au live.

**How to apply:** nouvelle ressource d'app → dans `AppResources.xaml`, jamais dans `App.xaml`.
Nouvelle init globale → dans `AppBootstrap`, pas dans `App.OnStartup`. KotonLive.exe doit rester dans le
dossier d'installation du séquenceur (cible MSBuild `CopyNextToKotonStudio`) : `AppPaths` y résout
`plugins\`, `SoundFont\` et `Localization\`.

**Vérifié à la construction** (harnais de fumée, hôte étranger) : XAML OK, WASAPI in+out 48 kHz sans
décrochage. **Non testé** : le chemin ASIO (pilote « Sonicake USB Audio Device » présent mais matériel
débranché) — c'est le premier point à valider à l'oreille. Réglages persistés dans
`%AppData%\MusicTracker\live.json` (séparé de settings.json). Voir [[meltysynth-audio-engine]],
[[violin-audio-capture]].
