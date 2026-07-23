# Check the phrase ARC of a generated melody: split the lead voice (track 1) into phrases on a beat-long
# rest, and report where the peak (highest note) falls within each phrase (a clear arch peaks ~0.5-0.7).
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = "$repo\MusicTracker\bin\Debug"
$mid  = if ($args.Count -ge 1) { [string]$args[0] } else { "$repo\corpus\ghibli_v2_calme.mid" }
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }

$mf  = New-Object NAudio.Midi.MidiFile($mid, $false)
$ppq = $mf.DeltaTicksPerQuarterNote
$ons = @()
foreach ($e in $mf.Events[1]) { $on = $e -as [NAudio.Midi.NoteOnEvent]; if ($null -ne $on -and $on.Velocity -gt 0) { $ons += $on } }
$ons = $ons | Sort-Object AbsoluteTime

# group by 2-bar windows (8 beats) = the composer's phrase unit
$win = 8 * $ppq
$groups = @{}
foreach ($o in $ons) { $w = [int]([math]::Floor($o.AbsoluteTime / $win)); if (-not $groups.ContainsKey($w)) { $groups[$w] = @() }; $groups[$w] += $o }
$phrases = @(); foreach ($k in ($groups.Keys | Sort-Object)) { $phrases += ,$groups[$k] }

Write-Host ("{0}: {1} phrases" -f (Split-Path $mid -Leaf), $phrases.Count)
$peaks = @()
$pi = 0
foreach ($ph in $phrases) {
  $n = $ph.Count; if ($n -lt 3) { continue }
  $maxp = ($ph | Measure-Object -Property NoteNumber -Maximum).Maximum
  $idx  = 0; for ($j=0; $j -lt $n; $j++) { if ($ph[$j].NoteNumber -eq $maxp) { $idx = $j; break } }
  $frac = [math]::Round($idx / ($n-1), 2)
  $lo = ($ph | Measure-Object -Property NoteNumber -Minimum).Minimum
  $peaks += $frac
  $pi++
  if ($pi -le 8) { Write-Host ("  phrase {0,2}: {1,2} notes  peak@{2:N2}  range {3}-{4} ({5} st)" -f $pi, $n, $frac, $lo, $maxp, ($maxp-$lo)) }
}
if ($peaks.Count -gt 0) {
  $avg = [math]::Round(($peaks | Measure-Object -Average).Average, 2)
  Write-Host ("  => mean peak position = {0}  (≈0.6 = a clear rise-to-peak-then-fall arch)" -f $avg)
}
