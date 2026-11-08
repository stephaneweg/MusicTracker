---
name: musecoco-offline-generation
description: Project direction — offline symbolic music generation via MuseCoco → MIDI → existing riff import
metadata: 
  node_type: memory
  type: project
  originSessionId: 7e3e9e06-fd16-434f-8088-c87af4ab2c9d
---

Direction décidée (2026-06-19, exploratoire) pour générer de la musique sans Claude/online : **MuseCoco**
(modèle symbolique de Microsoft **Muzic**, 1,2 Md params, attributs → MIDI). Plan v1 de l'utilisateur :
*« je lui donne les paramètres, il génère TOUT le morceau, que je découpe en track + riff par mesure ;
rien de plus »*.

**Pont = MIDI standard** → réutilise l'existant : `MidiImporter` (MIDI → `Score` : pistes/programmes +
`TimeSig`/`MeasureStartSlices`) + `AddBarRiffs` (un riff/mesure). **Aucune couche de traduction à écrire.**
Intégration .NET 4.8 = **sidecar Python local** (`params → .mid`) appelé en HTTP/stdin (tout l'écosystème
ML musique est Python ; pas de lib native .NET).

**Licence OK** : repo `microsoft/muzic` sous **MIT** (commercial permis) ; checkpoints sur Hugging Face
(offline). À vérifier : model card HF (licence des poids) ; bémol général = provenance des données
d'entraînement si diffusion commerciale.

**Caveats acceptés** : (1) contrôle approximatif/échantillonné → générer plusieurs seeds et curer, pas
du one-shot déterministe ; (2) pas d'arc d'arrangement intégré (texture homogène → la dynamique
couplet→climax vient du post-traitement) ; (3) style générique, pas la signature Hisaishi pentatonique/
modale. **Repli = hybride** : MuseCoco fournit la graine (thème/harmonie), `RecipeRenderer` applique les
params exacts — c'est le rôle de [[theme-library-seed-engine]] mais entrées générées au lieu de curées.

Alternatives symboliques notées : Anticipatory Music Transformer (Stanford, Apache-2.0, infilling/control,
install moderne), Magenta ChordConditionedMelodyRNN/Coconet (accords→mélodie, mais TF ancien). MusicGen =
écarté (sort de l'audio, pas éditable). Doc de reprise complète : `NOTES-compo-llm-handoff.md` (racine repo).
Voir aussi [[hand-composed-mscx-pieces]], [[riff-note-list-model]].
