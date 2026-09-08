`MachineProjectRuntimeComponentCompiler` owns conversion of the selected
active layout's component definitions into typed runtime component records,
including sensor target/delay and cylinder timing validation. The surrounding
`MachineProjectRuntimeLayoutCompiler` keeps project validation and active-layout
selection, then delegates device-family runtime compilation to
`MachineLayoutDeviceRuntimeCompiler`. This keeps the junior navigation path
project compiler → layout compiler → component compiler explicit.

`DeterministicScenarioAssertion` is the project-owned normalized assertion
contract. `DeterministicScenarioAssertionEvaluator` is the internal WPF-neutral
runtime evidence policy: it evaluates captured snapshots/events, resolves final
equipment state for checkpoints, and owns the definition/outcome hashes used by
portable run evidence. `DeterministicSimulationRunResultPackage` and
`DeterministicRecipeDryRunRunner` call that owner directly. Definition mapping,
normalization, and validation remain with the assertion contract. Do not split
these owners again without a newly measured defect, requirement, responsibility
conflict, or constraint.

# OpenVisionLab Machine Studio Architecture

## Overview

OpenVisionLab Machine Studio is a Windows desktop environment for authoring
equipment layouts and validating automatic machine behavior without physical
hardware. The primary runtime path joins authored layout components, motion
axes, command-driven pneumatic cylinders and conveyors, transported workpieces,
geometry-driven sensors, digital I/O, and embedded sequences on one
deterministic fixed-step clock.

Camera acquisition and Vision evidence remain supported secondary capabilities.
They join a machine cycle when an inspection task requires them; they do not own
the core equipment-simulation workflow.

For current source navigation, read [DEVELOPER_ONBOARDING.md](DEVELOPER_ONBOARDING.md)
and [MVVM_ARCHITECTURE.md](MVVM_ARCHITECTURE.md). MachineStudio feature owners
live in shallow `ViewModel` domain folders; `MainViewModel.cs` remains the root
composition entrypoint. Folder movement preserves existing namespaces and does
not itself change the module dependency graph below.

## Layered architecture

```text
MachineStudio (WPF composition and presentation)
  -> Core, IO, Sequence, Simulation, Vision, Infrastructure

Infrastructure (project assets and external adapters)
  -> Vision

Simulation (project compiler, fixed-step composition, runtime truth,
            deterministic execution evidence)
  -> Core, IO, Sequence

IO -> Core
Sequence -> Core
Vision -> Core
Core -> Logging only
```

`Simulation` owns runtime composition, fixed-step timing, axis advancement,
layout evaluation, cylinder travel and end-feedback DI, conveyor/workpiece
transport, geometry-sensor DI, automatic repetition, camera timing when
configured, snapshots, and ordered events. `Vision` owns source-neutral
acquisition/frame/runner contracts. `Infrastructure` may perform asynchronous
project-asset I/O, but file I/O is never executed inside a fixed simulation
tick.

The fixed-step engine remains the authoritative owner of simulation-thread
state transitions and tick ordering, while concrete owners hold cohesive
runtime state and policy boundaries. `DeterministicSimulationCommandTraceStore` owns synchronized in-memory
command-boundary entries, clearing, snapshots, and trace-package creation;
`FixedStepSimulationEngine` only decides when an applied command boundary is
captured. `DeterministicSequenceRuntimeContext` adapts the Sequence runtime
contract to the current signal hub, axes, and virtual cameras through explicit
dependencies and an event-emitter callback. It has no engine or WPF reference;
the engine retains tick state, event sequencing, and component ownership.

Simulation output resource lifetime has two concrete owners. `SimulationEventPublisher`
owns the bounded event channel, monotonic event index, event construction, and
completion. `LatestSnapshotStore` owns the current snapshot value, bounded
snapshot publication, reader, and completion. `FixedStepSimulationEngine`
retains snapshot construction and all simulation-thread ordering, and delegates
only publication to these owners. `ISimulationEngine` readers and the existing
event/snapshot contracts remain unchanged.

Sequence authoring keeps Process Block planning, mutation, and managed timeout
policy in separate concrete owners. `SemiconductorProcessBlockPlanBuilder` owns
the WPF-neutral read-only preview/recognition plan, step derivation, status and
authored-role checks, and plan cloning. `SemiconductorProcessBlockComposer`
remains the public compatibility facade and owns project mutation: insertion,
removal, transition rebuilding, and explicit copy/commit. Its public Preview and
recognition methods delegate to the plan builder, while Apply keeps the existing
mutation order. `SemiconductorProcessBlockTimeoutAdjuster` owns managed-step
timeout request normalization, validation, stale-preview comparison, project
snapshotting, and timeout application. The composer preserves its public timeout
methods as compatibility forwards; these owners have no WPF dependency. Do not
split these owners again without a newly measured defect, requirement,
responsibility conflict, or constraint.

Semiconductor station setup policy, plan construction, and mutation commit are
separate. `SemiconductorStationSetupResolver` owns persisted setup restoration,
default derivation from an authored project, and setup validation without
mutating the project. WPF-neutral `SemiconductorStationSkeletonPlanner` owns the
clone/build/role-resolution plan and returns
`SemiconductorStationSkeletonPlan` with the prepared clone and read-only preview.
`SemiconductorStationSkeletonTemplate` remains the public compatibility facade
and owns schema/setup/serialization comparison plus the explicit assignment
commit into the caller's project. Its public `ResolveSetup`/`IsValidSetup`
forwards preserve existing callers. Do not split these owners again without a
newly measured defect, requirement, responsibility conflict, or constraint.

Deterministic simulation run comparison has its own concrete owner,
`DeterministicSimulationRunResultComparer`. It owns package identity,
scenario, ordered tick, per-field evidence, and aggregate result mismatch
policy. Deterministic run evidence hashing and per-tick evidence construction
have a separate concrete owner, `DeterministicSimulationRunEvidenceHasher`.
`DeterministicSimulationRunResultPackage` retains result creation, integrity
validation, JSON persistence, and public `CompareTo`/`IsEquivalentTo`
compatibility forwards while routing its hash policy through the hasher.

Multi-axis commissioning execution has a separate concrete owner,
`DeterministicMultiAxisCommissioningRunner`. It owns repetition orchestration,
fixed-step engine lifetime, command sequencing, snapshot waiting, target
verification, progress, and cancellation. `DeterministicMultiAxisCommissioningResultPackage`
retains immutable result data, result construction, evidence hashing, context
validation, and JSON persistence; the existing public runner API is unchanged.

Deterministic batch execution has a separate concrete owner,
`DeterministicSimulationBatchRunner`. It owns input normalization,
accepted-baseline selection, sequential repetition execution, cancellation,
first-mismatch capture, and batch result construction.
`DeterministicSimulationBatchResultPackage` retains immutable batch data,
mismatch translation, evidence hashing, integrity/context validation, and JSON
persistence.

Batch comparison policy has a separate concrete owner,
`DeterministicSimulationBatchResultComparer`. It owns batch identity and
completion comparison, per-run comparison, mismatch translation, and failed-run
normalization. The package retains public `CompareTo`/`IsEquivalentTo`
compatibility forwards and uses the comparer for persisted mismatch integrity
checks.

## Deterministic condition scenario ownership

The authored condition profile is discoverable in `DeterministicConditionScenarioProfile.cs`. It owns profile data, recovery schedules, JSON persistence, normalization, and validation. Runtime samples, transitions, snapshots, and mutable deterministic progression are discoverable in `DeterministicConditionScenarioRuntime.cs`, which owns `DeterministicConditionStateMachine`. Both files remain WPF-neutral in the existing `Scenarios` namespace, and existing engine command/progress/runtime/replay owners keep their boundaries. Do not re-split these owners without a new defect, requirement, responsibility conflict, or measured constraint.

## Authoring-to-runtime flow

```text
MachineProjectDocument
  layouts + activeLayoutId
  axes + devices + channels + sequences
  automaticRun policy
        |
        v
MachineProjectRuntimeCompiler
  validates project contract, fixed-step compatibility, signals, sequences,
  automatic-run, and pick/place policy
  delegates authored runtime conversion:
        |
        +--> MachineProjectRuntimeAxisCompiler
        |      converts authored axes and validates motion parameters
        +--> MachineProjectRuntimeCameraCompiler
        |      converts virtual cameras and legacy camera properties
        +--> MachineProjectRuntimeLayoutCompiler
               validates/selects the active layout and assembles base components
               delegates specialized layout-device configuration:
        |
        +------> MachineLayoutDeviceRuntimeCompiler
               coordinates LoadLock, Inspection, OHT, and Prealigner bindings
               delegates WaferHandler validation to MachineLayoutWaferHandlerRuntimeCompiler
               returns the immutable layout-device runtime configuration
        +--> MachineProjectRuntimeSequenceCompiler
        |      compiles authored sequences and maps typed compilation errors
        +--> MachineProjectRuntimeAutomaticRunCompiler
        |      validates optional automatic-run policy and timing
        +--> MachineProjectRuntimePickPlaceCompiler
               validates optional pick/place workpiece policy
        |
        v
  emits immutable runtime Axis, Layout, IO, Sequence, and AutomaticRun config
        |
        v
FixedStepSimulationEngine (single runtime owner)
  1. integrate Axis state
  2. apply the Axis delta to each bound stage transform
  3. evaluate load-lock pressure commands and door interlocks
  4. read the allowed cylinder command DO, advance deterministic travel, and publish
     authored delayed Extended/Retracted DI
  5. read conveyor Run/Reverse DO and transport Source/Destination workpieces
     along their current authored-carrier local axis
  6. evaluate sensor/target geometry and authored on/off delay ticks
  7. write geometry-sensor Digital Input state and emit transitions
  8. advance automatic-repeat state and the embedded Sequence
        |
        v
WaitSignal observes the current tick's sensor DI
  -> Sequence motion/output decision
  -> immutable SimulationSnapshot + non-dropping ordered events
```

