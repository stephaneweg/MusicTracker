# Detect the theme's chords, generate >=20 melodic variations (many techniques + V1 BuildVariations) with their
# harmony (transposed / modal / modulated / reharmonized), and export a 2-staff MuseScore .mscx (melody + chords)
# into the repo (local) folder. Pure reflection (no Add-Type).
$ErrorActionPreference = "Stop"
$repo    = "C:\Users\swe\source\repos\MusicTracker"
$bin     = "$repo\MusicTracker\bin\Debug"
$theme   = "C:\Users\swe\Documents\MuseScore3\Partitions\theme.mscz"
$outMscx = "$repo\theme_variations.mscx"

Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
function F($o,$n){ $o.GetType().GetField($n).GetValue($o) }

# ---- import; pick the melody track + a HARMONY pool (melody + any lower tracks) over the 4-bar theme ----
$score = $asm.GetType("MusicTracker.Engine.MuseScoreImporter").GetMethod("Load").Invoke($null, @([string]$theme))
$tracks0 = F $score 'Tracks'
$BAR = 96; $THEME_AT = 96; $TOTAL = 4 * $BAR; $THEME_END = $THEME_AT + $TOTAL
$best = 0; $bestKey = -1.0
$harm = @()   # all sounding notes in the theme region (for chord detection): @(pitch,start,len)
for ($ti = 0; $ti -lt $tracks0.Count; $ti++) {
  $nn = F $tracks0[$ti] 'Notes'; $c = 0; $sumP = 0.0
  foreach ($n in $nn) {
    $s=[int](F $n 'StartSlice'); $p=[int](F $n 'Pitch'); $l=[int](F $n 'LengthSlices')
    if ($s -lt $THEME_END -and $s+$l -gt $THEME_AT) { $harm += ,@($p,$s,$l); if ($s -lt 480) { $c++; $sumP += $p } }
  }
  $key = $c + ($(if ($c -gt 0) { ($sumP / $c) / 1000.0 } else { 0 }))
  if ($key -gt $bestKey) { $bestKey = $key; $best = $ti }
}
$notes0 = F $tracks0[$best] 'Notes'
$baseT = @()
foreach ($n in $notes0) { $s=[int](F $n 'StartSlice'); if ($s -ge $THEME_AT -and $s -lt $THEME_END) { $baseT += ,@([int](F $n 'Pitch'), ($s-$THEME_AT), [int](F $n 'LengthSlices')) } }
Write-Host ("melody track: $best   theme notes: $($baseT.Count)   harmony pool: $($harm.Count)")

# ---- detect ONE chord per theme bar (duration-weighted pc histogram + lowest note as bass) ----
$mm = $asm.GetType("MusicTracker.Engine.ComposerV2.MusicMathV2"); $dc = $mm.GetMethod("DetectChord")
$baseChords = @()   # @(rootPc, @(iv,...)) per bar
for ($b = 0; $b -lt 4; $b++) {
  $bs = $THEME_AT + $b*$BAR; $be = $bs + $BAR
  $pcw = New-Object 'double[]' 12; $low = 999; $bassPc = -1
  foreach ($h in $harm) {
    $ov = [Math]::Min($be, $h[1]+$h[2]) - [Math]::Max($bs, $h[1])
    if ($ov -gt 0) { $pcw[((($h[0]%12)+12)%12)] += $ov; if ($h[0] -lt $low) { $low = $h[0]; $bassPc = (($h[0]%12)+12)%12 } }
  }
  $tot=0; for($z=0;$z -lt 12;$z++){ $tot+=$pcw[$z] }
  if ($bassPc -ge 0 -and $low -lt 60) {
    # a real low bass IS the root: minor/major from whichever 3rd carries more weight, + a 7th if clearly present
    $root = $bassPc
    $iv = if ($pcw[(($root+3)%12)] -ge $pcw[(($root+4)%12)]) { @(0,3,7) } else { @(0,4,7) }
    if ($pcw[(($root+10)%12)] -gt $tot*0.12) { $iv = $iv + 10 }
    $baseChords += ,@([int]$root, [int[]]$iv)
  }
  elseif (($g = $dc.Invoke($null, @([double[]]$pcw, [int]$bassPc))) -ne $null) {
    $baseChords += ,@([int]$g.GetType().GetField('RootDeg').GetValue($g), [int[]]$g.GetType().GetField('Iv').GetValue($g))
  }
  else { $baseChords += ,@(7, [int[]]@(0,3,7)) }   # fallback Gm
}
$NM = @("Do","Do#","Re","Mib","Mi","Fa","Fa#","Sol","Lab","La","Sib","Si")
Write-Host ("Accords détectés (auto) : " + (($baseChords | ForEach-Object { $NM[$_[0]] + "(" + (($_[1] | ForEach-Object { $_ }) -join '-') + ")" }) -join "  |  "))
# The author states the intended progression is  i | V | i | V  in G minor.
# i = Gm, V = D major (harmonic-minor dominant).
$baseChords = @()
$baseChords += ,@(7, [int[]]@(0,3,7))   # i   Gm
$baseChords += ,@(2, [int[]]@(0,4,7))   # V   D
$baseChords += ,@(7, [int[]]@(0,3,7))   # i   Gm
$baseChords += ,@(2, [int[]]@(0,4,7))   # V   D
Write-Host ("Accords utilisés (i-V-i-V) : " + (($baseChords | ForEach-Object { $NM[$_[0]] } ) -join "  |  "))

