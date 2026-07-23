# Theme-library FACTORY. Calls the Claude API to generate auto-assembly SEED themes in the JSON theme-library format,
# gates each batch through validate_themes.ps1, and retries (re-prompting with the errors) until it passes or runs out
# of tries. Output is a STAGING file for you to ear-test + merge into the live family file -- it never auto-merges.
#
# ASCII-ONLY SOURCE on purpose: PS 5.1 reads a no-BOM .ps1 as ANSI, so accented literals break the parser. All accented
# data (mood names, theme names, few-shot examples) is loaded at RUNTIME from the UTF-8 JSON files, and ConvertTo-Json
# escapes any non-ASCII to \uXXXX on the wire -- so the API still receives correct French.
#
# Requires the API key in $env:ANTHROPIC_API_KEY (never hardcode it). Costs money per call (Opus 4.8).
#
# Examples:
#   $env:ANTHROPIC_API_KEY = "sk-ant-..."
#   .\generate_themes.ps1 -Family ghibli -Style Berceuse -Mood Calme -Count 3
#   .\generate_themes.ps1 -Family generic -Style Jazz -Mood Enjoue -Count 2 -TonicPc 5 -Minor
#   .\generate_themes.ps1 -Family bach -Style Gigue -Mood Enjoue -Count 2 -Num 6 -Den 8

param(
  [Parameter(Mandatory=$true)][string]$Family,
  [Parameter(Mandatory=$true)][string]$Style,
  [Parameter(Mandatory=$true)][string]$Mood,
  [int]$Count = 2,
  [int]$TonicPc = -1,          # -1 = let the model choose; 0..11 pins the tonic (0=Do/C)
  [switch]$Minor,
  [int]$Num = 4,               # meter numerator
  [int]$Den = 4,               # meter denominator
  [string]$Model = "claude-opus-4-8",
  [int]$MaxTokens = 16000,
  [int]$MaxRetries = 2,
  [string]$Out = "",
  [switch]$DryRun              # build + print the prompt, skip the API call (no cost)
)

$ErrorActionPreference = "Stop"
$root      = "C:\Users\swe\source\repos\MusicTracker"
$themesDir = Join-Path $root "MusicTracker\Data\themes"
$validator = Join-Path $root "validate_themes.ps1"

# ASCII-fold (Enjoue <- Enjoue', Meditatif <- Me'ditatif) so command-line args match accented vocab values.
function Fold([string]$s) {
  if ([string]::IsNullOrEmpty($s)) { return "" }
  $n = $s.Normalize([Text.NormalizationForm]::FormD)
  $r = -join ($n.ToCharArray() | Where-Object {
    [Globalization.CharUnicodeInfo]::GetUnicodeCategory($_) -ne [Globalization.UnicodeCategory]::NonSpacingMark })
  return ($r -replace '[^A-Za-z0-9]', '')
}

if (-not $DryRun -and [string]::IsNullOrWhiteSpace($env:ANTHROPIC_API_KEY)) {
  Write-Host "ERREUR: variable d'environnement ANTHROPIC_API_KEY absente." -ForegroundColor Red
  Write-Host '  $env:ANTHROPIC_API_KEY = "sk-ant-..."   puis relancez.'
  exit 2
}

