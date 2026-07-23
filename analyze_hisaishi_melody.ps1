# Hisaishi MELODY/RHYTHM/DYNAMICS analysis (key, intervals, contour, motif, phrasing, scale degrees, pentatonic).
$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm  = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$midiLoad = $asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$mszLoad  = $asm.GetType("MusicTracker.Engine.MuseScoreImporter").GetMethod("Load")
$dir = "C:\Users\swe\source\repos\MusicTracker\musescore\JoeHisaishi"

$KSMaj = 6.35,2.23,3.48,2.33,4.38,4.09,2.52,5.19,2.39,3.66,2.29,2.88
$KSMin = 6.33,2.68,3.52,5.38,2.60,3.53,2.54,4.75,3.98,2.69,3.34,3.17
$PCNAMES = "C","C#","D","Eb","E","F","F#","G","Ab","A","Bb","B"

function LoadScore($file) {
  $ext = [System.IO.Path]::GetExtension($file).ToLower()
  if ($ext -eq ".mid") { return $midiLoad.Invoke($null, @([string]$file)) } else { return $mszLoad.Invoke($null, @([string]$file)) }
}
function GetTracks($score) {
  $tracks = $score.GetType().GetField("Tracks").GetValue($score)
  $out = New-Object System.Collections.ArrayList
  foreach ($t in $tracks) {
    $notes = $t.GetType().GetField("Notes").GetValue($t)
    $arr = New-Object System.Collections.ArrayList
    foreach ($n in $notes) { $nt=$n.GetType(); [void]$arr.Add([pscustomobject]@{ p=[int]$nt.GetField("Pitch").GetValue($n); s=[int]$nt.GetField("StartSlice").GetValue($n); l=[int]$nt.GetField("LengthSlices").GetValue($n); v=[int]$nt.GetField("Velocity").GetValue($n) }) }
    [void]$out.Add($arr)
  }
  return ,$out
}
function Corr($a,$b){ $n=$a.Count; $ma=($a|Measure-Object -Average).Average; $mb=($b|Measure-Object -Average).Average; $num=0.0;$da=0.0;$db=0.0; for($i=0;$i -lt $n;$i++){ $x=$a[$i]-$ma;$y=$b[$i]-$mb; $num+=$x*$y;$da+=$x*$x;$db+=$y*$y }; if($da -le 0 -or $db -le 0){return -1}; return $num/[math]::Sqrt($da*$db) }
function DetectKey($hist){
  $best=-2.0; $bk=0; $bm=0
  for($r=0;$r -lt 12;$r++){
    $rot=@(); for($i=0;$i -lt 12;$i++){ $rot+=$hist[($i+$r)%12] }
    $cM=Corr $rot $KSMaj; $cm=Corr $rot $KSMin
    if($cM -gt $best){$best=$cM;$bk=$r;$bm=0}; if($cm -gt $best){$best=$cm;$bk=$r;$bm=1}
  }
  return [pscustomobject]@{ tonic=$bk; minor=($bm -eq 1); score=$best }
}
# Skyline of a track: top pitch per distinct onset (melody proxy from a poly RH).
function Skyline($arr){
  if($arr.Count -eq 0){ return @() }
  $g = $arr | Group-Object s | Sort-Object { [int]$_.Name }
  $line = New-Object System.Collections.ArrayList
  foreach($grp in $g){ $top=$grp.Group | Sort-Object p -Descending | Select-Object -First 1; [void]$line.Add($top) }
  return ,$line
}
function IvName($i){ switch($i){0{"uni"}1{"m2"}2{"M2"}3{"m3"}4{"M3"}5{"P4"}6{"TT"}7{"P5"}8{"m6"}9{"M6"}10{"m7"}11{"M7"}12{"oct"}default{">oct"} } }
function SnapDur($d){ (3,6,8,12,16,18,24,32,36,48,72,96 | Sort-Object { [math]::Abs($_ - $d) })[0] }
function DurName($s){ switch($s){3{"32"}6{"16"}8{"8T"}12{"8"}16{"qT"}18{"d8"}24{"q"}32{"hT"}36{"dq"}48{"h"}72{"dh"}96{"w"}default{"$s"} } }

