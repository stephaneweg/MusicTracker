# Hisaishi: ACCOMPANIMENT articulation + CANONICALIZED MARKOV chains (melody degrees + chord roots), mode-aware.
# Tonality is canonicalized to scale degrees relative to a robust tonic (bass+melody weighted), so the chains are
# key-independent and reusable in any key per mode. Recurses the whole JoeHisaishi tree (.mid + .mscz).
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
  foreach($t in $tracks){ $tt=$t.GetType(); $isDrum=[bool]$tt.GetField("IsDrum").GetValue($t)
    if($isDrum){continue}
    $arr=New-Object System.Collections.ArrayList
    foreach($n in ($tt.GetField("Notes").GetValue($t))){ $nt=$n.GetType(); [void]$arr.Add([pscustomobject]@{ p=[int]$nt.GetField("Pitch").GetValue($n); s=[int]$nt.GetField("StartSlice").GetValue($n); l=[int]$nt.GetField("LengthSlices").GetValue($n) }) }
    if($arr.Count -gt 0){ [void]$out.Add($arr) }
  }
  return ,$out
}
function Skyline($arr){ $g=$arr|Group-Object s|Sort-Object {[int]$_.Name}; $line=New-Object System.Collections.ArrayList; foreach($grp in $g){ [void]$line.Add(($grp.Group|Sort-Object p -Descending|Select-Object -First 1)) }; return ,$line }
# Robust tonic: argmax of (melodyW + 1.3*bassW + fifth-support + final-note bonus); mode by the 3rd above it.
function TonicMode($tracks){
  $meanByTrack=@(); foreach($t in $tracks){ $meanByTrack+=($t|ForEach-Object{$_.p}|Measure-Object -Average).Average }
  $hiIdx=0;$loIdx=0; for($i=0;$i -lt $tracks.Count;$i++){ if($meanByTrack[$i] -gt $meanByTrack[$hiIdx]){$hiIdx=$i}; if($meanByTrack[$i] -lt $meanByTrack[$loIdx]){$loIdx=$i} }
  $melW=@(0.0)*12; foreach($e in $tracks[$hiIdx]){ $melW[$e.p%12]+=[math]::Max(1,$e.l) }
  $basW=@(0.0)*12; foreach($e in $tracks[$loIdx]){ $basW[$e.p%12]+=[math]::Max(1,$e.l) }
  $sm=($melW|Measure-Object -Sum).Sum; $sb=($basW|Measure-Object -Sum).Sum; if($sm -le 0){$sm=1}; if($sb -le 0){$sb=1}
  $sky=Skyline $tracks[$hiIdx]; $finalPc = if($sky.Count){$sky[$sky.Count-1].p%12}else{-1}
  $score=@(0.0)*12
  for($pc=0;$pc -lt 12;$pc++){ $score[$pc] = $melW[$pc]/$sm + 1.3*$basW[$pc]/$sb + 0.4*($basW[($pc+7)%12]/$sb) ; if($pc -eq $finalPc){$score[$pc]+=0.35} }
  $tonic=0; for($pc=1;$pc -lt 12;$pc++){ if($score[$pc] -gt $score[$tonic]){$tonic=$pc} }
  $minor = $melW[($tonic+3)%12] -gt $melW[($tonic+4)%12]
  return [pscustomobject]@{ tonic=$tonic; minor=$minor; hiIdx=$hiIdx; loIdx=$loIdx }
}

