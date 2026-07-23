# Generate one Bach piece per dance movement (forced), so each characteristic rhythm can be heard.
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = "$repo\MusicTracker\bin\Debug"
$dirs = @("$repo\corpus\bach\solo_cello", "$repo\corpus\bach\solo_violin", "$repo\corpus\bach\solo_flute")

Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
$bc  = $asm.GetType("MusicTracker.Engine.ComposerV2.BachSoloComposer")

$argv = New-Object 'object[]' 1; $argv[0] = [string[]]$dirs
$model = $an.GetMethod("AnalyzeMany").Invoke($null, $argv)
$names = "C","C#","D","D#","E","F","F#","G","G#","A","A#","B"

function Get-Meter($mid) {
  $mf = New-Object NAudio.Midi.MidiFile($mid, $false)
  foreach ($tr in $mf.Events) { foreach ($e in $tr) {
    $ts = $e -as [NAudio.Midi.TimeSignatureEvent]; if ($null -ne $ts) { return ("{0}/{1}" -f $ts.Numerator, [math]::Pow(2,$ts.Denominator)) } } }
  return "?"
}

$dances = "allemande","courante","sarabande","gigue","menuet","bourree","gavotte","chaconne","prelude"
$seed = 7
foreach ($d in $dances) {
  $mid = "$repo\corpus\bach_$d.mid"
  $o = [System.Activator]::CreateInstance($bc)
  $bc.GetProperty("Instrument").SetValue($o, "violin")
  $bc.GetProperty("Movement").SetValue($o, [string]$d)
  $bc.GetMethod("Compose").Invoke($o, @($model, [int]$seed, [string]$mid)) | Out-Null
  $tonic = $names[([int]$bc.GetProperty("ChosenTonicPc").GetValue($o))]
  $mode  = if ([bool]$bc.GetProperty("ChosenMinor").GetValue($o)) { "min" } else { "maj" }
  $meter = Get-Meter $mid
  Write-Host ("{0,-10} {1,-4} {2,3}  {3}" -f $d, $meter, ("{0}{1}" -f $tonic,$mode), (Split-Path $mid -Leaf))
}
Write-Host "`nDone. corpus\bach_<dance>.mid (+ .mscx) for each."