# Aggregators
$agg = @{ iv=@{}; deg=@{}; degMaj=@{}; degMin=@{}; mdur=@{}; adur=@{}; vel=New-Object System.Collections.ArrayList; phr=New-Object System.Collections.ArrayList;
  step=0; leap=0; rep=0; dirChg=0; dirTot=0; motif3hits=0; motif3tot=0; pieces=0; majPieces=0; minPieces=0 }

function AnalyzeMelodyPiece($file, $label, $isMidi) {
  try { $score = LoadScore $file } catch { "  FAIL $label"; return }
  $st=$score.GetType()
  $tsN=[int]$st.GetField("TimeSigN").GetValue($score); $tsD=[int]$st.GetField("TimeSigD").GetValue($score)
  $beatSlices = if($tsD -eq 8){ 36 } else { 24 }   # dotted-quarter beat in compound, else quarter
  $tracks = GetTracks $score
  if($tracks.Count -eq 0){ return }
  # full-texture histogram for key
  $hist=@(0)*12
  foreach($tr in $tracks){ foreach($e in $tr){ $hist[$e.p%12] += [math]::Max(1,$e.l) } }
  $key = DetectKey $hist
  $tonic=$key.tonic
  # melody track = highest mean pitch
  $best=-1; $bestMean=-1
  for($i=0;$i -lt $tracks.Count;$i++){ if($tracks[$i].Count -eq 0){continue}; $m=($tracks[$i]|ForEach-Object{$_.p}|Measure-Object -Average).Average; if($m -gt $bestMean){$bestMean=$m;$best=$i} }
  if($best -lt 0){ return }
  $mel = Skyline $tracks[$best]
  if($mel.Count -lt 4){ return }
  $agg.pieces++

  # melody durations + LOCAL scale-degree weights (relative to tonic) + velocity
  $locDeg=@(0.0)*12
  foreach($e in $mel){ $d=SnapDur $e.l; if(-not $agg.mdur.ContainsKey($d)){$agg.mdur[$d]=0}; $agg.mdur[$d]++
    $dg=(($e.p - $tonic)%12+12)%12; $locDeg[$dg]+=[math]::Max(1,$e.l)
    [void]$agg.vel.Add($e.v) }
  # decide mode by the THIRD (K-S mislabels pentatonic music as relative-major): minor if b3 outweighs 3
  $pieceMinor = $locDeg[3] -gt $locDeg[4]
  if($pieceMinor){$agg.minPieces++} else {$agg.majPieces++}
  for($dg=0;$dg -lt 12;$dg++){
    if(-not $agg.deg.ContainsKey($dg)){$agg.deg[$dg]=0.0}; $agg.deg[$dg]+=$locDeg[$dg]
    if($pieceMinor){ if(-not $agg.degMin.ContainsKey($dg)){$agg.degMin[$dg]=0.0}; $agg.degMin[$dg]+=$locDeg[$dg] }
    else { if(-not $agg.degMaj.ContainsKey($dg)){$agg.degMaj[$dg]=0.0}; $agg.degMaj[$dg]+=$locDeg[$dg] }
  }
  # accompaniment durations (all non-melody tracks)
  for($i=0;$i -lt $tracks.Count;$i++){ if($i -eq $best){continue}; foreach($e in $tracks[$i]){ $d=SnapDur $e.l; if(-not $agg.adur.ContainsKey($d)){$agg.adur[$d]=0}; $agg.adur[$d]++ } }

  # intervals + contour + motif (3-gram of deltas)
  $deltas=New-Object System.Collections.ArrayList
  for($i=1;$i -lt $mel.Count;$i++){
    $iv=$mel[$i].p - $mel[$i-1].p; $a=[math]::Abs($iv)
    [void]$deltas.Add($iv)
    $ivk=[math]::Min($a,13); if(-not $agg.iv.ContainsKey($ivk)){$agg.iv[$ivk]=0}; $agg.iv[$ivk]++
    if($a -eq 0){$agg.rep++} elseif($a -le 2){$agg.step++} else {$agg.leap++}
  }
  # contour direction changes
  for($i=1;$i -lt $deltas.Count;$i++){ if($deltas[$i] -ne 0 -and $deltas[$i-1] -ne 0){ $agg.dirTot++; if([math]::Sign($deltas[$i]) -ne [math]::Sign($deltas[$i-1])){$agg.dirChg++} } }
  # motif: repeated 3-gram of (clamped) deltas
  $seq=@(); foreach($d in $deltas){ $seq+=[math]::Max(-12,[math]::Min(12,$d)) }
  $grams=@{}
  for($i=0;$i+2 -lt $seq.Count;$i++){ $k="$($seq[$i]),$($seq[$i+1]),$($seq[$i+2])"; if(-not $grams.ContainsKey($k)){$grams[$k]=0}; $grams[$k]++ }
  foreach($i in 0..([math]::Max(0,$seq.Count-3))){ if($i+2 -lt $seq.Count){ $k="$($seq[$i]),$($seq[$i+1]),$($seq[$i+2])"; $agg.motif3tot++; if($grams[$k] -ge 2){$agg.motif3hits++} } }

  # phrase lengths: gap >= 1 beat in the melody, or a long held note (>= 1.5 beats)
  $phrStart=$mel[0].s
  for($i=1;$i -lt $mel.Count;$i++){
    $gap = $mel[$i].s - ($mel[$i-1].s + $mel[$i-1].l)
    $long = $mel[$i-1].l -ge [int]($beatSlices*1.5)
    if($gap -ge $beatSlices -or $long){ $plen=($mel[$i-1].s + $mel[$i-1].l - $phrStart)/[double]$beatSlices; if($plen -ge 1){[void]$agg.phr.Add($plen)}; $phrStart=$mel[$i].s }
  }

  # per-piece short line
  $kn = $PCNAMES[$tonic] + $(if($key.minor){"m"}else{""})
  $itot=($deltas | ForEach-Object {[math]::Min([math]::Abs($_),13)} | Group-Object).Count
  $s2=0;$l2=0;$r2=0; foreach($d in $deltas){$a=[math]::Abs($d); if($a -eq 0){$r2++}elseif($a -le 2){$s2++}else{$l2++}}
  $dt=$deltas.Count; if($dt -eq 0){$dt=1}
  "  {0,-46} key={1,-4} mel={2,4}n  step={3,3}% leap={4,3}% rep={5,2}%" -f $label,$kn,$mel.Count,[int](100*$s2/$dt),[int](100*$l2/$dt),[int](100*$r2/$dt)
}

