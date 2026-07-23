# Hisaishi PHRASING analysis: phrase length, period pairing (antecedent/consequent), cadential (phrase-final) degree,
# phrase-initial degree, anacrusis (upbeat starts), breath length (held note / rest at phrase ends), contour (peak pos).
$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm  = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$midiLoad = $asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$mszLoad  = $asm.GetType("MusicTracker.Engine.MuseScoreImporter").GetMethod("Load")
$dir = "C:\Users\swe\source\repos\MusicTracker\musescore\JoeHisaishi"
$DNAME=@("1","b2","2","b3","3","4","#4","5","b6","6","b7","7")

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
function MonoRatio($arr){ if($arr.Count -lt 2){return 0.0}; $s=$arr|Sort-Object s,p; $ov=0;$re=-1; foreach($e in $s){ if($e.s -lt $re-2){$ov++}; $en=$e.s+$e.l; if($en -gt $re){$re=$en} }; return [double]$ov/$s.Count }
function Skyline($arr){ $g=$arr|Group-Object s|Sort-Object {[int]$_.Name}; $line=New-Object System.Collections.ArrayList; foreach($grp in $g){ [void]$line.Add(($grp.Group|Sort-Object p -Descending|Select-Object -First 1)) }; return ,$line }
function Tonic($tracks){  # bass+melody weighted argmax
  $means=@(); foreach($t in $tracks){ $means+=($t|ForEach-Object{$_.p}|Measure-Object -Average).Average }
  $hi=0;$lo=0; for($i=0;$i -lt $tracks.Count;$i++){ if($means[$i] -gt $means[$hi]){$hi=$i}; if($means[$i] -lt $means[$lo]){$lo=$i} }
  $mw=@(0.0)*12; foreach($e in $tracks[$hi]){ $mw[$e.p%12]+=[math]::Max(1,$e.l) }
  $bw=@(0.0)*12; foreach($e in $tracks[$lo]){ $bw[$e.p%12]+=[math]::Max(1,$e.l) }
  $sm=($mw|Measure-Object -Sum).Sum; $sb=($bw|Measure-Object -Sum).Sum; if($sm -le 0){$sm=1}; if($sb -le 0){$sb=1}
  $best=0;$bv=-1; for($pc=0;$pc -lt 12;$pc++){ $v=$mw[$pc]/$sm+1.3*$bw[$pc]/$sb+0.4*$bw[($pc+7)%12]/$sb; if($v -gt $bv){$bv=$v;$best=$pc} }
  return [pscustomobject]@{ tonic=$best; hi=$hi }
}

# aggregate
$ag=@{ plen=New-Object System.Collections.ArrayList; finalDeg=@{}; initDeg=@{}; anacrusis=0; phrTot=0;
  pairEq=0; pairTot=0; antSusp=0; antTot=0; breathLen=New-Object System.Collections.ArrayList; restGap=New-Object System.Collections.ArrayList;
  peakPos=New-Object System.Collections.ArrayList; startBeat=@{} }
$pieceN=0

