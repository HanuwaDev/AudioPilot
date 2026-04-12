[CmdletBinding()]
param(
    [string]$PublishRoot = "artifacts/publish",
    [string]$OutputRoot = "artifacts/release",
    [string]$InstallerRoot = "AudioPilot.Installer/bin",
    [string]$Version,
    [string]$Repository = "HanuwaDev/AudioPilot",
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-Version {
    param([string]$ExplicitVersion)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitVersion)) {
        return $ExplicitVersion.Trim()
    }

    $versionPropsPath = Join-Path $PSScriptRoot "../Version.props"
    if (Test-Path $versionPropsPath) {
        try {
            [xml]$projXml = Get-Content -Path $versionPropsPath -Raw
            $fromVersion = $projXml.Project.PropertyGroup.AudioPilotVersion | Select-Object -First 1
            if (-not [string]::IsNullOrWhiteSpace($fromVersion)) {
                return $fromVersion.Trim()
            }
        }
        catch {
        }
    }

    return (Get-Date).ToString("yyyy.MM.dd-HHmm")
}

function Add-ArtifactRecord {
    param(
        [System.Collections.Generic.List[object]]$List,
        [System.Collections.Generic.List[string]]$Checksums,
        [string]$Path,
        [hashtable]$Metadata
    )

    $item = Get-Item -LiteralPath $Path
    $hash = Get-FileHash -LiteralPath $Path -Algorithm SHA256

    $record = [ordered]@{
        file = $item.Name
        sizeBytes = $item.Length
        sha256 = $hash.Hash.ToLowerInvariant()
    }

    foreach ($key in $Metadata.Keys) {
        $record[$key] = $Metadata[$key]
    }

    $List.Add([pscustomobject]$record)
    $Checksums.Add("$($hash.Hash.ToLowerInvariant()) *$($item.Name)")
}

