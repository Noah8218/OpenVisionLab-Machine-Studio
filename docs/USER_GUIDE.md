# Machine Studio user guide

[English README](../README.md) · [한국어 README](../README.ko.md)

Use the six workspaces to configure a machine, check its behavior and inspect recorded runs.

## Equipment setup

![Equipment setup with the equipment hierarchy, scene and configuration panel](images/equipment-setup.png)

Create stations and units, add devices, and configure their placement and
connections. The equipment tree and scene share the same selection. Top and
oblique views show the same layout; the oblique view is a schematic equipment
view rather than a CAD or contact-physics model.

## Process and actions

![Process and actions with the sequence overview and step settings](images/process-and-actions.png)

Review sequences as a step table or flow overview, including branches and
connections between units. Add an individual axis, camera, DI/DO, subsequence,
unit, transfer or workpiece feed/eject step by explicitly selecting its target.
Names and IDs remain visible so identically named devices can be distinguished.

Step settings use an explicit **Apply** action. Position teaching and step
targets are separate: reviewing a teaching position does not move the axis;
copying its numerical value changes the step draft until it is applied. Adding,
deleting and reordering steps currently requires a supported linear sequence.

## Capture and inspection

![Capture and inspection with the main image and camera settings](images/capture-and-inspection.png)

Select a virtual camera, configure its image source and acquisition settings,
then acquire an image explicitly. The main image remains separate from the
scrolling settings. Preparation details distinguish the selected recipe from
the recipe, image ID and result used by the current acquisition.

Supported paths include project image files and numerical 2D/height data.
External inspection requires a compatible inspection application and connection
settings. Built-in deterministic judgments support control-flow testing;
they do not establish inspection accuracy or provide a production AI model.

## Simulation

![Simulation with the equipment scene and runtime diagnostics](images/simulation.png)

Start the authored machine, pause and resume it, advance a tick or sequence
step, and reset it to inspect a control problem. Read axis, signal, device and
sequence state beside the equipment scene. The simulation uses a fixed 5 ms
tick; this is simulation time, not a hard real-time scheduling guarantee.

End the run before changing equipment settings. Pausing retains the current
run; it does not apply edited definitions to that run.

## Experiments and data

![Experiments and data with camera variation settings and the applied configuration summary](images/experiments-and-data.png)

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

![Result analysis with recorded acquisitions and comparison controls](images/result-analysis.png)

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
[ten semiconductor equipment recipes](../samples/SemiconductorRecipes/README.md).
These model equipment control and inspection handoffs, not semiconductor
process physics. Basic local simulation requires no cloud account.
