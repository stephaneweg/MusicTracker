---
name: multi-api-keys
description: Multiple named API keys per provider (rotate Gemini projects) — settings manager + key-by-name pickers in compose dialogs
metadata: 
  node_type: memory
  type: project
  originSessionId: 5fb908b2-3cfc-4ea2-8f9e-423d71503dd6
---

Feature (2026-07-21) : plusieurs **clés API nommées par fournisseur** (l'utilisateur a N projets Gemini avec clés distinctes, tournées pour dépasser le quota journalier).

- **Stockage** (`AppSettings`) : `List<ApiKeyEntry {Provider,Name,Key}> ApiKeys` + `Dictionary<string,string> SelectedKeyName` (fournisseur→nom choisi). Les anciens champs clé-unique (`GeminiApiKey`…) restent pour la MIGRATION seulement.
- **`AiProviders`** (Engine/AI) : `EnsureMigrated()` (clé-unique héritée → entrée "Défaut", one-shot tant que ApiKeys vide), `KeysFor(p)`, `GetSelectedKeyName/SetSelectedKeyName(p)`, `KeyByName(p,name)`, `GetKey(p)` = valeur de la clé sélectionnée (fallback 1re clé puis legacy). `SetKey` SUPPRIMÉ (n'était utilisé que par AiElementDialog).
- **`Dialogs/ApiKeysDialog`** : gestionnaire tableau `[Fournisseur ▾][Nom][Clé]` (lignes construites en code, working-copy, Ajouter/🗑, Enregistrer écrit `AppSettings.ApiKeys`). Ouvert depuis **SettingsDialog** (bouton "Gérer les clés API…") et depuis les 2 dialogues de compo (bouton "Gérer…").
- **AiComposeDialog + AiElementDialog** ([[ai-per-element-generation]], [[mistral-ai-arrangement]]) : le champ clé (PasswordBox `txtApiKey`) → **ComboBox `cboKey`** filtrée par fournisseur (par NOM) ; sélection mémorisée via SetSelectedKeyName ; clé effective = `KeyByName(provider, cboKey.SelectedItem)`. Expander MODÈLE ouvert si aucune clé.

Pas encore testé live (migration + rotation).
