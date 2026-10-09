[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$captureRoot = Split-Path -Parent $PSScriptRoot
$captureVersion = (Get-Content -LiteralPath (Join-Path $captureRoot 'VERSION') -Raw).Trim()
if ($captureVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must contain a version such as 0.1.0.' }
foreach ($capturePart in $captureVersion.Split('.')) {
    if ([int]$capturePart -gt 65534) { throw 'Each version component must be at most 65534.' }
}
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Build this Windows application on Windows.' }

$captureCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $captureCompiler -PathType Leaf)) { throw '.NET Framework x64 C# compiler was not found.' }

$captureStage = Join-Path $captureRoot "obj\package-$captureVersion"
$captureDist = Join-Path $captureRoot 'dist'
New-Item -ItemType Directory -Path $captureStage, $captureDist -Force | Out-Null

$captureVersionSource = Join-Path $captureStage 'VersionInfo.cs'
$captureVersionCode = @"
using System.Reflection;
[assembly: AssemblyVersion("$captureVersion.0")]
[assembly: AssemblyFileVersion("$captureVersion.0")]
[assembly: AssemblyInformationalVersion("$captureVersion")]
"@
[IO.File]::WriteAllText($captureVersionSource, $captureVersionCode, [Text.UTF8Encoding]::new($false))

$captureExe = Join-Path $captureStage 'WeTypeVoiceCapture.exe'
$captureSources = @(Get-ChildItem -LiteralPath (Join-Path $captureRoot 'src') -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName)
$captureCompilerArguments = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warnaserror+', '/codepage:65001',
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
    "/win32manifest:$(Join-Path $captureRoot 'src\app.manifest')", "/out:$captureExe"
) + $captureSources + @($captureVersionSource)
& $captureCompiler @captureCompilerArguments
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE." }

$capturePortableReadme = Join-Path $captureStage 'README.txt'
$captureLicense = Join-Path $captureStage 'LICENSE.txt'
Copy-Item -LiteralPath (Join-Path $captureRoot 'docs\portable-readme.txt') -Destination $capturePortableReadme -Force
Copy-Item -LiteralPath (Join-Path $captureRoot 'LICENSE') -Destination $captureLicense -Force

$captureStandalone = Join-Path $captureDist "WeTypeVoiceCapture-v$captureVersion-win-x64.exe"
$captureZip = Join-Path $captureDist "wetype-voice-clipboard-v$captureVersion-win-x64.zip"
Copy-Item -LiteralPath $captureExe -Destination $captureStandalone -Force
# Package an explicit allowlist; no logs, local settings, or diagnostic files are included.
Compress-Archive -LiteralPath @($captureExe, $capturePortableReadme, $captureLicense) -DestinationPath $captureZip -CompressionLevel Optimal -Force

$captureChecksums = foreach ($captureFile in @($captureStandalone, $captureZip)) {
    $captureHash = (Get-FileHash -LiteralPath $captureFile -Algorithm SHA256).Hash.ToLowerInvariant()
    '{0}  {1}' -f $captureHash, [IO.Path]::GetFileName($captureFile)
}
[IO.File]::WriteAllLines((Join-Path $captureDist 'SHA256SUMS.txt'), [string[]]$captureChecksums, [Text.UTF8Encoding]::new($false))
Write-Output "Built v$captureVersion. Download files are in $captureDist"
Get-Item -LiteralPath $captureStandalone, $captureZip | Select-Object Name, Length
