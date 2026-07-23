# Melody NOTE-RESOLVER test driver (Composer V2 / MelodyResolverV2).
# Holds out every Nth interior melody note of each test file and measures how well the Viterbi resolver
# recovers it from the CHORDS (degree before/during/after) + the surrounding notes. Loads the built
# MusicTracker.exe by reflection (LoadFrom context probes bin\Debug for System.Text.Json, NAudio, ...).
#
#   .\resolve_test.ps1                                   # ghibli model over corpus\Ghibli, hole every 4th note
#   .\resolve_test.ps1 -Model bach_solo_model_v2.json -Test midi\other -N 3 -Max 12
param(
  [string]$Model = "ghibli_model_v2.json",
  [string]$Test  = "corpus\Ghibli",     # a file OR a folder of .mid/.mscz/.mscx
  [int]$N        = 4,                    # hold out every Nth interior note
  [int]$Max      = 8,                    # cap on files when -Test is a folder
  [string]$Bin   = "bin\Debug"          # build output to load (e.g. bin\ResolveTest while the app is running)
)
$ErrorActionPreference = "Stop"
$repo = "C:\Users\swe\source\repos\MusicTracker"
$bin  = if (Test-Path $Bin) { (Resolve-Path $Bin).Path } else { "$repo\MusicTracker\$Bin" }
$modelPath = if (Test-Path $Model) { (Resolve-Path $Model).Path } else { "$bin\Data\models\$Model" }
if (-not (Test-Path $modelPath)) { $modelPath = "$repo\MusicTracker\Data\models\$Model" }
if (-not (Test-Path $modelPath)) { throw "Model not found: $Model" }
$testPath = if (Test-Path $Test) { (Resolve-Path $Test).Path } else { "$repo\$Test" }
if (-not (Test-Path $testPath)) { throw "Test path not found: $Test" }

# Resolve ANY requested assembly (regardless of version) to the matching DLL in $bin. Needed because
# ComposerV2Runtime.ReadFromPath uses System.Text.Json, whose deps rely on the app's binding redirects
# (App.config) that PowerShell's host does not apply — without this the JsonSerializer type init throws.
$global:__resolveBin = $bin
$onResolve = [System.ResolveEventHandler] {
  param($s, $e)
  $simple = ($e.Name -split ',')[0].Trim()
  $dll = Join-Path $global:__resolveBin "$simple.dll"
  if (Test-Path $dll) { return [System.Reflection.Assembly]::LoadFrom($dll) }
  return $null
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($onResolve)

# Load NAudio (MidiImporter) then the exe via LoadFrom so transitive deps resolve from $bin.
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$rv  = $asm.GetType("MusicTracker.Engine.ComposerV2.MelodyResolverV2")
if ($null -eq $rv) { throw "MelodyResolverV2 not found - rebuild MusicTracker first." }

# All object marshalling stays inside C# (EvaluateFolderReport) — PowerShell just prints the returned string.
Write-Host ("Model : {0}" -f (Split-Path $modelPath -Leaf))
Write-Host ("Test  : {0}  (hole every {1} notes, max {2} files)" -f $Test, $N, $Max)
Write-Host ("-" * 96)
$report = $rv.GetMethod("EvaluateFolderReport").Invoke($null, @([string]$modelPath, [string]$testPath, [int]$N, [int]$Max))
Write-Host $report
Write-Host "Baselines: random top1 ~8% (1/12), diatonic-random ~14% (1/7), chord-tone-only ~33%."
