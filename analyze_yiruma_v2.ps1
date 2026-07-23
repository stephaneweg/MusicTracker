# Composer V2 - Yiruma corpus analysis driver (modeled on analyze_ghibli_v2.ps1).
# Analyzes corpus\Yiruma into MusicTracker\Data\models\yiruma_model_v2.json (+ a markdown report).
$ErrorActionPreference = "Stop"
$repo   = "C:\Users\swe\source\repos\MusicTracker"
$bin    = "$repo\MusicTracker\bin\Debug"
$corpus = "$repo\corpus\Yiruma"
$json   = "$repo\MusicTracker\Data\models\yiruma_model_v2.json"
$report = "$repo\corpus\yiruma_model_v2_report.md"

Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
if ($null -eq $an) { throw "CorpusAnalyzerV2 type not found - rebuild MusicTracker first." }

Write-Host "Analyzing corpus: $corpus"
$model = $an.GetMethod("AnalyzeToFile").Invoke($null, @([string]$corpus, [string]$json, [string]$report))

$mt = $model.GetType()
function P($name) { $mt.GetProperty($name).GetValue($model) }
$files = P "FilesAnalyzed"; $maj = P "MajorPieces"; $min = P "MinorPieces"
$skipped = P "Skipped"

Write-Host ""
Write-Host "=== Yiruma V2 model ==="
Write-Host ("Files analyzed : {0}  (major {1} / minor {2})" -f $files, $maj, $min)
Write-Host ("Skipped        : {0}" -f $skipped.Count)
Write-Host ("JSON           : {0}  ({1:N0} bytes)" -f $json, ((Get-Item $json).Length))
Write-Host ("Report         : {0}" -f $report)

$ok = $true
function Check($cond, $msg) { if ($cond) { Write-Host "  [OK]  $msg" } else { Write-Host "  [!!]  $msg"; $script:ok = $false } }
Check ($files -ge 5) "at least 5 files analyzed"
Check (($maj + $min) -eq $files) "every analyzed piece classified major/minor"

if ($skipped.Count -gt 0) {
  Write-Host ""
  Write-Host "Skipped files:"
  foreach ($s in $skipped) { Write-Host "  - $s" }
}
Write-Host ""
if ($ok) { Write-Host "All sanity checks passed." } else { Write-Host "Some checks FAILED (see above)." }
