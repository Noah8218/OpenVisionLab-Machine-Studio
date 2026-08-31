# .NET Version Policy

## Current

- Target framework: `net8.0` / `net8.0-windows`
- Language version: C# 12
- Nullable: enable
- ImplicitUsings: enable
- Product version: `0.2.0-dev`

The public repository is currently on the Release 2 development line. This is
not a release tag or a downloadable release; the release-candidate gates below
remain required before publication.

The product version has one source of truth: `Version` in
`Directory.Build.props`. Machine Studio generates runtime evidence identity as
`<version>+g<40-character commit>.<clean|dirty|unknown>`. `clean` means the
build observed no tracked or untracked worktree changes, `dirty` means it did,
and `unknown` means Git identity could not be established. Evidence is an exact
commit build only when the state is `clean` and the recorded commit is a full
40-character hexadecimal hash. `0.1.0.0`, a hash without source state, and
`unknown` are not exact evidence identities.

## Release candidate convention

- Use Semantic Versioning: `MAJOR.MINOR.PATCH[-prerelease]`.
- Use `-rc.N` for a candidate and remove the suffix only after every required
  release gate passes.
- Name the framework-dependent archive
  `OpenVisionLab.MachineStudio-<version>-windows-framework-dependent.zip`.
- Name the current self-contained Windows x64 archive
  `OpenVisionLab.MachineStudio-<version>-windows-x64-self-contained.zip`.
- A release-candidate tag uses `vMAJOR.MINOR.PATCH-rc.N` and must point to the
  exact clean source commit recorded in its manifest.
- Creating a tag, GitHub Release, installer, signed package, or public download
  requires an explicit release decision; a successful local publish alone does
  not authorize distribution.

## Policy

1. Start on .NET 8 for compatibility with existing OpenVisionLab infrastructure.
2. Plan migration to .NET 10 LTS before v1.0 public release.
3. Keep Core/Simulation/Vision code free of WPF and Windows API dependencies.
4. Keep package versions explicit and consistent; introduce
   `Directory.Packages.props` only when central package management is actually
   enabled for the repository.
5. Avoid .NET 8-only non-standard APIs that block migration.
6. Prefer file/IPC contracts over runtime version coupling with OpenVisionLab.
