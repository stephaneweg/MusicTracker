<#
.SYNOPSIS
  Publie le site vitrine (WebSite\) chez l'hébergeur par FTP / FTPS / SFTP.

.DESCRIPTION
  Lit ses identifiants dans un fichier de configuration EXCLU DE GIT — ils ne doivent jamais
  se retrouver dans le dépôt. Crée ce fichier une seule fois, à la main :

      installer\website-ftp.local.json

      {
        "Protocol":   "ftps",              // "ftp" | "ftps" | "sftp"
        "Host":       "ftp.monhebergeur.tld",
        "Port":       0,                   // 0 = port par défaut du protocole
        "User":       "identifiant",
        "Password":   "motdepasse",
        "PasswordEncoding": "plain",       // "plain" | "base64" | "dpapi"
        "RemoteRoot": "/www"               // racine web chez l'hébergeur
      }

  PasswordEncoding :
    plain   le mot de passe tel quel.
    base64  encodé en base64 (UTF-8). ATTENTION : ce n'est PAS du chiffrement, seulement de
            l'obscurcissement — quiconque lit le fichier retrouve le mot de passe en une commande.
            Cela évite juste la lecture accidentelle par-dessus l'épaule.
    dpapi   chiffré par Windows (DPAPI), déchiffrable UNIQUEMENT par le même compte utilisateur sur
            la même machine. C'est la seule option réellement sûre au repos. Pour produire la valeur :

                (ConvertTo-SecureString 'mon-mot-de-passe' -AsPlainText -Force | ConvertFrom-SecureString)

            Colle la longue chaîne obtenue dans "Password" et mets "PasswordEncoding": "dpapi".

  FTP et FTPS sont gérés nativement (FtpWebRequest). SFTP exige WinSCP
  (https://winscp.net) : winscp.com doit être dans le PATH ou dans %ProgramFiles(x86)%\WinSCP.

.PARAMETER ConfigPath  Chemin d'un autre fichier de configuration.
.PARAMETER WhatIf      Liste ce qui serait envoyé, sans rien transférer.

.EXAMPLE
  installer\deploy-website.ps1
  installer\deploy-website.ps1 -WhatIf
#>
[CmdletBinding()]
param(
  [string]$ConfigPath,
  [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$repo         = Split-Path -Parent $installerDir
$siteDir      = Join-Path $repo 'WebSite'
if (-not $ConfigPath) { $ConfigPath = Join-Path $installerDir 'website-ftp.local.json' }

# --- garde-fous ---------------------------------------------------------------
if (-not (Test-Path $siteDir)) { throw "Dossier du site introuvable : $siteDir" }
if (-not (Test-Path $ConfigPath)) {
  throw @"
Configuration de deploiement absente : $ConfigPath
Cree ce fichier (il est exclu de git) avec Protocol / Host / User / Password / RemoteRoot.
Voir l'en-tete de ce script pour le format exact.
"@
}

$cfg = Get-Content $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($k in @('Host', 'User', 'Password', 'RemoteRoot')) {
  if (-not $cfg.$k) { throw "Champ '$k' manquant dans $ConfigPath" }
}
$proto = if ($cfg.Protocol) { $cfg.Protocol.ToLowerInvariant() } else { 'ftp' }
if ($proto -notin @('ftp', 'ftps', 'sftp')) { throw "Protocol invalide : '$proto' (attendu ftp, ftps ou sftp)" }

# --- mot de passe : decodage selon PasswordEncoding ---------------------------
# $pwd ne doit jamais etre affiche ni journalise : les messages d'erreur ci-dessous
# decrivent le probleme sans jamais reveler la valeur.
$pwdEnc = if ($cfg.PasswordEncoding) { $cfg.PasswordEncoding.ToLowerInvariant() } else { 'plain' }
switch ($pwdEnc) {
  'plain' { $pwd = $cfg.Password }
  'base64' {
    try { $pwd = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($cfg.Password)) }
    catch { throw "Password n'est pas du base64 valide (PasswordEncoding vaut 'base64' dans $ConfigPath)." }
  }
  'dpapi' {
    try {
      $sec = ConvertTo-SecureString $cfg.Password -ErrorAction Stop
      $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
      try { $pwd = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr) }
      finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    } catch {
      throw "Dechiffrement DPAPI impossible. Une valeur 'dpapi' n'est lisible que par le compte Windows et la machine qui l'ont produite : regenere-la ici avec (ConvertTo-SecureString '<mdp>' -AsPlainText -Force | ConvertFrom-SecureString)."
    }
  }
  default { throw "PasswordEncoding inconnu : '$pwdEnc' (attendu plain, base64 ou dpapi)" }
}
if ([string]::IsNullOrEmpty($pwd)) { throw "Le mot de passe decode est vide (verifie Password / PasswordEncoding dans $ConfigPath)." }

# Fichiers de developpement : ne partent jamais en ligne.
$excludes = @('README.md', 'setup-iis.ps1', 'web.config.local', '.deploy', 'deploy.log')

$files = Get-ChildItem $siteDir -Recurse -File | Where-Object {
  $rel = $_.FullName.Substring($siteDir.Length + 1)
  $name = $_.Name
  ($excludes -notcontains $name) -and ($rel -notmatch '(^|\\)\.') -and ($name -notlike '*.local.*')
}

if (-not $files) { throw "Aucun fichier a publier dans $siteDir" }

$totalKo = [math]::Round((($files | Measure-Object Length -Sum).Sum) / 1KB, 1)
Write-Host "Site   : $siteDir"
Write-Host "Cible  : ${proto}://$($cfg.Host)$($cfg.RemoteRoot)"
Write-Host "Fichiers : $($files.Count)  ($totalKo Ko)"

if ($WhatIf) {
  Write-Host "`n-- WhatIf : rien n'est transfere --" -ForegroundColor Yellow
  $files | ForEach-Object { Write-Host ("  " + $_.FullName.Substring($siteDir.Length + 1)) }
  return
}

# --- SFTP : delegue a WinSCP --------------------------------------------------
if ($proto -eq 'sftp') {
  $winscp = Get-Command 'winscp.com' -ErrorAction SilentlyContinue
  $winscpPath = if ($winscp) { $winscp.Source } else { Join-Path ${env:ProgramFiles(x86)} 'WinSCP\WinSCP.com' }
  if (-not (Test-Path $winscpPath)) {
    throw "SFTP demande mais WinSCP est introuvable (winscp.com). Installe-le depuis https://winscp.net"
  }
  $port = if ($cfg.Port -and $cfg.Port -gt 0) { $cfg.Port } else { 22 }
  # -hostkey=* accepte la cle du serveur : acceptable pour un deploiement vers un hote connu et maitrise.
  $script = @"
open sftp://$($cfg.User):$pwd@$($cfg.Host):$port/ -hostkey=*
synchronize remote -delete=off "$siteDir" "$($cfg.RemoteRoot)"
exit
"@
  $tmp = Join-Path $env:TEMP "winscp-koton-$PID.txt"
  try {
    Set-Content -Path $tmp -Value $script -Encoding ASCII
    & $winscpPath /ini=nul /script="$tmp"
    if ($LASTEXITCODE -ne 0) { throw "WinSCP a echoue (code $LASTEXITCODE)" }
  } finally {
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue   # contient le mot de passe
  }
  Write-Host "`nSite publie (sftp)." -ForegroundColor Green
  return
}

# --- FTP / FTPS natifs --------------------------------------------------------
$port   = if ($cfg.Port -and $cfg.Port -gt 0) { $cfg.Port } else { 21 }
$base   = "ftp://$($cfg.Host):$port" + $cfg.RemoteRoot.TrimEnd('/')
$cred   = New-Object System.Net.NetworkCredential($cfg.User, $pwd)
$useTls = ($proto -eq 'ftps')
$made   = New-Object 'System.Collections.Generic.HashSet[string]'

function New-FtpRequest {
  param([string]$Uri, [string]$Method)
  $r = [System.Net.FtpWebRequest]::Create($Uri)
  $r.Method = $Method
  $r.Credentials = $cred
  $r.EnableSsl = $useTls
  $r.UsePassive = $true
  $r.UseBinary = $true
  $r.KeepAlive = $false
  $r.Timeout = 60000
  return $r
}

# Cree un dossier distant (silencieux s'il existe deja : 550 = deja present).
function Ensure-RemoteDir {
  param([string]$RelDir)
  if ([string]::IsNullOrEmpty($RelDir) -or $made.Contains($RelDir)) { return }
  $parent = Split-Path $RelDir -Parent
  if ($parent) { Ensure-RemoteDir $parent }
  try {
    $req = New-FtpRequest "$base/$($RelDir -replace '\\','/')" ([System.Net.WebRequestMethods+Ftp]::MakeDirectory)
    $req.GetResponse().Close()
  } catch [System.Net.WebException] {
    $resp = $_.Exception.Response
    if (-not $resp -or $resp.StatusCode -ne [System.Net.FtpStatusCode]::ActionNotTakenFileUnavailable) { throw }
  }
  [void]$made.Add($RelDir)
}

$n = 0
foreach ($f in $files) {
  $rel    = $f.FullName.Substring($siteDir.Length + 1)
  $relDir = Split-Path $rel -Parent
  if ($relDir) { Ensure-RemoteDir $relDir }

  $req = New-FtpRequest "$base/$($rel -replace '\\','/')" ([System.Net.WebRequestMethods+Ftp]::UploadFile)
  $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
  $req.ContentLength = $bytes.Length
  $stream = $req.GetRequestStream()
  try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Close() }
  $req.GetResponse().Close()

  $n++
  Write-Host ("  [{0}/{1}] {2}" -f $n, $files.Count, $rel)
}

Write-Host "`nSite publie ($proto) : $n fichiers." -ForegroundColor Green
