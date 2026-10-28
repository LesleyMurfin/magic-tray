@echo off
setlocal EnableExtensions
title Magic Tray — keyboard battery patch

:: User-initiated PATH-C wrapper. Elevates, then runs kbd-patch-cachedservices.ps1.
:: Does not change the patch protocol. Requires -Mac: the ps1 has no default MAC
:: and refuses to run without one, so the address has to come from here.
:: Running it twice is safe - a keyboard that already carries the unlock is
:: reported as already patched and nothing is written.

set "HASMAC="
echo(%*| findstr /I /C:"-Mac" >nul && set "HASMAC=1"
if not defined HASMAC (
  echo.
  echo Usage: Install-KeyboardBattery.cmd -Mac 12hexdigits
  echo Example: Install-KeyboardBattery.cmd -Mac aabbccddeeff
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

powershell -NoProfile -ExecutionPolicy Bypass -File "%PATCH%" %*
set "ERR=%ERRORLEVEL%"
echo.
if not "%ERR%"=="0" (
  echo The unlock ended on exit code %ERR%.
  echo Read the log named above: it says what the run found and what it changed.
) else if defined ISDRY (
  echo Dry run: nothing was written to the registry.
) else (
  echo Toggle Bluetooth off and on so Windows re-reads the SDP cache.
)
pause
exit /b %ERR%
