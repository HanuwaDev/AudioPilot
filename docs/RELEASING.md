# Releasing

This guide describes how to ship a new AudioPilot release.

Use this page for maintainer release work. Use [CONTRIBUTING.md](CONTRIBUTING.md) for normal development workflow and [CHANGELOG.md](CHANGELOG.md) for release-facing change notes.

## Versioning

- Follow Semantic Versioning.
- Update `AudioPilotVersion` in `Version.props` before release.
- Keep `docs/CHANGELOG.md` with a live `Unreleased` section between releases.

## Pre-Release Checklist

1. Confirm all release-targeted work is merged.
2. Move notable items from `Unreleased` into the new version section in [CHANGELOG.md](CHANGELOG.md).
3. Re-create an empty `Unreleased` section immediately after cutting release notes.
4. Run local validation:

   ```powershell
   dotnet build AudioPilot.sln
   pwsh ./scripts/run-tests.ps1 -Category unit
   dotnet format AudioPilot.Format.slnf --verify-no-changes --severity info
   ./scripts/validate-doc-links.ps1
   ```

5. Ensure CI is green on `main`.
6. Complete a privacy and logging review so default logs remain useful without exposing unnecessary sensitive identifiers.

## Docs Consistency Check

Before tagging:

- `README.md`, `docs/USER_GUIDE.md`, and `docs/CLI.md` should not contradict one another.
- Detailed command, JSON, and exit-code behavior belongs only in [CLI.md](CLI.md).
- Contributor and maintainer workflow docs should still match the real script names and workflow names.

## Coverage Policy

- CI coverage policy is defined in `.github/quality/coverage-policy.json`.
- Coverage must stay above `minimumCoveragePercent`.
- Once measured coverage reaches or exceeds `nextTargetPercent`, CI fails until the policy file is ratcheted in the same PR.
- On a release milestone, raise `minimumCoveragePercent` toward `nextTargetPercent` and set the next target.
- Keep the policy update in the same PR as the coverage ratchet.
- Keep unit, integration, and stress coverage runs separated. Do not collapse them into one collector session; the known hardware-plus-stress combination can trigger CLR abort `0x80131506` under combined coverage collection.

CI uploads `artifacts/testresults/coverage` on each run for diagnostics and trend review.

## Release Gate In CI

Normal `CI` and release automation do different things:

- `.github/workflows/ci.yml` stays unit-oriented and does not set `AUDIOPILOT_RUN_INTEGRATION` or `AUDIOPILOT_RUN_STRESS`,
- `.github/workflows/release-artifacts.yml` splits unit, integration, and stress into separate jobs before packaging,
- the dedicated stress job enables `AUDIOPILOT_RUN_STRESS=1`,
- the integration job enables `AUDIOPILOT_RUN_INTEGRATION=1` only after device-id preflight succeeds,
- the small set of visible WPF window tests remains manual-only because release automation does not set `AUDIOPILOT_RUN_VISUAL_WPF=1`.

If you intentionally need the visible WPF checks during local release prep, use:

```powershell
pwsh ./scripts/run-tests.ps1 -Category visual
```

That path is manual-only and intentionally enables visible test windows for the dedicated WPF shell checks.

The `Release Artifacts` workflow runs the broader gate before publishing:

```powershell
pwsh ./scripts/run-tests.ps1 -Category unit
$env:AUDIOPILOT_RUN_INTEGRATION = "1"; dotnet test AudioPilot.sln --nologo --filter "Category=Integration"
$env:AUDIOPILOT_RUN_STRESS = "1"; dotnet test AudioPilot.sln --nologo --filter "Category=Stress"
```

Plain `dotnet test AudioPilot.sln` is no longer the release-oriented superset by itself because integration and stress tests discovery-skip unless explicitly enabled.

Required integration device variables:

- `AUDIOPILOT_TEST_OUTPUT_DEVICE_A`
- `AUDIOPILOT_TEST_OUTPUT_DEVICE_B`
- `AUDIOPILOT_TEST_INPUT_DEVICE_A`
- `AUDIOPILOT_TEST_INPUT_DEVICE_B`