`DeterministicSignalHub` owns both digital and the approved first-slice analog
channel state. Digital values retain their existing `Signals` contract; analog
values are exposed through `AnalogSignals` beside them under the same signal
revision. Analog channels store finite scalar `double` values only: no units,
range, scaling, calibration, or hardware mapping is implied. Analog Input
writes are owned by `Manual` and `SimulationComponent`; Analog Output writes
are owned by `Manual` and `EmbeddedSequence`. The fixed-step engine projects
both lists into the immutable `SimulationSnapshot`. Analog Sequence semantics,
WPF authoring, and external adapters remain separate follow-up boundaries.

During Design-mode authoring, `LayoutAuthoringWorkspace` under
`ViewModel/Layout` owns command creation and the composition of history,
mutation, selection-command, and Scene-interaction workflows. It also owns their
Reset, command-invalidation, and history-disposal routing. `MainViewModel`
retains public command/API facades and explicit project/session and cross-feature
refresh callbacks; it no longer constructs those collaborators individually.
`LayoutAuthoringHistoryViewModel` owns bounded Undo/Redo/clipboard transactions
and selection restoration. `LayoutAuthoringMutationWorkflow` captures/commits
history around add/remove and maps typed results to dirty/runtime/definition
refresh and status/log callbacks.

The Core authoring rules remain in their existing concrete owners.
`LayoutComponentAuthoringFactory` creates a component and its bound
axis/device/channel artifacts with stable identifiers.
`LayoutComponentPlacementService` owns grid snapping, collision-free default
placement, and synchronization with a moved component's bound axis/device.
`LayoutComponentAuthoringService` orchestrates active-layout resolution,
rollback, full-project validation, deletion policy, and dependency guards.
`MachineLayoutViewModel` retains layout selection, definition synchronization,
editor lifetime, and binding state. `LayoutSelectionEditingWorkflow` owns
WPF-neutral drag/transform, snapping, alignment, nudging, and layer-order policy.
`SceneViewportInteractionWorkflow` handles neutral View requests through the
workspace. `SceneViewportProjection` owns the pure fit, screen/world, zoom-anchor,
and translation calculations shared by rendering and hit-testing; it has no WPF
control, resource, snapshot, or pointer state. `MachineSceneViewport` retains the
actual WPF resource lifetime, collection/snapshot observation, pointer capture, and
gesture state. Neither a partial Main type nor a second runtime is introduced.

`ProjectLifecycleCoordinator` under `ViewModel/Project` owns the active
`ProjectDocumentSession`, project-operation gate, save participant, and concrete
open/save/unsaved/copy workflows. It orders document transitions and delegates
runtime application and shell refresh through explicit callbacks.
`ProjectOpenWorkflow` owns file validation before the unsaved-change gate and
reloading after acceptance. `ProjectUnsavedChangesWorkflow` owns focused-editor
commit, save preparation, dirty-state refresh, and Save/Discard/Cancel resolution.
The operation gate coordinates entry to the project transaction, while the save
participant exposes its in-flight result to session close.

`ProjectSaveWorkflow` prepares the captured project, records its content hash,
awaits `ProjectDocumentStore.SaveAsync`, and persists linked scenario-batch,
multi-axis, and Vision artifacts in their existing order. It returns a
`ProjectDocumentSaveReceipt`. `ProjectDocumentSession` uses session identity,
revision, and content evidence to decide whether the completed save can update
the active path and dirty baseline. Each artifact owner retains its own path
relinking and persistence. `MainViewModel` retains public commands, title/binding
notifications, child updates, and the runtime-application callback.

`MainMessageDialogHost` under `View/Dialogs` owns localized unsaved/open-failure/
save-failure options, WPF owner resolution, modal invocation, and conversion to
neutral decisions. The project coordinator/workflows own the decision sequence
and expected save/open failure routing. Main supplies the host, status, and log
callbacks. Native dialog mechanics and file persistence remain separate.

Shell-only WPF interaction has a separate concrete `MainWpfInteractionHost`
boundary under `View/Shell`. It owns application shutdown, keyboard focus
clearing with the existing `DataBind` wait, and the guarded dispatcher
forwarding used by runtime and batch progress. `MainViewModel` retains command
and workflow timing while delegating those framework calls; `ShellWindow`
continues to own visual lifecycle, keyboard shortcut, resize, close, and
disposal events.

Project and recipe path selection has a separate concrete
`ProjectFileDialogHost` boundary under `View/Dialogs`. It owns the three
native Open/Save dialog configurations for opening a project, saving a project
as, and choosing a semiconductor recipe-copy destination. It returns a path or
cancelled selection and performs no project I/O, unsaved-change policy, or
workflow action. `MainViewModel` keeps the existing command and callback
routing, while the project/recipe workflows keep persistence and activation.

`SemiconductorRecipeCopyWorkflow` under `ViewModel/Recipes` owns the file-copy
transaction: path normalization, same-path overwrite rejection, source loading,
new identity/timestamp assignment, and destination persistence.
`ProjectLifecycleCoordinator` owns the surrounding unsaved-change decision,
copy/open ordering, and document-operation admission. Main supplies the copied
project's runtime-application and presentation callbacks.
`ProjectFileDialogHost` owns destination selection; neither the copy workflow
nor the project coordinator depends on WPF controls.

Simulation evidence and command-trace file selection has a separate concrete
`SimulationEvidenceFileDialogHost` boundary under `View/Dialogs`. It owns the
six native Open/Save dialog configurations for simulation evidence, command
trace, and unified commissioning evidence, and converts cancel/acceptance to a
selected path. It performs no application file I/O, business operation, or
runtime action. `MainViewModel` retains the existing callback and action
routing, while project workflow message dialogs use
`MainMessageDialogHost` and other view-local dialogs remain in their existing
owners.

Camera image-source selection has a separate concrete
`CameraImageSourceFileDialogHost` boundary under `View/Dialogs`. It owns the
native image-file dialog title, filter, project-root initial directory, and
cancel-to-null result. `CameraImageSourceEditorViewModel` retains the selected
path's project-relative conversion, validation, draft state, and camera-source
application. The selector is passed as a delegate so the editor workflow can
be exercised without opening an OS dialog; no interface or image-buffer owner
is introduced.

Machine integration path selection has a separate concrete
`MachineIntegrationFileDialogHost` boundary under `View/Dialogs`. It owns the
exchange-folder and 2D inspection-recipe native dialog configuration and
returns only a selected path or cancellation. `MachineIntegrationViewModel`
retains setting updates, status presentation, persistence, TCP, and handoff
workflow policy; the host performs no integration I/O or runtime action.

Semiconductor recipe compatibility-report path selection has a separate
concrete `SemiconductorRecipeCompatibilityDialogHost` boundary under
`View/Dialogs`. It owns the report Save and baseline/current Open dialog
configuration and returns only a selected path or cancellation.
`SemiconductorRecipeGalleryViewModel` retains validation, report creation and
persistence, report loading, comparison, and command state. Both hosts are
concrete/non-`partial` and use delegate seams so their caller workflows can be
tested without opening an OS dialog.

The semiconductor recipe gallery catalog and validation paths have separate
concrete workflow boundaries. `SemiconductorRecipeGalleryCatalog` owns bundled
recipe enumeration, project loading, and topology/count metadata extraction,
returning descriptors without WPF or localized presentation. The
`SemiconductorRecipeGalleryValidationWorkflow` owns one recipe's asynchronous
project load, automatic-sequence handling, deterministic dry-run, and failure
stage selection. `SemiconductorRecipeGalleryValidationSession` owns the
ordered multi-item validation loop and translates one-recipe results into
item outcomes through explicit progress/result callbacks. The
`SemiconductorRecipeGalleryViewModel` owns observable validation
progress/summary, selection, report composition, commands, localization, and the
existing smoke-facing façade. Its item validation state/projection is owned by
the concrete `SemiconductorRecipeGalleryItemViewModel` in
`ViewModel/Recipes/SemiconductorRecipeGalleryItemViewModel.cs`, while
compatibility comparison rows are owned by
`RecipePackCompatibilityComparisonItemViewModel` in
`ViewModel/Recipes/RecipePackCompatibilityComparisonItemViewModel.cs`. Both
projection owners are WPF/parent-ViewModel neutral; the project schema and
dry-run semantics are unchanged. Existing catalog/session/workflow/copy/dialog
owners are not re-split without new evidence.

Simulation scenario project mapping has a separate concrete
`SimulationScenarioProjectMapper` boundary. It owns the neutral scenario
snapshot, `SimulationDefinition` load/save mapping, legacy axis-fault
compatibility, assertion definition construction, scheduled-fault validation,
and deterministic engine-profile assembly. `SimulationWorkspaceViewModel`
retains observable authoring state, profile selection, localization, target
correction, commands, and event lifetime; its existing public mapping methods
remain compatibility facades. The mapper has no WPF or ViewModel dependency,
and the project schema and engine contract remain unchanged.

`RuntimeDefinitionApplicationWorkflow` under `ViewModel/Simulation` owns live
compilation through `MachineProjectRuntimeCompiler` and one
`ConfigureRuntimeCommand` application, with distinct compilation-rejected and
engine-rejected outcomes. `ProjectRuntimeApplicationWorkflow` owns the reentrant
runtime-application gate and accepted/rejected routing. Main retains the
applying-state facade, child restoration, runtime-dirty presentation, and the
callback that applies a project through `ProjectLifecycleCoordinator`'s session.
Initial configuration is performed by `SimulationRuntimeLoop` composed by
`SimulationSessionCoordinator`. Main's `BuildRuntimeConfiguration` remains the
adapter used by initial, child-validation, and readiness callbacks.

`ProjectDirtyState` owns the saved-content baseline and comparison transitions.
`ProjectDocumentSession`, created by `ProjectLifecycleCoordinator`, combines it
with active document/path/session/revision state and evidence serialization.
The coordinator owns save and unsaved-resolution sequencing. Main retains
`HasUnsavedChanges`/title notifications and host callbacks;
`MainWpfInteractionHost` owns focus/dispatcher mechanics. These owners preserve
project replacement, persistence, and runtime command contracts.

Scheduled-fault scenario startup has a separate
`ScheduledFaultScenarioWorkflow` boundary. It owns the ordered Reset,
condition-scenario start, automatic-run or recovery-sequence branch, and Play
transaction, including stop-condition compensation after a post-start
rejection. `SimulationScenarioExecutionCoordinator` owns the shell-facing
runtime preparation, Test Scenario Start/Stop/Replay ownership state, and
localized success/failure presentation while reusing the workspace and this
transaction workflow. `MainViewModel` retains the public Start/Stop/Replay
facades and supplies explicit mode, run-state, status, and log callbacks. Both
owners are concrete, independently testable, and WPF/parent ViewModel neutral.

