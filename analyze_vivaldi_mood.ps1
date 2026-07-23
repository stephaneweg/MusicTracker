# Objective mood analysis of the canonical Vivaldi set (corpus/vivaldi/vivaldi_full, 80 pieces).
# Baroque features DO map to mood: tempo (Allegro/Largo) + mode (maj/min) + meter.
# Combines measured bpm / Krumhansl mode / density / meter with filename hints
# (largo/adagio/andante/allegro/presto, key, season, sacred markers).
$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm  = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$midiLoad = $asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$dir = "C:\Users\swe\source\repos\MusicTracker\corpus\vivaldi\vivaldi_full"

$majProf = @(6.35,2.23,3.48,2.33,4.38,4.09,2.52,5.19,2.39,3.66,2.29,2.88)
$minProf = @(6.33,2.68,3.52,5.38,2.60,3.53,2.54,4.75,3.98,2.69,3.34,3.17)
function Corr($a,$b){ $ma=($a|Measure-Object -Average).Average; $mb=($b|Measure-Object -Average).Average
  $num=0.0;$da=0.0;$db=0.0; for($i=0;$i -lt 12;$i++){ $x=$a[$i]-$ma;$y=$b[$i]-$mb;$num+=$x*$y;$da+=$x*$x;$db+=$y*$y }
  if($da -eq 0 -or $db -eq 0){return 0}; return $num/[math]::Sqrt($da*$db) }
function EstimateMode($pc){ $bMaj=-2;$bMin=-2
  for($r=0;$r -lt 12;$r++){ $rot=@(); for($i=0;$i -lt 12;$i++){ $rot+=$pc[($i+$r)%12] }
    $cm=Corr $rot $majProf; if($cm -gt $bMaj){$bMaj=$cm}; $cn=Corr $rot $minProf; if($cn -gt $bMin){$bMin=$cn} }
  if($bMin -gt $bMaj){"min"}else{"maj"} }

$files = Get-ChildItem $dir -Filter *.mid | Sort-Object Name
"Analyzing $($files.Count) files`n"
$rows = New-Object System.Collections.ArrayList
foreach($f in $files){
  try { $score = $midiLoad.Invoke($null, @([string]$f.FullName)) } catch { "FAIL $($f.Name)"; continue }
  $st=$score.GetType()
  $bpm=[math]::Round([double]$st.GetField("Bpm").GetValue($score))
  $tsN=$st.GetField("TimeSigN").GetValue($score); $tsD=$st.GetField("TimeSigD").GetValue($score)
  $msl=$st.GetField("MeasureStartSlices").GetValue($score); $bars= if($msl){$msl.Count}else{0}
  $tracks=$st.GetField("Tracks").GetValue($score)
  $pc=@(0)*12; $total=0
  foreach($t in $tracks){ foreach($n in $t.GetType().GetField("Notes").GetValue($t)){
    $p=[int]$n.GetType().GetField("Pitch").GetValue($n); $pc[$p%12]+=1; $total++ } }
  if($total -eq 0){ continue }
  $modeEst = EstimateMode $pc
  $dens = if($bars -gt 0){[math]::Round($total/$bars,1)}else{0}
  $nl = $f.Name.ToLower()

  # filename hints
  $hintSlow = ($nl -match 'largo|adagio|lento|andante|grave|siciliano')
  $hintFast = ($nl -match 'allegro|presto|vivace|molto')
  $sacred   = ($nl -match 'gloria_|christe|domine|magn|caccia')
  $keyMin   = ($nl -match 'minor|gminor|bminor|dminor|aminor|cminor|eminor|fminor')
  $keyMaj   = ($nl -match 'major|amajor|gmajor|dmajor|cmajor|fmajor|bmajor|emajor')
  $mode = if($keyMin){"min"} elseif($keyMaj){"maj"} else {$modeEst}
  $ternary = ($tsN -eq 3 -or ($tsN -eq 6 -and $tsD -eq 8) -or ($tsN -eq 9) -or ($tsN -eq 12))

  $fast = $hintFast -or ($bpm -ge 112 -and -not $hintSlow)
  $slow = $hintSlow -or $bpm -le 72

  if($sacred){ $mood="Sacré_Solennel" }
  elseif($slow){ $mood="Lent_Lyrique" }
  elseif($fast){ if($mode -eq "min"){"Sombre_Dramatique"|Out-Null; $mood="Sombre_Dramatique"} else {$mood="Vif_Brillant"} }
  else { if($ternary){$mood="Dansant_Pastoral"} elseif($mode -eq "min"){$mood="Lent_Lyrique"} else {$mood="Vif_Brillant"} }

  [void]$rows.Add([pscustomobject]@{
    File=$f.Name; bpm=$bpm; ts="$tsN/$tsD"; mode=$mode; dens=$dens; notes=$total; mood=$mood })
}
$rows | Sort-Object mood,File | Format-Table File,bpm,ts,mode,dens,mood -AutoSize | Out-String -Width 200
"`n===== RÉPARTITION ====="
$rows | Group-Object mood | Sort-Object Count -Descending | ForEach-Object { "{0,-20} {1}" -f $_.Name,$_.Count }
