# Simulation Time Model

## Fixed Step

The simulation uses a fixed time step of 5 ms (200 Hz). This is a virtual time interval, not a guarantee of OS scheduling accuracy.

## Run Modes

- **Paused**: State computation is stopped.
- **SingleStep**: Exactly one fixed tick or one sequence step is executed.
- **RealTime**: Wall-clock time is scaled by the configured Time Scale.
- **FastForward/Max**: Batch execution without sleep, with periodic yield.

## Time Scale

Available time scales: x0.1, x0.5, x1.0, x2.0, x10.0, Max.

## Rules

- The model's `deltaTime` is always the fixed step.
- Catch-up ticks are capped to avoid unbounded work after a stall.
- Overrun is logged, never hidden by skipping physical time.
- No hard real-time claims.