# ---- scale helpers (G minor) ----
$TON=7; $MIN=@(0,2,3,5,7,8,10); $MAJ=@(0,2,4,5,7,9,11); $SC=@(7,9,10,0,2,3,5)
function M12($x){ (($x % 12) + 12) % 12 }
function InSc($p){ $SC -contains (M12 $p) }
function Snap($p){ for($d=0;$d -lt 6;$d++){ if(InSc($p+$d)){return $p+$d}; if(InSc($p-$d)){return $p-$d} } return $p }
function DegIx($p){ $rel=M12($p-$TON); $best=0;$bd=99; for($i=0;$i -lt 7;$i++){ $dd=[Math]::Min((M12($MIN[$i]-$rel)),(M12($rel-$MIN[$i]))); if($dd -lt $bd){$bd=$dd;$best=$i} } return $best }
function TSc($p,$st){ $rel=M12($p-$TON); $idx=DegIx $p; $oct=[int][Math]::Floor(($p-$TON-$rel)/12); $ni=$idx+$st; $noct=$oct+[int][Math]::Floor($ni/7.0); $nrel=$MIN[(($ni%7)+7)%7]; return $TON+$nrel+12*$noct }
function ToMaj($p){ $idx=DegIx $p; $rel=M12($p-$TON); $oct=[int][Math]::Floor(($p-$TON-$rel)/12); return $TON+$MAJ[$idx]+12*$oct }
function Clone($t){ $o=@(); foreach($n in $t){ $o += ,@([int]$n[0],[int]$n[1],[int]$n[2]) } return ,$o }
function EndOf($t){ $e=0; foreach($n in $t){ if($n[1]+$n[2] -gt $e){$e=$n[1]+$n[2]} } return $e }
function PMap($t,$fn){ $o=@(); foreach($n in $t){ $o += ,@([int](& $fn $n[0]),[int]$n[1],[int]$n[2]) } return ,$o }
function Retro($t){ $s=$t | Sort-Object {$_[1]}; $o=@(); $pos=0; for($i=$s.Count-1;$i -ge 0;$i--){ $o += ,@([int]$s[$i][0],[int]$pos,[int]$s[$i][2]); $pos+=$s[$i][2] } return ,$o }
function Dotted($t){ $s=$t | Sort-Object {$_[1]}; $o=@(); $pos=0; for($i=0;$i -lt $s.Count;$i++){ $bl=$s[$i][2]; if($i%2 -eq 0){$l=[int](($bl*3)/2)}else{$l=[Math]::Max(3,[int]($bl/2))}; $o += ,@([int]$s[$i][0],[int]$pos,[int]$l); $pos+=$l } return ,$o }
function Triplets($t){ $s=$t | Sort-Object {$_[1]}; $o=@(); $pos=0; foreach($n in $s){ if($n[2] -ge 24){ $u=8; $o+=,@([int]$n[0],[int]$pos,$u); $o+=,@([int](Snap($n[0]+2)),[int]($pos+$u),$u); $o+=,@([int]$n[0],[int]($pos+2*$u),$u); $pos+=24 } else { $o+=,@([int]$n[0],[int]$pos,[int]$n[2]); $pos+=$n[2] } } return ,$o }
function Embellish($t){ $s=$t | Sort-Object {$_[1]}; $o=@(); for($i=0;$i -lt $s.Count;$i++){ $n=$s[$i]; if($n[2] -ge 24 -and $i+1 -lt $s.Count){ $h=[int]($n[2]/2); $o+=,@([int]$n[0],[int]$n[1],$h); $nb= if((M12($n[0]-$TON)) -lt 6){$n[0]+1}else{$n[0]-1}; $o+=,@([int](Snap($nb)),[int]($n[1]+$h),[int]($n[2]-$h)) } else { $o+=,@([int]$n[0],[int]$n[1],[int]$n[2]) } } return ,$o }
function Seq($t){ $m=$t | Where-Object {$_[1] -lt 2*$BAR} | Sort-Object {$_[1]}; $o=@(); for($r=0;$r -lt 2;$r++){ foreach($n in $m){ $o+=,@([int](TSc $n[0] $r),[int]($r*2*$BAR+$n[1]),[int]$n[2]) } } return ,$o }
function Fragment($t){ $m=$t | Where-Object {$_[1] -lt $BAR} | Sort-Object {$_[1]}; $o=@(); for($r=0;$r -lt 4;$r++){ foreach($n in $m){ $o+=,@([int](TSc $n[0] $r),[int]($r*$BAR+$n[1]),[int]$n[2]) } } return ,$o }
function Arp($t){ $s=$t | Sort-Object {$_[1]}; $o=@(); foreach($n in $s){ if($n[2] -ge 24){ $u=6;$k=0; for($p=0;$p -lt $n[2] -and $k -lt 4;$p+=$u){ $o+=,@([int](TSc $n[0] (2*$k)),[int]($n[1]+$p),$u); $k++ } } else { $o+=,@([int]$n[0],[int]$n[1],[int]$n[2]) } } return ,$o }