$files = Get-ChildItem $dir -Recurse | Where-Object { $_.Extension -match '\.(mid|mscz)$' }
foreach($f in $files){
  try{ $score=LoadScore $f.FullName }catch{ continue }
  $st=$score.GetType(); $tsN=[int]$st.GetField("TimeSigN").GetValue($score); $tsD=[int]$st.GetField("TimeSigD").GetValue($score)
  $beat = if($tsD -eq 8){36}else{24}; $barSlices = if($tsD -eq 8){[int]($tsN/3)*$beat}else{$tsN*$beat}
  $tracks=GetTracks $score; if($tracks.Count -eq 0){continue}
  $tm=Tonic $tracks; $tonic=$tm.tonic
  $mt=$tracks[$tm.hi]
  $mono = (MonoRatio $mt) -lt 0.12
  $line = if($mono){ ($mt|Sort-Object s,p) } else { Skyline $mt }
  if($line.Count -lt 8){ continue }
  $pieceN++
  # median length
  $lens=$line|ForEach-Object{$_.l}|Sort-Object; $Lmed=$lens[[int]($lens.Count/2)]
  $longThr=[math]::Max([int](1.25*$beat),[int](1.6*$Lmed))
  # detect phrase-end indices
  $bounds=New-Object System.Collections.ArrayList
  for($i=0;$i -lt $line.Count;$i++){
    $gap = if($i -lt $line.Count-1){ $line[$i+1].s-($line[$i].s+$line[$i].l) } else { 99999 }
    $isLong = $line[$i].l -ge $longThr
    if($gap -ge [int]($beat*0.75) -or $isLong -or $i -eq $line.Count-1){ [void]$bounds.Add($i) }
  }
  # build phrases between bounds
  $phrases=New-Object System.Collections.ArrayList
  $startIdx=0
  foreach($b in $bounds){
    if($b -lt $startIdx){ continue }
    $ph = @{ s=$startIdx; e=$b }
    [void]$phrases.Add($ph)
    $startIdx=$b+1
  }
  $prevLen=-1; $prevFinalDeg=-1
  for($pi=0;$pi -lt $phrases.Count;$pi++){
    $ph=$phrases[$pi]; $a=$ph.s; $b=$ph.e
    $startS=$line[$a].s; $endS=$line[$b].s+$line[$b].l
    $plenBeats=[math]::Round(($endS-$startS)/[double]$beat,1)
    if($plenBeats -lt 1 -or $plenBeats -gt 16){ continue }
    [void]$ag.plen.Add($plenBeats); $ag.phrTot++
    # anacrusis: start not on a downbeat (offset within bar != 0)
    $off=$startS % $barSlices; $bp=[int]($off/$beat)+1; if(-not $ag.startBeat.ContainsKey($bp)){$ag.startBeat[$bp]=0}; $ag.startBeat[$bp]++
    if($off -ne 0){ $ag.anacrusis++ }
    # degrees
    $fd=((($line[$b].p-$tonic)%12)+12)%12; $idg=((($line[$a].p-$tonic)%12)+12)%12
    if(-not $ag.finalDeg.ContainsKey($fd)){$ag.finalDeg[$fd]=0}; $ag.finalDeg[$fd]++
    if(-not $ag.initDeg.ContainsKey($idg)){$ag.initDeg[$idg]=0}; $ag.initDeg[$idg]++
    # breath: final note length, and rest gap after
    [void]$ag.breathLen.Add([math]::Round($line[$b].l/[double]$beat,2))
    if($b -lt $line.Count-1){ $g=$line[$b+1].s-($line[$b].s+$line[$b].l); if($g -gt 0){ [void]$ag.restGap.Add([math]::Round($g/[double]$beat,2)) } }
    # peak position within phrase (0..1)
    $maxp=-999;$maxi=$a; for($k=$a;$k -le $b;$k++){ if($line[$k].p -gt $maxp){$maxp=$line[$k].p;$maxi=$k} }
    if($b -gt $a){ [void]$ag.peakPos.Add([math]::Round(($maxi-$a)/[double]($b-$a),2)) }
    # period pairing: compare consecutive phrases (antecedent even, consequent odd)
    if($prevLen -ge 0){
      $ag.pairTot++
      if([math]::Abs($plenBeats-$prevLen) -le 0.6){ $ag.pairEq++ }
      # antecedent (prev) suspended (non-tonic) -> consequent (this) resolved (tonic)?
      $ag.antTot++
      if($prevFinalDeg -ne 0 -and $fd -eq 0){ $ag.antSusp++ }
    }
    $prevLen=$plenBeats; $prevFinalDeg=$fd
  }
}

"===== PHRASING over $pieceN melodies =====`n"
$pa=$ag.plen | Sort-Object
"Phrase length (beats): avg={0:N1}  median={1:N1}  (n={2})" -f (($pa|Measure-Object -Average).Average),($pa[[int]($pa.Count/2)]),$pa.Count
"  histogram (beats, rounded):"
$ag.plen | ForEach-Object {[int][math]::Round($_)} | Group-Object | Sort-Object {[int]$_.Name} | ForEach-Object { "    {0,2} beats: {1,4}  {2}" -f $_.Name,$_.Count,('#'*[int]($_.Count*40/$ag.phrTot)) }
"`nPhrase START beat-in-bar (1 = downbeat):"
$ag.startBeat.GetEnumerator()|Sort-Object {[int]$_.Name}|ForEach-Object{ "    beat {0}: {1,4} ({2:N0}%)" -f $_.Name,$_.Value,(100*$_.Value/$ag.phrTot) }
"  -> anacrusis (phrase starts off the downbeat) = {0:N0}%" -f (100*$ag.anacrusis/[math]::Max(1,$ag.phrTot))

"`nPhrase-FINAL degree (cadential note):"
$ft=($ag.finalDeg.Values|Measure-Object -Sum).Sum
$ag.finalDeg.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 7|ForEach-Object{ "    {0,-3} {1,5:N1}%" -f $DNAME[$_.Key],(100*$_.Value/$ft) }
"Phrase-INITIAL degree:"
$it=($ag.initDeg.Values|Measure-Object -Sum).Sum
$ag.initDeg.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 6|ForEach-Object{ "    {0,-3} {1,5:N1}%" -f $DNAME[$_.Key],(100*$_.Value/$it) }

"`nPeriod pairing: consecutive phrases of ~EQUAL length = {0:N0}% (n={1})" -f (100*$ag.pairEq/[math]::Max(1,$ag.pairTot)),$ag.pairTot
"Antecedent->consequent: prev ends NON-tonic AND this ends on TONIC = {0:N0}% of pairs" -f (100*$ag.antSusp/[math]::Max(1,$ag.antTot))
"`nBreath: phrase-final note length avg = {0:N2} beats ; rest gap after phrase avg = {1:N2} beats (n_gaps={2})" -f (($ag.breathLen|Measure-Object -Average).Average),(($ag.restGap|Measure-Object -Average).Average),$ag.restGap.Count
"Contour: melodic PEAK position within phrase (0=start,1=end) avg = {0:N2}" -f (($ag.peakPos|Measure-Object -Average).Average)
