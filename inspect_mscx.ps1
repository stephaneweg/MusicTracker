$f = "C:\Users\swe\source\repos\MusicTracker\theme_variations.mscx"
[xml]$x = Get-Content $f -Raw
$st = @($x.museScore.Score.Staff)[0]   # melody staff
$m = 0; $trailing = 0
foreach ($meas in $st.Measure) {
  $m++
  $seq = @(); $lastIsRest = $false
  foreach ($node in $meas.voice.ChildNodes) {
    if ($node.Name -eq 'Chord') { $d=$node.durationType; $dt= if($node.dots){"."}else{""}; $seq += "C:$d$dt"; $lastIsRest=$false }
    elseif ($node.Name -eq 'Rest') { $d=$node.durationType; $dt= if($node.dots){"."}else{""}; $seq += "R:$d$dt"; $lastIsRest=$true }
  }
  if ($lastIsRest -and $seq.Count -gt 1) { $trailing++; Write-Host ("m{0,3}: {1}  <-- finit par un SILENCE" -f $m, ($seq -join " ")) }
}
Write-Host ("`nMesures (mélodie) finissant par un silence: $trailing")