# chord templates (for the chord Markov root)
$TPL=@(@{n="";iv=@(0,4,7);g="triad"},@{n="m";iv=@(0,3,7);g="triad"},@{n="maj7";iv=@(0,4,7,11);g="maj7/9"},@{n="maj9";iv=@(0,2,4,7,11);g="maj7/9"},@{n="7";iv=@(0,4,7,10);g="dom7"},@{n="m7";iv=@(0,3,7,10);g="min7"},@{n="sus4";iv=@(0,5,7);g="sus"},@{n="sus2";iv=@(0,2,7);g="sus"},@{n="add9";iv=@(0,2,4,7);g="add9"},@{n="madd9";iv=@(0,2,3,7);g="add9"},@{n="6";iv=@(0,4,7,9);g="6th"},@{n="m6";iv=@(0,3,7,9);g="6th"},@{n="dim";iv=@(0,3,6);g="dim"})
function IdRoot($w,$bassPc){ $tot=0.0;for($i=0;$i -lt 12;$i++){$tot+=$w[$i]}; if($tot -le 0){return $null}; $bS=-1e9;$bR=0;$bG="triad"
  for($r=0;$r -lt 12;$r++){ foreach($tp in $TPL){ $cov=0.0;$pr=0; foreach($iv in $tp.iv){$pc=($r+$iv)%12;$cov+=$w[$pc]; if($w[$pc] -gt 0){$pr++}}; $sc=$cov*($pr/$tp.iv.Count)-0.55*($tot-$cov); if($r -eq $bassPc){$sc*=1.12}; $sc-=0.04*$tp.iv.Count; if($sc -gt $bS){$bS=$sc;$bR=$r;$bG=$tp.g} } }
  return [pscustomobject]@{root=$bR;grp=$bG} }

# Aggregators
$MmajMel=New-Object 'double[,]' 12,12   # melody degree transitions, major-mode pieces
$MminMel=New-Object 'double[,]' 12,12   # minor-mode pieces
$Mchord =New-Object 'double[,]' 12,12   # chord root-degree transitions (all)
$chordQbyDeg=@{}                         # per-degree quality group counts
$ivPairs=@{ stepRev=0; stepTot=0; leapRev=0; leapTot=0 }   # leap-resolution evidence
# accompaniment articulation
$acc=@{ singleOn=0; totOn=0; dirChg=0; dirTot=0; dur16=0; dur8=0; durOther=0; onsetsPerBeat=New-Object System.Collections.ArrayList; cyclehist=@{}; spanSum=0.0; spanN=0; fillGap=0; fillTot=0 }
$pieces=0; $majN=0; $minN=0

function SnapDur($d){ (3,6,8,12,16,18,24,32,36,48,72,96 | Sort-Object {[math]::Abs($_-$d)})[0] }

