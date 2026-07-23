# Read theme.mscz, then use the V1 GhibliComposer.BuildVariations (by reflection) to make variations in G minor.
# Writes a MIDI: [original theme + pickup] then [4 variations], at 60 bpm 4/4.
$ErrorActionPreference = "Stop"
$repo  = "C:\Users\swe\source\repos\MusicTracker"
$bin   = "$repo\MusicTracker\bin\Debug"
$theme = "C:\Users\swe\Documents\MuseScore3\Partitions\theme.mscz"
$outMid = "C:\Users\swe\Documents\MuseScore3\Partitions\theme_variations.mid"

Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
function F($o,$n){ $o.GetType().GetField($n).GetValue($o) }

# ---- 1. import the theme ----
$imp = $asm.GetType("MusicTracker.Engine.MuseScoreImporter")
$score = $imp.GetMethod("Load").Invoke($null, @([string]$theme))
$tracks = F $score 'Tracks'
$notes = F $tracks[0] 'Notes'

$BAR = 96; $THEME_AT = 96   # bar 1 downbeat (the 4-bar theme proper; the 2 notes before are the pickup)

# original notes (MIDI pitch, slice, len) — kept verbatim for the reference playback
$orig = @()
foreach ($n in $notes) { $orig += ,@([int](F $n 'Pitch'), [int](F $n 'StartSlice'), [int](F $n 'LengthSlices')) }

# ---- 2. build the 4-bar theme as List<RiffNote> (RiffNote.Note = MIDI-12, Start relative to bar 1) ----
$rnT  = $asm.GetType("MusicTracker.Engine.RiffNote")
$themeList = [Activator]::CreateInstance([System.Collections.Generic.List`1].MakeGenericType($rnT))
foreach ($n in $notes) {
  $s = [int](F $n 'StartSlice')
  if ($s -lt $THEME_AT) { continue }                      # skip the pickup for the engine
  $themeList.Add([Activator]::CreateInstance($rnT, @([int]((F $n 'Pitch') - 12), [int]($s - $THEME_AT), [int](F $n 'LengthSlices')))) | Out-Null
}

# ---- 3. G minor scale (G A Bb C D Eb F) + same-key dev offsets (true variations, not a modulation) ----
$scale = [Activator]::CreateInstance([System.Collections.Generic.HashSet`1].MakeGenericType([int]))
foreach ($pc in 7,9,10,0,2,3,5) { [void]$scale.Add([int]$pc) }
$devKeys = [int[]]@(0,0,0,0)
$rng = [Activator]::CreateInstance([System.Random], @([int]7))   # Activator → raw object (New-Object wraps in PSObject, which reflection Invoke rejects)

# ---- 4. call the V1 variation engine: BuildVariations(theme, devMult, themeB, barSlices, chordSlices, ternary, scale, devKeys, rng) ----
$gh = $asm.GetType("MusicTracker.Engine.Timeline.GhibliComposer")
$bv = $gh.GetMethod("BuildVariations", ([System.Reflection.BindingFlags]"Static,NonPublic,Public"))
$argv = New-Object 'object[]' 9
$argv[0] = $themeList; $argv[1] = [int]4; $argv[2] = [int]4; $argv[3] = [int]$BAR; $argv[4] = [int]$BAR
$argv[5] = $false; $argv[6] = $scale; $argv[7] = $devKeys; $argv[8] = $rng
$vars = $bv.Invoke($null, $argv)
Write-Host "Theme notes (4 bars): $($themeList.Count)   Variation notes: $($vars.Count)"

# ---- 5. assemble the output note list: original (verbatim) + variations after a 1-bar gap, each with the pickup ----
# [midi, startSlice, lenSlice, vel]
$out = New-Object System.Collections.ArrayList
foreach ($o in $orig) { [void]$out.Add(@($o[0], $o[1], $o[2], 82)) }     # original theme + pickup, verbatim
$VAR_BASE = 6 * $BAR                                                      # variations start at bar 7 (clear gap after the held final)
$fNote = $rnT.GetField('Note'); $fStart = $rnT.GetField('Start'); $fLen = $rnT.GetField('Length')
for ($k = 0; $k -lt $vars.Count; $k++) {
  $v = $vars[$k]
  $note = [int]$fNote.GetValue($v); $st = [int]$fStart.GetValue($v); $ln = [int]$fLen.GetValue($v)
  [void]$out.Add(@(($note + 12), ($VAR_BASE + $st), $ln, 76))
}
# pickup (Sol5-La5) before each variation's downbeat
for ($r = 0; $r -lt 4; $r++) {
  $b = [int]$VAR_BASE + [int]$r * 4 * [int]$BAR
  [void]$out.Add(@(79, ([int]$b - 24), 12, 70))
  [void]$out.Add(@(81, ([int]$b - 12), 12, 70))
}

# ---- 6. write the MIDI (480 ppq, 60 bpm, type 1) ----
$ppq = 480; $tickPerSlice = $ppq / 24
$col = New-Object NAudio.Midi.MidiEventCollection -ArgumentList 1, $ppq
$col.AddEvent((New-Object NAudio.Midi.TempoEvent -ArgumentList ([int]1000000), ([long]0)), 1) | Out-Null   # 60 bpm
foreach ($e in $out) {
  $midi=[int]$e[0]; $start=[int]$e[1]; $len=[int]$e[2]; $vel=[int]$e[3]
  if ($midi -lt 0 -or $midi -gt 127 -or $start -lt 0) { continue }
  $on = New-Object NAudio.Midi.NoteOnEvent -ArgumentList ([long]($start*$tickPerSlice)), 1, $midi, $vel, ([int]($len*$tickPerSlice))
  $col.AddEvent($on, 1) | Out-Null
  $col.AddEvent($on.OffEvent, 1) | Out-Null
}
[NAudio.Midi.MidiFile]::Export($outMid, $col)
Write-Host "Wrote: $outMid"
