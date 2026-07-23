# Analyze Bach's SOLO works (cello suites + violin sonatas/partitas + flute partita) into ONE model:
# corpus/bach/solo_cello + solo_violin + solo_flute  ->  Data\bach_solo_model_v2.json + a markdown report.
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = "$repo\MusicTracker\bin\Debug"
$dirs = @("$repo\corpus\bach\solo_cello", "$repo\corpus\bach\solo_violin", "$repo\corpus\bach\solo_flute")
$json = "$repo\MusicTracker\Data\models\bach_solo_model_v2.json"
$report = "$repo\corpus\bach_solo_model_v2_report.md"

Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")

Write-Host "Analyzing Bach solo corpus:"
$dirs | ForEach-Object { Write-Host ("  - {0}" -f $_) }

$model = $an.GetMethod("AnalyzeManyToFile").Invoke($null, @([string[]]$dirs, [string]$json, [string]$report))
$mt = $model.GetType()

Write-Host ""
Write-Host "=== Bach solo V2 model ==="
Write-Host ("Files analyzed : {0}  (major {1} / minor {2})" -f `
  $mt.GetProperty("FilesAnalyzed").GetValue($model), $mt.GetProperty("MajorPieces").GetValue($model), $mt.GetProperty("MinorPieces").GetValue($model))
Write-Host ("Skipped        : {0}" -f ($mt.GetProperty("Skipped").GetValue($model)).Count)
Write-Host ("JSON           : {0}  ({1:N0} bytes)" -f $json, (Get-Item $json).Length)
Write-Host ("Report         : {0}" -f $report)
Write-Host ""

# headline distributions straight from the report
$r = Get-Content $report
($r | Select-String -Pattern "Modes|Caract|Mesure|Tempo" -Context 0,1)
