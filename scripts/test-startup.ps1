[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$captureRoot = Split-Path -Parent $PSScriptRoot
$captureVersion = (Get-Content -LiteralPath (Join-Path $captureRoot 'VERSION') -Raw).Trim()
$captureExe = Join-Path $captureRoot "dist\WeTypeVoiceCapture-v$captureVersion-win-x64.exe"
$captureAssembly = [Reflection.Assembly]::LoadFile($captureExe)
$captureType = $captureAssembly.GetType('WeTypeVoiceCapture.StartupRegistration', $true)
$captureConstructor = $captureType.GetConstructor(
    [Reflection.BindingFlags]'Instance, NonPublic', $null, [type[]]@([string], [string]), $null)
$captureEnabledProperty = $captureType.GetProperty('Enabled')
$captureSetEnabled = $captureType.GetMethod('SetEnabled')
$captureRefreshPath = $captureType.GetMethod('RefreshExecutablePath')

# Exercise the built EXE against a unique registry sandbox, never the real Run key.
$captureSandbox = 'Software\WeTypeVoiceCapture\Tests\' + [Guid]::NewGuid().ToString('N')
$captureRunKey = $captureSandbox + '\Run'
# Keep this script ASCII so Windows PowerShell 5.1 reads it without a BOM.
$captureUnicode = [string][char]0x4e2d + [char]0x6587
$captureFirstPath = Join-Path $captureRoot ("artifacts\startup test $captureUnicode\WeTypeVoiceCapture.exe")
$captureNewPath = Join-Path $captureRoot ("artifacts\upgrade test $captureUnicode\WeTypeVoiceCapture.exe")

function New-StartupRegistration([string]$ExecutablePath) {
    $captureConstructor.Invoke([object[]]@($ExecutablePath, $captureRunKey))
}

function Assert-Startup([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Read-StartupCommand {
    $captureKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($captureRunKey)
    if ($null -eq $captureKey) { return $null }
    try { $captureKey.GetValue('WeTypeVoiceCapture', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    finally { $captureKey.Dispose() }
}

try {
    $captureRegistration = New-StartupRegistration $captureFirstPath
    Assert-Startup (-not $captureEnabledProperty.GetValue($captureRegistration, $null)) 'Startup must be disabled by default.'
    $captureRefreshPath.Invoke($captureRegistration, $null)
    $captureMissingKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($captureSandbox)
    if ($null -ne $captureMissingKey) { $captureMissingKey.Dispose(); throw 'Reading or refreshing a disabled setting created registry data.' }
    Write-Output 'PASS: startup is opt-in; reading or moving a disabled copy creates no entry.'

    $captureKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($captureRunKey)
    try { $captureKey.SetValue('UnrelatedApplication', 'keep this value') }
    finally { $captureKey.Dispose() }

    $captureSetEnabled.Invoke($captureRegistration, [object[]]@($true))
    $captureSetEnabled.Invoke($captureRegistration, [object[]]@($true))
    $captureReloaded = New-StartupRegistration $captureFirstPath
    Assert-Startup ($captureEnabledProperty.GetValue($captureReloaded, $null)) 'Startup did not persist across instances.'
    Assert-Startup ((Read-StartupCommand) -ceq ('"' + $captureFirstPath + '"')) 'The startup command must quote the complete Unicode path with spaces.'
    $captureKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($captureRunKey)
    try {
        Assert-Startup ($captureKey.GetValueKind('WeTypeVoiceCapture') -eq [Microsoft.Win32.RegistryValueKind]::String) 'The startup entry must be REG_SZ.'
    } finally { $captureKey.Dispose() }
    Write-Output 'PASS: enabling is repeatable and persists a quoted executable path, including spaces and Chinese characters.'

    $captureMoved = New-StartupRegistration $captureNewPath
    $captureRefreshPath.Invoke($captureMoved, $null)
    $captureRefreshPath.Invoke($captureMoved, $null)
    Assert-Startup ((Read-StartupCommand) -ceq ('"' + $captureNewPath + '"')) 'An enabled portable copy did not update its startup path after moving.'
    Write-Output 'PASS: running a moved or upgraded copy updates an existing startup setting.'

    $captureSetEnabled.Invoke($captureMoved, [object[]]@($false))
    $captureSetEnabled.Invoke($captureMoved, [object[]]@($false))
    $captureRefreshPath.Invoke($captureRegistration, $null)
    Assert-Startup (-not $captureEnabledProperty.GetValue($captureRegistration, $null)) 'Disabled startup was re-enabled by an older copy.'
    Assert-Startup ($null -eq (Read-StartupCommand)) 'Disabling must remove the startup entry.'
    $captureKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($captureRunKey)
    try {
        Assert-Startup ($captureKey.GetValue('UnrelatedApplication') -ceq 'keep this value') 'Changing startup modified another application entry.'
    } finally { $captureKey.Dispose() }
    Write-Output 'PASS: disabling is repeatable, survives subsequent launches, and preserves other application entries.'
} finally {
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($captureSandbox, $false)
}
