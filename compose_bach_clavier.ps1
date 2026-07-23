# Compose a Bach KEYBOARD piece (WTC style) from the bach_clavier model (analyzed in-memory from corpus/bach/wtc).
#   compose_bach_clavier.ps1 [seed] [prelude|fugue]
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = "$repo\MusicTracker\bin\Debug"
$dir  = "$repo\corpus\bach\wtc"

$seed = if ($args.Count -ge 1) { [int]$args[0] } else { 7 }
$mvt  = if ($args.Count -ge 2) { [string]$args[1] } else { "" }
$tag  = if ([string]::IsNullOrEmpty($mvt)) { "auto" } else { $mvt }
$mid  = "$repo\corpus\bach_clavier_$tag.mid"

Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
$bc  = $asm.GetType("MusicTracker.Engine.ComposerV2.BachClavierComposer")
if ($null -eq $bc) { throw "BachClavierComposer not found - rebuild first." }

Write-Host "Analyzing Bach WTC (in-memory)..."
$model = $an.GetMethod("Analyze").Invoke($null, @([string]$dir))

$o = [System.Activator]::CreateInstance($bc)
if (-not [string]::IsNullOrEmpty($mvt)) { $bc.GetProperty("Movement").SetValue($o, [string]$mvt) }
$bc.GetMethod("Compose").Invoke($o, @($model, [int]$seed, [string]$mid)) | Out-Null

$names = "C","C#","D","D#","E","F","F#","G","G#","A","A#","B"
$tonic = $names[([int]$bc.GetProperty("ChosenTonicPc").GetValue($o))]
$mode  = if ([bool]$bc.GetProperty("ChosenMinor").GetValue($o)) { "min" } else { "maj" }
$m     = [string]$bc.GetProperty("ChosenMovement").GetValue($o)
Write-Host ("  -> {0}, {1} {2}  (seed {3})" -f $m, $tonic, $mode, $seed)
$fi = Get-Item $mid
Write-Host ("MIDI     : {0}  ({1:N0} bytes)" -f $mid, $fi.Length)
$mscx = [System.IO.Path]::ChangeExtension($mid, ".mscx")
if (Test-Path $mscx) { Write-Host ("MuseScore: {0}" -f $mscx) }
