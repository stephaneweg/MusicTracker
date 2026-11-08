---
name: new-module-three-resolvers
description: "⚠️ un nouveau FlowModule qui produit des notes doit être ajouté à TROIS resolveurs (player, partition, export MIDI) — sinon il sort EN SILENCE, sans erreur ni warning"
metadata: 
  node_type: memory
  type: project
  originSessionId: 358b357f-06c6-4108-9fb9-cf5c4bf4da89
  modified: 2026-07-29T21:51:11.137Z
---

Tout nouveau `FlowModule` qui produit des notes doit être déclaré dans **TROIS** `switch` distincts. Chacun a un `default:` qui renvoie `null`, donc un module oublié **disparaît silencieusement** — aucune exception, aucun warning, le build est vert et seule l'oreille (ou l'œil sur la partition) le signale.

1. `TimelinePlayer.RiffForModule` → **lecture audio**
2. `ScoreModel.RiffForModule` → **vue partition**, et avec elle **MusicXML, MuseScore (.mscx) et PDF** — les trois passent par `ScoreBuilder.Build`, donc ce seul oubli casse quatre sorties
3. `TimelineImporter.FlattenLeaf` → **export MIDI**

Compter aussi, selon le module : `ModuleDuration.Beats` (longueur du bloc), `TimelineHelper.TruncateModule` (insertion au curseur), et le `[JsonDerivedType]` sur `FlowModule` (persistance) — plus l'ajout au .csproj, voir [[legacy-csproj-add-files]].

**Why:** le piège s'est refermé **deux fois** : d'abord sur les lignes mélodiques (classiques et polyrythmiques), jamais exportées en MIDI ; puis le 2026-07-29 sur `PolyChordModule` (accords polyrythmiques), qui sonnait à la lecture mais était absent de la partition ET des quatre exports. Le commentaire d'avertissement écrit dans `FlattenLeaf` après le premier incident n'a pas empêché le second — un commentaire ne remplace pas une vérification.

**How to apply:** après avoir ajouté un module, écrire un contrôle qui compare le nombre de notes des trois chemins sur un projet de test — ils doivent coïncider (`PolyChord.Generate` = `ScoreBuilder.Build` = `FlattenForExport`), puis ÉCRIRE les fichiers et les relire (`.mid` via NAudio, `<pitch>` dans le `.musicxml`, `<Chord>` dans le `.mscx`). Sur le .mscx le compte est plus BAS que le nombre de notes, c'est normal : les notes simultanées fusionnent en un `<Chord>`. Voir [[euclidean-rhythm-generation-idea]] et [[polychord-module]].
