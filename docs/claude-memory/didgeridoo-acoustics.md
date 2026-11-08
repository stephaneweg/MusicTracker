---
name: didgeridoo-acoustics
description: "Les 3 faits acoustiques qui font sonner un didgeridoo, et où sont les papiers sources"
metadata: 
  node_type: memory
  type: reference
  originSessionId: ffb82570-33cf-4597-9658-0b5f7d6b9eea
  modified: 2026-08-24T00:39:45.270Z
---

Sources : `C:\Users\swe\Downloads\Quilcom-SIM-DIDGE_x64.zip` — le VST freeware Quilcom SIM-DIDGE livre
son guide utilisateur complet **et** les papiers d'acoustique dans `Background info/` (Tarnopolsky,
Fletcher, Hollenberg, Lange, Smith & Wolfe JASA 119(2) 2006 + Nature 436:39 2005 ; Ayers & Horner
ICMC ; Hindle & Posnett NIME 2017). Le DLL lui-même est packé UPX autour d'un patch SynthEdit :
inutile de le désassembler, la doc dit tout.

Les trois points qui décident du réalisme (implémentés dans `KotonPluginDidgeridoo` v2) :

1. **Les lèvres se ferment ~la moitié du cycle.** C'est la fermeture qui crée les harmoniques hautes
   (composantes de l'aire d'ouverture en ~n⁻²). Une sinusoïde écrêtée par un seuil marche ; la
   formule fermée de Fletcher `(1-a)²/(1+a·sin)²` NON — mesurée à −8 dB/harmonique, plus rien
   au-dessus de 700 Hz, parce qu'elle ne ferme jamais la valve.
2. **Tube = cylindre fermé côté lèvres** → résonances sur les harmoniques IMPAIRES (f, 3f, 5f…),
   légèrement étirées. Peigne récursif à contre-réaction NÉGATIVE + passe-tout dans la boucle.
3. **Le conduit vocal creuse des CREUX, il ne crée pas de bosses.** Les maxima d'impédance du conduit
   (~1,5 / 2,1 / 2,8 kHz langue haute) donnent des MINIMA dans le son rayonné ; le formant entendu
   est ce qui subsiste entre deux creux. Modéliser en passe-bande sur la source (ce que faisait la
   v1) donne une voyelle de synthé, pas un didge. Les creux doivent rester ÉTROITS (Q ≈ 5), sinon
   langue haute ils fusionnent en un seul trou et la voyelle disparaît.

Le didge est un instrument à hauteur FIXE : tout l'intérêt musical est dans l'articulation
(coup de langue, coup de diaphragme, toot, respiration circulaire) et la modulation continue de la
bouche. Un bourdon statique + un wobble = ennuyeux, c'était le vrai défaut de la v1.

Vérifié par mesure offline, cf. [[koton-plugin-audio-harness]].
