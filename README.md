# OpenVisionLab Machine Studio

**Configure a virtual machine, write its control sequence, and check machine behavior before hardware commissioning.**

Machine Studio is a Windows desktop application for control and machine-vision
engineers. Arrange conveyors, axes, sensors, cylinders and virtual cameras;
define their signals and sequences; then inspect motion, interlocks, acquisition
and inspection results in the same project.

Current version: **`v0.2.0-dev.82`**. This is an actively developed, hardware-free
simulation environment. It is not qualified for production equipment control
or safety functions.

## Equipment setup

![Equipment setup with the equipment hierarchy, scene and configuration panel](docs/images/equipment-setup.png)

Create stations and units, add devices, and configure their placement and
connections. The equipment tree and scene share the same selection. Top and
oblique views show the same layout; the oblique view is a schematic equipment
view rather than a CAD or contact-physics model.

## Process and actions

![Process and actions with the sequence overview and step settings](docs/images/process-and-actions.png)

Review sequences as a step table or flow overview, including branches and
connections between units. Add an individual axis, camera, DI/DO, subsequence,
unit, transfer or workpiece feed/eject step by explicitly selecting its target.
Names and IDs remain visible so identically named devices can be distinguished.

Step settings use an explicit **Apply** action. Position teaching and step
targets are separate: reviewing a teaching position does not move the axis;
copying its numerical value changes the step draft until it is applied. Adding,
deleting and reordering steps currently requires a supported linear sequence.

## Capture and inspection

![Capture and inspection with the main image and camera settings](docs/images/capture-and-inspection.png)

Select a virtual camera, configure its image source and acquisition settings,
then acquire an image explicitly. The main image remains separate from the
scrolling settings. Preparation details distinguish the selected recipe from
the recipe, image ID and result used by the current acquisition.

Supported paths include project image files and numerical 2D/height data.
External inspection requires a compatible inspection application and connection
settings. Built-in deterministic judgments support control-flow testing;
they do not establish inspection accuracy or provide a production AI model.

## Simulation

![Simulation with the equipment scene and runtime diagnostics](docs/images/simulation.png)

Start the authored machine, pause and resume it, advance a tick or sequence
step, and reset it to inspect a control problem. Read axis, signal, device and
sequence state beside the equipment scene. Expand process diagnostics to use a
right-hand panel in wide layouts or a scrolling panel below the scene in compact
layouts. Choose a unit by its ID to narrow the displayed processes; this does
not change the run target or open equipment settings. Current-run diagnostics
are hidden while viewing isolated playback or unapplied definitions.

The simulation uses a fixed 5 ms tick; this is simulation time, not a hard
real-time scheduling guarantee.

End the run before changing equipment settings. Pausing retains the current
run; it does not apply edited definitions to that run.

## Experiments and data

![Experiments and data with camera variation settings and the applied configuration summary](docs/images/experiments-and-data.png)

Edit the selected camera's variation parameters and review its applied image
source. Apply acquisition settings explicitly. Scenario configuration and
repeat validation are separate from camera variation settings: choose the
scenario and repetition count, run the validation, and inspect its status.

The equipment-fault tab supports forced digital inputs, blocked cylinder
travel, blocked axes and axis following errors. Select the fault and actual
runtime target, then activate or clear it explicitly. Active faults show their
target IDs and command status. Faults belong to the current simulation session;
they are not saved as recipe settings.

## Result analysis

![Result analysis with recorded acquisitions and comparison controls](docs/images/result-analysis.png)

Open a recorded run, inspect its acquisitions and results, or select two
records from the same recipe for comparison. Choose the acquisitions to compare
explicitly. A recorded camera can open its current settings and return to the
selected record; missing step identity is not guessed from its name or order.

Saved run records and current-session events serve different purposes. Opening
a record or restoring settings does not start a run, acquire an image, or apply
an inspection result.

Screenshots show the actual WPF views with example data, rendered in process at
native 100% scaling in an FHD display's working area. They illustrate the
interface; they do not qualify every desktop input state, theme or DPI setting.

## Start with an example

1. Open **Automatic Transfer Cell** from the start screen, or open an
   `.ovmachine` project. Save a copy before changing an example.
2. In **Equipment setup**, check the units, device connections and DI/DO.
3. In **Process and actions**, review the sequence targets and waiting conditions.
4. Configure **Capture and inspection** if the process needs vision. Keep the
   selected recipe, applied settings and current acquisition distinct.
5. Start **Simulation**, inspect the result, then end the run before editing.
   Use **Experiments and data** for scenario validation and **Result analysis**
   for recorded runs.