# --- load + check vocabulary -------------------------------------------------
$idx = Get-Content (Join-Path $themesDir "index.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$fam = $idx.families | Where-Object { $_.key -eq $Family } | Select-Object -First 1
if ($null -eq $fam) { Write-Host "ERREUR: famille '$Family' inconnue (index.json)." -ForegroundColor Red; exit 2 }
$canonMood = $idx.vocab.moods | Where-Object { (Fold $_) -ieq (Fold $Mood) } | Select-Object -First 1
if (-not $canonMood) {
  Write-Host "ERREUR: humeur '$Mood' hors vocab. Humeurs: $($idx.vocab.moods -join ', ')" -ForegroundColor Red; exit 2
}
$Mood = $canonMood   # canonical (accented) form: required verbatim in the theme's mood field by the validator
if (-not ($fam.styles -contains $Style)) {
  Write-Host "AVERTISSEMENT: style '$Style' pas dans les styles declares de '$Family' ($($fam.styles -join ', ')). On continue." -ForegroundColor Yellow
}

$barlen = [int]($Num * (96 / $Den))

# --- few-shot: real validated themes from this family ------------------------
$famPath = Join-Path $themesDir $fam.file
$examples = "(aucun exemple existant -- suis strictement le schema ci-dessous)"
if (Test-Path $famPath) {
  $lib = Get-Content $famPath -Raw -Encoding UTF8 | ConvertFrom-Json
  if ($lib.themes) {
    $pick = @($lib.themes | Where-Object { $_.style -eq $Style })
    if ($pick.Count -eq 0) { $pick = @($lib.themes) }
    $pick = $pick | Select-Object -First 2
    # keep only the BARE-theme fields (drop arrangement/variations/form) so the example matches the requested output
    $keep = 'composer','style','name','mood','auto','key','meter','tempo','spq','themeBars','melody','harmony','accomp'
    $trim = $pick | ForEach-Object {
      $o = $_; $d = [ordered]@{}
      foreach ($k in $keep) { if ($o.PSObject.Properties.Name -contains $k) { $d[$k] = $o.$k } }
      [PSCustomObject]$d
    }
    $examples = (@($trim) | ConvertTo-Json -Depth 12)
  }
}

# --- per-family idiom guidance (ASCII; from the corpus analyses) -------------
$idioms = @{
  ghibli  = "Style Hisaishi/Ghibli: melodie PENTATONIQUE (privilegie les 5 degres 1-2-3-5-6, evite les notes de tension 4 et 7), lyrique, motivique, phrases carrees de 2 ou 4 mesures, croches fluides. Harmonie COULEUR: accords sus/add9/maj7 frequents (q=4,5,6,13,16), dominantes 7 rares. Emprunts modaux (bVII, bIII, bVI), mouvements de tierce/mediante, pedales, cadences plagales. PAS de royal-road. Accomp = arpeges fluides/roules."
  bach    = "Style Bach: ligne en moto perpetuo (croches/doubles continues), Fortspinnung (une cellule qui se devide), polyphonie implicite (la ligne solo sous-entend 2 voix par sauts registre haut/bas), sequences au cercle des quintes, suspensions/retards. Harmonie fonctionnelle riche (dominantes, 7, demi-dim q=8,9). Accomp = basse continue active (marche)."
  vivaldi = "Style Vivaldi/baroque concertant: moteur de notes repetees, virtuosite (doubles, 32e), alternance tutti/solo (ritornello, parfois du silence), tierces/sixtes paralleles, basse marchante. Harmonie fonctionnelle directe, sequences. Moins contrapuntique que Bach. Accomp = basse motrice + accords paralleles."
  generic = "Famille generique multi-styles. Classique: equilibre, phrases periodiques, harmonie fonctionnelle. Romantique: lignes expressives, rubato, chromatismes de couleur, 7/9. Ballade/film: lent, tonal, texture en nappes, harmonie fonctionnelle douce, rythme libre. Contemporain: tonal mais couleurs add9/sus, ostinatos. Jazz: ii-V-I, accords 7/9 (q=7,8,15,16,17), blue notes, PAS de swing rythmique (le moteur ne le gere pas encore)."
}
$idiom = $idioms[$Family]
if ([string]::IsNullOrWhiteSpace($idiom)) { $idiom = "Compose dans le style demande, de maniere idiomatique et coherente." }

# mood-specific feel
$moodHints = @{
  "Enjoue"      = "vif, rythme, lumineux, registre clair"
  "Tendre"      = "doux, chantant, intime"
  "Calme"       = "paisible, lent, peu de notes, espace"
  "Meditatif"   = "contemplatif, suspendu, lignes longues"
  "Melancolique"= "nostalgique, mode mineur ou couleurs sombres, soupirs"
  "Majestueux"  = "ample, large, registre etoffe, gravite"
}
$moodKey  = Fold $Mood   # ASCII key for the hint table above
$moodHint = if ($moodHints.ContainsKey($moodKey)) { $moodHints[$moodKey] } else { "" }

# --- the prompt --------------------------------------------------------------
$minorStr = if ($Minor) { 'true' } else { 'false' }
$tonalLine = if ($TonicPc -ge 0) {
  "Tonalite IMPOSEE: tonicPc=$TonicPc, minor=$minorStr. Utilise-la pour TOUS les themes."
} else {
  "Tonalite LIBRE: choisis une tonique (tonicPc 0..11) et minor selon l'humeur; VARIE les tonalites entre les themes."
}

$schema = @"
SCHEMA (objet JSON unique, AUCUN markdown, AUCUNE prose):
{
  "version": 1,
  "themes": [
    {
      "composer": "$Family",
      "style": "$Style",
      "name": "<titre francais court et evocateur, UNIQUE par theme>",
      "mood": "$Mood",
      "auto": true,
      "key": { "tonicPc": <0..11, 0=Do>, "minor": <true|false> },
      "meter": { "num": $Num, "den": $Den },
      "tempo": <BPM entier>,
      "spq": 24,
      "themeBars": 8,
      "melody": "<8 mesures, voir FORMAT>",
      "harmony": { "cadenceStyle": -1, "chords": [ <EXACTEMENT 8 accords, un par mesure> ] },
      "accomp": { "bars": 1, "spq": 24, "openVoicing": true, "spread": false, "smartVoice": true,
                  "notes": [ { "deg": <degre>, "start": <slice>, "len": <slices> }, ... ] }
    }
  ]
}

FORMAT melodie (jeton = "<midi>/<duree>", separes par des espaces; "|" separe les mesures; "r/<duree>" = silence;
accord = "60+64+67/<duree>"):
  - durees: 1=ronde 2=blanche 4=noire 8=croche 16=double 32=triple; "." pointe (x1.5); "t" triolet (x2/3).
  - METRE $Num/$Den => chaque mesure DOIT sommer EXACTEMENT a $barlen slices. Valeurs (spq=24): 1=96 2=48 4=24 8=12 16=6 32=3 ; 4.=36 8.=18 16.=9 2.=72 ; 8t=8 16t=4 4t=16.
  - Il doit y avoir EXACTEMENT 8 mesures (donc 7 separateurs "|"). themeBars=8.
  - MIDI chantable ~ 55..84 (Do central=60). Reste dans 48..88.

ACCORDS (harmony.chords): un objet { "root": <0..11 classe de hauteur, 0=Do>, "q": <index qualite> } PAR MESURE (8 au total).
  Index qualite q: 0=Maj 1=min 2=dim 3=aug 4=sus2 5=sus4 6=Maj7 7=min7 8=7(dom) 9=m7b5 10=dim7
                   11=6 12=m6 13=add9 14=m(add9) 15=9(dom) 16=Maj9 17=m9 18=7b9 19=7#9 20=11 21=13 22=Maj7#11.

ACCOMP (accomp.notes): un arpege d'UNE mesure, en DEGRES de l'accord courant (1=fondamentale, 3=tierce, 5=quinte,
  8=octave, 7/9/... possibles). start et len en slices; l'ensemble couvre la mesure ($barlen slices). Le moteur le
  transpose sur chaque accord de la grille.
"@

$promptBase = @"
Tu es un compositeur expert. Genere $Count theme(s) "graine" pour un moteur d'auto-arrangement, dans la famille
"$Family", style "$Style", humeur "$Mood"$(if($moodHint){" ($moodHint)"}).

$tonalLine

IDIOME A RESPECTER:
$idiom

CONSIGNES MUSICALES:
- Chaque theme = une vraie MELODIE memorable (un theme qu'on peut chanter), pas une suite d'arpeges ni un exercice.
- Arc melodique clair sur 8 mesures (depart, montee/tension, sommet, detente), phrases de 2 ou 4 mesures.
- La grille d'accords (8) doit soutenir la melodie et etre coherente avec l'humeur/l'idiome.
- Diversifie les $Count themes entre eux (contour, rythme, tonalite si libre).
- Le moteur ajoute lui-meme intro/variations/climax/outro a partir de ce theme: fournis SEULEMENT le theme nu.

$schema

REPONDS UNIQUEMENT par l'objet JSON (commence par '{', finit par '}'). Aucune explication.

EXEMPLES VALIDES de cette famille (meme schema, a imiter pour le format -- ne les recopie pas):
$examples
"@

# --- API call helper ---------------------------------------------------------
$uri = "https://api.anthropic.com/v1/messages"
$headers = @{
  "x-api-key"         = $env:ANTHROPIC_API_KEY
  "anthropic-version" = "2023-06-01"
}

function Invoke-Claude($promptText) {
  $body = @{
    model      = $Model
    max_tokens = $MaxTokens
    thinking   = @{ type = "adaptive" }
    messages   = @( @{ role = "user"; content = $promptText } )
  } | ConvertTo-Json -Depth 20
  try {
    $resp = Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -Body $body `
              -ContentType "application/json" -TimeoutSec 600
  } catch {
    $msg = $_.Exception.Message
    try {
      $stream = $_.Exception.Response.GetResponseStream()
      $reader = New-Object System.IO.StreamReader($stream)
      $msg = $reader.ReadToEnd()
    } catch {}
    throw "Appel API echoue: $msg"
  }
  if ($resp.stop_reason -eq "max_tokens") {
    Write-Host "  ~ stop_reason=max_tokens : sortie tronquee. Augmentez -MaxTokens ou baissez -Count." -ForegroundColor Yellow
  }
  $text = ($resp.content | Where-Object { $_.type -eq "text" } | ForEach-Object { $_.text }) -join ""
  if ($resp.usage) { Write-Host ("  tokens: in={0} out={1}" -f $resp.usage.input_tokens, $resp.usage.output_tokens) }
  return $text
}

function Extract-Json($text) {
  if ([string]::IsNullOrWhiteSpace($text)) { return $null }
  $t = $text.Trim()
  # strip ```json ... ``` fences if present
  if ($t -match '(?s)```(?:json)?\s*(.*?)\s*```') { $t = $Matches[1].Trim() }
  # else clip to the outermost object
  $i = $t.IndexOf('{'); $j = $t.LastIndexOf('}')
  if ($i -ge 0 -and $j -gt $i) { $t = $t.Substring($i, $j - $i + 1) }
  return $t
}

# --- output path -------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($Out)) {
  $gen = Join-Path $themesDir "_generated"
  if (-not (Test-Path $gen)) { New-Item -ItemType Directory -Path $gen | Out-Null }
  $safeStyle = ($Style -replace '[^A-Za-z0-9]', '')
  $safeMood  = ($moodKey)
  $Out = Join-Path $gen ("{0}_{1}_{2}.json" -f $Family, $safeStyle, $safeMood)
}

# --- generate + validate + retry loop ----------------------------------------
Write-Host ""
Write-Host ("USINE: {0} theme(s) | famille={1} style={2} humeur={3} metre={4}/{5} (barlen={6}) | modele={7}" -f `
  $Count, $Family, $Style, $Mood, $Num, $Den, $barlen, $Model) -ForegroundColor Cyan
Write-Host ("Sortie staging: {0}" -f $Out)

if ($DryRun) {
  Write-Host "`n--- DRY RUN: prompt qui SERAIT envoye a l'API (aucun appel) ---`n" -ForegroundColor Yellow
  Write-Host $promptBase
  Write-Host "`n--- (fin du prompt) --- Modele=$Model MaxTokens=$MaxTokens" -ForegroundColor Yellow
  exit 0
}

$prompt = $promptBase
$ok = $false
for ($attempt = 1; $attempt -le ($MaxRetries + 1); $attempt++) {
  Write-Host ""
  Write-Host ("--- Tentative {0}/{1} : appel API..." -f $attempt, ($MaxRetries + 1)) -ForegroundColor Cyan
  $raw = Invoke-Claude $prompt
  $json = Extract-Json $raw
  if ([string]::IsNullOrWhiteSpace($json)) { Write-Host "  X reponse vide." -ForegroundColor Red; continue }

  # sanity-parse before writing
  try { [void]($json | ConvertFrom-Json) }
  catch {
    Write-Host "  X JSON non parsable: $($_.Exception.Message)" -ForegroundColor Red
    $prompt = $promptBase + "`n`nLa tentative precedente n'etait pas du JSON valide ($($_.Exception.Message)). Renvoie UNIQUEMENT l'objet JSON, sans texte autour."
    continue
  }

  [System.IO.File]::WriteAllText($Out, $json, (New-Object System.Text.UTF8Encoding($false)))

  # validate via the shared validator (-File mode)
  $vout = & $validator -File $Out -FamilyKey $Family 2>&1
  $vexit = $LASTEXITCODE
  if ($vexit -eq 0) {
    Write-Host "  OK validation." -ForegroundColor Green
    $ok = $true
    break
  }
  $verr = ($vout | Out-String).Trim()
  Write-Host "  X validation echouee:" -ForegroundColor Red
  Write-Host $verr
  $prompt = $promptBase + @"


La tentative precedente a ECHOUE a la validation. Corrige TOUS ces problemes et regenere les $Count themes:
$verr

Rappels critiques: chaque mesure DOIT sommer a EXACTEMENT $barlen slices; EXACTEMENT 8 mesures; root 0..11; q valide; mood="$Mood".
"@
}

Write-Host ""
if ($ok) {
  Write-Host "TERMINE: themes valides ecrits dans $Out" -ForegroundColor Green
  Write-Host "Etapes suivantes: ecoute (importe/teste), puis fusionne les entrees voulues dans $($fam.file),"
  Write-Host "et relance .\validate_themes.ps1 sur toute la bibliotheque."
  exit 0
} else {
  Write-Host "ECHEC: aucun lot valide apres $($MaxRetries + 1) tentative(s). Dernier essai laisse dans $Out pour inspection." -ForegroundColor Red
  exit 1
}
