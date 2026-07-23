$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$mid = if ($args.Count -ge 1) { $args[0] } else { "C:\Users\swe\source\repos\MusicTracker\corpus\ghibli_v2_proto.mid" }
$score = $asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load").Invoke($null, @([string]$mid))
$st = $score.GetType()
$tracks = $st.GetField("Tracks").GetValue($score)
$sc = $st.GetField("SliceCount").GetValue($score)
"SliceCount={0} (~{1} bars 4/4)  Tracks={2}" -f $sc, [math]::Round($sc/96,1), $tracks.Count

$idx = -1
foreach ($t in $tracks) {
  $idx++
  $tt = $t.GetType(); $name = $tt.GetField("Name").GetValue($t); $prog = $tt.GetField("GmProgram").GetValue($t)
  $notes = $tt.GetField("Notes").GetValue($t)
  $arr = @()
  foreach ($n in $notes) { $nt=$n.GetType(); $arr += [pscustomobject]@{ p=[int]$nt.GetField("Pitch").GetValue($n); s=[int]$nt.GetField("StartSlice").GetValue($n); l=[int]$nt.GetField("LengthSlices").GetValue($n) } }
  $arr = $arr | Sort-Object s
  $mn=($arr.p|Measure-Object -Minimum).Minimum; $mx=($arr.p|Measure-Object -Maximum).Maximum
  "  '{0}' prog={1} notes={2} range[{3}..{4}]" -f $name,$prog,$arr.Count,$mn,$mx
  if ($idx -eq 0) {
    $step=0;$rep=0;$third=0;$leap=0;$maxleap=0;$tot=0;$breaths=0
    for ($i=1; $i -lt $arr.Count; $i++) {
      $iv = [math]::Abs($arr[$i].p - $arr[$i-1].p); $tot++
      if ($iv -eq 0) { $rep++ } elseif ($iv -le 2) { $step++ } elseif ($iv -le 4) { $third++ } else { $leap++ }
      if ($iv -gt $maxleap) { $maxleap=$iv }
      $gap = $arr[$i].s - ($arr[$i-1].s + $arr[$i-1].l)
      if ($gap -ge 12) { $breaths++ }
    }
    if ($tot -eq 0) { $tot = 1 }
    "     MELODIE: step(<=2)={0}%  repeat={1}%  third(3-4)={2}%  leap(>4)={3}%  maxLeap={4}st  breaths(rests>=1/2beat)={5}" -f `
      [math]::Round(100*$step/$tot),[math]::Round(100*$rep/$tot),[math]::Round(100*$third/$tot),[math]::Round(100*$leap/$tot),$maxleap,$breaths
    "     longest notes (held cadences), top 5 lengths: " + (($arr | Sort-Object l -Descending | Select-Object -First 5).l -join ", ")
  }
}