Ordinary Test Scenario command orchestration has a separate
`SimulationScenarioWorkflow` boundary. It owns ordinary start, Reset-then-
start replay, and Stop-then-optional-Pause ordering, delegates scheduled-fault
start/replay to `ScheduledFaultScenarioWorkflow`, and returns neutral results.
The coordinator owns the caller-facing target/profile preparation and result
mapping; `MainViewModel` retains only the public facades and shell callbacks.
The workflow and coordinator are concrete, independently testable, and
WPF/parent ViewModel neutral.

Non-equipment simulation command presentation has a separate
`SimulationCommandPresentationDispatcher` boundary. It owns engine enqueue and
localized accepted/rejected status/log mapping for Runtime Debugger, digital
I/O, and fault commands. `MainViewModel` retains public child ViewModel
composition and applies the Runtime Debugger snapshot through an explicit
callback after enqueue. Equipment command presentation remains owned by
`EquipmentCommandDispatcher`; no generic command registry or simulation
schema change is introduced.

`CameraCommissioningViewModel` under `ViewModel/Camera` owns camera selection,
source editing/application, presentation, manual-trigger commands, acquisition
observation, and Vision evidence composition/lifetime. It creates the concrete
selection, source-application, trigger, request, participant, and evidence owners.
Main retains public binding facades and supplies immutable runtime/project/session
context, engine dispatch, monitor refresh, and Integration-context callbacks.

`CameraSelectionWorkflow` retains selected camera/recipe state, fallback rules,
recipe derivation, and ordered selection callbacks. `ManualCameraTriggerRequestFactory`
captures one request's camera, recipe, project, runtime, and evidence context.
`ManualCameraTriggerWorkflow` performs acquire/inspect/revalidate/evidence/dispatch,
using `VirtualCameraInspectionWorkflow` for source and inspection work. The
camera workspace owns availability and localized outcome presentation. Its
acquisition/preparation participants preserve cancellation and stale-session/
runtime-generation rejection before results can become current. No additional
image-buffer owner or WPF visual-tree dependency is introduced.

The Process Block to Sequence review route is owned by the
`ProcessPlanReviewViewModel`. It keeps the review step snapshot, boundary
navigation, and Return command state behind explicit callbacks.
`RecipeAuthoringWorkspace` composes this route with Sequence selection and closes
review context when the Process Block preview closes. `MainViewModel` retains
the public binding facade and cross-feature presentation callbacks, while
`SequenceEditorViewModel` and `RecipeProcessBlockViewModel` remain the data and
selection owners.

Recipe Connection project application has a separate domain boundary.
`RecipeConnectionProjectApplier` owns typed setup equality, deterministic
device lookup/creation, channel and definition copying, and process-block/
managed-timeout project mutation. It returns an explicit applied/no-change/
multiple-device result. `RecipeAuthoringWorkspace` owns composition, dry-run
exit, recipe mutation completion, Sequence refresh/navigation, managed timeout
adjustment and checkpoint completion. Main supplies current-project, shared dirty/
availability/definition/history callbacks and camera/runtime presentation integration.
Status and log sinks remain explicit. The applier is concrete, non-`partial`, and has no
WPF or parent ViewModel dependency.

Recipe Connection simulation preflight and isolated previews have a separate
`RecipeConnectionSimulationWorkflow` boundary. It owns readiness-result
presentation and the deterministic sequence-step preview and recipe dry-run
runner invocations, including their existing localized status and category log
mapping. `RecipeAuthoringWorkspace` composes this workflow with the workbench
and isolated playback. Main supplies the current project and shared
`BuildRuntimeConfiguration` adapter, retaining public facades and cross-feature
presentation callbacks. The workflow is concrete,
independently testable, and WPF/parent-ViewModel neutral; project schema,
callback contracts, and simulation semantics remain unchanged.

The axis-to-layout mapping is explicit. A `LinearStage` names an authored
`Linear` axis; its world X is its base X plus the axis delta from the authored
home. A `RotaryStage` names an authored `Rotary` axis; its world X/Y remain at
the authored position and its angle is the authored base angle plus the axis
delta from the authored home. Saving upgrades the persisted project contract to
schema `1.11`, and validation rejects a stage bound to the wrong axis kind. A
`PneumaticCylinder` names one command `DigitalOutput`, distinct Extended and
Retracted `DigitalInput` channels, exact extend/retract durations, end-feedback
delays, and stroke. A `Conveyor` names distinct Run and Reverse outputs and a
positive speed. A `Workpiece` names one conveyor in the same layout, retains its
type and inspection state, and starts fully inside and aligned with its carrier.
When linked to a wafer handler, the workpiece also owns its current transfer
state and derives current-carrier exposure and conveyor movement from that same
state.
A `DigitalSensor` names its target layout component and one
configured `DigitalInput`. The runtime evaluates deterministic inclusive
geometry and converts authored milliseconds to exact fixed-step tick counts. It
does not infer missing axes, targets, channels, or time units.

A schema `1.6` `LoadLock` device is a non-visual chamber contract that
references two distinct pneumatic-cylinder components as its outer and inner
doors. It also names distinct Evacuate/Vent outputs, Vacuum Ready/Atmosphere
Ready inputs, and exact fixed-step pump-down/vent durations. The independent
`LoadLockRuntimeState` owns the Atmosphere, PumpingDown, Vacuum, Venting, and
latched InterlockFault transitions. `DeterministicMachineLayout` only supplies
the referenced door states, applies the state owner's door permission, and
publishes its immutable snapshot. Invalid simultaneous door or pressure
requests fail closed; Reset is the explicit fault-recovery boundary. This is a
control-state model and does not model vacuum conductance or physical pressure.

A schema `1.7` `WaferHandler` device is a non-visual transfer contract. It
references two distinct linear axes, one active-layout workpiece, source-present
and gate-open inputs, Pick/Place outputs, Holding/Placed feedback inputs, and
pick/place coordinates inside both axis limits. `WaferHandlerRuntimeState`
evaluates the pick/place command edges and interlock policy, while the linked
`WorkpieceRuntimeState` is the single mutable owner of Source, Handler,
Destination, and latched InterlockFault. Pick requires both pick coordinates,
source presence, and a closed gate; place requires both place coordinates,
handler ownership, and an open gate. Unsafe, wrong-order, or simultaneous
requests publish false feedback until reset. One workpiece may be referenced by
at most one wafer handler. Source and Destination expose the authored conveyor
as the current carrier and can move with it. Handler and InterlockFault expose
no conveyor carrier and retain the last valid physical pose. Destination
reattaches at that retained pose so the existing downstream unload flow can
continue without fabricating a robot path or destination transform. Handler
feedback, handler snapshots, workpiece snapshots, and WPF presentation all read
that Workpiece-owned state; the layout no longer projects a duplicate state.
This contract does not claim physical robot motion or destination placement.

A schema `1.8` `Sorter` device with `inspectionSortRouter` is a non-visual
inspection-disposition contract. It references one configured virtual camera,
two distinct active-layout conveyors, and distinct Pass/NG routed DigitalInput
feedback. The compiler resolves the two existing conveyor Run outputs; the
contract does not duplicate those commands. Independent
`InspectionSortRouterRuntimeState` latches the first camera decision and owns
AwaitingDecision, PassReady, NgReady, PassRouted, NgRouted, and reset-only
InterlockFault transitions. A matching Run rising edge selects one route;
wrong, simultaneous, or alternate route requests fail closed with both feedback
inputs false. WPF consumes immutable sorter snapshots and does not reconstruct
route policy.

A schema `1.9` `Oht` device with `ohtHandoff` is a non-visual carrier-handoff
contract. It references one active-layout conveyor; the compiler resolves that
conveyor's existing Run/Reverse outputs. Route-available, vehicle-docked,
load-port-ready, and carrier-received DigitalInputs are explicit, as are
handoff-ready and carrier-transferred feedback. Independent
`OhtHandoffRuntimeState` owns Vehicle, Ready, Transferring, LoadPort, and
reset-only InterlockFault state. The layout orchestrator applies its forward
motion permission to the referenced conveyor but contains no handoff policy.
Premature, reverse, simultaneous, or readiness-loss requests fail closed.
After receipt, forward motion is downstream load-port transport and does not
change semantic ownership. This is a single local semantic handoff, not route
planning, multi-vehicle traffic, vendor protocol, or vehicle kinematics.

A schema `1.10` `Inspection` device with `inspectionHandoff` is a non-visual
inspection-control contract. It references one configured virtual camera, one
inspection-position DigitalInput, one result-accepted DigitalOutput, and
distinct Ready/Complete feedback inputs. Independent
`InspectionHandoffRuntimeState` owns AwaitingMaterial, Ready, Inspecting,
ResultAvailable, Complete, and reset-only InterlockFault. The existing camera
continues to own acquisition timing, correlation, and its source-neutral
decision; the handoff owner only validates material presence and request/result
ordering. This does not add pixel analysis, automatic image file I/O, an
external Vision SDK, or a second inspection engine.

A schema `1.11` `Prealigner` device with `prealigner` is a non-visual alignment-
control contract. It references one active rotary-stage component, one active
pneumatic-cylinder clamp, one wafer-present DigitalInput, one alignment-
accepted DigitalOutput, distinct Ready/Complete feedback inputs, and a finite
target angle with positive tolerance inside the rotary-axis limits. Independent
`PrealignerRuntimeState` owns AwaitingWafer, AwaitingClamp, Ready, Aligning,
Aligned, Released, and reset-only InterlockFault. Axis integration, stage pose,
clamp travel, and sensor geometry remain with their existing owners. The model
does not add notch-image physics, a vendor algorithm, or a second motion engine.