**Apply** updates the relevant settings or step in the working project.
**Save** writes the project file. Starting a run is a separate action. Loading
a project, selecting a device or browsing results does not run the machine.

The repository also includes a wafer EFEM example, an inspection-flow example,
a large-layout navigation example, and
[ten semiconductor equipment recipes](samples/SemiconductorRecipes/README.md).
These model equipment control and inspection handoffs, not semiconductor
process physics. Basic local simulation requires no cloud account.

## Build from source

Requirements: Windows 10 or later, the .NET 8 SDK, and Git. Visual Studio users
can open `OpenVisionLab.MachineStudio.sln` and select
`OpenVisionLab.MachineStudio` as the startup project.

From the repository root:

```powershell
dotnet build OpenVisionLab.MachineStudio.sln -c Release
dotnet run --project src/OpenVisionLab.MachineStudio/OpenVisionLab.MachineStudio.csproj -c Release --no-build
```

The pinned integration packages are included in the local NuGet feed. See
[the dependency notes](third_party/OpenVisionLabIntegrationContracts/README.md)
for their versions and licenses. A source build does not establish compatibility
with an arbitrary installed inspection application.

## Development status

This development version includes equipment authoring, sequence editing,
virtual acquisition, simulation, scenario checks and run analysis. Full desktop
interaction, all themes and scaling combinations, restart behavior and
production performance are still being qualified. Use copies of important
projects when evaluating it.

Real PLC, robot and camera control, safety control, MES/cloud operation,
CAD authoring, calibrated physical simulation and production AI inspection
are outside the current supported scope. Height data is numerical test data;
it is not evidence of a calibrated 3D measurement system. Image-set generation,
review and retention are not presented as a completed workflow.

## Version history

Machine Studio uses explicit development versions. Older history remains in Git.

### `v0.2.0-dev.82` (2026-10-18)

- Move expanded simulation diagnostics beside the scene in wide layouts and
  retain the compact scrolling layout.
- Add explicit unit observation by ID, preserve the run target and drafts,
  and keep current-run information separate from playback.

### `v0.2.0-dev.81` (2026-10-18)

- Introduce the six-workspace interface for equipment, process authoring,
  acquisition, simulation, experiments and result analysis.
- Add explicit single-step authoring, sequence flow views, teaching-settings
  navigation, camera preparation summaries and equipment-fault target details.
- Retain explicit Apply, Save and Run actions and replace the README with the
  current interface and supported workflows.

### `v0.2.0-dev.69` (2026-10-01)

- Preserve pause while waiting for an external inspection result and keep
  run controls responsive during result waiting.

### `v0.2.0-dev.68` (2026-10-01)

- Preserve the full acquisition image with long camera names and keep image
  settings reachable in compact layouts.

### `v0.2.0-dev.67` (2026-10-01)

- Preserve image aspect ratio, restore scene input between grid lines, and
  retain image settings when undoing a layout change.

### `v0.2.0-dev.66` (2026-10-01)

- Recover from unreadable images and cancellation at the start of repeat validation.

### `v0.2.0-dev.65` (2026-10-01)

- Expand schematic equipment profiles and correct language-initialization
  and inspection-validation build issues.

### `v0.2.0-dev.64` (2026-10-01)

- Improve run controls, material-flow status and camera settings; preserve
  manual-control ownership across pause and resume.

### `v0.2.0-dev.62` (2026-09-29)

- Add station/unit navigation, top and oblique equipment views, and a large-layout overview.

### `v0.2.0-dev.23` (2026-09-22)

- Extend external inspection-result integration.

## Contributing and licensing

For source ownership and build guidance, see [Architecture](docs/ARCHITECTURE.md).
Follow [Contributing](https://github.com/Noah8218/OpenVisionLab-Machine-Studio/blob/main/CONTRIBUTING.md) for changes and include the version,
reproduction steps and relevant layout/scaling information with UI reports.
Remove customer data and credentials from shared projects.

[Support](https://github.com/Noah8218/OpenVisionLab-Machine-Studio/blob/main/SUPPORT.md) · [Security reporting](https://github.com/Noah8218/OpenVisionLab-Machine-Studio/blob/main/SECURITY.md) ·
[Code of conduct](https://github.com/Noah8218/OpenVisionLab-Machine-Studio/blob/main/CODE_OF_CONDUCT.md)

Machine Studio is available under the [MIT License](LICENSE). See
[NOTICE](NOTICE), [third-party notices](THIRD-PARTY-NOTICES.md) and
[asset provenance](ASSET-PROVENANCE.json) for attribution. The project is
vendor-neutral and is not affiliated with an equipment manufacturer.