These values must be exact Core Audio endpoint IDs from the self-hosted Windows runner, not friendly names.

Tag behavior:

- GitHub-hosted `windows-latest` runs remain valid for release automation, but hardware validation is advisory only when no dedicated hardware runner or device-id secrets are available,
- when the required device secrets are present and preflight succeeds, the integration gate is enabled,
- when secrets are missing or preflight fails on a GitHub-hosted runner, the workflow warns and continues with software-only validation,
- for strict hardware enforcement, run the workflow on a self-hosted Windows runner with the required device configuration.

## Hardware Runner Setup

Configure the required device IDs as repository secrets in GitHub Actions.

Current repository automation does not assume a dedicated self-hosted
Windows hardware runner. If you want release tags to fail on missing
hardware validation instead of falling back to warnings and software-only
coverage, provide a self-hosted runner plus the required device-id
secrets.

Name note:

- validation commands use the `AudioPilot.CliHost` source project,
- the shipped executable inside release artifacts is always `AudioPilot.Cli.exe`.

Validate the configured IDs locally or on the runner with:

```powershell
pwsh ./scripts/validate-release-hardware.ps1 -Configuration Release
```

If you need to refresh the IDs on the runner:

```powershell
dotnet run --project AudioPilot.CliHost/AudioPilot.CliHost.csproj -c Release -- devices list output --json
dotnet run --project AudioPilot.CliHost/AudioPilot.CliHost.csproj -c Release -- devices list input --json

# After choosing an exact id, verify it resolves cleanly:
dotnet run --project AudioPilot.CliHost/AudioPilot.CliHost.csproj -c Release -- devices get output "<endpoint-id>" --json
dotnet run --project AudioPilot.CliHost/AudioPilot.CliHost.csproj -c Release -- devices get input "<endpoint-id>" --json
```

Use the `id` values from those commands, not the friendly `name` values.

## Manual Release Validation

Before tagging, run the local validation flow when possible. Then do real-hardware checks:

- hotplug churn: unplug and replug output and input devices while the app is running,
- rapid hotkey bursts: trigger output, input, and reverse actions repeatedly,
- shutdown soak: minimize, restore, and exit repeatedly,
- settings recovery: corrupt the primary settings file and verify backup recovery.

Confirm recovery candidates come from `backups/settings.json.bak*` and that the newest valid payload wins.

## Create The Release

1. Create and push the version tag:

   ```powershell
   git tag vX.Y.Z
   git push origin vX.Y.Z
   ```

2. Open or update the GitHub release notes.
3. Review the draft release created by the workflow before publishing.

The `Release Artifacts` workflow creates releases as drafts so maintainers can adjust highlights before publication.

If needed, regenerate the release body locally:

```powershell
./scripts/release-body.ps1 -Version X.Y.Z
```

## Publish Artifacts

Use the publish profiles under `AudioPilot/Properties/PublishProfiles`.

Each packaged publish output should include both:

- `AudioPilot.exe`
- `AudioPilot.Cli.exe`

### Supported Profiles

- `FrameworkDependent-win-x64`
- `FrameworkDependent-win-x86`
- `FrameworkDependent-win-arm64`
- `SelfContained-win-x64`
- `SelfContained-win-x86`
- `SelfContained-win-arm64`

ReadyToRun is enabled only for self-contained `win-x64` and `win-arm64` publishes. Framework-dependent publishes and `win-x86` remain IL-only unless measurements justify a broader rollout.

### Publish Commands

