$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
$gh = $asm.GetType("MusicTracker.Engine.ComposerV2.GhibliComposer")
$midiLoad = $asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$model = $an.GetMethod("Analyze").Invoke($null, @([string]"C:\Users\swe\source\repos\MusicTracker\corpus\Ghibli"))
$compose = $gh.GetMethod("Compose")
for ($s = 1; $s -le 8; $s++) {
  $mid = Join-Path $env:TEMP ("g_" + $s + ".mid")
  $inst = [System.Activator]::CreateInstance($gh)
  $compose.Invoke($inst, @($model, [int]$s, [string]$mid)) | Out-Null
  $score = $midiLoad.Invoke($null, @([string]$mid))
  $tracks = $score.GetType().GetField("Tracks").GetValue($score)
  $drum = 0
  foreach ($t in $tracks) { if ([bool]$t.GetType().GetField("IsDrum").GetValue($t)) { $drum = 1 } }
  "seed {0}: tracks={1}  drums={2}" -f $s, $tracks.Count, $drum
}
