[CmdletBinding()]
param([switch]$DesktopClipboard)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $DesktopClipboard) {
    throw 'Pass -DesktopClipboard to allow temporary test writes to the desktop clipboard. Avoid copying other content during this test.'
}
$captureRoot = Split-Path -Parent $PSScriptRoot
$captureCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$captureTestDirectory = Join-Path $captureRoot 'obj\clipboard-tests'
New-Item -ItemType Directory -Path $captureTestDirectory -Force | Out-Null
$captureTestExe = Join-Path $captureTestDirectory 'ClipboardTextWriterTests.exe'

# Actual Win32 writes/listeners. Tests preserve clipboard data in memory.
& $captureCompiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /codepage:65001 `
    /reference:System.Windows.Forms.dll "/out:$captureTestExe" `
    (Join-Path $captureRoot 'src\ClipboardTextWriter.cs') `
    (Join-Path $captureRoot 'src\TranscriptCapture.cs') `
    (Join-Path $captureRoot 'tests\ClipboardTextWriterTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Clipboard integration test compilation failed.' }
& $captureTestExe --desktop-clipboard
if ($LASTEXITCODE -ne 0) { throw 'Clipboard integration tests failed.' }
