# Same-style held-out benchmark for the melody resolver.
# Splits corpus\Ghibli into train (minus held-out) + test (held-out), RE-ANALYZES train into a fresh model
# (which ACTIVATES the new pdeg|cdeg|ndeg tiers), then runs the resolver config sweep on the held-out files.
param([string]$Bin = "MusicTracker\bin\ResolveTest", [int]$N = 4)
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = if (Test-Path $Bin) { (Resolve-Path $Bin).Path } else { "$repo\MusicTracker\$Bin" }

$global:__b = $bin
[System.AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler] {
  param($s, $e) $n = ($e.Name -split ',')[0].Trim(); $d = Join-Path $global:__b "$n.dll"
  if (Test-Path $d) { return [System.Reflection.Assembly]::LoadFrom($d) }; return $null })
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
$rv  = $asm.GetType("MusicTracker.Engine.ComposerV2.MelodyResolverV2")

$src   = "$repo\corpus\Ghibli"
$train = "$repo\_holdout\train"
$test  = "$repo\_holdout\test"
$model = "$repo\_holdout\ghibli_heldout_model_v2.json"

# uniquely-named, unambiguously-Ghibli melodies to hold out
$holdout = @(
  "Spirited Away - Itsumo Nando Demo.mid",
  "Kiki's Delivery Service - Tabidachi.mid",
  "joe_hisaishi-nostalgia-1998.mid",
  "joe_hisaishi-summer-1999.mid",
  "Laputa - Castle in the Sky - Laputa Theme.mid",
  "Spirited Away - The Sixth Station.mid"
)

Remove-Item "$repo\_holdout" -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $train, $test | Out-Null
Copy-Item "$src\*" $train -Recurse -Force
Get-ChildItem $train -Recurse -File | Where-Object { $holdout -contains $_.Name } | Remove-Item -Force
foreach ($h in $holdout) {
  $f = Get-ChildItem $src -Recurse -File | Where-Object { $_.Name -eq $h } | Select-Object -First 1
  if ($f) { Copy-Item $f.FullName (Join-Path $test $h) -Force } else { Write-Host "  (held-out not found: $h)" }
}
$nTrain = (Get-ChildItem $train -Recurse -File | Where-Object { @(".mid", ".midi", ".mscz", ".mscx") -contains $_.Extension.ToLower() }).Count
$nTest  = (Get-ChildItem $test -File).Count
Write-Host ("Split: train={0} files, test(held-out)={1} files" -f $nTrain, $nTest)

Write-Host "Re-analyzing train -> fresh model (degree tiers active) ..."
[void]$an.GetMethod("AnalyzeToFile").Invoke($null, @([string]$train, [string]$model, [string]""))
Write-Host ("Model: {0} KB" -f [int]((Get-Item $model).Length / 1KB))
Write-Host ""
Write-Host "=== Resolver config sweep on HELD-OUT (same-style, unseen) files ==="
$rv.GetMethod("SweepReport").Invoke($null, @([string]$model, [string]$test, [int]$N, [int]50))
Write-Host ""
Write-Host "Baselines: random ~8% (1/12), diatonic-random ~14% (1/7), chord-tone-only ~33%."
