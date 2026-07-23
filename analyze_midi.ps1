# Analyze any MIDI folder(s) under .\midi using the app's own MidiImporter. One stats block per subfolder.
$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
[void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin "NAudio.dll"))
$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $bin "MusicTracker.exe"))
$load = $asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$midiRoot = "C:\Users\swe\source\repos\MusicTracker\midi"

$grid = 3,6,8,12,16,18,24,32,36,48,72,96
function SnapDur($d){ ($grid | Sort-Object { [math]::Abs($_ - $d) })[0] }
function DurName($s){ switch($s){3{"32nd"}6{"16th"}8{"8thTrip"}12{"8th"}16{"qtrTrip"}18{"dot8th"}24{"qtr"}32{"hlfTrip"}36{"dotQtr"}48{"half"}72{"dotHalf"}96{"whole"}default{"$s"} } }
function IvName($i){ switch($i){0{"unison"}1{"m2"}2{"M2"}3{"m3"}4{"M3"}5{"P4"}6{"tritone"}7{"P5"}8{"m6"}9{"M6"}10{"m7"}11{"M7"}12{"octave"}13{">oct"}default{"$i"} } }

function AnalyzeDir($dir, $label) {
  $files = Get-ChildItem $dir -Filter *.mid
  $c = [pscustomobject]@{ files=0; fail=0; voicesList=New-Object System.Collections.ArrayList; durs=@{}; ivals=@{};
    notesTotal=0; quartersTotal=0.0; pmin=200; pmax=0; entrySpreads=New-Object System.Collections.ArrayList; monoTracks=0; polyTracks=0 }
  foreach ($f in $files) {
    try { $score = $load.Invoke($null, @($f.FullName)) } catch { $c.fail++; continue }
    $st = $score.GetType(); $tracks = $st.GetField("Tracks").GetValue($score)
    $c.files++; [void]$c.voicesList.Add($tracks.Count)
    $firstOnsets = New-Object System.Collections.ArrayList
    foreach ($t in $tracks) {
      $notes = $t.GetType().GetField("Notes").GetValue($t); if ($notes.Count -eq 0) { continue }
      $arr = New-Object System.Collections.ArrayList
      foreach ($n in $notes) { $nt=$n.GetType(); [void]$arr.Add([pscustomobject]@{p=$nt.GetField("Pitch").GetValue($n);s=$nt.GetField("StartSlice").GetValue($n);l=$nt.GetField("LengthSlices").GetValue($n)}) }
      $sorted = $arr | Sort-Object s, p
      [void]$firstOnsets.Add($sorted[0].s)
      $mono = $true; $runEnd = -1
      foreach ($e in $sorted) { if ($e.s -lt $runEnd - 2) { $mono=$false; break }; $end=$e.s+$e.l; if ($end -gt $runEnd){$runEnd=$end} }
      if ($mono) { $c.monoTracks++ } else { $c.polyTracks++ }
      $prevP=$null; $maxS=0
      foreach ($e in $sorted) {
        $sd = SnapDur $e.l; if ($e.s -gt $maxS){$maxS=$e.s}
        if (-not $c.durs.ContainsKey($sd)){$c.durs[$sd]=0}; $c.durs[$sd]++
        if ($e.p -lt $c.pmin){$c.pmin=$e.p}; if ($e.p -gt $c.pmax){$c.pmax=$e.p}; $c.notesTotal++
        if ($mono -and $prevP -ne $null) { $ivk=[math]::Min([math]::Abs($e.p-$prevP),13); if(-not $c.ivals.ContainsKey($ivk)){$c.ivals[$ivk]=0}; $c.ivals[$ivk]++ }
        $prevP=$e.p
      }
      $c.quartersTotal += ($maxS/24.0)
    }
    if ($firstOnsets.Count -gt 1) { $sp=(($firstOnsets|Measure-Object -Maximum).Maximum-($firstOnsets|Measure-Object -Minimum).Minimum)/24.0; [void]$c.entrySpreads.Add($sp) }
  }
  Write-Output ("==================== {0}  ({1} pieces, {2} unparsed) ====================" -f $label, $c.files, $c.fail)
  $vl=$c.voicesList
  Write-Output ("Voices/piece: min={0} max={1} avg={2:N1}" -f ($vl|Measure-Object -Minimum).Minimum,($vl|Measure-Object -Maximum).Maximum,($vl|Measure-Object -Average).Average)
  Write-Output ("  distribution: " + (($vl|Group-Object|Sort-Object {[int]$_.Name}|ForEach-Object{"{0}v:{1}" -f $_.Name,$_.Count}) -join "  "))
  $dtot=($c.durs.Values|Measure-Object -Sum).Sum
  Write-Output "Rhythm (note-duration share):"
  $c.durs.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 8|ForEach-Object{ Write-Output ("  {0,-8} {1,5:N1}%" -f (DurName $_.Key),(100.0*$_.Value/$dtot)) }
  Write-Output ("Tracks: monophonic={0} polyphonic={1}  (melodic stats from mono only)" -f $c.monoTracks,$c.polyTracks)
  $itot=($c.ivals.Values|Measure-Object -Sum).Sum
  $rep=if($c.ivals.ContainsKey(0)){$c.ivals[0]}else{0}; $step=0; foreach($k in 1,2){if($c.ivals.ContainsKey($k)){$step+=$c.ivals[$k]}}; $leap=$itot-$rep-$step
  Write-Output ("Melodic motion: repeat={0:N1}%  step(<=M2)={1:N1}%  leap(>=m3)={2:N1}%" -f (100.0*$rep/$itot),(100.0*$step/$itot),(100.0*$leap/$itot))
  Write-Output "  top intervals:"
  $c.ivals.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 7|ForEach-Object{ Write-Output ("    {0,-8} {1,5:N1}%" -f (IvName $_.Key),(100.0*$_.Value/$itot)) }
  $dens=if($c.quartersTotal -gt 0){$c.notesTotal/$c.quartersTotal}else{0}
  Write-Output ("Density: {0:N2} notes per quarter per voice;  pitch range MIDI {1}..{2}" -f $dens,$c.pmin,$c.pmax)
  if ($c.entrySpreads.Count -gt 0){ Write-Output ("Voice-entry stagger (quarters): avg={0:N1}" -f (($c.entrySpreads|Measure-Object -Average).Average)) }
  Write-Output ""
}

$only = if ($args.Count -gt 0) { $args[0] } else { $null }   # optional: analyze just one subfolder
foreach ($d in (Get-ChildItem $midiRoot -Directory)) { if (-not $only -or $d.Name -eq $only) { AnalyzeDir $d.FullName $d.Name } }
