@echo off
rem Removes Ya Mini Player, its shortcuts and its saved settings for the current user.
setlocal
set "DEST=%LOCALAPPDATA%\Programs\YaMiniPlayer"

taskkill /im YaMiniPlayer.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul
taskkill /f /im YaMiniPlayer.exe >nul 2>&1

powershell -NoProfile -ExecutionPolicy Bypass -Command "foreach ($place in 'Desktop', 'Programs') { Remove-Item (Join-Path ([Environment]::GetFolderPath($place)) 'Ya Mini Player.lnk') -ErrorAction SilentlyContinue }"
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\YaMiniPlayer" /f >nul 2>&1
if exist "%APPDATA%\YaMiniPlayer" rmdir /s /q "%APPDATA%\YaMiniPlayer"

echo Ya Mini Player has been removed.
ping -n 4 127.0.0.1 >nul

rem This script lives in the folder being removed, so hand the last step to a separate process
start "" /b cmd /c "cd /d "%TEMP%" & ping -n 2 127.0.0.1 >nul & rmdir /s /q "%DEST%""
exit /b 0
