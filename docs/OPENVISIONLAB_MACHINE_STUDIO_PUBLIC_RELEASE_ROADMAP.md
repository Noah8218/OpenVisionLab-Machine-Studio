# OpenVisionLab Machine Studio Public Release Roadmap

Updated: 2026-08-22

Status: Current authority for sequential public development and releases.

## Release rule

Finish, qualify, publish, and read back one release before starting the next
release's qualification. A public repository, passing local build, or existing
prerelease does not by itself authorize a final release.

Every public application release uses an immutable Git tag and GitHub Release
from an exact clean commit. The release contains a self-contained Windows x64
ZIP, SHA-256, payload manifest, source identity, license/notices, approved asset
provenance, supported workflows, limitations, and verification summary.

```text
feature branch -> pull request -> hosted CI -> protected main
  -> v0.N.0-rc.N -> extracted-package qualification
  -> owner GO/NO-GO -> v0.N.0 GitHub Release -> download/hash readback
```

Failed candidates receive a new commit and version. Existing tags and assets
are not replaced.

## Release 1 — Standalone public foundation (`v0.1.0`)

Users can author equipment and sequences, run deterministic local simulation,
use the bundled semiconductor recipes, and retain project-linked evidence
without physical hardware.

Release 1 includes:

- one reproducible self-contained Windows x64 package;
- public README, contribution, security, conduct, support, and issue/PR rules;
- history/privacy, secret, license, dependency, and asset-provenance review;
- protected `main`, required hosted CI, maintainer ownership, and private
  vulnerability reporting;
- extracted-package workflow checks and public download/hash readback.

The repository visibility change, release version movement, and publication
remain explicit owner decisions.

## Release 2 — Machine Studio and 3D Studio integration (`v0.2.0`)

One explicit Machine action exports exact context to 3D Studio. 3D Studio
acknowledges the handoff, preserves explicit Preview/Publish/Run, writes a
verified result, and Machine imports it only after explicit refresh.

Before consumer UI work, both products must use the same exact
`OpenVisionLab.Integration.Contracts` package and pass its Handoff,
Acknowledgement, Result, schema, identity, error-code, and recovery fixtures.
Release 2 publishes both independent applications, the pinned package, and an
exact compatibility matrix.

## Release 3 — Repeated workflow and recovery (`v0.3.0`)

Users can review project-scoped transaction history and recover waiting,
rejected, interrupted, stale, and completed work without inspecting JSON.
Retry, cancel, replace, retain, and remove remain explicit. Restore never runs
or saves either product automatically.

## Release 4 — Bounded extensibility (`v0.4.0`)

Contributors can add vendor-neutral virtual equipment, evidence adapters, and
integration mappings through documented source-neutral extension points.
Extensions remain optional and isolated from the simulation clock. This phase
does not authorize PLC, robot, MES, cloud, production control, or safety-
critical connectors.

## `1.0.0` boundary

Release 4 does not automatically become `1.0.0`. A later stable release needs
proven upgrade compatibility, clean-host evidence, security-response ownership,
maintainer capacity, resolved release-blocking feedback, and explicit owner GO.

## Priorities

1. Qualify release 1 source, governance, CI, privacy, and reproducible package.
   | Recommended model: `gpt-5.6-sol` | Reasoning effort: `high`
2. Build and qualify the common integration package and release 2 after release
   1 public readback. | Recommended model: `gpt-5.6-sol` | Reasoning effort:
   `high`
3. Implement release 3 history and recovery after release 2 evidence is
   accepted. | Recommended model: `gpt-5.6-sol` | Reasoning effort: `medium`
4. Implement release 4 extension boundaries after release 3 usage evidence.
   | Recommended model: `gpt-5.6-sol` | Reasoning effort: `high`

Only release 1 is active.
