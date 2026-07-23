# Analyse SIGNAL d'un audio (mp3/wav) : tonalité (Krumhansl), chroma, registre, tempo approx, enveloppe RMS.
# Usage:  .\analyze_audio.ps1 "C:\chemin\vers\fichier.mp3"
$ErrorActionPreference='Stop'
$bin='C:\Users\swe\source\repos\MusicTracker\MusicTracker\bin\Debug'
$csc='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$ns='C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\Facades\netstandard.dll'
$src="$PSScriptRoot\AudioProbe.cs"
$exe="$bin\AudioProbe.exe"   # compiled INTO bin\Debug so it resolves the NAudio dlls at runtime
if((-not (Test-Path $exe)) -or ((Get-Item $src).LastWriteTime -gt (Get-Item $exe).LastWriteTime)){
  & $csc /nologo /target:exe /out:"$exe" /reference:"$bin\NAudio.dll" /reference:"$bin\NAudio.Core.dll" /reference:"$ns" "$src"
  if($LASTEXITCODE -ne 0){ throw "csc failed" }
}
$mp3 = if($args.Count -gt 0){ $args[0] } else { 'C:\Users\swe\Downloads\Brume sur le lac.mp3' }
& $exe $mp3
