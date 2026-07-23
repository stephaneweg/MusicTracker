# Smoke-test ComposeInMemory for Ghibli V2 (forced character) and Vivaldi — confirms the in-memory path
# (the one the app uses after LoadModel) returns populated pieces for these styles too.
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = "$repo\MusicTracker\bin\Debug"
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
$names = "C","C#","D","D#","E","F","F#","G","G#","A","A#","B"
function F($o,$n){ return $o.GetType().GetField($n).GetValue($o) }
function Show($tag,$piece){
  Write-Host ("{0}: bpm={1} meter={2}/{3} minor={4} bars={5} parts={6}" -f $tag, (F $piece "Bpm"), (F $piece "MeterNum"), (F $piece "MeterDen"), (F $piece "Minor"), ([math]::Ceiling((F $piece "TotalSlices")/((F $piece "MeterNum")*96/(F $piece "MeterDen")))), (F $piece "Parts").Count)
  foreach($p in (F $piece "Parts")){ Write-Host ("    prog={0,3} drum={1,-5} '{2}' notes={3}" -f (F $p "Program"),(F $p "IsDrum"),(F $p "Name"),(F $p "Notes").Count) }
}

# Ghibli V2 (forced enjoué)
$gh = $asm.GetType("MusicTracker.Engine.ComposerV2.GhibliComposer")
$ghModel = $an.GetMethod("Analyze").Invoke($null, @([string]"$repo\corpus\Ghibli"))
$g = [System.Activator]::CreateInstance($gh)
$gh.GetProperty("CharacterOverride").SetValue($g, "enjouee")
Show "Ghibli V2 (enjoué)" ($gh.GetMethod("ComposeInMemory").Invoke($g, @($ghModel, [int]4)))

# Vivaldi
$vc = $asm.GetType("MusicTracker.Engine.ComposerV2.VivaldiComposer")
$dirs = @("$repo\corpus\vivaldi\vivaldi_full","$repo\corpus\vivaldi\vivaldi_seasons")
$argv = New-Object 'object[]' 1; $argv[0] = [string[]]$dirs
$vModel = $an.GetMethod("AnalyzeMany").Invoke($null, $argv)
$v = [System.Activator]::CreateInstance($vc)
Show "Vivaldi" ($vc.GetMethod("ComposeInMemory").Invoke($v, @($vModel, [int]7)))
