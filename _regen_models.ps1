# Regenerate the 5 bundled Composer-V2 models with the CURRENT analyzer (adds the chord-degree pdeg|cdeg|ndeg
# tiers from step 1). Uses the SAME corpus paths/methods as the per-model analyze_*.ps1 scripts and DEFAULT orders
# (all 5 models have "Orders": {}), so nothing tuned is lost. Writes the source models then copies to the runtime
# folder. Load an alt build via -Bin while the app holds bin\Debug.
param([string]$Bin = "MusicTracker\bin\ResolveTest")
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
if ($null -eq $an) { throw "CorpusAnalyzerV2 not found - build first." }
$md  = "$repo\MusicTracker\Data\models"
$run = "$repo\MusicTracker\bin\Debug\Data\models"

function ReportModel($j) {
  $has = Select-String -Path $j -Pattern 'pdeg' -SimpleMatch -Quiet
  Write-Host ("  -> {0:N0} bytes, pdeg tiers present: {1}" -f (Get-Item $j).Length, $has)
}
function One($corpus, $name) {
  $j = "$md\$name"; Write-Host "Analyzing $name  <-  $corpus"
  [void]$an.GetMethod("AnalyzeToFile").Invoke($null, @([string]$corpus, [string]$j, [string]""))
  ReportModel $j
}
function Many($dirs, $name) {
  $j = "$md\$name"; Write-Host "Analyzing $name  <-  $($dirs -join ' ; ')"
  $argv = New-Object 'object[]' 3; $argv[0] = [string[]]$dirs; $argv[1] = [string]$j; $argv[2] = [string]""
  [void]$an.GetMethod("AnalyzeManyToFile").Invoke($null, $argv)
  ReportModel $j
}

One  "$repo\corpus\Ghibli" "ghibli_model_v2.json"
Many @("$repo\corpus\vivaldi\vivaldi_full", "$repo\corpus\vivaldi\season") "vivaldi_model_v2.json"
One  "$repo\corpus\bach\wtc" "bach_clavier_model_v2.json"
Many @("$repo\corpus\bach\solo_cello", "$repo\corpus\bach\solo_violin", "$repo\corpus\bach\solo_flute") "bach_solo_model_v2.json"
One  "$repo\corpus\Yiruma" "yiruma_model_v2.json"

Write-Host "Copying to runtime (bin\Debug\Data\models) ..."
foreach ($n in @("ghibli_model_v2.json", "vivaldi_model_v2.json", "bach_clavier_model_v2.json", "bach_solo_model_v2.json", "yiruma_model_v2.json")) {
  Copy-Item "$md\$n" "$run\$n" -Force; Write-Host "  copied $n"
}
Write-Host "Done. (Yiruma.json left untouched.)"
