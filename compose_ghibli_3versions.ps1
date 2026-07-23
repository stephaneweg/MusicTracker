# Generate 3 Ghibli pieces from the corpus, one per forced melody CHARACTER (enjoué / modéré / calme),
# EACH WITH ITS OWN SEED.  Usage: compose_ghibli_3versions.ps1 [seedEnjoue] [seedModere] [seedCalme]
$ErrorActionPreference = "Stop"
$repo   = "C:\Users\swe\source\repos\MusicTracker"
$bin    = "$repo\MusicTracker\bin\Debug"
$corpus = "$repo\corpus\Ghibli"
$seedEnjoue = if ($args.Count -ge 1) { [int]$args[0] } else { 1187 }
$seedModere = if ($args.Count -ge 2) { [int]$args[1] } else { 5249 }
$seedCalme  = if ($args.Count -ge 3) { [int]$args[2] } else { 8806 }
$seedMajest = if ($args.Count -ge 4) { [int]$args[3] } else { 3041 }

Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$an  = $asm.GetType("MusicTracker.Engine.ComposerV2.CorpusAnalyzerV2")
$gh  = $asm.GetType("MusicTracker.Engine.ComposerV2.GhibliComposer")

Write-Host "Analyzing Ghibli corpus (in-memory)..."
$model = $an.GetMethod("Analyze").Invoke($null, @([string]$corpus))

function Get-MidiBpm($path) {
  $mf = New-Object NAudio.Midi.MidiFile($path, $false)
  foreach ($tr in $mf.Events) { foreach ($e in $tr) {
    $te = $e -as [NAudio.Midi.TempoEvent]; if ($null -ne $te) { return [math]::Round($te.Tempo, 0) } } }
  return 0
}

# ASCII tokens (PS 5.1 mangles non-BOM accents); the composer normalizes them to the internal labels.
$versions = @(
  @{ token = "enjouee";    name = "enjoue";    seed = $seedEnjoue },
  @{ token = "majestueux"; name = "majestueux"; seed = $seedMajest },
  @{ token = "moderee";    name = "modere";    seed = $seedModere },
  @{ token = "calme";      name = "calme";     seed = $seedCalme  }
)

foreach ($v in $versions) {
  $mid = "$repo\corpus\ghibli_v2_$($v.name).mid"
  $inst = [System.Activator]::CreateInstance($gh)
  $gh.GetProperty("CharacterOverride").SetValue($inst, [string]$v.token)
  $gh.GetMethod("Compose").Invoke($inst, @($model, [int]$v.seed, [string]$mid)) | Out-Null
  $bpm  = Get-MidiBpm $mid
  $mode = [string]$gh.GetProperty("ChosenMode").GetValue($inst)
  $min  = if ([bool]$gh.GetProperty("ChosenMinor").GetValue($inst)) { "min" } else { "MAJ" }
  $fi   = Get-Item $mid
  Write-Host ("[{0,-8}] seed {1,5}  {2,-10} {3}  {4} bpm  ({5:N0} bytes)" -f $v.token, $v.seed, $mode, $min, $bpm, $fi.Length)
}
Write-Host "Done. 3 versions written to corpus\ghibli_v2_{enjoue,modere,calme}.mid (+ .mscx)."
