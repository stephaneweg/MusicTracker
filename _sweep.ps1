# Run the resolver config sweep at a given hole density N on the already-built held-out model+test.
param([int]$N = 4, [string]$Bin = "MusicTracker\bin\ResolveTest")
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = if (Test-Path $Bin) { (Resolve-Path $Bin).Path } else { "$repo\MusicTracker\$Bin" }
$global:__b = $bin
[System.AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler] {
  param($s, $e) $n = ($e.Name -split ',')[0].Trim(); $d = Join-Path $global:__b "$n.dll"
  if (Test-Path $d) { return [System.Reflection.Assembly]::LoadFrom($d) }; return $null })
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$rv  = $asm.GetType("MusicTracker.Engine.ComposerV2.MelodyResolverV2")
$model = "$repo\_holdout\ghibli_heldout_model_v2.json"
$test  = "$repo\_holdout\test"
Write-Host ("=== Sweep on held-out, N=$N (1 note in $N held out) ===")
$rv.GetMethod("SweepReport").Invoke($null, @([string]$model, [string]$test, [int]$N, [int]50))
