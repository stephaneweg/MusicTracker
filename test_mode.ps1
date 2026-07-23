$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
$gh = $asm.GetType("MusicTracker.Engine.ComposerV2.GhibliComposer")
$model = $an.GetMethod("Analyze").Invoke($null, @([string]"C:\Users\swe\source\repos\MusicTracker\corpus\Ghibli"))
$compose = $gh.GetMethod("Compose")
$maj = 0; $min = 0
for ($s = 1; $s -le 16; $s++) {
  $mid = Join-Path $env:TEMP ("mode_" + $s + ".mid")
  $inst = [System.Activator]::CreateInstance($gh)
  $compose.Invoke($inst, @($model, [int]$s, [string]$mid)) | Out-Null
  $mscx = [System.IO.Path]::ChangeExtension($mid, ".mscx")
  $line = (Select-String -Path $mscx -Pattern '<accidental>' | Select-Object -First 1).Line
  $acc = 0; if ($line -match '<accidental>(-?\d+)</accidental>') { $acc = [int]$Matches[1] }
  $mode = if ($acc -eq 0) { "mineur (eolien)" } else { "MAJEUR" }
  if ($acc -eq 0) { $min++ } else { $maj++ }
  "seed {0}: armure={1} -> {2}" -f $s, $acc, $mode
}
"`nTotal: {0} majeur / {1} mineur (sur 16)" -f $maj, $min
