<#
.SYNOPSIS
  Build a MusicTracker release: bump the version, build Release, compile the Inno Setup installer,
  publish it to the MusicTracker_Releases checkout (releases/) and update latest.json for the
  in-app auto-update.

.DESCRIPTION
  Steps:
    1. Bump AssemblyVersion / AssemblyFileVersion in Properties\AssemblyInfo.cs (patch by default).
    2. Build the solution in Release (the GitHub token is injected from releases\releases.token).
    3. Compile installer\MusicTracker.iss with ISCC -> installer\Output\KotonStudioSetup-<ver>.exe.
    4. Bundle the same Release output as a portable ZIP under a single top-level folder
       KotonStudio-<ver>/, excluding what the .iss also excludes (*.pdb, SoundFont, user files) ->
       installer\Output\KotonStudioPortable-<ver>.zip.
    5. Copy both artifacts into releases\ and write releases\latest.json { version, installer,
       portable, notes }. The in-app updater only reads 'installer' — 'portable' is for the website.
    6. Unless -NoPush: commit the version bump on the main repo and commit+push the releases repo.

.PARAMETER Bump      patch | minor | major   (ignored when -Version is given)
.PARAMETER Version   explicit 4-part version, e.g. 1.2.0.0
.PARAMETER Notes     release notes shown in the update popup (and stored in latest.json)
.PARAMETER NoPush    build + stage everything locally but do not commit/push

.EXAMPLE
  installer\release.ps1 -Bump patch -Notes "Correctifs divers + pistes repliables."
#>
[CmdletBinding()]
param(
  [ValidateSet('patch', 'minor', 'major')] [string]$Bump = 'patch',
  [string]$Version,
  [string]$Notes = '',
  [string]$NotesEn = '',
  [switch]$NoPush
)

$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$repo         = Split-Path -Parent $installerDir
$sln          = Join-Path $repo 'MusicTracker.sln'
$asmInfo      = Join-Path $repo 'MusicTracker\Properties\AssemblyInfo.cs'
$releaseBin   = Join-Path $repo 'MusicTracker\bin\Release'
$iss          = Join-Path $installerDir 'MusicTracker.iss'
$issOut       = Join-Path $installerDir 'Output'
$releasesDir  = Join-Path $repo 'releases'

function Find-Tool {
  param([string[]]$Candidates, [string]$InPath)
  foreach ($c in $Candidates) { if ($c -and (Test-Path $c)) { return $c } }
  if ($InPath) { $g = Get-Command $InPath -ErrorAction SilentlyContinue; if ($g) { return $g.Source } }
  return $null
}

# --- locate tooling -----------------------------------------------------------
$msbuild = Find-Tool @(
  "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
  "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
  "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
) 'MSBuild.exe'
if (-not $msbuild) { throw "MSBuild introuvable (installe Visual Studio 2022 ou les Build Tools)." }

$iscc = Find-Tool @(
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
  "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"  # winget per-user install
) 'ISCC.exe'
if (-not $iscc) { throw "ISCC.exe (Inno Setup 6) introuvable. Installe-le : https://jrsoftware.org/isdl.php" }

# --- 1. bump version ----------------------------------------------------------
$asm = Get-Content $asmInfo -Raw
$m = [regex]::Match($asm, 'AssemblyVersion\("(\d+)\.(\d+)\.(\d+)\.(\d+)"\)')
if (-not $m.Success) { throw "AssemblyVersion introuvable dans $asmInfo" }
$cur = @([int]$m.Groups[1].Value, [int]$m.Groups[2].Value, [int]$m.Groups[3].Value, [int]$m.Groups[4].Value)

if ($Version) {
  if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Version invalide : $Version (attendu x.y.z.w)" }
  $newVer = $Version
}
else {
  # Les parentheses autour du "+ 1" sont OBLIGATOIRES : en PowerShell la virgule lie plus fort que
  # l'addition, donc @($cur[0], $cur[1], $cur[2] + 1, 0) se lit @($cur[0],$cur[1],$cur[2]) + @(1,0)
  # — une CONCATENATION de tableaux, qui rendait 5 elements. -Bump minor incrementait alors le 3e
  # chiffre au lieu du 2e, -Bump patch le 4e au lieu du 3e, et -Bump major levait "op_Addition".
  switch ($Bump) {
    'major' { $cur = @(($cur[0] + 1), 0, 0, 0) }
    'minor' { $cur = @($cur[0], ($cur[1] + 1), 0, 0) }
    'patch' { $cur = @($cur[0], $cur[1], ($cur[2] + 1), 0) }
  }
  if ($cur.Count -ne 4) { throw "Calcul de version casse : $($cur.Count) composantes au lieu de 4." }
  $newVer = "$($cur[0]).$($cur[1]).$($cur[2]).$($cur[3])"
}
Write-Host "Version : $($m.Groups[1].Value).$($m.Groups[2].Value).$($m.Groups[3].Value).$($m.Groups[4].Value) -> $newVer" -ForegroundColor Cyan

$asm = [regex]::Replace($asm, 'AssemblyVersion\("\d+\.\d+\.\d+\.\d+"\)',     "AssemblyVersion(""$newVer"")")
$asm = [regex]::Replace($asm, 'AssemblyFileVersion\("\d+\.\d+\.\d+\.\d+"\)', "AssemblyFileVersion(""$newVer"")")
[System.IO.File]::WriteAllText($asmInfo, $asm, (New-Object System.Text.UTF8Encoding($true)))

