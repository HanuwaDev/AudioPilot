[CmdletBinding()]
param(
    [string]$Project = "AudioPilot/AudioPilot.csproj",
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$ArtifactsRoot = "artifacts/benchmarks/readytorun"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $Project)) {
    throw "Project not found: $Project"
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$artifactsPath = Join-Path $repoRoot $ArtifactsRoot
$tempLockFile = Join-Path $artifactsPath "benchmark.packages.lock.json"
$lockFileSnapshots = @{}

Get-ChildItem -Path $repoRoot -Recurse -File -Filter "packages.lock.json" | ForEach-Object {
    $lockFileSnapshots[$_.FullName] = [System.IO.File]::ReadAllText($_.FullName)
}

try {
    if (Test-Path $artifactsPath) {
        Remove-Item -Path $artifactsPath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $artifactsPath -Force | Out-Null

    & dotnet restore $Project -r $RuntimeIdentifier --force-evaluate --lock-file-path $tempLockFile --nologo -p:SelfContained=true -p:PublishReadyToRun=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed for runtime '$RuntimeIdentifier'."
    }

    $variants = @(
        @{ Name = "r2r-off"; PublishReadyToRun = "false" },
        @{ Name = "r2r-on"; PublishReadyToRun = "true" }
    )

    $results = New-Object System.Collections.Generic.List[object]

    foreach ($variant in $variants) {
        $publishDir = Join-Path $artifactsPath $variant.Name

        $publishArgs = @(
            "publish",
            $Project,
            "-c",
            $Configuration,
            "-r",
            $RuntimeIdentifier,
            "--self-contained",
            "true",
            "--no-restore",
            "--nologo",
            "-p:PublishSingleFile=false",
            "-p:PublishReadyToRun=$($variant.PublishReadyToRun)",
            "-p:PublishDir=$publishDir"
        )

        & dotnet @publishArgs

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for variant '$($variant.Name)'."
        }

        $sizeBytes = (Get-ChildItem -Path $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
        $results.Add([PSCustomObject]@{
            variant = $variant.Name
            publishReadyToRun = [bool]::Parse($variant.PublishReadyToRun)
            runtimeIdentifier = $RuntimeIdentifier
            sizeBytes = [int64]$sizeBytes
        })
    }

    $r2rOff = $results | Where-Object variant -eq "r2r-off"
    $r2rOn = $results | Where-Object variant -eq "r2r-on"
    $deltaBytes = [int64]($r2rOn.sizeBytes - $r2rOff.sizeBytes)
    $deltaPercent = if ($r2rOff.sizeBytes -gt 0) {
        [Math]::Round(($deltaBytes / $r2rOff.sizeBytes) * 100, 2)
    }
    else {
        0
    }

    $summary = [PSCustomObject]@{
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        project = $Project
        configuration = $Configuration
        runtimeIdentifier = $RuntimeIdentifier
        baseline = $r2rOff
        readyToRun = $r2rOn
        deltaBytes = $deltaBytes
        deltaPercent = $deltaPercent
    }

    $summaryPath = Join-Path $artifactsPath "summary.json"
    $markdownPath = Join-Path $artifactsPath "summary.md"

    $summary | ConvertTo-Json -Depth 6 | Set-Content -Path $summaryPath -Encoding UTF8

    $markdown = @(
        "# ReadyToRun Publish Size Benchmark",
        "",
        "- Runtime: $RuntimeIdentifier",
        "- Configuration: $Configuration",
        "- Baseline (`PublishReadyToRun=false`): $($r2rOff.sizeBytes) bytes",
        "- ReadyToRun (`PublishReadyToRun=true`): $($r2rOn.sizeBytes) bytes",
        "- Delta: $deltaBytes bytes ($deltaPercent%)"
    ) -join [Environment]::NewLine

    $markdown | Set-Content -Path $markdownPath -Encoding UTF8
    Write-Host $markdown

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $markdown
    }
}
finally {
    foreach ($path in $lockFileSnapshots.Keys) {
        [System.IO.File]::WriteAllText($path, $lockFileSnapshots[$path])
    }
}
