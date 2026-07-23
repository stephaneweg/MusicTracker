# Confirm the fugal exposition accumulates: subject ALONE (alto), then +answer (soprano), then +bass.
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = "$repo\MusicTracker\bin\Debug"
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
$bc  = $asm.GetType("MusicTracker.Engine.ComposerV2.BachClavierComposer")
$model = $an.GetMethod("Analyze").Invoke($null, @([string]"$repo\corpus\bach\wtc"))
$mid = "$repo\corpus\bach_clavier_fugue2.mid"
$o = [System.Activator]::CreateInstance($bc)
$bc.GetProperty("Movement").SetValue($o, "fugue")
$bc.GetMethod("Compose").Invoke($o, @($model, [int]4, [string]$mid)) | Out-Null

$mf = New-Object NAudio.Midi.MidiFile($mid, $false)
$ppq = $mf.DeltaTicksPerQuarterNote; $barT = 4 * $ppq
$lbl = @{ 1 = "SOP "; 2 = "ALT "; 3 = "BASS" }
Write-Host "Notes per voice per bar (0-17): exposition should accumulate -> ALT alone, then +SOP, then +BASS"
for ($tr = 1; $tr -le 3; $tr++) {
  $byBar = @{}
  foreach ($e in $mf.Events[$tr]) {
    $on = $e -as [NAudio.Midi.NoteOnEvent]
    if ($null -ne $on -and $on.Velocity -gt 0) { $b = [int][math]::Floor($on.AbsoluteTime / $barT); $byBar[$b] = 1 + ([int]$byBar[$b]) }
  }
  $line = (0..17 | ForEach-Object { "{0,2}" -f ([int]$byBar[$_]) }) -join " "
  Write-Host ("  {0}: {1}" -f $lbl[$tr], $line)
}
