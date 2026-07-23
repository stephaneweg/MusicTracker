$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
foreach ($dll in (Get-ChildItem $bin -Filter "NAudio*.dll")) { [void][System.Reflection.Assembly]::LoadFrom($dll.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $bin "MusicTracker.exe"))
$load=$asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$ks=$asm.GetType("MusicTracker.Engine.Score.KeySig"); $detect=$ks.GetMethod("Detect"); $hashT=[System.Collections.Generic.HashSet[int]]
$mt=$asm.GetType("MusicTracker.Engine.Flow.MusicTheory")
$TPL=@{ "maj"=@(0,4,7); "min"=@(0,3,7); "dim"=@(0,3,6) }
function Tri($w){ $bs=-1e9;$br=-1; foreach($r in 0..11){ foreach($q in $TPL.Keys){ $t=$TPL[$q]; $on=0.0; foreach($x in $t){$on+=$w[(($r+$x)%12)]}; $off=0.0; for($p=0;$p -lt 12;$p++){ if($t -notcontains ((($p-$r)%12+12)%12)){$off+=$w[$p]} }; $pr=0; foreach($x in $t){ if($w[(($r+$x)%12)] -gt 0){$pr++} }; $sc=$on-0.55*$off; if($pr -ge 2 -and $sc -gt $bs){$bs=$sc;$br=$r} } }; return $br }

$f="C:\Users\swe\source\repos\MusicTracker\midi\_exclass2\ex_class2_c.mid"
$score=$load.Invoke($null,@([string]$f)); $tracks=$score.GetType().GetField("Tracks").GetValue($score)
$P=New-Object System.Collections.ArrayList;$S=New-Object System.Collections.ArrayList;$E=New-Object System.Collections.ArrayList;$maxE=0
foreach($t in $tracks){ foreach($n in $t.GetType().GetField("Notes").GetValue($t)){ $nt=$n.GetType(); $np=$nt.GetField("Pitch").GetValue($n);$ns=$nt.GetField("StartSlice").GetValue($n);$nl=$nt.GetField("LengthSlices").GetValue($n); [void]$P.Add($np);[void]$S.Add($ns);[void]$E.Add($ns+$nl); if($ns+$nl -gt $maxE){$maxE=$ns+$nl} } }
$wg=[double[]]::new(12); $low=[int]::MaxValue;$flp=-1; for($i=0;$i -lt $P.Count;$i++){ $wg[$P[$i]%12]+=($E[$i]-$S[$i]); if($P[$i] -lt $low){$low=$P[$i];$flp=$P[$i]%12} }
$hk=$detect.Invoke($null,@($wg,[int]$flp,([System.Activator]::CreateInstance($hashT)))); $tonic=$mt.GetMethod("TonicPc").Invoke($null,@($hk))
# per-beat: root, top pitch, bass pitch (onset-attacked notes preferred for the soprano)
$bt=New-Object System.Collections.ArrayList
for($t=0;$t -lt $maxE;$t+=24){ $w=[double[]]::new(12); $top=0;$bass=200; for($i=0;$i -lt $P.Count;$i++){ $ov=[math]::Min($E[$i],$t+12)-[math]::Max($S[$i],$t); if($ov -gt 0){ $w[$P[$i]%12]+=$ov; if($P[$i] -gt $top){$top=$P[$i]}; if($P[$i] -lt $bass){$bass=$P[$i]} } }; $tot=0.0; foreach($x in $w){$tot+=$x}; if($tot -le 0){ [void]$bt.Add(@(-1,0,0)); continue }; [void]$bt.Add(@((Tri $w),$top,$bass)) }
# find V -> tonic resolutions
$V=(($tonic+7)%12); $sop=@(); $bas=@(); $stepSop=0; $nC=0; $examples=@()
for($i=1;$i -lt $bt.Count;$i++){ if($bt[$i][0] -eq $tonic -and $bt[$i-1][0] -eq $V -and $bt[$i][1] -gt 0 -and $bt[$i-1][1] -gt 0){ $ds=$bt[$i][1]-$bt[$i-1][1]; $db=$bt[$i][2]-$bt[$i-1][2]; $sop+=$ds; $bas+=$db; $nC++; if([math]::Abs($ds) -le 2){$stepSop++}; if($examples.Count -lt 8){ $examples += ("sopr {0}->{1} (d{2})  bass d{3}" -f $bt[$i-1][1],$bt[$i][1],$ds,$db) } } }
Write-Output ("Home tonic pc={0}  V→i resolutions found: {1}" -f $tonic,$nC)
if($nC -gt 0){ Write-Output ("  soprano motion: STEPWISE(<=M2)={0:N0}%   descending={1:N0}%   |interval| avg={2:N1} semis" -f (100*$stepSop/$nC),(100*(($sop|Where-Object{$_ -lt 0}).Count)/$nC),(($sop|ForEach-Object{[math]::Abs($_)}|Measure-Object -Average).Average))
  Write-Output ("  bass motion: avg |interval|={0:N1} semis (a 4th/5th root leap = ~5-7)" -f (($bas|ForEach-Object{[math]::Abs($_)}|Measure-Object -Average).Average))
  Write-Output "  examples (MIDI):"; $examples | ForEach-Object { Write-Output ("    " + $_) } }
