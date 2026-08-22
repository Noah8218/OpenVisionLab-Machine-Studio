# Contributing to OpenVisionLab Machine Studio

Thank you for helping improve Machine Studio. The project accepts focused bug
fixes, tests, documentation, and vendor-neutral virtual-commissioning features
that stay within the supported local desktop scope.

## Before starting

- Search existing issues and pull requests.
- Open a feature request before broad UI, architecture, protocol, dependency,
  file-format, or compatibility changes.
- Do not present PLC, robot, MES, cloud, production-line, safety-control, or
  vendor-specific integration as a currently supported feature.
- Do not submit vendor logos, copied equipment designs, private protocols,
  proprietary recipes, credentials, customer data, or unlicensed assets.

## Development workflow

1. Fork the repository and create a short-lived branch from `main`.
2. Keep one pull request to one coherent behavior or contract boundary.
3. Follow the existing module ownership and MVVM boundaries in
   [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
4. Add the smallest relevant automated check for changed behavior.
5. Run the Release build and tests:

   ```powershell
   dotnet build OpenVisionLab.MachineStudio.sln -c Release
   dotnet test OpenVisionLab.MachineStudio.sln -c Release --no-build
   ```

6. For UI changes, include current-build evidence at `1280x760` and
   `1920x1040` and follow the repository UI/performance requirements.
7. Update user documentation only for behavior included in the same pull
   request.

Generated outputs, local evidence, user projects, credentials, and IDE files
must not be committed. Pull requests must pass the hosted deterministic smoke
and release-candidate checks before merge.

## Pull requests

Describe the user problem, the exact included and excluded scope, verification
performed, and any limitation that remains. Maintainers may close proposals
that expand unsupported factory integration, mix unrelated changes, lack
provenance, or cannot be verified deterministically.

By contributing, you agree that your contribution is licensed under the
repository's [MIT License](LICENSE).
