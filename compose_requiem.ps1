# Compose a "Nausicaa Requiem"-like piece: force the CALM character (slow, legato, sustained, low ornament)
# and pick a seed that lands in aeolian (natural minor) for the elegiac requiem colour.
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = "$repo\MusicTracker\bin\Debug"
$corpus = "$repo\corpus\Ghibli"
$mid  = "$repo\corpus\ghibli_requiem.mid"

Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
$gh  = $asm.GetType("MusicTracker.Engine.ComposerV2.GhibliComposer")
$model = $an.GetMethod("Analyze").Invoke($null, @([string]$corpus))

function Get-MidiBpm($path) {
  $mf = New-Object NAudio.Midi.MidiFile($path, $false)
  foreach ($tr in $mf.Events) { foreach ($e in $tr) { $te = $e -as [NAudio.Midi.TempoEvent]; if ($null -ne $te) { return [math]::Round($te.Tempo,0) } } }
  return 0
}

$probe = "$repo\corpus\_req_probe.mid"
$chosen = $null
foreach ($seed in 1..60) {
  $o = [System.Activator]::CreateInstance($gh)
  $gh.GetProperty("CharacterOverride").SetValue($o, "calme")
  $gh.GetMethod("Compose").Invoke($o, @($model, [int]$seed, [string]$probe)) | Out-Null
  if ([string]$gh.GetProperty("ChosenMode").GetValue($o) -eq "aeolian") { $chosen = $seed; break }
}
Remove-Item $probe,([System.IO.Path]::ChangeExtension($probe,".mscx")) -ErrorAction SilentlyContinue
if ($null -eq $chosen) { $chosen = 8806 }

$o = [System.Activator]::CreateInstance($gh)
$gh.GetProperty("CharacterOverride").SetValue($o, "calme")
$gh.GetMethod("Compose").Invoke($o, @($model, [int]$chosen, [string]$mid)) | Out-Null
$mode = [string]$gh.GetProperty("ChosenMode").GetValue($o)
$min  = if ([bool]$gh.GetProperty("ChosenMinor").GetValue($o)) { "min" } else { "MAJ" }
Write-Host ("Requiem-like : seed {0}  {1} {2}  {3} bpm" -f $chosen, $mode, $min, (Get-MidiBpm $mid))
Write-Host ("MIDI: {0}  ({1:N0} bytes)" -f $mid, (Get-Item $mid).Length)
$mscx = [System.IO.Path]::ChangeExtension($mid,".mscx")
if (Test-Path $mscx) { Write-Host ("MuseScore: {0}" -f $mscx) }
