---
name: koton-plugin-audio-harness
description: "Comment vérifier un plugin instrument Koton hors app — rendu WAV offline + analyse spectrale, et le piège du TargetExt .ksl"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: ffb82570-33cf-4597-9658-0b5f7d6b9eea
  modified: 2026-08-24T20:02:18.844Z
---

Pour valider un plugin instrument Koton sans lancer l'app : petit exe console `net10.0-windows` +
`UseWPF` qui `Prepare` / `NoteOn` / `Render` en boucle et écrit un WAV, puis analyse en Python
(FFT maison, pas de numpy installé — `pypdf` et `fitz` le sont).

⚠️ **Ne PAS utiliser `<ProjectReference>` vers un projet de plugin** : les .csproj plugins ont
`<TargetExt>.ksl</TargetExt>`, donc le deps.json liste un asset runtime `X.ksl` que le host .NET
refuse → `Failed to create CoreCLR, HRESULT: 0x80070057` avant tout code managé. Contournement :
copier le `.ksl` en `.dll` dans un dossier `libs/` et le référencer par `<Reference><HintPath>`.

Ce qu'il faut mesurer, pas seulement écouter :
- enveloppe spectrale par harmonique (lissée sur ±2-3 harmoniques, sinon la structure paire/impaire
  du peigne masque tout) — c'est ce qui a révélé que l'excitateur de [[didgeridoo-acoustics]]
  s'écroulait à −8 dB/harmonique ;
- balayage de chaque paramètre à ses deux bornes × plusieurs sample rates × toute l'étendue du
  clavier, en cherchant NaN/Inf et pic > 1.0 ;
- coût CPU (viser < 1 % d'un cœur par voix).

⚠️ **Toujours couper vibrato ET bruit de souffle avant de mesurer un spectre.** Une mesure par
corrélation à la fréquence exacte de chaque harmonique voit un partiel modulé en fréquence comme
ATTÉNUÉ (son énergie part dans les bandes latérales), et l'erreur croît avec le rang puisque la
déviation vaut h × depth. Sur le duduk (vibrato ±38 cents), ça faisait lire −29 dB sur H3 là où le
modèle en produisait −16, et un rapport impair/pair qui changeait de signe d'une note à l'autre —
j'ai failli réécrire un modèle qui allait bien. Passer les paramètres en ligne de commande
(`--set=vibrato_depth=0 --set=breath=0`) plutôt que de recompiler.

Pour peser le SOUFFLE seul : rendre deux fois la même note, `breath=0` puis `breath=N`, et
soustraire — la partie tonale est déterministe donc elle s'annule exactement, il ne reste que le
bruit, qu'on pèse ensuite par bandes d'octave.

⚠️ Piège maison trouvé comme ça : l'approximation rationnelle de tanh `x(27+x²)/(27+9x²)` **diverge**
au-delà de |x|>3 (elle tend vers x/9) — un "limiteur doux" écrit comme ça amplifie au lieu de
limiter. Écrêter à ±3 avant.

Complément audio du harnais UI [[wpf-dialog-inprocess-harness]] (même logique : rendre hors app,
`RenderTargetBitmap` sur l'éditeur pour vérifier la mise en page des sliders).
