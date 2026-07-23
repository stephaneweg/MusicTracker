$ErrorActionPreference = "Stop"
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"
$f   = "C:\Users\swe\source\repos\MusicTracker\theme_variations.mscx"
[void][xml](Get-Content $f -Raw)
Write-Host "XML well-formed: OK"
Get-ChildItem "$bin\NAudio*.dll" | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom("$bin\MusicTracker.exe")
$sc = $asm.GetType("MusicTracker.Engine.MuseScoreImporter").GetMethod("Load").Invoke($null, @([string]$f))
function F($o,$n){ $o.GetType().GetField($n).GetValue($o) }
$tr = F $sc 'Tracks'; $n0 = F $tr[0] 'Notes'
Write-Host ("Re-import OK: {0} track(s), {1} notes, {2} bars" -f $tr.Count, $n0.Count, ([int]((F $sc 'SliceCount')/96)))
