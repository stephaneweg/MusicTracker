# Verify the new ComposeInMemory path: analyze bach_solo in-memory, run BachSoloComposer.ComposeInMemory,
# and report the returned V2Piece (parts + note counts + key/meter/tempo) — the data the app turns into riffs.
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = "$repo\MusicTracker\bin\Debug"
$dirs = @("$repo\corpus\bach\solo_cello", "$repo\corpus\bach\solo_violin", "$repo\corpus\bach\solo_flute")
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
$bc  = $asm.GetType("MusicTracker.Engine.ComposerV2.BachSoloComposer")
$argv = New-Object 'object[]' 1; $argv[0] = [string[]]$dirs
$model = $an.GetMethod("AnalyzeMany").Invoke($null, $argv)

$o = [System.Activator]::CreateInstance($bc)
$o.GetType().GetProperty("Movement").SetValue($o, "sarabande")
$piece = $bc.GetMethod("ComposeInMemory").Invoke($o, @($model, [int]7))
$pt = $piece.GetType()
function F($obj, $name) { return $obj.GetType().GetField($name).GetValue($obj) }
Write-Host ("V2Piece: bpm={0} meter={1}/{2} tonicLetter={3} minor={4} totalSlices={5}" -f `
  (F $piece "Bpm"), (F $piece "MeterNum"), (F $piece "MeterDen"), (F $piece "TonicLetter"), (F $piece "Minor"), (F $piece "TotalSlices"))
$parts = F $piece "Parts"
foreach ($p in $parts) {
  $nl = F $p "Notes"
  Write-Host ("  part: prog={0,3} drum={1,-5} name='{2}'  notes={3}" -f (F $p "Program"), (F $p "IsDrum"), (F $p "Name"), $nl.Count)
}
