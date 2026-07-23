# Smoke-test the melody-CHARACTER root context: compose for several seeds and read back the MIDI
# tempo (driven by the picked character: calme 64-73 / modérée 76-85 / enjouée 92-105 bpm).
$ErrorActionPreference = "Stop"
$repo   = "C:\Users\swe\source\repos\MusicTracker"
$bin    = "$repo\MusicTracker\bin\Debug"
$corpus = "$repo\corpus\Ghibli"
$midi   = "$repo\corpus\ghibli_char_test.mid"

Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
$gh  = $asm.GetType("MusicTracker.Engine.ComposerV2.GhibliComposer")

$model = $an.GetMethod("Analyze").Invoke($null, @([string]$corpus))

# show the model's character distribution
$cd = $model.GetType().GetProperty("CharacterDistribution").GetValue($model)
$tot = 0.0; foreach ($k in $cd.Keys) { $tot += $cd[$k] }
Write-Host "CharacterDistribution:" -NoNewline
foreach ($k in $cd.Keys) { Write-Host (" {0}={1:P0}" -f $k, ($cd[$k]/$tot)) -NoNewline }
Write-Host "`n"

function Get-MidiBpm($path) {
  $mf = New-Object NAudio.Midi.MidiFile($path, $false)
  foreach ($tr in $mf.Events) { foreach ($e in $tr) {
    $te = $e -as [NAudio.Midi.TempoEvent]
    if ($null -ne $te) { return [math]::Round($te.Tempo, 0) }
  } }
  return 0
}

foreach ($seed in 1,2,3,4,5,6,7,8) {
  $inst = [System.Activator]::CreateInstance($gh)
  $gh.GetMethod("Compose").Invoke($inst, @($model, [int]$seed, [string]$midi)) | Out-Null
  $bpm = Get-MidiBpm $midi
  $label = if ($bpm -ge 92) { "enjouée" } elseif ($bpm -le 73) { "calme" } else { "modérée" }
  Write-Host ("seed {0,2}  ->  {1,3} bpm   ({2})" -f $seed, $bpm, $label)
}
