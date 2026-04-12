[CmdletBinding()]
param(
    [string]$ChecksumsPath = "artifacts/release/SHA256SUMS.txt"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ChecksumsPath)) {
    throw "Checksums file not found: $ChecksumsPath"
}

$lines = Get-Content -LiteralPath $ChecksumsPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
if ($lines.Count -eq 0) {
    throw "No checksum entries found in: $ChecksumsPath"
}

$rows = foreach ($line in $lines) {
    if ($line -match '^(?<hash>[a-fA-F0-9]{64})\s+\*(?<file>.+)$') {
        [pscustomobject]@{
            File = $matches.file.Trim()
            Hash = $matches.hash.ToLowerInvariant()
        }
    }
}

if ($null -eq $rows -or $rows.Count -eq 0) {
    throw "Could not parse checksum lines from: $ChecksumsPath"
}

Write-Output "| Artifact | SHA256 |"
Write-Output "|---|---|"
foreach ($row in $rows) {
    Write-Output "| $($row.File) | $($row.Hash) |"
}
