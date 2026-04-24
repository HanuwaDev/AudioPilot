param(
    [ValidateSet("unit", "integration", "visual", "stress", "full")]
    [string]$Category = "unit",
    [string]$Project = "AudioPilot.Tests/AudioPilot.Tests.csproj",
    [string[]]$DotnetTestArgs = @("--nologo"),
    [switch]$Coverage,
    [switch]$KeepRunningUi,
    [switch]$Parallel,
    [switch]$ShowLogs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $Project)) {
    throw "Test project not found: $Project"
}

function Set-TestCategoryEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SelectedCategory,
        [switch]$ShowLogs
    )

    $disableLogging = if ($ShowLogs) { $null } else { "1" }

    switch ($SelectedCategory) {
        "unit" {
            if ($disableLogging) { $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING = $disableLogging }
            Remove-Item Env:AUDIOPILOT_RUN_INTEGRATION -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_VISUAL_WPF -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_TEST_SHOW_WINDOWS -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_STRESS -ErrorAction SilentlyContinue
        }
        "integration" {
            if ($disableLogging) { $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING = $disableLogging }
            $env:AUDIOPILOT_RUN_INTEGRATION = "1"
            Remove-Item Env:AUDIOPILOT_RUN_VISUAL_WPF -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_TEST_SHOW_WINDOWS -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_STRESS -ErrorAction SilentlyContinue
        }
        "visual" {
            if ($disableLogging) { $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING = $disableLogging }
            $env:AUDIOPILOT_RUN_VISUAL_WPF = "1"
            $env:AUDIOPILOT_TEST_SHOW_WINDOWS = "1"
            Remove-Item Env:AUDIOPILOT_RUN_INTEGRATION -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_STRESS -ErrorAction SilentlyContinue
        }
        "stress" {
            if ($disableLogging) { $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING = $disableLogging }
            Remove-Item Env:AUDIOPILOT_RUN_INTEGRATION -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_VISUAL_WPF -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_TEST_SHOW_WINDOWS -ErrorAction SilentlyContinue
            $env:AUDIOPILOT_RUN_STRESS = "1"
        }
        "full" {
            if ($disableLogging) { $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING = $disableLogging }
            $env:AUDIOPILOT_RUN_INTEGRATION = "1"
            Remove-Item Env:AUDIOPILOT_RUN_VISUAL_WPF -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_TEST_SHOW_WINDOWS -ErrorAction SilentlyContinue
            $env:AUDIOPILOT_RUN_STRESS = "1"
        }
    }
}

function New-DotnetTestArguments {
    param(
        [string]$CoverageResultsDirectory,
        [string]$TestFilter
    )

    $dotnetArgs = @($Project)
    $dotnetArgs += $DotnetTestArgs

    if (-not [string]::IsNullOrWhiteSpace($TestFilter)) {
        $dotnetArgs += "--filter"
        $dotnetArgs += $TestFilter
    }

    if ($CoverageResultsDirectory) {
        New-Item -ItemType Directory -Path $CoverageResultsDirectory -Force | Out-Null
        $dotnetArgs += "--collect:Code Coverage;Format=Cobertura"
        $dotnetArgs += "--results-directory"
        $dotnetArgs += $CoverageResultsDirectory
    }

    return $dotnetArgs
}

$originalIntegration = $env:AUDIOPILOT_RUN_INTEGRATION
$originalVisualWpf = $env:AUDIOPILOT_RUN_VISUAL_WPF
$originalShowWindows = $env:AUDIOPILOT_TEST_SHOW_WINDOWS
$originalStress = $env:AUDIOPILOT_RUN_STRESS

try {
    Set-TestCategoryEnvironment -SelectedCategory $Category -ShowLogs:$ShowLogs

    $testFilter = switch ($Category) {
        "unit" { 'Category!=Integration&Category!=Stress' }
        "integration" { 'Category=Integration' }
        "visual" { 'Category=VisualWpf' }
        "stress" { 'Category=Stress' }
        default { $null }
    }

    if (-not $KeepRunningUi -and -not $env:AUDIOPILOT_TEST_ALLOW_RUNNING_UI) {
        $runningUi = Get-Process -Name "AudioPilot" -ErrorAction SilentlyContinue
        if ($runningUi) {
            $runningUi | Stop-Process -Force
            Wait-Process -Name "AudioPilot" -Timeout 5 -ErrorAction SilentlyContinue
        }
    }

    $resultsDirectory = if ($Coverage) { "artifacts/testresults/coverage" } else { $null }
    $dotnetArgs = New-DotnetTestArguments -CoverageResultsDirectory $resultsDirectory -TestFilter $testFilter

    if ($Parallel -and -not $Coverage) {
        & dotnet test @dotnetArgs --parallel
    }
    else {
        & dotnet test @dotnetArgs
    }
    exit $LASTEXITCODE
}
finally {
    if ($null -eq $originalIntegration) {
        Remove-Item Env:AUDIOPILOT_RUN_INTEGRATION -ErrorAction SilentlyContinue
    }
    else {
        $env:AUDIOPILOT_RUN_INTEGRATION = $originalIntegration
    }

    if ($null -eq $originalVisualWpf) {
        Remove-Item Env:AUDIOPILOT_RUN_VISUAL_WPF -ErrorAction SilentlyContinue
    }
    else {
        $env:AUDIOPILOT_RUN_VISUAL_WPF = $originalVisualWpf
    }

    if ($null -eq $originalShowWindows) {
        Remove-Item Env:AUDIOPILOT_TEST_SHOW_WINDOWS -ErrorAction SilentlyContinue
    }
    else {
        $env:AUDIOPILOT_TEST_SHOW_WINDOWS = $originalShowWindows
    }

    if ($null -eq $originalStress) {
        Remove-Item Env:AUDIOPILOT_RUN_STRESS -ErrorAction SilentlyContinue
    }
    else {
        $env:AUDIOPILOT_RUN_STRESS = $originalStress
    }
}
