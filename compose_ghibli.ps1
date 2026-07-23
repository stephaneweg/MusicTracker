# Hand-composed Ghibli-style piano piece (by Claude), emitted as a MuseScore 3 .mscx using the SAME format as
# MuseScoreExporter (opens in MuseScore 3/4). C major, 4/4. Structure: Intro / Thème / Variation / Reprise / Outro.
# Token = "<midi[+midi...] or r>/<dur>", dur: 1=whole 2=half 4=quarter 8=eighth.
$ErrorActionPreference = "Stop"
$out = "C:\Users\swe\source\repos\MusicTracker\ghibli_lullaby.mscx"
$title = "Berceuse - style Ghibli (composee par Claude)"

# --- the 8-bar harmony loop, RIGHT HAND theme (singable, pentatonic-leaning, clear arc) ---
$RHtheme = @(
  "64/8 67/8 69/8 67/8 64/4 67/4",   # C(add9):  E G A G | E  G
  "69/8 67/8 64/8 62/8 64/2",        # G/B:      A G E D | E(half)
  "64/8 67/8 69/8 72/8 71/4 69/4",   # Am7:      E G A C | B  A   (rise to peak)
  "67/4 69/8 67/8 64/2",             # G:        G | A G | E(half)
  "69/8 72/8 74/8 72/8 69/4 72/4",   # F:        A C D C | A  C   (high point D5)
  "72/8 71/8 69/8 67/8 64/2",        # C/E:      C B A G | E(half)
  "65/8 69/8 74/8 72/8 69/4 65/4",   # Dm7:      F A D C | A  F
  "67/8 69/8 71/8 74/8 67/2"         # G7:       G A B D | G(half) -> leads back
)
# --- LEFT HAND: flowing broken chords, DESCENDING bass C-B-A-G-F-E-D-G (the Ghibli signature) ---
$LHprog = @(
  "48/8 52/8 55/8 60/8 55/8 52/8 55/8 52/8",   # C
  "47/8 50/8 55/8 59/8 55/8 50/8 55/8 50/8",   # G/B
  "45/8 48/8 52/8 55/8 52/8 48/8 52/8 48/8",   # Am7
  "43/8 47/8 50/8 55/8 50/8 47/8 50/8 47/8",   # G
  "41/8 45/8 48/8 53/8 48/8 45/8 48/8 45/8",   # F
  "40/8 43/8 48/8 52/8 48/8 43/8 48/8 43/8",   # C/E
  "38/8 41/8 45/8 48/8 45/8 41/8 45/8 41/8",   # Dm7
  "43/8 47/8 50/8 53/8 50/8 47/8 50/8 47/8"    # G7
)
# --- INTRO (4 bars): left hand alone establishes the rocking arpeggio, RH enters with a pickup ---
$RHintro = @("r/1", "r/1", "r/1", "r/2 64/4 67/4")
# --- OUTRO (4 bars): soft plagal-ish close, melody settles, final Cadd9 rings ---
$RHoutro = @(
  "69/8 67/8 65/8 64/8 65/2",        # F:  A G F E | F(half)
  "72/8 71/8 69/8 67/8 64/2",        # C/E: C B A G | E(half)
  "65/8 69/8 72/8 74/8 71/4 69/4",   # Dm7->G: F A C D | B  A
  "60+64+67+74/1"                    # Cadd9 (C E G D), whole - the resolution
)
$LHoutro = @(
  "41/8 45/8 48/8 53/8 48/8 45/8 48/8 45/8",   # F
  "40/8 43/8 48/8 52/8 48/8 43/8 48/8 43/8",   # C/E
  "38/8 41/8 45/8 48/8 43/8 47/8 50/8 47/8",   # Dm7 -> G
  "36+48+52+55/1"                              # low C + C3 E3 G3, held
)

function Transpose($tokstr, $semi) {
  (($tokstr -split ' ') | ForEach-Object {
    $p, $d = $_ -split '/'
    if ($p -eq 'r') { "$p/$d" }
    else { ((($p -split '\+') | ForEach-Object { [int]$_ + $semi }) -join '+') + "/$d" }
  }) -join ' '
}

# Treble (RH): Intro + Theme + Variation(octave up) + Reprise(theme) + Outro = 32 bars
$treble = @()
$treble += $RHintro
$treble += $RHtheme
$treble += ($RHtheme | ForEach-Object { Transpose $_ 12 })   # variation: brighter octave
$treble += $RHtheme
$treble += $RHoutro
# Bass (LH): Intro(first 4) + 3x the 8-bar loop + Outro = 32 bars
$bass = @()
$bass += $LHprog[0..3]
$bass += $LHprog
$bass += $LHprog
$bass += $LHprog
$bass += $LHoutro

function Tpc($pc) { switch ($pc) { 0 {14} 2 {16} 4 {18} 5 {13} 7 {15} 9 {17} 11 {19} default {14} } }
function DurName($c) { switch ($c) { "1" {"whole"} "2" {"half"} "4" {"quarter"} "8" {"eighth"} default {"quarter"} } }

$sb = New-Object System.Text.StringBuilder
function L($s) { [void]$sb.AppendLine($s) }

function WriteStaff($measures, $clef, $isFirstStaff) {
  for ($m = 0; $m -lt $measures.Count; $m++) {
    L "      <Measure>"
    L "        <voice>"
    if ($m -eq 0) {
      L "          <Clef><concertClefType>$clef</concertClefType><transposingClefType>$clef</transposingClefType></Clef>"
      L "          <KeySig><accidental>0</accidental></KeySig>"
      L "          <TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>"
      if ($isFirstStaff) { L "          <Tempo><tempo>1.2</tempo><followText>1</followText><text>Andante</text></Tempo>" }
    }
    foreach ($tok in ($measures[$m] -split ' ')) {
      if ([string]::IsNullOrWhiteSpace($tok)) { continue }
      $p, $d = $tok -split '/'
      $dur = DurName $d
      if ($p -eq 'r') {
        L "          <Rest><durationType>$dur</durationType></Rest>"
      } else {
        L "          <Chord>"
        L "            <durationType>$dur</durationType>"
        foreach ($mp in ($p -split '\+')) {
          $midi = [int]$mp; $pc = (($midi % 12) + 12) % 12
          L "            <Note><pitch>$midi</pitch><tpc>$(Tpc $pc)</tpc></Note>"
        }
        L "          </Chord>"
      }
    }
    L "        </voice>"
    L "      </Measure>"
  }
}

L '<?xml version="1.0" encoding="UTF-8"?>'
L '<museScore version="3.02">'
L '  <Score>'
L '    <Division>480</Division>'
foreach ($id in 1, 2) {
  L "    <Part>"
  L "      <Staff id=`"$id`"><StaffType group=`"pitched`"><name>stdNormal</name></StaffType></Staff>"
  L "      <trackName>Piano</trackName>"
  L "      <Instrument><longName>Piano</longName><Channel><program value=`"0`"/></Channel></Instrument>"
  L "    </Part>"
}
L '    <Staff id="1">'
L "      <VBox><height>10</height><Text><style>Title</style><text>$title</text></Text></VBox>"
WriteStaff $treble "G" $true
L '    </Staff>'
L '    <Staff id="2">'
WriteStaff $bass "F" $false
L '    </Staff>'
L '  </Score>'
L '</museScore>'

[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
"Treble bars: $($treble.Count)  Bass bars: $($bass.Count)"
"Written: $out  ($([int]((Get-Item $out).Length)) bytes)"
