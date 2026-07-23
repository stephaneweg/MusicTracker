# Rest / "breathing" analysis: within each MONO voice, the GAPS between a note's end and the next note's onset.
# Distinguishes short articulation gaps (detache) from real rests/breaths (>= a quarter), and how much of the line
# is silence. CAVEAT: if a corpus shows ~0 silence, its MIDIs are legato-rendered (gaps not encoded) -> inconclusive.
$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
[void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin "NAudio.dll"))
$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $bin "MusicTracker.exe"))
$load = $asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$midiRoot = "C:\Users\swe\source\repos\MusicTracker\midi"
$grid = 3,6,8,12,18,24,36,48,72,96
function Snap($d){ ($grid | Sort-Object { [math]::Abs($_-$d) })[0] }
function DN($s){ switch($s){3{"32"}6{"16"}8{"8t"}12{"8"}18{"8."}24{"q"}36{"q."}48{"h"}72{"h."}96{"w"}default{"$s"} } }

function Analyze($dir,$label){
  $files = Get-ChildItem $dir -Filter *.mid
  $sound=0.0;$gap=0.0;$pairs=0;$detach=0;$breath=0;$restD=@{};$onBeat=0;$gapsTot=0
  foreach($f in $files){
    try{ $score=$load.Invoke($null,@($f.FullName)) }catch{ continue }
    $tracks=$score.GetType().GetField("Tracks").GetValue($score)
    foreach($t in $tracks){
      if($t.GetType().GetField("IsDrum").GetValue($t)){continue}
      $notes=$t.GetType().GetField("Notes").GetValue($t); if($notes.Count -lt 4){continue}
      $arr=@(); foreach($n in $notes){ $nt=$n.GetType(); $arr+=[pscustomobject]@{s=$nt.GetField("StartSlice").GetValue($n);e=$nt.GetField("StartSlice").GetValue($n)+$nt.GetField("LengthSlices").GetValue($n)} }
      $arr=$arr|Sort-Object s
      # mono only (a clean single line, so gaps are real rests of THAT voice)
      $mono=$true;$re=-1; foreach($e in $arr){ if($e.s -lt $re-2){$mono=$false;break}; if($e.e -gt $re){$re=$e.e} }
      if(-not $mono){continue}
      for($i=1;$i -lt $arr.Count;$i++){
        $sound += ($arr[$i-1].e - $arr[$i-1].s)
        $g = $arr[$i].s - $arr[$i-1].e
        $pairs++
        if($g -ge 3){ $detach++; $gap += $g; $gapsTot++; $sd=Snap $g; if(-not $restD.ContainsKey($sd)){$restD[$sd]=0}; $restD[$sd]++
          if($g -ge 24){$breath++}
          if(($arr[$i].s % 24) -eq 0){$onBeat++} }
      }
    }
  }
  if($pairs -eq 0){ Write-Output ("{0}: no mono data" -f $label); return }
  $span=$sound+$gap; if($span -le 0){$span=1}
  Write-Output ("==== {0} ====" -f $label)
  Write-Output ("  silence ratio (gap time / total): {0:N1}%   detached note-pairs (gap>=32nd): {1:N1}%" -f (100*$gap/$span),(100*$detach/$pairs))
  if($gapsTot -gt 0){
    Write-Output ("  of those gaps:  real breaths (>= quarter): {0:N0}%   landing the next note on a beat: {1:N0}%" -f (100*$breath/$gapsTot),(100*$onBeat/$gapsTot))
    $line="  top rest values:"; $restD.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 5|ForEach-Object{ $line += (" {0}={1:N0}%" -f (DN $_.Key),(100*$_.Value/$gapsTot)) }
    Write-Output $line
  }
}
$only=$args
foreach($d in (Get-ChildItem $midiRoot -Directory)){ if(($only.Count -eq 0) -or ($only -contains $d.Name)){ Analyze $d.FullName $d.Name } }
