[CmdletBinding()]
param([switch]$DesktopInput)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $DesktopInput) { throw 'Pass -DesktopInput on a dedicated test desktop; this check opens its own input window.' }
$captureRoot = Split-Path -Parent $PSScriptRoot
$captureCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$captureDirectory = Join-Path $captureRoot 'obj\keyboard-tests'
New-Item -ItemType Directory -Path $captureDirectory -Force | Out-Null
$captureExe = Join-Path $captureDirectory 'KeyboardTransportTests.exe'
$captureSources = @('src\AutoPasteController.cs', 'src\WindowsPasteDesktop.cs', 'tests\KeyboardTransportTests.cs') | ForEach-Object { Join-Path $captureRoot $_ }
& $captureCompiler /nologo /target:exe /platform:x64 /warnaserror+ /codepage:65001 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$captureExe" @captureSources
if ($LASTEXITCODE -ne 0) { throw 'Keyboard transport test compilation failed.' }
& $captureExe --desktop-input
if ($LASTEXITCODE -ne 0) { throw 'Native keyboard transport test failed.' }
