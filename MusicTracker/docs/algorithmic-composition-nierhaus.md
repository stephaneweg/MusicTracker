# Composition algorithmique (Nierhaus) — digest orienté implémentation

Source : Gerhard Nierhaus, *Algorithmic Composition — Paradigms of Automated Music Generation*, Springer, 2009 (293 p.).
But de ce fichier : servir de **spec** au moteur `Engine/Compose/ProceduralComposer.cs` (génération 100 % procédurale, sans modèle appris) pour les fonctions **Insérer → Thème / Variation**. Une fiche par technique : principe · pseudo-algo · paramètres · mapping hauteur/durée · pièges · branchement.

## Conventions du projet (rappel)
- `RiffNote.Note` : **0 = C0 = MIDI 12** ⇒ `Note = MIDI − 12`. Registre thème par défaut MIDI 60–84 → `Note` 48–72.
- Résolution : **24 slices/noire** (`SlicesPerQuarter = 24`). `barSlices = beatsParMesure × 24` (aligné sur `RulerBeatsPerBar()`).
- On travaille en **degrés de gamme** puis on place en registre au plus près (choix d'octave) ; le sériel travaille en **pitch-classes chromatiques** (atonal).
- Chaque technique produit une **mélodie** (List<RiffNote>) et, hors structure sans accords, un **accompagnement verticalisé** (blocs).

## ⚠ Rythme = couche PARTAGÉE, sensible à la mesure (binaire / ternaire)
**Le temps fait toujours 24 slices.** La division dépend de la mesure (ternaire ⇔ `TimeSigDen == 8`) :
- **Binaire** : le temps se divise par 2/4 → durées **{6, 12}** dans le temps. Jamais /3 ni /6.
- **Ternaire** (composé x/8) : le temps se divise par 3/6 → durées **{4, 8}** dans le temps. Jamais /2 ni /4.
- **Regroupements** whole-beat : **24 · 48 · 72 (si ≥3 temps/mesure) · 96 (si ≥4 temps)**, alignés sur les temps, **jamais à cheval sur une barre**.

`ProceduralComposer.GenerateRhythm(total, barSlices, ternary, rng)` fabrique **une** liste de durées beat-alignées, équilibrées (les noires/temps entiers dominent), **réutilisée par TOUS les algorithmes**. Chaque technique ne fournit plus que le **flux de HAUTEURS** (une par slot rythmique, `REST` = silence) ; `Assemble` zippe hauteurs↔rythme. ⇒ les remarques « rythme » par technique ci-dessous (croche fixe, durées sérielles, variété rythmique génétique…) sont **superseded** : le rythme est toujours cette couche correcte. La série de DURÉES du sérialisme intégral et les features rythmiques Towsey deviennent donc informatives seulement.

## Panorama du livre (chapitres → nos techniques)
| Ch. | Sujet | Usage ici |
|----|-------|-----------|
| 3 | Markov / HMM | contexte seulement (on abandonne l'appris pour cette fonction) |
| 4 | Grammaires génératives (Chomsky, Bol Processor, jazz) | parenté des L-systèmes |
| 6 | Chaos & auto-similarité | **L-systèmes (6.4/6.6)**, **bruit 1/f (6.5.1)**, cartes chaotiques (6.5.2), Thue-Morse (auto-sim.) |
| 7 | Algorithmes génétiques | **Génétique** : opérateurs, **fitness Towsey (7.4.2)**, rythme (7.4.4) |
| 8 | Automates cellulaires | **CA 1-D (8.2.1)**, en composition (8.3), polyrythmie (8.3.1) |
| — | Sérialisme | pas un paradigme dédié du livre → **implémenté depuis la théorie 12 sons** |

---

## 1. Sérialisme (intégral)  → `ProcTechnique.Serial`
**Principe** (théorie dodécaphonique / sérialisme intégral Boulez-Messiaen ; le livre l'évoque en ch.2 historique, p. ex. Schönberg cité comme fournisseur de matériau pour Xenakis).
- Une **série** = permutation des 12 pitch-classes. 4 opérations : **P** (prime), **I** (inversion, intervalles opposés mod 12), **R** (rétrograde), **RI** (rétrograde de l'inversion), chacune × **transposition Tn** (n demi-tons).
- Sérialisme **intégral** : séries séparées pour hauteurs, **durées** (gamme chromatique de durées : 1..12 × unité, façon Messiaen *Mode de valeurs*) et **nuances** (⚠ non appliquées ici, pas de vélocité par note).

**Pseudo-algo**
```
rng = Random(seed)
pitchRow  = shuffle(0..11)
durRow    = shuffle([u, 2u, 3u, ... 12u])   // u = unité (ex. 6 slices = double-croche)
forms = [P, I, R, RI]; k = 0
pos = 0
while pos < bars*barSlices:
    form = forms[k % 4] transposé de Tn(k)   // n tourne (ex. +5*k mod 12)
    for pc in appliquer(form, pitchRow):
        dur = durRow[i % 12] quantifié à la grille
        pitch = placerAuPlusPres(pc, precedent, lo, hi)   // octave la plus proche
        emit(Note=pitch-12, pos, dur); pos += dur
    k++
```
**Mapping** : pitch = pc chromatique placé dans [lo,hi] au plus près de la note précédente. Durée = valeur de `durRow` bouclée, bornée pour ne pas dépasser la mesure.
**Verticalisation (accompagnement)** : trichords/tétrachords consécutifs de `pitchRow` empilés (une superposition par mesure) → `ChordAccomp`.
**Pièges** : atonal → ne PAS snapper à une gamme ni forcer des notes d'accord ; garder les 12 pcs pour que ça « sonne sériel ». Répartir les durées pour que la ligne respire (éviter 12 double-croches plates → utiliser toute la gamme de durées).

---

## 2. Automate cellulaire  → `ProcTechnique.CellularAutomaton`
**Principe** (ch.8 ; Wolfram *A New Kind of Science* ; applications Beyls, Millen, Miranda/CAMUS).
- **CA élémentaire 1-D, K2R1** : cellules binaires, voisinage = {gauche, centre, droite} → 2³ = 8 configurations → **règle = octet 0..255** (bit i = état suivant de la config i). Grille **toroïdale** (bords recollés).
- Classes de Wolfram : 1 (homogène), 2 (périodique), 3 (chaotique, ex. **rule 30**), 4 (complexe/auto-similaire, ex. **rule 110**). **Rule 90 = triangle de Sierpiński** (auto-similaire).
- λ de Langton ≈ **30–40 %** de cellules actives = zone la plus « productive ».

**Mapping Beyls (le plus musical, [3])**
- Grille de largeur W (≈ |gamme| ou 12 si chromatique) ; init = 1 cellule active au centre.
- Chaque **génération = un pas de temps**. Pour la mélodie mono : indice de la (ou d'une) cellule active → **degré de gamme** ; **même hauteur que la génération précédente ⇒ note tenue** (allonge la note au lieu d'en rejouer une) ; **voisinage vide ⇒ silence**.
- Verticalisation : toutes les colonnes actives d'une génération empilées = un accord.

**Pseudo-algo**
```
row = grille[W] init centre=1
for gen in 0..N:
    pitch = degré(colonneActiveChoisie(row))   // ex. barycentre, ou 1re active
    if pitch == last: prolonger la note courante (tenue)
    elif aucune active: silence (avancer d'un pas)
    else: emit note (degré→registre), last=pitch
    row = step(row, rule)   // règle élémentaire toroïdale
```
**Paramètres** : `rule` (défaut 90 ; exposer 30/90/110), `W`, cadence (1 génération = 1 croche par défaut).
**Pièges** : sur grille finie le CA devient **périodique** → varier init/seed. Éviter que rule=0/255 (états fixes → silence/plein).

---

## 3. L-système  → `ProcTechnique.LSystem`
**Principe** (ch.6.4/6.6 ; Lindenmayer ; DuBois pour le mapping musical).
- Système de réécriture **parallèle** (v, ω, P) : alphabet, axiome, règles `α → χ` appliquées **simultanément** à chaque itération. Formes : D0L (déterministe, sans contexte), stochastique (règles pondérées), **paramétrique**, contextuel (IL).
- **Mapping DuBois (relatif)** — préférer au turtle-graphique : chaque symbole = un événement musical *relatif au précédent* (« transpose la note d'une tierce », « raccourcis la durée ») → mêmes symboles = résultats différents dans le temps.

**Interprétation « tortue musicale » retenue**
```
F  → émet une note au degré courant (durée courante)
+  → degré courant += 1 pas de gamme
-  → degré courant -= 1 pas de gamme
[  → push(état: degré, octave, durée)      // branche
]  → pop(état)                              // fin de branche
a,b,X → symboles non terminaux (silencieux, pilotent la réécriture)
```
**Pseudo-algo**
```
s = axiome
repeat depth: s = appliquer(P, s)   // réécriture parallèle (+ π si stochastique)
état = {degré0, oct0, dur0}
for ch in s:
    switch ch: F→emit; +→deg++; -→deg--; [→push; ]→pop
```
**Jeux de règles intégrés** (au moins 2–3) :
- Branchement stochastique (p.151) : `F → F[+FF]F[−F]F | F[+F]F | F[−FF]F` (1/3 chacun).
- « Réduction vers centres tonals » (p.152) : règles `0→0,1→0,2→8,3→3,4→7,5→5,6→3,7→3,8→11,9→2,10→11,11→5` sur pcs chromatiques → converge vers ~5 hauteurs.
- Koch-like `F → F+F−−F+F` (φ interprété comme pas de gamme).
**Pièges** : croissance **exponentielle** du string → borner `depth` (4–5) et le nb de notes émises (couper à `bars*barSlices`). Le turtle-graphique pur reflète mal le comportement du LS → utiliser le mapping relatif DuBois.

---

## 4. Algorithme génétique  → `ProcTechnique.Genetic`
**Principe** (ch.7 ; Holland/Goldberg ; Papadopoulos-Wiggins ; **Towsey** pour la fitness). C'est le seul à intégrer un **critic objectif** (le levier longtemps manquant, cf. [[algorithmic-composition-nierhaus]]).
- Schéma GA : population aléatoire → **fitness** → sélection **roulette** (proba ∝ fitness) → **crossover 1 point** + **mutation** → génération suivante → répéter K fois → meilleur individu.
- Encodage (Papadopoulos) : génome = suite (degré/relatif-accord, durée) ; **silence** placé à ~12,5 %. Mutations « locales » = transposition / inversion / tri asc-desc d'un fragment ; « copy&operate » = copie/échange de fragments ; « restricted » = opère sur les **temps forts** (motifs reconnaissables).

**Fitness Towsey (6 features, vers moyennes cibles ; [43])**
```
1 Pitch variety      = distinctPitches / totalNotes            cible 0.27
2 Dissonant intervals= Σ dissonance(interval) / Σ intervals    cible 0.01
3 Contour direction  = risingIntervals / intervals             cible 0.49
4 Contour stability  = (montées consécutives même sens)/(intervals-1) cible 0.40
5 Rhythmic variety   = distinctDurations / 16                  cible 0.24
6 Rhythmic range     = (maxDur - minDur) / 16                  cible 0.32
fitness = Σ  wi * (1 - |feature_i - cible_i|)     (+ bonus notes d'accord si harmonyDegrees)
```
**Table de dissonance d'intervalle** (demi-tons, [43]) : `0,1,2,3,4,5,7,8,9,12 → 0.0` · `10 → 0.5` · `6,11,13 → 1.0`.
**Paramètres** : taille pop (~40), générations K (~60), taux mutation (~0.1). `harmonyDegrees` → bonus si la note tombe sur une note d'accord (temps fort surtout).
**Pièges** : fitness-bottleneck (pas d'évaluation humaine ici → tout algorithmique). Le modèle 3 étages (Towsey) n'atteint jamais la fitness optimale : viser « assez bon », pas parfait.

---

## 5. Fractale 1/f (bruit fractionnaire)  → `ProcTechnique.Fractal1f`
**Principe** (ch.6.5.1 ; Voss & Clarke ; Voss-McCartney).
- **Blanc** 1/f⁰ = décorrélé (trop aléatoire) ; **brownien** 1/f² = trop corrélé ; **rose 1/f** = équilibre **pas/sauts** → le plus musical.
- Générateur **Voss-McCartney** : somme de N sources aléatoires rafraîchies à des **cadences dyadiques** (source k mise à jour quand le bit k de n change) → bruit rose.
- Dodge : mapping multi-voix + **rythme dérivé d'une « diversité tonale »** (une 4ᵉ ligne 1/f fixe les durées).

**Pseudo-algo**
```
sources = tableau[N] de valeurs aléatoires
pinkAt(n): quels bits ont changé entre n-1 et n → resample ces sources; return moyenne(sources)
for n in 0..count:
    deg = quantifie(pinkAt(n) → index dans la gamme)
    dur = quantifie(pink2(n) → {croche, noire, ...})
    emit(placer(deg), pos, dur)
```
**Paramètres** : N (≈ log2(count), 4–8), plage de degrés, gamme de durées.
**Pièges** : normaliser la sortie sur [lo,hi] ; garder le rythme lisible (peu de valeurs de durée).

---

## 6. Thue-Morse (auto-similarité)  → `ProcTechnique.ThueMorse`
**Principe** (ch.6 auto-similarité ; morphisme d'**Axel Thue**, cité ch.4). Suite t(n) = parité du nombre de 1 dans l'écriture binaire de n : `0 1 1 0 1 0 0 1 1 0 0 1 0 1 1 0…` ; morphisme `0→01, 1→10` (auto-similaire).
**Mapping** :
```
for n in 0..count:
    if t(n)==0: degré += pasMontant   else: degré -= pasDescendant   // marche auto-similaire
    dur = durée selon t(n / bloc)  // niveaux de durée auto-similaires (blocs)
    emit(placer(degré), pos, dur)
```
Verticalisation depuis les pcs sélectionnés par TM. **Piège** : la marche peut dériver hors registre → replier dans [lo,hi] (mod-octave).

---

## Helpers partagés (à factoriser dans `ProceduralComposer`)
- `PlaceNear(prevPitch, pc, lo, hi)` : place une pitch-class à l'octave la plus proche de la note précédente, replie dans [lo,hi] (déjà présent dans `BaseComposerV3.PlaceNear` — s'en inspirer).
- `DegreeToPitch(deg, tonicPc, scale, lo, hi)` : degré de gamme → MIDI.
- `Quantize(dur)` : arrondir une durée à la grille 24-spq (croche=12, noire=24, …).
- `Verticalize(pcs, register)` : empile un ensemble de pcs en accord (bloc).
- Bornage anti-emballement : couper toute séquence à `bars*barSlices`.

## Variation (rappel — code existant réutilisé, pas dans ce moteur)
- Catalogue tonal public : `ArrangementEngine.ApplyVariation` (Rétrograde, InvertContour, BorrowMode, Ornament, MotoPerpetuo, RefitThemeModal, VaryRhythm).
- Ops « livre » de développement mélodique : `RecipeRenderer.ApplyOps` → à exposer via `RecipeRenderer.Develop` (`augment/diminish/expand/retroinvert/spin`=Fortspinnung `/grow`=L-système `/thuemorse/evolve`=génétique).
- La plupart des techniques de variation sont **agnostiques au contenu** (retrograde, inversion, augment…) → fonctionnent aussi sur du matériau sériel/atonal ; RefitThemeModal/MotoPerpetuo supposent des accords.

## Notes de style (Nierhaus, synthèse ch.11)
- La plupart des systèmes basculent d'un « objet scalebound » (principe formel strict, ex. auto-similarité) vers un « objet scaling » (retouché selon le goût). ⇒ nos techniques donnent une **matière première** ; l'utilisateur affine ensuite (édition riff, variation, degrés d'accord).
- Les cartes chaotiques (logistique, Hénon, Lorenz) offrent aussi un matériau (pitch via `F = 2^(c·x+d)`), mais moins contrôlable → non retenu au premier jet (extension possible).
