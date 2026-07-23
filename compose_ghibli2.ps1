# Hand-composed (by Claude) Ghibli-style ROMANCE for VIOLIN + PIANO, emitted as MuseScore 3 .mscx (same format as
# MuseScoreExporter). C major, 4/4, Andante. Form: piano intro -> violin exposes theme -> piano takes it (violin
# descant) -> development (theme brodé, rising, borrowed bVII) -> CLIMAX à 2 (theme in octaves) -> descent -> outro.
# Token = "<midi[+midi...] or r>/<dur>"; dur: 1=whole 2=half 4=quarter 8=eighth.
$ErrorActionPreference = "Stop"
$out = "C:\Users\swe\source\repos\MusicTracker\ghibli_romance.mscx"
$title = "Romance pour violon et piano - style Ghibli (Claude)"

# ===== MATERIALS (over the 8-bar loop: C G/B Am7 G F C/E Dm7 G7, descending bass C-B-A-G-F-E-D-G) =====
$T = @(   # THEME (the tune)
  "64/8 67/8 69/8 67/8 64/4 67/4", "69/8 67/8 64/8 62/8 64/2",
  "64/8 67/8 69/8 72/8 71/4 69/4", "67/4 69/8 67/8 64/2",
  "69/8 72/8 74/8 72/8 69/4 72/4", "72/8 71/8 69/8 67/8 64/2",
  "65/8 69/8 74/8 72/8 69/4 65/4", "67/8 69/8 71/8 74/8 67/2")
$P = @(   # PIANO LEFT-HAND broken-chord loop
  "48/8 52/8 55/8 60/8 55/8 52/8 55/8 52/8", "47/8 50/8 55/8 59/8 55/8 50/8 55/8 50/8",
  "45/8 48/8 52/8 55/8 52/8 48/8 52/8 48/8", "43/8 47/8 50/8 55/8 50/8 47/8 50/8 47/8",
  "41/8 45/8 48/8 53/8 48/8 45/8 48/8 45/8", "40/8 43/8 48/8 52/8 48/8 43/8 48/8 43/8",
  "38/8 41/8 45/8 48/8 45/8 41/8 45/8 41/8", "43/8 47/8 50/8 53/8 50/8 47/8 50/8 47/8")
$ACC = @(  # PIANO RIGHT-HAND soft pad (whole-note triads) while the VIOLIN sings the theme
  "60+64+67/1", "62+67+71/1", "60+64+69/1", "59+62+67/1",
  "60+65+69/1", "60+64+67/1", "62+65+69/1", "62+65+71/1")
$CM = @(   # VIOLIN descant (counter-melody) while the PIANO has the theme
  "79/2 76/2", "74/1", "76/2 79/2", "74/1",
  "81/2 79/2", "76/1", "77/4 81/4 79/2", "74/1")

$introRH = @("76/4 74/4 72/2", "74/4 72/4 71/2", "72/4 71/4 69/2", "67/4 r/4 r/2")

# DEVELOPMENT (8 bars): Am F C G Dm Bb F G — rising sequence of the theme head, building to the climax
$devVln = @(
  "69/8 72/8 76/8 72/8 69/4 72/4", "72/8 77/8 81/8 77/8 72/4 77/4",
  "76/8 79/8 84/8 79/8 76/4 79/4", "74/8 79/8 83/8 79/8 74/2",
  "77/8 81/8 86/8 81/8 77/4 81/4", "79/8 82/8 86/8 82/8 79/2",
  "81/4 84/4 86/2", "83/4 86/4 88/2")
$devRH = @("60+64+69/1", "60+65+69/1", "60+64+67/1", "59+62+67/1",
  "62+65+69/1", "58+62+65/1", "60+65+69/1", "62+67+71/1")
$devLH = @(
  "45/8 48/8 52/8 57/8 52/8 48/8 52/8 48/8", "41/8 45/8 48/8 53/8 48/8 45/8 48/8 45/8",
  "48/8 52/8 55/8 60/8 55/8 52/8 55/8 52/8", "43/8 47/8 50/8 55/8 50/8 47/8 50/8 47/8",
  "38/8 41/8 45/8 50/8 45/8 41/8 45/8 41/8", "46/8 50/8 53/8 58/8 53/8 50/8 53/8 50/8",
  "41/8 45/8 48/8 53/8 48/8 45/8 48/8 45/8", "43/8 47/8 50/8 55/8 50/8 47/8 50/8 47/8")

$outroVln = @("81/2 79/2", "76/1", "74/2 72/2", "76/1")
$outroRH  = @("69/4 67/4 65/2", "67/4 64/4 60/2", "62/4 65/4 67/2", "60+64+67+74/1")
$outroLH  = @($P[4], $P[5], "38/8 41/8 45/8 48/8 43/8 47/8 50/8 47/8", "36+48+52+55/1")

