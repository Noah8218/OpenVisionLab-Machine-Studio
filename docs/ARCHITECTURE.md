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

The wall-clock accumulator, catch-up cap, pause/reset alignment, and real-time
delay calculation are owned by `SimulationRunLoopTiming`. The engine retains
run-mode policy and fixed-tick execution; it resets or aligns the timing owner
at the same lifecycle boundaries so simulation-time behavior remains
deterministic and independently testable.

## Authoring-to-runtime flow

```text
MachineProjectDocument
  layouts + activeLayoutId
  axes + devices + channels + sequences
  automaticRun policy
        |
        v
MachineProjectRuntimeCompiler
  validates identities, bindings, channel kinds, delays, and fixed-step units
  selects the active layout
  emits immutable runtime Axis, Layout, IO, Sequence, and AutomaticRun config
        |
        v
FixedStepSimulationEngine (single runtime owner)
  1. integrate Axis state
  2. apply the Axis delta to each bound stage transform
  3. evaluate load-lock pressure commands and door interlocks
  4. read the allowed cylinder command DO, advance deterministic travel, and publish
     authored delayed Extended/Retracted DI
  5. read conveyor Run/Reverse DO and transport bound workpieces along the
     conveyor's authored local axis
  6. evaluate sensor/target geometry and authored on/off delay ticks
  7. write geometry-sensor Digital Input state and emit transitions
  8. advance automatic-repeat state and the embedded Sequence
        |
        v
WaitSignal observes the current tick's sensor DI
  -> Sequence motion/output decision
  -> immutable SimulationSnapshot + non-dropping ordered events
```

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
pick/place coordinates inside both axis limits. Independent
`WaferHandlerRuntimeState` owns Source, Handler, Destination, and latched
InterlockFault state. Pick requires both pick coordinates, source presence, and
a closed gate; place requires both place coordinates, handler ownership, and an
open gate. Unsafe, wrong-order, or simultaneous requests publish false feedback
until reset. One workpiece may be referenced by at most one wafer handler. After
each handler evaluation, the layout projects the owner id and ownership state
onto that workpiece's immutable snapshot; this projection does not transition
state or reinterpret axis coordinates as a robot path. WPF renders the projected
Source/Handler/Destination/Fault evidence and does not reconstruct transfer
policy. The workpiece remains governed by its authored conveyor geometry, so
this contract does not claim physical robot motion or destination placement.

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
`OhtHandoffRuntimeState`, and `PrealignerRuntimeState` each own their
state transition. `DeterministicMachineLayout` only creates, orders, and coordinates
those objects. New equipment kinds should follow the same boundary: add one
owned runtime object and reference it through the layout orchestrator instead
of extending the orchestrator through partial files.

The fixed-step order is intentional: axis integration, load-lock interlock
evaluation, cylinder state/feedback, OHT handoff permission,
conveyor/workpiece transport,
geometry-sensor DI publication, wafer-handler evaluation, inspection-sorter
and inspection-handoff evaluation from the prior camera snapshot, pre-aligner
evaluation from the current rotary-axis/clamp/sensor state, and then the camera tick occur before
the Sequence tick. Therefore `WaitVisionResult` sees the current camera result,
while the sorter safely latches it on the following layout tick before either
branch can issue its route command; `WaitSignal` reads current-tick equipment
feedback.
Reset restores axes, cylinders, conveyors, and workpieces to authored home,
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
window placement, scripted interactions, captures, reports, and exit codes.
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
stale visual snapshot, while the separate Event Journal path remains
non-dropping.

The Sequence editor mutates only authored `SequenceDefinition` values while the
application is in Design mode. Field edits are continuously checked by the
source-neutral `SequenceCompiler`; they do not advance or patch the current
runtime. List add, delete, and reorder commands are delegated to the
WPF-neutral `SequenceDefinitionEditor` and are deliberately limited to one
strict linear success path ending in `Complete`. Explicit error/failure branches
remain visible and field-editable, but structural commands fail closed rather
than silently rewriting control flow. **Simulation ON** remains the only path
that validates and atomically replaces the runtime configuration.

`SequenceStepTemplateCatalog` owns source-neutral action-to-target-kind mapping
and deterministic draft construction. MachineStudio adapts project axes,
digital channels, and camera devices into typed authoring targets and presents
only compatible choices. The catalog does not inspect WPF controls and does not
compile or execute the resulting step. A missing compatible target rejects the
draft instead of creating a placeholder identity.

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
4. **Command queue**: All runtime state changes go through a thread-safe command
   queue. Commands include identity and timing evidence for traceability.
5. **Immutable snapshots and ordered events**: UI reads snapshots and events;
   it never reads mutable runtime objects.
6. **Deterministic execution**: The same project, fixed step, seed, and commands
   produce the same ordered results.
7. **MVVM strictness**: ViewModel owns UI state and commands only. Domain and
   runtime modules own machine behavior. View renders; code-behind is limited
   to presentation wiring.
8. **Content-addressed Vision evidence**: When Vision is used, frames retain
   project-relative paths, SHA-256 content identity, simulation time, and exact
   acquisition correlation. They do not use wall-clock timestamps, mutable
   pixel arrays, or random IDs as runtime identity.
