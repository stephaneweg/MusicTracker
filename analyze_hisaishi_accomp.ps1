# Hisaishi ACCOMPANIMENT phrasing (deep): per-bar SHAPE (rising wave / arch / zigzag / sustain), reversals, peak
# position, sustains, span, rhythm, onset density, and MOTIF repetition bar-to-bar. Accompaniment = the busiest
# non-melody track (the arpeggio/LH engine). Focuses on piano pieces (>=2 voices).
$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm  = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$midiLoad = $asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$mszLoad  = $asm.GetType("MusicTracker.Engine.MuseScoreImporter").GetMethod("Load")
$dir = "C:\Users\swe\source\repos\MusicTracker\musescore\JoeHisaishi"

function LoadScore($file){ $ext=[System.IO.Path]::GetExtension($file).ToLower(); if($ext -eq ".mid"){$midiLoad.Invoke($null,@([string]$file))}else{$mszLoad.Invoke($null,@([string]$file)) } }
function GetTracks($score){
  $tracks=$score.GetType().GetField("Tracks").GetValue($score); $out=New-Object System.Collections.ArrayList
  foreach($t in $tracks){ $tt=$t.GetType(); if([bool]$tt.GetField("IsDrum").GetValue($t)){continue}
    $arr=New-Object System.Collections.ArrayList
    foreach($n in ($tt.GetField("Notes").GetValue($t))){ $nt=$n.GetType(); [void]$arr.Add([pscustomobject]@{ p=[int]$nt.GetField("Pitch").GetValue($n); s=[int]$nt.GetField("StartSlice").GetValue($n); l=[int]$nt.GetField("LengthSlices").GetValue($n) }) }
    if($arr.Count -gt 0){ [void]$out.Add($arr) }
  }
  return ,$out
}
function SnapDur($d){ (3,6,8,12,16,18,24,36,48,72,96 | Sort-Object {[math]::Abs($_-$d)})[0] }
function DurName($s){ switch($s){3{"32"}6{"16"}8{"8T"}12{"8"}16{"qT"}18{"d8"}24{"q"}36{"dq"}48{"h"}72{"dh"}96{"w"}default{"$s"} } }

$ag=@{ dur=@{}; shapes=@{}; rev=New-Object System.Collections.ArrayList; peak=New-Object System.Collections.ArrayList;
  span=New-Object System.Collections.ArrayList; onsetsBar=New-Object System.Collections.ArrayList; susBars=0; barTot=0;
  motifMatch=0; motifTot=0; risingRun=New-Object System.Collections.ArrayList; topStep=0; topTot=0 }
$pieceN=0

