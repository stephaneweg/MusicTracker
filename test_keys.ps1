# Verify Krumhansl-Schmuckler key detection against corpus ground truth (AoF=Dm, Goldberg=GM).
$bin = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug"   # NAudio + deps live here
$obj = "C:\Users\swe\source\repos\MusicTracker\MusicTracker\obj\Debug"   # fresh K-S build lives here
foreach ($dll in (Get-ChildItem $bin -Filter "NAudio*.dll")) { [void][System.Reflection.Assembly]::LoadFrom($dll.FullName) }
$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $obj "MusicTracker.exe"))
$load = $asm.GetType("MusicTracker.Engine.MidiImporter").GetMethod("Load")
$keysig = $asm.GetType("MusicTracker.Engine.Score.KeySig")
$detect = $keysig.GetMethod("Detect")
$derive = $keysig.GetMethod("Derive")
$hashType = [System.Collections.Generic.HashSet[int]]
$midiRoot = "C:\Users\swe\source\repos\MusicTracker\midi"

foreach ($d in (Get-ChildItem $midiRoot -Directory)) {
  $tally = @{}; $okN=0; $failN=0; $firstErr=""
  $files = Get-ChildItem $d.FullName -Filter *.mid
  foreach ($f in $files) {
   try {
    $score = $load.Invoke($null, @($f.FullName))
    $tracks = $score.GetType().GetField("Tracks").GetValue($score)
    $w = [double[]]::new(12)
    $firstSlice = [int]::MaxValue
    foreach ($t in $tracks) { if ($t.GetType().GetField("IsDrum").GetValue($t)) { continue }
      foreach ($n in $t.GetType().GetField("Notes").GetValue($t)) { $nt=$n.GetType()
        $pc = ((($nt.GetField("Pitch").GetValue($n))%12)+12)%12; $len=$nt.GetField("LengthSlices").GetValue($n); $st=$nt.GetField("StartSlice").GetValue($n)
        $w[$pc]+=[math]::Max(1,$len); if($st -lt $firstSlice){$firstSlice=$st} } }
    # firstLowPc = lowest pitch among notes near the first onset
    $low=[int]::MaxValue; $firstLowPc=-1; $firstPcs=[System.Activator]::CreateInstance($hashType)
    foreach ($t in $tracks) { if ($t.GetType().GetField("IsDrum").GetValue($t)) { continue }
      foreach ($n in $t.GetType().GetField("Notes").GetValue($t)) { $nt=$n.GetType(); $st=$nt.GetField("StartSlice").GetValue($n)
        if ($st -le $firstSlice+3) { $p=$nt.GetField("Pitch").GetValue($n); $pc=(($p%12)+12)%12; [void]$firstPcs.Add($pc); if($p -lt $low){$low=$p;$firstLowPc=$pc} } } }
    $key = $detect.Invoke($null, @($w, [int]$firstLowPc, $firstPcs))
    $dk = $derive.Invoke($null, @($key, [int]0))
    $name = $dk.GetType().GetField("Name").GetValue($dk)
    if (-not $tally.ContainsKey($name)) { $tally[$name]=0 }; $tally[$name]++; $okN++
   } catch { $failN++; if ($firstErr -eq "") { $firstErr = $_.Exception.Message; if($_.Exception.InnerException){$firstErr=$_.Exception.InnerException.Message} } }
  }
  Write-Output ("==== {0} ====  (ok={1} fail={2})" -f $d.Name, $okN, $failN)
  if ($firstErr -ne "") { Write-Output ("  firstErr: " + $firstErr) }
  $tally.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { Write-Output ("  {0,-16} {1}" -f $_.Key, $_.Value) }
}
