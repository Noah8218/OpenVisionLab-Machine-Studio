# OpenVisionLab Machine Studio

OpenVisionLab Machine Studio is a Windows desktop workbench for designing,
simulating, and validating industrial machine behavior before physical hardware
is available.

Build a machine layout, connect virtual axes and I/O, author an automatic
sequence, and inspect the same deterministic execution through run, pause,
single-step, reset, dry-run, and layout playback workflows.

> Machine Studio is a technical-alpha virtual-commissioning tool. It is not
> production-control or safety software.

## Version

Current version: `v0.2.0-dev.4`

This project is maintained using explicit version numbers. The current branch
is a Release 2 development candidate; it is not a release tag or downloadable
package.

### Recent version history

#### `v0.2.0-dev.4` (2026-09-04)

- Aligned the release-candidate validation script with the active `-dev.N`
  development-candidate version convention.
- Kept the release-candidate workflow limited to build, test, audit, and package
  verification; no release or download was published.

#### `v0.2.0-dev.3` (2026-09-04)

- Corrected the canonical product-version source after the preceding public
  metadata commit left `Directory.Build.props` at the previous development
  version.
- Retained the independently tested `SimulationRunLoopTiming` refactor with
  no additional runtime behavior change.

#### `v0.2.0-dev.2` (2026-09-04)

- Isolated real-time wall-clock accumulation, catch-up limiting, and delay
  calculation behind the independently tested `SimulationRunLoopTiming` owner.
- Preserved the existing deterministic fixed-step engine and Release 2 public
  development branch contract.

#### `v0.2.0-dev.1` (2026-09-01)

- Aligned the Release 2 development branch with immutable Contracts `0.2.0-alpha.3` and TCP Transport `0.1.0-alpha.3` package bytes.
- Recorded the clean shared-package source commit and preserved the previous alpha.2 package bytes for traceability.

#### `v0.2.0-dev` (2026-08-31)

- Added authenticated TCP transfer for explicit Machine/consumer transaction
  exchange, with session-only shared keys and persisted endpoint settings.
- Preserved the existing explicit Handoff export and Result refresh workflow.

#### `v0.1.0-rc.4` (2026-08-22)

- Release 1 public release-candidate baseline for local deterministic
  simulation and virtual commissioning.

## Download

