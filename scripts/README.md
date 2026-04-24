# Scripts

This folder contains repo automation for local development, CI, release packaging, and release validation.

## Daily Development

- `build.ps1`: restore and build the solution.
- `run-tests.ps1`: run unit, integration, visual, stress, or full test suites. It refuses to stop a running AudioPilot UI unless `-StopRunningUi` is supplied explicitly.
- `validate-all.ps1`: run the normal local validation chain.
- `validate-format.ps1`: run or fix solution formatting.
- `check-format-changed-files.ps1`: style-check changed C# files.
- `validate-doc-links.ps1`: validate markdown links.
- `validate-test-isolation.ps1`: audit static mutable test hooks; local runs warn, CI runs strict.
- `update-cli-docs.ps1`: check generated CLI documentation blocks.
- `stop-audiopilot-and-test.ps1`: stop a running UI process, then run tests. Use `-CheckOnly` when you only want to fail if the UI is running.

## Release And Packaging

- `publish-release-profiles.ps1`: restore and publish all release profiles except `FolderProfile`.
- `build-local-release-artifacts.ps1`: build local release artifacts end to end.
- `package-release.ps1`: package ZIP/MSI/winget outputs and write checksums, release manifest, SBOM, and provenance metadata.
- `validate-release-integrity.ps1`: validate packaged release artifacts, MSI metadata, SBOM, and provenance metadata.
- `validate-winget-manifests.ps1`: validate generated winget YAML.
- `release-body.ps1`: generate release notes. Use `-ChecksumTable` to print markdown checksum rows.
- `generate-wix-publish-fragment.ps1`: generate the WiX fragment from published app files.

## Specialized Checks

- `validate-release-gate-policy.ps1`: verify the release workflow still has the required gate structure.
- `validate-release-hardware.ps1`: preflight hardware device IDs for integration release checks.
- `benchmark-readytorun.ps1`: measure ReadyToRun publish size and repeated startup-to-window timing.
- `test-msi-smoke.ps1`: install, upgrade, and uninstall MSI smoke test helper used by release automation.

`lib/` contains shared helper code for scripts and is not intended as a direct command surface.
