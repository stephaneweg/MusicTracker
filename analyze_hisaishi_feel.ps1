# Hisaishi RHYTHMIC FEEL spectrum: per-piece melody density + duration profile, clustered slow <-> lively (airy).
$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm  = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$midiLoad = $asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$mszLoad  = $asm.GetType("MusicTracker.Engine.MuseScoreImporter").GetMethod("Load")
$dir = "C:\Users\swe\source\repos\MusicTracker\musescore\JoeHisaishi"
function LoadScore($file){ $ext=[System.IO.Path]::GetExtension($file).ToLower(); if($ext -eq ".mid"){$midiLoad.Invoke($null,@([string]$file))}else{$mszLoad.Invoke($null,@([string]$file)) } }
function GetTracks($score){ $tracks=$score.GetType().GetField("Tracks").GetValue($score); $out=New-Object System.Collections.ArrayList
  foreach($t in $tracks){ $tt=$t.GetType(); if([bool]$tt.GetField("IsDrum").GetValue($t)){continue}
    $arr=New-Object System.Collections.ArrayList; foreach($n in ($tt.GetField("Notes").GetValue($t))){ $nt=$n.GetType(); [void]$arr.Add([pscustomobject]@{ p=[int]$nt.GetField("Pitch").GetValue($n); s=[int]$nt.GetField("StartSlice").GetValue($n); l=[int]$nt.GetField("LengthSlices").GetValue($n) }) }
    if($arr.Count -gt 0){ [void]$out.Add($arr) } }
  return ,$out }
function Skyline($arr){ $g=$arr|Group-Object s|Sort-Object {[int]$_.Name}; $line=New-Object System.Collections.ArrayList; foreach($grp in $g){ [void]$line.Add(($grp.Group|Sort-Object p -Descending|Select-Object -First 1)) }; return ,$line }

$rows=New-Object System.Collections.ArrayList
$files = Get-ChildItem $dir -Recurse | Where-Object { $_.Extension -match '\.(mid|mscz)$' }
foreach($f in $files){
  try{ $score=LoadScore $f.FullName }catch{ continue }
  $st=$score.GetType(); $tsD=[int]$st.GetField("TimeSigD").GetValue($score); $bpm=[double]$st.GetField("Bpm").GetValue($score)
  $beat = if($tsD -eq 8){36}else{24}
  $tracks=GetTracks $score; if($tracks.Count -eq 0){continue}
  $means=@(); foreach($t in $tracks){ $means+=($t|ForEach-Object{$_.p}|Measure-Object -Average).Average }
  $hi=0; for($i=1;$i -lt $tracks.Count;$i++){ if($means[$i] -gt $means[$hi]){$hi=$i} }
  $mel = Skyline $tracks[$hi]; if($mel.Count -lt 8){ continue }
  $span = (($mel|ForEach-Object{$_.s}|Measure-Object -Maximum).Maximum)/[double]$beat; if($span -le 0){continue}
  $dens = $mel.Count/$span
  $short=0;$mid=0;$long=0;$fast=0
  foreach($e in $mel){ if($e.l -le 12){$short++}; if($e.l -ge 13 -and $e.l -le 36){$mid++}; if($e.l -ge 48){$long++}; if($e.l -le 8){$fast++} }
  [void]$rows.Add([pscustomobject]@{ name=$f.Name; bpm=$bpm; dens=[math]::Round($dens,2); shortP=[int](100*$short/$mel.Count); longP=[int](100*$long/$mel.Count); fastP=[int](100*$fast/$mel.Count) })
}
$sorted = $rows | Sort-Object dens
$n=$sorted.Count; $t=[int]($n/3)
"Pieces: $n  (melody density = notes per beat)`n"
"--- SLOW third (lowest density) ---"
$sorted | Select-Object -First $t | ForEach-Object { "  {0,-44} bpm={1,3:N0} dens={2,4} short={3,2}% long={4,2}% fast={5,2}%" -f ($_.name.Substring(0,[math]::Min(44,$_.name.Length))),$_.bpm,$_.dens,$_.shortP,$_.longP,$_.fastP }
"--- LIVELY/AIRY third (highest density) ---"
$sorted | Select-Object -Last $t | ForEach-Object { "  {0,-44} bpm={1,3:N0} dens={2,4} short={3,2}% long={4,2}% fast={5,2}%" -f ($_.name.Substring(0,[math]::Min(44,$_.name.Length))),$_.bpm,$_.dens,$_.shortP,$_.longP,$_.fastP }
function Avg($set,$prop){ ($set | ForEach-Object { $_.$prop } | Measure-Object -Average).Average }
$slow=$sorted|Select-Object -First $t; $mod=$sorted|Select-Object -Skip $t -First $t; $fastSet=$sorted|Select-Object -Last $t
"`n=== PROFILES (avg) ==="
"  LENT     : dens={0:N2}  short={1:N0}%  long={2:N0}%  fast={3:N0}%  bpm={4:N0}" -f (Avg $slow dens),(Avg $slow shortP),(Avg $slow longP),(Avg $slow fastP),(Avg $slow bpm)
"  MODERE   : dens={0:N2}  short={1:N0}%  long={2:N0}%  fast={3:N0}%  bpm={4:N0}" -f (Avg $mod dens),(Avg $mod shortP),(Avg $mod longP),(Avg $mod fastP),(Avg $mod bpm)
"  ENJOUE   : dens={0:N2}  short={1:N0}%  long={2:N0}%  fast={3:N0}%  bpm={4:N0}" -f (Avg $fastSet dens),(Avg $fastSet shortP),(Avg $fastSet longP),(Avg $fastSet fastP),(Avg $fastSet bpm)
