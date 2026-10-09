@echo off
rem Installs Ya Mini Player for the current user. No administrator rights needed.
setlocal
set "SRC=%~dp0"
set "DEST=%LOCALAPPDATA%\Programs\YaMiniPlayer"
set "KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\YaMiniPlayer"

if not exist "%SRC%YaMiniPlayer.exe" (
    echo YaMiniPlayer.exe was not found next to this installer.
    echo Extract the whole zip to a folder first, then run Install.cmd again.
    pause
    exit /b 1
)

rem Close a running copy so its file can be replaced
taskkill /im YaMiniPlayer.exe >nul 2>&1
ping -n 3 127.0.0.1 >nul
taskkill /f /im YaMiniPlayer.exe >nul 2>&1

if not exist "%DEST%" mkdir "%DEST%"

rem Windows can keep the old file locked for a moment after the app closes, so retry for a while
set /a TRIES=0
:copy_exe
copy /y "%SRC%YaMiniPlayer.exe" "%DEST%\" >nul 2>&1 && goto :copied
set /a TRIES+=1
if %TRIES% geq 10 goto :failed
ping -n 2 127.0.0.1 >nul
goto :copy_exe
:copied
copy /y "%SRC%Uninstall.cmd" "%DEST%\" >nul || goto :failed
if exist "%SRC%README.txt" copy /y "%SRC%README.txt" "%DEST%\" >nul

rem Desktop and Start menu shortcuts
powershell -NoProfile -ExecutionPolicy Bypass -Command "$exe = Join-Path $env:LOCALAPPDATA 'Programs\YaMiniPlayer\YaMiniPlayer.exe'; $shell = New-Object -ComObject WScript.Shell; foreach ($place in 'Desktop', 'Programs') { $link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath($place)) 'Ya Mini Player.lnk')); $link.TargetPath = $exe; $link.WorkingDirectory = Split-Path $exe; $link.Description = 'Tiny floating remote for Yandex Music'; $link.Save() }" || goto :failed

rem Entry under Settings - Apps - Installed apps
reg add "%KEY%" /v DisplayName /d "Ya Mini Player" /f >nul
reg add "%KEY%" /v DisplayVersion /d "1.1.0" /f >nul
reg add "%KEY%" /v Publisher /d "Ya Mini Player contributors" /f >nul
reg add "%KEY%" /v DisplayIcon /d "%DEST%\YaMiniPlayer.exe" /f >nul
reg add "%KEY%" /v InstallLocation /d "%DEST%" /f >nul
reg add "%KEY%" /v UninstallString /d "\"%DEST%\Uninstall.cmd\"" /f >nul
reg add "%KEY%" /v NoModify /t REG_DWORD /d 1 /f >nul
reg add "%KEY%" /v NoRepair /t REG_DWORD /d 1 /f >nul

start "" "%DEST%\YaMiniPlayer.exe"
echo Ya Mini Player is installed and running.
echo You can start it any time from the desktop or the Start menu.
ping -n 5 127.0.0.1 >nul
exit /b 0

:failed
echo.
echo Installation did not finish. Nothing else was changed.
pause
exit /b 1
