# Hisaishi HARMONY analysis: per-beat chord ID from the full texture, progression in scale degrees,
# chord-quality colour, harmonic rhythm, root motion, bass line (pedal / descending), royal-road detection.
$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm  = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$midiLoad = $asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$mszLoad  = $asm.GetType("MusicTracker.Engine.MuseScoreImporter").GetMethod("Load")
$dir = "C:\Users\swe\source\repos\MusicTracker\musescore\JoeHisaishi"
$KSMaj = 6.35,2.23,3.48,2.33,4.38,4.09,2.52,5.19,2.39,3.66,2.29,2.88
$KSMin = 6.33,2.68,3.52,5.38,2.60,3.53,2.54,4.75,3.98,2.69,3.34,3.17
$PCN = "C","Db","D","Eb","E","F","Gb","G","Ab","A","Bb","B"

function LoadScore($file){ $ext=[System.IO.Path]::GetExtension($file).ToLower(); if($ext -eq ".mid"){$midiLoad.Invoke($null,@([string]$file))}else{$mszLoad.Invoke($null,@([string]$file))} }
function AllNotes($score){
  $tracks=$score.GetType().GetField("Tracks").GetValue($score)
  $out=New-Object System.Collections.ArrayList
  foreach($t in $tracks){ foreach($n in ($t.GetType().GetField("Notes").GetValue($t))){ $nt=$n.GetType(); [void]$out.Add([pscustomobject]@{ p=[int]$nt.GetField("Pitch").GetValue($n); s=[int]$nt.GetField("StartSlice").GetValue($n); l=[int]$nt.GetField("LengthSlices").GetValue($n) }) } }
  return ,$out
}
function Corr($a,$b){ $n=$a.Count;$ma=($a|Measure-Object -Average).Average;$mb=($b|Measure-Object -Average).Average;$num=0.0;$da=0.0;$db=0.0;for($i=0;$i -lt $n;$i++){$x=$a[$i]-$ma;$y=$b[$i]-$mb;$num+=$x*$y;$da+=$x*$x;$db+=$y*$y};if($da -le 0 -or $db -le 0){return -1};$num/[math]::Sqrt($da*$db) }
function DetectKey($hist){ $best=-2.0;$bk=0;$bm=0; for($r=0;$r -lt 12;$r++){ $rot=@();for($i=0;$i -lt 12;$i++){$rot+=$hist[($i+$r)%12]}; $cM=Corr $rot $KSMaj;$cm=Corr $rot $KSMin; if($cM -gt $best){$best=$cM;$bk=$r;$bm=0};if($cm -gt $best){$best=$cm;$bk=$r;$bm=1} }; [pscustomobject]@{tonic=$bk;minor=($bm -eq 1)} }

