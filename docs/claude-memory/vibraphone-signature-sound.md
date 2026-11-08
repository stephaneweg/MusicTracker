---
name: vibraphone-signature-sound
description: le vibraphone du plugin Mallets est un son signature apprecie par le user et son epouse (evocation reveuse) — a preserver lors de toute modif Mallets
metadata: 
  node_type: memory
  type: feedback
  originSessionId: da5caa5c-1b54-4450-91c2-c367312ffcde
  modified: 2026-08-20T23:03:05.548Z
---

Le preset vibraphone du plugin [[koton-plugin-mallets]] a un rendu que le user
et son epouse apprecient particulierement — evocation "reveuse", "suspendue
dans le temps". La qualite vient de :

- Synthese modale additive (sinusoides amorties avec ratios inharmoniques
  proches du vrai vibra)
- Decay exponentiel long des modes graves
- LFO d'amplitude qui simule les disques rotatifs des tubes resonateurs
  (tremolo chatoyant caracteristique)

**Why:** Feedback validant, pas une correction — le user a exprime sans
demander de changement, donc l'equilibre actuel est le bon.

**How to apply:** Toute modif future du plugin Mallets doit preserver cet
equilibre sur le preset vibraphone (ratios de partiels, taux de decay,
profondeur/vitesse du LFO d'amplitude). Ne pas casser en essayant
d'ameliorer autre chose. Si un preset autre a besoin d'un fix DSP
partage, valider aussi le vibra avant/apres.
