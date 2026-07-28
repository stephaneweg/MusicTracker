# Analyses de fonctionnalités

Un dossier par feature traitée par le pipeline automatisé, nommé `<AAAA-MM-JJ>-<slug>/` :

| Fichier | Auteur | Contenu |
|---|---|---|
| `01-fonctionnel.md` | agent analyste fonctionnel | Le **quoi** : besoin, comportement vu de l'utilisateur, cas limites, hors-périmètre, critères d'acceptation. Aucun nom de classe ni de fichier. |
| `02-technique.md` | agent architecte | Le **comment** : approche retenue, fichiers touchés, persistance, clés de localisation, risques, plan de test. Une section « Tour de correction N » y est ajoutée à chaque passage de la boucle de bug. |
| `03-tests.md` | agent testeur | Ce qui a été exécuté et observé, les échecs, et — toujours — ce qui **n'a pas pu** être vérifié. |

## Comment c'est produit

Le workflow `.claude/workflows/feature-pipeline.js` enchaîne quatre agents spécialisés — analyste
fonctionnel → architecte → développeur → testeur — puis boucle « diagnostic → correctif → re-test »
(3 tours maximum) tant que les tests sont rouges. La tâche planifiée `musictracker-daily-feature`
l'invoque chaque soir et ne publie **que** si tout est vert.

Chaque agent démarre sans mémoire des autres : ces documents sont le seul lien entre eux. C'est aussi
pourquoi ils sont conservés même quand un run échoue — le run suivant y lit ce qui a déjà été tenté.

## Une limite à garder en tête

Un run automatisé n'a **ni oreille ni yeux**. Il vérifie les invariants par le calcul et pilote
l'interface via le harnais FlaUI (`AutoTest/`), mais il ne peut pas juger qu'un rendu sonore est
musical ni qu'une disposition est lisible. La section « non vérifié » de `03-tests.md` liste ce qui
reste à valider à la main — elle n'est jamais vide sur une feature audio ou visuelle.