# Fill short variations to whole bars: repeat ones shorter than 4 bars, then extend the last note to the final
# bar line (and clamp tiny spills) so no measure ends on an unwanted rest.
function FillVar($v){
  $s = @($v | Sort-Object {$_[1]})
  if ($s.Count -eq 0) { return ,$s }
  $end = EndOf $s
  $unitBars = [Math]::Max(1, [int][Math]::Ceiling($end/[double]$BAR))
  if ($unitBars -lt 4) {                                  # repeat short variations (e.g. diminution) up to 4 bars
    $unitLen = $unitBars*$BAR; $o=@(); $rep=0
    while ($rep*$unitLen -lt 4*$BAR) { foreach($n in $s){ $o += ,@([int]$n[0], [int]($rep*$unitLen+$n[1]), [int]$n[2]) }; $rep++ }
    $s = @($o | Sort-Object {$_[1]}); $end = EndOf $s
  }
  $target = if ($end -le 432) { 4*$BAR } else { [int][Math]::Ceiling($end/[double]$BAR)*$BAR }   # tiny spills → 4 bars
  # legato fill: each note is sustained up to the NEXT onset (last note up to the bar line) → zero internal rests
  $s = @($s | Sort-Object {$_[1]})
  $o2 = @()
  for($i=0;$i -lt $s.Count;$i++){
    $st=[int]$s[$i][1]; if($st -ge $target){ continue }
    $nxt = if($i+1 -lt $s.Count){ [int]$s[$i+1][1] } else { $target }
    if($nxt -gt $target){ $nxt = $target }
    $ln = $nxt - $st; if($ln -lt 1){ continue }
    $o2 += ,@([int]$s[$i][0],$st,$ln)
  }
  return ,$o2
}