"##### MELODY (per piece) #####"
$files = Get-ChildItem $dir | Where-Object { $_.Extension -match '\.(mid|mscz)$' } | Sort-Object Extension, Name
foreach($f in $files){ AnalyzeMelodyPiece $f.FullName $f.Name ($f.Extension -eq ".mid") }

"`n##### AGGREGATE (all pieces) #####"
"pieces analyzed: $($agg.pieces)"
$it=($agg.iv.Values|Measure-Object -Sum).Sum
"Melodic motion:  step(<=M2)={0:N1}%  leap(>=m3)={1:N1}%  repeat={2:N1}%" -f (100*$agg.step/$it),(100*$agg.leap/$it),(100*$agg.rep/$it)
"Top melodic intervals:"
$agg.iv.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 9|ForEach-Object{ "    {0,-5} {1,5:N1}%" -f (IvName $_.Key),(100*$_.Value/$it) }
"Contour: direction changes = {0:N1}% of adjacent interval pairs (high=wavy/balanced, low=long runs)" -f (100*$agg.dirChg/[math]::Max(1,$agg.dirTot))
"Motif: 3-note interval-cells that recur (>=2x) = {0:N1}% of cells" -f (100*$agg.motif3hits/[math]::Max(1,$agg.motif3tot))
$pa=$agg.phr
if($pa.Count){ "Phrase length (beats): avg={0:N1} median={1:N1} (n={2})  histogram:" -f (($pa|Measure-Object -Average).Average),(($pa|Sort-Object)[[int]($pa.Count/2)]),$pa.Count
  $pa | ForEach-Object {[math]::Round($_)} | Group-Object | Sort-Object {[int]$_.Name} | ForEach-Object { "    {0,2} beats: {1}" -f $_.Name,$_.Count } }

