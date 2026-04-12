[CmdletBinding()]
param(
    [string]$Version,
    [string]$PackageIdentifier = "HanuwaDev.AudioPilot",
    [string]$Publisher = "Hanuwa",
    [string]$PackageName = "AudioPilot",
    [string]$RepositoryOwner = "HanuwaDev",
    [string]$RepositoryName = "AudioPilot",
    [string]$ManifestVersion = "1.10.0",
    [string]$OutputRoot,
    [string]$X64InstallerPath,
    [string]$Arm64InstallerPath
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
}

function Get-VersionFromProps {
    param([string]$RepoRoot)

    $versionPropsPath = Join-Path $RepoRoot "Version.props"
    [xml]$versionProps = Get-Content -Path $versionPropsPath
    return $versionProps.Project.PropertyGroup.AudioPilotVersion
}

function Get-MsiProperty {
    param(
        [string]$Path,
        [string]$PropertyName
    )

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember("OpenDatabase", "InvokeMethod", $null, $installer, @($Path, 0))
    $query = "SELECT `Value` FROM `Property` WHERE `Property`='$PropertyName'"
    $view = $database.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $database, @($query))
    $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null) | Out-Null
    $record = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
    if ($null -eq $record) {
        throw "MSI property '$PropertyName' was not found in '$Path'."
    }

    return $record.StringData(1)
}

function Ensure-Directory {
    param([string]$Path)

    if (-not (Test-Path -Path $Path)) {
        New-Item -ItemType Directory -Path $Path | Out-Null
    }
}

$repoRoot = Get-RepoRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $PSScriptRoot "manifests"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-VersionFromProps -RepoRoot $repoRoot
}

if ([string]::IsNullOrWhiteSpace($X64InstallerPath)) {
    $X64InstallerPath = Join-Path $repoRoot "AudioPilot.Installer\bin\x64\Release\AudioPilot-$Version-x64.msi"
}

if ([string]::IsNullOrWhiteSpace($Arm64InstallerPath)) {
    $Arm64InstallerPath = Join-Path $repoRoot "AudioPilot.Installer\bin\arm64\Release\AudioPilot-$Version-arm64.msi"
}

if (-not (Test-Path -Path $X64InstallerPath)) {
    throw "x64 installer not found: $X64InstallerPath"
}

if (-not (Test-Path -Path $Arm64InstallerPath)) {
    throw "arm64 installer not found: $Arm64InstallerPath"
}

$x64Hash = (Get-FileHash -Path $X64InstallerPath -Algorithm SHA256).Hash.ToUpperInvariant()
$arm64Hash = (Get-FileHash -Path $Arm64InstallerPath -Algorithm SHA256).Hash.ToUpperInvariant()
$x64ProductCode = Get-MsiProperty -Path $X64InstallerPath -PropertyName "ProductCode"
$arm64ProductCode = Get-MsiProperty -Path $Arm64InstallerPath -PropertyName "ProductCode"
$upgradeCode = Get-MsiProperty -Path $X64InstallerPath -PropertyName "UpgradeCode"

$releaseTag = "v$Version"
$releaseBaseUrl = "https://github.com/$RepositoryOwner/$RepositoryName/releases/download/$releaseTag"
$packageSegments = $PackageIdentifier.Split(".")
$manifestDirectory = Join-Path $OutputRoot $packageSegments[0].Substring(0, 1).ToLowerInvariant()
foreach ($segment in $packageSegments) {
    $manifestDirectory = Join-Path $manifestDirectory $segment
}

$manifestDirectory = Join-Path $manifestDirectory $Version
Ensure-Directory -Path $manifestDirectory

$versionManifestPath = Join-Path $manifestDirectory "$PackageIdentifier.yaml"
$installerManifestPath = Join-Path $manifestDirectory "$PackageIdentifier.installer.yaml"
$localeManifestPath = Join-Path $manifestDirectory "$PackageIdentifier.locale.en-US.yaml"

$versionManifest = @"
PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $ManifestVersion
"@

$installerManifest = @"
PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
InstallerType: msi
Scope: user
UpgradeBehavior: install
Installers:
- Architecture: x64
  InstallerUrl: $releaseBaseUrl/AudioPilot-$Version-x64.msi
  InstallerSha256: $x64Hash
  ProductCode: $x64ProductCode
  AppsAndFeaturesEntries:
  - DisplayName: $PackageName
    DisplayVersion: $Version
    Publisher: $Publisher
    ProductCode: $x64ProductCode
    UpgradeCode: $upgradeCode
- Architecture: arm64
  InstallerUrl: $releaseBaseUrl/AudioPilot-$Version-arm64.msi
  InstallerSha256: $arm64Hash
  ProductCode: $arm64ProductCode
  AppsAndFeaturesEntries:
  - DisplayName: $PackageName
    DisplayVersion: $Version
    Publisher: $Publisher
    ProductCode: $arm64ProductCode
    UpgradeCode: $upgradeCode
ManifestType: installer
ManifestVersion: $ManifestVersion
"@

$localeManifest = @"
PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
PackageLocale: en-US
Publisher: $Publisher
PublisherUrl: https://github.com/$RepositoryOwner
PublisherSupportUrl: https://github.com/$RepositoryOwner/$RepositoryName/issues
PackageName: $PackageName
PackageUrl: https://github.com/$RepositoryOwner/$RepositoryName
License: MIT
LicenseUrl: https://github.com/$RepositoryOwner/$RepositoryName/blob/main/LICENSE
Copyright: Copyright (c) 2026 Hanuwa
ShortDescription: Audio switcher with hotkeys, mixers, routines, and profiles for Windows.
Moniker: audiopilot
Tags:
- audio
- hotkeys
- mixer
- profiles
- routines
- sound
ManifestType: defaultLocale
ManifestVersion: $ManifestVersion
"@

Set-Content -Path $versionManifestPath -Value $versionManifest -NoNewline
Set-Content -Path $installerManifestPath -Value $installerManifest -NoNewline
Set-Content -Path $localeManifestPath -Value $localeManifest -NoNewline

Write-Host "Generated winget manifests in $manifestDirectory"
