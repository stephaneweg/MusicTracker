# Hand-composed (by Claude) ORCHESTRAL Ghibli-style film cue, emitted as MuseScore 3 .mscx (same format as
# MuseScoreExporter / compose_ghibli2.ps1). Key: D major (warm, violin-friendly), meter 6/8, gently swaying Andante.
# Brief: orchestral fantasy score, tender violin+piano duo; verse = sparse piano arpeggios + solo violin;
# midpoint opens into warm strings, harp, woodwinds (rising); final climax = full orchestra, soaring violin,
# low swells, choir-like strings; then a luminous resolve.
# FORM (34 bars): Intro 4 | Verse/theme 8 (duo) | Midpoint 8 (opens up + build) | Climax 8 (tutti) | Outro 6.
# Token = "<midi[+midi...] or r>/<dur>"; dur: 1 whole, 2 half, 4 quarter, 8 eighth, 16 sixteenth; trailing "." = dotted.
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$out  = "$repo\ghibli_orchestral.mscx"
$title = "Au coeur des nuages - fresque orchestrale style Ghibli (Claude)"

# ===================== HARMONY PLAN (34 bars) : root pitch-class, quality, bass MIDI =====================
# D major, warm/modal colour (add9, maj7, sus, mediants); one bVII (C maj9) "ocean swell" at the climax.
$chords = @(
  # -- Intro (idx 0-3) --
  @{r=2;  q='add9'; b=38}, @{r=2;  q='add9'; b=38}, @{r=7; q='maj7'; b=43}, @{r=9; q='sus4'; b=45},
  # -- Verse / theme loop (idx 4-11) :  Dadd9 F#m7 Gmaj7 Asus | Bm7 Gadd9 Em7 Asus --
  @{r=2;  q='add9'; b=38}, @{r=6;  q='m7';  b=42}, @{r=7; q='maj7'; b=43}, @{r=9; q='sus4'; b=45},
  @{r=11; q='m7';   b=47}, @{r=7;  q='add9'; b=43}, @{r=4; q='m7';   b=40}, @{r=9; q='sus4'; b=45},
  # -- Midpoint loop + 2-bar build (idx 12-19) --
  @{r=2;  q='add9'; b=38}, @{r=6;  q='m7';  b=42}, @{r=7; q='maj7'; b=43}, @{r=9; q='sus4'; b=45},
  @{r=11; q='m7';   b=47}, @{r=7;  q='add9'; b=43}, @{r=7; q='maj7'; b=43}, @{r=9; q='sus4'; b=45},
  # -- Climax / tutti (idx 20-27) : Dadd9 F#m7 Gmaj7 A | Bm7 Cmaj9(bVII swell) Gadd9 Asus --
  @{r=2;  q='add9'; b=38}, @{r=6;  q='m7';  b=42}, @{r=7; q='maj7'; b=43}, @{r=9; q='maj';  b=45},
  @{r=11; q='m7';   b=47}, @{r=0;  q='maj9'; b=36}, @{r=7; q='add9'; b=43}, @{r=9; q='sus4'; b=45},
  # -- Outro (idx 28-33) : Gmaj7 D/F# Em7 Asus | A Dadd9(final) --
  @{r=7;  q='maj7'; b=43}, @{r=2;  q='maj'; b=42}, @{r=4; q='m7';   b=40}, @{r=9; q='sus4'; b=45},
  @{r=9;  q='maj';  b=45}, @{r=2;  q='add9'; b=38}
)
$N = $chords.Count   # 34

