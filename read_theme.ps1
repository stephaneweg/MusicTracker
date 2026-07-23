# Read theme.mscz via the app's MuseScoreImporter and dump its notes (to understand the theme).
$ErrorActionPreference = "Stop"
$repo  = "C:\Users\swe\source\repos\MusicTracker"
$bin   = "$repo\MusicTracker\bin\Debug"
$theme = "C:\Users\swe\Documents\MuseScore3\Partitions\theme.mscz"

Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$imp = $asm.GetType("MusicTracker.Engine.MuseScoreImporter")
$score = $imp.GetMethod("Load").Invoke($null, @([string]$theme))
$st = $score.GetType()
function F($o,$n){ $o.GetType().GetField($n).GetValue($o) }

$names = @("Do","Do#","Ré","Mib","Mi","Fa","Fa#","Sol","Lab","La","Sib","Si")
"Bpm        : $(F $score 'Bpm')"
"TimeSig    : $(F $score 'TimeSigN')/$(F $score 'TimeSigD')  (specified=$(F $score 'HasTimeSig'))"
"KeyFifths  : $(F $score 'KeyFifths')   KeyIsMinor: $(F $score 'KeyIsMinor')"
"SliceCount : $(F $score 'SliceCount')  (24 slices = 1 noire)"
$meas = F $score 'MeasureStartSlices'
"Measures   : $([string]::Join(', ', $meas))"
$tracks = F $score 'Tracks'
"Tracks     : $($tracks.Count)"
for ($i=0; $i -lt $tracks.Count; $i++) {
  $t = $tracks[$i]
  $notes = F $t 'Notes'
  "`n--- Track $i : '$(F $t 'Name')'  prog=$(F $t 'GmProgram')  drum=$(F $t 'IsDrum')  notes=$($notes.Count) ---"
  foreach ($n in $notes) {
    $p = F $n 'Pitch'; $s = F $n 'StartSlice'; $l = F $n 'LengthSlices'
    $pc = (($p % 12) + 12) % 12; $oct = [math]::Floor($p / 12) - 1
    "  start={0,4}  len={1,3}  pitch={2,3} ({3}{4})  beat={5:N2}" -f $s,$l,$p,$names[$pc],$oct,($s/24.0)
  }
}
