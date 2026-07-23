# Pattern analysis of the Bach corpora: recurring RHYTHMIC cells (n-grams), melodic CONTOUR shapes (direction
# n-grams + run length), and melodic SEQUENCE/motif repetition. Uses the app's MidiImporter; melody from MONO tracks.
$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
[void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin "NAudio.dll"))
$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $bin "MusicTracker.exe"))
$load = $asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$midiRoot = "C:\Users\swe\source\repos\MusicTracker\midi"
$grid = 3,6,8,12,16,18,24,36,48,72,96
function Snap($d){ ($grid | Sort-Object { [math]::Abs($_-$d) })[0] }
function DN($s){ switch($s){3{"32"}6{"16"}8{"8t"}12{"8"}16{"qt"}18{"8."}24{"q"}36{"q."}48{"h"}72{"h."}96{"w"}default{"$s"} } }

function Analyze($dir,$label){
  $files = Get-ChildItem $dir -Filter *.mid
  $rtri=@{}; $ctri=@{}; $runSum=0.0; $runN=0; $perPieceTop=@(); $seqShareSum=0.0; $seqN=0
  foreach($f in $files){
    try{ $score=$load.Invoke($null,@($f.FullName)) }catch{ continue }
    $tracks=$score.GetType().GetField("Tracks").GetValue($score)
    $pieceRtri=@{}
    foreach($t in $tracks){
      if($t.GetType().GetField("IsDrum").GetValue($t)){continue}
      $notes=$t.GetType().GetField("Notes").GetValue($t); if($notes.Count -lt 4){continue}
      $arr=@(); foreach($n in $notes){ $nt=$n.GetType(); $arr+=[pscustomobject]@{p=$nt.GetField("Pitch").GetValue($n);s=$nt.GetField("StartSlice").GetValue($n);l=$nt.GetField("LengthSlices").GetValue($n)} }
      $arr=$arr|Sort-Object s,p
      # mono check
      $mono=$true;$re=-1; foreach($e in $arr){ if($e.s -lt $re-2){$mono=$false;break}; $en=$e.s+$e.l; if($en -gt $re){$re=$en} }
      if(-not $mono){continue}
      $durs=@(); $pits=@(); foreach($e in $arr){ $durs+=(Snap $e.l); $pits+=$e.p }
      # rhythm trigrams
      for($i=0;$i -le $durs.Count-3;$i++){ $k="{0}-{1}-{2}" -f (DN $durs[$i]),(DN $durs[$i+1]),(DN $durs[$i+2])
        if(-not $rtri.ContainsKey($k)){$rtri[$k]=0}; $rtri[$k]++
        if(-not $pieceRtri.ContainsKey($k)){$pieceRtri[$k]=0}; $pieceRtri[$k]++ }
      # contour symbols + run length
      $sym=@(); for($i=1;$i -lt $pits.Count;$i++){ $d=$pits[$i]-$pits[$i-1]; if($d -gt 0){$sym+="U"}elseif($d -lt 0){$sym+="D"}else{$sym+="S"} }
      for($i=0;$i -le $sym.Count-3;$i++){ $k=$sym[$i]+$sym[$i+1]+$sym[$i+2]; if(-not $ctri.ContainsKey($k)){$ctri[$k]=0}; $ctri[$k]++ }
      # monotonic run lengths (over U/D only)
      $run=0;$last=""
      foreach($x in $sym){ if($x -eq "S"){continue}; if($x -eq $last){$run++} else { if($last -ne ""){ $runSum+=$run; $runN++ }; $last=$x; $run=1 } }
      if($last -ne ""){ $runSum+=$run; $runN++ }
      # melodic sequence: signed-interval 3-grams that repeat within this track
      $ig=@{}; $tot=0
      for($i=0;$i -le $pits.Count-4;$i++){ $a=[math]::Max(-12,[math]::Min(12,$pits[$i+1]-$pits[$i])); $b=[math]::Max(-12,[math]::Min(12,$pits[$i+2]-$pits[$i+1])); $c=[math]::Max(-12,[math]::Min(12,$pits[$i+3]-$pits[$i+2]))
        $k="$a,$b,$c"; if(-not $ig.ContainsKey($k)){$ig[$k]=0}; $ig[$k]++; $tot++ }
      if($tot -gt 0){ $rep=0; foreach($v in $ig.Values){ if($v -ge 2){$rep+=$v} }; $seqShareSum+=($rep/$tot); $seqN++ }
    }
    if($pieceRtri.Count -gt 0){ $mx=($pieceRtri.Values|Measure-Object -Maximum).Maximum; $sm=($pieceRtri.Values|Measure-Object -Sum).Sum; $perPieceTop+=($mx/$sm) }
  }
  Write-Output ("==================== {0} ====================" -f $label)
  $rt=($rtri.Values|Measure-Object -Sum).Sum; if($rt -eq 0){$rt=1}
  Write-Output "Top RHYTHM cells (3 consecutive durations):"
  $rtri.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 6|ForEach-Object{ Write-Output ("   {0,-12} {1,5:N1}%" -f $_.Key,(100*$_.Value/$rt)) }
  if($perPieceTop.Count -gt 0){ Write-Output ("Rhythm self-similarity (avg share of a piece's #1 cell): {0:N0}%" -f (100*($perPieceTop|Measure-Object -Average).Average)) }
  $ct=($ctri.Values|Measure-Object -Sum).Sum; if($ct -eq 0){$ct=1}
  Write-Output "Top CONTOUR shapes (U=up D=down S=same, 3 steps):"
  $ctri.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 6|ForEach-Object{ Write-Output ("   {0,-6} {1,5:N1}%" -f $_.Key,(100*$_.Value/$ct)) }
  if($runN -gt 0){ Write-Output ("Avg monotonic run length (steps before turning): {0:N2}" -f ($runSum/$runN)) }
  if($seqN -gt 0){ Write-Output ("Melodic SEQUENCE/motif repetition (notes in a within-piece repeated interval-motif): {0:N0}%" -f (100*$seqShareSum/$seqN)) }
  Write-Output ""
}
$only = $args
foreach($d in (Get-ChildItem $midiRoot -Directory)){ if(($only.Count -eq 0) -or ($only -contains $d.Name)){ Analyze $d.FullName $d.Name } }