Runtime equipment is owned by independent referenced objects rather than
`DeterministicMachineLayout` partial files or nested device implementations.
`LinearStageRuntimeState`, `RotaryStageRuntimeState`,
`PneumaticCylinderRuntimeState`, `ConveyorRuntimeState`,
`WorkpieceRuntimeState`, `DigitalSensorRuntimeState`,
`LoadLockRuntimeState`, `WaferHandlerRuntimeState`,
`InspectionSortRouterRuntimeState`, `InspectionHandoffRuntimeState`,
`OhtHandoffRuntimeState`, and `PrealignerRuntimeState` own their equipment
state transitions. For a linked transfer, `WorkpieceRuntimeState` owns Material
state and `WaferHandlerRuntimeState` owns only command/interlock policy.
`DeterministicMachineLayout` only creates, orders, and coordinates those
objects. New equipment kinds should follow the same boundary: add one
owned runtime object and reference it through the layout orchestrator instead
of extending the orchestrator through partial files.

The fixed-step order is intentional: axis integration, load-lock interlock
evaluation, cylinder state/feedback, OHT handoff permission,
conveyor/workpiece transport from the Workpiece-owned carrier state,
geometry-sensor DI publication, wafer-handler evaluation, inspection-sorter
and inspection-handoff evaluation from the prior camera snapshot, pre-aligner
evaluation from the current rotary-axis/clamp/sensor state, and then the camera tick occur before
the Sequence tick. Therefore `WaitVisionResult` sees the current camera result,
while the sorter safely latches it on the following layout tick before either
branch can issue its route command; `WaitSignal` reads current-tick equipment
feedback.
Reset restores axes, cylinders, conveyors, and workpieces to authored home and
restores linked workpieces to Source on their authored conveyor,
clears sensor delay history, restores load locks to Atmosphere, returns
inspection sorters to AwaitingDecision with both route feedback inputs false,
writes inspection handoffs to AwaitingMaterial with Ready/Complete false,
writes OHT handoffs to Vehicle with readiness/transfer feedback false,
writes pre-aligners to AwaitingWafer with Ready/Complete false,
writes Retracted=true plus the appropriate pressure feedback, resets sequences and
automatic-cycle counters, and returns the clock and tick index to zero.

Before layout evaluation, the engine resolves the active command-owned fault
set. `CylinderTravelBlocked` is passed into the layout Tick so a cylinder enters
`Fault`, freezes its current progress, and resumes from that progress after the
fault is cleared. `StuckDigitalInput` is implemented as an effective-value
override in the deterministic signal hub: nominal manual/component writes keep
updating behind the override and become effective immediately on recovery.
Active faults are immutable snapshot data and emit correlated injection/clear
events. Runtime replacement and Reset clear every fault.

## Runtime and UI ownership

The WPF application owns authored editing, selection, command intent, and
presentation. **Simulation ON** asks the compiler to build the current authored
machine, applies the runtime configuration atomically, and requests automatic
execution. The UI then displays immutable snapshots and ordered events.

`App` owns only normal interactive startup and the top-level decision to enter
direct-EXE automation. `DirectExeSmokeHost` owns automation arguments, smoke
window/process lifecycle, shared WPF interaction adapters, common report
routing, and shutdown. `DirectExeFaultScenarioHost` owns the headless
fault-scenario CLI workflow. `DirectExeSmokeFailurePolicy` owns the ordered
report-failure exit-code selection. `SmokeNativeInput` owns the user32 calls,
WPF input synchronization, pointer ownership diagnostics, and the per-run
held-pointer `IDisposable` lifetime. `SmokeWindowCapture` owns Popup visual state,
Window/Popup bitmap composition, and PNG persistence. Scenario-specific report
contracts and assertions stay with their concrete smoke verifiers.
`SmokeTestScenarioRuntimeVerifier` owns deterministic condition-scenario and
scheduled-axis-fault runtime checks; the Host retains their top-level route
selection and the separate settings visual-state workflow.
`SmokeRuntimeEvidenceVerifier` is the
concrete owner for command-trace capture/export/replay, portable scenario
evidence exchange, and unified commissioning-evidence exchange. It receives
explicit visual and native-input callbacks and has no dependency on the Host;
the production runtime and evidence state remain owned by the session/workspace and
the simulation/evidence modules.
`SmokeScenarioBatchVerifier` owns the non-persistence scenario-batch smoke
sequence: deterministic batch setup, cancellation, baseline/mismatch checks,
and routing to the evidence verifiers. Project-linked batch persistence and
View-specific result-panel scrolling remain Host-owned boundaries.
  `SmokeBatchPersistenceVerifier` owns the smoke-only save/run/sidecar/reopen/
  restore verification for an explicitly supplied project path; restore-only
  flag checks and visual capture preparation remain in the Host. These smoke
  owners do not replace the production project save/open workflows or alter the
  project format.
  `DirectExeSmokeArgumentParser` owns Direct-EXE argument lookup, primitive
  parsing, camera-first-use request derivation, and cross-argument validation.
  `DirectExeSmokeOptions` owns the typed non-WPF snapshot of smoke values,
  defaults, derived flags, and window settings. `DirectExeSmokeHost` retains
  top-level smoke/fault route selection, WPF interaction, scenario ordering,
  report routing, and shutdown ownership. `DirectExeFaultScenarioHost` owns
  the headless fault-scenario argument/result workflow.
  `DirectExeSmokeFailurePolicy` owns the ordered report-failure
  exit-code selection. `SmokeNativeInput` owns user32 input and pointer
  cleanup.
  `SmokeWindowCapture` owns screenshot/Popup composition
  and output persistence.
  `DirectExeSmokeProjectLoader` owns initial blank/startup-choice/explicit-path/
  bundled-sample project materialization. `DirectExeConnectionWorkbenchWorkflow`
  owns Recipe Connection workbench preparation, named-control lookup, state
  dispatch to concrete smoke verifiers, and workbench-specific report
  persistence. `DirectExeSmokeHost` retains WPF shell creation, shared visual
  interaction setup, top-level scenario selection, common report routing, and
  shutdown selection. `SmokeNativeInput` owns native input and pointer state.
  `SmokeWindowCapture` owns Window/Popup
  screenshot composition.
  `SmokePerformanceVerifier` owns startup-to-idle, navigation, and steady
  interaction measurements plus the `SmokePerformanceReport` contract.
   `SmokeProjectRoundTripVerifier` owns the SaveReload/Reopen restored-state
   assertions and `SmokeProjectRoundTripReport` contract. `SmokeVisualTreeQuery`
   and `SmokeProjectTreeQuery` own the shared WPF visual-tree and project-tree
   queries used by those smoke scenarios. These owners do not change production
   ViewModel, project-format, or runtime behavior.
  `SmokeUiInteraction` owns the concrete visual/native-input callback contract
  shared by runtime-evidence and visual-state smoke verifiers.
  `SmokeAxisTuningStateVerifier`, `SmokeLayoutPropertyStateVerifier`,
  `SmokeEditMenuStateVerifier`, and `SmokeEvidenceDrawerStateVerifier` own the
  corresponding Direct EXE visual-state preparation workflows. The host retains
  their routing and shutdown selection; `SmokeNativeInput` owns pointer
  cleanup; `SmokeWindowCapture` owns screenshot
  composition. The overlapping
  direct-scene gesture state path documented by PL-0060.
  `SmokePickAndPlaceStateVerifier` now owns fixed-tick Pick-and-Place workpiece,
  gripper, render, and language-refresh assertions. `SmokeGlobalCommandStateVerifier`
  owns Simulation ON plus Abort/Retry command-state and lifecycle assertions;
  `SmokeUiInteraction` supplies its cursor diagnostics. The host retains the
  global-command start/fault preconditions, state routing, and shutdown.
  `SmokeNativeInput` owns pointer cleanup. `SmokeWindowCapture` owns screenshot
  composition.
  `SmokeSequenceStateVerifier` owns the
  sequence-editor focus, hover, popup, subsequence, validation, and expected-
  state checkpoint smoke preparation. The host retains the sequence route and
  shutdown; `SmokeNativeInput` owns pointer cleanup. `SmokeWindowCapture` owns
  Popup screenshot storage. Direct-scene gesture
  preparation remains host-owned under the separate PL-0060 boundary decision.
  The existing commissioning smoke verifiers also own their matching Direct
  EXE state preparation and scroll targets: `SmokeFaultManagerVerifier`,
  `SmokeCameraCommissioningVerifier`, `SmokeDigitalIoCommissioningVerifier`,
  `SmokeCylinderCommissioningVerifier`, `SmokeConveyorCommissioningVerifier`,
  and `SmokeSensorCommissioningVerifier`. Axis state preparation and scrolling
  are owned by the concrete `SmokeAxisCommissioningStateVerifier`, while
  `SmokeAxisCommissioningVerifier` owns the 64-check report verification and
  serialization path.
  `SmokeMultiAxisCommissioningStateVerifier` owns the 17 multi-axis recipe
  state-preparation paths, while `SmokeMultiAxisCommissioningVerifier` owns the
  multi-axis report verification and serialization path. `DirectExeSmokeHost`
  calls those two owners separately and retains commissioning
  argument routing and shutdown. `SmokeNativeInput` owns shared pointer
  cleanup. `SmokeWindowCapture` owns Popup screenshot composition.
  `SmokeRecipeDryRunStateVerifier` owns the Recipe Connection dry-run state
  family: deterministic dry-run execution, fault setup, isolated playback,
  timeline navigation, and dry-run control focus/hover/pressed/disabled
  preparation. It uses `SmokeUiInteraction` for native input. The host retains
  dry-run route selection, shared lifecycle, and shutdown. `SmokeNativeInput`
  owns pointer cleanup.
  `SmokeWindowCapture` owns Popup/screenshot composition.
  `SmokeProcessBlockPreparation` owns the process smoke setup snapshot, while
  `SmokeProcessBlockSequenceStateVerifier` validates the actual authored
  `process-block.*` ID set rather than a fixed bundled-sample count.
  `SmokeProcessBlockApplicationVerifier` derives the expected post-Apply and
  dry-run step count from that setup snapshot, preserving the existing PB-C2
  report keys while avoiding a stale fixture-specific literal.
  `SmokeRecipeCheckpointStateVerifier` owns its deterministic in-memory
  checkpoint and strict-linear edit fixtures; those clones are smoke-only and
  never mutate the tracked sample. `SmokeButtonPointerState` owns the shared
  focus/hover/pressed WPF protocol, including native-input retry and release
  cleanup coordination; the process-block application verifier uses the same
  helper for Apply focus/pressed states. These owners keep fixture policy and
  pointer state out of the generic host and do not change production runtime
  behavior.
  Direct EXE and integration/simulation tests share the linked
  `tests/TestSupport/TestStorage.cs` policy: D: is preferred when it has the
  required free space, while `OPENVISIONLAB_TEST_DATA_ROOT` or the TEMP path
  provides an explicit fallback when D: is full. This is test-only storage
  routing; the production exchange and evidence writers retain their own
  free-space and atomic-save guards.
  `MachineLayoutDeviceRuntimeCompiler` owns the specialized layout-device runtime
