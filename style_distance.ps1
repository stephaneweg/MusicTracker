# Style-distance driver: how closely does the generator's melody match the trained corpus's statistics?
# Generates K pieces per config and reports histogram-intersection overlap for degree / degree-bigram / rhythm-cell
# distributions vs the model. Compares the trained RhythmCell CHAIN (#1) against the old reused MOTIF.
#
#   .\style_distance.ps1                              # ghibli, 8 seeds, chain vs motif
#   .\style_distance.ps1 -Model vivaldi_model_v2.json -Seeds 12
param(
  [string]$Model = "ghibli_model_v2.json",
  [int]$Seeds    = 8,
  [switch]$NoMotif,
  [string]$Bin   = "bin\Debug"
)
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = if (Test-Path $Bin) { (Resolve-Path $Bin).Path } else { "$repo\MusicTracker\$Bin" }
$modelPath = if (Test-Path $Model) { (Resolve-Path $Model).Path } else { "$repo\MusicTracker\Data\models\$Model" }
if (-not (Test-Path $modelPath)) { throw "Model not found: $Model" }

$global:__b = $bin
[System.AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler] {
  param($s, $e) $n = ($e.Name -split ',')[0].Trim(); $d = Join-Path $global:__b "$n.dll"
  if (Test-Path $d) { return [System.Reflection.Assembly]::LoadFrom($d) }; return $null })
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$sd  = $asm.GetType("MusicTracker.Engine.ComposerV3.StyleDistanceV3")
if ($null -eq $sd) { throw "StyleDistanceV3 not found - rebuild MusicTracker first." }

$report = $sd.GetMethod("Report").Invoke($null, @([string](Split-Path $modelPath -Leaf), [string]$modelPath, [int]$Seeds, [bool](-not $NoMotif)))
Write-Host $report
