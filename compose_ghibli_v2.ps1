# Composer V2 - PROTOTYPE: analyze the corpus in-memory, then compose a piece and export MIDI.
# (No JSON round-trip: we pass the live CorpusModelV2 object straight into the generator.)
$ErrorActionPreference = "Stop"
$repo   = "C:\Users\swe\source\repos\MusicTracker"
$bin    = "$repo\MusicTracker\bin\Debug"
$corpus = "$repo\corpus\Ghibli"
$midi   = "$repo\corpus\ghibli_v2_proto.mid"
$seed   = if ($args.Count -ge 1) { [int]$args[0] } else { 1234 }

Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")

$an = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
$gh = $asm.GetType("MusicTracker.Engine.ComposerV2.GhibliComposer")
if ($null -eq $gh) { throw "GhibliComposer not found - rebuild first." }

Write-Host "Analyzing corpus (in-memory)..."
$model = $an.GetMethod("Analyze").Invoke($null, @([string]$corpus))

Write-Host ("Composing (seed {0})..." -f $seed)
$inst = [System.Activator]::CreateInstance($gh)
$gh.GetMethod("Compose").Invoke($inst, @($model, [int]$seed, [string]$midi))

$mscx = [System.IO.Path]::ChangeExtension($midi, ".mscx")
$fi = Get-Item $midi
Write-Host ""
Write-Host ("MIDI     : {0}  ({1:N0} bytes)" -f $midi, $fi.Length)
if (Test-Path $mscx) { Write-Host ("MuseScore: {0}  ({1:N0} bytes)" -f $mscx, (Get-Item $mscx).Length) }
Write-Host "Open the .mid in any player to listen, or the .mscx in MuseScore 3/4."