composition boundary. It receives the already selected layout's runtime
components, authored device definitions, axis definitions, channel kinds, and
fixed-step timing policy, then validates and assembles LoadLock, WaferHandler,
OHT handoff, and Prealigner runtime configuration. Its concrete
`MachineLayoutInspectionRuntimeCompiler` companion owns InspectionSortRouter
  and InspectionHandoff configuration, including inspection camera/channel
  validation and immutable result construction. `MachineLayoutRuntimeConfiguration`
  remains the public immutable runtime configuration and property owner, while
  its concrete `MachineLayoutRuntimeConfigurationValidator` companion owns
  cross-component reference checks, channel/semantic ownership invariants, and
  deterministic ordinal ordering. `MachineLayoutSignalBindingValidator`
  owns construction-time digital signal-kind checks for the runtime device
  states, while `DeterministicMachineLayout` retains single-thread tick/reset
  orchestration. `MachineLayoutRuntimeResetter` owns runtime-state reset and
  workpiece carrier initialization; the layout retains the signal-restoration
  callback that bridges reset state to the signal hub. `MachineProjectRuntimeLayoutCompiler` owns project layout
  validation, active-layout selection, and base component assembly. The concrete
  `MachineProjectRuntimeAxisCompiler` and `MachineProjectRuntimeCameraCompiler`
  own authored axis/camera conversion, including the existing legacy camera
  property path. `FixedStepDelayConverter` is the shared owner of deterministic
  milliseconds-to-ticks conversion for these compilation owners. The public
  `MachineProjectRuntimeCompiler` retains project contracts, orchestration,
  signal contract construction, dependency gating, and the public compilation
  result contract. `MachineProjectRuntimeSequenceCompiler`,
`MachineProjectRuntimeAutomaticRunCompiler`, and
`MachineProjectRuntimePickPlaceCompiler` own their respective typed project
policy conversions and have no WPF/ViewModel dependency.

`MachineLayoutDeviceRuntimeCompiler` remains the layout-device orchestration owner.
`MachineLayoutWaferHandlerRuntimeCompiler` owns the separate wafer-handler contract:
handler filtering, axis/workpiece/channel validation, soft-limit checks, and typed
runtime configuration/error construction. It has no WPF, file, network, engine, or
shared mutable state dependency; the coordinator keeps the other device-family
compilers and final `MachineLayoutRuntimeConfiguration` assembly.
The active application shell is `ShellWindow`; the retired `MainWindow`,
`WorkspaceView`, and `SimulationWorkspaceView` resources are not part of the
compiled UI path.

`FaultManagerViewModel` is a presentation adapter over that same boundary. A
source-neutral `SimulationFaultTargetCatalog` derives eligible Digital Input or
pneumatic-cylinder targets from the latest snapshot. Inject and clear actions
submit the existing typed engine commands. The ViewModel rebuilds its active
rows from `SimulationSnapshot.Faults`; it never treats its observable
collection as runtime truth. Dirty authored state disables these commands until
Simulation ON installs the validated runtime definition.

The UI does not own or calculate:

- motion integration or axis completion;
- cylinder travel, direction reversal, or end-feedback timing;
- conveyor state, direction, workpiece transport, or travel clamping;
- stage world transforms derived from runtime axes;
- sensor overlap, on/off delays, or sensor DI values;
- Sequence transitions, timeouts, or automatic repeat timing;
- fault activation, forced-input resolution, or actuator travel blocking;
- runtime reset semantics or event ordering.

Views never mutate simulation state directly. ViewModels never reconstruct
runtime truth by comparing rendered frames. Presentation refresh may discard a
stale visual snapshot. The Event Journal is a bounded latest-value projection:
the engine retains the latest 4,096 ordered events and its monotonically
increasing `EventIndex` exposes any discarded prefix, while MachineStudio shows
the latest 1,000 localized log lines.

The Sequence editor mutates only authored `SequenceDefinition` values while the
application is in Design mode. Field edits are continuously checked by the
source-neutral `SequenceCompiler`; they do not advance or patch the current
runtime. List add, delete, and reorder commands are delegated to the
WPF-neutral `SequenceDefinitionEditor` and are deliberately limited to one
strict linear success path ending in `Complete`. Explicit error/failure branches
remain visible and field-editable, but structural commands fail closed rather
than silently rewriting control flow. **Simulation ON** remains the only path
that validates and atomically replaces the runtime configuration.

`SequenceCompiler` owns the one-definition contract: authored fields, target lookup,
step parameter parsing, and typed `CompiledSequence` construction.
`SequenceCompositionValidator` owns the separate post-compilation graph contract:
sequence-id lookup, unknown `CallSubsequenceStep` targets, and cycle detection.
`MachineProjectRuntimeSequenceCompiler` and `SimulationRuntimeConfigurationBuilder`
call the validator directly; `SequenceCompiler.ValidateComposition` remains only as
a public compatibility forwarder.

Each authored Sequence carries `watchdogTimeoutMs`. Zero explicitly permits an
unlimited whole-Sequence execution for backward compatibility; a positive value
is compiled into the UI-neutral `CompiledSequence`. The
`DeterministicSequenceExecutor` compares that limit only with deterministic
simulation time, faults a still-running execution with
`SequenceWatchdogTimedOut` at the inclusive boundary, and clears the budget on
its existing Reset/repeat path. Immutable Sequence snapshots expose the
configured limit and typed error; the engine publishes the fault through its
existing ordered `SequenceFaulted` event. WPF does not run a duplicate timer.

Sequence debugging uses the same ownership boundary. `StepSequenceCommand`
asks the engine to run until exactly one transition, completion, or fault;
`SetSequenceBreakpointCommand` changes a session-only engine breakpoint set.
The engine pauses before an entered breakpoint step executes and publishes one
immutable `SequenceDebugSnapshot` plus ordered debug events. Reset retains valid
session breakpoints while clearing pending step state; atomic runtime
replacement clears them. `RuntimeDebuggerViewModel` retains the public debugger
facade and selected snapshot watches. Its concrete `RuntimeTimelineViewModel`
child owns the session-only bounded 200-event history, localized timeline-row
materialization, clear lifecycle, and timeline summary while the parent keeps
the existing `Timeline`/summary binding facade. Its concrete
`RuntimeAlarmCollectionViewModel` child owns fault/error alarm
projection, active-occurrence tracking, clear/reappear lifecycle, bounded
history, acknowledgement, and localized recovery guidance. The child receives
only explicit enabled-state, sequence-name, and status callbacks; neither owner
creates a timer or transition detector, and debugger choices are not silently
written to the project.

`RuntimeDebuggerWatchTargetCatalog` owns the WPF-neutral combined projection
and deterministic kind/name/ID ordering of Sequence, axis, signal, and
equipment watch targets from one immutable snapshot. The debugger ViewModel
retains the observable collection, identity comparison, selected-target
restoration, value formatting, and commands.

MachineStudio async commands share one small concurrency boundary.
`AsyncRelayCommand` atomically claims its own execution slot before invoking
the delegate and releases it in a `finally` path. This prevents concurrent
callers from starting the same command twice while preserving each command's
existing `CanExecute` predicate, exception routing, cancellation handling, and
cross-command policy. It is not a global lock for unrelated commands.

Deterministic command tracing uses the same boundary. The
`FixedStepSimulationEngine` identifies applied command boundaries and delegates
their synchronized in-memory storage to `DeterministicSimulationCommandTraceStore`, while
`DeterministicSimulationCommandTraceCommandCodec` owns the WPF- and
engine-neutral command argument serialization, replayability policy, and
command reconstruction. `DeterministicSimulationCommandTracePackage` owns
schema validation, semantic hashing, and JSON persistence, and
`DeterministicSimulationCommandTraceReplayRunner` owns paused replay through
the existing engine queue. MachineStudio's Run Inspector exposes only explicit
Start capture, Export, and Replay actions. `SimulationCommandTraceViewModel`
owns their capture/replay state, CanExecute predicates, status, and transitions;
`MainViewModel` retains the public binding facade, file-dialog routing, engine snapshot
projection, and unified-evidence callbacks. Neither UI owner serializes command
arguments or reconstructs runtime commands. Capture clears only the in-memory
trace, and runtime snapshot projection is guarded so derived reset/replay
corrections do not become authored project changes.

`DeterministicSignalHub` owns synchronized multi-channel lookup, revision
accounting, write ownership, and cross-channel interlock policy.
`DeterministicSignalState` owns the independent mutable state of one channel,
including nominal/effective values, digital overrides, reset, and immutable
snapshot conversion. The single-channel owner does not reference the hub or
its lock.

`MachineProjectLayoutValidator` owns layout identity, geometry, component
identity, and structural shape validation. Its concrete
`MachineProjectLayoutBehaviorBindingValidator` companion owns the explicit
axis, device, channel, and cross-component binding rules, including reference
lookup and component-kind dispatch. The two owners return the same existing
validation result contract; neither mutates the authored project, and the
structural owner does not share its duplicate-ID state or error collector with
binding policy.

Embedded Sequence steps use `DeterministicSequenceRuntimeContext` as the
simulation-to-Sequence adapter. Signal reads/writes, axis move requests and
motion-state reads, virtual-camera triggering, and vision-result reads remain
deterministic and are translated into the existing typed Sequence results.
Accepted state-changing operations publish through the engine's existing event
sequencer; the adapter does not own the engine loop or snapshot state.

