@echo off
setlocal
cd /d "%~dp0"
title Installazione SwinKnife

rem L'app compilata va fuori da OneDrive (le librerie native pesano parecchio)
set "OUT=%LOCALAPPDATA%\SwinKnife\app"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo Serve il .NET 10 SDK: https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

tasklist /FI "IMAGENAME eq SwinKnife.exe" | find /I "SwinKnife.exe" >nul
if not errorlevel 1 (
    echo Chiudi SwinKnife prima di aggiornarlo, poi premi un tasto.
    pause >nul
)

echo Compilo SwinKnife (Release)...
dotnet publish src\SwinKnife\SwinKnife.csproj -c Release -o "%OUT%" --nologo -v q || goto :error

echo Creo i collegamenti sul Desktop e nel menu Start...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$w = New-Object -ComObject WScript.Shell;" ^
  "foreach ($d in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {" ^
  "  $s = $w.CreateShortcut((Join-Path $d 'SwinKnife.lnk'));" ^
  "  $s.TargetPath = '%OUT%\SwinKnife.exe';" ^
  "  $s.WorkingDirectory = '%OUT%';" ^
  "  $s.Description = 'SwinKnife - il coltellino svizzero per Windows';" ^
  "  $s.Save() }"

echo.
echo Fatto! SwinKnife e' installato in %OUT%
echo Avvialo dal collegamento sul Desktop o dal menu Start.
start "" "%OUT%\SwinKnife.exe"
exit /b 0

:error
echo.
echo Compilazione non riuscita: controlla i messaggi qui sopra.
pause
exit /b 1
