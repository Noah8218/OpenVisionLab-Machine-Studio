# Vision Integration Boundary

## Current contract

Machine Studio treats vision acquisition and inspection as source-neutral,
deterministic evidence. The fixed-step simulation owns timing and correlation;
the image source owns project-asset identity; an inspection runner owns only
the declared recipe evaluation.

```text
Virtual Camera trigger
  -> VirtualAcquisitionContext
  -> IVirtualImageSource
  -> VirtualFrameDescriptor
  -> IVisionInspectionRunner
  -> VisionRunResult
```

The Main Layout Run manual-acquisition path now bridges the runtime camera and
the project image source without performing file I/O inside the fixed-step
engine tick:

```text
authored singleImageSource
  -> hash project asset while Paused
  -> IVisionInspectionRunner outside the fixed-step Tick
  -> TriggerVirtualCameraCommand(frame + inspection evidence)
  -> authoritative runtime camera snapshot/events
```

The UI predicts the next deterministic acquisition ID, hashes the secured
project-relative asset, runs the existing deterministic inspection runner, then
submits both correlated descriptors through one engine command. The engine
validates that acquisition, camera, recipe, and frame identifiers match its
next acquisition and atomically owns the evidence, exposure/transfer progress,
result, Reset, and ordered events. If the paused Tick, time, camera ordinal, or
owner changes while the file is read or inspected, the UI discards the
prepared evidence instead of triggering with stale context.

Automatic sequence triggers retain their existing tick-only path and do not
perform file I/O or invoke the runner. Their explicitly authored placeholder
decision remains unchanged. For the current manual mock adapter, that same
authored decision supplies the selected recipe's explicit deterministic
judgment; the published inspection identity, judgment, message, and metrics
come from `IVisionInspectionRunner` rather than being synthesized by the
camera runtime.

## Acquisition evidence

`VirtualAcquisitionContext` requires:

- acquisition, camera, and recipe identifiers;
- simulation tick and simulation time;
- deterministic run seed;
- an ordinally sorted, deep-copied axis-position map.

It contains no wall-clock timestamp. Non-finite axis positions and negative
simulation time are rejected.

## Frame evidence

`VirtualFrameDescriptor` retains:

- exact acquisition, camera, recipe, and frame correlation;
- simulation tick/time, seed, and axis positions;
- normalized project-relative source path;
- uppercase content SHA-256 and content length;
- declared width, height, and pixel format.

The descriptor does not retain a mutable pixel buffer or a WPF image object.
Consumers that need pixels must open the identified asset for the bounded
duration of their operation and validate its content identity.

## Project-relative single image source

`ProjectRelativeSingleImageSource` is the first `IVirtualImageSource`.
It receives an explicit project root and an authored relative asset path. It:

- rejects rooted paths, parent traversal, and paths resolving outside the
  project root;
- checks symbolic-link targets before opening the file;
- rejects missing and empty files;
- streams the file through SHA-256 without retaining the image bytes;
- preserves the exact acquisition context in the returned descriptor;
- respects cancellation before and during file hashing.

Example:

```csharp
var context = new VirtualAcquisitionContext(
    acquisitionId: "cam1/frame/00000001",
    cameraId: "cam1",
    recipeId: "presence-check",
    simulationTick: 120,
    simulationTime: TimeSpan.FromMilliseconds(600),
    seed: 1001,
    axisPositions: new Dictionary<string, double> { ["x"] = 100.0 });

IVirtualImageSource source = new ProjectRelativeSingleImageSource(
    projectRoot,
    "assets/inspection-part.raw",
    width: 640,
    height: 480,
    pixelFormat: "Mono8");

var frame = await source.AcquireAsync(context, cancellationToken);
```

## Deterministic mock inspection

`DeterministicMockVisionInspectionRunner` requires an explicit
recipe-to-judgment map. It does not infer OK/NG from a file name, clock, random
value, or source ordering. Its inspection ID is a versioned SHA-256 identity
derived from the canonical recipe and frame evidence. Results retain exact
acquisition, camera, recipe, and frame identifiers, plus a stably ordered,
read-only metric map.

Unmapped recipes and recipe/frame correlation mismatches fail explicitly.
Undefined or `None` judgments are rejected.

The simulation module does not reference the Vision module. It receives a
simulation-owned, immutable `VirtualCameraInspectionEvidence` value through
the manual trigger command, validates its exact correlation, and deep-copies
its finite metrics in ordinal key order. The completed camera result retains
that evidence, and `VisionResultReady` publishes the same inspection ID,
judgment, and metrics through the existing ordered Event Journal path.

## Verification

The current verification totals and artifact paths are maintained in
the current release notes. The focused evidence-package
tests prove canonical repeat equality, first context mismatch classification,
atomic save/load, hash tamper rejection, and correlated event recording.

## Current manual commissioning surface

The existing Main Layout Run camera card exposes selected camera and authored
recipe, immutable state, exact remaining exposure/transfer ticks, decision,
acquisition ID, project source, and full content SHA-256. `Start manual`,
`Pause`, `Trigger`, `Step`, and `Reset` reuse the simulation command queue and
existing Event Journal. Reopening a project restores only the authored source
and recipe; it does not restore or start a runtime acquisition.

The same card now owns one explicit image-source setup workflow:

1. Save or open the project so it has an asset root.
2. Select a file inside that project root.
3. Enter positive width and height plus a non-empty pixel format such as
   `Mono8`.
4. Select `Apply settings`, then save the project.
5. Start manual control, pause, and select `Trigger` separately.

Invalid or project-external paths never mutate the authored camera. `Revert edits`
restores the last applied values. Applying and reopening settings do not
advance the Tick, create frame evidence, or start acquisition. The focused
`CameraImageSourceEditorViewModel` owns draft state, secure path validation,
and authored source mutation; the shell only coordinates camera selection,
project save/reopen, and runtime Trigger.

The concrete sample source is
`samples/VisionInspectionCell/assets/presence-check.pgm`, declared as a `16 x
12` `Mono8` single-image source. Its accepted content SHA-256 is
`F4531B8121B4C1F2591967525FEE4E50E90FBDED3590DB9CFC44755B16A84AA3`.

## Project-linked single-execution evidence

The selected-camera Run card presents the inspection ID, runner message,
ordinal metrics, short evidence hash, persistence status, and comparison
status. After `VisionResultReady`, `DeterministicVisionExecutionRecorder`
combines the project content hash, assembly informational build identity,
fixed-step timing, camera/recipe/frame/inspection correlation, condition and
fault state, final camera snapshot, and normalized ordered events. The atomic
sidecar is saved as `<project>.vision-result.json`.

Opening a project restores only an integrity-valid package whose project,
build, selected camera, and recipe still match. Restoration never triggers the
camera. A subsequent manual execution is compared with the restored package;
the result is either an exact match or the first classified mismatch. Changing
and then restoring the project context visibly invalidates and revalidates the
evidence without executing the task.

## Next boundary

This completes the bounded local single-image Vision commissioning slice. A
full Vision Workspace, pixel preview, generalized recipe editor, automatic
sequence file I/O, Vision batch execution, 3D view, or external camera SDK
remains out of scope. The next repository decision is release-candidate
readiness and versioning, not expansion into those platforms.
