---
name: riff-slices-setter-merges
description: "⚠️ un générateur ne doit JAMAIS rendre son riff via Riff.Slices — le setter passe par FromSlices qui SOUDE les coups adjacents de même hauteur (coups perdus, silencieusement)"
metadata: 
  node_type: memory
  type: project
  originSessionId: d7fe5618-4ecf-4040-a23c-c388d5813660
  modified: 2026-07-30T08:12:53.058Z
---

`new Riff { Slices = grille }` fait passer la grille par `RiffNotes.FromSlices`, qui convertit
tout run de slices contigus de la même hauteur en UNE note. Un générateur qui écrit un bit par
coup perd donc tous les coups adjacents sauf le premier de chaque run. Tout générateur doit
poser `Notes` + `LengthSlices` + `SlicesPerQuarter` directement.

**Why:** le symptôme est muet et asymétrique, donc très coûteux à diagnostiquer — l'export MIDI
relit la grille (`TimelineImporter.FlattenLeaf` : un coup par slice allumé) et sort JUSTE, alors
que la lecture (`TimelinePlayer.PlaceRiffNotes`, `MeltyRiffPlayer`) et la partition lisent
`Notes`. « Ça sort bien en MIDI mais ça ne s'entend pas » = cette signature. Le piège se déclenche
dès qu'un générateur choisit une résolution MINIMALE (cf. `PolyDrum.SpqFor` = LCM/gcd : une cellule
d'un seul slice ⇒ coups consécutifs adjacents). Corrigé le 2026-07-30 dans `PolyDrum.Generate`
(E(8,8) rendait 1 coup sur 8) et dans `DrumPattern.Generate` (chemin `CustomNotes`, donc « Figer »).

**How to apply:** au moindre coup manquant à l'audition, chercher `Slices =` dans le générateur
avant de suspecter un arrondi. Indice de reconnaissance : les coups qui SURVIVENT sont le premier
de chaque run du motif — ni les pairs, ni un sous-échantillonnage régulier. Et poser
`LengthSlices` explicitement, sinon le riff garde son défaut de 96 slices et l'aperçu boucle sur
une durée étrangère au module (oubli déjà vu dans `PolyChord.Generate` et `PatternGenerator.Generate`).
Voir aussi [[riff-note-list-model]] et [[new-module-three-resolvers]].