# chord templates (name -> interval set from root) and colour group
$TPL = @(
  @{n="";iv=@(0,4,7);g="triad"}, @{n="m";iv=@(0,3,7);g="triad"},
  @{n="dim";iv=@(0,3,6);g="dim/aug"}, @{n="aug";iv=@(0,4,8);g="dim/aug"},
  @{n="maj7";iv=@(0,4,7,11);g="maj7/9"}, @{n="maj9";iv=@(0,2,4,7,11);g="maj7/9"},
  @{n="7";iv=@(0,4,7,10);g="dom7/9"}, @{n="9";iv=@(0,2,4,7,10);g="dom7/9"},
  @{n="m7";iv=@(0,3,7,10);g="min7"}, @{n="m9";iv=@(0,2,3,7,10);g="min7"},
  @{n="m7b5";iv=@(0,3,6,10);g="dim/aug"}, @{n="mMaj7";iv=@(0,3,7,11);g="min7"},
  @{n="sus4";iv=@(0,5,7);g="sus"}, @{n="sus2";iv=@(0,2,7);g="sus"},
  @{n="add9";iv=@(0,2,4,7);g="add9"}, @{n="madd9";iv=@(0,2,3,7);g="add9"},
  @{n="6";iv=@(0,4,7,9);g="6th"}, @{n="m6";iv=@(0,3,7,9);g="6th"}
)
function IdChord($w,$bassPc){
  $tot=0.0; for($i=0;$i -lt 12;$i++){$tot+=$w[$i]}; if($tot -le 0){return $null}
  $bestS=-1e9;$bestR=0;$bestT=$TPL[0]
  for($r=0;$r -lt 12;$r++){
    foreach($tp in $TPL){
      $cov=0.0;$present=0
      foreach($iv in $tp.iv){ $pc=($r+$iv)%12; $cov+=$w[$pc]; if($w[$pc] -gt 0){$present++} }
      $extra=$tot-$cov
      $covFrac=$present/$tp.iv.Count   # how complete the chord is
      $score=$cov*$covFrac - 0.55*$extra
      if($r -eq $bassPc){$score*=1.12}        # prefer root in the bass
      $score -= 0.04*$tp.iv.Count             # slight simplicity bias
      if($score -gt $bestS){$bestS=$score;$bestR=$r;$bestT=$tp}
    }
  }
  [pscustomobject]@{ root=$bestR; name=$bestT.n; grp=$bestT.g; bass=$bassPc }
}
function DegName($d){ @{0="I";1="bII";2="II";3="bIII";4="III";5="IV";6="#IV";7="V";8="bVI";9="VI";10="bVII";11="VII"}[$d] }

$agg=@{ grp=@{}; rootmot=@{}; hr=New-Object System.Collections.ArrayList; royal=0; pieces=0; slashShare=0.0; slashN=0 }

function AnalyzePiece($file,$label){
  $score=LoadScore $file
  $st=$score.GetType()
  $tsN=[int]$st.GetField("TimeSigN").GetValue($score); $tsD=[int]$st.GetField("TimeSigD").GetValue($score)
  $beat = if($tsD -eq 8){36}else{24}
  $barBeats = if($tsD -eq 8){[int]($tsN/3)}else{$tsN}
  $notes=AllNotes $score
  if($notes.Count -lt 12){ return }
  $maxEnd=0; foreach($e in $notes){ if($e.s+$e.l -gt $maxEnd){$maxEnd=$e.s+$e.l} }
  $hist=@(0)*12; foreach($e in $notes){ $hist[$e.p%12]+=[math]::Max(1,$e.l) }
  $key=DetectKey $hist; $tonic=$key.tonic
  # bucket notes by beat-window for speed
  $nWin=[int][math]::Ceiling($maxEnd/$beat)
  if($nWin -lt 1){return}
  $chords=New-Object System.Collections.ArrayList
  for($wi=0;$wi -lt $nWin;$wi++){
    $t0=$wi*$beat; $t1=$t0+$beat
    $w=@(0.0)*12; $bassPc=-1; $bassPitch=999
    foreach($e in $notes){
      if($e.s -lt $t1 -and ($e.s+$e.l) -gt $t0){
        $ov=[math]::Min($t1,$e.s+$e.l)-[math]::Max($t0,$e.s); if($ov -le 0){continue}
        $w[$e.p%12]+=$ov
        if($e.p -lt $bassPitch){$bassPitch=$e.p;$bassPc=$e.p%12}
      }
    }
    $c=IdChord $w $bassPc
    if($c){ [void]$chords.Add($c) }
    else { [void]$chords.Add($null) }
  }
  # merge consecutive identical (root+name)
  $prog=New-Object System.Collections.ArrayList
  $prev=$null;$dur=0
  foreach($c in $chords){
    $kk = if($c){"$($c.root)/$($c.name)"}else{"-"}
    if($c -and $prev -and $kk -eq $prev.kk){ $prev.dur++ }
    else { if($prev){[void]$prog.Add($prev)}; $prev=[pscustomobject]@{kk=$kk;c=$c;dur=1} }
  }
  if($prev){[void]$prog.Add($prev)}
  $changes=($prog | Where-Object { $_.c }).Count
  $bars=[math]::Max(1,$maxEnd/($beat*$barBeats))
  [void]$agg.hr.Add($changes/$bars)
  $agg.pieces++

  # quality colour + root motion + slash + degrees sequence
  $degSeq=New-Object System.Collections.ArrayList
  $prevRoot=-1; $slash=0; $tot=0
  foreach($pp in $prog){ if(-not $pp.c){continue}; $c=$pp.c; $tot++
    if(-not $agg.grp.ContainsKey($c.grp)){$agg.grp[$c.grp]=0}; $agg.grp[$c.grp]++
    if($c.bass -ne $c.root){$slash++}
    if($prevRoot -ge 0){ $mot=(($c.root-$prevRoot)%12+12)%12; if(-not $agg.rootmot.ContainsKey($mot)){$agg.rootmot[$mot]=0}; $agg.rootmot[$mot]++ }
    $prevRoot=$c.root
    [void]$degSeq.Add( ((($c.root-$tonic)%12+12)%12) )
  }
  if($tot -gt 0){ $agg.slashShare += $slash; $agg.slashN += $tot }
  # royal-road 4-5-3-6 (IV V iii vi) scan over degrees
  for($i=0;$i+3 -lt $degSeq.Count;$i++){ if($degSeq[$i] -eq 5 -and $degSeq[$i+1] -eq 7 -and $degSeq[$i+2] -eq 4 -and $degSeq[$i+3] -eq 9){ $agg.royal++ } }

  # per-piece: key, harmonic rhythm, first 20 chords as degree+quality
  $kn=$PCN[$tonic]+$(if($key.minor){"m"}else{""})
  $seq = ($prog | Where-Object {$_.c} | Select-Object -First 22 | ForEach-Object { (DegName ((($_.c.root-$tonic)%12+12)%12)) + $_.c.name }) -join " "
  "  [{0}] key~{1}  hr={2:N1} ch/bar  ({3} changes / {4:N0} bars)" -f $label,$kn,($changes/$bars),$changes,$bars
  "       $seq"
}

