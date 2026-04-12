# Contributing

Thanks for contributing to AudioPilot.

This guide keeps changes predictable, reviewable, and easy to validate. Use [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) for architecture details and [DOCS_STYLE_GUIDE.md](DOCS_STYLE_GUIDE.md) for documentation standards.

## Development Setup

1. Install .NET SDK 10.
2. Fork and clone the repository.
3. Create a feature branch from `main`.
4. Run the default local loop:

   ```powershell
   pwsh ./scripts/build.ps1
   pwsh ./scripts/run-tests.ps1 -Category unit
   ```

### Optional Installer Tooling

If you want to open and edit `AudioPilot.Installer/AudioPilot.Installer.wixproj` inside Visual Studio, install FireGiant HeatWave for your Visual Studio version.

This is optional IDE integration only. Normal app build/test workflows and command-line MSI builds do not require HeatWave.

## Recommended Workflow

1. Open an issue, or comment on an existing issue, before large changes.
2. Keep each PR scoped to one feature, fix, or cleanup topic.
3. Write commit messages that explain intent, not just the file touched.
4. Update docs in the same PR when behavior, commands, or user workflows change.

## Required Checks Before A PR

Run these locally:

```powershell
pwsh ./scripts/build.ps1
pwsh ./scripts/run-tests.ps1 -Category unit
pwsh ./scripts/validate-format.ps1 -Action check
./scripts/check-format-changed-files.ps1
./scripts/validate-release-gate-policy.ps1
./scripts/validate-doc-links.ps1
```

If you want the aggregate local pre-PR flow, use:

```powershell
pwsh ./scripts/validate-all.ps1
```

## Test Categories

- `unit`: default fast loop for most changes.
- `integration`: hardware-aware or broader workflow coverage.
- `visual`: manual-only visible WPF test path for the 5 real window-show tests.
- `stress`: churn-oriented reliability coverage.
- `full`: aggregate suite used for broader validation and release-oriented work.

Useful commands:

```powershell
pwsh ./scripts/run-tests.ps1 -Category integration
pwsh ./scripts/run-tests.ps1 -Category visual
pwsh ./scripts/run-tests.ps1 -Category stress
pwsh ./scripts/run-tests.ps1 -Category full
pwsh ./scripts/run-tests.ps1 -Category full -Coverage
```

For local IDE or one-off command-line runs, integration and stress suites also expose xUnit traits:

```powershell
dotnet test AudioPilot.Tests/AudioPilot.Tests.csproj --filter "Category=Integration"
dotnet test AudioPilot.Tests/AudioPilot.Tests.csproj --filter "Category=Stress"
```

The PowerShell categories remain the authoritative workflow because they also set the required environment guards. In particular, `-Category visual` enables both `AUDIOPILOT_RUN_VISUAL_WPF=1` and `AUDIOPILOT_TEST_SHOW_WINDOWS=1`, so the visible WPF tests are both runnable and intentionally shown.

Plain `dotnet test` is now unit-oriented by default:

- integration and stress tests discovery-skip unless `AUDIOPILOT_RUN_INTEGRATION=1` or `AUDIOPILOT_RUN_STRESS=1` is set,
- the 5 visual WPF window-show tests additionally require `AUDIOPILOT_RUN_VISUAL_WPF=1`,
- the default repo scripts and CI also exclude those categories explicitly,
- this keeps hardware-sensitive and visible-window integration tests out of the normal local and PR loop.

If you intentionally want the real visible WPF window tests, opt in explicitly:

```powershell
pwsh ./scripts/run-tests.ps1 -Category visual
```

Default GitHub Actions CI in `.github/workflows/ci.yml` stays unit-oriented:

- pull requests run the normal solution test pass without setting `AUDIOPILOT_RUN_INTEGRATION` or `AUDIOPILOT_RUN_STRESS`,
- pushes to `main` add coverage collection for that same unit-oriented pass,
- stress and integration suites are reserved for explicit local runs and the separate release workflow.

Practical implication: adding or expanding stress coverage does not change default PR CI behavior unless the workflow itself is updated.

If the AudioPilot UI is already running, test execution can fail fast because single-instance activation, global hotkeys, and shared UI resources interfere with local runs.

Useful helpers:

```powershell
pwsh ./scripts/stop-audiopilot-and-test.ps1
pwsh ./scripts/check-audiopilot-running.ps1
```

## Script Reference

