# Digest the CLEAN Vivaldi corpus (full pieces only: vivaldi_full + season) into the model.
#   pass "all" to digest the entire corpus/vivaldi instead (includes vivaldi-o3-mids per-instrument splits + loose bulk).
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = "$repo\MusicTracker\bin\Debug"
$json = "$repo\MusicTracker\Data\models\vivaldi_model_v2.json"
$report = "$repo\corpus\vivaldi_model_v2_report.md"
$all  = ($args.Count -ge 1 -and $args[0] -eq "all")

Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")

if ($all) {
  Write-Host "Analyzing FULL corpus/vivaldi (recursive, incl. per-instrument splits)..."
  $model = $an.GetMethod("AnalyzeToFile").Invoke($null, @([string]"$repo\corpus\vivaldi", [string]$json, [string]$report))
} else {
  $dirs = @("$repo\corpus\vivaldi\vivaldi_full", "$repo\corpus\vivaldi\season")
  Write-Host "Analyzing CLEAN Vivaldi (full pieces only):"
  $dirs | ForEach-Object { Write-Host ("  - {0}" -f $_) }
  # wrap string[] as ONE element so reflection binds it to the single string[] parameter
  $argv = New-Object 'object[]' 3
  $argv[0] = [string[]]$dirs; $argv[1] = [string]$json; $argv[2] = [string]$report
  $model = $an.GetMethod("AnalyzeManyToFile").Invoke($null, $argv)
}
$mt = $model.GetType()

Write-Host ""
Write-Host "=== Vivaldi V2 model ==="
Write-Host ("Files analyzed : {0}  (major {1} / minor {2})" -f `
  $mt.GetProperty("FilesAnalyzed").GetValue($model), $mt.GetProperty("MajorPieces").GetValue($model), $mt.GetProperty("MinorPieces").GetValue($model))
Write-Host ("Skipped        : {0}" -f ($mt.GetProperty("Skipped").GetValue($model)).Count)
Write-Host ("JSON           : {0}  ({1:N0} bytes)" -f $json, (Get-Item $json).Length)
Write-Host ("Report         : {0}" -f $report)
Write-Host ""
$r = Get-Content $report
($r | Select-String -Pattern "Modes|Caract|Mesure|Tempo|notes d'accord|doublure" -Context 0,1)