# --- 2. build Release ---------------------------------------------------------
Write-Host "Build Release…" -ForegroundColor Cyan
& $msbuild $sln /t:Rebuild /p:Configuration=Release /v:minimal /nologo /m
if ($LASTEXITCODE -ne 0) { throw "Le build Release a échoué (code $LASTEXITCODE)." }

# --- 3. compile installer -----------------------------------------------------
Write-Host "Compilation de l'installeur…" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $issOut | Out-Null
& $iscc "/DMyAppVersion=$newVer" "/DSourceDir=$releaseBin" "/DOutputDir=$issOut" $iss
if ($LASTEXITCODE -ne 0) { throw "La compilation Inno Setup a échoué (code $LASTEXITCODE)." }

$setupName = "KotonStudioSetup-$newVer.exe"
$setupPath = Join-Path $issOut $setupName
if (-not (Test-Path $setupPath)) { throw "Installeur introuvable après compilation : $setupPath" }
Write-Host "Installeur : $setupPath" -ForegroundColor Green

# --- 4. build the portable ZIP -----------------------------------------------
# Doit rester ALIGNÉ avec le champ 'Excludes' de MusicTracker.iss (§[Files]). Le doublon est assumé :
# ISCC lit ses excludes depuis le .iss (syntaxe Pascal) et n'a pas d'API pour les partager côté PS.
# On stage dans %TEMP% puis on zippe en une passe : plus simple et sans dépendance à l'enum
# System.IO.Compression.ZipArchiveMode (chargé de façon inégale selon les hôtes PowerShell).
Write-Host "Emballage de la version portable…" -ForegroundColor Cyan
$portableName = "KotonStudioPortable-$newVer.zip"
$portablePath = Join-Path $issOut $portableName
# Dossier racine dans le zip = l'utilisateur dézippe n'importe où sans exploser son dossier
# courant, et garde plusieurs versions côte à côte s'il télécharge plusieurs releases.
$topFolder = "KotonStudio-$newVer"
$stageRoot = Join-Path $env:TEMP "kotonstudio-portable-$newVer"
$stageTop  = Join-Path $stageRoot $topFolder

if (Test-Path -LiteralPath $stageRoot)   { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
if (Test-Path -LiteralPath $portablePath) { Remove-Item -LiteralPath $portablePath -Force }
New-Item -ItemType Directory -Path $stageTop -Force | Out-Null

# robocopy exit code : 0-7 = succès (bit 3 = fichiers copiés), >= 8 = échec réel.
& robocopy $releaseBin $stageTop /E `
  /XF *.pdb *.vshost.* settings.json recent.json userdata.json `
  /XD SoundFont userdata `
  /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy a échoué (code $LASTEXITCODE) pour la version portable." }

# Marqueur .portable : PRÉSENT UNIQUEMENT DANS LE ZIP (jamais dans l'installeur Inno). Sa présence
# à côté de l'exe fait basculer UpdateChecker sur le chemin portable (updater + zip) au lieu de
# télécharger et lancer le setup silencieux.
New-Item -ItemType File -Path (Join-Path $stageTop '.portable') -Force | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
# includeBaseDirectory=$true => l'archive contient KotonStudio-<ver>/ à sa racine.
[System.IO.Compression.ZipFile]::CreateFromDirectory($stageTop, $portablePath, 'Optimal', $true)
Remove-Item -LiteralPath $stageRoot -Recurse -Force

$portableSize = [math]::Round((Get-Item -LiteralPath $portablePath).Length / 1MB, 1)
Write-Host "Portable   : $portablePath ($portableSize Mo)" -ForegroundColor Green

# --- 5. publish into releases/ + latest.json ---------------------------------
if (-not (Test-Path $releasesDir)) { throw "Dossier releases/ absent (clone de MusicTracker_Releases attendu)." }
Copy-Item $setupPath    (Join-Path $releasesDir $setupName)    -Force
Copy-Item $portablePath (Join-Path $releasesDir $portableName) -Force

# notes = language-neutral fallback (French); notesFr/notesEn feed the language-aware changelog (UpdateChecker).
$notesEnFinal = if ($NotesEn) { $NotesEn } else { $Notes }
$manifest = [ordered]@{ version = $newVer; installer = $setupName; portable = $portableName; notes = $Notes; notesFr = $Notes; notesEn = $notesEnFinal }
$json = $manifest | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText((Join-Path $releasesDir 'latest.json'), $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "latest.json mis à jour (version $newVer)." -ForegroundColor Green

# --- 5. commit / push ---------------------------------------------------------
if ($NoPush) { Write-Host "-NoPush : rien n'est commité/poussé." -ForegroundColor Yellow; return }

# main repo: the version bump
& git -C $repo add 'MusicTracker/Properties/AssemblyInfo.cs'
& git -C $repo commit -m "Release v$newVer" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
& git -C $repo push

# releases repo: the installer + manifest (+ the token-ignoring .gitignore if new)
& git -C $releasesDir add -A
& git -C $releasesDir commit -m "Release v$newVer"
& git -C $releasesDir push
if ($LASTEXITCODE -ne 0) { throw "Le push du dépôt releases a échoué (vérifie l'auth git sur MusicTracker_Releases)." }

Write-Host "Release v$newVer publiée." -ForegroundColor Green
