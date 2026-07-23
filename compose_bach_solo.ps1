# Compose a Bach SOLO piece from the bach_solo model (analyzed in-memory from corpus/bach/solo_*).
#   compose_bach_solo.ps1 [seed] [violin|cello|flute] [solo|continuo] [movement]
#   movement = prelude|allemande|courante|sarabande|gigue|menuet|bourree|gavotte|chaconne
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = "$repo\MusicTracker\bin\Debug"
$dirs = @("$repo\corpus\bach\solo_cello", "$repo\corpus\bach\solo_violin", "$repo\corpus\bach\solo_flute")

$seed     = if ($args.Count -ge 1) { [int]$args[0] } else { 7 }
$inst     = if ($args.Count -ge 2) { [string]$args[1] } else { "" }
$continuo = if ($args.Count -ge 3 -and $args[2] -eq "continuo") { $true } else { $false }
$movement = if ($args.Count -ge 4) { [string]$args[3] } else { "" }
$tag      = if ([string]::IsNullOrEmpty($inst)) { "auto" } else { $inst }
$tag      = if ([string]::IsNullOrEmpty($movement)) { $tag } else { "$tag" + "_$movement" }
$tag      = if ($continuo) { "$tag" + "_continuo" } else { $tag }
$mid      = "$repo\corpus\bach_solo_$tag.mid"

Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
$bc  = $asm.GetType("MusicTracker.Engine.ComposerV2.BachSoloComposer")
if ($null -eq $bc) { throw "BachSoloComposer not found - rebuild first." }

Write-Host "Analyzing Bach solo corpus (in-memory)..."
# wrap the string[] as a SINGLE element so reflection binds it to the one string[] parameter
# (a bare @([string[]]$dirs) unrolls the array into 3 separate args)
$argv = New-Object 'object[]' 1
$argv[0] = [string[]]$dirs
$model = $an.GetMethod("AnalyzeMany").Invoke($null, $argv)

Write-Host ("Composing Bach solo (seed {0}, inst '{1}', continuo {2})..." -f $seed, $tag, $continuo)
$o = [System.Activator]::CreateInstance($bc)
if (-not [string]::IsNullOrEmpty($inst)) { $bc.GetProperty("Instrument").SetValue($o, [string]$inst) }
if (-not [string]::IsNullOrEmpty($movement)) { $bc.GetProperty("Movement").SetValue($o, [string]$movement) }
$bc.GetProperty("WithContinuo").SetValue($o, [bool]$continuo)
$bc.GetMethod("Compose").Invoke($o, @($model, [int]$seed, [string]$mid)) | Out-Null

$names = "C","C#","D","D#","E","F","F#","G","G#","A","A#","B"
$tonic = $names[([int]$bc.GetProperty("ChosenTonicPc").GetValue($o))]
$mode  = if ([bool]$bc.GetProperty("ChosenMinor").GetValue($o)) { "min" } else { "maj" }
$mvt   = [string]$bc.GetProperty("ChosenMovement").GetValue($o)
Write-Host ("  -> {0}, {1} {2}" -f $mvt, $tonic, $mode)
$fi = Get-Item $mid
Write-Host ("MIDI     : {0}  ({1:N0} bytes)" -f $mid, $fi.Length)
$mscx = [System.IO.Path]::ChangeExtension($mid, ".mscx")
if (Test-Path $mscx) { Write-Host ("MuseScore: {0}  ({1:N0} bytes)" -f $mscx, (Get-Item $mscx).Length) }
