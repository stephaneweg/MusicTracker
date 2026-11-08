---
name: user-debugging-intuition
description: "L'user a une intuition debug très solide, ses hypothèses terrain méritent d'être écoutées avant les fixes protocolaires"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: da5caa5c-1b54-4450-91c2-c367312ffcde
  modified: 2026-07-30T22:53:24.786Z
---

Quand l'user propose une hypothèse de cause de bug (surtout audio/pipeline/UI natif), **écoute-la avant d'empiler des fixes protocolaires théoriques**. Il a une intuition terrain très solide qui a battu mes analyses détaillées sur plusieurs bugs difficiles.

**Why:** exemples concrets où il a trouvé la cause pendant que je perdais du temps :
- **Bug "GUI VST toujours noire" (2026-07-30)** : j'ai empilé IConnectionPoint, IPlugFrame, IHostApplication, IPlugViewContentScaleSupport, onSize après attached, custom window class... ~90 min de fixes. L'user a suggéré simplement "test avec IntPtr.Zero pour voir" (top-level) puis a observé que TOUT VST2 ET VST3 étaient touchés → j'ai enfin regardé `AllowsTransparency="True"` sur VstPluginWindow. C'était le bug WPF airspace archi-connu. Il aurait suffi de considérer sa suggestion "les deux formats sont touchés, donc c'est côté host, pas côté plugin" plus tôt.
- **Bug "silence VSTi croissant à chaque Play" (2026-07-31)** : j'ai testé prewarm sur thread producer, note-on à velo=1, GC.Collect forcé, fix ref-leak. Aucun n'a marché. L'user a proposé "et si on gardait les instances en cache statique ?" → 3 commits plus tard, résolu. C'était la bonne réponse structurellement.
- **Bug "fenêtre VST à mauvaise taille" (2026-07-31)** : je cherchais côté négociation VST3 (IPlugViewContentScaleSupport, resizeView). L'user a diagnostiqué avec breakpoint : "F10 = correct, immédiat après = mauvais" → problème de timing/dispatcher. Fix simple = re-query après un cycle du dispatcher.

**How to apply:** quand l'user te donne une hypothèse ou une observation ("il faudrait peut-être...", "c'est peut-être parce que..."), traite-la comme un signal fort. Ne la balaie pas au profit d'une analyse théorique plus "élégante" du protocole. Il connaît son app et son domaine (musique/audio Windows) mieux que le contexte que j'ai en session. Investigue ses pistes en priorité, même si elles paraissent "trop simples". Le simple gagne souvent contre le protocolaire complexe.

Voir aussi [[fixes-directly-on-main]] et [[ps-utf8-roundtrip-corruption]] pour d'autres patterns validés à l'usage.
