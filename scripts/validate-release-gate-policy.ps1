param(
    [string]$WorkflowPath = ".github/workflows/release-artifacts.yml"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $WorkflowPath)) {
    throw "Workflow file not found: $WorkflowPath"
}

$content = Get-Content -Raw -Path $WorkflowPath

function Assert-Pattern {
    param(
        [string]$Pattern,
        [string]$Description
    )

    if (-not [System.Text.RegularExpressions.Regex]::IsMatch($content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "Release gate workflow policy validation failed: $Description"
    }
}

Assert-Pattern -Pattern 'on:\s*(?:.|\r|\n)*workflow_dispatch:' -Description 'workflow_dispatch trigger is required.'
Assert-Pattern -Pattern 'on:\s*(?:.|\r|\n)*push:\s*(?:.|\r|\n)*tags:\s*(?:.|\r|\n)*-\s*"v\*"' -Description 'tag trigger (v*) is required.'
Assert-Pattern -Pattern 'jobs:\s*(?:.|\r|\n)*release-gate-unit-tests:' -Description 'release-gate-unit-tests job is required.'
Assert-Pattern -Pattern 'jobs:\s*(?:.|\r|\n)*release-gate-integration-tests:' -Description 'release-gate-integration-tests job is required.'
Assert-Pattern -Pattern 'jobs:\s*(?:.|\r|\n)*release-gate-stress-tests:' -Description 'release-gate-stress-tests job is required.'
Assert-Pattern -Pattern 'jobs:\s*(?:.|\r|\n)*publish-profiles:' -Description 'publish-profiles job is required.'
Assert-Pattern -Pattern 'jobs:\s*(?:.|\r|\n)*build-msi-installers:' -Description 'build-msi-installers job is required.'
Assert-Pattern -Pattern 'jobs:\s*(?:.|\r|\n)*publish-and-package:' -Description 'publish-and-package job is required.'
Assert-Pattern -Pattern 'release-gate-unit-tests:\s*(?:.|\r|\n)*?runs-on:\s*windows-latest' -Description 'release-gate-unit-tests must run on windows-latest.'
Assert-Pattern -Pattern 'release-gate-integration-tests:\s*(?:.|\r|\n)*?runs-on:\s*windows-latest' -Description 'release-gate-integration-tests must run on windows-latest.'
Assert-Pattern -Pattern 'release-gate-stress-tests:\s*(?:.|\r|\n)*?runs-on:\s*windows-latest' -Description 'release-gate-stress-tests must run on windows-latest.'
Assert-Pattern -Pattern 'name:\s*Release gate tests \(unit\)' -Description 'unit release gate test step is required.'
Assert-Pattern -Pattern 'name:\s*Release gate tests \(integration\)' -Description 'integration release gate test step is required.'
Assert-Pattern -Pattern 'name:\s*Release gate tests \(stress\)' -Description 'stress release gate test step is required.'
Assert-Pattern -Pattern 'publish-profiles:\s*(?:.|\r|\n)*needs:\s*(?:.|\r|\n)*release-gate-unit-tests\s*(?:.|\r|\n)*release-gate-integration-tests\s*(?:.|\r|\n)*release-gate-stress-tests' -Description 'publish-profiles must depend on all split release gate jobs.'
Assert-Pattern -Pattern 'build-msi-installers:\s*(?:.|\r|\n)*needs:\s*(?:.|\r|\n)*release-gate-unit-tests\s*(?:.|\r|\n)*release-gate-integration-tests\s*(?:.|\r|\n)*release-gate-stress-tests' -Description 'build-msi-installers must depend on all split release gate jobs.'
Assert-Pattern -Pattern 'publish-and-package:\s*(?:.|\r|\n)*needs:\s*(?:.|\r|\n)*publish-profiles\s*(?:.|\r|\n)*build-msi-installers' -Description 'publish-and-package must depend on publish-profiles and build-msi-installers.'
Assert-Pattern -Pattern 'publish-release-profiles\.ps1\s+-TargetProfile\s+\$profile\s+-Configuration\s+Release' -Description 'publish-profiles must use the shared publish-release-profiles script.'
Assert-Pattern -Pattern 'softprops/action-gh-release@v3' -Description 'release creation must use the Node 24-compatible softprops/action-gh-release v3 line.'

$requiredEnvMappings = @(
    'RELEASE_REF_TYPE:\s*\$\{\{\s*github\.ref_type\s*\}\}',
    'RUNNER_ENVIRONMENT:\s*\$\{\{\s*runner\.environment\s*\}\}',
    'AUDIOPILOT_TEST_OUTPUT_DEVICE_A:\s*\$\{\{\s*secrets\.AUDIOPILOT_TEST_OUTPUT_DEVICE_A\s*\}\}',
    'AUDIOPILOT_TEST_OUTPUT_DEVICE_B:\s*\$\{\{\s*secrets\.AUDIOPILOT_TEST_OUTPUT_DEVICE_B\s*\}\}',
    'AUDIOPILOT_TEST_INPUT_DEVICE_A:\s*\$\{\{\s*secrets\.AUDIOPILOT_TEST_INPUT_DEVICE_A\s*\}\}',
    'AUDIOPILOT_TEST_INPUT_DEVICE_B:\s*\$\{\{\s*secrets\.AUDIOPILOT_TEST_INPUT_DEVICE_B\s*\}\}'
)

foreach ($envPattern in $requiredEnvMappings) {
    Assert-Pattern -Pattern $envPattern -Description "required release-gate env mapping missing: $envPattern"
}

$requiredFilterPatterns = @(
    'run-tests\.ps1.*-Category unit',
    'run-tests\.ps1.*-Category integration',
    'run-tests\.ps1.*-Category stress'
)

foreach ($filterPattern in $requiredFilterPatterns) {
    Assert-Pattern -Pattern $filterPattern -Description "required release-gate filter missing: $filterPattern"
}

$requiredLogicPatterns = @(
    '\$isTagRelease\s*=\s*\$env:RELEASE_REF_TYPE\s*-eq\s*"tag"',
    '\$isSelfHostedRunner\s*=\s*\$env:RUNNER_ENVIRONMENT\s*-eq\s*"self-hosted"',
    '\$env:AUDIOPILOT_REQUIRE_INTEGRATION_HARDWARE\s*=\s*if\s*\(\$isSelfHostedRunner\)\s*\{\s*"1"\s*\}\s*else\s*\{\s*"0"\s*\}',
    'if\s*\(\$missing\.Count\s*-eq\s*0\)',
    'validate-release-hardware\.ps1\s*-Configuration\s+Release\s+-NoBuild\s+-Strict:\$isSelfHostedRunner',
    'if\s*\(\$isSelfHostedRunner\)\s*\{\s*throw',
    'if\s*\(\$isTagRelease\)',
    'Hardware integration gate skipped for tag release because no self-hosted hardware runner is configured',
    'Hardware integration gate skipped for non-tag run',
    'Hardware integration gate disabled for this non-tag run because device-id preflight failed:',
    'Release validation will continue with software-only tests\.'
)

foreach ($logicPattern in $requiredLogicPatterns) {
    Assert-Pattern -Pattern $logicPattern -Description "required release-gate logic missing: $logicPattern"
}

Write-Host "Release gate workflow policy validation passed."
