# Migration .NET Framework 4.8 -> .NET 10 (WPF, SDK-style)

Branche : `worktree-agent-aa375c00a863bc8a5` (worktree isole, ne pas merger sur
`main` sans relecture — un autre agent est en train de coder le hosting VST en
parallele sur `main`).

## Statut

- **Phase A (net8.0-windows)** : OK. Build vert Debug + Release, 0 warning, exe
  se lance et affiche la fenetre principale (fumee validee).
- **Phase B (net10.0-windows)** : OK. Le SDK/runtime .NET 10.0.110 etait deja
  installe sur la machine (decouvert lors du premier build). Meme resultat :
  build vert Debug + Release, 0 warning, exe se lance.

## Verification pre-merge cote humain

Ce que je n'ai PAS pu tester (pas de projet `.sq` accessible dans le worktree
isole) et qu'il faut valider avant de merger :

1. Ouvrir un projet `.sq` existant.
2. Playback : jouer un morceau (SoundFont, mixer, effets).
3. Enregistrement violon (capture audio in).
4. Export : MIDI, WAV, MuseScore/.mscx, PDF.
5. Compilation de l'installeur Inno via `installer/release.ps1` (voir section
   "Installer" ci-dessous — probablement des ajustements a faire).
6. Passage AutoTest (`AutoTest/run.ps1`) — la migration bump aussi le harnais
   FlaUI en net10.
7. Update auto : l'exe portable + KotonStudioUpdater sur cycle complet.

## Ce qui a ete migre

### Projets