Download the latest Windows x64 ZIP from
[GitHub Releases](https://github.com/Noah8218/OpenVisionLab-Machine-Studio/releases),
extract the complete archive, and run `OpenVisionLab.MachineStudio.exe`.

The self-contained package includes the required .NET 8 desktop runtime. No
separate .NET installation is required. Current packages are unsigned, so
Windows may show an unknown-publisher warning. Verify the published SHA-256
before running a downloaded archive.

## What you can do

- Create a blank machine project or start from the bundled Automatic Transfer
  Cell.
- Place and edit machine frames, stages, sensors, cylinders, conveyors, and
  workpieces on a 2D/2.5D layout.
- Define virtual motion axes and digital I/O with explicit equipment bindings.
- Author and reorder automatic sequence steps without editing project JSON.
- Compile a project atomically and run it on a deterministic 5 ms simulation
  clock.
- Pause, step, reset, inject deterministic faults, and review immutable runtime
  snapshots and events.
- Check simulation readiness, preview a connected step, dry-run a complete
  recipe, and replay the exact failing boundary without changing the main
  runtime.
- Save and reopen `.ovmachine` projects and project-linked evidence.
- Use ten editable semiconductor equipment recipes as vendor-neutral starting
  programs.
- Switch the interface between Korean and English.

## Five-minute start

1. Launch `OpenVisionLab.MachineStudio.exe`.
2. Choose **Start from sample**.
3. Open **Connections** to review the equipment graph and sequence usage.
4. Choose **Check simulation readiness** to compile without starting the runtime.
5. Choose **Dry run recipe** to inspect the isolated timeline and final state.
6. Select a timeline entry and choose **View state on layout** to review that
   boundary.
7. Return to the workspace and choose **Simulation ON** for the live deterministic
   run. Use **Pause**, **Step**, and **Reset** as needed.
8. Use **Save As** before editing a bundled sample.

Loading, previewing, restoring, or opening linked evidence never starts the
simulation or saves a project automatically.

## Semiconductor recipe pack

The built-in gallery contains editable examples for:

- FOUP load port;
- cassette mapping;
- wafer prealignment;
- OCR inspection handoff;
- load-lock entry;
- spin-coat transfer;
- developer-track transfer;
- dry-etch transfer;
- CMP transfer;
- metrology sorting.

These recipes validate equipment ownership, interlocks, I/O, motion, branching,
and control flow. They do not predict semiconductor chemistry, vacuum
conductance, plasma, polishing, overlay, yield, or production classification.
See the [recipe pack guide](samples/SemiconductorRecipes/README.md) for the
case-by-case boundaries.

## Supported scope

Machine Studio currently focuses on local desktop simulation and virtual
commissioning:

- virtual motion axes and axis-bound stages;
- deterministic digital I/O;
- sensor, cylinder, conveyor, workpiece, chamber, and handoff state models;
- automatic sequence authoring and isolated verification;
- deterministic fault and assertion evidence;
- optional virtual-camera timing and mock inspection decisions.

Real-time PLC control, robot teaching, MES, cloud services, production-line
control, external camera SDKs, production Vision, and safety certification are
not supported in the current release.

## Projects and local data

Machine projects use the `.ovmachine` JSON format. Normal project data and
accepted evidence stay beside the saved project. User language settings are
stored under `%LOCALAPPDATA%\OpenVisionLab\MachineStudio\CONFIG`.

The application does not require an account, cloud service, or network
connection for its local workflows. Release 2 adds an optional, explicit,
authenticated TCP transfer path for a configured peer; it does not
automatically update, upload projects, or execute external equipment.

### Optional TCP integration (Release 2 development)

On the **3D Exchange** tab, save the shared exchange folder and the listen and
peer endpoints, then enter the same Base64 key (at least 32 decoded bytes) in
each participating application. The key is session-only; it is never written
to the settings file. The `OPENVISIONLAB_TCP_SHARED_KEY` environment variable
can supply the key for a headless or repeatable session. Start the listener and
use **Ping peer**, **Push latest**, or **Pull latest** explicitly. A transfer
only copies the selected transaction files; use the existing explicit Result
refresh action to validate and display a returned inspection result.

## Build from source

Requirements:

- Windows 10 or later;
- .NET 8 SDK.

```powershell
dotnet restore OpenVisionLab.MachineStudio.sln
dotnet build OpenVisionLab.MachineStudio.sln -c Release --no-restore
dotnet test OpenVisionLab.MachineStudio.sln -c Release --no-build --no-restore
dotnet run --project src/OpenVisionLab.MachineStudio/OpenVisionLab.MachineStudio.csproj
```

Create the verified self-contained release-candidate package from a clean Git
commit:

```powershell
.\scripts\build-release-candidate.ps1 `
  -ArtifactDirectory .\artifacts\release-candidate
```

The script verifies the Release build, tests, dependency findings, asset
provenance, runtime notices, payload manifest, SHA-256, and archive extraction
round trip. The output path must not already exist.

## Architecture

The simulation thread is the only runtime-state owner. UI commands enter a
non-dropping command queue, while the UI reads immutable snapshots and ordered
events. Simulation math, sequencing, device state, and presentation remain in
their owning modules.

See:

- [Architecture](docs/ARCHITECTURE.md)
- [Simulation time model](docs/SIMULATION_TIME_MODEL.md)
- [Vision integration boundary](docs/VISION_INTEGRATION.md)
- [Vendor integration and asset policy](docs/VENDOR_INTEGRATION_AND_ASSET_POLICY.md)
- [Public release roadmap](docs/OPENVISIONLAB_MACHINE_STUDIO_PUBLIC_RELEASE_ROADMAP.md)

## Contributing and support

Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Use the
issue templates for reproducible bugs and scoped, vendor-neutral proposals.

- Security reports: [SECURITY.md](SECURITY.md)
- Support boundaries: [SUPPORT.md](SUPPORT.md)
- Community conduct: [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)

## License and attribution

Machine Studio is available under the [MIT License](LICENSE). Dependency
attribution is recorded in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md),
and built-in visual assets are tracked by path and SHA-256 in
[ASSET-PROVENANCE.json](ASSET-PROVENANCE.json).

Machine Studio is vendor-neutral and is not affiliated with or endorsed by an
industrial-equipment or semiconductor-equipment vendor. Product names and
trademarks belong to their respective owners.
