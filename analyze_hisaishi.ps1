# Hisaishi corpus analysis. Loads BOTH .mid (MidiImporter) and .mscz (MuseScoreImporter) into the
# common MuseScoreImporter.Score, then analyzes melody / harmony / rhythm / dynamics / balance.
$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm  = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$midiLoad = $asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$mszLoad  = $asm.GetType("MusicTracker.Engine.MuseScoreImporter").GetMethod("Load")
$dir = "C:\Users\swe\source\repos\MusicTracker\musescore\JoeHisaishi"

function LoadScore($file) {
  $ext = [System.IO.Path]::GetExtension($file).ToLower()
  if ($ext -eq ".mid") { return $midiLoad.Invoke($null, @([string]$file)) }
  else { return $mszLoad.Invoke($null, @([string]$file)) }
}

# Score -> array of tracks; each track = arraylist of {p,s,l,v}
function GetTracks($score) {
  $st = $score.GetType()
  $tracks = $st.GetField("Tracks").GetValue($score)
  $out = New-Object System.Collections.ArrayList
  foreach ($t in $tracks) {
    $tt = $t.GetType()
    $notes = $tt.GetField("Notes").GetValue($t)
    $nm = $tt.GetField("Name").GetValue($t)
    $gm = $tt.GetField("GmProgram").GetValue($t)
    $arr = New-Object System.Collections.ArrayList
    foreach ($n in $notes) {
      $nt = $n.GetType()
      [void]$arr.Add([pscustomobject]@{
        p = [int]$nt.GetField("Pitch").GetValue($n)
        s = [int]$nt.GetField("StartSlice").GetValue($n)
        l = [int]$nt.GetField("LengthSlices").GetValue($n)
        v = [int]$nt.GetField("Velocity").GetValue($n)
      })
    }
    [void]$out.Add([pscustomobject]@{ name=$nm; gm=$gm; notes=$arr })
  }
  return ,$out
}

function MonoRatio($arr) {
  # fraction of onsets that overlap a still-sounding note (0 = perfectly monophonic)
  if ($arr.Count -lt 2) { return 0.0 }
  $sorted = $arr | Sort-Object s, p
  $overlap = 0; $runEnd = -1
  foreach ($e in $sorted) { if ($e.s -lt $runEnd - 2) { $overlap++ }; $end=$e.s+$e.l; if ($end -gt $runEnd){$runEnd=$end} }
  return [double]$overlap / $sorted.Count
}

$files = Get-ChildItem $dir | Where-Object { $_.Extension -match '\.(mid|mscz)$' } | Sort-Object Extension, Name
"Found $($files.Count) files`n"
foreach ($f in $files) {
  try { $score = LoadScore $f.FullName } catch { "FAIL  $($f.Name) : $($_.Exception.Message)"; continue }
  $st = $score.GetType()
  $bpm = $st.GetField("Bpm").GetValue($score)
  $tsN = $st.GetField("TimeSigN").GetValue($score)
  $tsD = $st.GetField("TimeSigD").GetValue($score)
  $kf  = $st.GetField("KeyFifths").GetValue($score)
  $km  = $st.GetField("KeyIsMinor").GetValue($score)
  $msl = $st.GetField("MeasureStartSlices").GetValue($score)
  $bars = if ($msl) { $msl.Count } else { 0 }
  $tracks = GetTracks $score
  $totalNotes = ($tracks | ForEach-Object { $_.notes.Count } | Measure-Object -Sum).Sum
  $kfs = if ($kf -ne $null) { "$kf" } else { "?" }
  $kms = if ($km -ne $null) { if ($km) {"min"} else {"maj"} } else { "?" }
  "=== [$($f.Extension.TrimStart('.'))] $($f.Name)"
  "    bpm=$bpm  ts=$tsN/$tsD  keyFifths=$kfs/$kms  bars=$bars  tracks=$($tracks.Count)  notes=$totalNotes"
  $ti = 0
  foreach ($t in $tracks) {
    if ($t.notes.Count -eq 0) { "      [$ti] '$($t.name)' gm=$($t.gm)  (empty)"; $ti++; continue }
    $ps = $t.notes | ForEach-Object { $_.p }
    $pmin = ($ps | Measure-Object -Minimum).Minimum
    $pmax = ($ps | Measure-Object -Maximum).Maximum
    $pmean = ($ps | Measure-Object -Average).Average
    $mr = MonoRatio $t.notes
    $monoTag = if ($mr -lt 0.10) { "MONO" } else { "poly($([math]::Round($mr*100))%)" }
    "      [$ti] '$($t.name)' gm=$($t.gm)  n=$($t.notes.Count)  pitch=$pmin..$pmax mean=$([math]::Round($pmean))  $monoTag"
    $ti++
  }
  ""
}
