<#
  Crea i file da pubblicare in una release di GitHub:
    SwinKnife-Setup-<versione>.exe      installer (consigliato)
    SwinKnife-<versione>-portable.zip   stessa app da estrarre ed eseguire
    SHA256SUMS.txt
  L'app è "self-contained": chi la scarica non deve installare .NET.

  Uso:  powershell -ExecutionPolicy Bypass -File tools\release\build.ps1 [-Version 0.4.0] [-OutDir <cartella>]
  Serve Inno Setup 7 a 64 bit (https://jrsoftware.org/isdl.php); in alternativa indica ISCC.exe con la variabile ISCC.
#>
param(
    [string]$Version,
    [string]$OutDir = (Join-Path $env:LOCALAPPDATA 'SwinKnife\release')
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$proj = Join-Path $root 'src\SwinKnife\SwinKnife.csproj'

if (-not $Version) { $Version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versione non valida: '$Version' (atteso es. 0.4.0)" }

$iscc = @($env:ISCC,
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup non trovato: installalo da https://jrsoftware.org/isdl.php o imposta la variabile ISCC' }

$app = Join-Path $OutDir 'app'
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Force $app | Out-Null

Write-Host "== SwinKnife ${Version}: compilo (self-contained, win-x64)"
dotnet publish $proj -c Release -r win-x64 --self-contained true -o $app --nologo `
    -p:Version=$Version -p:DebugType=none -p:GenerateDocumentationFile=false
if ($LASTEXITCODE) { throw 'dotnet publish non riuscito' }

Write-Host '== Creo l''installer'
& $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$app" "/O$OutDir" (Join-Path $root 'installer\SwinKnife.iss')
if ($LASTEXITCODE) { throw 'Inno Setup non riuscito' }

Write-Host '== Creo la versione portable'
$zip = Join-Path $OutDir "SwinKnife-$Version-portable.zip"
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip -CompressionLevel Optimal

$files = Get-ChildItem $OutDir -File | Where-Object Name -match '\.(exe|zip)$'
$files | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name } |
    Set-Content (Join-Path $OutDir 'SHA256SUMS.txt') -Encoding ascii

Write-Host "`nFatto, in $OutDir"
$files | ForEach-Object { '  {0,-40} {1,6:N1} MB' -f $_.Name, ($_.Length / 1MB) }