$files = Get-ChildItem $dir -Recurse | Where-Object { $_.Extension -match '\.(mid|mscz)$' }
foreach($f in $files){
  try{ $score=LoadScore $f.FullName }catch{ continue }
  $st=$score.GetType(); $tsD=[int]$st.GetField("TimeSigD").GetValue($score); $tsN=[int]$st.GetField("TimeSigN").GetValue($score)
  $beat = if($tsD -eq 8){36}else{24}; $bpb = if($tsD -eq 8){[int]($tsN/3)}else{$tsN}; $barSlices=$bpb*$beat
  $tracks=GetTracks $score; if($tracks.Count -lt 2){ continue }
  # melody = highest mean; accompaniment = busiest of the rest
  $means=@(); foreach($t in $tracks){ $means+=($t|ForEach-Object{$_.p}|Measure-Object -Average).Average }
  $hi=0; for($i=1;$i -lt $tracks.Count;$i++){ if($means[$i] -gt $means[$hi]){$hi=$i} }
  $acc=-1;$accN=-1; for($i=0;$i -lt $tracks.Count;$i++){ if($i -eq $hi){continue}; if($tracks[$i].Count -gt $accN){$accN=$tracks[$i].Count;$acc=$i} }
  if($acc -lt 0){ continue }
  $pieceN++
  $notes = $tracks[$acc] | Sort-Object s,p
  foreach($e in $notes){ $d=SnapDur $e.l; if(-not $ag.dur.ContainsKey($d)){$ag.dur[$d]=0}; $ag.dur[$d]++ }
  # group by bar (using top-of-onset pitch per distinct start = the arpeggio line; also track sustains)
  $maxEnd=0; foreach($e in $notes){ if($e.s+$e.l -gt $maxEnd){$maxEnd=$e.s+$e.l} }
  $nbar=[int][math]::Ceiling($maxEnd/$barSlices)
  $prevSig=$null
  for($b=0;$b -lt $nbar;$b++){
    $lo=$b*$barSlices; $hiB=$lo+$barSlices
    $inbar = $notes | Where-Object { $_.s -ge $lo -and $_.s -lt $hiB }
    if(@($inbar).Count -lt 2){ continue }
    $ag.barTot++
    # onset line: per distinct start, the highest pitch (the arpeggio's reaching note)
    $byOnset = $inbar | Group-Object s | Sort-Object {[int]$_.Name}
    $line=@(); foreach($g in $byOnset){ $line += ($g.Group | Sort-Object p -Descending | Select-Object -First 1).p }
    [void]$ag.onsetsBar.Add(@($byOnset).Count)
    $ps = $inbar | ForEach-Object { $_.p }
    [void]$ag.span.Add((($ps|Measure-Object -Maximum).Maximum - ($ps|Measure-Object -Minimum).Minimum))
    # reversals + peak position
    $rev=0; for($i=2;$i -lt $line.Count;$i++){ $a=$line[$i]-$line[$i-1]; $c=$line[$i-1]-$line[$i-2]; if($a -ne 0 -and $c -ne 0 -and [math]::Sign($a) -ne [math]::Sign($c)){$rev++} }
    [void]$ag.rev.Add($rev)
    $maxp=-999;$maxi=0; for($i=0;$i -lt $line.Count;$i++){ if($line[$i] -gt $maxp){$maxp=$line[$i];$maxi=$i} }
    if($line.Count -gt 1){ [void]$ag.peak.Add([math]::Round($maxi/[double]($line.Count-1),2)) }
    # sustain: any note in the bar held >= 1.25 beat
    $hasSus = ($inbar | Where-Object { $_.l -ge [int]($beat*1.25) } | Measure-Object).Count -gt 0
    if($hasSus){ $ag.susBars++ }
    # shape classification
    $net = $line[$line.Count-1]-$line[0]
    $shape = if($rev -ge 3){"zigzag"} elseif($line.Count -ge 3 -and $maxi -ge 1 -and $maxi -le $line.Count-2 -and $net -le 2 -and $net -ge -2){"arch/wave"} elseif($net -ge 3){"rising"} elseif($net -le -3){"falling"} else{"flat/other"}
    if(-not $ag.shapes.ContainsKey($shape)){$ag.shapes[$shape]=0}; $ag.shapes[$shape]++
    # rising-run: longest consecutive ascending steps (a wave climbing several beats)
    $run=0;$best=0; for($i=1;$i -lt $line.Count;$i++){ if($line[$i] -gt $line[$i-1]){$run++; if($run -gt $best){$best=$run}} else {$run=0} }
    [void]$ag.risingRun.Add($best)
    # top-line stepwise? (counter-melody): consecutive top notes a step apart
    for($i=1;$i -lt $line.Count;$i++){ $d=[math]::Abs($line[$i]-$line[$i-1]); if($d -gt 0){ $ag.topTot++; if($d -le 2){$ag.topStep++} } }
    # motif repetition: same onset-count AND same up/down/flat sign sequence as previous bar
    $signs=@(); for($i=1;$i -lt $line.Count;$i++){ $signs += [math]::Sign($line[$i]-$line[$i-1]) }
    $sig = "$($line.Count):" + ($signs -join ",")
    if($prevSig -ne $null){ $ag.motifTot++; if($sig -eq $prevSig){ $ag.motifMatch++ } }
    $prevSig=$sig
  }
}

"===== ACCOMPANIMENT phrasing over $pieceN pieces, $($ag.barTot) bars =====`n"
"Rhythm (accompaniment note durations):"
$dt=($ag.dur.Values|Measure-Object -Sum).Sum
$ag.dur.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 8|ForEach-Object{ "    {0,-4} {1,5:N1}%" -f (DurName $_.Key),(100*$_.Value/$dt) }
"Onsets per bar: avg={0:N1}  (per beat ~{1:N1})" -f (($ag.onsetsBar|Measure-Object -Average).Average),(($ag.onsetsBar|Measure-Object -Average).Average/4.0)
"`nPer-bar SHAPE distribution:"
$st2=($ag.shapes.Values|Measure-Object -Sum).Sum
$ag.shapes.GetEnumerator()|Sort-Object Value -Descending|ForEach-Object{ "    {0,-11} {1,5:N1}%" -f $_.Key,(100*$_.Value/$st2) }
"Direction reversals per bar: avg={0:N1} (0-1 = smooth wave/line, >=3 = zigzag/Alberti)" -f (($ag.rev|Measure-Object -Average).Average)
"Peak position within bar (0=start,1=end): avg={0:N2} (≈1 = rises to the top late; ≈0.5 = arch)" -f (($ag.peak|Measure-Object -Average).Average)
"Longest rising run per bar (consecutive ascending steps): avg={0:N1}" -f (($ag.risingRun|Measure-Object -Average).Average)
"Bars containing a SUSTAIN (note >= 1.25 beat): {0:N0}%" -f (100*$ag.susBars/[math]::Max(1,$ag.barTot))
"Figure span per bar: avg={0:N1} semitones (median {1})" -f (($ag.span|Measure-Object -Average).Average),(($ag.span|Sort-Object)[[int]($ag.span.Count/2)])
"Top-of-arpeggio line stepwise (counter-melody): {0:N0}%" -f (100*$ag.topStep/[math]::Max(1,$ag.topTot))
"MOTIF repetition (bar's contour == previous bar's): {0:N0}%" -f (100*$ag.motifMatch/[math]::Max(1,$ag.motifTot))
