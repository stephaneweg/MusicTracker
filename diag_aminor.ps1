# Reproduce an A-minor Ghibli V2 piece and dump the real pitches in bars 4-7 (= measures 5-8), per part,
# flagging any A# / Bb (pitch class 10) — to locate the reported out-of-key note.
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = "$repo\MusicTracker\bin\Debug"
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
$gh  = $asm.GetType("MusicTracker.Engine.ComposerV2.GhibliComposer")
$model = $an.GetMethod("Analyze").Invoke($null, @([string]"$repo\corpus\Ghibli"))
$mid = "$repo\corpus\_diag_aminor.mid"
$names = "C","C#","D","D#","E","F","F#","G","G#","A","A#","B"

$seed = -1
foreach ($s in 1..60) {
  $o = [System.Activator]::CreateInstance($gh)
  $o.GetType().GetProperty("CharacterOverride").SetValue($o, "calme")  # calm tends to pick a minor mode
  $gh.GetMethod("Compose").Invoke($o, @($model, [int]$s, [string]$mid)) | Out-Null
  if ([string]$o.GetType().GetProperty("ChosenMode").GetValue($o) -eq "aeolian") { $seed = $s; break }
}
if ($seed -lt 0) { Write-Host "no aeolian seed found"; exit }
Write-Host ("A-minor (aeolian) piece: seed $seed`n")

$mf = New-Object NAudio.Midi.MidiFile($mid, $false)
$ppq = $mf.DeltaTicksPerQuarterNote; $barT = 4 * $ppq
for ($tr = 1; $tr -lt $mf.Tracks; $tr++) {
  $trName = ""
  foreach ($e in $mf.Events[$tr]) { $te = $e -as [NAudio.Midi.TextEvent]; if ($null -ne $te -and $te.MetaEventType -eq [NAudio.Midi.MetaEventType]::SequenceTrackName) { $trName = $te.Text; break } }
  for ($bar = 4; $bar -le 6; $bar++) {
    $line = @()
    foreach ($e in $mf.Events[$tr]) {
      $on = $e -as [NAudio.Midi.NoteOnEvent]
      if ($null -ne $on -and $on.Velocity -gt 0) {
        $b = [int][math]::Floor($on.AbsoluteTime / $barT)
        if ($b -eq $bar) { $pc = $on.NoteNumber % 12; $mark = if ($pc -eq 10) { "<<A#/Bb" } else { "" }; $line += ("{0}{1}{2}" -f $names[$pc], $on.NoteNumber, $mark) }
      }
    }
    if ($line.Count -gt 0) { Write-Host ("  m{0} [{1,-18}]: {2}" -f ($bar+1), $trName, ($line -join " ")) }
  }
}