# ---- chord helpers ----
function ShiftCh($ch,$semis){ $o=@(); foreach($c in $ch){ $o += ,@((M12($c[0]+$semis)), $c[1]) } return ,$o }
function MajCh($ch){ $o=@(); foreach($c in $ch){ $o += ,@([int]$c[0], [int[]]@(0,4,7)) } return ,$o }     # majorize the triads
function RevCh($ch){ $o=@(); for($i=$ch.Count-1;$i -ge 0;$i--){ $o += ,@([int]$ch[$i][0],$ch[$i][1]) } return ,$o }
function MkCh($roots,$quals){ $o=@(); for($i=0;$i -lt $roots.Count;$i++){ $iv= if($quals[$i] -eq 'm'){[int[]]@(0,3,7)}elseif($quals[$i] -eq 'M'){[int[]]@(0,4,7)}else{[int[]]@(0,3,6)}; $o += ,@([int]$roots[$i],$iv) } return ,$o }

# ---- register control: octave-shift each variation so its MEAN pitch sits on the middle of the treble
# staff (B4 = MIDI 71 = "zone canonique"). A high theme is automatically pulled down an octave; the contour
# is preserved. Replaces the old "drop notes > MIDI 96" filter. ----
$STAFF_MID = 71   # B4, middle line of the treble staff
function FitRange($v){
  $arr=@($v); if($arr.Count -eq 0){ return ,$arr }
  $sum=0; foreach($n in $arr){ $sum+=[int]$n[0] }; $mean=$sum/[double]$arr.Count
  $sh = 12 * [int][Math]::Floor((($STAFF_MID - $mean)/12.0) + 0.5)   # nearest whole octave that centres the mean
  $o=@(); foreach($n in $arr){ $o += ,@([int]($n[0]+$sh),[int]$n[1],[int]$n[2]) }; return ,$o
}

# ---- figuration ("ritournelle") helpers: chord tones of a bar; chord tones as ACTUAL pitches; nearest index ----
function ChordPcs($ch,$bar){ $b=[Math]::Min([Math]::Max(0,$bar),$ch.Count-1); $c=$ch[$b]; $root=[int]$c[0]; $o=@(); foreach($iv in $c[1]){ $o += (M12($root+$iv)) }; return ,$o }
function ChordScale($pcs,$lo,$hi){ $o=@(); for($p=$lo;$p -le $hi;$p++){ if($pcs -contains (M12 $p)){ $o += [int]$p } }; return ,$o }
function NearIx($arr,$v){ $bi=0;$bd=999; for($i=0;$i -lt $arr.Count;$i++){ $d=[Math]::Abs($arr[$i]-$v); if($d -lt $bd){$bd=$d;$bi=$i} } return $bi }
# Rhythm becomes a continuous 16th arpeggio, but each beat is ANCHORED on the theme note sounding then
# (the strong-beat "idea") and spun from the bar's chord tones via a pattern of chord-step offsets.
# Always 16 beats x 4 sixteenths = 4 bars (= theme length).
function Figuration($t,$chords,$pat){
  $s = @($t | Sort-Object {$_[1]}); $o=@(); $u=6
  for($beat=0;$beat -lt 16;$beat++){
    $br=[int][Math]::Floor($beat/4.0); $pos=$beat*24
    $anchor=[int]$s[0][0]; foreach($n in $s){ if([int]$n[1] -le $pos){ $anchor=[int]$n[0] } else { break } }
    $pcs = ChordPcs $chords $br; $ct = ChordScale $pcs ($anchor-7) ($anchor+13)
    if($ct.Count -eq 0){ $ct=@($anchor) }
    $i0 = NearIx $ct $anchor
    foreach($st in $pat){ $idx=[Math]::Max(0,[Math]::Min($ct.Count-1,$i0+$st)); $o += ,@([int]$ct[$idx],[int]$pos,$u); $pos+=$u }
  }
  return ,$o
}

$a0 = $baseT[0][0]
$named = @()
function AddV($name,$v,$ch,$bass='block'){ if($null -eq $ch){ $ch=$baseChords }; $script:named += ,@($name,$v,$ch,$bass) }