# ===================== VIOLIN (lead) =====================
# 8-bar lyrical THEME over the verse loop (arch contour, pentatonic-leaning, breathing on sustained 5th/2nd).
$T = @(
  "69/4. 74/4.",                  # 1 Dadd9 : A4 -> D5      (hopeful rising 5th)
  "73/8 74/8 73/8 69/8 66/4",     # 2 F#m7  : C#5 D5 C#5 A4 | F#4  (lyrical fall)
  "71/8 69/8 67/8 69/8 71/4",     # 3 Gmaj7 : B4 A4 G4 A4 | B4   (gentle wave)
  "69/2.",                        # 4 Asus  : A4 held       (breath, suspended 5th)
  "74/8 76/8 78/8 76/8 74/4",     # 5 Bm7   : D5 E5 F#5 E5 | D5  (rise to F#5 peak)
  "71/8 74/8 71/8 69/8 67/4",     # 6 Gadd9 : B4 D5 B4 A4 | G4
  "69/8 71/8 69/8 67/8 66/4",     # 7 Em7   : A4 B4 A4 G4 | F#4  (settle)
  "64/2."                         # 8 Asus  : E4 held       (suspensive 2nd, loops)
)
function Transpose($tokstr, $semi) {
  (($tokstr -split ' ') | ForEach-Object {
    $p, $d = $_ -split '/'
    if ($p -eq 'r') { "$p/$d" }
    else { ((($p -split '\+') | ForEach-Object { [int]$_ + [int]$semi }) -join '+') + "/$d" }
  }) -join ' '
}
$violin = @()
$violin += @("r/2.", "r/2.", "r/2.", "r/2 64/8 66/8")          # intro: silence, then E4 F#4 pickup
$violin += $T                                                  # verse: theme
$violin += $T[0..5]                                            # midpoint: theme (6 bars)
$violin += @("69/8 71/8 73/8 74/8 76/8 78/8",                  #   build bar 7: A4 B4 C#5 D5 E5 F#5 rising
             "78/8 79/8 81/8 83/8 86/4")                       #   build bar 8: F#5 G5 A5 B5 -> D6 leap
foreach ($t in $T) { $violin += (Transpose $t 12) }            # climax: theme up an octave (soaring)
$violin += @("71/4. 69/4.", "66/4. 69/4.", "64/4. 67/4.",      # outro: B4 A4 | F#4 A4 | E4 G4
             "69/2.", "69/8 71/8 73/8 74/4.", "74/2.")         #   A4 held | A4 B4 C#5 D5 | D5 (final)

# ===================== FLUTE (woodwind, enters midpoint, high choir line at climax) =====================
$flute = @()
1..12 | ForEach-Object { $flute += "r/2." }                    # tacet intro + verse
$flute += @("r/2.", "r/2.", "r/2.", "r/2.",                    # midpoint: enters bar 5
            "81/2.", "81/4. 79/4.", "83/4. 86/4.", "88/2.")    #   A5 | A5 G5 | B5 D6 | E6 (into climax)
$flute += @("86/2.","85/2.","83/2.","85/2.","86/2.","86/2.","83/2.","81/2.")  # climax: high sustained descant
$flute += @("r/2.","r/2.","r/2.","r/2.","r/2.","86/2.")        # outro: tacet, final D6 shimmer

# ===================== auto-generated textures from the chord plan =====================
function ChordIvs($q) {   # rich set incl. 9th (for harp)
  switch ($q) {
    'add9' { @(0,4,7,14) } 'maj7' { @(0,4,7,11) } 'maj9' { @(0,4,7,11,14) }
    'm7'   { @(0,3,7,10) } 'm9'   { @(0,3,7,10,14) } 'sus4' { @(0,5,7,14) }
    'maj'  { @(0,4,7) }    'm'    { @(0,3,7) } default { @(0,4,7) }
  }
}
function PadIvs($q) {      # clean 3-4 note pad (no close 9th in the sustained voice)
  switch ($q) {
    'add9' { @(0,4,7) } 'maj7' { @(0,4,7,11) } 'maj9' { @(0,4,7,11) }
    'm7'   { @(0,3,7,10) } 'm9' { @(0,3,7,10) } 'sus4' { @(0,5,7) }
    'maj'  { @(0,4,7) } 'm' { @(0,3,7) } default { @(0,4,7) }
  }
}
function ThirdIv($q) { if ($q -eq 'sus4') { 5 } elseif ($q -match '^m') { 3 } else { 4 } }

