[CmdletBinding()]
param([switch]$LiveProbe)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$captureRoot = Split-Path -Parent $PSScriptRoot
$captureVersion = (Get-Content -LiteralPath (Join-Path $captureRoot 'VERSION') -Raw).Trim()
$captureDist = Join-Path $captureRoot 'dist'
$captureStandalone = Join-Path $captureDist "WeTypeVoiceCapture-v$captureVersion-win-x64.exe"
$captureZipPath = Join-Path $captureDist "wetype-voice-clipboard-v$captureVersion-win-x64.zip"

# Check both downloaded artifacts against the exact checksum list supplied to users.
$captureSeen = @()
foreach ($captureLine in (Get-Content -LiteralPath (Join-Path $captureDist 'SHA256SUMS.txt'))) {
    if ($captureLine -notmatch '^([a-f0-9]{64})  ([^\\/]+)$') { throw 'Invalid checksum record.' }
    $captureExpectedHash = $Matches[1]
    $captureName = $Matches[2]
    $captureSeen += $captureName
    $captureActualHash = (Get-FileHash -LiteralPath (Join-Path $captureDist $captureName) -Algorithm SHA256).Hash
    if ($captureActualHash -ine $captureExpectedHash) { throw "Checksum mismatch: $captureName" }
}
$captureExpectedNames = @([IO.Path]::GetFileName($captureStandalone), [IO.Path]::GetFileName($captureZipPath))
if (@(Compare-Object ($captureSeen | Sort-Object) ($captureExpectedNames | Sort-Object)).Count -ne 0) {
    throw 'Checksum list does not describe exactly the EXE and ZIP release files.'
}

# Validate the actual executable header and version resource rather than its filename.
$captureBytes = [IO.File]::ReadAllBytes($captureStandalone)
$capturePe = [BitConverter]::ToInt32($captureBytes, 0x3c)
if ($captureBytes[0] -ne 0x4d -or $captureBytes[1] -ne 0x5a -or
    [BitConverter]::ToUInt32($captureBytes, $capturePe) -ne 0x4550 -or
    [BitConverter]::ToUInt16($captureBytes, $capturePe + 4) -ne 0x8664 -or
    [BitConverter]::ToUInt16($captureBytes, $capturePe + 24) -ne 0x20b -or
    [BitConverter]::ToUInt16($captureBytes, $capturePe + 24 + 68) -ne 2) {
    throw 'Release binary is not a Windows x64 graphical executable.'
}
$captureInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($captureStandalone)
if ($captureInfo.FileVersion -ne "$captureVersion.0") { throw 'Executable version does not match VERSION.' }
Write-Output "PASS: EXE architecture, version $captureVersion, and release checksums."

# Test from the ZIP, which is the environment a user gets after downloading.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$captureZip = [IO.Compression.ZipFile]::OpenRead($captureZipPath)
try {
    $captureEntryNames = @($captureZip.Entries | ForEach-Object FullName | Sort-Object)
    $captureAllowed = @('LICENSE.txt', 'README.txt', 'WeTypeVoiceCapture.exe') | Sort-Object
    if (@(Compare-Object $captureEntryNames $captureAllowed).Count -ne 0) {
        throw 'Unexpected files or missing files in the portable ZIP.'
    }
} finally { $captureZip.Dispose() }

$captureTestRoot = Join-Path $captureRoot 'artifacts'
New-Item -ItemType Directory -Path $captureTestRoot -Force | Out-Null
$captureExtracted = Join-Path $captureTestRoot ('package test ' + [Guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory($captureZipPath, $captureExtracted)
$captureExtractedExe = Join-Path $captureExtracted 'WeTypeVoiceCapture.exe'
if ((Get-FileHash -LiteralPath $captureExtractedExe).Hash -ne (Get-FileHash -LiteralPath $captureStandalone).Hash) {
    throw 'ZIP and standalone executable differ.'
}
Write-Output 'PASS: ZIP contains exactly the executable, instructions, and license; its EXE matches the standalone download.'

$captureProbeReport = Join-Path $captureExtracted 'probe.txt'
# The helper is windowless and read-only; it never starts dictation or writes the clipboard.
$captureProbe = Start-Process -FilePath $captureExtractedExe -ArgumentList @('--probe', ('"{0}"' -f $captureProbeReport)) -WindowStyle Hidden -PassThru
if (-not $captureProbe.WaitForExit(15000)) {
    $captureProbe.Kill()
    throw 'Packaged executable did not finish its read-only startup probe within 15 seconds.'
}
$captureProbe.Refresh()
$captureReport = Get-Content -LiteralPath $captureProbeReport -Raw
if ($LiveProbe) {
    if ($captureProbe.ExitCode -ne 0 -or $captureReport -notmatch '(?m)^source_valid=true\r?$') {
        throw "Packaged live probe failed: $captureReport"
    }
    Write-Output 'PASS: packaged executable read the live WeType voice source without keyboard, focus, speech, or clipboard operations.'
} else {
    if ($captureProbe.ExitCode -notin @(0, 1) -or $captureReport -notmatch '(?m)^source_valid=(true|false)\r?$') {
        throw "Packaged executable failed to start correctly: $captureReport"
    }
    if ($captureProbe.ExitCode -eq 0) {
        Write-Output 'PASS: packaged executable started and found a compatible live WeType voice source.'
    } else {
        Write-Output 'PASS: packaged executable started and reported that no compatible live source was available; live integration was not tested.'
    }
}
Write-Output 'Package validation completed.'
