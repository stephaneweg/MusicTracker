# Confirm the ritornello structure: ripieno plays in the TUTTI bars and RESTS in the SOLO episodes;
# the solo spins 16ths in the episodes; the motor bass drives throughout.
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = "$repo\MusicTracker\bin\Debug"
$mid  = "$repo\corpus\vivaldi_concerto.mid"
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$mf = New-Object NAudio.Midi.MidiFile($mid, $false)
$ppq = $mf.DeltaTicksPerQuarterNote; $barT = 4 * $ppq
$lbl = @{ 1 = "SOLO"; 2 = "RIP "; 3 = "HARP"; 4 = "BASS" }
Write-Host "Notes per voice per bar (0-27). RIP should fill the ritornello bars (0-3, 8-9, 14-15, 20-23) and REST in the solo episodes."
for ($tr = 1; $tr -le 4; $tr++) {
  $byBar = @{}
  foreach ($e in $mf.Events[$tr]) { $on = $e -as [NAudio.Midi.NoteOnEvent]; if ($null -ne $on -and $on.Velocity -gt 0) { $b = [int][math]::Floor($on.AbsoluteTime / $barT); $byBar[$b] = 1 + ([int]$byBar[$b]) } }
  $line = (0..27 | ForEach-Object { "{0,2}" -f ([int]$byBar[$_]) }) -join " "
  Write-Host ("  {0}: {1}" -f $lbl[$tr], $line)
}