$files = Get-ChildItem $dir -Recurse | Where-Object { $_.Extension -match '\.(mid|mscz)$' }
foreach($f in $files){
  try{ $score=LoadScore $f.FullName }catch{ continue }
  $st=$score.GetType()
  $tsN=[int]$st.GetField("TimeSigN").GetValue($score); $tsD=[int]$st.GetField("TimeSigD").GetValue($score)
  $beat = if($tsD -eq 8){36}else{24}
  $tracks=GetTracks $score
  if($tracks.Count -eq 0){ continue }
  $tm=TonicMode $tracks; $tonic=$tm.tonic
  $pieces++; if($tm.minor){$minN++}else{$majN++}
  $mel=Skyline $tracks[$tm.hiIdx]
  # MELODY degree + interval Markov
  for($i=1;$i -lt $mel.Count;$i++){
    $d0=(($mel[$i-1].p-$tonic)%12+12)%12; $d1=(($mel[$i].p-$tonic)%12+12)%12
    if($tm.minor){ $MminMel[$d0,$d1]+=1 } else { $MmajMel[$d0,$d1]+=1 }
  }
  for($i=2;$i -lt $mel.Count;$i++){
    $iv1=$mel[$i-1].p-$mel[$i-2].p; $iv2=$mel[$i].p-$mel[$i-1].p
    if($iv1 -eq 0){continue}
    $rev = ([math]::Sign($iv2) -ne 0 -and [math]::Sign($iv2) -ne [math]::Sign($iv1))
    if([math]::Abs($iv1) -ge 3){ $ivPairs.leapTot++; if($rev){$ivPairs.leapRev++} } else { $ivPairs.stepTot++; if($rev){$ivPairs.stepRev++} }
  }
  # ACCOMPANIMENT articulation (lowest-mean track)
  if($tracks.Count -ge 2){
    $accTrk=$tracks[$tm.loIdx]
    $melOnsets=@{}; foreach($e in $mel){ $melOnsets[$e.s]=$true }
    $grp = $accTrk | Group-Object s | Sort-Object {[int]$_.Name}
    $seqPitch=New-Object System.Collections.ArrayList
    $bars = [double]([math]::Max(1,(($accTrk|ForEach-Object{$_.s+$_.l}|Measure-Object -Maximum).Maximum)/$beat))
    foreach($gr in $grp){
      $acc.totOn++; $cnt=$gr.Group.Count
      if($cnt -eq 1){ $acc.singleOn++; [void]$seqPitch.Add(($gr.Group|Select-Object -First 1).p) }
      else { [void]$seqPitch.Add((($gr.Group|Measure-Object p -Average).Average)) }
      $os=[int]$gr.Name; $acc.fillTot++; if(-not $melOnsets.ContainsKey($os)){$acc.fillGap++}
    }
    foreach($e in $accTrk){ $sd=SnapDur $e.l; if($sd -le 6){$acc.dur16++}elseif($sd -le 12){$acc.dur8++}else{$acc.durOther++} }
    [void]$acc.onsetsPerBeat.Add($grp.Count / [math]::Max(1,$bars))   # $bars is actually the beat count (maxEnd/beat)
    for($i=1;$i -lt $seqPitch.Count;$i++){ $a=$seqPitch[$i]-$seqPitch[$i-1]; if($i -ge 2){ $b=$seqPitch[$i-1]-$seqPitch[$i-2]; if($a -ne 0 -and $b -ne 0){ $acc.dirTot++; if([math]::Sign($a) -ne [math]::Sign($b)){$acc.dirChg++} } } }
    # dominant cycle period k in 2..8 (ostinato length): k maximizing matches pitch[i]==pitch[i+k]
    $bestK=0;$bestM=-1; for($k=2;$k -le 8;$k++){ $m=0;$tt=0; for($i=0;$i+$k -lt $seqPitch.Count;$i++){ $tt++; if([math]::Abs($seqPitch[$i]-$seqPitch[$i+$k]) -le 1){$m++} }; if($tt -gt 8){ $fr=$m/$tt; if($fr -gt $bestM){$bestM=$fr;$bestK=$k} } }
    if($bestK -gt 0){ if(-not $acc.cyclehist.ContainsKey($bestK)){$acc.cyclehist[$bestK]=0}; $acc.cyclehist[$bestK]++ }
    # span per bar (avg)
    $byBar=@{}; foreach($e in $accTrk){ $b=[int]($e.s/$beat); if(-not $byBar.ContainsKey($b)){$byBar[$b]=New-Object System.Collections.ArrayList}; [void]$byBar[$b].Add($e.p) }
    foreach($kv in $byBar.GetEnumerator()){ if($kv.Value.Count -ge 2){ $acc.spanSum += (($kv.Value|Measure-Object -Maximum).Maximum-($kv.Value|Measure-Object -Minimum).Minimum); $acc.spanN++ } }
  }
  # CHORD root-degree Markov (per-beat ID, full texture, merged)
  if($tracks.Count -ge 2){
    $all=New-Object System.Collections.ArrayList; foreach($t in $tracks){ foreach($e in $t){ [void]$all.Add($e) } }
    if($all.Count -le 3000){
      $maxEnd=0; foreach($e in $all){ if($e.s+$e.l -gt $maxEnd){$maxEnd=$e.s+$e.l} }
      $nW=[int][math]::Ceiling($maxEnd/$beat); $prevR=-9; $prevDeg=-9
      for($wi=0;$wi -lt $nW;$wi++){ $t0=$wi*$beat;$t1=$t0+$beat; $w=@(0.0)*12; $bp=-1;$bl=999
        foreach($e in $all){ if($e.s -lt $t1 -and ($e.s+$e.l) -gt $t0){ $ov=[math]::Min($t1,$e.s+$e.l)-[math]::Max($t0,$e.s); if($ov -le 0){continue}; $w[$e.p%12]+=$ov; if($e.p -lt $bl){$bl=$e.p;$bp=$e.p%12} } }
        $c=IdRoot $w $bp; if(-not $c){continue}
        if($c.root -ne $prevR){ $deg=(($c.root-$tonic)%12+12)%12
          if($prevDeg -ge 0){ $Mchord[$prevDeg,$deg]+=1 }
          if(-not $chordQbyDeg.ContainsKey($deg)){$chordQbyDeg[$deg]=@{}}; if(-not $chordQbyDeg[$deg].ContainsKey($c.grp)){$chordQbyDeg[$deg][$c.grp]=0}; $chordQbyDeg[$deg][$c.grp]++
          $prevR=$c.root; $prevDeg=$deg }
      }
    }
  }
}

