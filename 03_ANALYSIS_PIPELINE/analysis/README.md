# Offline Experiment Analysis

Reproducible offline layer for loading, validating, transforming, aggregating, and exporting metrics from Multimodal AI-VR 2x2 experiment runs.

This folder is intentionally isolated from Unity runtime code. It does not open Unity, modify scenes, write to the original run folder, alter NavMesh, change robot behavior, or touch the experiment protocol.

## Setup

```powershell
python -m pip install -r analysis/requirements.txt
```

`pandas` is required for tabular analysis. `matplotlib` is optional at runtime; if it is absent, omit `--plots` and the CSV/Markdown export still works.

For plots:

```powershell
python -m pip install matplotlib
```

## Run

```powershell
python analysis/offline_experiment_analysis.py --run-dir <ruta_del_run> --out-dir analysis/output/<nombre>
```

Example:

```powershell
python analysis/offline_experiment_analysis.py --run-dir "Logs/experiments/tiago_run_20260603_230947__autonomous_demo_step22_multimodal_bridge__Arcade-FastDemo__voice_only__attempt78" --out-dir "analysis/output/tiago_run_20260603_230947"
```

Optional flags:

- `--include-invalid`: include trials with `valid_for_analysis=false` in condition aggregations.
- `--allow-red-flags`: export diagnostics even if `experiment_data_validation.json` reports red flags.
- `--validation-file <path>`: attach a specific validation JSON instead of auto-detecting it.
- `--plots`: write simple PNG charts if `matplotlib` is installed.
- `--self-test`: run a synthetic parser and metrics smoke test.

## Expected Artifacts

The run folder should contain:

- `manifest.json`
- `events.jsonl`
- `samples.csv`
- the manifest-referenced `session_trials_csv_file`
- the manifest-referenced `session_trials_jsonl_file`
- zero or more `*trial_summary.csv` / `*trial_summary.jsonl`

If available, the analysis also loads `*experiment_data_validation.json` from the run folder. If it is not there, it tries `Logs/ExperimentDataValidation/<run_id>__experiment_data_validation.json`.

## Outputs

The output folder receives:

- `trial_metrics.csv`
- `condition_metrics.csv`
- `event_counts_by_trial.csv`
- `event_counts_by_condition.csv`
- `condition_reporting_summary.csv`
- `condition_validation_report.csv`
- `condition_validation_report.json`
- `condition_validation_report.md`
- `voice_metrics_by_trial.csv`
- `reset_metrics_by_trial.csv`
- `run_integrity_report.csv`
- `run_integrity_report.md`
- `analysis_summary.md`

If events remain outside a real trial after timestamp-window attribution, the output folder also receives:

- `pretrial_or_unassigned_events.csv`

With `--plots`, it may also receive:

- `duration_by_condition.png`
- `deposited_boxes_by_condition.png`
- `voice_events_by_condition.png`
- `autonomy_requests_by_condition.png`

## Output Interpretation

`trial_metrics.csv` is the main per-trial table. It preserves invalid trials for diagnostics, but condition-level aggregations exclude them by default.

`condition_metrics.csv` aggregates only valid trials unless `--include-invalid` is passed. `voice_events_mean` uses `voice_event_count_total`, which counts all event types beginning with `voice_`, rather than only transcription and mapped-intent events.

`event_counts_by_trial.csv` contains only events attached to real trial IDs. Pretrial, postrun, uninitialized, or otherwise unassigned events are excluded from this table and exported to `pretrial_or_unassigned_events.csv` when present.

`event_counts_by_condition.csv` aggregates event counts by condition from the valid trial set by default. Use `--include-invalid` to include invalid trials in this condition export.

`condition_reporting_summary.csv` is the compact P42B condition-level report. It includes run and trial counts, valid/invalid trials, round completeness, correct picks/places, warnings/errors, aborts/manual stops, robot and voice intervention evidence, voice blocked/rejected/executed counts, P41 `LocalPickCost` / `robot_to_pickup_cost` evidence, diagnostic pick+place benchmark evidence, invalid pick+place runtime policy detections, and startup-alignment soft-exit diagnostics.

`condition_validation_report.csv`, `.json`, and `.md` contain aggregate condition rules for the final C00/C10/C11 matrix. C00 rejects operational robot activity and executed voice commands, C10 permits autonomous robot activity but rejects executed voice commands, and C11 permits both. `log_context=voice_only` is treated as legacy diagnostic metadata when stronger condition fields are present.

`voice_metrics_by_trial.csv` separates voice-specific counters:

- total `voice_` events;
- listening start/stop events;
- VAD events;
- transcription, normalization, intent mapping, routing, feedback, pending confirmation, and condition-blocked events;
- time to first voice event and first voice command when timestamps allow it.

Missing event types are reported as `0`; missing timing evidence is left blank.

`reset_metrics_by_trial.csv` separates reset traceability:

- reset started/applied/completed/failed counters;
- `reset_consistent`, inferred conservatively.

`reset_consistent=false` is reported when reset failures are observed. If the validator exposes explicit per-trial consistency evidence, it is used. Otherwise the value remains `unknown` instead of inventing consistency.

`run_integrity_report.csv` and `run_integrity_report.md` summarize validation status, red flags, warnings, parser diagnostics, unassigned events, invalid trials, detected conditions, and whether the run is eligible for the main analysis.

## Validity Policy

Trials with `valid_for_analysis=false` are preserved in `trial_metrics.csv` and diagnostic exports, but excluded from `condition_metrics.csv` unless `--include-invalid` is passed.

Condition attribution is resolved canonically in this order: explicit `condition_id`, `condition_log_context`, explicit `robot_enabled` / `voice_enabled` flags, trial/session summary fields, enriched manifest fields, then legacy `log_context` or folder-name fallback. Legacy recovery emits warnings; explicit contradictions or irrecoverable condition absence are reported as validation failures.

Validator warnings are acceptable when they document known non-fatal issues, for example terminal timestamp resets, run-level samples, duplicated idempotent reset events, or the known metric sufficiency warning for non-instrumented box fall/incidents.

Validator red flags mean the run is not eligible for the main analysis. By default the script stops before export when red flags exist. Use `--allow-red-flags` only for diagnostic exports.

Accepted warnings do not invalidate the run automatically. Red flags always block the main export unless `--allow-red-flags` is supplied.

## Notebook

The reproducible entry point is `analysis/offline_experiment_analysis.py`. If a notebook is needed, create it from this script or import the modules directly:

```python
from analysis.src.experiment_loader import load_experiment_run
from analysis.src.experiment_metrics import build_trial_metrics, build_condition_metrics

data = load_experiment_run("Logs/experiments/<run_folder>")
trial_metrics_df, event_counts_df = build_trial_metrics(data)
condition_metrics_df = build_condition_metrics(trial_metrics_df)
```