function Get-RelativePathSafe {
    param(
        [string]$BasePath,
        [string]$TargetPath
    )

    $baseUri = [Uri]((Resolve-Path -LiteralPath $BasePath).Path.TrimEnd('\') + '\')
    $targetUri = [Uri](Resolve-Path -LiteralPath $TargetPath).Path
    return $baseUri.MakeRelativeUri($targetUri).ToString()
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishRootPath = Join-Path $repoRoot $PublishRoot
$outputRootPath = Join-Path $repoRoot $OutputRoot
$installerRootPath = Join-Path $repoRoot $InstallerRoot
$resolvedVersion = Resolve-Version -ExplicitVersion $Version

if (-not (Test-Path $publishRootPath)) {
    throw "Publish root not found: $publishRootPath"
}

if ($Clean -and (Test-Path $outputRootPath)) {
    Remove-Item -Path $outputRootPath -Recurse -Force
}

New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null

$targets = @(
    @{ Profile = "FrameworkDependent-win-arm64"; Mode = "FrameworkDependent"; Rid = "win-arm64"; Source = "FrameworkDependent/win-arm64" },
    @{ Profile = "FrameworkDependent-win-x64";   Mode = "FrameworkDependent"; Rid = "win-x64";   Source = "FrameworkDependent/win-x64" },
    @{ Profile = "FrameworkDependent-win-x86";   Mode = "FrameworkDependent"; Rid = "win-x86";   Source = "FrameworkDependent/win-x86" },
    @{ Profile = "SelfContained-win-arm64";      Mode = "SelfContained";      Rid = "win-arm64"; Source = "SelfContained/win-arm64" },
    @{ Profile = "SelfContained-win-x64";        Mode = "SelfContained";      Rid = "win-x64";   Source = "SelfContained/win-x64" },
    @{ Profile = "SelfContained-win-x86";        Mode = "SelfContained";      Rid = "win-x86";   Source = "SelfContained/win-x86" }
)

$packages = New-Object System.Collections.Generic.List[object]
$installers = New-Object System.Collections.Generic.List[object]
$wingetManifests = New-Object System.Collections.Generic.List[object]
$checksumLines = New-Object System.Collections.Generic.List[string]

foreach ($target in $targets) {
    $sourceDir = Join-Path $publishRootPath $target.Source
    if (-not (Test-Path $sourceDir)) {
        throw "Expected publish output missing for profile '$($target.Profile)': $sourceDir"
    }

    $cliExePath = Join-Path $sourceDir "AudioPilot.Cli.exe"
    if (-not (Test-Path $cliExePath)) {
        throw "Expected CLI host executable missing for profile '$($target.Profile)': $cliExePath"
    }

    $zipFileName = "AudioPilot-$resolvedVersion-$($target.Profile).zip"
    $zipPath = Join-Path $outputRootPath $zipFileName
    $stagingRoot = Join-Path $outputRootPath ("tmp-" + [guid]::NewGuid().ToString("N"))
    $archiveRoot = Join-Path $stagingRoot "AudioPilot"

    if (Test-Path $zipPath) {
        Remove-Item -Path $zipPath -Force
    }

    try {
        New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
        Copy-Item -Path (Join-Path $sourceDir "*") -Destination $archiveRoot -Recurse -Force
        Compress-Archive -Path $archiveRoot -DestinationPath $zipPath -CompressionLevel Optimal
    }
    finally {
        if (Test-Path $stagingRoot) {
            Remove-Item -Path $stagingRoot -Recurse -Force
        }
    }

    $packageRecord = @{
        profile = $target.Profile
        mode = $target.Mode
        runtimeIdentifier = $target.Rid
        packageFile = [IO.Path]::GetFileName($zipPath)
        cliHostExecutable = "AudioPilot.Cli.exe"
    }

    Add-ArtifactRecord -List $packages -Checksums $checksumLines -Path $zipPath -Metadata $packageRecord

    Write-Host "Packaged $($target.Profile) -> $([IO.Path]::GetFileName($zipPath))"
}

$installerTargets = @(
    @{ Architecture = "x64"; Source = "x64/Release/AudioPilot-$resolvedVersion-x64.msi" },
    @{ Architecture = "arm64"; Source = "arm64/Release/AudioPilot-$resolvedVersion-arm64.msi" }
)

foreach ($target in $installerTargets) {
    $sourcePath = Join-Path $installerRootPath $target.Source
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "Expected MSI output missing for architecture '$($target.Architecture)': $sourcePath"
    }

    $destinationPath = Join-Path $outputRootPath ([IO.Path]::GetFileName($sourcePath))
    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force

    Add-ArtifactRecord -List $installers -Checksums $checksumLines -Path $destinationPath -Metadata @{
        architecture = $target.Architecture
        installerFile = [IO.Path]::GetFileName($destinationPath)
        installerType = "msi"
    }

    Write-Host "Staged installer $($target.Architecture) -> $([IO.Path]::GetFileName($destinationPath))"
}

$repositoryParts = $Repository.Split("/", 2, [System.StringSplitOptions]::RemoveEmptyEntries)
if ($repositoryParts.Count -ne 2) {
    throw "Repository must be in 'owner/name' format. Actual value: $Repository"
}

$wingetOutputRoot = Join-Path $outputRootPath "winget"
$wingetScriptPath = Join-Path $repoRoot "packaging/winget/generate-winget-manifest.ps1"
if (-not (Test-Path -LiteralPath $wingetScriptPath)) {
    throw "Winget manifest generator not found: $wingetScriptPath"
}

& $wingetScriptPath `
    -Version $resolvedVersion `
    -RepositoryOwner $repositoryParts[0] `
    -RepositoryName $repositoryParts[1] `
    -OutputRoot $wingetOutputRoot `
    -X64InstallerPath (Join-Path $outputRootPath "AudioPilot-$resolvedVersion-x64.msi") `
    -Arm64InstallerPath (Join-Path $outputRootPath "AudioPilot-$resolvedVersion-arm64.msi")

$generatedWingetFiles = Get-ChildItem -Path $wingetOutputRoot -Recurse -File -Filter "*.yaml" | Sort-Object FullName
if ($generatedWingetFiles.Count -eq 0) {
    throw "No winget manifest files were generated in: $wingetOutputRoot"
}

foreach ($file in $generatedWingetFiles) {
    Add-ArtifactRecord -List $wingetManifests -Checksums $checksumLines -Path $file.FullName -Metadata @{
        manifestFile = $file.Name
        relativePath = Get-RelativePathSafe -BasePath $outputRootPath -TargetPath $file.FullName
        artifactType = "winget-manifest"
    }
}

$manifest = [ordered]@{
    app = "AudioPilot"
    version = $resolvedVersion
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    publishRoot = $PublishRoot
    outputRoot = $OutputRoot
    installerRoot = $InstallerRoot
    packageCount = $packages.Count
    installerCount = $installers.Count
    wingetManifestCount = $wingetManifests.Count
    packages = $packages
    installers = $installers
    wingetManifests = $wingetManifests
}

$manifestPath = Join-Path $outputRootPath "release-manifest.json"
$checksumsPath = Join-Path $outputRootPath "SHA256SUMS.txt"

$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $manifestPath -Encoding UTF8
$checksumLines | Set-Content -Path $checksumsPath -Encoding UTF8

Write-Host ""
Write-Host "Release packaging complete."
Write-Host "Manifest:  $manifestPath"
Write-Host "Checksums: $checksumsPath"
Write-Host "Packages:  $($packages.Count)"