function Transpose($tokstr, $semi) {
  (($tokstr -split ' ') | ForEach-Object {
    $p, $d = $_ -split '/'
    if ($p -eq 'r') { "$p/$d" } else { ((($p -split '\+') | ForEach-Object { [int]$_ + $semi }) -join '+') + "/$d" }
  }) -join ' '
}

# ===== assemble the three staves (36 bars each): intro4 | violinTheme8 | pianoTheme8 | dev8 | climax4 | outro4 =====
$violin = @(); $violin += @("r/1", "r/1", "r/1", "r/1"); $violin += $T; $violin += $CM; $violin += $devVln
$violin += ($T[4..7] | ForEach-Object { Transpose $_ 12 }); $violin += $outroVln          # climax: theme up an octave
$pnoRH  = @(); $pnoRH  += $introRH; $pnoRH += $ACC; $pnoRH += $T; $pnoRH += $devRH; $pnoRH += $T[4..7]; $pnoRH += $outroRH
$pnoLH  = @(); $pnoLH  += $P[0..3]; $pnoLH += $P; $pnoLH += $P; $pnoLH += $devLH; $pnoLH += $P[4..7]; $pnoLH += $outroLH

# ===== MuseScore TPC spelling (sharp/flat by line-of-fifths, spellCenter=0 for C major) =====
$sharp = @(14, 21, 16, 23, 18, 13, 20, 15, 22, 17, 24, 19)
$flat  = @(14, 9, 16, 11, 18, 13, 8, 15, 10, 17, 12, 19)
function Tpc($pc) {
  $s = $sharp[$pc]; $f = $flat[$pc]; if ($s -eq $f) { return $s }
  $ds = [Math]::Abs($s - 14); $df = [Math]::Abs($f - 14)
  if ($ds -ne $df) { if ($ds -lt $df) { return $s } else { return $f } }
  return $s
}
function DurName($c) { switch ($c) { "1" {"whole"} "2" {"half"} "4" {"quarter"} "8" {"eighth"} default {"quarter"} } }

$sb = New-Object System.Text.StringBuilder
function L($s) { [void]$sb.AppendLine($s) }
function WriteStaff($measures, $clef, $isFirst) {
  for ($m = 0; $m -lt $measures.Count; $m++) {
    L "      <Measure>"; L "        <voice>"
    if ($m -eq 0) {
      L "          <Clef><concertClefType>$clef</concertClefType><transposingClefType>$clef</transposingClefType></Clef>"
      L "          <KeySig><accidental>0</accidental></KeySig>"
      L "          <TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>"
      if ($isFirst) { L "          <Tempo><tempo>1.2</tempo><followText>1</followText><text>Andante</text></Tempo>" }
    }
    foreach ($tok in ($measures[$m] -split ' ')) {
      if ([string]::IsNullOrWhiteSpace($tok)) { continue }
      $p, $d = $tok -split '/'; $dur = DurName $d
      if ($p -eq 'r') { L "          <Rest><durationType>$dur</durationType></Rest>" }
      else {
        L "          <Chord>"; L "            <durationType>$dur</durationType>"
        foreach ($mp in ($p -split '\+')) { $midi = [int]$mp; $pc = (($midi % 12) + 12) % 12; L "            <Note><pitch>$midi</pitch><tpc>$(Tpc $pc)</tpc></Note>" }
        L "          </Chord>"
      }
    }
    L "        </voice>"; L "      </Measure>"
  }
}

$names = @("Violon", "Piano", "Piano"); $progs = @(40, 0, 0); $clefs = @("G", "G", "F"); $music = @($violin, $pnoRH, $pnoLH)
L '<?xml version="1.0" encoding="UTF-8"?>'; L '<museScore version="3.02">'; L '  <Score>'; L '    <Division>480</Division>'
for ($i = 0; $i -lt 3; $i++) {
  $id = $i + 1
  L "    <Part>"
  L "      <Staff id=`"$id`"><StaffType group=`"pitched`"><name>stdNormal</name></StaffType></Staff>"
  L "      <trackName>$($names[$i])</trackName>"
  L "      <Instrument><longName>$($names[$i])</longName><Channel><program value=`"$($progs[$i])`"/></Channel></Instrument>"
  L "    </Part>"
}
for ($i = 0; $i -lt 3; $i++) {
  $id = $i + 1
  L "    <Staff id=`"$id`">"
  if ($i -eq 0) { L "      <VBox><height>10</height><Text><style>Title</style><text>$title</text></Text></VBox>" }
  WriteStaff $music[$i] $clefs[$i] ($i -eq 0)
  L "    </Staff>"
}
L '  </Score>'; L '</museScore>'
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
"bars: violin=$($violin.Count) pnoRH=$($pnoRH.Count) pnoLH=$($pnoLH.Count)"
"Written: $out  ($([int]((Get-Item $out).Length)) bytes)"
