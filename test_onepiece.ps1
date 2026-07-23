# Reduced model from a SINGLE piece, then generate from it.
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = "$repo\MusicTracker\bin\Debug"
$seed = if ($args.Count -ge 1) { [int]$args[0] } else { 1234 }
$src  = if ($args.Count -ge 2) { [string]$args[1] } else { "$repo\corpus\Ghibli\joe_hisaishi-castle_in_the_sky_innocent-1986.mid" }
$skip = 0
if ($args.Count -ge 3) { if ($args[2] -eq "auto") { $skip = -1 } else { $skip = [double]$args[2] } }
$mid  = "$repo\corpus\ghibli_onepiece.mid"

Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
$gh  = $asm.GetType("MusicTracker.Engine.ComposerV2.GhibliComposer")

Write-Host ("Reduced model from ONE piece: {0}  (skip {1}s)" -f (Split-Path $src -Leaf), $skip)
if ($skip -ne 0) {
  $model = $an.GetMethod("AnalyzeOneFile").Invoke($null, @([string]$src, [double]$skip))
} else {
  $tmp = Join-Path $env:TEMP "ghibli_one"
  if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
  New-Item -ItemType Directory -Path $tmp | Out-Null
  Copy-Item $src $tmp
  $model = $an.GetMethod("Analyze").Invoke($null, @([string]$tmp))
}
$mt = $model.GetType()
$pieces = $mt.GetProperty("Pieces").GetValue($model)
foreach ($p in $pieces) {
  $pt = $p.GetType()
  "  analyzed: {0}  tonic={1} minor={2} bars={3} melNotes={4}" -f `
    $pt.GetProperty("File").GetValue($p), $pt.GetProperty("TonicPc").GetValue($p), $pt.GetProperty("Minor").GetValue($p), `
    $pt.GetProperty("Bars").GetValue($p), $pt.GetProperty("MelodyNotes").GetValue($p)
}
"  major pieces={0}  minor pieces={1}" -f $mt.GetProperty("MajorPieces").GetValue($model), $mt.GetProperty("MinorPieces").GetValue($model)

Write-Host ("Composing from the reduced model (seed {0})..." -f $seed)
$inst = [System.Activator]::CreateInstance($gh)
$gh.GetMethod("Compose").Invoke($inst, @($model, [int]$seed, [string]$mid))
$fi = Get-Item $mid
Write-Host ("MIDI: {0}  ({1:N0} bytes)" -f $mid, $fi.Length)
if (Test-Path ([System.IO.Path]::ChangeExtension($mid, ".mscx"))) { Write-Host ("MuseScore: {0}" -f ([System.IO.Path]::ChangeExtension($mid, ".mscx"))) }
