# Find a violin seed that lands in D minor and check the leading-tone / Picardy spelling in the .mscx.
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = "$repo\MusicTracker\bin\Debug"
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
$bc  = $asm.GetType("MusicTracker.Engine.ComposerV2.BachSoloComposer")
$dirs = @("$repo\corpus\bach\solo_cello","$repo\corpus\bach\solo_violin","$repo\corpus\bach\solo_flute")
$argv = New-Object 'object[]' 1; $argv[0] = [string[]]$dirs
$model = $an.GetMethod("AnalyzeMany").Invoke($null, $argv)
$names = "C","C#","D","D#","E","F","F#","G","G#","A","A#","B"
$mid = "$repo\corpus\bach_dmin_probe.mid"
foreach ($seed in 1..30) {
  $o = [System.Activator]::CreateInstance($bc)
  $bc.GetProperty("Instrument").SetValue($o, "violin")
  $bc.GetMethod("Compose").Invoke($o, @($model, [int]$seed, [string]$mid)) | Out-Null
  $tonic = [int]$bc.GetProperty("ChosenTonicPc").GetValue($o)
  $isMin = [bool]$bc.GetProperty("ChosenMinor").GetValue($o)
  $mf = New-Object NAudio.Midi.MidiFile($mid, $false)
  $pcs = @{}
  foreach ($tr in $mf.Events) { foreach ($e in $tr) { $on = $e -as [NAudio.Midi.NoteOnEvent]; if ($null -ne $on -and $on.Velocity -gt 0) { $pcs[($on.NoteNumber % 12)] = $true } } }
  if ($tonic -eq 2 -and $isMin) {
    $x = Get-Content ([System.IO.Path]::ChangeExtension($mid,".mscx"))
    $db = ($x | Select-String -SimpleMatch "<tpc>9</tpc>").Count
    $cs = ($x | Select-String -SimpleMatch "<tpc>21</tpc>").Count
    $fs = ($x | Select-String -SimpleMatch "<tpc>20</tpc>").Count
    $used = ($pcs.Keys | Sort-Object | ForEach-Object { $names[$_] }) -join " "
    Write-Host ("seed {0}: D minor.  Db(tpc9)={1}  C#(tpc21)={2}  F#(tpc20)={3}" -f $seed, $db, $cs, $fs)
    Write-Host ("         pitch classes = {0}" -f $used)
    Copy-Item $mid "$repo\corpus\bach_solo_dmin.mid" -Force
    Copy-Item ([System.IO.Path]::ChangeExtension($mid,".mscx")) "$repo\corpus\bach_solo_dmin.mscx" -Force
    break
  }
}