"##### HARMONY (pieces with >=2 voices) #####"
$files = Get-ChildItem $dir | Where-Object { $_.Extension -match '\.(mid|mscz)$' } | Sort-Object Extension, Name
foreach($f in $files){
  try{ $sc=LoadScore $f.FullName }catch{ continue }
  $tr=$sc.GetType().GetField("Tracks").GetValue($sc)
  $poly = ($tr | Where-Object { $_.GetType().GetField("Notes").GetValue($_).Count -gt 0 }).Count -ge 2
  if(-not $poly){ continue }
  try{ AnalyzePiece $f.FullName $f.Name }catch{ "  ERR $($f.Name): $($_.Exception.Message)" }
}

"`n##### AGGREGATE HARMONY #####"
"pieces: $($agg.pieces)"
"Harmonic rhythm: avg {0:N2} chord-changes/bar (median {1:N2})" -f (($agg.hr|Measure-Object -Average).Average),(($agg.hr|Sort-Object)[[int]($agg.hr.Count/2)])
"Chord-quality colour (share of chord events):"
$gt=($agg.grp.Values|Measure-Object -Sum).Sum
$agg.grp.GetEnumerator()|Sort-Object Value -Descending|ForEach-Object{ "    {0,-9} {1,5:N1}%" -f $_.Key,(100*$_.Value/$gt) }
"Slash/inversion (bass != root): {0:N1}% of chords" -f (100*$agg.slashShare/[math]::Max(1,$agg.slashN))
"Root motion (semitones up to next root):"
$rt=($agg.rootmot.Values|Measure-Object -Sum).Sum
$rmName=@{0="same";1="+m2";2="+M2";3="+m3";4="+M3";5="+P4(=down5)";6="+TT";7="+P5(=down4)";8="+m6";9="+M6";10="+m7";11="+M7"}
$agg.rootmot.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 8|ForEach-Object{ "    {0,-12} {1,5:N1}%" -f $rmName[$_.Key],(100*$_.Value/$rt) }
"Royal-road IV-V-iii-vi occurrences (exact): $($agg.royal)"
