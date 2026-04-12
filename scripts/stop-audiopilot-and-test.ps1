param(
    [string]$Project = "AudioPilot.Tests/AudioPilot.Tests.csproj",
    [string[]]$DotnetTestArgs = @("--nologo", "--filter", "Category!=Integration&Category!=Stress")
)

$ErrorActionPreference = "Stop"

$runningUi = Get-Process -Name "AudioPilot" -ErrorAction SilentlyContinue
if ($runningUi) {
    $runningUi | Stop-Process -Force
    Wait-Process -Name "AudioPilot" -Timeout 5 -ErrorAction SilentlyContinue
}

& dotnet test $Project @DotnetTestArgs
exit $LASTEXITCODE
