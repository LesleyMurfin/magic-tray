@echo off
setlocal EnableExtensions
title Magic Tray — keyboard battery patch

::: User-initiated PATH-C wrapper. Elevates, then runs kbd-patch-cachedservices.ps1.
::: Does not change the patch protocol. Requires -Mac: the ps1 has no default MAC
::: and refuses to run without one, so the address has to come from here.
::: Running it twice is safe - a keyboard that already carries the unlock is
::: reported as already patched and nothing is written.
::: Everything after -Mac is handed to the ps1 as it stands, so -Restore (undo the
::: patch from the backups it saved) and -DryRun work from here too.

set "HASMAC="
echo(%*| findstr /I /C:"-Mac" >nul && set "HASMAC=1"
if not defined HASMAC (
  echo.
  echo Usage: Install-KeyboardBattery.cmd -Mac 12hexdigits [-Restore] [-DryRun]
  echo Example: Install-KeyboardBattery.cmd -Mac aabbccddeeff
  echo Example: Install-KeyboardBattery.cmd -Mac aabbccddeeff -Restore
  echo.
  echo -Restore puts the keyboard's original Bluetooth record back from the backup
  echo the patch saved before it wrote. -DryRun checks everything and writes nothing.
  echo.
  echo You must pass the keyboard Bluetooth MAC (12 hex digits, no colons^).
  echo The script has no default address and will not run without one.
  echo Find the address in Device Manager - Bluetooth - keyboard - Details - Bluetooth device address.
  echo.
  pause
  exit /b 1
)

net session >nul 2>&1
if errorlevel 1 (
  echo Requesting Administrator permission...
  powershell -NoProfile -Command "Start-Process -LiteralPath '%~f0' -Verb RunAs -ArgumentList '%*'"
  exit /b
)

set "PATCH=%~dp0kbd-patch-cachedservices.ps1"
if not exist "%PATCH%" (
  echo ERROR: kbd-patch-cachedservices.ps1 was not found next to this file.
  echo Download both files from the same Magic Tray GitHub Release and keep them in one folder.
  pause
  exit /b 1
)

set "ISDRY="
echo(%*| findstr /I /C:"-DryRun" >nul && set "ISDRY=1"
set "ISUNDO="
echo(%*| findstr /I /C:"-Restore" >nul && set "ISUNDO=1"

powershell -NoProfile -ExecutionPolicy Bypass -File "%PATCH%" %*
set "ERR=%ERRORLEVEL%"
echo.
if not "%ERR%"=="0" goto :ended_badly
if defined ISDRY goto :wrote_nothing
if defined ISUNDO goto :undone
echo Toggle Bluetooth off and on so Windows re-reads the SDP cache.
goto :finished

:ended_badly
if defined ISUNDO (
  echo The undo ended on exit code %ERR%.
  echo Read the log named above: it says which records were put back, which were not,
  echo and why. The ones that were not are untouched.
) else (
  echo The unlock ended on exit code %ERR%.
  echo Read the log named above: it says what the run found and what it changed.
)
goto :finished

:wrote_nothing
if defined ISUNDO (
  echo Dry run: nothing was written, not even a copy of the bytes an undo would replace.
) else (
  echo Dry run: nothing was written to the registry.
)
goto :finished

:undone
echo The keyboard's original Bluetooth record is back, so the battery percent will
echo stop being reported for it.
echo Toggle Bluetooth off and on so Windows re-reads the SDP cache.
goto :finished

:finished
pause
exit /b %ERR%