```powershell
dotnet publish AudioPilot/AudioPilot.csproj -c Release -p:PublishProfile=FrameworkDependent-win-x64
dotnet publish AudioPilot/AudioPilot.csproj -c Release -p:PublishProfile=FrameworkDependent-win-x86
dotnet publish AudioPilot/AudioPilot.csproj -c Release -p:PublishProfile=FrameworkDependent-win-arm64

dotnet publish AudioPilot/AudioPilot.csproj -c Release -p:PublishProfile=SelfContained-win-x64
dotnet publish AudioPilot/AudioPilot.csproj -c Release -p:PublishProfile=SelfContained-win-x86
dotnet publish AudioPilot/AudioPilot.csproj -c Release -p:PublishProfile=SelfContained-win-arm64
```

The CLI host executable is included by the app publish flow through `PublishCliHostAlongsideApp` in `AudioPilot/AudioPilot.csproj`.

### MSI Build Commands

Build the WiX installer after the publish outputs are ready:

```powershell
dotnet build AudioPilot.Installer/AudioPilot.Installer.wixproj -c Release -p:Platform=x64
dotnet build AudioPilot.Installer/AudioPilot.Installer.wixproj -c Release -p:Platform=arm64
```

If you want to open `AudioPilot.Installer/AudioPilot.Installer.wixproj` inside Visual Studio, install FireGiant HeatWave for your Visual Studio version. This is optional IDE support only and is not required by the command-line build or CI.

Current installer coverage:

- `x64`: MSI and ZIP
- `arm64`: MSI and ZIP
- `x86`: ZIP only

## Packaging And Integrity Validation

After publish completes, generate packages:

```powershell
./scripts/package-release.ps1 -Version X.Y.Z
```

This writes:

- `artifacts/release/SHA256SUMS.txt`
- `artifacts/release/release-manifest.json`
- `artifacts/release/*.zip`
- `artifacts/release/*.msi`
- `artifacts/release/winget/**/*.yaml`

Then validate package integrity:

```powershell
./scripts/validate-release-integrity.ps1 -ReleaseRoot artifacts/release
```

Validation covers:

- manifest and checksum count consistency,
- SHA256 parity between release artifacts, `SHA256SUMS.txt`, and `release-manifest.json`,
- required executables inside each ZIP package,
- expected MSI outputs and generated winget manifests.

`release-manifest.json` is for CI and diagnostics. It is not attached to the public GitHub release.

If you need checksum rows for the release body:

```powershell
./scripts/checksum-table.ps1
```

Optional packaging flags:

```powershell
./scripts/package-release.ps1 -Version X.Y.Z -Clean
./scripts/package-release.ps1 -PublishRoot artifacts/publish -OutputRoot artifacts/release
```

## GitHub Actions Automation

Use `.github/workflows/release-artifacts.yml` to automate publish and packaging on:

- manual runs through `workflow_dispatch`,
- tag pushes matching `v*`.

The workflow publishes all non-folder profiles, builds `x64` and `arm64` MSI installers, runs `scripts/package-release.ps1`, validates the result, and uploads `artifacts/release` as a workflow artifact.

Release creation happens on tag push only. Public release assets currently include:

- packaged ZIP files,
- MSI installers for `x64` and `arm64`,
- `SHA256SUMS.txt`.

Generated winget manifest YAML files remain in `artifacts/release/winget` and the workflow artifact for submission and audit, but they are not attached as user-facing GitHub release assets.

## Distribution Guidance

- Recommend the self-contained ZIP builds by default, especially `win-x64`, while releases remain unsigned.
- Keep the `x64` and `arm64` MSI installers available as an alternate per-user install path for users who want shortcuts and Add/Remove Programs integration.
- Use ZIP artifacts when you want a portable layout or need `x86`.
- Framework-dependent ZIP builds are smaller but require the matching .NET runtime.
- Self-contained ZIP builds are larger but include the runtime.

## Post-Release

- Verify installation and launch on a clean Windows environment.
- Confirm the GitHub release body, assets, and checksums are correct.
- Start a new `Unreleased` section in [CHANGELOG.md](CHANGELOG.md) if needed.

## Related Docs

- Changelog: [CHANGELOG.md](CHANGELOG.md)
- Contributor workflow: [CONTRIBUTING.md](CONTRIBUTING.md)
- Developer guide: [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md)
