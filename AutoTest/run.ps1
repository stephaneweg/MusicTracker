# Full automated test pass: build the app + harness, then run the UI scenarios.
# Writes AutoTest\results.json and prints a summary.
# Exit codes: 0 = all green, 1 = at least one scenario failed, 3 = app build failed,
#             4 = harness build failed, 2 = MSBuild not found.
# Used by the musictracker-daily-test scheduled task; also runnable by hand.
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$repo = Split-Path $here -Parent
$results = Join-Path $here "results.json"
$buildLog = Join-Path $here "build.log"

$msbuild = Get-ChildItem "C:\Program Files\Microsoft Visual Studio\2022\*\MSBuild\Current\Bin\MSBuild.exe" -ErrorAction SilentlyContinue |
           Select-Object -First 1 -ExpandProperty FullName
if (-not $msbuild) { Write-Host "MSBUILD_NOT_FOUND"; exit 2 }

# The app is a .NET Framework 4.8 WPF project: MSBuild, not dotnet build.
Write-Host "== Build de l'application ($Configuration) =="
& $msbuild (Join-Path $repo "MusicTracker\MusicTracker.csproj") /p:Configuration=$Configuration /nologo /v:minimal 2>&1 |
    Tee-Object -FilePath $buildLog
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD_FAILED"; exit 3 }

# Parité des langues : le repli de Loc.T est silencieux, donc une clé oubliée ne se voit jamais à l'usage —
# elle affiche simplement du français à un utilisateur anglais. Ce contrôle est le seul qui la détecte.
Write-Host "== Parite des langues =="
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here "check-lang-parity.ps1")
if ($LASTEXITCODE -ne 0) { Write-Host "LANG_PARITY_FAILED"; exit 5 }

Write-Host "== Build du harness de test =="
& dotnet build (Join-Path $here "AutoTest.csproj") -v minimal 2>&1 | Tee-Object -FilePath $buildLog -Append
if ($LASTEXITCODE -ne 0) { Write-Host "HARNESS_BUILD_FAILED"; exit 4 }

# A leftover instance from a previous crashed run would steal the UI automation.
Get-Process KotonStudio -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "== Scenarios UI =="
$exe = Join-Path $repo "MusicTracker\bin\$Configuration\KotonStudio.exe"
$harness = Join-Path $here "bin\Debug\net48\MusicTracker.AutoTest.exe"
& $harness --exe $exe --out $results
$testExit = $LASTEXITCODE

Get-Process KotonStudio -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

if (Test-Path $results) {
    $r = Get-Content $results -Raw | ConvertFrom-Json
    Write-Host ""
    Write-Host "== Resume =="
    foreach ($s in $r.Scenarios) { "{0,-28} {1}" -f $s.Name, $s.Status | Write-Host }
}
exit $testExit
