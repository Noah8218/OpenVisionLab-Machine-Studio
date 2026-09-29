# Machine Studio Architecture

Machine Studio composes a Windows WPF authoring surface with a deterministic,
hardware-free machine runtime. The project definition, simulation state, file
adapters, and presentation have separate owners.

## Start here

Open `OpenVisionLab.MachineStudio.sln` and choose the WPF executable project
`src/OpenVisionLab.MachineStudio/OpenVisionLab.MachineStudio.csproj` as the
startup project. Read this path to locate a feature:

1. [`App.xaml.cs`](../src/OpenVisionLab.MachineStudio/App.xaml.cs) for application composition and lifetime.
2. [`ShellWindow.xaml`](../src/OpenVisionLab.MachineStudio/View/Shell/ShellWindow.xaml) and [`MainViewModel.cs`](../src/OpenVisionLab.MachineStudio/ViewModel/MainViewModel.cs) for workspace binding and command routing.
3. The feature owner under `ViewModel`, followed by its domain module and matching test class.

The solution and each project's `ProjectReference` entries are authoritative for
the dependency graph. Do not infer a startup project from solution ordering.

## Module map

| Module under `src` | Responsibility | Matching tests under `tests` |
| --- | --- | --- |
| `OpenVisionLab.MachineStudio` | WPF composition, Views, ViewModels, commands and UI lifetime | `OpenVisionLab.MachineStudio.Tests` |
| `OpenVisionLab.Machine.Core` | Authored project/device/layout definitions, validation and serialization contracts | `OpenVisionLab.Machine.Core.Tests` |
| `OpenVisionLab.Machine.IO` | Signal and channel state | `OpenVisionLab.Machine.Simulation.Tests` |
| `OpenVisionLab.Machine.Sequence` | Sequence authoring, compilation and execution contracts | `OpenVisionLab.Machine.Simulation.Tests` |
| `OpenVisionLab.Machine.Simulation` | Project-to-runtime compilation, fixed-step execution, snapshots and evidence | `OpenVisionLab.Machine.Simulation.Tests` |
| `OpenVisionLab.Machine.Vision` | Source-neutral image acquisition and inspection contracts | `OpenVisionLab.Machine.Vision.Tests` |
| `OpenVisionLab.Machine.Persistence` | Project file adapter and recipe catalog | Core and MachineStudio tests |
| `OpenVisionLab.Machine.Infrastructure` | Project assets and external inspection adapters | `OpenVisionLab.Machine.Infrastructure.Tests` |
| `OpenVisionLab.Localization`, `OpenVisionLab.Logging`, `OpenVisionLab.Logging.Controls`, `OpenVisionLab.Wpf.MessageDialogs` | Shared localization, logging and WPF presentation services | Relevant consumer tests |

## Equipment authoring and presentation

`ShellNavigationViewModel` owns workspace and panel navigation.
`ProjectTreeViewModel` owns the hierarchy projection and selection.
`MachineLayoutViewModel` owns scene scope and layout presentation; it does not
create an independent persisted copy of the selected unit. Layout mutations,
history, and property drafts use the existing authoring and editor owners.

`SceneDocumentView` binds this state; `MachineSceneViewport` and
`SceneViewportProjection` draw the top and oblique projections of the same
scene. These projections are presentation, not separate simulation engines.
Views retain only presentation and framework lifecycle behavior. Business
changes pass through the bound command and its existing state owner.

## From project to running machine

```text
Authored .ovmachine project
  -> validation and MachineProjectRuntimeCompiler
  -> SimulationSessionCoordinator
  -> FixedStepSimulationEngine command queue
  -> device / signal / sequence runtime state
  -> immutable snapshots and ordered events
  -> SimulationRuntimeProjectionCoordinator
  -> WPF presentation
```

The fixed-step engine owns simulation-thread ordering and runtime mutation.
Sequence execution consumes the runtime context rather than WPF objects.
The UI submits commands and reads snapshots; it does not directly advance
device state. See the [time model](SIMULATION_TIME_MODEL.md) for virtual time
and wall-clock distinctions.

File and network operations belong outside a fixed simulation tick. Vision
acquisition and external inspection adapters prepare correlated evidence;
the runtime admits it through explicit command/result contracts. See
[vision integration](VISION_INTEGRATION.md) for those boundaries.

## Persistence and lifetime

`ProjectLifecycleCoordinator` coordinates project transitions and unsaved
changes. `ProjectDocumentFileStore` owns the file boundary; authored project
serialization and validation remain in Core. Loading settings does not start
a machine or restore a running acquisition.

Application and session composition own runtime cancellation and disposal.
View and ViewModel lifetime owners detach subscriptions and dispose their
resources. Late asynchronous results must be checked against the current
session before they can change displayed state.

## Verification boundaries

Build the solution to check project references and XAML compilation. Run the
matching tests for changed contracts and failure/recovery paths. A ViewModel
or in-process WPF test does not prove desktop pointer, focus, DPI, theme, or
window-bound behavior. UI changes require actual-window evidence for the
states being claimed. Source-level checks do not establish hardware support
or production performance.