| Projet               | Avant              | Apres              |
|----------------------|--------------------|--------------------|
| MusicTracker         | net48 (legacy)     | net10.0-windows (SDK, UseWPF) |
| MeltySynth           | net48 (legacy)     | net10.0 (SDK, C# pur) |
| KotonStudioUpdater   | net48 (legacy)     | net10.0-windows (SDK) |
| AutoTest             | net48 (SDK)        | net10.0-windows (SDK) |

### APIs / infra reoutillees

- **`packages.config` -> `PackageReference`** (MusicTracker). Le fichier
  `packages.config` est supprime. Tous les NuGet passent en `PackageReference`
  au format SDK.
- **App.config conserve** : `System.Configuration.ConfigurationManager` ajoute
  en NuGet (in-box sur .NET Framework, package separe sur .NET Core+).
  `ChangelogUrl`, `GitHubRepo`, `GitHubReleasesRepo` continuent de se lire via
  `ConfigurationManager.AppSettings["…"]` sans changement dans le code.
- **`GenerateAssemblyInfo=false`** sur MusicTracker + KotonStudioUpdater : on
  conserve les `Properties/AssemblyInfo.cs` manuscrits parce que
  `installer/release.ps1` bumpe la version par regex dedans (voir plus bas).
- **`AppendTargetFrameworkToOutputPath=false`** +
  **`AppendRuntimeIdentifierToOutputPath=false`** sur MusicTracker et
  KotonStudioUpdater : preserve l'ancien layout `bin\Release\KotonStudio.exe`
  (au lieu du `bin\Release\net10.0-windows\` par defaut du SDK) pour que
  `installer/MusicTracker.iss` (`SourceDir=..\MusicTracker\bin\Release`) et
  `installer/release.ps1` continuent de fonctionner tels quels.
- **`GenerateBuildSecrets` UsingTask** : passe de `CodeTaskFactory` (MSBuild
  legacy, requiert Microsoft.Build.Tasks.Core.dll historique) a
  `RoslynCodeTaskFactory` (moderne, dispo dans le SDK depuis 2019). Logique
  interne du token XOR/base64 inchangee. XOR key toujours en phase avec
  `BugReportConfig.XorKey`.
- **NuGet drops** (etaient dans `packages.config`, plus utilises ou in-box) :
  - `Microsoft.Bcl.AsyncInterfaces` — in-box net8+
  - `Microsoft.Win32.Registry` — in-box net8-windows
  - `System.Buffers`, `System.Memory`, `System.Numerics.Vectors`,
    `System.Runtime.CompilerServices.Unsafe`, `System.IO.Pipelines`,
    `System.Threading.Tasks.Extensions`, `System.ValueTuple` — tous in-box
  - `System.Security.AccessControl`, `System.Security.Principal.Windows` —
    in-box
  - `System.Text.Encodings.Web` — transitive de System.Text.Json
  - `NAudio.WinForms` — jamais reference dans le code (grep `NAudio.Gui`
    vide) ; supprime
  - `Unnoficial.Microsoft.Expression.Drawing` — jamais reference ; supprime
  - `WriteableBitmapEx` — jamais reference ; supprime

  Reste referencees en explicite : `NAudio` (2.2.1), `NAudio.Lame` (2.1.0,
  natifs libmp3lame.32/64.dll copies via son .targets),
  `System.Text.Json` (9.0.0), `System.Configuration.ConfigurationManager`
  (9.0.0).

- **`Reference Include="System.Windows.Forms"`** supprime : le code n'utilise
  aucune API WinForms (grep vide sauf commentaires + boilerplate resx).

### Fix code

- **`MusicTracker/Engine/IWaveProvider.cs`** : retire `using
  System.Runtime.Remoting.Channels;` (dead import ; le namespace Remoting
  n'existe pas sur .NET Core+).
- **`MeltySynth/ArrayMath.cs`** : le compilateur .NET 10 (C# 12/13) resout
  desormais `MemoryMarshal.Cast<float, Vector<float>>(x)` (ou `x` est
  `float[]`) sur l'overload `ReadOnlySpan`, ce qui donne un indexeur en
  ref-readonly, et le `vd[i] += …` echoue en CS8331 (Vector<T> est passe
  `readonly struct` sur .NET 8+). Fix : forcer `Span<float>` explicitement
  avec une variable locale intermediaire, puis lire/ecrire via une temp.
  Chemin critique (rendu additif), verifier a l'oreille apres merge.
- **`MeltySynth/Properties/AssemblyInfo.cs`** : retire les attributs
  Title/Product/Description qui font double emploi avec ceux auto-generes par
  le SDK (`GenerateAssemblyInfo=true` par defaut). `InternalsVisibleTo`
  conserve — MusicTracker en depend pour lire les regions/samples SoundFont
  internes.

### Warnings supprimes (NoWarn) — a savoir

- **`CA1416`** (MusicTracker) : l'app cible `net10.0-windows` et est
  Windows-only par design, mais l'analyseur n'infere pas la version de
  plateforme Windows minimum (defaut = 7.0). Les appels `Registry.*`,
  quelques APIs WPF, `System.Printing` declarent tous `[SupportedOSPlatform]`
  avec une version >= 7.0 → l'analyseur crie "reachable on all platforms".
  Faux positif ici puisque le TFM force Windows.
- **`NU1510`** (MusicTracker + AutoTest) : sur net10 le pruning NuGet propose
  de supprimer `System.Text.Json` et `System.Configuration.ConfigurationManager`
  qui sont in-box. Je les garde referencees en explicite pour survivre a un
  rollback net10 → net8 (ou elles sont toujours package).
- **`NU1904`** (AutoTest) : FlaUI 4.0.0 traine System.Drawing.Common 5.0.2,
  connu vulnerable (GHSA-rxg9-xrhp-64gj). Le harness ne rend rien via GDI+,
  faux positif transitive. A adresser en bumpant FlaUI si l'auteur publie
  une version compatible net10.
- **`SYSLIB0014`** (MusicTracker) : `ServicePointManager.SecurityProtocol`
  utilise dans `SoundFontDownloader` et `Engine/Update/UpdateChecker` — utile
  sur net48 (forcer TLS 1.2), no-op sur .NET moderne. Non deplacable vers
  HttpClient sans refonte du telechargement, deferre.
- **`SYSLIB0011`, `SYSLIB0051`** : preventif (BinaryFormatter, serialization
  reflection). Aucun trigger dans le code actuellement, garde par prudence
  au cas ou un compilateur futur les reveille sur du code deja present.

## Rollback vers net8.0-windows

Pour repasser net10 → net8 : chercher-remplacer `net10.0-windows` par
`net8.0-windows` (et `net10.0` par `net8.0` pour MeltySynth) dans les 4
`.csproj`. Aucun autre changement necessaire (pas de code net10-specifique).
La branche a un commit dedie a Phase B pour ce revert :
`git revert b699766` fera exactement ca.

Le SDK 8.0.22 est aussi installe sur la machine, donc la construction fonctionne
sans autre installation.

## Installer / release.ps1 — a ajuster (non-fait, hors-scope)

`installer/release.ps1` et `installer/MusicTracker.iss` etaient calibres pour
une sortie `bin\Release\MusicTracker.exe` (netfx). Grace a
`AppendTargetFrameworkToOutputPath=false`, la SORTIE reste au bon endroit
(`bin\Release\KotonStudio.exe`), donc **rien a bouger de ce cote**. MAIS il y a
d'autres points a corriger dans l'installeur avant la premiere release en .NET
10 :

1. **Prerequis .NET Desktop Runtime 10** : `installer/MusicTracker.iss` ne
   telecharge / ne verifie pas la presence du runtime .NET 10 Desktop chez
   l'utilisateur. Sans lui l'exe crashe au lancement. Options :
   - Ajouter un bloc `[Code]` qui verifie
     `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\<arch>\sharedhost` (ou
     `dotnet --list-runtimes`) et telecharge le Desktop runtime 10 depuis
     `https://dotnet.microsoft.com/download/dotnet/10.0/runtime` avant
     d'installer l'app (idempotent, silencieux).
   - OU passer a un **self-contained deployment** : ajouter
     `<PublishSingleFile>true</PublishSingleFile>` +
     `<SelfContained>true</SelfContained>` +
     `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` dans MusicTracker.csproj,
     changer `installer\release.ps1` pour appeler `dotnet publish -c Release
     -r win-x64 --self-contained true` au lieu de `msbuild /p:Configuration=Release`,
     puis pointer `SourceDir` dans le .iss sur `bin\Release\net10.0-windows\
     win-x64\publish`. Taille de l'installeur passe de ~15 MB a ~150 MB (le
     runtime .NET Desktop 10 est bundle) mais l'utilisateur n'a rien a
     installer avant.

   **Recommande** : self-contained. C'est ce qui evite les tickets support
   "l'app ne demarre pas" chez les utilisateurs finaux (mode installe ET mode
   portable — le zip portable est aussi non-self-contained aujourd'hui).

2. **Portable zip** (`installer/release.ps1` genere `KotonStudioPortable-<ver>.zip`) :
   meme probleme, doit contenir le runtime, sinon casse chez tout utilisateur
   n'ayant pas explicitement installe .NET 10 Desktop. Meme fix = publish
   self-contained + zipper le dossier `publish`.

3. **Build via msbuild ou dotnet** : `installer/release.ps1` appelle
   probablement `msbuild.exe`. Sur les projets SDK-style c'est OK, mais
   `dotnet build` est plus canonique. A tester au moment de la premiere
   release ; probablement aucun changement necessaire cote script car msbuild
   comprend les .csproj SDK.

4. **`installer/MusicTracker.iss`** : `[Files]` liste probablement fichier par
   fichier. Avec le layout SDK certains fichiers changent : notamment on gagne
   `KotonStudio.deps.json`, `KotonStudio.runtimeconfig.json`, `KotonStudio.dll`
   (le .exe est un apphost, tout le code est dans le .dll), plusieurs
   `System.*.dll` packagees, un dossier `runtimes/` avec sous-dossiers RID.
   Verifier que le `[Files]` du .iss embarque bien tout `bin\Release\*.dll`,
   `*.exe`, `*.deps.json`, `*.runtimeconfig.json` et le dossier `runtimes\`
   entier + `Data\`, `Fonts\`, `Localization\` (deja copies par le build).

5. **Signature Authenticode** : signer maintenant le .exe (apphost natif) ET
   le .dll principal (`KotonStudio.dll`). L'ancien setup ne signait que le
   .exe.

Aucun de ces ajustements n'est fait dans cette branche — hors scope indique
par la mission. Ils sont a traiter au moment de la premiere release en .NET
10, dans une PR dediee touchant `installer/`.

## Notes suivi VST

- Cette migration debloque **`VST.NET2-Host 2.1.10`** (VST2, cible
  `net10.0-windows`) et **`NPlug 0.5`** (VST3, cible `net10.0`) qui
  imposaient .NET 10 minimum. Prêt a etre reference par l'autre agent quand
  sa branche VST hosting merge.

## Historique git de la branche

- `7f39fa6` MeltySynth : migration net48 → net8.0 (SDK-style csproj)
- `324cb7d` MusicTracker + KotonStudioUpdater : migration net48 → net8.0-windows
- `bc04ef7` AutoTest : bump TFM net48 → net8.0-windows
- `b699766` Phase B : bump net8.0-windows → net10.0-windows
- (ce commit) MIGRATION_NOTES.md

Petits commits par etape volontaire — chaque commit build vert independamment
pour faciliter le cherry-pick / revert.
