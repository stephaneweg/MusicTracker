# Theme-library VALIDATOR. Reads the FROZEN vocabulary from Data\themes\index.json and validates every per-family
# library file against it (pure PowerShell, no reflection). Authoring gate: run after editing/adding any theme JSON.
# Exit 0 = clean, 1 = errors. Warnings (e.g. a style not yet declared) do not fail. ASCII-only (PS 5.1 reads no-BOM
# scripts as ANSI -> accents break the parser); JSON read forced to UTF-8 so accented values compare correctly.
# Usage: no args = validate every family file. -File <path> -FamilyKey <key> = validate ONE arbitrary themes file
# against that family's vocab (used by generate_themes.ps1 to gate API-generated batches before merge).
param([string]$File, [string]$FamilyKey)
$ErrorActionPreference = "Stop"
$themesDir = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\Data\themes"
$BARLEN = 96   # slices per bar at 4/4, spq=24 (assumed until entries carry an explicit meter). NB: not $BAR (collides with loop var $bar -- PS is case-insensitive)

$idx = Get-Content (Join-Path $themesDir "index.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$V = $idx.vocab
$errors = New-Object System.Collections.Generic.List[string]
$warns  = New-Object System.Collections.Generic.List[string]
function Err($m)  { $errors.Add($m) }
function Warn($m) { $warns.Add($m) }
function InSet($val, $set) { return ($set -contains $val) }

function DurSlices($tok) {
  $dotted = $false; $trip = $false
  if ($tok.EndsWith(".")) { $dotted = $true; $tok = $tok.Substring(0, $tok.Length - 1) }
  if ($tok.EndsWith("t")) { $trip = $true; $tok = $tok.Substring(0, $tok.Length - 1) }
  $d = 0; if (-not [int]::TryParse($tok, [ref]$d) -or $d -le 0) { return -1 }
  $s = [int]((4 * 24) / $d)
  if ($dotted) { $s = [int]($s * 3 / 2) }
  if ($trip)   { $s = [int]($s * 2 / 3) }
  return $s
}

function CheckMelody($label, $mel, $barlen) {
  if ([string]::IsNullOrWhiteSpace($mel)) { return }
  $bi = 0
  foreach ($bar in ($mel -split '\|')) {
    $bi++; $sum = 0
    foreach ($tok in ($bar.Trim() -split '\s+')) {
      if ([string]::IsNullOrWhiteSpace($tok)) { continue }
      $slash = $tok.LastIndexOf('/')
      if ($slash -le 0) { Err "$label bar $bi : token malforme '$tok'"; continue }
      $dur = DurSlices $tok.Substring($slash + 1)
      if ($dur -le 0) { Err "$label bar $bi : duree invalide '$tok'" } else { $sum += $dur }
      $p = $tok.Substring(0, $slash)
      if ($p -ne 'r') {
        foreach ($mp in ($p -split '\+')) {
          $m = 0
          if ([int]::TryParse($mp, [ref]$m)) { if ($m -lt 12 -or $m -gt 108) { Err "$label bar $bi : MIDI hors plage '$m'" } }
          else { Err "$label bar $bi : hauteur non numerique '$mp'" }
        }
      }
    }
    if ($sum -ne $barlen) { Err "$label bar $bi : $sum slices (attendu $barlen)" }
  }
}

function CheckMotif($label, $motif) {
  if ($null -eq $motif -or $null -eq $motif.notes) { return }
  foreach ($n in $motif.notes) {
    if ($n.deg -lt 1 -or $n.deg -gt 15) { Err "$label : degre hors plage 1..15 ($($n.deg))" }
    if ($n.len -le 0) { Err "$label : longueur non positive" }
  }
}

function CheckIntent($label, $intent) {
  if ($null -eq $intent) { return }
  foreach ($dim in 'function','energy','register','density','flavor') {
    $val = $intent.$dim
    if ($null -ne $val -and -not (InSet $val $V.intent.$dim)) { Err "$label : intent.$dim '$val' hors vocab" }
  }
}

$script:themes = 0
function ValidateLib($lib, $fam) {
  if ($null -eq $lib.themes) { Warn "$($fam.file) : aucun theme"; }
  else {
  foreach ($t in $lib.themes) {
    $script:themes++
    $id = "$($fam.key)/$($t.style)/$($t.name)"
    if ($t.composer -and $t.composer.ToLower() -ne $fam.key.ToLower()) { Err "$id : composer '$($t.composer)' != famille '$($fam.key)'" }
    if (-not (InSet $t.style $fam.styles)) { Warn "$id : style '$($t.style)' pas dans les styles declares de $($fam.key)" }
    if ($t.mood -and -not (InSet $t.mood $V.moods)) { Err "$id : mood '$($t.mood)' hors vocab" }
    if ($null -ne $t.key) { if ($t.key.tonicPc -lt 0 -or $t.key.tonicPc -gt 11) { Err "$id : tonicPc hors 0..11" } }
    if ($t.themeBars -le 0) { Err "$id : themeBars non positif" }
    $barlen = 96
    if ($t.meter -and $t.meter.num -and $t.meter.den) { $barlen = [int]($t.meter.num * (96 / $t.meter.den)) }

    CheckMelody "$id melody" $t.melody $barlen

    if ($null -ne $t.harmony -and $null -ne $t.harmony.chords) {
      $ci = 0
      foreach ($c in $t.harmony.chords) {
        $ci++
        if ($c.root -lt 0 -or $c.root -gt 11) { Err "$id chord $ci : root hors 0..11" }
        if ($c.q -lt 0 -or $c.q -gt 30) { Err "$id chord $ci : quality suspecte ($($c.q))" }
      }
    }

    CheckMotif "$id accomp" $t.accomp
    CheckMotif "$id bass"   $t.bass

    if ($null -ne $t.arrangement -and $null -ne $t.arrangement.sections) {
      $si = 0
      foreach ($s in $t.arrangement.sections) {
        $si++; $sl = "$id sec$si($($s.role))"
        if (-not (InSet $s.role $V.sectionRoles)) { Err "$sl : role hors vocab" }
        if ($s.bars -le 0) { Err "$sl : bars non positif" }
        if ($null -ne $s.cadence -and ($s.cadence -lt 0 -or $s.cadence -gt 2)) { Err "$sl : cadence hors 0..2" }
        if ($s.voice  -and -not (InSet $s.voice  $V.voices))  { Err "$sl : voice '$($s.voice)' hors vocab" }
        if ($s.source -and -not (InSet $s.source $V.sources)) { Err "$sl : source '$($s.source)' hors vocab" }
        if ($null -ne $s.ops) {
          foreach ($op in $s.ops) {
            $name = ($op -split ':')[0]
            if (-not (InSet $name $V.ops)) { Err "$sl : op '$name' hors vocab" }
          }
        }
        if ($s.melody)  { CheckMelody "$sl melody"  $s.melody $barlen }
        if ($s.counter) { CheckMelody "$sl counter" $s.counter $barlen }
      }
    }

  }
  }

  # family-shared POOL of intent-tagged gestures
  if ($null -ne $lib.pool) {
    foreach ($g in $lib.pool) {
      $gl = "$($fam.key) pool/$($g.id)"
      if ($g.kind -and -not (InSet $g.kind @('melody','accomp','intro','outro','counter','variation'))) { Err "$gl : kind '$($g.kind)' invalide" }
      CheckIntent $gl $g.intent
      if ($null -ne $g.ops) { foreach ($op in $g.ops) { $name = ($op -split ':')[0]; if (-not (InSet $name $V.ops)) { Err "$gl : op '$name' hors vocab" } } }
      if ($g.melody) { $gbar = 96; if ($g.meter -and $g.meter.num -and $g.meter.den) { $gbar = [int]($g.meter.num * (96 / $g.meter.den)) }; CheckMelody "$gl melody" $g.melody $gbar }
    }
  }
}

$files = 0
if ($File) {
  $fam = $idx.families | Where-Object { $_.key -eq $FamilyKey } | Select-Object -First 1
  if ($null -eq $fam) { Err "FamilyKey inconnu: '$FamilyKey'" }
  else {
    $lib = $null
    try { $lib = Get-Content $File -Raw -Encoding UTF8 | ConvertFrom-Json } catch { Err "$File : JSON invalide -- $($_.Exception.Message)" }
    if ($null -ne $lib) { $files++; ValidateLib $lib $fam }
  }
}
else {
  foreach ($fam in $idx.families) {
    $path = Join-Path $themesDir $fam.file
    if (-not (Test-Path $path)) { continue }   # family not authored yet -- fine
    $files++
    $lib = $null
    try { $lib = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json } catch { Err "$($fam.file) : JSON invalide -- $($_.Exception.Message)"; continue }
    ValidateLib $lib $fam
  }
}

Write-Host ("Valide : {0} fichier(s), {1} theme(s)." -f $files, $themes)
if ($warns.Count  -gt 0) { Write-Host "`nAVERTISSEMENTS :"; $warns  | ForEach-Object { Write-Host "  ~ $_" } }
if ($errors.Count -gt 0) {
  Write-Host "`nERREURS :"; $errors | ForEach-Object { Write-Host "  X $_" }
  Write-Host ("`nECHEC ({0} erreur(s))." -f $errors.Count); exit 1
}
Write-Host "`nOK - aucune erreur."
exit 0   # explicit so callers (generate_themes.ps1) get a reliable $LASTEXITCODE on success
