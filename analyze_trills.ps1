# Are the abundant 32nds real figuration, or trills/tremolos? Within each MONO voice, find RUNS of consecutive 32nd
# notes and classify by DISTINCT pitch count: 1 = tremolo (repeated note), 2 = trill / 2-note tremolo, >=3 = real
# melodic figuration (scale/arpeggio). Reports the share of all 32nds in each category.
$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
[void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin "NAudio.dll"))
$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $bin "MusicTracker.exe"))
$load = $asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$midiRoot = "C:\Users\swe\source\repos\MusicTracker\midi"
$grid = 3,6,8,12,18,24,36,48
function Snap($d){ ($grid | Sort-Object { [math]::Abs($_-$d) })[0] }

function Analyze($dir,$label){
  $files = Get-ChildItem $dir -Filter *.mid
  $tot32=0; $tremolo=0; $trill=0; $real=0; $isolated=0; $trillIv=@{}
  foreach($f in $files){
    try{ $score=$load.Invoke($null,@($f.FullName)) }catch{ continue }
    $tracks=$score.GetType().GetField("Tracks").GetValue($score)
    foreach($t in $tracks){
      if($t.GetType().GetField("IsDrum").GetValue($t)){continue}
      $notes=$t.GetType().GetField("Notes").GetValue($t); if($notes.Count -lt 4){continue}
      $arr=@(); foreach($n in $notes){ $nt=$n.GetType(); $arr+=[pscustomobject]@{p=$nt.GetField("Pitch").GetValue($n);s=$nt.GetField("StartSlice").GetValue($n);e=$nt.GetField("StartSlice").GetValue($n)+$nt.GetField("LengthSlices").GetValue($n)} }
      $arr=$arr|Sort-Object s
      $mono=$true;$re=-1; foreach($e in $arr){ if($e.s -lt $re-2){$mono=$false;break}; if($e.e -gt $re){$re=$e.e} }
      if(-not $mono){continue}
      $i=0
      while($i -lt $arr.Count){
        if((Snap ($arr[$i].e - $arr[$i].s)) -ne 3){ $i++; continue }
        # start a run of contiguous 32nds
        $j=$i; $pitches=New-Object System.Collections.ArrayList
        while($j -lt $arr.Count -and (Snap ($arr[$j].e-$arr[$j].s)) -eq 3 -and ($j -eq $i -or ($arr[$j].s - $arr[$j-1].e) -le 1)){ [void]$pitches.Add($arr[$j].p); $j++ }
        $len=$j-$i; $tot32+=$len
        if($len -ge 3){
          $distinct=($pitches | Select-Object -Unique).Count
          if($distinct -eq 1){ $tremolo+=$len }
          elseif($distinct -eq 2){ $trill+=$len
            $pp=($pitches|Select-Object -Unique); $iv=[math]::Abs($pp[0]-$pp[1]); $ivk=[math]::Min($iv,13); if(-not $trillIv.ContainsKey($ivk)){$trillIv[$ivk]=0}; $trillIv[$ivk]++ }
          else{ $real+=$len }
        } else { $isolated+=$len }
        $i=$j
      }
    }
  }
  if($tot32 -eq 0){ Write-Output ("{0}: no 32nds" -f $label); return }
  Write-Output ("==== {0} ====  (total 32nd notes={1})" -f $label,$tot32)
  Write-Output ("  tremolo (1 pitch repeated): {0:N0}%   trill/2-note: {1:N0}%   REAL figuration (>=3 pitches): {2:N0}%   isolated/short: {3:N0}%" -f (100*$tremolo/$tot32),(100*$trill/$tot32),(100*$real/$tot32),(100*$isolated/$tot32))
  if($trillIv.Count -gt 0){ $line="  2-note-run interval (semitones): "; $trillIv.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 4|ForEach-Object{ $line+=("{0}={1} " -f $_.Key,$_.Value) }; Write-Output $line }
}
$only=$args
foreach($d in (Get-ChildItem $midiRoot -Directory)){ if(($only.Count -eq 0) -or ($only -contains $d.Name)){ Analyze $d.FullName $d.Name } }