function LHbar($c) {       # piano left-hand rocking broken chord, 6 eighths up-and-back
  $l0 = [int]$c.b; $l1 = $l0 + 7; $l2 = $l0 + 12; $l3 = $l2 + (ThirdIv $c.q)
  "$l0/8 $l1/8 $l2/8 $l3/8 $l2/8 $l1/8"
}
function HarpBar($c) {     # harp ascending arpeggio (6 eighths), higher register = shimmer
  $ivs = ChordIvs $c.q
  $anchor = 60 + [int]$c.r; if ($anchor -gt 71) { $anchor -= 12 }
  $cand = @()
  for ($o = 0; $o -lt 3; $o++) { foreach ($iv in $ivs) { $cand += ($anchor + [int]$iv + 12 * $o) } }
  $sel = ($cand | Sort-Object -Unique | Select-Object -First 6)
  (($sel | ForEach-Object { "$_/8" }) -join ' ')
}
function PadBar($c) {      # sustained strings/choir block, whole 6/8 bar (dotted half)
  $base = [int]$c.r + 48; while ($base -lt 55) { $base += 12 }
  $ns = @(); foreach ($iv in (PadIvs $c.q)) { $n = $base + [int]$iv; if ($n -gt 76) { $n -= 12 }; $ns += $n }
  $ns = ($ns | Sort-Object -Unique)
  (($ns -join '+') + "/2.")
}
function RHbar($c) {       # piano right hand: two soft chords per bar (compound beats)
  $base = [int]$c.r + 57; while ($base -lt 60) { $base += 12 }
  $ns = @(); foreach ($iv in (PadIvs $c.q)) { $n = $base + [int]$iv; if ($n -gt 81) { $n -= 12 }; $ns += $n }
  $ns = ($ns | Sort-Object -Unique); $blk = ($ns -join '+')
  "$blk/4. $blk/4."
}
function CBbar($c, $climax) {  # contrabass / low swell (stands in for timpani foundation)
  if ($climax) { $up = [int]$c.b + 12; "$($c.b)+$up/2." } else { "$($c.b)/2." }
}

$cordes = @(); $harpe = @(); $pianoRH = @(); $pianoLH = @(); $cb = @()
for ($i = 0; $i -lt $N; $i++) {
  $c = $chords[$i]
  $pianoLH += (LHbar $c)                       # piano LH plays throughout
  if ($i -ge 12) {                             # everything else enters at the midpoint
    $cordes  += (PadBar $c)
    $harpe   += (HarpBar $c)
    $pianoRH += (RHbar $c)
    $cb      += (CBbar $c ($i -ge 20 -and $i -le 27))
  } else {
    $cordes  += "r/2."; $harpe += "r/2."; $pianoRH += "r/2."; $cb += "r/2."
  }
}

# ===================== MuseScore emit =====================
# tpc (line of fifths) spelling for D major (2 sharps): C natural=14, C#=21, D=16, Eb=11, E=18, F=13,
# F#=20, G=15, G#=22, A=17, Bb=12, B=19.
$TPC = @{ 0=14; 1=21; 2=16; 3=11; 4=18; 5=13; 6=20; 7=15; 8=22; 9=17; 10=12; 11=19 }
function Tpc($pc) { $TPC[(([int]$pc % 12) + 12) % 12] }
function DurName($c) { switch ($c) { '1'{'whole'} '2'{'half'} '4'{'quarter'} '8'{'eighth'} '16'{'16th'} default{'quarter'} } }
function DurSixteenths($d) {  # for validation; base value then +half if dotted
  $dot = $d.EndsWith('.'); $base = $d.TrimEnd('.')
  $u = switch ($base) { '1'{16} '2'{8} '4'{4} '8'{2} '16'{1} default{0} }
  if ($dot) { $u + ($u / 2) } else { $u }
}