# ---- extra strategies (added 2026-06-18) ----
# modal remap: keep each theme note's scale-DEGREE but rebuild it through a different mode's intervals
$DOR=@(0,2,3,5,7,9,10); $PHR=@(0,1,3,5,7,8,10); $HARM=@(0,2,3,5,7,8,11); $MEL=@(0,2,3,5,7,9,11)
function ToMode($p,$mode){ $idx=DegIx $p; $rel=M12($p-$TON); $oct=[int][Math]::Floor(($p-$TON-$rel)/12); return $TON+$mode[$idx]+12*$oct }
# scalar "liaison": continuous 16ths stepping diatonically from each strong-beat theme note toward the next
function ScaleRun($t){
  $s=@($t | Sort-Object {$_[1]}); $u=6; $anch=@()
  for($beat=0;$beat -le 16;$beat++){ $pos=$beat*24; $a=[int]$s[0][0]; foreach($n in $s){ if([int]$n[1] -le $pos){ $a=[int]$n[0] } else { break } }; $anch += $a }
  $o=@()
  for($beat=0;$beat -lt 16;$beat++){ $cur=$anch[$beat]; $to=$anch[$beat+1]; $pos=$beat*24
    for($k=0;$k -lt 4;$k++){ $o += ,@([int]$cur,[int]$pos,$u); $pos+=$u; if($to -gt $cur){ $cur=[int](TSc $cur 1) } elseif($to -lt $cur){ $cur=[int](TSc $cur -1) } else { $cur=[int](TSc $cur 1) } } }
  return ,$o
}
# canon: leader + a delayed, transposed copy; both truncated to 4 bars
function Canon($t,$delay,$tr){ $lim=4*$BAR; $o=@()
  foreach($n in $t){ if($n[1] -lt $lim){ $o += ,@([int]$n[0],[int]$n[1],[int]$n[2]) } }
  foreach($n in $t){ $st=[int]$n[1]+$delay; if($st -lt $lim){ $o += ,@([int]($n[0]+$tr),$st,[int]$n[2]) } }
  return ,$o
}
# chorale: harmonise each theme note with up to 3 chord tones BELOW it (block chords in the theme rhythm)
function Choral($t,$chords){ $s=@($t | Sort-Object {$_[1]}); $o=@()
  foreach($n in $s){ $br=[int][Math]::Floor($n[1]/[double]$BAR); $pcs=ChordPcs $chords $br
    $o += ,@([int]$n[0],[int]$n[1],[int]$n[2]); $added=0
    foreach($pc in $pcs){ $p=[int]$n[0]-1; while((M12 $p) -ne $pc -and $p -gt [int]$n[0]-13){ $p-- }
      if((M12 $p) -eq $pc -and $p -gt 0 -and $p -lt [int]$n[0]){ $o += ,@([int]$p,[int]$n[1],[int]$n[2]); $added++ }; if($added -ge 3){ break } } }
  return ,$o
}
# metric displacement: rotate the theme inside the 4-bar window (strong material lands off the beat)
function Displace($t,$shift){ $lim=4*$BAR; $o=@()
  foreach($n in $t){ $st=([int]$n[1]+$shift)%$lim; $ln=[int]$n[2]; if($st+$ln -gt $lim){ $ln=$lim-$st }; if($ln -ge 1){ $o += ,@([int]$n[0],$st,$ln) } }
  return ,$o
}
# baroque ornament: replace the head of each long note with a turn (grupetto), then sustain the rest
function Ornee($t){ $s=@($t | Sort-Object {$_[1]}); $o=@()
  foreach($n in $s){ $p=[int]$n[0]; $st=[int]$n[1]; $ln=[int]$n[2]
    if($ln -ge 24){ $u=6; $up=[int](TSc $p 1); $dn=[int](TSc $p -1)
      $o += ,@($up,$st,$u); $o += ,@($p,($st+$u),$u); $o += ,@($dn,($st+2*$u),$u); $o += ,@($p,($st+3*$u),[Math]::Max($u,$ln-3*$u)) }
    else { $o += ,@($p,$st,$ln) } }
  return ,$o
}
$augT = @($baseT | ForEach-Object { ,@([int]$_[0],[int]($_[1]*2),[int]($_[2]*2)) })   # theme doubled (8 bars) for ornamentation
$drone = @(); for($i=0;$i -lt 4;$i++){ $drone += ,@(7,[int[]]@(0,7)) }                # tonic pedal G + fifth D, each bar
AddV "Theme original"                 (Clone $baseT)                                   $baseChords
AddV "Augmentation (x2)"              (@($baseT | ForEach-Object { ,@([int]$_[0],[int]($_[1]*2),[int]($_[2]*2)) })) $baseChords
AddV "Diminution (/2)"               (@($baseT | ForEach-Object { ,@([int]$_[0],[int]([int]($_[1]/2)),[Math]::Max(3,[int]($_[2]/2))) })) $baseChords
AddV "Inversion (miroir)"            (PMap $baseT { param($p) Snap(2*$a0-$p) })          $baseChords
AddV "Retrograde"                    (Retro $baseT)                                      (RevCh $baseChords)
AddV "Retrograde-inversion"          (Retro (PMap $baseT { param($p) Snap(2*$a0-$p) }))  (RevCh $baseChords)
AddV "Transpose quinte (re m)"       (PMap $baseT { param($p) TSc $p 4 })                (ShiftCh $baseChords 7)
AddV "Transpose quarte (do m)"       (PMap $baseT { param($p) TSc $p 3 })                (ShiftCh $baseChords 5)
# "Octave aigu" retiré : montait trop haut, et la borne de registre FitRange le ramènerait au thème de toute façon
# "Octave grave" retiré : le recentrage FitRange sur le milieu de portée annule tout décalage d'octave
AddV "Mode majeur (sol M)"           (PMap $baseT { param($p) ToMaj $p })                (MajCh $baseChords)
AddV "Tierce superieure"             (PMap $baseT { param($p) TSc $p 2 })                (ShiftCh $baseChords 3)
AddV "Broderies"                     (Embellish $baseT)                                  $baseChords
AddV "Rythme pointe"                 (Dotted $baseT)                                     $baseChords
AddV "Syncope"                       (@($baseT | ForEach-Object { ,@([int]$_[0],[int]($_[1]+6),[int]$_[2]) })) $baseChords
AddV "Triolets"                      (Triplets $baseT)                                   $baseChords
AddV "Sequence du motif"             (Seq $baseT)                                        $baseChords
AddV "Fragmentation"                 (Fragment $baseT)                                   $baseChords
AddV "Arpegiation"                   (Arp $baseT)                                        $baseChords
AddV "Expansion intervallique"       (PMap $baseT { param($p) Snap($a0 + [int][Math]::Round(($p-$a0)*1.6)) }) $baseChords
AddV "Compression intervallique"     (PMap $baseT { param($p) Snap($a0 + [int](($p-$a0)/2)) }) $baseChords
AddV "Inversion + transpose (mi m)"  (PMap $baseT { param($p) TSc (Snap(2*$a0-$p)) 2 })  (ShiftCh $baseChords 9)
# explicit HARMONY variations (same melody, new chords)
AddV "Reharmonisation A (i-VI-III-V)" (Clone $baseT)  (MkCh @(7,3,10,2) @('m','M','M','M'))
AddV "Reharmonisation B (i-iv-VII-III)" (Clone $baseT) (MkCh @(7,0,5,10) @('m','m','M','M'))
# figural ("ritournelle") variations: same harmonic/melodic idea, continuous-16th arpeggiated rhythm
AddV "Ritournelle (arpege montant)"  (Figuration $baseT $baseChords @(0,1,2,3))  $baseChords
AddV "Ritournelle (Alberti)"         (Figuration $baseT $baseChords @(0,2,1,2))  $baseChords
AddV "Ritournelle (vague)"           (Figuration $baseT $baseChords @(0,1,2,1))  $baseChords
AddV "Ritournelle (arpege brise)"    (Figuration $baseT $baseChords @(0,2,1,3))  $baseChords
# modal colour (same chords, melody recoloured by mode)
AddV "Mode dorien"        (PMap $baseT { param($p) ToMode $p $DOR })  $baseChords
AddV "Mode phrygien"      (PMap $baseT { param($p) ToMode $p $PHR })  $baseChords
AddV "Mineur harmonique"  (PMap $baseT { param($p) ToMode $p $HARM }) $baseChords
AddV "Mineur melodique"   (PMap $baseT { param($p) ToMode $p $MEL })  $baseChords
# texture / counterpoint / accompaniment strategies
AddV "Gammes de liaison"            (ScaleRun $baseT)             $baseChords
AddV "Pedale (bourdon de tonique)"  (Clone $baseT)               $drone
AddV "Canon (1 mesure, octave bas)" (Canon $baseT 96 -12)        $baseChords
AddV "Choral (homophonie)"          (Choral $baseT $baseChords)  $baseChords
AddV "Deplacement metrique"         (Displace $baseT 24)         $baseChords
AddV "Ornementation baroque"        (Ornee $augT)                $baseChords
AddV "Basse d'Alberti"              (Clone $baseT)               $baseChords 'alberti'
AddV "Basse marchante"              (Clone $baseT)               $baseChords 'walking'