function TopTrans($M,$title){
  "  $title (top transitions per degree; only degrees with >=3% total weight):"
  $tot=0.0; for($a=0;$a -lt 12;$a++){for($b=0;$b -lt 12;$b++){$tot+=$M[$a,$b]}}
  if($tot -le 0){ "    (none)"; return }
  for($a=0;$a -lt 12;$a++){
    $rs=0.0; for($b=0;$b -lt 12;$b++){$rs+=$M[$a,$b]}
    if($rs/$tot -lt 0.03){ continue }
    $dests=@(); for($b=0;$b -lt 12;$b++){ if($M[$a,$b] -gt 0){ $dests+=[pscustomobject]@{d=$b;p=$M[$a,$b]/$rs} } }
    $top=($dests|Sort-Object p -Descending|Select-Object -First 4|ForEach-Object{ "{0}:{1:N0}%" -f $DNAME[$_.d],(100*$_.p) }) -join "  "
    "    {0,-3} (={1,4:N1}% of notes) -> {2}" -f $DNAME[$a],(100*$rs/$tot),$top
  }
}

"===== CORPUS: $pieces pieces (minor-mode=$minN, major-mode=$majN) ====="
"`n##### MELODY MARKOV (canonicalized scale-degree transitions) #####"
TopTrans $MminMel "MINOR / modal pieces"
TopTrans $MmajMel "MAJOR / modal pieces"
"`nLeap-resolution evidence:"
"  after a STEP: next note reverses direction {0:N0}% of the time" -f (100*$ivPairs.stepRev/[math]::Max(1,$ivPairs.stepTot))
"  after a LEAP(>=m3): next note reverses direction {0:N0}% of the time  (Hisaishi rule: leaps resolve inward)" -f (100*$ivPairs.leapRev/[math]::Max(1,$ivPairs.leapTot))

"`n##### CHORD MARKOV (root-degree transitions, all pieces) #####"
TopTrans $Mchord "Chord roots"
"Chord quality by degree (top group):"
foreach($deg in ($chordQbyDeg.Keys|Sort-Object)){ $h=$chordQbyDeg[$deg]; $tt=($h.Values|Measure-Object -Sum).Sum; $top=($h.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 3|ForEach-Object{"{0}:{1:N0}%" -f $_.Key,(100*$_.Value/$tt)}) -join "  "; "    {0,-3} -> {1}" -f $DNAME[$deg],$top }

"`n##### ACCOMPANIMENT ARTICULATION #####"
"  single-note onsets (arpeggiated) = {0:N0}%   ; multi-note (block) = {1:N0}%" -f (100*$acc.singleOn/[math]::Max(1,$acc.totOn)),(100*($acc.totOn-$acc.singleOn)/[math]::Max(1,$acc.totOn))
"  accompaniment durations: 16th={0:N0}%  8th={1:N0}%  longer={2:N0}%" -f (100*$acc.dur16/[math]::Max(1,($acc.dur16+$acc.dur8+$acc.durOther))),(100*$acc.dur8/[math]::Max(1,($acc.dur16+$acc.dur8+$acc.durOther))),(100*$acc.durOther/[math]::Max(1,($acc.dur16+$acc.dur8+$acc.durOther)))
"  line direction-change rate = {0:N0}% (high=broken/zigzag arpeggio, low=scalar)" -f (100*$acc.dirChg/[math]::Max(1,$acc.dirTot))
"  onsets per beat (density) avg = {0:N2}" -f (($acc.onsetsPerBeat|Measure-Object -Average).Average)
"  avg figure span per bar = {0:N1} semitones" -f ($acc.spanSum/[math]::Max(1,$acc.spanN))
"  onsets falling in a melody REST (fills the gaps) = {0:N0}%" -f (100*$acc.fillGap/[math]::Max(1,$acc.fillTot))
"  dominant ostinato cycle length (notes) histogram:"
$acc.cyclehist.GetEnumerator()|Sort-Object Value -Descending|ForEach-Object{ "      {0}-note cycle: {1} pieces" -f $_.Key,$_.Value }