$degNames=@{0="1";1="b2";2="2";3="b3";4="3";5="4";6="#4";7="5";8="b6";9="6";10="b7";11="7"}
"`nMode split (by 3rd): minor-leaning pieces = $($agg.minPieces) ; major-leaning = $($agg.majPieces)"
function ShowDeg($h,$title){
  $tot=($h.Values|Measure-Object -Sum).Sum; if(-not $tot){ "  ($($title): none)"; return }
  "  $title (duration-weighted scale degrees):"
  0..11 | ForEach-Object { $w=if($h.ContainsKey($_)){$h[$_]}else{0}; "      {0,-3} {1,5:N1}%" -f $degNames[$_],(100*$w/$tot) }
}
ShowDeg $agg.degMin "MINOR-leaning pieces"
$tmin=($agg.degMin.Values|Measure-Object -Sum).Sum
if($tmin){ $mp=0; foreach($d in 0,3,5,7,10){if($agg.degMin.ContainsKey($d)){$mp+=$agg.degMin[$d]}}; $av=0; foreach($d in 1,6,11){if($agg.degMin.ContainsKey($d)){$av+=$agg.degMin[$d]}}
  "      -> minor-pentatonic (1,b3,4,5,b7) = {0:N1}% ; leading-tone(7)+b2+#4 = {1:N1}%" -f (100*$mp/$tmin),(100*$av/$tmin) }
ShowDeg $agg.degMaj "MAJOR-leaning pieces"
$tmaj=($agg.degMaj.Values|Measure-Object -Sum).Sum
if($tmaj){ $mp=0; foreach($d in 0,2,4,7,9){if($agg.degMaj.ContainsKey($d)){$mp+=$agg.degMaj[$d]}}; $av=0; foreach($d in 5,11){if($agg.degMaj.ContainsKey($d)){$av+=$agg.degMaj[$d]}}
  "      -> major-pentatonic (1,2,3,5,6) = {0:N1}% ; 4+7 (avoided) = {1:N1}%" -f (100*$mp/$tmaj),(100*$av/$tmaj) }

"`nRhythm - MELODY (note-duration share):"
$mt=($agg.mdur.Values|Measure-Object -Sum).Sum
$agg.mdur.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 8|ForEach-Object{ "    {0,-3} {1,5:N1}%" -f (DurName $_.Key),(100*$_.Value/$mt) }
"Rhythm - ACCOMPANIMENT (note-duration share):"
$at=($agg.adur.Values|Measure-Object -Sum).Sum
if($at){ $agg.adur.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 8|ForEach-Object{ "    {0,-3} {1,5:N1}%" -f (DurName $_.Key),(100*$_.Value/$at) } }
"`nDynamics (velocity): min={0} max={1} mean={2:N0} (range={3})" -f (($agg.vel|Measure-Object -Minimum).Minimum),(($agg.vel|Measure-Object -Maximum).Maximum),(($agg.vel|Measure-Object -Average).Average),(($agg.vel|Measure-Object -Maximum).Maximum-($agg.vel|Measure-Object -Minimum).Minimum)
