[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallMsiPath,

    [string]$UpgradeFromMsiPath,

    [string]$ProductName = "AudioPilot",

    [string]$ManufacturerName = "Hanuwa",

    [string]$ExpectedVersion,

    [string]$InstallRoot,

    [string]$DataRoot,

    [switch]$CleanUninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:msiSmokeMutex = $null
$script:msiSmokeMutexAcquired = $false

function Release-MsiSmokeMutex {
    if ($script:msiSmokeMutexAcquired -and $null -ne $script:msiSmokeMutex) {
        try {
            $script:msiSmokeMutex.ReleaseMutex()
        }
        catch {
        }
    }

    if ($null -ne $script:msiSmokeMutex) {
        $script:msiSmokeMutex.Dispose()
    }

    $script:msiSmokeMutex = $null
    $script:msiSmokeMutexAcquired = $false
}

trap {
    Release-MsiSmokeMutex
    throw $_
}

function Acquire-MsiSmokeMutex {
    $script:msiSmokeMutex = [Threading.Mutex]::new($false, "Local\AudioPilot.MsiSmoke")
    $script:msiSmokeMutexAcquired = $script:msiSmokeMutex.WaitOne([TimeSpan]::FromMinutes(10))
    if (-not $script:msiSmokeMutexAcquired) {
        throw "Timed out waiting for another AudioPilot MSI smoke test to finish."
    }
}

function Invoke-MsiInstall {
    param(
        [Parameter(Mandatory = $true)]
        [string]$MsiPath,

        [Parameter(Mandatory = $true)]
        [string]$LogPath,

        [hashtable]$Properties = @{}
    )

    $arguments = @(
        "/i", $MsiPath,
        "/qn",
        "/norestart",
        "/l*v", $LogPath
    )

    foreach ($key in ($Properties.Keys | Sort-Object)) {
        $arguments += "$key=$($Properties[$key])"
    }

    $process = Start-Process -FilePath "msiexec.exe" -ArgumentList $arguments -Wait -PassThru -NoNewWindow

    if ($process.ExitCode -ne 0) {
        throw "MSI install failed for '$MsiPath' with exit code $($process.ExitCode). Log: $LogPath"
    }
}

function Invoke-MsiUninstall {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProductCode,

        [Parameter(Mandatory = $true)]
        [string]$LogPath,

        [hashtable]$Properties = @{}
    )

    $arguments = @(
        "/x", $ProductCode,
        "/qn",
        "/norestart",
        "/l*v", $LogPath
    )

    foreach ($key in ($Properties.Keys | Sort-Object)) {
        $arguments += "$key=$($Properties[$key])"
    }

    $process = Start-Process -FilePath "msiexec.exe" -ArgumentList $arguments -Wait -PassThru -NoNewWindow

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

function Assert-UninstallEntryDword {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Entry,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [int]$ExpectedValue
    )

    $property = $Entry.PSObject.Properties[$Name]
    if (-not $property) {
        throw "Expected uninstall entry to include '$Name'."
    }

    if ([int]$property.Value -ne $ExpectedValue) {
        throw "Expected uninstall entry '$Name' to be $ExpectedValue, found '$($property.Value)'."
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

$logRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\artifacts\msi-smoke"))
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
Acquire-MsiSmokeMutex

$installRoot = if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    [IO.Path]::GetFullPath((Join-Path $logRoot "install-root"))
}
else {
    [IO.Path]::GetFullPath($InstallRoot)
}
$dataRoot = if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    [IO.Path]::GetFullPath((Join-Path $logRoot "data-root"))
}
else {
    [IO.Path]::GetFullPath($DataRoot)
}
$exePath = Join-Path $installRoot "$ProductName.exe"
$startMenuFolder = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$ProductName"
$startMenuShortcutPath = Join-Path $startMenuFolder "$ProductName.lnk"
$uninstallShortcutPath = Join-Path $startMenuFolder "Change or uninstall $ProductName.lnk"
$cleanUninstallShortcutPath = Join-Path $startMenuFolder "Uninstall $ProductName and delete settings.lnk"
$desktopShortcutPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "$ProductName.lnk"
$userDataPath = $dataRoot
$userDataSentinelPath = Join-Path $userDataPath "smoke-user-data.txt"
$manufacturerRegistryPath = "HKCU:\Software\$ManufacturerName\$ProductName"
$runRegistryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$installProperties = @{
    INSTALLFOLDER = $installRoot
    AUDIOPILOT_DATA_FOLDER = $userDataPath
}
$isUpgradeSmoke = $null -ne $resolvedUpgradeMsiPath