- `scripts/build.ps1`: restore and build the solution.
- `scripts/run-tests.ps1`: run unit, integration, visual, stress, or full suites, with optional coverage. Unit is the default non-integration, non-stress path; `visual` runs only the manual visible WPF tests and intentionally shows their windows.
- `scripts/stop-audiopilot-and-test.ps1`: stop the running UI process, then run the default unit-oriented test pass.
- `scripts/check-audiopilot-running.ps1`: fail if the AudioPilot UI is already running.
- `scripts/validate-format.ps1`: run formatter checks or fixes against the SDK-style project solution filter.
- `scripts/check-format-changed-files.ps1`: enforce style on changed C# files only.
- `scripts/validate-doc-links.ps1`: verify markdown links across repo docs.
- `scripts/validate-release-gate-policy.ps1`: verify the release workflow still enforces the required gate policy.
- `scripts/validate-release-hardware.ps1`: verify `AUDIOPILOT_TEST_*` values resolve to exact endpoint IDs on the current runner.
- `scripts/benchmark-readytorun.ps1`: measure ReadyToRun publish size deltas.
- `scripts/validate-all.ps1`: run the normal local validation chain.
- `scripts/package-release.ps1`: create packaged release artifacts, MSI staging, winget manifests, checksums, and a manifest.
- `scripts/validate-release-integrity.ps1`: validate release ZIPs, MSI installers, winget manifests, checksums, and manifest entries.
- `scripts/release-body.ps1`: generate release notes from packaged release metadata.
- `scripts/checksum-table.ps1`: render markdown checksum rows for release notes.

If you use VS Code, `.vscode/tasks.json` exposes the common local entry points.

## Pull Request Expectations

- Explain what changed and why.
- Include the validation commands you ran.
- Attach screenshots or GIFs for UI changes when possible.
- Call out tradeoffs, limitations, or behavior changes explicitly.
- Link related issues.

## Code Expectations

- Follow the existing structure, naming, and layering.
- Prefer root-cause fixes over short-lived workarounds.
- Avoid broad refactors in feature or bug-fix PRs unless the scope requires them.
- Preserve existing behavior unless the change is intentionally behavioral.

### Performance And Reliability Guardrails

- Preserve event coalescing in high-churn paths such as hotplug, session-created, and refresh loops.
- Prefer centralized timing and cadence constants in `AppConstants` over ad-hoc delays.
- For high-frequency diagnostics, prefer sampled or windowed summaries over per-item logging.

## Documentation Expectations

Use [DOCS_STYLE_GUIDE.md](DOCS_STYLE_GUIDE.md) as the source of truth.

Docs updates are required in the same PR when you change:

- command behavior, flags, exit codes, or JSON output shape,
- startup, tray, or minimize behavior,
- switch recovery, retry, debounce, or resume handling,
- hotplug refresh or mixer refresh behavior,
- cache or snapshot fast-path behavior,
- interop conventions or lifetime rules,
- settings keys or defaults.

Keep these ownership boundaries intact:

- `README.md`: landing page and high-level orientation.
- `docs/USER_GUIDE.md`: user workflows and troubleshooting.
- `docs/CLI.md`: detailed CLI reference.
- `docs/DEVELOPER_GUIDE.md`: architecture and implementation guidance.

## Testing Expectations

- Add or update tests when behavior changes.
- Keep tests focused and deterministic.
- Do not mix unrelated test refactors into the same PR.
- When you change logging patterns, keep at least one focused real log-file assertion where practical.

## Coverage Policy

Coverage policy is defined in `.github/quality/coverage-policy.json`.

CI enforces both rules:

- coverage must stay above `minimumCoveragePercent`,
- once coverage reaches `nextTargetPercent`, CI fails until the policy file is ratcheted in the same PR.

Keep unit, integration, and stress coverage runs separated. The known hardware-sensitive stress combination can still trigger CLR abort `0x80131506` if those categories are forced through one combined coverage collection session.

CI also uploads `artifacts/testresults/coverage` for each run.

## Release Trust Posture

- Release verification currently relies on artifact integrity checks plus CI validation gates.
- If you modify packaging or release automation, preserve checksum generation and verification behavior.
- Code signing and SBOM publishing are future improvements, so avoid changes that make those harder to adopt later.

## Communication

Be respectful and constructive in issues and reviews. The project optimizes for clarity, maintainability, and reliable behavior.

Use [../CODE_OF_CONDUCT.md](../CODE_OF_CONDUCT.md) for the baseline community expectations.

## Related Docs

- User guide: [USER_GUIDE.md](USER_GUIDE.md)
- Developer guide: [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md)
- Docs style guide: [DOCS_STYLE_GUIDE.md](DOCS_STYLE_GUIDE.md)
- Releasing: [RELEASING.md](RELEASING.md)
