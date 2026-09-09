@echo off
setlocal

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer-Windows.ps1"
if errorlevel 1 (
    echo.
    echo L'installation a rencontre une erreur.
    pause
    exit /b 1
)

echo.
echo Installation terminee. Appuie sur une touche pour fermer.
pause >nul
endlocal
