# Compose a Vivaldi concerto allegro (ritornello) from the vivaldi model (clean full pieces, in-memory).
#   compose_vivaldi.ps1 [seed]
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = "$repo\MusicTracker\bin\Debug"
$dirs = @("$repo\corpus\vivaldi\vivaldi_full", "$repo\corpus\vivaldi\vivaldi_seasons")
$seed = if ($args.Count -ge 1) { [int]$args[0] } else { 7 }
$mid  = "$repo\corpus\vivaldi_concerto.mid"

Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
$vc  = $asm.GetType("MusicTracker.Engine.ComposerV2.VivaldiComposer")
if ($null -eq $vc) { throw "VivaldiComposer not found - rebuild first." }

Write-Host "Analyzing clean Vivaldi (in-memory)..."
$argv = New-Object 'object[]' 1; $argv[0] = [string[]]$dirs
$model = $an.GetMethod("AnalyzeMany").Invoke($null, $argv)

$o = [System.Activator]::CreateInstance($vc)
$bc = $vc.GetMethod("Compose")
$bc.Invoke($o, @($model, [int]$seed, [string]$mid)) | Out-Null

$names = "C","C#","D","D#","E","F","F#","G","G#","A","A#","B"
$tonic = $names[([int]$vc.GetProperty("ChosenTonicPc").GetValue($o))]
$mode  = if ([bool]$vc.GetProperty("ChosenMinor").GetValue($o)) { "min" } else { "maj" }
Write-Host ("  -> concerto, {0} {1}  (seed {2})" -f $tonic, $mode, $seed)
$fi = Get-Item $mid
Write-Host ("MIDI     : {0}  ({1:N0} bytes)" -f $mid, $fi.Length)
$mscx = [System.IO.Path]::ChangeExtension($mid, ".mscx")
if (Test-Path $mscx) { Write-Host ("MuseScore: {0}" -f $mscx) }
