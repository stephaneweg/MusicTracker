<#
    check-lang-parity.ps1 — GARDE-FOU de la localisation.

    Le repli de Loc.T est SILENCIEUX : une clé absente d'une traduction retombe sur le français sans rien
    signaler. Rien ne détecte donc la dérive toute seule, et elle grandit à chaque fonctionnalité — l'écart
    était monté à 44 clés avant ce contrôle.

    Sortie 0 = les 7 langues ont les mêmes clés. Sortie 1 = il en manque, listées par langue.

    Usage :  powershell -ExecutionPolicy Bypass -File AutoTest\check-lang-parity.ps1
#>
[CmdletBinding()]
param([string]$Dir = "MusicTracker\Localization")

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Web.Extensions

$root = Split-Path -Parent $PSScriptRoot
$path = Join-Path $root $Dir
if (-not (Test-Path $path)) { Write-Host "Dossier introuvable : $path" -ForegroundColor Red; exit 2 }

# JavaScriptSerializer et NON ConvertFrom-Json : ce dernier est insensible à la casse et échoue à tort sur le
# couple préexistant AUDIO / Audio.
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = 50000000

$maps = @{}
foreach ($f in Get-ChildItem $path -Filter 'lang.*.json') {
    $code = $f.BaseName -replace '^lang\.', ''
    try { $maps[$code] = $ser.DeserializeObject([System.IO.File]::ReadAllText($f.FullName)) }
    catch { Write-Host "$($f.Name) : JSON INVALIDE — $($_.Exception.Message)" -ForegroundColor Red; exit 2 }
}
if (-not $maps.ContainsKey('fr')) { Write-Host "lang.fr.json manquant (langue source)" -ForegroundColor Red; exit 2 }

Write-Host ("Langues : " + (($maps.Keys | Sort-Object | ForEach-Object { "$_=$($maps[$_].Count)" }) -join '  '))

# Le français est la langue SOURCE : c'est lui qui fait référence.
$ref = $maps['fr']
$total = 0
foreach ($code in ($maps.Keys | Where-Object { $_ -ne 'fr' } | Sort-Object)) {
    $miss = @($ref.Keys | Where-Object { -not $maps[$code].ContainsKey($_) } | Sort-Object)
    # Une valeur vide vaut une absence : elle affiche la clé brute à l'écran.
    $vides = @($ref.Keys | Where-Object { $maps[$code].ContainsKey($_) -and [string]::IsNullOrWhiteSpace([string]$maps[$code][$_]) } | Sort-Object)
    $extra = @($maps[$code].Keys | Where-Object { -not $ref.ContainsKey($_) } | Sort-Object)

    if ($miss.Count -or $vides.Count) {
        $total += $miss.Count + $vides.Count
        Write-Host "`n$code : $($miss.Count) manquante(s), $($vides.Count) vide(s)" -ForegroundColor Yellow
        $miss  | ForEach-Object { Write-Host "    manque : $_" }
        $vides | ForEach-Object { Write-Host "    vide   : $_" }
    }
    # Les clés en trop ne cassent rien (jamais lues) : on informe sans faire échouer.
    if ($extra.Count) { Write-Host "$code : $($extra.Count) cle(s) orpheline(s), sans equivalent francais" -ForegroundColor DarkGray }
}

if ($total -eq 0) { Write-Host "`nPARITE OK — les 7 langues ont les memes cles." -ForegroundColor Green; exit 0 }
Write-Host "`n$total ecart(s) au total." -ForegroundColor Red
exit 1