if ($isUpgradeSmoke) {
    $upgradeInstallLog = Join-Path $logRoot "install-upgrade-baseline.log"
    Invoke-MsiInstall -MsiPath $resolvedUpgradeMsiPath -LogPath $upgradeInstallLog -Properties $installProperties

    $baselineEntries = @(Get-UninstallEntries -DisplayName $ProductName)
    if ($baselineEntries.Count -ne 1) {
        throw "Expected one uninstall entry after baseline install, found $($baselineEntries.Count)."
    }

    New-Item -ItemType Directory -Path $userDataPath -Force | Out-Null
    Set-Content -Path $userDataSentinelPath -Value "preserve-upgrade" -Encoding UTF8
}

$installLogPath = Join-Path $logRoot "install-current.log"
Invoke-MsiInstall -MsiPath $resolvedInstallMsiPath -LogPath $installLogPath -Properties $installProperties

$entries = @(Get-UninstallEntries -DisplayName $ProductName)
if ($entries.Count -ne 1) {
    throw "Expected one uninstall entry after current install, found $($entries.Count)."
}

$entry = $entries[0]
Assert-UninstallEntryDword -Entry $entry -Name "NoRepair" -ExpectedValue 1
$displayVersionProperty = $entry.PSObject.Properties["DisplayVersion"]
$installedDisplayVersion = if ($displayVersionProperty) { [string]$displayVersionProperty.Value } else { $null }

if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and $installedDisplayVersion -ne $ExpectedVersion) {
    throw "Installed DisplayVersion '$installedDisplayVersion' did not match expected version '$ExpectedVersion'."
}

Assert-PathExists -Path $exePath
Assert-PathExists -Path $startMenuShortcutPath
Assert-PathExists -Path $uninstallShortcutPath
Assert-PathExists -Path $cleanUninstallShortcutPath
Assert-PathExists -Path $desktopShortcutPath

if ($isUpgradeSmoke) {
    Assert-PathExists -Path $userDataSentinelPath
}
else {
    New-Item -ItemType Directory -Path $userDataPath -Force | Out-Null
    Set-Content -Path $userDataSentinelPath -Value "preserve-uninstall" -Encoding UTF8
}
New-Item -Path $runRegistryPath -Force | Out-Null
Set-ItemProperty -Path $runRegistryPath -Name $ProductName -Value $exePath -Type String

Assert-PathExists -Path $userDataSentinelPath
Assert-PathExists -Path $manufacturerRegistryPath

$productCode = $entry.PSChildName
if ([string]::IsNullOrWhiteSpace($productCode)) {
    throw "Unable to determine installed product code from uninstall entry."
}

$uninstallLogPath = Join-Path $logRoot "uninstall-current.log"
$uninstallProperties = @{}
if ($CleanUninstall) {
    $uninstallProperties["AUDIOPILOT_CLEAN_UNINSTALL"] = "1"
}

Invoke-MsiUninstall -ProductCode $productCode -LogPath $uninstallLogPath -Properties $uninstallProperties

$remainingEntries = @(Get-UninstallEntries -DisplayName $ProductName)
if ($remainingEntries.Count -ne 0) {
    throw "Expected uninstall entry to be removed after uninstall, found $($remainingEntries.Count)."
}

Assert-PathMissing -Path $exePath
Assert-PathMissing -Path $startMenuShortcutPath
Assert-PathMissing -Path $uninstallShortcutPath
Assert-PathMissing -Path $desktopShortcutPath
Assert-PathMissing -Path $cleanUninstallShortcutPath
if ($CleanUninstall) {
    Assert-PathMissing -Path $userDataSentinelPath
}
else {
    Assert-PathExists -Path $userDataSentinelPath
}
Assert-RegistryValueMissing -Path $runRegistryPath -Name $ProductName

if (Test-Path -LiteralPath $manufacturerRegistryPath) {
    throw "Expected current-user AudioPilot registry key to be removed: $manufacturerRegistryPath"
}

if (Test-Path -LiteralPath $userDataSentinelPath) {
    Remove-Item -LiteralPath $userDataSentinelPath -Force
}
if ((Test-Path -LiteralPath $userDataPath -PathType Container) -and -not (Get-ChildItem -LiteralPath $userDataPath -Force)) {
    Remove-Item -LiteralPath $userDataPath -Force
}

Write-Host "MSI smoke test passed."
Release-MsiSmokeMutex
