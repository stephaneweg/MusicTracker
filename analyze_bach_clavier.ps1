# Digest Bach's Well-Tempered Clavier (corpus/bach/wtc) into a new model:
#   Data\bach_clavier_model_v2.json + corpus\bach_clavier_model_v2_report.md
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = "$repo\MusicTracker\bin\Debug"
$dir  = "$repo\corpus\bach\wtc"
$json = "$repo\MusicTracker\Data\models\bach_clavier_model_v2.json"
$report = "$repo\corpus\bach_clavier_model_v2_report.md"

Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")

Write-Host ("Analyzing Bach WTC: {0}" -f $dir)
$model = $an.GetMethod("AnalyzeToFile").Invoke($null, @([string]$dir, [string]$json, [string]$report))
$mt = $model.GetType()

Write-Host ""
Write-Host "=== Bach clavier (WTC) V2 model ==="
Write-Host ("Files analyzed : {0}  (major {1} / minor {2})" -f `
  $mt.GetProperty("FilesAnalyzed").GetValue($model), $mt.GetProperty("MajorPieces").GetValue($model), $mt.GetProperty("MinorPieces").GetValue($model))
Write-Host ("Skipped        : {0}" -f ($mt.GetProperty("Skipped").GetValue($model)).Count)
Write-Host ("JSON           : {0}  ({1:N0} bytes)" -f $json, (Get-Item $json).Length)
Write-Host ("Report         : {0}" -f $report)
Write-Host ""
$r = Get-Content $report
($r | Select-String -Pattern "Modes|Caract|Mesure|Tempo" -Context 0,1)