# ---- V1 GhibliComposer.BuildVariations (rhythmic + modulating G/D/A/E minor) → chords transposed to match ----
$rnT = $asm.GetType("MusicTracker.Engine.RiffNote")
$themeList = [Activator]::CreateInstance([System.Collections.Generic.List`1].MakeGenericType($rnT))
foreach($n in $baseT){ $themeList.Add([Activator]::CreateInstance($rnT, @([int]($n[0]-12),[int]$n[1],[int]$n[2]))) | Out-Null }
$hs = [Activator]::CreateInstance([System.Collections.Generic.HashSet`1].MakeGenericType([int])); foreach($pc in $SC){ [void]$hs.Add([int]$pc) }
$bv = $asm.GetType("MusicTracker.Engine.Timeline.GhibliComposer").GetMethod("BuildVariations", ([System.Reflection.BindingFlags]"Static,NonPublic,Public"))
$bargs = New-Object 'object[]' 9
$bargs[0]=$themeList;$bargs[1]=[int]4;$bargs[2]=[int]4;$bargs[3]=[int]$BAR;$bargs[4]=[int]$BAR;$bargs[5]=$false;$bargs[6]=$hs;$bargs[7]=[int[]]@(0,7,2,9);$bargs[8]=[Activator]::CreateInstance([System.Random],@([int]7))
$v1 = $bv.Invoke($null, $bargs)
$fN=$rnT.GetField('Note');$fS=$rnT.GetField('Start');$fL=$rnT.GetField('Length')
$blocks = @{}
for($k=0;$k -lt $v1.Count;$k++){ $e=$v1[$k]; $pp=[int]$fN.GetValue($e); $st=[int]$fS.GetValue($e); $ll=[int]$fL.GetValue($e); $blk=[int]([Math]::Floor($st/$TOTAL)); if(-not $blocks.ContainsKey($blk)){$blocks[$blk]=@()}; $blocks[$blk] += ,@(($pp+12),($st-$blk*$TOTAL),$ll) }
$kn=@("sol m","re m","la m","mi m"); $ksemi=@(0,7,2,9)
for($b=0;$b -lt 4;$b++){ if($blocks.ContainsKey($b)){ AddV ("V1 BuildVariations - "+$kn[$b]) $blocks[$b] (ShiftCh $baseChords $ksemi[$b]) } }

