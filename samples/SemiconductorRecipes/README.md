# Semiconductor Equipment Recipe Pack

These ten projects are editable Machine Studio equipment programs. Open any
`.ovmachine` file, add or reposition equipment on the Design canvas, change its
explicit axis/device/channel/target connections in Properties, save a copy, and
select **Simulation ON** when ready. Opening a project never starts simulation.

| File | Equipment case | Topology | Distinctive connected flow |
| --- | --- | --- | --- |
| `01-FoupLoadPort.ovmachine` | FOUP load port | 1 axis, 2 sensors, 1 cylinder, 1 conveyor, 1 OHT handoff, 14 steps | route/vehicle readiness and vehicle-to-load-port carrier ownership |
| `02-CassetteMapper.ovmachine` | Cassette mapping | 2 axes, 3 sensors, 1 cylinder, 1 conveyor, 15 steps | mapper-head lift and slot-reference confirmation |
| `03-WaferPrealigner.ovmachine` | Wafer prealignment | 2 axes, 2 sensors, 1 cylinder, 1 conveyor, 1 pre-aligner, 17 steps | wafer-present clamp, rotary target acceptance, and safe release |
| `04-WaferOcrInspection.ovmachine` | OCR handoff | 2 axes, 3 sensors, 1 cylinder, 1 conveyor, 1 camera, 1 inspection handoff, 21 steps | position-gated camera request, result, and explicit acceptance |
| `05-LoadLockEntry.ovmachine` | Load-lock entry | 1 axis, 2 sensors, 2 cylinders, 1 conveyor, 1 load lock, 22 steps | outer-door/inner-door exclusion with deterministic pump-down and vent |
| `06-SpinCoatTrack.ovmachine` | Spin-coat transfer | 3 axes, 2 sensors, 1 cylinder, 1 conveyor, 16 steps | spin-chuck rotation and dispense-height positioning |
| `07-DevelopTrack.ovmachine` | Developer track | 1 axis, 3 sensors, 2 cylinders, 1 conveyor, 17 steps | dispense-zone confirmation and nozzle-guard cycle |
| `08-DryEtchTransfer.ovmachine` | Dry-etch handoff | 2 axes, 3 sensors, 2 cylinders, 1 conveyor, 1 wafer handler, 27 steps | source-to-handler-to-chamber ownership with gate interlock |
| `09-CmpTransfer.ovmachine` | CMP handoff | 3 axes, 2 sensors, 2 cylinders, 1 conveyor, 20 steps | head lift, platen rotation, and head-load cycle |
| `10-MetrologySorter.ovmachine` | Metrology sorting | 1 axis, 3 sensors, 1 cylinder, 2 conveyors, 1 camera, 1 sorter, 21 steps | camera PASS/NG disposition with mutually exclusive route feedback |

Every project retains the shared station roles required by the Connections
workbench, then adds only the process-specific axes, sensors, cylinders,
conveyors, and workpieces shown above. Every added axis is targeted by both a
move and completion wait; every added cylinder and conveyor is commanded in
both active and safe states. All ten automatic sequences compile and complete
one bounded fixed-step cycle while exercising every declared axis, cylinder,
sensor, and workpiece.

Prealigner, spin-chuck, and CMP-platen angle motions use connected
`RotaryStage` components driven by `Rotary` axes in degrees. The runtime keeps
each stage's authored X/Y position fixed and evaluates
`authored angle + axis position - home position` on every deterministic tick.
The recipes therefore exercise vendor-neutral rotary kinematics as well as
authoring and control flow; they still do not model semiconductor process
physics or vendor hardware behavior.

The Wafer Prealignment project uses the schema `1.11` `Prealigner` device
contract. It references the existing process-position sensor, clamp cylinder,
and rotary-stage component plus explicit Ready, accepted, and Complete signals.
The independent owner advances AwaitingWafer -> AwaitingClamp -> Ready ->
Aligning -> Aligned -> Released. Unsafe rotation, early acceptance, wafer loss,
clamp release during alignment, or a second rotation latches InterlockFault
until Reset. The target and tolerance prove deterministic rotary control only;
they do not detect a physical notch or invoke a vendor algorithm.

Each automatic sequence retains at least five representative step-end checks
for the primary cylinder, process sensor, process axis, and conveyor, while
process-specific waits may add their own equipment checks. The Connections
workspace shows the actual coverage for the selected recipe before dry run;
opening the project or viewing the coverage never starts simulation.

For another connected recipe, choose **Configure representative checks** in the
Connections workspace. Machine Studio previews the five equipment/step
boundaries it can derive from the current links, keeps existing checks intact,
and changes the recipe only after **Apply** is selected. Save the project to
retain the applied checks.

These projects validate equipment layout, binding, timing, sequence, and state
flow. They do not model semiconductor chemistry, vacuum conductance, thermal
behavior, plasma, polishing removal rate, overlay, yield, or production PLC
integration.

The FOUP Load Port project uses the schema `1.9` `Oht` device contract. Its
handoff references the existing transport conveyor, load-position sensor, and
door-ready feedback plus explicit route-available, vehicle-docked,
handoff-ready, and carrier-transferred inputs. The independent owner advances
Vehicle -> Ready -> Transferring -> LoadPort and blocks conveyor motion while
latched in InterlockFault. The model proves one deterministic vehicle-to-port
handoff; it does not plan routes, coordinate multiple vehicles, implement a
vendor protocol, or simulate vehicle kinematics.

The Wafer OCR Inspection project uses the schema `1.10` `Inspection` device
contract. Its handoff references the existing inspection-position sensor and
one virtual camera plus explicit Ready, result-accepted, and Complete signals.
The independent owner advances AwaitingMaterial -> Ready -> Inspecting ->
ResultAvailable -> Complete and latches invalid ordering until Reset. The
camera retains acquisition timing and the authored placeholder decision; the
handoff does not inspect pixels, load image files during automatic Sequence
execution, or call an external Vision SDK.

The Load Lock Entry project uses the schema `1.6` `LoadLock` device contract.
Its chamber references the existing outer-door and slit-valve cylinders plus
explicit Evacuate, Vent, Vacuum Ready, and Atmosphere Ready channels. The
runtime latches an interlock fault if both doors are requested together, a door
is requested on the wrong pressure side, or pressure transition commands are
invalid. Reset returns the chamber to Atmosphere with both door actuators
forced toward Retracted. The model proves deterministic control sequencing; it
does not calculate pressure, conductance, leak rate, or pump performance.

The Dry Etch Transfer project uses the schema `1.7` `WaferHandler` device
contract. Its two-axis pick/place positions, source-present and gate-open
conditions, commands, feedback, and semantic wafer owner are explicit. Invalid
order, simultaneous commands, or an unsafe handoff latch a fail-closed fault
until reset. This is local deterministic transfer ownership, not robot-path,
collision, 3D, or vendor-hardware simulation.

The Metrology Sorter project uses the schema `1.8` `Sorter` device contract.
Its virtual camera defaults to NG so the normal bounded recipe exercises the
sort-output conveyor and confirmation sensor; changing the authored placeholder
decision to PASS executes the existing success branch and primary conveyor.
The independent route owner latches the first decision and rejects wrong,
simultaneous, or alternate route commands until Reset. This proves local
deterministic disposition routing, not production image classification, yield,
or physical cross-conveyor wafer transfer.
