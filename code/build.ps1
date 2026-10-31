<#
  Build MouseMotionEffect and pack it into a .ymme file.

  Usage (PowerShell):
    .\build.ps1 -Ymm4Dir "C:\Path\To\YukkuriMovieMaker4"

  Ymm4Dir is the folder that contains YukkuriMovieMaker.Plugin.dll.
  Requires the .NET 10 SDK.
#>
param(
    [Parameter(Mandatory = $true)][string]$Ymm4Dir,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$Ymm4Dir = $Ymm4Dir.TrimEnd('\', '/')
$root = $PSScriptRoot

if (-not (Test-Path (Join-Path $Ymm4Dir 'YukkuriMovieMaker.Plugin.dll'))) {
    throw "YukkuriMovieMaker.Plugin.dll was not found in: $Ymm4Dir"
}

dotnet build (Join-Path $root 'MouseMotionEffect.csproj') -c $Configuration "-p:Ymm4Dir=$Ymm4Dir"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$outDir = Join-Path $root "bin\$Configuration\net10.0-windows10.0.19041.0"
$dll = Join-Path $outDir 'MouseMotionEffect.dll'
if (-not (Test-Path $dll)) { throw "Output not found: $dll" }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist 'MouseMotionEffect.zip'
$ymme = Join-Path $dist 'MouseMotionEffect.ymme'
Remove-Item $zip, $ymme -ErrorAction SilentlyContinue

Compress-Archive -Path $dll -DestinationPath $zip
Rename-Item $zip 'MouseMotionEffect.ymme'

Write-Host ""
Write-Host "Done: $ymme"
Write-Host "Double-click it to install, then restart YMM4."
Write-Host "(If it does not install, see 'manual install' in README.md)"