Write-Host ("Variations: " + $named.Count)

# ---- lay melody + chords on two aligned staves, then export ----
$snT = $asm.GetType("MusicTracker.Engine.Score.ScoreNote")
$fMidi=$snT.GetField('Midi');$fSB=$snT.GetField('StartBeat');$fBe=$snT.GetField('Beats')
function NewSN($midi,$startSlice,$lenSlice){ $sn=[Activator]::CreateInstance($snT); $fMidi.SetValue($sn,[int]$midi); $fSB.SetValue($sn,[double]($startSlice/24.0)); $fBe.SetValue($sn,[double]([Math]::Max(1,$lenSlice)/24.0)); return $sn }
$melSN = [Activator]::CreateInstance([System.Collections.Generic.List`1].MakeGenericType($snT))
$chSN  = [Activator]::CreateInstance([System.Collections.Generic.List`1].MakeGenericType($snT))
$off=0
foreach($kv in $named){
  $v=FillVar (FitRange $kv[1]); $ch=$kv[2]; $bstyle=$kv[3]
  foreach($n in $v){ if($n[0] -ge 24 -and $n[0] -le 96 -and $n[1] -ge 0){ $melSN.Add((NewSN $n[0] ($off+$n[1]) $n[2])) | Out-Null } }
  $vbars=[Math]::Max(1,[int][Math]::Ceiling((EndOf $v)/[double]$BAR))   # NB: PowerShell [int] ROUNDS, so use Ceiling explicitly
  for($bi=0;$bi -lt $vbars;$bi++){
    $ci=[int]([Math]::Floor($bi*4.0/$vbars)); if($ci -gt 3){$ci=3}
    $root=[int]$ch[$ci][0]; $ivs=$ch[$ci][1]
    $tones=@(); foreach($iv in $ivs){ $pc=(($root+$iv)%12+12)%12; $tones += [int](43 + (((($pc-43)%12)+12)%12)) }
    $tones=@($tones | Sort-Object); if($tones.Count -eq 0){ $tones=@(43) }
    $bb=$off+$bi*$BAR
    switch($bstyle){
      'alberti' { $pat=@(0,2,1,2); $u=12; for($e=0;$e -lt 8;$e++){ $ix=[Math]::Min($tones.Count-1,$pat[$e%4]); $chSN.Add((NewSN $tones[$ix] ($bb+$e*$u) $u)) | Out-Null } }
      'walking' { $u=24; $seq=@(0,1,2,1); for($q=0;$q -lt 4;$q++){ $ix=[Math]::Min($tones.Count-1,$seq[$q]); $chSN.Add((NewSN $tones[$ix] ($bb+$q*$u) $u)) | Out-Null } }
      default   { foreach($t0 in $tones){ $chSN.Add((NewSN $t0 $bb $BAR)) | Out-Null } }
    }
  }
  $off += $vbars*$BAR + $BAR
}

