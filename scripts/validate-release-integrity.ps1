[CmdletBinding()]
param(
    [string]$ReleaseRoot = "artifacts/release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$releaseRootPath = Join-Path $repoRoot $ReleaseRoot

if (-not (Test-Path $releaseRootPath)) {
    throw "Release root not found: $releaseRootPath"
}

$manifestPath = Join-Path $releaseRootPath "release-manifest.json"
$checksumsPath = Join-Path $releaseRootPath "SHA256SUMS.txt"

if (-not (Test-Path $manifestPath)) {
    throw "Release manifest not found: $manifestPath"
}

if (-not (Test-Path $checksumsPath)) {
    throw "Checksums file not found: $checksumsPath"
}

[object]$manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
if ($null -eq $manifest.packages -or $manifest.packages.Count -eq 0) {
    throw "No packages listed in manifest: $manifestPath"
}

$checksumMap = @{}
$checksumLines = Get-Content -Path $checksumsPath
foreach ($line in $checksumLines) {
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    if ($line -notmatch '^([a-fA-F0-9]{64})\s+\*(.+)$') {
        throw "Invalid checksum line format: '$line'"
    }

    $hash = $Matches[1].ToLowerInvariant()
    $fileName = $Matches[2].Trim()
    $checksumMap[$fileName] = $hash
}

if ($checksumMap.Count -eq 0) {
    throw "No checksum entries found in: $checksumsPath"
}

$manifestPackageCount = [int]$manifest.packageCount
if ($manifestPackageCount -ne $manifest.packages.Count) {
    throw "Manifest packageCount mismatch: packageCount=$manifestPackageCount actual=$($manifest.packages.Count)"
}

$installerCount = 0
if ($null -ne $manifest.installers) {
    $installerCount = $manifest.installers.Count
}

$wingetManifestCount = 0
if ($null -ne $manifest.wingetManifests) {
    $wingetManifestCount = $manifest.wingetManifests.Count
}

if ([int]$manifest.installerCount -ne $installerCount) {
    throw "Manifest installerCount mismatch: installerCount=$($manifest.installerCount) actual=$installerCount"
}

if ([int]$manifest.wingetManifestCount -ne $wingetManifestCount) {
    throw "Manifest wingetManifestCount mismatch: wingetManifestCount=$($manifest.wingetManifestCount) actual=$wingetManifestCount"
}

$expectedChecksumCount = $manifest.packages.Count + $installerCount + $wingetManifestCount
if ($checksumMap.Count -ne $expectedChecksumCount) {
    throw "Checksum entry count mismatch: checksums=$($checksumMap.Count) manifestArtifacts=$expectedChecksumCount"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($package in $manifest.packages) {
    $fileName = [string]$package.packageFile
    if ([string]::IsNullOrWhiteSpace($fileName)) {
        throw "Manifest contains a package entry with empty packageFile."
    }

    $zipPath = Join-Path $releaseRootPath $fileName
    if (-not (Test-Path $zipPath)) {
        throw "Manifest package file missing: $zipPath"
    }

    if (-not $checksumMap.ContainsKey($fileName)) {
        throw "No checksum entry found for manifest package: $fileName"
    }

    $actualHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedFromChecksums = [string]$checksumMap[$fileName]
    $expectedFromManifest = ([string]$package.sha256).ToLowerInvariant()

    if ($actualHash -ne $expectedFromChecksums) {
        throw "Checksum mismatch against SHA256SUMS for $fileName"
    }

    if ($actualHash -ne $expectedFromManifest) {
        throw "Checksum mismatch against manifest for $fileName"
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entryNames = $archive.Entries | ForEach-Object { $_.FullName }
        $hasUiExe = $false
        $hasCliExe = $false
        $hasAudioPilotRoot = $false

        foreach ($entryName in $entryNames) {
            if ($entryName -match '^AudioPilot/$') {
                $hasAudioPilotRoot = $true
            }

            if ($entryName -notmatch '^AudioPilot/') {
                throw "Release package contains files outside the AudioPilot root folder: $fileName -> $entryName"
            }

            $hasAudioPilotRoot = $true

            if ($entryName -match '^AudioPilot/AudioPilot\.exe$') {
                $hasUiExe = $true
            }
            if ($entryName -match '^AudioPilot/AudioPilot\.Cli\.exe$') {
                $hasCliExe = $true
            }
        }

        if (-not $hasAudioPilotRoot) {
            throw "Release package is missing the AudioPilot root folder: $fileName"
        }

        if (-not $hasUiExe) {
            throw "Release package is missing AudioPilot.exe: $fileName"
        }

        if (-not $hasCliExe) {
            throw "Release package is missing AudioPilot.Cli.exe: $fileName"
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-ArtifactChecksum {
    param(
        [string]$FileName,
        [string]$ExpectedHash
    )

    $path = Join-Path $releaseRootPath $FileName
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Manifest artifact missing: $path"
    }

    if (-not $checksumMap.ContainsKey($FileName)) {
        throw "No checksum entry found for artifact: $FileName"
    }

    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedFromChecksums = [string]$checksumMap[$FileName]
    $expectedFromManifest = $ExpectedHash.ToLowerInvariant()

    if ($actualHash -ne $expectedFromChecksums) {
        throw "Checksum mismatch against SHA256SUMS for $FileName"
    }

    if ($actualHash -ne $expectedFromManifest) {
        throw "Checksum mismatch against manifest for $FileName"
    }

    return $path
}

foreach ($installer in @($manifest.installers)) {
    $fileName = [string]$installer.installerFile
    if ([string]::IsNullOrWhiteSpace($fileName)) {
        throw "Manifest contains an installer entry with empty installerFile."
    }

    $installerPath = Assert-ArtifactChecksum -FileName $fileName -ExpectedHash ([string]$installer.sha256)
    if ([IO.Path]::GetExtension($installerPath) -ne ".msi") {
        throw "Installer artifact does not have an .msi extension: $fileName"
    }
}

foreach ($wingetManifest in @($manifest.wingetManifests)) {
    $relativePath = [string]$wingetManifest.relativePath
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        throw "Manifest contains a winget manifest entry with empty relativePath."
    }

    $normalizedRelativePath = $relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar)
    $artifactPath = Join-Path $releaseRootPath $normalizedRelativePath
    $fileName = [IO.Path]::GetFileName($artifactPath)
    if ([IO.Path]::GetExtension($artifactPath) -ne ".yaml") {
        throw "Winget manifest artifact does not have a .yaml extension: $relativePath"
    }

    if (-not (Test-Path -LiteralPath $artifactPath)) {
        throw "Manifest winget file missing: $artifactPath"
    }

    $actualHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $checksumMap.ContainsKey($fileName)) {
        throw "No checksum entry found for winget manifest: $fileName"
    }

    if ($actualHash -ne ([string]$checksumMap[$fileName])) {
        throw "Checksum mismatch against SHA256SUMS for winget manifest: $relativePath"
    }

    if ($actualHash -ne ([string]$wingetManifest.sha256).ToLowerInvariant()) {
        throw "Checksum mismatch against manifest for winget manifest: $relativePath"
    }
}

Write-Host "Release integrity validation passed."
Write-Host "Validated $expectedChecksumCount artifacts in $releaseRootPath"
