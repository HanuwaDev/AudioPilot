[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version,
    [string]$Repository = "HanuwaDev/AudioPilot",
    [switch]$Clean,
    [switch]$IncludeDebugInstallers,
    [switch]$SuppressMsiValidation,
    [switch]$SkipPackage,
    [switch]$SkipIntegrityValidation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$installerProject = Join-Path $repoRoot "AudioPilot.Installer/AudioPilot.Installer.wixproj"

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Step,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Host "=== $Step ==="
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE"
    }
}

if (-not (Test-Path -LiteralPath $installerProject)) {
    throw "Installer project not found: $installerProject"
}

& (Join-Path $PSScriptRoot "publish-release-profiles.ps1") -Configuration $Configuration

Invoke-DotNet -Step "restore MSI installer project" -Arguments @(
    "restore",
    $installerProject,
    "--locked-mode",
    "--nologo"
)

$installerConfigurations = @($Configuration)
if ($IncludeDebugInstallers -and $Configuration -ne "Debug") {
    $installerConfigurations = @("Debug") + $installerConfigurations
}

foreach ($installerConfiguration in $installerConfigurations) {
    foreach ($platform in @("x64", "arm64")) {
        $arguments = @(
            "build",
            $installerProject,
            "-c",
            $installerConfiguration,
            "-p:Platform=$platform",
            "--no-restore",
            "--nologo"
        )

        if ($SuppressMsiValidation) {
            $arguments += "-p:SuppressValidation=true"
        }

        Invoke-DotNet -Step "build MSI $platform $installerConfiguration" -Arguments $arguments
    }
}

if (-not $SkipPackage) {
    $packageArguments = @{
        Repository = $Repository
    }

    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        $packageArguments.Version = $Version
    }

    if ($Clean) {
        $packageArguments.Clean = $true
    }

    Write-Host "=== package release artifacts ==="
    & (Join-Path $PSScriptRoot "package-release.ps1") @packageArguments
}

if (-not $SkipIntegrityValidation) {
    Write-Host "=== validate release integrity ==="
    & (Join-Path $PSScriptRoot "validate-release-integrity.ps1") -ReleaseRoot "artifacts/release"
}

Write-Host ""
Write-Host "Local release artifact build completed."