$ksT=$asm.GetType("MusicTracker.Engine.Score.KeySignature")
function NewKey(){ $ks=[Activator]::CreateInstance($ksT); $ksT.GetField('TonicLetter').SetValue($ks,[int]4); $ksT.GetField('Accidental').SetValue($ks,[int]0); $ksT.GetField('Mode').SetValue($ks,[int]1); $ksT.GetProperty('FullMode').SetValue($ks,[int]1); return $ks }
$tsT=$asm.GetType("MusicTracker.Engine.Score.TrackScore"); $clefT=$asm.GetType("MusicTracker.Engine.Score.ScoreClefKind")
function NewTS($list,$clef){ $ts=[Activator]::CreateInstance($tsT); $ts.GetType().GetField('Notes').SetValue($ts,$list); $ts.GetType().GetField('TotalBeats').SetValue($ts,[double]($off/24.0)); $ts.GetType().GetField('Clef').SetValue($ts,[Enum]::Parse($clefT,$clef)); $ts.GetType().GetField('Key').SetValue($ts,(NewKey)); return $ts }

$partT=$asm.GetType("MusicTracker.Engine.Timeline.MuseScoreExporter+Part")
function NewPart($name,$prog,$ts){ $p=[Activator]::CreateInstance($partT); $partT.GetField('Name').SetValue($p,[string]$name); $partT.GetField('Program').SetValue($p,[int]$prog); $partT.GetField('Score').SetValue($p,$ts); return $p }
$listP=[Activator]::CreateInstance([System.Collections.Generic.List`1].MakeGenericType($partT))
$listP.Add((NewPart "Melodie (sol m)" 0 (NewTS $melSN 'Treble'))) | Out-Null
$listP.Add((NewPart "Accords" 48 (NewTS $chSN 'Bass'))) | Out-Null

$exp=$asm.GetType("MusicTracker.Engine.Timeline.MuseScoreExporter").GetMethod("Export")
$eargs = New-Object 'object[]' 5; $eargs[0]=[string]$outMscx; $eargs[1]=$listP; $eargs[2]=[int]4; $eargs[3]=[int]4; $eargs[4]=[string]"Variations du theme - sol mineur (melodie + accords)"
$exp.Invoke($null, $eargs)
Write-Host ("Melodie: $($melSN.Count) notes   Accords: $($chSN.Count) notes   Wrote: $outMscx")
if (Test-Path $outMscx) { Get-Item $outMscx | Select-Object Name, Length, LastWriteTime }
