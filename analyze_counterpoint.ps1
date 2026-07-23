# Counterpoint analysis for "solo + accompaniment": harmonic rhythm, bass-line behaviour, outer-voice (top vs bass)
# vertical intervals + motion type, and a suspension proxy. Samples the full polyphony per beat.
$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
[void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin "NAudio.dll"))
$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $bin "MusicTracker.exe"))
$load = $asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$midiRoot = "C:\Users\swe\source\repos\MusicTracker\midi"
$TPL = @{ "maj"=@(0,4,7); "min"=@(0,3,7); "dim"=@(0,3,6) }
function TriRoot($w){ $bestS=-1e9;$br=-1
  foreach($r in 0..11){ foreach($q in $TPL.Keys){ $tones=$TPL[$q]; $on=0.0; foreach($t in $tones){ $on+=$w[(($r+$t)%12)] }
      $off=0.0; for($p=0;$p -lt 12;$p++){ if($tones -notcontains ((($p-$r)%12+12)%12)){ $off+=$w[$p] } }
      $present=0; foreach($t in $tones){ if($w[(($r+$t)%12)] -gt 0){$present++} }
      $sc=$on-0.55*$off; if($present -ge 2 -and $sc -gt $bestS){$bestS=$sc;$br=$r} } }
  return $br }

function Analyze($dir,$label){
  $files=Get-ChildItem $dir -Filter *.mid
  $bars=0;$chordChanges=0;$beats=0
  $bassStep=0;$bassLeap=0;$bassPairs=0
  $vi=@{};$viTot=0; $contrary=0;$motionPairs=0
  $strong=0;$susp=0
  foreach($f in $files){
    try{ $score=$load.Invoke($null,@($f.FullName)) }catch{ continue }
    $tracks=$score.GetType().GetField("Tracks").GetValue($score)
    $P=New-Object System.Collections.ArrayList;$S=New-Object System.Collections.ArrayList;$E=New-Object System.Collections.ArrayList;$maxE=0
    foreach($tk in $tracks){ if($tk.GetType().GetField("IsDrum").GetValue($tk)){continue}
      foreach($n in $tk.GetType().GetField("Notes").GetValue($tk)){ $nt=$n.GetType(); $pp=$nt.GetField("Pitch").GetValue($n);$ss=$nt.GetField("StartSlice").GetValue($n);$ll=$nt.GetField("LengthSlices").GetValue($n)
        [void]$P.Add($pp);[void]$S.Add($ss);[void]$E.Add($ss+$ll); if($ss+$ll -gt $maxE){$maxE=$ss+$ll} } }
    if($P.Count -lt 8){ continue }
    # per-beat samples: sounding pitches in [t, t+8)
    $prevRoot=-99;$prevTop=$null;$prevBot=$null;$prevTopVi=-1
    for($t=0;$t -lt $maxE;$t+=24){
      $w=@(0)*12; $sound=New-Object System.Collections.ArrayList
      for($i=0;$i -lt $P.Count;$i++){ if($S[$i] -lt $t+8 -and $E[$i] -gt $t){ $w[$P[$i]%12]+=1; [void]$sound.Add($P[$i]) } }
      if($sound.Count -eq 0){ continue }
      $beats++
      $top=($sound|Measure-Object -Maximum).Maximum; $bot=($sound|Measure-Object -Minimum).Minimum
      $root=TriRoot $w
      if($prevRoot -ne -99){ if($root -ne $prevRoot){$chordChanges++} }
      $prevRoot=$root
      # bass behaviour
      if($prevBot -ne $null){ $d=[math]::Abs($bot-$prevBot); $bassPairs++; if($d -le 2){$bassStep++}elseif($d -gt 0){$bassLeap++} }
      # outer-voice vertical interval (top-bot mod12) + motion type
      $ivl=(($top-$bot)%12+12)%12; if(-not $vi.ContainsKey($ivl)){$vi[$ivl]=0}; $vi[$ivl]++; $viTot++
      if($prevTop -ne $null){ $dt=$top-$prevTop;$db=$bot-$prevBot; if($dt -ne 0 -or $db -ne 0){ $motionPairs++; if([math]::Sign($dt) -ne [math]::Sign($db) -and $dt -ne 0 -and $db -ne 0){$contrary++} } }
      # suspension proxy: top dissonant with bass on the beat, resolves DOWN by step next beat
      $isStrong = ($t % 48) -eq 0   # ~ a "strong" beat every half-bar in 4/4 sampling
      if($isStrong){ $strong++; $cls=$ivl; $diss = @(1,2,5,6,10,11) -contains $cls; if($diss -and $prevTop -ne $null){} }
      # (suspension counted below using next-beat info via prevTop tracking)
      if($prevTopVi -ge 0 -and $prevTop -ne $null){ $pcls=$prevTopVi; $pdiss=@(1,2,5,6,10,11) -contains $pcls; if($pdiss -and ($top-$prevTop) -ge -2 -and ($top-$prevTop) -lt 0){ $susp++ } }
      $prevTopVi=$ivl; $prevTop=$top;$prevBot=$bot
    }
    $bars += [math]::Floor($maxE/96)
  }
  Write-Output ("==== {0} ====" -f $label)
  if($beats -gt 0){ Write-Output ("  harmonic rhythm: chord changes on {0:N0}% of beats (~{1:N1} changes/bar in 4/4)" -f (100*$chordChanges/$beats), (4.0*$chordChanges/$beats)) }
  if($bassPairs -gt 0){ Write-Output ("  bass line: stepwise(<=M2)={0:N0}%  leaps={1:N0}%" -f (100*$bassStep/$bassPairs),(100*$bassLeap/$bassPairs)) }
  if($viTot -gt 0){
    $th=0;$sx=0;$oct=0;$fi=0;$ds=0
    foreach($k in $vi.Keys){ $c=$vi[$k]; if($k -eq 3 -or $k -eq 4){$th+=$c}elseif($k -eq 8 -or $k -eq 9){$sx+=$c}elseif($k -eq 0){$oct+=$c}elseif($k -eq 7){$fi+=$c}elseif(@(1,2,5,6,10,11) -contains $k){$ds+=$c} }
    Write-Output ("  outer-voice intervals: 3rds={0:N0}% 6ths={1:N0}% oct/unis={2:N0}% 5th={3:N0}% dissonant={4:N0}%" -f (100*$th/$viTot),(100*$sx/$viTot),(100*$oct/$viTot),(100*$fi/$viTot),(100*$ds/$viTot)) }
  if($motionPairs -gt 0){ Write-Output ("  contrary motion (top vs bass): {0:N0}%" -f (100*$contrary/$motionPairs)) }
  if($strong -gt 0){ Write-Output ("  suspension proxy (dissonant beat resolving down by step): {0:N0}% of beats" -f (100*$susp/$beats)) }
  Write-Output ""
}
$only=$args
foreach($d in (Get-ChildItem $midiRoot -Directory)){ if(($only.Count -eq 0) -or ($only -contains $d.Name)){ Analyze $d.FullName $d.Name } }
