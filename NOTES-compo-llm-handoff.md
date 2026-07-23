# Reprise de conversation — Composition Ghibli + LLM/génération symbolique offline

> Document de hand-off (2026-06-19) pour reprendre le fil après fork du workspace.
> Couvre : (1) la pièce orchestrale composée, (2) toute la discussion « LLM embarqué / génération
> symbolique offline », (3) les décisions prises et les prochaines étapes.

---

## Partie 1 — Ce qui a été PRODUIT (livrable concret)

Brief de départ (style Ghibli) : *score orchestral de film, duo violon+piano tendre, 6/8 berçant ;
couplet intime (arpèges piano + violon solo) ; mi-parcours qui s'ouvre (cordes chaudes, harpe, bois) ;
climax plein orchestre (violon qui plane, swells de timbales, cordes « chœur ») ; transitions
cinématiques, houle océanique, shimmer, mix lumineux.*

**Fichiers (à la racine du repo) :**
- `ghibli_orchestral.mscx` — la partition (ouvre dans MuseScore 3/4).
- `compose_ghibli_orchestral.ps1` — le générateur reproductible (Claude a écrit les notes à la main,
  le script émet le .mscx).

**La pièce :** « Au cœur des nuages », **Ré majeur, 6/8, Andante en berçant, 34 mesures, 7 portées**
(Violon · Flûte · Cordes nappe/chœur · Harpe · Piano m.d. · Piano m.g. · Contrebasse).
Forme : Intro 4 | Couplet 8 (duo intime) | Mi-parcours 8 (ça s'ouvre + 2 mes. de montée) |
Climax 8 (tutti, thème +8ve, Do maj9 = ♭VII « houle ») | Coda 6 (résolution Ré add9).
Harmonie *colour*/modale (add9, maj7, sus, médiantes). Timbales rendues par les swells graves de la
Contrebasse (choix pour rester à 7 portées lisibles — une vraie portée Timbales reste possible).

**Vérifié :** chaque mesure validée = 6/8 exact (auto-contrôle dans le script) ; réimport via
`MuseScoreImporter.Load` → 7 pistes, 34 mesures (2448 slices ÷ 72), ambitus cohérents.

> Méthode = même approche « Claude-compositeur → .mscx » que `compose_ghibli.ps1` (berceuse piano) et
> `compose_ghibli2.ps1` (romance violon/piano). Voir mémoire `hand-composed-mscx-pieces.md`.
> NB technique : ce générateur a ajouté le **6/8 + durées pointées** au format .mscx (token `4.`, `2.`,
> et `<dots>1</dots>` AVANT `<durationType>`, cf. mémoire `musescore-export-tag-order`).

---

## Partie 2 — Discussion : LLM embarqué / génération symbolique offline

### Recadrage clé : comment la pièce a réellement été composée
Pas un modèle « musicien ». Trois ingrédients combinés :
1. Raisonnement général + théorie musicale d'un gros modèle (Opus).
2. Règles structurées issues des analyses de corpus (mémoires Hisaishi/Ghibli : mélodie pentatonique,
   accords *colour*/modaux, basse descendante, arc de forme…).
3. Code déterministe (PowerShell → .mscx) qui **garantit** la validité (mesures 6/8, voix, forme).

⟹ Créativité utile = (1)+(2) ; correction musicale = (3). Donc **l'objectif n'est PAS « un LLM local qui
compose tout seul comme Opus »**. Le bon découpage : le moteur déterministe garde la correction, le
modèle local ne fait que la part créative *bornée*.

### Le compromis fondamental
- **Paramètres précis → sortie exacte** = composition **algorithmique/déterministe** (= ce que fait déjà
  `RecipeRenderer` + `theme_library.json`). Pas d'IA.
- **Modèle qui invente, varié** = **génératif conditionné** : on *pilote* par paramètres mais la sortie
  est **échantillonnée**, pas dictée.
- Il n'existe pas de « LLM-librairie musique » qui rende du déterministe à partir de paramètres
  numériques. Ce qui existe = modèles **symboliques conditionnés** (sortie MIDI éditable).

### Options offline (runtimes)
| Option | Embarqué ? | Pour app .NET 4.8 |
|---|---|---|
| **Ollama / LM Studio** | service local | Le + simple ; serveur OpenAI-compatible localhost, appel `HttpClient`. |
| **llama.cpp (`llama-server`)** | service local | + décodage contraint **GBNF / JSON-schema** (clé pour sortie JSON valide). Bundlable. |
| **LLamaSharp** | in-process | Binding .NET, mais cible .NET Std 2.0/.NET 6+ → fragile en 4.8 (plutôt service enfant). |
| **ONNX Runtime GenAI** | in-process | Bon .NET + DirectML (GPU Windows) ; va bien avec Phi. |

Modèles petits open-weight (début 2026) : **Qwen2.5/3 (7-8B)** (très bon JSON + français),
**Mistral/Ministral (3-8B)**, **Phi-4/3.5 (MIT)**, Llama 3.2/3.3 (licence « community »).
GGUF Q4_K_M ≈ compromis ; 7B ≈ 4,5-6 Go. Pour usage commercial : préférer **Apache-2.0 / MIT**.

### Modèles symboliques dédiés musique (sortie MIDI)
| Lib / modèle | Pilotage | Notes |
|---|---|---|
| **MuseCoco** (Microsoft **Muzic**) | **attributs** (clé, tempo, mesure, instruments, émotion…) → MIDI | Le + proche de « paramètres → morceau ». 1,2 Md params. **← retenu, voir Partie 3.** |
| **Magenta** (MusicVAE, **ChordConditionedMelodyRNN**, **Coconet**) | accords → mélodie ; complétion ; latent | Vraies libs mais base TensorFlow ancienne (install pénible en 2026). |
| **Anticipatory Music Transformer** (Stanford) | contrôle / **infilling** évènementiel | Apache-2.0, PyTorch/pip propre, moderne. Bon pour « impose le thème, génère le reste ». |
| **MusicGen / AudioCraft** (Meta) | texte + mélodie | **Écarté** : sort de l'AUDIO, pas des notes → non éditable, n'entre pas dans le moteur. |

### Intégration .NET — réalité
Tout est **Python**. Pas de lib générative musique native .NET. Trois voies : (1) **sidecar** Python en
service local + HTTP/stdin (le + robuste en 4.8) ; (2) **export ONNX** + ONNX Runtime C# (pas toujours
trivial pour ces transformers) ; (3) **DryWetMIDI** (C#) pour la plomberie MIDI côté .NET.

### « Entraîner » un petit modèle — l'échelle (si on partait sur un LLM JSON)
1. **Zéro entraînement** : prompt + few-shot (tes thèmes pré-générés SONT les exemples) + **grammaire**
   qui force un JSON valide. Commencer là.
2. **RAG** sur la bibliothèque de thèmes + analyses.
3. **LoRA/QLoRA** seulement si ça plafonne — en **distillant depuis Claude** (briefs → JSON validés par
   le moteur → dataset). Outils : Unsloth / Axolotl / HF PEFT-TRL / LLaMA-Factory. 500-2000 exemples
   suffisent souvent. Export GGUF ensuite.

---

## Partie 3 — DÉCISIONS prises & prochaines étapes

### Décisions
1. **Voie symbolique** (pas audio). Le pont = **MIDI standard**.
2. **Réutilisation de l'existant** : `MidiImporter` fait déjà MIDI → `Score` (pistes/programmes +
   `TimeSig`/`MeasureStartSlices`) et `AddBarRiffs` fait déjà « un riff par mesure ». **Donc aucune
   couche de traduction à écrire.**
