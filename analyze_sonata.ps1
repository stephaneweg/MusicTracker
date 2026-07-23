$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
foreach ($dll in (Get-ChildItem $bin -Filter "NAudio*.dll")) { [void][System.Reflection.Assembly]::LoadFrom($dll.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $bin "MusicTracker.exe"))
$load=$asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$ks=$asm.GetType("MusicTracker.Engine.Score.KeySig"); $derive=$ks.GetMethod("Derive"); $detect=$ks.GetMethod("Detect"); $hashT=[System.Collections.Generic.HashSet[int]]
$pcN="Do","Do#","Re","Mi b","Mi","Fa","Fa#","Sol","La b","La","Si b","Si"
$TPL=@{ "maj"=@(0,4,7); "min"=@(0,3,7); "dim"=@(0,3,6) }
function Tri($w){ $bs=-1e9;$br=-1;$bq="maj"; foreach($r in 0..11){ foreach($q in $TPL.Keys){ $t=$TPL[$q]; $on=0.0; foreach($x in $t){$on+=$w[(($r+$x)%12)]}; $off=0.0; for($p=0;$p -lt 12;$p++){ if($t -notcontains ((($p-$r)%12+12)%12)){$off+=$w[$p]} }; $pr=0; foreach($x in $t){ if($w[(($r+$x)%12)] -gt 0){$pr++} }; $sc=$on-0.55*$off; if($pr -ge 2 -and $sc -gt $bs){$bs=$sc;$br=$r;$bq=$q} } }; return @($br,$bq) }
$deg="I","bII","II","bIII","III","IV","#IV","V","bVI","VI","bVII","VII"
function KeyName($w,$flp){ $fp=[System.Activator]::CreateInstance($hashT); $k=$detect.Invoke($null,@($w,[int]$flp,$fp)); $dk=$derive.Invoke($null,@($k,[int]0)); return $dk.GetType().GetField("Name").GetValue($dk) }

$f = "C:\Users\swe\source\repos\MusicTracker\midi\_exclass2\ex_class2_c.mid"
$score=$load.Invoke($null,@([string]$f)); $tracks=$score.GetType().GetField("Tracks").GetValue($score)
$P=New-Object System.Collections.ArrayList;$S=New-Object System.Collections.ArrayList;$E=New-Object System.Collections.ArrayList;$maxE=0
foreach($t in $tracks){ foreach($n in $t.GetType().GetField("Notes").GetValue($t)){ $nt=$n.GetType(); $np=$nt.GetField("Pitch").GetValue($n);$ns=$nt.GetField("StartSlice").GetValue($n);$nl=$nt.GetField("LengthSlices").GetValue($n); [void]$P.Add($np);[void]$S.Add($ns);[void]$E.Add($ns+$nl); if($ns+$nl -gt $maxE){$maxE=$ns+$nl} } }
# home key (global)
$wg=[double[]]::new(12); $low=[int]::MaxValue;$flp=-1; for($i=0;$i -lt $P.Count;$i++){ $wg[$P[$i]%12]+=($E[$i]-$S[$i]); if($P[$i] -lt $low){$low=$P[$i];$flp=$P[$i]%12} }
Write-Output ("HOME key (global): " + (KeyName $wg $flp) + ("  (~{0} bars)" -f [math]::Round($maxE/96)))
# windowed key map (4-bar windows) -> sonata tonal plan
Write-Output "TONAL MAP (4-bar windows):"
$line=""
for($t=0;$t -lt $maxE;$t+=384){ $w=[double[]]::new(12); $lw=[int]::MaxValue;$fl=-1; for($i=0;$i -lt $P.Count;$i++){ if($S[$i] -ge $t -and $S[$i] -lt $t+384){ $w[$P[$i]%12]+=($E[$i]-$S[$i]); if($P[$i] -lt $lw){$lw=$P[$i];$fl=$P[$i]%12} } }; $kn=KeyName $w $fl; $line += ("  b{0,2}:{1}" -f [math]::Round($t/96),$kn) }
Write-Output $line
# per-beat chord -> collapse to (chord, beats), bass note; degree vs home tonic
$hk=$detect.Invoke($null,@($wg,[int]$flp,([System.Activator]::CreateInstance($hashT)))); $tonic = ($hk.GetType().GetField("TonicLetter").GetValue($hk)); # not pc; recompute pc:
$tonicPc = $flp # approx home tonic = lowest first note pc; will refine below
# better home tonic: most-weighted pc that is the detected key tonic — use TonicPc via reflection
$mt=$asm.GetType("MusicTracker.Engine.Flow.MusicTheory"); $tonicPc=$mt.GetMethod("TonicPc").Invoke($null,@($hk))
$chords=New-Object System.Collections.ArrayList
for($t=0;$t -lt $maxE;$t+=24){ $w=[double[]]::new(12); $bass=200; for($i=0;$i -lt $P.Count;$i++){ $ov=[math]::Min($E[$i],$t+12)-[math]::Max($S[$i],$t); if($ov -gt 0){ $w[$P[$i]%12]+=$ov; if($P[$i] -lt $bass){$bass=$P[$i]} } }; $tot=0.0; foreach($x in $w){$tot+=$x}; if($tot -le 0){ [void]$chords.Add(@(-1,"",$bass)); continue }; $tr=Tri $w; [void]$chords.Add(@($tr[0],$tr[1],$bass)) }
# collapse + degree
function Lab($root){ if($root -lt 0){return "-"}; $d=((($root-$tonicPc)%12)+12)%12; return $deg[$d] }
Write-Output "CHORD ORDER (beat-resolution, collapsed; first 40 bars):  [degree(quality) xBeats  bassPc]"
$seq=""; $i=0
while($i -lt $chords.Count -and $i -lt 160){ $r=$chords[$i][0];$q=$chords[$i][1]; $j=$i; while($j -lt $chords.Count -and $chords[$j][0] -eq $r -and $chords[$j][1] -eq $q){$j++}; $len=$j-$i; $qs= if($q -eq "min"){"m"}elseif($q -eq "dim"){"o"}else{""}; $seq += ("{0}{1}x{2} " -f (Lab $r),$qs,$len); $i=$j }
Write-Output ("  " + $seq)
Write-Output ("HOME tonic pc = {0} ({1})" -f $tonicPc,$pcN[$tonicPc])