Sequence debugging has a separate narrow state owner. `DeterministicSequenceDebugState`
owns enabled sequence breakpoints, pending semantic-step matching, pause
metadata, reset, and `SequenceDebugSnapshot` projection. The
`FixedStepSimulationEngine` retains command validation, pause-mode transitions,
sequence execution, event emission, and deterministic tick orchestration. The
owner is Simulation-only, non-partial, and does not reference WPF or the
engine, so debug state can be tested without constructing the runtime shell.

The configured Sequence catalog, executor catalog, and Sequence-debug lifecycle
now have one concrete `SimulationSequenceRuntime` owner. It owns configuration,
debug clearing, executor reset, and current-step projection. The
`FixedStepSimulationEngine` retains `ActiveSequenceId`, shared RunMode/
ControlOwner/PendingSteps state, cross-feature recovery policy, tick ordering,
event/snapshot publication, and the public engine contract. The owner is
Simulation-only, non-partial, and receives no WPF or hardware dependency; the
existing handler context contracts continue to receive the same read-only
catalog/debug projections.

Runtime configuration uses the same candidate/application split.
`SimulationRuntimeConfigurationBuilder` validates and materializes axes,
cameras, signals, compiled sequences/executors, automatic-run timing, layout,
and pick/place into an explicit candidate result without touching installed
engine state. `FixedStepSimulationEngine` remains the transaction owner: it
installs the candidate only after the complete build succeeds, resets related
runtime state, and emits the existing command result/event. Axis-only
configuration reuses the same axis candidate owner.

Manual commissioning command policy has a concrete Simulation owner as well.
`SimulationManualControlCommandHandler` remains the compatibility façade over an
explicit context containing the installed axes, cameras, layout, signal hub,
active faults, sequences, and command-boundary state. It routes axis,
camera, output-equipment, and input-force commands, keeps manual-session entry
and the shared result envelope, and returns the existing command result plus a
small state delta and operation-event list. The concrete
`SimulationManualAxisCommandHandler`, `SimulationManualCameraCommandHandler`,
`SimulationManualEquipmentCommandHandler`, and
`SimulationManualInputCommandHandler` own the corresponding validation and
runtime mutation policies. `FixedStepSimulationEngine` remains the owner of
authoritative installed state, command-boundary timing, event publication,
final command trace, lifecycle, and deterministic tick policy.

Run-control command policy follows the same narrow boundary. The concrete
`SimulationRunControlCommandHandler` owns Play, Pause, fixed-step, semantic
Sequence-step, and Sequence-breakpoint command transitions using explicit
run/debug/sequence context. It returns the existing command result and typed
run/control state delta plus ordered operation events. The engine remains the
owner of installed state, command dispatch and final trace/event publication,
and keeps tick-time Sequence debug pause behavior with deterministic tick
ordering.

Fault command policy follows the same explicit boundary. The concrete
SimulationFaultCommandHandler owns Inject/Clear fault validation, target
mutation, active-fault registration, recovery mutation, and ordered operation
events through a runtime context. `SimulationFaultRuntime` owns the mutable
active-fault records and exposes the existing read-only dictionary projection
to manual-control, condition, and Sequence policies. FixedStepSimulationEngine
uses the same concrete runtime for external fault commands and condition-
scenario scheduled faults; it retains condition timing/restart policy,
external final command event/trace, and snapshot/lifecycle/tick ownership.

External sequence command policy now has its own concrete boundary as well.
SimulationSequenceCommandHandler owns Start, Abort, and Retry validation,
DeterministicSequenceExecutor mutation, sequence-debug mutation, automatic-run
state deltas, and ordered Sequence/AutomaticRun operation events through an
explicit command context. FixedStepSimulationEngine applies the returned
state delta and publishes those events at the existing command boundary; it
retains authoritative state storage, automatic cycle/recovery policy, tick
execution, lifecycle, snapshot publication, and final command event/trace.

Condition-scenario start policy follows the same concrete boundary.
SimulationConditionScenarioCommandHandler owns StartConditionScenario
normalization, profile validation, runtime-target compatibility checks,
condition state-machine initialization, and ordered start/zero-duration
completion operation events through an explicit snapshot-based context.
FixedStepSimulationEngine applies the returned condition state and publishes
those events at the existing command boundary. It intentionally retains
StopConditionScenario, scheduled-fault clearing, automatic recovery, condition
tick progression, authoritative state, lifecycle, snapshots, and final
command event/trace ownership because those operations still share engine-owned
recovery state.

Automatic-run start policy is also isolated from the engine shell.
`SimulationAutomaticRunCommandHandler` owns StartAutomaticRun validation,
optional start-input mutation, sequence-executor start, automatic/run state
delta construction, and ordered I/O/Sequence/AutomaticRun operation events
through an explicit context. `FixedStepSimulationEngine` applies the returned
state and publishes those events at the established command boundary; it
retains automatic repeat, cycle completion, fault/recovery, condition
interaction, tick progression, lifecycle, snapshots, and final command
event/trace ownership.

Automatic-run cycle policy has a concrete tick-time boundary.
`SimulationAutomaticRunCycleHandler` owns repeat-delay decrement, successful
sequence restart, cycle-count transitions, and ordered cycle/restart/completion
operation events through explicit state and configuration. On a restart failure
it returns the existing fault detail; `FixedStepSimulationEngine` applies the
returned state through `SimulationAutomaticRunRuntime` and invokes its existing
`FaultAutomaticRun` policy so condition-scenario recovery context remains
engine-owned. `SimulationAutomaticRunRuntime` owns AutomaticRun configuration,
active/repeat state, cycle count, delay state, handler-state projection, and
cycle-context projection; the engine retains shared sequence/control state,
tick ordering, fault/recovery, lifecycle, snapshots, and final command
event/trace publication.

