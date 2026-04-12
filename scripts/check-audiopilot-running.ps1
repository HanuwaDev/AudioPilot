$ErrorActionPreference = "Stop"

$runningUi = Get-Process -Name "AudioPilot" -ErrorAction SilentlyContinue
if ($runningUi) {
    Write-Host "AudioPilot is running."
    exit 1
}

Write-Host "AudioPilot is not running."
exit 0