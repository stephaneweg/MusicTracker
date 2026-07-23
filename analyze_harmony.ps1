# Harmonic analysis of the MIDI corpora: key detection (Krumhansl-Schmuckler) + per-beat chord (template match)
# -> Roman-numeral degree sequence -> recurring schema (bigrams, approach-to-I, closing cadences, T/S/D flow).
$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
[void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin "NAudio.dll"))
$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $bin "MusicTracker.exe"))
$load = $asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$midiRoot = "C:\Users\swe\source\repos\MusicTracker\midi"

# Krumhansl-Schmuckler key profiles
$KSmaj = 6.35,2.23,3.48,2.33,4.38,4.09,2.52,5.19,2.39,3.66,2.29,2.88
$KSmin = 6.33,2.68,3.52,5.38,2.60,3.53,2.54,4.75,3.98,2.69,3.34,3.17
function Pearson($x,$y){ $n=$x.Count; $mx=($x|Measure-Object -Average).Average; $my=($y|Measure-Object -Average).Average
  $num=0.0;$dx=0.0;$dy=0.0; for($i=0;$i -lt $n;$i++){ $a=$x[$i]-$mx; $b=$y[$i]-$my; $num+=$a*$b; $dx+=$a*$a; $dy+=$b*$b }
  if($dx -le 0 -or $dy -le 0){return -1}; return $num/[math]::Sqrt($dx*$dy) }
function DetectKey($hist){ $best=-2;$bk=0;$bmode=0
  for($k=0;$k -lt 12;$k++){
    $em=@(); $en=@(); for($p=0;$p -lt 12;$p++){ $em+=$KSmaj[(($p-$k)%12+12)%12]; $en+=$KSmin[(($p-$k)%12+12)%12] }
    $cm=Pearson $hist $em; $cn=Pearson $hist $en
    if($cm -gt $best){$best=$cm;$bk=$k;$bmode=0}; if($cn -gt $best){$best=$cn;$bk=$k;$bmode=1}
  }
  return @($bk,$bmode) }

# triad templates relative to root (no augmented: it over-matches dominants/passing sonorities in this repertoire)
$TPL = @{ "maj"=@(0,4,7); "min"=@(0,3,7); "dim"=@(0,3,6) }
function DetectTriad($w){ # w = 12-length weight vector; returns @(root, qualityName, score)
  $bestS=-1e9;$br=0;$bq="maj"
  foreach($r in 0..11){ foreach($q in $TPL.Keys){ $tones=$TPL[$q]
      $on=0.0; foreach($t in $tones){ $on+=$w[(($r+$t)%12)] }
      $off=0.0; for($p=0;$p -lt 12;$p++){ if($tones -notcontains ((($p-$r)%12+12)%12)){ $off+=$w[$p] } }
      $present=0; foreach($t in $tones){ if($w[(($r+$t)%12)] -gt 0){$present++} }
      $score=$on-0.55*$off + 0.001*$present
      if($present -ge 2 -and $score -gt $bestS){$bestS=$score;$br=$r;$bq=$q} } }
  return @($br,$bq,$bestS) }

$degRoman = "I","bII","II","bIII","III","IV","#IV","V","bVI","VI","bVII","VII"
function Label($degree,$q){ $base=$degRoman[$degree]
  switch($q){ "min"{ $base.ToLower() } "dim"{ $base.ToLower()+"o" } "aug"{ $base+"+" } default{ $base } } }
# functional category by degree (T/S/D), works for major & (raised-LT) minor
function Func($degree){ switch($degree){ 0{"T"} 9{"T"} 4{"T"} 3{"T"} 5{"S"} 2{"S"} 7{"D"} 11{"D"} default{"?"} } }

