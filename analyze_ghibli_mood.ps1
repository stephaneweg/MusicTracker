# Objective mood analysis of the reorganized Ghibli corpus MIDI files.
# Measures tempo / mode (major-minor via Krumhansl-Schmuckler) / note density / meter / range,
# proposes a mood bucket, and flags mismatches vs the folder the file currently sits in.
$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm  = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$midiLoad = $asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$root = "C:\Users\swe\source\repos\MusicTracker\corpus\Ghibli"

# Krumhansl-Schmuckler key profiles
$majProf = @(6.35,2.23,3.48,2.33,4.38,4.09,2.52,5.19,2.39,3.66,2.29,2.88)
$minProf = @(6.33,2.68,3.52,5.38,2.60,3.53,2.54,4.75,3.98,2.69,3.34,3.17)

function Corr($a,$b){
  $n=12; $ma=($a|Measure-Object -Average).Average; $mb=($b|Measure-Object -Average).Average
  $num=0.0;$da=0.0;$db=0.0
  for($i=0;$i -lt $n;$i++){ $x=$a[$i]-$ma; $y=$b[$i]-$mb; $num+=$x*$y; $da+=$x*$x; $db+=$y*$y }
  if($da -eq 0 -or $db -eq 0){return 0}
  return $num/[math]::Sqrt($da*$db)
}

function EstimateMode($pc){
  # pc = 12-length pitch-class weight histogram
  $bestMaj=-2;$bestMin=-2
  for($r=0;$r -lt 12;$r++){
    $rot=@(); for($i=0;$i -lt 12;$i++){ $rot += $pc[($i+$r)%12] }
    $cm=Corr $rot $majProf; if($cm -gt $bestMaj){$bestMaj=$cm}
    $cn=Corr $rot $minProf; if($cn -gt $bestMin){$bestMin=$cn}
  }
  if($bestMin -gt $bestMaj){ return "min" } else { return "maj" }
}

$files = Get-ChildItem -Recurse $root -Filter *.mid | Sort-Object FullName
"Analyzing $($files.Count) MIDI files`n"
$rows = New-Object System.Collections.ArrayList
foreach($f in $files){
  try { $score = $midiLoad.Invoke($null, @([string]$f.FullName)) } catch { "FAIL $($f.Name): $($_.Exception.Message)"; continue }
  $st=$score.GetType()
  $bpm=[double]$st.GetField("Bpm").GetValue($score)
  $tsN=$st.GetField("TimeSigN").GetValue($score)
  $tsD=$st.GetField("TimeSigD").GetValue($score)
  $km =$st.GetField("KeyIsMinor").GetValue($score)
  $msl=$st.GetField("MeasureStartSlices").GetValue($score)
  $bars= if($msl){$msl.Count}else{0}
  $tracks=$st.GetField("Tracks").GetValue($score)
  $pc=@(0)*12; $total=0; $pmin=200;$pmax=0; $sumLen=0.0; $lenN=0
  foreach($t in $tracks){
    $notes=$t.GetType().GetField("Notes").GetValue($t)
    foreach($n in $notes){
      $nt=$n.GetType()
      $p=[int]$nt.GetField("Pitch").GetValue($n)
      $l=[int]$nt.GetField("LengthSlices").GetValue($n)
      $pc[$p%12]+=1; $total++
      if($p -lt $pmin){$pmin=$p}; if($p -gt $pmax){$pmax=$p}
      $sumLen+=$l; $lenN++
    }
  }
  if($total -eq 0){ continue }
  $mode = if($km -ne $null){ if($km){"min"}else{"maj"} } else { EstimateMode $pc }
  $modeEst = EstimateMode $pc
  $density = if($bars -gt 0){ [math]::Round($total/$bars,1) } else { 0 }
  $avgLen = if($lenN -gt 0){ [math]::Round($sumLen/$lenN,1) } else { 0 }
  $curFolder = Split-Path (Split-Path $f.FullName -Parent) -Leaf

  # --- mood heuristic from objective features ---
  $ternary = ($tsN -eq 3 -or ($tsN -eq 6 -and $tsD -eq 8))
  $fast = $bpm -ge 108
  $slow = $bpm -le 76
  $dense = $density -ge 14
  $sparse = $density -le 7
  $mood = "Calme_Nostalgique"
  if($modeEst -eq "min"){
    if($fast -or $dense){ $mood="Sombre_Dramatique" }
    elseif($slow -and $sparse){ $mood="Solennel_Requiem" }
    else { $mood="Calme_Nostalgique" }
  } else {
    if($ternary -and $bpm -ge 90 -and $bpm -le 170){ $mood="Valse_Dansant" }
    elseif($fast -and $dense){ $mood="Enjoué_Léger" }
    elseif($slow -or $sparse){ $mood="Calme_Nostalgique" }
    else { $mood="Enjoué_Léger" }
  }

  [void]$rows.Add([pscustomobject]@{
    File=$f.Name; Folder=$curFolder; bpm=[math]::Round($bpm); ts="$tsN/$tsD";
    modeEst=$modeEst; dens=$density; range="$pmin-$pmax"; notes=$total; suggest=$mood;
    flag= if($mood -ne $curFolder){"<< DIFF"}else{""}
  })
}

$rows | Sort-Object Folder,File | Format-Table File,Folder,bpm,ts,modeEst,dens,range,suggest,flag -AutoSize | Out-String -Width 300
"`n===== ÉCARTS (suggestion != dossier actuel) ====="
$rows | Where-Object { $_.flag -ne "" } | Sort-Object suggest,File | Format-Table File,Folder,bpm,ts,modeEst,dens,suggest -AutoSize | Out-String -Width 300
