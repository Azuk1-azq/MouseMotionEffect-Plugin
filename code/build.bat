@echo off
setlocal
cd /d "%~dp0"
title MouseMotionEffect build

rem ===== 1. find the YMM4 folder (the one that has YukkuriMovieMaker.Plugin.dll) =====
set "CFG=%~dp0ymm4dir.txt"
set "YMM4DIR="

if not "%~1"=="" set "YMM4DIR=%~1"
if defined YMM4DIR goto checkdir
if exist "%CFG%" set /p YMM4DIR=<"%CFG%"
if defined YMM4DIR goto checkdir
goto autodetect

:checkdir
set "YMM4DIR=%YMM4DIR:"=%"
if "%YMM4DIR:~-1%"=="\" set "YMM4DIR=%YMM4DIR:~0,-1%"
if exist "%YMM4DIR%\YukkuriMovieMaker.Plugin.dll" goto savedir
set "YMM4DIR="

:autodetect
for %%D in (
  "%USERPROFILE%\Documents\ymm4"
  "%USERPROFILE%\Documents\YukkuriMovieMaker4"
  "%USERPROFILE%\Desktop\YukkuriMovieMaker4"
  "%USERPROFILE%\Downloads\YukkuriMovieMaker4"
  "C:\YukkuriMovieMaker4"
  "D:\YukkuriMovieMaker4"
  "C:\ymm4"
) do call :trydir "%%~D"
if defined YMM4DIR goto savedir

:ask
echo.
echo YukkuriMovieMaker.Plugin.dll was not found automatically.
echo Drag the YMM4 folder (the folder that contains YukkuriMovieMaker.exe) onto this window,
echo then press Enter.
echo.
set "YMM4DIR="
set /p "YMM4DIR=YMM4 folder: "
if not defined YMM4DIR goto ask
set "YMM4DIR=%YMM4DIR:"=%"
if "%YMM4DIR:~-1%"=="\" set "YMM4DIR=%YMM4DIR:~0,-1%"
if exist "%YMM4DIR%\YukkuriMovieMaker.Plugin.dll" goto savedir
echo.
echo YukkuriMovieMaker.Plugin.dll is not in: %YMM4DIR%
goto ask

:savedir
>"%CFG%" echo %YMM4DIR%
echo YMM4 folder: %YMM4DIR%

rem ===== 2. make sure the .NET 10 SDK is installed =====
where dotnet >nul 2>nul
if errorlevel 1 goto installdotnet
dotnet --list-sdks | findstr /b "10." >nul
if errorlevel 1 goto installdotnet
goto build

:installdotnet
echo.
echo .NET 10 SDK is required. Installing it with winget. This may take a while...
winget install --id Microsoft.DotNet.SDK.10 -e --accept-package-agreements --accept-source-agreements
if errorlevel 1 goto installfailed
echo.
echo Install finished. Close this window and run build.bat again.
pause
exit /b 0

:installfailed
echo.
echo Automatic install failed.
echo Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0
echo and run build.bat again.
pause
exit /b 1

rem ===== 3. build =====
:build
echo.
echo Building...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Ymm4Dir "%YMM4DIR%"
if errorlevel 1 goto failed

echo.
echo Done. Opening the dist folder.
echo Double-click MouseMotionEffect.ymme to install, then restart YMM4.
start "" "%~dp0dist"
pause
exit /b 0

:failed
echo.
echo Build failed. Please send the error text above.
pause
exit /b 1

:trydir
if defined YMM4DIR exit /b 0
if exist "%~1\YukkuriMovieMaker.Plugin.dll" set "YMM4DIR=%~1"
exit /b 0
