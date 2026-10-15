[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$captureRoot = Split-Path -Parent $PSScriptRoot
$captureCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$captureOutput = Join-Path $captureRoot 'obj\autopaste-tests'
New-Item -ItemType Directory -Path $captureOutput -Force | Out-Null
$captureTestExe = Join-Path $captureOutput 'AutoPasteTests.exe'
$captureSources = @('src\TranscriptCapture.cs', 'src\AutoPasteController.cs', 'src\AutoPasteSettings.cs',
    'src\WindowsPasteDesktop.cs', 'src\PasteKeySequence.cs', 'tests\AutoPasteTests.cs') | ForEach-Object { Join-Path $captureRoot $_ }
# Deterministic production-controller replay; simulated keyboard/clipboard and a
# unique registry key. This script never sends keys or changes input focus.
& $captureCompiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /codepage:65001 "/out:$captureTestExe" @captureSources
if ($LASTEXITCODE -ne 0) { throw 'Auto-paste test compilation failed.' }
& $captureTestExe
if ($LASTEXITCODE -ne 0) { throw 'Auto-paste regression tests failed.' }
