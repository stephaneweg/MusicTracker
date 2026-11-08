---
name: plugin-param-automation
description: "Automation des KotonParameter par lanes (piste + master) — courbe normalisée 0..1, écriture via SetFromAutomation"
metadata: 
  node_type: memory
  type: project
  originSessionId: da5caa5c-1b54-4450-91c2-c367312ffcde
  modified: 2026-08-24T07:51:13.282Z
---

Automation des paramètres de plugin Koton par courbe sur la timeline (fait 2026-08-24). Trois sources
uniformes : instrument Koton d'une piste, inserts Koton d'une piste, inserts Koton du bus master
(rangée « Master » en haut de la timeline, visible dès qu'un effet Koton est posé sur le master).

Deux règles NON ÉVIDENTES à respecter si on touche à ce code :

1. **La courbe est stockée NORMALISÉE 0..1**, jamais en unité du paramètre. Elle est dépliée au rendu sur
   les bornes VIVANTES du `KotonParameter` (`Min + f*(Max-Min)`). Ça permet à l'éditeur graphique unipolaire
   de servir pour tous les paramètres, et à une lane de survivre à un plugin dont une version élargit la plage.
   Avant le premier point, la lane tient `DefaultNorm` = la valeur qu'avait le paramètre à la création de la lane.

2. **Écrire via `KotonParameter.SetFromAutomation(v)`, JAMAIS `Value`.** `Value` lève `Changed`, et l'automation
   écrit ~90×/s depuis le thread audio : les éditeurs qui rafraîchissent dans le handler noieraient le
   dispatcher WPF, et ceux qui touchent l'UI sans `Dispatcher.BeginInvoke` (WaveMorph, FmSynth) lèveraient une
   exception de thread DANS le moteur audio. Conséquence assumée : le curseur d'un éditeur ouvert ne suit pas
   l'automation.

Autres points de câblage : les valeurs d'inserts doivent être posées **APRÈS** `fx[i].Load(d.Params)` (qui
réapplique à chaque buffer les valeurs figées du .sq et écraserait l'automation) ; `TimelinePlayer.Stop()` et
`RaiseEnded()` remettent chaque paramètre à sa valeur d'avant lecture (sinon la valeur de mi-sweep partirait
dans le .sq à la sauvegarde) ; `PluginAutomation.FixupAll` recale les `InsertIndex` quand la chaîne d'inserts
a bougé. Fichiers : `Engine/Timeline/Effects/PluginAutomation.cs`, `PluginAutomationLane` dans
`TimelineProject.cs`, résolution/application dans `TimelinePlayer.cs`, UI dans `AutomationLaneControl` +
`TimelineScreen`. Voir [[koton-plugin-audio-harness]].