# validate every bar sums to 12 sixteenths (a full 6/8 bar) and every staff has N bars
function Validate($name, $bars) {
  if ($bars.Count -ne $N) { throw "$name has $($bars.Count) bars, expected $N" }
  for ($m = 0; $m -lt $bars.Count; $m++) {
    $sum = 0
    foreach ($tok in ($bars[$m] -split ' ')) {
      if ([string]::IsNullOrWhiteSpace($tok)) { continue }
      $null, $d = $tok -split '/'; $sum += (DurSixteenths $d)
    }
    if ($sum -ne 12) { throw "$name bar $($m+1) sums to $sum/16ths (need 12): '$($bars[$m])'" }
  }
}
$staffNames = @('Violon','Flute','Cordes','Harpe','Piano','Piano','Contrebasse')
$progs      = @(40, 73, 49, 46, 0, 0, 43)
$clefs      = @('G','G','G','G','G','F','F')
$music      = @($violin, $flute, $cordes, $harpe, $pianoRH, $pianoLH, $cb)
for ($i = 0; $i -lt $staffNames.Count; $i++) { Validate $staffNames[$i] $music[$i] }

$sb = New-Object System.Text.StringBuilder
function L($s) { [void]$sb.AppendLine($s) }
function WriteStaff($measures, $clef, $isFirst) {
  for ($m = 0; $m -lt $measures.Count; $m++) {
    L "      <Measure>"; L "        <voice>"
    if ($m -eq 0) {
      L "          <Clef><concertClefType>$clef</concertClefType><transposingClefType>$clef</transposingClefType></Clef>"
      L "          <KeySig><accidental>2</accidental></KeySig>"
      L "          <TimeSig><sigN>6</sigN><sigD>8</sigD></TimeSig>"
      if ($isFirst) { L "          <Tempo><tempo>1.35</tempo><followText>1</followText><text>Andante, en bercant</text></Tempo>" }
    }
    foreach ($tok in ($measures[$m] -split ' ')) {
      if ([string]::IsNullOrWhiteSpace($tok)) { continue }
      $p, $d = $tok -split '/'
      $dot = $d.EndsWith('.'); $dur = DurName ($d.TrimEnd('.'))
      if ($p -eq 'r') {
        L "          <Rest>"; if ($dot) { L "            <dots>1</dots>" }; L "            <durationType>$dur</durationType>"; L "          </Rest>"
      } else {
        L "          <Chord>"; if ($dot) { L "            <dots>1</dots>" }; L "            <durationType>$dur</durationType>"
        foreach ($mp in ($p -split '\+')) {
          $midi = [int]$mp; $pc = (($midi % 12) + 12) % 12
          L "            <Note><pitch>$midi</pitch><tpc>$(Tpc $pc)</tpc></Note>"
        }
        L "          </Chord>"
      }
    }
    L "        </voice>"; L "      </Measure>"
  }
}

L '<?xml version="1.0" encoding="UTF-8"?>'
L '<museScore version="3.02">'; L '  <Score>'; L '    <Division>480</Division>'
for ($i = 0; $i -lt $staffNames.Count; $i++) {
  $id = $i + 1
  L "    <Part>"
  L "      <Staff id=`"$id`"><StaffType group=`"pitched`"><name>stdNormal</name></StaffType></Staff>"
  L "      <trackName>$($staffNames[$i])</trackName>"
  L "      <Instrument><longName>$($staffNames[$i])</longName><Channel><program value=`"$($progs[$i])`"/></Channel></Instrument>"
  L "    </Part>"
}
for ($i = 0; $i -lt $staffNames.Count; $i++) {
  $id = $i + 1
  L "    <Staff id=`"$id`">"
  if ($i -eq 0) { L "      <VBox><height>10</height><Text><style>Title</style><text>$title</text></Text></VBox>" }
  WriteStaff $music[$i] $clefs[$i] ($i -eq 0)
  L "    </Staff>"
}
L '  </Score>'; L '</museScore>'
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

"Bars per staff: " + (($staffNames | ForEach-Object { $_ }) -join ', ')
"All staves validated: $N bars, every bar = 6/8."
"Written: $out  ($([int]((Get-Item $out).Length)) bytes)"