3. **Choix d'outil : MuseCoco** en direct. Plan de l'utilisateur : *« je lui donne les paramètres, il
   génère TOUT le morceau, que je découpe en track + riff par mesure ; rien de plus. »* → architecture
   v1 volontairement simple.
4. **Licence MuseCoco = OK** : code du repo `microsoft/muzic` sous **MIT** (usage commercial permis,
   pas de « research only ») ; **checkpoints 1,2 Md publiés sur Hugging Face** (offline). Nuances :
   vérifier la *model card* HF pour la licence des **poids** ; bémol général à tout modèle musical =
   **provenance des données d'entraînement** si diffusion commerciale (sans objet pour un usage
   « graine de thème → moteur réarrange »).

### Caveats acceptés de l'approche « MuseCoco génère tout »
1. **Contrôle approximatif, pas exact** : attributs fixes, sortie **échantillonnée** → générer
   plusieurs seeds et **curer**, pas du one-shot déterministe. Mapper les params du soft sur le
   vocabulaire d'attributs de MuseCoco ; le reste est ignoré.
2. **Pas d'arc d'arrangement intégré** : MuseCoco produit une texture assez **homogène** ; la dynamique
   *couplet intime → climax tutti* devra venir du post-traitement (muter/ajouter des pistes par
   section, automation de volume) — pas du modèle.
3. **Style « générique émotionnel », pas Hisaishi** : genre/émotion approchent le climat, pas la
   signature pentatonique/modale. Pour du vrai Ghibli → fine-tuning, ou hybride.

### Hybride (option de repli si v1 trop plate/neutre)
MuseCoco ne fournit que **le matériau** (thème/harmonie) → ton `RecipeRenderer` applique les params
EXACTS (tempo, mesure, forme, instrumentation, densité) de façon déterministe. Les params qui doivent
être exacts sont de l'**arrangement** = déjà géré par ton moteur ; le modèle ne sert qu'à la *graine*.
C'est exactement le rôle de ta `theme_library.json`, sauf entrées générées au lieu de curées.
(Cf. mémoires `theme-library-seed-engine`, `riff-note-list-model`.)

### TODO à la reprise (au choix)
- [ ] Vérifier la **liste exacte des attributs** acceptés par MuseCoco (savoir quels params du soft sont
      honorés) + la **model card Hugging Face** (licence des poids, taille/format du checkpoint).
- [ ] **Caler le contrat du sidecar** : service Python local `params → .mid` ; appel C# ; import via
      `MidiImporter` → découpe track + `AddBarRiffs`.
- [ ] (Option) brancher la sortie sur `theme_library.json` pour la voie hybride.

### Sources
- MuseCoco / Muzic (GitHub, MIT) : https://github.com/microsoft/muzic
- MuseCoco README : https://github.com/microsoft/muzic/blob/main/musecoco/README.md
- Page projet : https://microsoft.github.io/muzic/musecoco/
- Article arXiv 2306.00110 : https://arxiv.org/pdf/2306.00110
