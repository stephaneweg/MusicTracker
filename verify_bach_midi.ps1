# Inspect a generated Bach MIDI: per-channel note count, pitch range, duration histogram, and a peek
# at the first bar to confirm the implied-polyphony figuration (a low pedal recurring under an upper voice).
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = "$repo\MusicTracker\bin\Debug"
$mid  = if ($args.Count -ge 1) { [string]$args[0] } else { "$repo\corpus\bach_solo_violin.mid" }
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }

$mf = New-Object NAudio.Midi.MidiFile($mid, $false)
$ppq = $mf.DeltaTicksPerQuarterNote
Write-Host ("File: {0}   PPQ={1}" -f (Split-Path $mid -Leaf), $ppq)

for ($tr = 0; $tr -lt $mf.Tracks; $tr++) {
  $ons = @()
  foreach ($e in $mf.Events[$tr]) {
    $on = $e -as [NAudio.Midi.NoteOnEvent]
    if ($null -ne $on -and $on.Velocity -gt 0) { $ons += $on }
  }
  if ($ons.Count -eq 0) { continue }
  $pitches = $ons | ForEach-Object { $_.NoteNumber }
  $durs    = $ons | ForEach-Object { [int][math]::Round($_.NoteLength / ($ppq/4.0)) }  # in sixteenths
  $lo = ($pitches | Measure-Object -Minimum).Minimum
  $hi = ($pitches | Measure-Object -Maximum).Maximum
  $ch = $ons[0].Channel
  Write-Host ("  track {0}  ch={1}  notes={2}  pitch {3}..{4}  (range {5} st)" -f $tr, $ch, $ons.Count, $lo, $hi, ($hi-$lo))
  # duration histogram (in sixteenths)
  $h = $durs | Group-Object | Sort-Object Name | ForEach-Object { "{0}/16:{1}" -f $_.Name, $_.Count }
  Write-Host ("    durations: {0}" -f ($h -join "  "))
  # first 16 note pitches (one bar of the prelude) to eyeball the pedal+upper-voice pattern
  $first = ($ons | Sort-Object AbsoluteTime | Select-Object -First 16 | ForEach-Object { $_.NoteNumber }) -join " "
  Write-Host ("    first bar pitches: {0}" -f $first)
}