function AnalyzeHarmony($dir,$label){
  $files = Get-ChildItem $dir -Filter *.mid
  $bigrams=@{}; $approachI=@{}; $cadence=@{}; $funcBigrams=@{}; $keyModes=@(0,0); $chordCount=0; $pieces=0
  $trigEndI=@{}
  foreach($f in $files){
    try{ $score=$load.Invoke($null,@($f.FullName)) }catch{ continue }
    $st=$score.GetType(); $tracks=$st.GetField("Tracks").GetValue($score)
    # gather all notes (NB: PowerShell vars are case-insensitive, so array names must DIFFER, not just by case)
    $arrPit=New-Object System.Collections.ArrayList;$arrSta=New-Object System.Collections.ArrayList;$arrEnd=New-Object System.Collections.ArrayList
    $maxE=0
    foreach($tk in $tracks){ $notes=$tk.GetType().GetField("Notes").GetValue($tk)
      foreach($n in $notes){ $nt=$n.GetType(); $np=$nt.GetField("Pitch").GetValue($n);$nstart=$nt.GetField("StartSlice").GetValue($n);$nlen=$nt.GetField("LengthSlices").GetValue($n)
        [void]$arrPit.Add($np);[void]$arrSta.Add($nstart);[void]$arrEnd.Add($nstart+$nlen); if($nstart+$nlen -gt $maxE){$maxE=$nstart+$nlen} } }
    if($arrPit.Count -lt 4){ continue }
    $pieces++
    # key from duration-weighted pc histogram
    $hist=@(0)*12; for($i=0;$i -lt $arrPit.Count;$i++){ $hist[$arrPit[$i]%12]+=($arrEnd[$i]-$arrSta[$i]) }
    $kk=DetectKey $hist; $tonic=$kk[0]; $mode=$kk[1]; $keyModes[$mode]++
    # per-beat chord (window = first half of each beat to favour the on-beat sonority)
    $seq=New-Object System.Collections.ArrayList
    for($bt0=0;$bt0 -lt $maxE;$bt0+=24){
      $w=@(0.0)*12; $a=$bt0; $b=$bt0+12
      for($i=0;$i -lt $arrPit.Count;$i++){ $ov=[math]::Min($arrEnd[$i],$b)-[math]::Max($arrSta[$i],$a); if($ov -gt 0){ $w[$arrPit[$i]%12]+=$ov } }
      $tot=0.0; foreach($x in $w){$tot+=$x}; if($tot -le 0){ continue }
      $tr=DetectTriad $w; $deg=((($tr[0]-$tonic)%12)+12)%12; $lbl=Label $deg $tr[1]
      if($seq.Count -eq 0 -or $seq[$seq.Count-1].lbl -ne $lbl){ [void]$seq.Add([pscustomobject]@{deg=$deg;lbl=$lbl;q=$tr[1]}); $chordCount++ }
    }
    # tally
    for($i=1;$i -lt $seq.Count;$i++){
      $bg="{0} -> {1}" -f $seq[$i-1].lbl,$seq[$i].lbl
      if(-not $bigrams.ContainsKey($bg)){$bigrams[$bg]=0}; $bigrams[$bg]++
      $fb="{0}{1}" -f (Func $seq[$i-1].deg),(Func $seq[$i].deg)
      if(-not $funcBigrams.ContainsKey($fb)){$funcBigrams[$fb]=0}; $funcBigrams[$fb]++
      if($seq[$i].deg -eq 0){ $ap=$seq[$i-1].lbl; if(-not $approachI.ContainsKey($ap)){$approachI[$ap]=0}; $approachI[$ap]++ }
      if($i -ge 2 -and $seq[$i].deg -eq 0){ $tg="{0} {1} {2}" -f $seq[$i-2].lbl,$seq[$i-1].lbl,$seq[$i].lbl; if(-not $trigEndI.ContainsKey($tg)){$trigEndI[$tg]=0}; $trigEndI[$tg]++ }
    }
    if($seq.Count -ge 2){ $cd="{0} -> {1}" -f $seq[$seq.Count-2].lbl,$seq[$seq.Count-1].lbl; if(-not $cadence.ContainsKey($cd)){$cadence[$cd]=0}; $cadence[$cd]++ }
  }
  Write-Output ("==================== HARMONY: {0}  ({1} pieces, {2} chords) ====================" -f $label,$pieces,$chordCount)
  Write-Output ("Detected keys: major={0} minor={1}" -f $keyModes[0],$keyModes[1])
  $bt=($bigrams.Values|Measure-Object -Sum).Sum
  Write-Output "Top chord transitions (degree -> degree):"
  $bigrams.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 12|ForEach-Object{ Write-Output ("  {0,-14} {1,5:N1}%" -f $_.Key,(100.0*$_.Value/$bt)) }
  Write-Output "Approach to I (what resolves to the tonic):"
  $at=($approachI.Values|Measure-Object -Sum).Sum
  $approachI.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 6|ForEach-Object{ Write-Output ("  {0,-6} -> I   {1,5:N1}%" -f $_.Key,(100.0*$_.Value/$at)) }
  Write-Output "Top 3-chord progressions ending on I:"
  $tt=($trigEndI.Values|Measure-Object -Sum).Sum
  $trigEndI.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 6|ForEach-Object{ Write-Output ("  {0,-16} {1,5:N1}%" -f $_.Key,(100.0*$_.Value/$tt)) }
  Write-Output "Closing cadence (last two chords of each piece):"
  $ct=($cadence.Values|Measure-Object -Sum).Sum
  $cadence.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 6|ForEach-Object{ Write-Output ("  {0,-14} {1,5:N1}%" -f $_.Key,(100.0*$_.Value/$ct)) }
  Write-Output "Functional flow (T/S/D transitions):"
  $ft=($funcBigrams.Values|Measure-Object -Sum).Sum
  $funcBigrams.GetEnumerator()|Sort-Object Value -Descending|ForEach-Object{ if($_.Key -notmatch "\?"){ Write-Output ("  {0,-4} {1,5:N1}%" -f $_.Key,(100.0*$_.Value/$ft)) } }
  Write-Output ""
}

$only = if ($args.Count -gt 0) { $args[0] } else { $null }   # optional: analyze just one subfolder
foreach($d in (Get-ChildItem $midiRoot -Directory)){ if (-not $only -or $d.Name -eq $only) { AnalyzeHarmony $d.FullName $d.Name } }
