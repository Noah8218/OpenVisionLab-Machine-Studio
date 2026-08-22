# UI Performance Smoke Checklist (Template)

Purpose:
Use this checklist for every completed UI task before marking its pull request complete.

## 0) Global performance rule (mandatory)

- Any UI work is considered incomplete until this checklist is filled and the smoke
  performance script passes with acceptable limits.
- Baseline source must always be the latest manually accepted UI/perf result
  (the run that was marked PASS in this same checklist).
- Metrics are measured as the worst observed run for the case.
- Baseline update rule:
  - run this script with `-CreateBaseline` after a task pass review,
  - commit the generated baseline JSON with evidence,
  - do not refresh baseline for unrelated platform or environment changes.
- Default regression limits:
  - Mean: baseline mean * 1.25
  - p95: baseline p95 * 1.40
- Baseline invalid condition defaults:
  - non-positive baseline metric,
  - baseline jitter ratio > 0.15,
  - missing baseline case for resolution/dpi,
  - missing baseline file.

## 1) Run metadata
- Date:
- Operator:
- Branch / Commit:
- UI change ticket or scope:
- Environment:
  - Machine:
  - OS:
  - GPU:
  - CPU:
  - Thermal mode:
  - Background workload:
  - Screen scale:
  - Locale/timezone:
- Execution command/path:

## 2) Scenario definition
- Representative interaction path:
  1.
  2.
  3.
- Steady-state interaction path:

## 3) Baseline values (latest accepted)
Record baseline (latest accepted run) for each metric and each resolution.
`Mean` is `run_mean` and `P95` is `run_p95` from the latest accepted baseline.

| Resolution | Metric | Baseline Mean (ms) | Baseline P95 (ms) | Mean Limit (x1.25) | P95 Limit (x1.40) |
| --- | --- | ---: | ---: | ---: | ---: |
| 1920x1040 | startupToIdleMeanMs | | | =Mean*1.25 | |
| 1920x1040 | startupToIdleP95Ms | | |  | =P95*1.40 |
| 1920x1040 | navigationMeanMs | | | =Mean*1.25 | |
| 1920x1040 | navigationP95Ms | | |  | =P95*1.40 |
| 1920x1040 | steadyInteractionMeanMs | | | =Mean*1.25 | |
| 1920x1040 | steadyInteractionP95Ms | | |  | =P95*1.40 |
| 1280x760 | startupToIdleMeanMs | | | =Mean*1.25 | |
| 1280x760 | startupToIdleP95Ms | | |  | =P95*1.40 |
| 1280x760 | navigationMeanMs | | | =Mean*1.25 | |
| 1280x760 | navigationP95Ms | | |  | =P95*1.40 |
| 1280x760 | steadyInteractionMeanMs | | | =Mean*1.25 | |
| 1280x760 | steadyInteractionP95Ms | | |  | =P95*1.40 |

## 4) Initial validation (minimum required, 2 runs each)
Run each resolution and metric at least twice. Use worst-case for pass/fail.

| Resolution | Metric | Run1 | Run2 | Worst | Against baseline mean | Against baseline p95 | Pass? |
| --- | --- | ---: | ---: | ---: | --- | --- | --- |
| 1920x1040 | startupToIdle | | | | | | |
| 1920x1040 | navigation | | | | | | |
| 1920x1040 | steadyInteraction | | | | | | |
| 1280x760 | startupToIdle | | | | | | |
| 1280x760 | navigation | | | | | | |
| 1280x760 | steadyInteraction | | | | | | |

## 5) Recheck when threshold fails (2-run)
If any initial metric exceeds limits, run 2 recheck runs under the same environment and recalc.

| Resolution | Metric | Run1 | Run2 | Worst | Mean | P95 | jitter |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1920x1040 | startupToIdleMeanMs | | | | | | |
| 1920x1040 | startupToIdleP95Ms | | | | | | |
| 1920x1040 | navigationMeanMs | | | | | | |
| 1920x1040 | navigationP95Ms | | | | | | |
| 1920x1040 | steadyInteractionMeanMs | | | | | | |
| 1920x1040 | steadyInteractionP95Ms | | | | | | |
| 1280x760 | startupToIdleMeanMs | | | | | | |
| 1280x760 | startupToIdleP95Ms | | | | | | |
| 1280x760 | navigationMeanMs | | | | | | |
| 1280x760 | navigationP95Ms | | | | | | |
| 1280x760 | steadyInteractionMeanMs | | | | | | |
| 1280x760 | steadyInteractionP95Ms | | | | | | |

`jitter = (maxObserved - minObserved) / meanObserved`
Acceptable jitter: <= 0.15 for mean and p95.

## 6) Decision
- [ ] PASS: all metrics within threshold (or recheck passed).
- [ ] FAIL: persistent breach after 2-run recheck, low jitter.
- [ ] BASELINE INVALID: baseline values are non-positive, missing, or show invalid jitter pattern.
- [ ] Artifact links:

## 6.5) Baseline invalid handling

- If `BASELINE INVALID` is selected, do not declare task complete.
- Required action:
  1) investigate environment changes (GPU/driver/thermal/background/remote session),
  2) rerun 2-run recheck and validate jitter (<= 0.15) and threshold behavior,
  3) only then refresh baseline with `-CreateBaseline` and rerun once for confirmation.
- Baseline invalid conditions are:
  - baseline file missing or unreadable,
  - baseline metric `<= 0`,
  - baseline jitter ratio above `0.15`,
  - baseline case missing required `runsSummary` sections for comparison,
  - environment mismatch (OS/driver/monitor scale changes not recorded).

## 7) Evidence
- Raw perf logs:
- Screenshots / captures:
- Environment snapshot (if changed):
- Notes:
