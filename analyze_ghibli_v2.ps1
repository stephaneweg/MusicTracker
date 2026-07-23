# Composer V2 - corpus analysis driver.
# Loads the built MusicTracker.exe by reflection (with an AssemblyResolve handler so System.Text.Json
# and friends resolve from bin\Debug) and calls CorpusAnalyzerV2.AnalyzeToFile over corpus\Ghibli.
# Writes MusicTracker\Data\ghibli_model_v2.json (the bundled model) + corpus\ghibli_model_v2_report.md.
$ErrorActionPreference = "Stop"
$repo   = "C:\Users\swe\source\repos\MusicTracker"
$bin    = "$repo\MusicTracker\bin\Debug"
$corpus = "$repo\corpus\Ghibli"
$json   = "$repo\MusicTracker\Data\models\ghibli_model_v2.json"
$report = "$repo\corpus\ghibli_model_v2_report.md"

# Load NAudio (needed by MidiImporter) then the exe. Both via LoadFrom so the LoadFrom context probes
# bin\Debug for transitive dependencies (System.Text.Json, System.Memory, ...) automatically.
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
if ($null -eq $an) { throw "CorpusAnalyzerV2 type not found - rebuild MusicTracker first." }

Write-Host "Analyzing corpus: $corpus"
$method = $an.GetMethod("AnalyzeToFile")
$model  = $method.Invoke($null, @([string]$corpus, [string]$json, [string]$report))

# ---- read summary back via reflection ----
$mt = $model.GetType()
function P($name) { $mt.GetProperty($name).GetValue($model) }
$files = P "FilesAnalyzed"; $maj = P "MajorPieces"; $min = P "MinorPieces"
$skipped = P "Skipped"

Write-Host ""
Write-Host "=== Ghibli V2 model ==="
Write-Host ("Files analyzed : {0}  (major {1} / minor {2})" -f $files, $maj, $min)
Write-Host ("Skipped        : {0}" -f $skipped.Count)
Write-Host ("JSON           : {0}  ({1:N0} bytes)" -f $json, ((Get-Item $json).Length))
Write-Host ("Report         : {0}" -f $report)

# ---- a few sanity assertions ----
$ok = $true
function Check($cond, $msg) { if ($cond) { Write-Host "  [OK]  $msg" } else { Write-Host "  [!!]  $msg"; $script:ok = $false } }
Check ($files -ge 50) "at least 50 files analyzed"
Check ($maj -ge 1 -and $min -ge 1) "both major and minor pieces present"

# minor-mode melody degree histogram should peak on the tonic (degree '1')
$minor = $mt.GetProperty("Minor").GetValue($model)
$deg = $minor.GetType().GetProperty("DegreeHistogram").GetValue($minor)
$top = $null; $topv = -1
foreach ($k in $deg.Keys) { if ($deg[$k] -gt $topv) { $topv = $deg[$k]; $top = $k } }
Check ($top -eq "1" -or $top -eq "5") "minor melody peaks on tonic/dominant (got '$top')"

if ($skipped.Count -gt 0) {
  Write-Host ""
  Write-Host "Skipped files:"
  foreach ($s in $skipped) { Write-Host "  - $s" }
}
Write-Host ""
if ($ok) { Write-Host "All sanity checks passed." } else { Write-Host "Some checks FAILED (see above)." }
