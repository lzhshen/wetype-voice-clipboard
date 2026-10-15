[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$captureRoot = Split-Path -Parent $PSScriptRoot
$captureCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$captureTestDirectory = Join-Path $captureRoot 'obj\transcript-tests'
New-Item -ItemType Directory -Path $captureTestDirectory -Force | Out-Null
$captureTestExe = Join-Path $captureTestDirectory 'TranscriptCaptureTests.exe'

# Replay frames through the production session tracker with a deterministic clock
# and an in-memory clipboard. No live speech, focus or user clipboard is touched.
& $captureCompiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /codepage:65001 "/out:$captureTestExe" `
    (Join-Path $captureRoot 'src\TranscriptCapture.cs') (Join-Path $captureRoot 'tests\TranscriptCaptureTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Transcript test compilation failed.' }
& $captureTestExe
if ($LASTEXITCODE -ne 0) { throw 'Transcript regression tests failed.' }

$captureSnapshotExe = Join-Path $captureTestDirectory 'VoiceSnapshotReaderTests.exe'
& $captureCompiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /codepage:65001 "/out:$captureSnapshotExe" `
    (Join-Path $captureRoot 'src\TranscriptCapture.cs') (Join-Path $captureRoot 'src\VoiceSnapshotReader.cs') `
    (Join-Path $captureRoot 'tests\VoiceSnapshotReaderTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Voice snapshot test compilation failed.' }
& $captureSnapshotExe
if ($LASTEXITCODE -ne 0) { throw 'Voice snapshot regression tests failed.' }