Condition scheduled-fault injection is also isolated behind a concrete
boundary. \`SimulationConditionScheduledFaultInjectionHandler\` owns injection
command construction, existing fault-handler delegation, injection/rejection
event translation, and condition-stop state outcome through explicit context.
The engine retains scenario tick calculation, command-boundary time assignment,
condition state-machine order, hold-boundary detection, and the scheduled-fault
recovery wrapper.

Multi-axis commissioning validation follows the same explicit ownership
boundary. `AsyncOperationLifetime` owns one cancellable operation's Task and
cancellation-source lifetime. `AsyncOperationParticipant<TResult>` builds on
that primitive for the repeated participant mechanics: one current typed task,
duplicate-start sharing, bounded observation, typed cancellation/timeout/failure
mapping, and disposal. Batch and multi-axis keep their domain-specific result
records and concrete participant facades; Camera preparation, Project Save's
multiple in-flight saves, and Integration's admission/state contract remain
separate owners.
`MultiAxisCommissioningViewModel` owns repeat-validation state, progress/status/result presentation, bounded history
projection, baseline comparison, stale-context presentation, and mismatch
navigation. The concrete WPF-neutral `MultiAxisCommissioningArtifactStore`
owns result/history/baseline artifact state, project-context validation, sidecar
persistence and restore, project-path relinking, and baseline deletion. The
existing Simulation package types remain the validation, JSON/hash and file-save
API authority. `Scenarios/AtomicEvidenceFile` owns their shared temporary-file
lifetime: invoke the synchronous writer, replace the destination, and clean up
that call's temporary file. Run/batch/exchange/trace/Vision/unified/multi-axis
evidence and canonical journal export use the same owner. Packages still prepare
paths/directories and retain serializer timing, encoding and journal space/size
checks. Baseline/history keep their existing internal forwarding entry point.
Core project save/backup and retention deletion have separate contracts and are
unchanged. See [PL-0265](../.proofline/evidence-file-persistence-refactor-report.md).
`MainViewModel` retains the public binding facade and supplies project/runtime,
status/logging, UI-progress, layout-selection, and presentation callbacks. The
recipe editor remains the authoring owner, while manual coordinated Run/Stop,
project mutation, and runtime compilation remain outside the child. The child
and store have no WPF visual-tree dependency and do not mutate project or
runtime state directly.

Manual multi-axis commissioning execution has a separate
`MultiAxisCommissioningExecutionWorkflow` boundary. It owns the ordered
Pause-if-needed, `StartManualControlCommand`, and `MoveAxesAbsoluteCommand`
transaction, snapshots the move targets before asynchronous dispatch, and
returns typed pause/manual-control/move rejection outcomes. `MainViewModel`
retains recipe selection and validation, command availability, `IsRunning`
state, pause-rejection logging, and the public Run/Stop command facades. The
workflow reuses the existing `EquipmentCommandDispatcher`, is concrete and
independently testable, and has no WPF or parent-ViewModel dependency.

The workflow captures one `SimulationRuntimeIdentity` from the initial snapshot
and binds all three preparation commands through `SimulationCommand.ExpectedRuntime`.
At application time, the existing engine `ApplyCommand` compares the expectation
with its owned project ID and runtime generation before any handler runs. A stale
command returns `RuntimeIdentityMismatch` through the normal completion, command
event and trace path. It must not consult the published snapshot here: an earlier
command in the same drain may already have changed the owned runtime. Null
`ExpectedRuntime` preserves unbound callers; a bound identity may contain a null
project ID. Project comparison is ordinal. Previously applied commands are not
rolled back, and PL-0263's host lifetime checks remain necessary and unchanged.

Portable command traces preserve bound commands' arguments and actual outcomes,
but mark them non-replayable. Replay must not drop the admission condition or
invent a mapping to a new session's generation. Existing unbound trace schema,
argument codec and hash rules are unchanged. See the
[PL-0264 report](../.proofline/runtime-command-admission-refactor-report.md).

Deterministic Test Scenario batch execution follows the same boundary.
`SimulationScenarioBatchViewModel` owns the sequential batch command/state
facade, progress, accepted baseline, mismatch navigation, and artifact-store
coordination; its cancellable operation lifetime is owned by the typed
`SimulationScenarioBatchParticipant`, which reuses
`AsyncOperationParticipant<SimulationScenarioBatchParticipantResult>` for the
common lifecycle mechanics. `SimulationScenarioBatchPresentation` is the
concrete WPF-neutral owner for localized read-only status, result, baseline,
artifact, and assertion display mapping. `SimulationScenarioBatchRepetitionRunner`
is the concrete WPF-neutral owner for one temporary engine's configure,
automatic/recovery start, deterministic replay, result-package creation, and
stop lifecycle. The concrete WPF-neutral `SimulationScenarioBatchArtifactStore`
owns the batch/baseline packages, project-linked sidecars, context validation,
stale rejection, path relinking, and portable evidence import/export.
`MainViewModel` remains the compatibility facade and forwards live Scenario
commands to their coordinator while supplying project/runtime context, primary engine pause/compile callbacks,
unified-evidence aggregation, and callback routing. The concrete
`SimulationEvidenceFileDialogHost` owns the six simulation evidence/trace file
dialogs; project/recipe path selection uses its dedicated dialog host. Restore
and import validate context without starting or replaying a runtime.

Vision execution evidence is composed by `CameraCommissioningViewModel`.
`VisionExecutionEvidenceViewModel` owns recorder lifetime, correlated event
completion, repeat comparison, linked sidecar persistence/restore, stale-context
state, and localized evidence presentation. The camera workspace supplies its
context and owns selection, trigger guards, and acquisition lifetime. Main
retains explicit project/runtime and dispatch callbacks, dialog-host routing,
and unified-evidence aggregation. `VirtualCameraInspectionWorkflow` owns
project-relative image acquisition, deterministic inspection, and evidence
command construction. Cancel/reset/reopen releases pending evidence without
implicitly acquiring a frame or starting a run.

The 2D Integration handoff callback has two narrow boundaries.
`MachineIntegrationRequestWorkflow` owns the explicit-context eligibility
policy, current frame extraction, and Trigger step lookup before delegating to
the existing `MachineIntegrationHandoffRequestFactory`. The factory owns
project-relative source resolution, source length/hash/frame identity
validation, and `MachineInspectionHandoffRequest` plus coordinate-projection
construction. `MainViewModel` supplies the current source context and a lazy
qualified-build identity provider. `MachineIntegrationViewModel` constructs the
request workflow and owns source-context refresh deduplication. Refresh does not
load build manifests; request admission and preparation read current context and
identity. The existing public request-callback constructor and unconditional
explicit `RefreshContext()` remain compatible. The ViewModel also retains
  dialog-result callbacks, setup state and
  persistence, Handoff/exchange publication, result-observation composition,
  and the public TCP command/property forwarding facade. Its lifetime gate
  rejects disposed Publish/Refresh command admission and suppresses late
  status, projection, and busy-state publication.
`MachineIntegrationPathReadinessPolicy` is the shared concrete owner for
trimmed path normalization, current exchange-root/recipe availability, and
full-path conversion. The ViewModel uses its snapshot for binding/status and
command admission; `MachineIntegrationRequestWorkflow` uses it for the recipe
precondition; `MachineIntegrationResultObservationWorkflow` uses it for refresh and projection path canonicalization; `MachineIntegrationResultFileWatcher` uses it before watcher
creation. Setup persistence, transaction reads, and watcher lifetime remain in
their existing owners.
`MachineIntegrationTcpControlViewModel` owns the TCP commands, listener/client
operations, setup/result presentation, transient shared-key diagnostics and
zeroization, and the `MachineIntegrationTcpWorkflow` transport resource.
`MachineIntegrationTcpOperationOwner` (PL-0267) is its WPF-neutral concrete child for
one-operation admission, current-task observation, cancellation, status/busy
callback ordering, late-publication suppression and disposal. The owner keeps
the close admission and typed `Tcp` observation contract without owning a View,
control, or transport. Disposal cancels the operation and lets the existing
completion path release its task resources.
  The parent supplies explicit settings, latest-transaction, result-refresh,
  and status callbacks; the child has no `MainViewModel`, WPF visual-tree, or
  dialog dependency. `MachineIntegrationSetupStore` is the concrete,
  independently testable owner
of the file-backed setup format, validation, atomic persistence, reset defaults,
and saved-versus-current TCP snapshot comparison. `MachineIntegrationTcpWorkflow`
owns TCP listener/client transport lifetime and transfer calls.
`MachineIntegrationSharedKeyStore` owns transient session-key parsing,
environment fallback, caller-copy creation, and deterministic zeroization; the
EXE smoke harness uses the same owner for environment-key validation.
`MachineIntegrationResultObservationWorkflow` owns file-backed transaction
discovery, acknowledgement/result/projection reads, and project filtering. Its
lifetime gate suppresses acknowledgement, result, projection, and read-error
publication after disposal, including between staged asynchronous reads.
`MachineIntegrationResultFileWatcher` owns the `FileSystemWatcher`, result-file
event subscriptions, debounced refresh scheduling, UI-dispatch handoff, and
watcher/cancellation disposal. Its lifetime gate also prevents a UI-dispatched
refresh that is still queued from starting after disposal. The observation
workflow remains its small compatibility façade for configuring that owner.
These concrete owners are concrete and independently testable, and have no
WPF, visual-tree, or parent ViewModel dependency.

The infrastructure exchange keeps its public static compatibility facade in
`MachineIntegrationExchange`. `MachineIntegrationTransactionMaintenance` owns
transaction discovery, diagnostics, stale staging cleanup, quarantine purge,
and quarantine-manifest recovery policy. The shared
`MachineIntegrationTransactionFileSystem` owner centralizes transaction paths,
message-file writes, timestamps, free-space lookup, and reparse-point safety so
normal publish/read and maintenance paths cannot drift into separate path rules.
Neither owner changes the Integration contract or starts a runtime action.

`SimulationSessionCoordinator` under `ViewModel/Simulation` creates and owns
one live engine plus `SimulationRunControlWorkflow`, `SimulationRuntimeLoop`,
`SimulationRuntimeResourceOwner`, `SimulationRuntimeShutdownWorkflow`, and
`SimulationSessionCloseWorkflow`. Main supplies project/runtime context and
presentation callbacks, retains public APIs, and delegates start/close/disposal
to the session. The session does not own authored project persistence.

`SimulationRuntimeLoop` owns Snapshot/Event readers, initial configuration,
monitor throttling, dispatch, termination observation, and cancellation/task
lifetime. `SimulationSessionCloseWorkflow` coordinates unsaved resolution and
save/camera/batch/commissioning/integration participant observation with the
runtime shutdown transaction. `SimulationRuntimeShutdownWorkflow` and
`BoundedShutdownCoordinator` retain ordered engine-stop, reader/canonical-event
drain, cancellation, completion barriers, typed diagnostics, and the one-shot
deadline. `SimulationRuntimeResourceOwner` owns safe disposal of the concrete
runtime-resource set. Main retains View/event unsubscription and maps typed
results to shell presentation. A failed deadline retains its named stage;
close-time cancellation and failure remain explicit typed results.

Runtime observability presentation has a concrete boundary. The
`RuntimeObservabilityPresenter` owns the existing bounded journal and maps
runtime events to Vision execution evidence, the Runtime Debugger timeline, and
structured diagnostics. It also maps engine termination, generic runtime logs,
and shutdown diagnostics while preserving the existing field and coordinate
contracts. `MainViewModel` retains current snapshot projection, the public
projection facade, public `LogMessages` and `OperationalDiagnostics`
compatibility, and broad WPF event/notification fan-out. Runtime shutdown
sequencing belongs to `SimulationRuntimeShutdownWorkflow`. The presenter is
non-partial, WPF-neutral, and independently tested; it is not a generic
callback registry.

Runtime snapshot calculation has a separate narrow projection owner.
`SimulationRuntimeSnapshotProjection` converts one immutable
`SimulationSnapshot` plus a neutral selection context into the selected axis,
camera, active sequence, cycle signals, run state, and runtime target-id lists.
`SimulationRuntimeProjectionCoordinator` owns the mutable application of that
projection to the runtime-facing child ViewModels, workspace option lists,
camera/manual projections, debugger, and Vision evidence completion.
`MainViewModel` retains selection-source access, public compatibility facades,
and WPF notification fan-out. Both projection owners are concrete,
independently testable, and have no WPF visual-tree dependency.

The remaining MainViewModel runtime command policy has two concrete owners.
`SimulationRunControlAdmissionPolicy` consumes the immutable
`SimulationRunControlState` snapshot and returns the seven shell-facing `Can*`
decisions without an engine, WPF, or callback dependency.
`SimulationRunControlWorkflow` keeps command construction, active-sequence
preconditions, cross-command serialization, busy admission, callbacks, and
disposal. Main keeps the public `ICommand` names and mode, runtime-definition,
status, log, and snapshot callbacks. The workflow rechecks the policy after
waiting on its gate, so a queued command cannot repeat an already completed
transition, and rechecks lifecycle admission after the gate so queued work does
not enter an engine command core after disposal is requested.

`ProjectSelectionSynchronizationWorkflow` under `ViewModel/Project` owns
ProjectTree/Layout selection subscriptions, editor instances, property-panel
synchronization, camera/sequence/recipe selection, and selected-axis projection
updates. Main supplies product-specific callbacks and facade notifications.
`LayoutAuthoringWorkspace` composes `LayoutSelectionCommandWorkflow` and
`SceneViewportInteractionWorkflow` for command parameters and neutral Scene
requests. WPF event/coordinate acquisition remains in View/Behavior.

`ManualEquipmentCommissioningViewModel` under `ViewModel/Commissioning` owns
selected manual-equipment projection, command creation, availability,
localization, close admission, and disposal invalidation. It composes
`ManualEquipmentPresentation` and `ManualControlCommandWorkflow`.
The presentation derives selected sensor/cylinder/conveyor identifiers,
force/interlock hints, and equipment gates from immutable projection facts;
axis facts for shared manual-start come from the current runtime projection.
The command workflow maps selected targets to typed equipment/camera commands.
`EquipmentCommandDispatcher` owns enqueue and accepted/rejected status/log
mapping. Main supplies selection/project/runtime facts and the accepted manual
run-state callback while retaining public binding facades. Engine interlock
and runtime truth remain authoritative; UI command availability is guidance.

The common Recipe Connection setup callback workflow is also separated from the
shell. `RecipeConnectionSetupWorkflow` dispatches the eight typed station/device/
process-block setup callbacks through `RecipeConnectionProjectApplier`, maps
applied/no-change/multiple-device outcomes to localized status and category logs,
and selects the existing setup or process-block completion callback.
`RecipeAuthoringWorkspace` supplies playback exit and completion callbacks and owns
workbench composition and the distinct managed-timeout path. Main supplies current
project and cross-feature presentation callbacks. The workflow is concrete and independently testable; project
mutation remains owned by the applier and no WPF or parent ViewModel is referenced.

Recipe Connection row projection has a separate narrow owner.
`RecipeConnectionRowCatalog` resolves the active layout, preserves component
layer ordering, and projects behavior links, related target identifiers,
validation errors, and Sequence-use metadata into the existing
`RecipeConnectionRowViewModel` rows. It is concrete, stateless, WPF-neutral,
and has no file-I/O or parent ViewModel dependency. `RecipeConnectionWorkbenchViewModel`
retains the `Rows` collection, selection synchronization, child setup
composition, preview/dry-run commands, mutation callbacks, and public
compatibility façade; it only applies the catalog's rows to the observable
collection. The existing layout fallback and localized row values remain
unchanged.

Unified commissioning evidence has its own MachineStudio lifecycle owner.
`UnifiedCommissioningEvidenceViewModel` owns aggregate artifact state, explicit
export/import transitions, context validation, JSON persistence, localized
status, and rejection/failure presentation. `MainViewModel` retains dialog-host routing, current project/runtime context,
package-input composition, imported
child-artifact application, and compatibility facade. The child is concrete
and non-`partial`; it receives neutral callbacks, has no WPF or visual-tree
dependency, and does not execute or replay a runtime while saving or restoring
an artifact.

The commissioning handoff keeps aggregation in the same neutral Simulation
boundary. `DeterministicUnifiedCommissioningEvidencePackage` composes the
portable Test Scenario exchange, command trace, and optional Vision execution
evidence, validates each nested hash plus their common project/build/fixed-step
context, and owns only the parent hash and source-path-safe import rebinding.
The command trace remains the only replayable artifact; Vision evidence is an
inspection/acquisition record. No bundle operation runs, replays, acquires, or
mutates authored project state, and the aggregate has no WPF or session-state
dependency.

MachineStudio exposes this contract through one explicit Run Inspector
file/status adapter. `UnifiedCommissioningEvidenceViewModel` owns the artifact
state, persistence, localized status, and context/rejection decisions;
`MainViewModel` supplies command availability, file-dialog routing, package inputs,
and memory-only import rebinding. `RightToolRegionView` binds the existing
semantic card and command-button styles. Export and import do not run, replay,
acquire, save, dirty, or acknowledge anything implicitly.

`SequenceStepTemplateCatalog` owns source-neutral action-to-target-kind mapping
and deterministic draft construction. MachineStudio adapts project axes,
digital channels, and camera devices into typed authoring targets and presents
only compatible choices. The catalog does not inspect WPF controls and does not
compile or execute the resulting step. A missing compatible target rejects the
draft instead of creating a placeholder identity.

`SequenceAuthoringTargetCatalog` owns the remaining project-backed target
projection boundary. It creates authoring targets from channels, axes, cameras,
and subsequences; creates expected-state targets from the active layout; filters
the selected subsequence; and builds compiler lookup targets. The concrete
catalog is WPF-neutral and returns read-only projection results.
`SequenceStepEditorCollection` owns materialized step-editor creation,
replacement, child-step event subscriptions, validation projection, and
disposal. `SequenceEditorViewModel` retains observable sequence/template state,
selection, structural editing, validation summary, localization, and the
existing public facade. Existing template-catalog and compiler contracts remain
the reusable downstream boundaries.

`SequenceDefinitionEditor.NormalizeStep` owns the source-neutral normalization
of authored step fields after an action change: compatible target fallback,
parameter-choice defaults, action-specific timeout/parameter rules, and
failure-branch cleanup. `SequenceStepEditorItem` keeps the WPF-facing property
notifications and `DefinitionChanged` event flow, then delegates the policy to
that editor. This keeps action normalization directly testable without
constructing the ViewModel or a WPF view.

`VirtualCameraInspectionTemplate` owns the smallest complete first-use camera
authoring policy: one explicit camera, recipe identifier `default`, and a
compiler-valid four-step Trigger/Wait pass/fail graph. It generates collision-
safe device and sequence identifiers and refuses to duplicate a project that
already has a camera. MachineStudio owns only the explicit command, dirty-state
refresh, selection, and navigation. The template creates no image source or
automatic run and never configures or advances the runtime.

## Dependency rules

- `Simulation` depends on `Core`, `IO`, and `Sequence`.
- `Vision` depends on `Core`; `Infrastructure` implements the Vision asset
  boundary without depending on `Simulation`.
- `MachineStudio` composes the integrated runtime and optional supporting
  modules through commands, snapshots, and adapters.
- There are no placeholder Motion, Devices, or VisionBridge projects. Authored
  device definitions belong to `Core`; runtime motion and camera state belong
  to `Simulation`; source-neutral inspection contracts belong to `Vision`;
  project-asset adapters belong to `Infrastructure`.

Forbidden:

- `Core` referencing WPF
- `Simulation` referencing ViewModel or View
- `Device` calling Dispatcher
- `ViewModel` computing axis integration or sensor geometry
- `View` directly mutating simulation state
- global mutable singletons

## Key design decisions

1. **Equipment behavior first**: The primary acceptance path is an authored
   layout whose axis-bound components, sensors, I/O, and Sequence complete and
   repeat a meaningful machine cycle. Camera/Vision joins only where inspection
   evidence is part of that cycle.
2. **Fixed-step Simulation Engine**: The engine runs on a dedicated thread with
   a fixed time step, 5 ms by default. UI rendering is independent of this
   clock.
3. **Atomic project compilation**: Authored definitions are validated and
   copied into one runtime configuration. A missing layout binding, axis,
   channel, sequence, or invalid fixed-step delay rejects the replacement
   without partially changing the current runtime.
4. **Command queue**: All runtime state changes go through a thread-safe bounded
   command queue (1,024 by default). Full queues apply asynchronous backpressure;
   commands are not dropped. Commands include identity and timing evidence for
   traceability.
5. **Immutable snapshots and ordered events**: UI reads snapshots and events;
   it never reads mutable runtime objects. Snapshot transport keeps the latest
   state, and ordered event transport keeps a finite recent window rather than
   accumulating an unbounded history.
6. **Deterministic execution**: The same project, fixed step, seed, and commands
   produce the same ordered results.
7. **MVVM strictness**: ViewModel owns UI state and commands only. Domain and
   runtime modules own machine behavior. View renders; code-behind is limited
   to presentation wiring.
8. **Content-addressed Vision evidence**: When Vision is used, frames retain
   project-relative paths, SHA-256 content identity, simulation time, and exact
   acquisition correlation. They do not use wall-clock timestamps, mutable
   pixel arrays, or random IDs as runtime identity.

Condition scheduled-fault recovery is isolated behind a concrete boundary.
`SimulationConditionScheduledFaultRecoveryHandler` owns fault clearing,
recovery-sequence restart, automatic-run resume state, and ordered fault/
recovery event translation through explicit runtime/state context. The engine
assigns command-boundary time, applies the returned state, and emits events; it
retains scheduled injection, `FaultAutomaticRun` interruption calculation,
condition timing, tick ordering, lifecycle, snapshots, and final command
event/trace publication.
Condition-scenario tick progression is now isolated behind a second concrete
boundary. SimulationConditionScenarioProgressHandler owns deterministic
state-machine advancement, executed-tick progression, transition event
description, and duration-completion state through explicit context and
outcome records. `SimulationConditionScenarioRuntime` now owns the mutable
condition profile, state machine, active state, executed ticks, last
transition, scheduled-fault active/interruption state, snapshot projection, and
Reset/Clear state. `FixedStepSimulationEngine` applies command and handler
outcomes and invokes the runtime after scheduled-fault processing; it retains
scheduled-fault timing and handler invocation, Sequence/AutomaticRun recovery
state, snapshot/lifecycle ownership, and engine tick/time identity.

Condition-scenario Stop policy is now isolated behind a concrete command
boundary. SimulationConditionScenarioStopHandler owns active-state validation,
composition of the existing scheduled-fault recovery outcome, condition stop
state, ordered recovery/stop events, and the accepted/rejected command result.
The engine applies the returned state and publishes events at the existing
command boundary; it retains authoritative state storage, final command trace,
snapshot, lifecycle, and tick ownership.

The FixedStep run-loop wall-clock policy is now isolated behind the concrete
SimulationRunLoopTiming owner. It owns only the wall-clock anchor, real-time
accumulator, catch-up budget, fixed-step remainder, and bounded delay
calculation. FixedStepSimulationEngine retains run-mode transitions,
pending-step behavior, tick execution, command completion, snapshots, events,
and lifecycle. SimulationClock continues to own deterministic simulation time;
the two timing domains remain explicit and separate.

Simulation engine lifecycle and command completion now have a separate
concrete owner, `SimulationEngineLifecycle`. It owns lifecycle transitions,
start/stop/cancellation admission, typed command rejection, pending/applied
command completion, terminal translation, output completion, and disposal.
`FixedStepSimulationEngine` retains mutable simulation state, command-reader
consumption, command policy application, deterministic tick ordering, event
content, and snapshot construction. The lifecycle owner receives only concrete
channel/store dependencies and narrow engine-context callbacks; the shared
command channel remains explicit between lifecycle admission and engine
application.

The fixed-tick physical runtime phases are now isolated behind the concrete
`SimulationPhysicalRuntimeTick` owner. It advances servo axes, the optional
machine layout, and virtual cameras in the existing axis -> layout -> camera
order and translates their transition messages through one narrow engine event
callback. `FixedStepSimulationEngine` retains condition, automatic-run,
sequence, workpiece, clock, snapshot, command, and lifecycle orchestration.
Physical component instances remain the state owners; the new owner receives an
explicit tick context and does not maintain a duplicate runtime state cache.

Immutable simulation snapshot composition is now isolated behind the concrete
`SimulationSnapshotFactory` owner. It captures the signal hub and component
snapshots, applies the existing deterministic ordering, and constructs the
public `SimulationSnapshot` through an explicit context. `FixedStepSimulationEngine`
retains mutable runtime state and derives engine-specific automatic,
condition-scenario, and sequence-debug snapshot values before delegating; no
new snapshot cache or public contract was introduced.
