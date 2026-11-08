---
name: legacy-csproj-add-files
description: OBSOLETE — MusicTracker.csproj est maintenant SDK-style, les nouveaux fichiers sont auto-inclus (ne plus editer manuellement)
metadata: 
  node_type: memory
  type: feedback
  originSessionId: e3a932f8-35d1-483c-bf18-4af6404c613a
  modified: 2026-08-04T13:57:58.581Z
---

**Retourné 2026-08-04** : `MusicTracker/MusicTracker.csproj` a été migré en SDK-style (`<Project Sdk="Microsoft.NET.Sdk">`, `TargetFramework=net10.0-windows`, `UseWPF=true`). Les fichiers `.cs` / `.xaml` / `.xaml.cs` sous `MusicTracker/` sont **auto-inclus** par le SDK — plus besoin d'entrées `<Compile>` / `<Page>` manuelles.

**How to apply:** Créer un nouveau fichier .cs ou .xaml directement, ne PAS toucher au csproj. Le build les prend automatiquement.

**Note historique** : avant cette migration, la memory disait qu'il fallait ajouter manuellement (ce qui était vrai à l'époque legacy .NET 4.8). Le user a paye une erreur passee (PolyChord.cs / .xaml oublies dans le csproj). Cette erreur n'est plus possible aujourd'hui.

**Exception potentielle** : si un jour on ajoute une ressource embarquée non-standard (font, image binaire, XSD), l'auto-detection peut ne pas suffire — verifier le comportement au cas par cas. Les .json de langue et de data sont explicitement ajoutes via `<Content Include="Data/**/*.json">` etc.
