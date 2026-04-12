[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallMsiPath,

    [string]$UpgradeFromMsiPath,

    [string]$ProductName = "AudioPilot",

    [string]$ManufacturerName = "Hanuwa",

    [string]$ExpectedVersion,

    [string]$InstallRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-MsiInstall {
    param(
        [Parameter(Mandatory = $true)]
        [string]$MsiPath,

        [Parameter(Mandatory = $true)]
        [string]$LogPath
    )

    $process = Start-Process -FilePath "msiexec.exe" -ArgumentList @(
        "/i", $MsiPath,
        "/qn",
        "/norestart",
        "/l*v", $LogPath
    ) -Wait -PassThru -NoNewWindow

    if ($process.ExitCode -ne 0) {
        throw "MSI install failed for '$MsiPath' with exit code $($process.ExitCode). Log: $LogPath"
    }
}

function Invoke-MsiUninstall {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProductCode,

        [Parameter(Mandatory = $true)]
        [string]$LogPath
    )

    $process = Start-Process -FilePath "msiexec.exe" -ArgumentList @(
        "/x", $ProductCode,
        "/qn",
        "/norestart",
        "/l*v", $LogPath
    ) -Wait -PassThru -NoNewWindow

    if ($process.ExitCode -ne 0) {
        throw "MSI uninstall failed for product code '$ProductCode' with exit code $($process.ExitCode). Log: $LogPath"
    }
}

function Get-UninstallEntries {
    param([string]$DisplayName)

    $roots = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )

    $entries = foreach ($root in $roots) {
        Get-ItemProperty -Path $root -ErrorAction SilentlyContinue |
            Where-Object {
                $displayNameProperty = $_.PSObject.Properties["DisplayName"]
                $displayNameProperty -and $displayNameProperty.Value -eq $DisplayName
            }
    }

    return @($entries)
}

function Assert-PathExists {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Expected path was not created: $Path"
    }
}

function Assert-PathMissing {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path) {
        throw "Expected path to be removed: $Path"
    }
}

function Assert-RegistryValueMissing {
    param(
        [string]$Path,
        [string]$Name
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $item = Get-ItemProperty -Path $Path -ErrorAction SilentlyContinue
    if ($null -eq $item) {
        return
    }

    if ($item.PSObject.Properties[$Name]) {
        throw "Expected registry value to be removed: $Path :: $Name"
    }
}

function Get-VersionFromInstallerName {
    param([string]$Path)

    $fileName = [IO.Path]::GetFileNameWithoutExtension($Path)
    if ($fileName -match '^.+-(?<version>\d+\.\d+\.\d+)-(?<arch>x64|arm64)$') {
        return $Matches.version
    }

    return $null
}

$resolvedInstallMsiPath = (Resolve-Path -LiteralPath $InstallMsiPath).Path
$resolvedUpgradeMsiPath = if ([string]::IsNullOrWhiteSpace($UpgradeFromMsiPath)) {
    $null
}
else {
    (Resolve-Path -LiteralPath $UpgradeFromMsiPath).Path
}

if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    $ExpectedVersion = Get-VersionFromInstallerName -Path $resolvedInstallMsiPath
}

$logRoot = Join-Path $PSScriptRoot "..\artifacts\msi-smoke"
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null

$installRoot = if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    Join-Path $env:LOCALAPPDATA $ProductName
}
else {
    $InstallRoot
}
$exePath = Join-Path $installRoot "$ProductName.exe"
$startMenuFolder = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$ProductName"
$startMenuShortcutPath = Join-Path $startMenuFolder "$ProductName.lnk"
$uninstallShortcutPath = Join-Path $startMenuFolder "Uninstall $ProductName.lnk"
$desktopShortcutPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "$ProductName.lnk"
$appDataPath = Join-Path $env:LOCALAPPDATA $ProductName
$manufacturerRegistryPath = "HKCU:\Software\$ManufacturerName\$ProductName"
$runRegistryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

if ($resolvedUpgradeMsiPath) {
    $upgradeInstallLog = Join-Path $logRoot "install-upgrade-baseline.log"
    Invoke-MsiInstall -MsiPath $resolvedUpgradeMsiPath -LogPath $upgradeInstallLog

    $baselineEntries = @(Get-UninstallEntries -DisplayName $ProductName)
    if ($baselineEntries.Count -ne 1) {
        throw "Expected one uninstall entry after baseline install, found $($baselineEntries.Count)."
    }
}

$installLogPath = Join-Path $logRoot "install-current.log"
Invoke-MsiInstall -MsiPath $resolvedInstallMsiPath -LogPath $installLogPath

$entries = @(Get-UninstallEntries -DisplayName $ProductName)
if ($entries.Count -ne 1) {
    throw "Expected one uninstall entry after current install, found $($entries.Count)."
}

$entry = $entries[0]
$displayVersionProperty = $entry.PSObject.Properties["DisplayVersion"]
$installedDisplayVersion = if ($displayVersionProperty) { [string]$displayVersionProperty.Value } else { $null }

if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and $installedDisplayVersion -ne $ExpectedVersion) {
    throw "Installed DisplayVersion '$installedDisplayVersion' did not match expected version '$ExpectedVersion'."
}

Assert-PathExists -Path $exePath
Assert-PathExists -Path $startMenuShortcutPath
Assert-PathExists -Path $uninstallShortcutPath
Assert-PathExists -Path $desktopShortcutPath

New-Item -ItemType Directory -Path $appDataPath -Force | Out-Null
Set-Content -Path (Join-Path $appDataPath "smoke.txt") -Value "cleanup" -Encoding UTF8
New-Item -Path $runRegistryPath -Force | Out-Null
Set-ItemProperty -Path $runRegistryPath -Name $ProductName -Value $exePath -Type String

Assert-PathExists -Path $appDataPath
Assert-PathExists -Path $manufacturerRegistryPath

$productCode = $entry.PSChildName
if ([string]::IsNullOrWhiteSpace($productCode)) {
    throw "Unable to determine installed product code from uninstall entry."
}

$uninstallLogPath = Join-Path $logRoot "uninstall-current.log"
Invoke-MsiUninstall -ProductCode $productCode -LogPath $uninstallLogPath

$remainingEntries = @(Get-UninstallEntries -DisplayName $ProductName)
if ($remainingEntries.Count -ne 0) {
    throw "Expected uninstall entry to be removed after uninstall, found $($remainingEntries.Count)."
}

Assert-PathMissing -Path $exePath
Assert-PathMissing -Path $startMenuShortcutPath
Assert-PathMissing -Path $uninstallShortcutPath
Assert-PathMissing -Path $desktopShortcutPath
Assert-PathMissing -Path $appDataPath
Assert-RegistryValueMissing -Path $runRegistryPath -Name $ProductName

if (Test-Path -LiteralPath $manufacturerRegistryPath) {
    throw "Expected current-user AudioPilot registry key to be removed: $manufacturerRegistryPath"
}

Write-Host "MSI smoke test passed."
