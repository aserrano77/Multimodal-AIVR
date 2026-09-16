from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import pandas as pd

from .condition_resolution import apply_condition_resolution_to_frame, resolve_condition_from_row


@dataclass
class JsonLineError:
    path: Path
    line_number: int
    message: str
    raw_line: str


@dataclass
class ExperimentRunData:
    run_dir: Path
    manifest_path: Path
    events_path: Path
    events_paths: list[Path]
    samples_path: Path
    samples_paths: list[Path]
    session_trials_csv_path: Path | None
    session_trials_jsonl_path: Path | None
    trial_summary_csv_paths: list[Path]
    trial_summary_jsonl_paths: list[Path]
    validation_path: Path | None
    manifest: dict[str, Any]
    validation: dict[str, Any] | None
    runs_df: pd.DataFrame
    events_df: pd.DataFrame
    samples_df: pd.DataFrame
    session_trials_df: pd.DataFrame
    trial_summaries_df: pd.DataFrame
    jsonl_errors: list[JsonLineError] = field(default_factory=list)
    csv_warnings: list[str] = field(default_factory=list)


def load_experiment_run(run_dir: str | Path, validation_path: str | Path | None = None) -> ExperimentRunData:
    run_path = Path(run_dir).expanduser().resolve()
    if not run_path.exists() or not run_path.is_dir():
        raise FileNotFoundError(f"Run directory does not exist: {run_path}")

    manifest_path = _find_one(run_path, "*manifest.json", "manifest.json")
    manifest = _read_json(manifest_path)

    events_path = _resolve_manifest_file(run_path, manifest, "events_file", "*events*.jsonl")
    events_paths = _resolve_manifest_files(run_path, manifest, "events_file", "*events*.jsonl")
    samples_path = _resolve_manifest_file(run_path, manifest, "samples_file", "*samples*.csv")
    samples_paths = _resolve_manifest_files(run_path, manifest, "samples_file", "*samples*.csv")
    session_trials_csv = _resolve_manifest_file_optional(run_path, manifest, "session_trials_csv_file", "*session_trials.csv")
    session_trials_jsonl = _resolve_manifest_file_optional(run_path, manifest, "session_trials_jsonl_file", "*session_trials.jsonl")

    trial_summary_csv_paths = sorted(run_path.glob("*trial_summary.csv"))
    trial_summary_jsonl_paths = sorted(run_path.glob("*trial_summary.jsonl"))

    validation_file = _find_validation(run_path, manifest, validation_path)
    validation = _read_json(validation_file) if validation_file else None

    events_df, event_errors = read_jsonl_many(events_paths)
    samples_df, csv_warnings = read_csv_many(samples_paths)

    if session_trials_csv:
        session_trials_df, session_warnings = read_csv_tolerant(session_trials_csv)
        csv_warnings.extend(session_warnings)
    elif session_trials_jsonl:
        session_trials_df, session_errors = read_jsonl(session_trials_jsonl)
        event_errors.extend(session_errors)
    else:
        session_trials_df = pd.DataFrame()

    trial_summaries = []
    for path in trial_summary_csv_paths:
        df, warnings = read_csv_tolerant(path)
        csv_warnings.extend(warnings)
        if not df.empty:
            df["_source_file"] = str(path)
            trial_summaries.append(df)
    if not trial_summaries and trial_summary_jsonl_paths:
        for path in trial_summary_jsonl_paths:
            df, errors = read_jsonl(path)
            event_errors.extend(errors)
            if not df.empty:
                df["_source_file"] = str(path)
                trial_summaries.append(df)
    trial_summaries_df = pd.concat(trial_summaries, ignore_index=True, sort=False) if trial_summaries else pd.DataFrame()

    events_df = normalize_event_frame(events_df, session_trials_df)
    samples_df = normalize_time_columns(samples_df)
    session_trials_df = normalize_time_columns(session_trials_df)
    trial_summaries_df = normalize_time_columns(trial_summaries_df)
    events_df = apply_condition_resolution_to_frame(events_df, manifest, run_path)
    session_trials_df = apply_condition_resolution_to_frame(session_trials_df, manifest, run_path)
    trial_summaries_df = apply_condition_resolution_to_frame(trial_summaries_df, manifest, run_path)
    samples_df = apply_condition_resolution_to_frame(samples_df, manifest, run_path)
    events_df = assign_events_to_session_attempts(events_df, session_trials_df)
    runs_df = build_runs_df(run_path, manifest, validation, events_df, samples_df, session_trials_df)
    if not runs_df.empty:
        runs_df["jsonl_parse_error_count"] = len(event_errors)
        runs_df["csv_warning_count"] = len(csv_warnings)
        runs_df["analysis_eligible"] = runs_df["analysis_eligible"].fillna(False).astype(bool) & (len(event_errors) == 0) & (len(csv_warnings) == 0)

    return ExperimentRunData(
        run_dir=run_path,
        manifest_path=manifest_path,
        events_path=events_path,
        events_paths=events_paths,
        samples_path=samples_path,
        samples_paths=samples_paths,
        session_trials_csv_path=session_trials_csv,
        session_trials_jsonl_path=session_trials_jsonl,
        trial_summary_csv_paths=trial_summary_csv_paths,
        trial_summary_jsonl_paths=trial_summary_jsonl_paths,
        validation_path=validation_file,
        manifest=manifest,
        validation=validation,
        runs_df=runs_df,
        events_df=events_df,
        samples_df=samples_df,
        session_trials_df=session_trials_df,
        trial_summaries_df=trial_summaries_df,
        jsonl_errors=event_errors,
        csv_warnings=csv_warnings,
    )


def read_jsonl(path: Path) -> tuple[pd.DataFrame, list[JsonLineError]]:
    rows: list[dict[str, Any]] = []
    errors: list[JsonLineError] = []
    with path.open("r", encoding="utf-8-sig") as handle:
        for line_number, raw_line in enumerate(handle, start=1):
            stripped = raw_line.strip()
            if not stripped:
                continue
            try:
                obj = json.loads(stripped)
            except json.JSONDecodeError as exc:
                errors.append(JsonLineError(path, line_number, str(exc), raw_line.rstrip("\n")))
                continue
            if not isinstance(obj, dict):
                errors.append(JsonLineError(path, line_number, "JSONL row is not an object", raw_line.rstrip("\n")))
                continue
            row = dict(obj)
            row["_line_number"] = line_number
            payload = row.get("payload")
            row["payload_json"] = json.dumps(payload, ensure_ascii=False, sort_keys=True) if payload is not None else ""
            if isinstance(payload, dict):
                for key, value in payload.items():
                    flat_key = f"payload_{key}"
                    if flat_key not in row:
                        row[flat_key] = _json_scalar(value)
            rows.append(row)
    return pd.DataFrame(rows), errors


def read_jsonl_many(paths: list[Path]) -> tuple[pd.DataFrame, list[JsonLineError]]:
    frames: list[pd.DataFrame] = []
    errors: list[JsonLineError] = []
    for path in paths:
        frame, path_errors = read_jsonl(path)
        errors.extend(path_errors)
        if not frame.empty:
            frame["_source_file"] = str(path)
            frames.append(frame)
    return (pd.concat(frames, ignore_index=True, sort=False) if frames else pd.DataFrame()), errors


def read_csv_tolerant(path: Path) -> tuple[pd.DataFrame, list[str]]:
    warnings: list[str] = []
    try:
        df = pd.read_csv(path, encoding="utf-8-sig", engine="python", on_bad_lines="warn")
    except UnicodeDecodeError:
        df = pd.read_csv(path, encoding="latin-1", engine="python", on_bad_lines="warn")
        warnings.append(f"{path}: read with latin-1 fallback")
    except pd.errors.ParserError as exc:
        warnings.append(f"{path}: parser error with python engine: {exc}")
        df = pd.read_csv(path, encoding="utf-8-sig", engine="python", on_bad_lines="skip")
        warnings.append(f"{path}: skipped malformed CSV lines")
    return df, warnings


def read_csv_many(paths: list[Path]) -> tuple[pd.DataFrame, list[str]]:
    frames: list[pd.DataFrame] = []
    warnings: list[str] = []
    for path in paths:
        frame, path_warnings = read_csv_tolerant(path)
        warnings.extend(path_warnings)
        if not frame.empty:
            frame["_source_file"] = str(path)
            frames.append(frame)
    return (pd.concat(frames, ignore_index=True, sort=False) if frames else pd.DataFrame()), warnings


def normalize_event_frame(events_df: pd.DataFrame, session_trials_df: pd.DataFrame) -> pd.DataFrame:
    events_df = normalize_time_columns(events_df)
    if events_df.empty:
        return events_df
    for field in ["trial_id", "trial_index", "condition_id", "condition_name", "session_id", "round_id"]:
        payload_field = f"payload_{field}"
        if field not in events_df.columns and payload_field in events_df.columns:
            events_df[field] = events_df[payload_field]
        elif field in events_df.columns and payload_field in events_df.columns:
            events_df[field] = events_df[field].where(events_df[field].notna() & (events_df[field] != ""), events_df[payload_field])
    if "trial_id" not in events_df.columns:
        events_df["trial_id"] = pd.NA
    if session_trials_df.empty or "timestamp_wall_dt" not in events_df.columns:
        return events_df
    trials = normalize_time_columns(session_trials_df)
    required = {"trial_id", "timestamp_start_dt", "timestamp_end_dt"}
    if not required.issubset(trials.columns):
        return events_df
    missing = events_df["trial_id"].isna() | (events_df["trial_id"].astype(str) == "")
    for _, trial in trials.dropna(subset=["timestamp_start_dt", "timestamp_end_dt"]).iterrows():
        mask = (
            missing
            & (events_df["timestamp_wall_dt"] >= trial["timestamp_start_dt"])
            & (events_df["timestamp_wall_dt"] <= trial["timestamp_end_dt"])
        )
        for field in ["trial_id", "trial_index", "condition_id", "condition_name", "session_id", "round_id"]:
            if field in trial.index:
                events_df.loc[mask, field] = trial[field]
    return events_df


def normalize_time_columns(df: pd.DataFrame) -> pd.DataFrame:
    if df.empty:
        return df
    df = df.copy()
    aliases = {
        "runId": "run_id",
        "participantId": "participant_id",
        "sessionId": "session_id",
        "trialId": "trial_id",
        "trialIndex": "trial_index",
        "conditionId": "condition_id",
        "conditionName": "condition_name",
        "terminalState": "terminal_state",
        "failureReason": "failure_reason",
        "totalDurationSeconds": "total_duration_seconds",
        "timestampStart": "timestamp_start",
        "timestampEnd": "timestamp_end",
        "validForAnalysis": "valid_for_analysis",
        "robotEnabled": "robot_enabled",
        "voiceEnabled": "voice_enabled",
        "assistanceMode": "assistance_mode",
        "roundId": "round_id",
        "expectedBoxes": "expected_boxes",
        "depositedBoxes": "deposited_boxes",
        "errorCount": "error_count",
        "nonTerminalWarningCount": "non_terminal_warning_count",
        "nonTerminalWarnings": "non_terminal_warnings",
    }
    for source, target in aliases.items():
        if source in df.columns and target not in df.columns:
            df[target] = df[source]
    for col in ["run_elapsed_time", "timestamp_unity", "total_duration_seconds"]:
        if col in df.columns:
            df[col] = pd.to_numeric(df[col], errors="coerce")
    timestamp_map = {
        "timestamp_wall": "timestamp_wall_dt",
        "timestamp_start": "timestamp_start_dt",
        "timestamp_end": "timestamp_end_dt",
        "trial_start_time": "trial_start_time_dt",
        "trial_end_time": "trial_end_time_dt",
    }
    for src, dst in timestamp_map.items():
        if src in df.columns:
            df[dst] = pd.to_datetime(df[src], errors="coerce", utc=True)
    return df


def assign_events_to_session_attempts(events_df: pd.DataFrame, session_trials_df: pd.DataFrame) -> pd.DataFrame:
    """Map repeated logical trial ids to the unique session-index row for each run attempt.

    Runtime event payloads retain ``trial_003`` after a saved-exit restart while the
    append-only session index disambiguates the second attempt as
    ``trial_003_dup01``.  The raw id is retained in ``source_trial_id`` and only the
    analytical event view is remapped.
    """
    required = {"run_id", "trial_id"}
    if events_df.empty or session_trials_df.empty or not required.issubset(events_df.columns) or not required.issubset(session_trials_df.columns):
        return events_df

    events = events_df.copy()
    trials = session_trials_df.copy()
    events["source_trial_id"] = events["trial_id"]
    trials["_logical_trial_id"] = trials["trial_id"].map(_logical_trial_id)
    lookup: dict[tuple[str, str], dict[str, Any]] = {}
    for _, trial in trials.iterrows():
        run_id = str(trial.get("run_id", "") or "").strip()
        logical_id = str(trial.get("_logical_trial_id", "") or "").strip()
        if not run_id or not logical_id:
            continue
        lookup[(run_id, logical_id)] = {
            "trial_id": str(trial.get("trial_id", "") or ""),
            "start": trial.get("timestamp_start_dt"),
            "end": trial.get("timestamp_end_dt"),
        }

    first_prepare_by_run: dict[str, Any] = {}
    if "event_type" in events.columns and "timestamp_wall_dt" in events.columns:
        prepare_events = events[
            events["event_type"].fillna("").astype(str).eq("experiment_trial_prepare_started")
        ].dropna(subset=["timestamp_wall_dt"])
        if not prepare_events.empty:
            first_prepare_by_run = prepare_events.groupby("run_id")["timestamp_wall_dt"].min().to_dict()

    for index, event in events.iterrows():
        run_id = str(event.get("run_id", "") or "").strip()
        event_time = event.get("timestamp_wall_dt")
        first_prepare = first_prepare_by_run.get(run_id)
        if pd.notna(event_time) and pd.notna(first_prepare) and event_time < first_prepare:
            events.at[index, "trial_id"] = pd.NA
            if "trial_index" in events.columns:
                events.at[index, "trial_index"] = 0
            if "round_id" in events.columns:
                events.at[index, "round_id"] = ""
            continue
        logical_id = _logical_trial_id(event.get("trial_id"))
        match = lookup.get((run_id, logical_id))
        if match is None:
            continue
        start = match["start"]
        end = match["end"]
        if pd.notna(event_time) and pd.notna(start) and event_time < start - pd.Timedelta(seconds=2):
            events.at[index, "trial_id"] = pd.NA
            continue
        if pd.notna(event_time) and pd.notna(end) and event_time > end + pd.Timedelta(seconds=2):
            events.at[index, "trial_id"] = pd.NA
            continue
        events.at[index, "trial_id"] = match["trial_id"]
    return events


def _format_persisted_condition_order(manifest: dict[str, Any]) -> str:
    """Return the authoritative condition order persisted by the application.

    ``condition_order_ids`` is the canonical field; ``condition_order`` is kept as
    a read-compatibility fallback for sessions written before the fields were
    unified. The value is flattened to a space-separated string so it survives the
    CSV round trip.
    """
    for key in ("condition_order_ids", "condition_order", "active_condition_plan"):
        value = manifest.get(key)
        if isinstance(value, (list, tuple)) and value:
            return " ".join(str(item) for item in value)
        if isinstance(value, str) and value.strip():
            return value.strip()
    return ""


def build_runs_df(
    run_dir: Path,
    manifest: dict[str, Any],
    validation: dict[str, Any] | None,
    events_df: pd.DataFrame,
    samples_df: pd.DataFrame,
    session_trials_df: pd.DataFrame,
) -> pd.DataFrame:
    warnings = validation.get("warnings", []) if validation else []
    red_flags = validation.get("red_flags", []) if validation else []
    resolution = resolve_condition_from_row({}, manifest=manifest, run_dir=run_dir)
    session_history_status = resolve_session_history_status(events_df)
    row = {
        "run_id": manifest.get("run_id") or (validation or {}).get("run_id") or run_dir.name,
        "run_dir": str(run_dir),
        "participant_id": manifest.get("participant_id"),
        "session_id": manifest.get("session_id"),
        "questionnaire_code": manifest.get("questionnaire_code"),
        "questionnaire_code_scheme": manifest.get("questionnaire_code_scheme"),
        "persisted_condition_order": _format_persisted_condition_order(manifest),
        "session_history_status": session_history_status,
        "scene": manifest.get("scene") or (validation or {}).get("scene"),
        "condition_id": resolution.condition_id,
        "condition_name": resolution.condition_name,
        "robot_enabled": resolution.robot_enabled,
        "voice_enabled": resolution.voice_enabled,
        "assistance_mode": resolution.assistance_mode,
        "condition_resolution_source": resolution.source,
        "condition_resolution_warnings": "; ".join(resolution.warnings),
        "condition_resolution_errors": "; ".join(resolution.errors),
        "timestamp": manifest.get("timestamp"),
        "timestamp_utc": manifest.get("timestamp_utc"),
        "event_count": len(events_df),
        "sample_count": len(samples_df),
        "event_source_file_count": events_df.get("_source_file", pd.Series(dtype=str)).dropna().astype(str).nunique(),
        "sample_source_file_count": samples_df.get("_source_file", pd.Series(dtype=str)).dropna().astype(str).nunique(),
        "trials_detected_count": len(session_trials_df) if not session_trials_df.empty else 0,
        "validation_status": (validation or {}).get("status"),
        "validation_red_flag_count": len(red_flags),
        "validation_warning_count": len(warnings),
        "analysis_eligible": len(red_flags) == 0 and session_history_status in {"", "completed"},
    }
    return pd.DataFrame([row])


def resolve_session_history_status(events_df: pd.DataFrame) -> str:
    if events_df.empty or "event_type" not in events_df.columns:
        return ""
    completed = events_df[events_df["event_type"].fillna("").astype(str) == "experiment_session_completed"]
    if completed.empty:
        return ""
    last = completed.iloc[-1]
    for column in ["payload_session_history_status", "session_history_status"]:
        value = str(last.get(column, "") or "").strip().lower()
        if value and value not in {"nan", "none", "null"}:
            return value
    return ""


def _resolve_manifest_file(run_dir: Path, manifest: dict[str, Any], key: str, pattern: str) -> Path:
    path = _resolve_manifest_file_optional(run_dir, manifest, key, pattern)
    if path is None:
        raise FileNotFoundError(f"Could not find required {key} in {run_dir} using pattern {pattern}")
    return path


def _resolve_manifest_file_optional(run_dir: Path, manifest: dict[str, Any], key: str, pattern: str) -> Path | None:
    value = manifest.get(key)
    candidates: list[Path] = []
    if value:
        candidates.extend([Path(value), run_dir / str(value)])
    path_key = key.replace("_file", "_path")
    if manifest.get(path_key):
        candidates.append(Path(manifest[path_key]))
    for candidate in candidates:
        if candidate.exists():
            return candidate.resolve()
        local = run_dir / candidate.name
        if local.exists():
            return local.resolve()
    matches = sorted(run_dir.glob(pattern))
    return matches[0].resolve() if matches else None


def _resolve_manifest_files(run_dir: Path, manifest: dict[str, Any], key: str, pattern: str) -> list[Path]:
    primary = _resolve_manifest_file_optional(run_dir, manifest, key, pattern)
    matches = sorted(path.resolve() for path in run_dir.glob(pattern))
    if primary is not None and primary not in matches:
        matches.append(primary)
    if not matches:
        raise FileNotFoundError(f"Could not find required {key} in {run_dir} using pattern {pattern}")
    return sorted(set(matches))


def _find_one(run_dir: Path, pattern: str, label: str) -> Path:
    matches = sorted(run_dir.glob(pattern))
    if not matches:
        raise FileNotFoundError(f"Could not find {label} in {run_dir}")
    if len(matches) > 1:
        p45f = [path for path in matches if path.name == "session_manifest.json"]
        if p45f:
            return p45f[0].resolve()
        preferred = [path for path in matches if path.name == label]
        if preferred:
            return preferred[0].resolve()
    return matches[0].resolve()


def _find_validation(run_dir: Path, manifest: dict[str, Any], override: str | Path | None) -> Path | None:
    if override:
        path = Path(override).expanduser().resolve()
        if not path.exists():
            raise FileNotFoundError(f"Validation file does not exist: {path}")
        return path
    local = sorted(run_dir.glob("*experiment_data_validation.json"))
    if local:
        return local[0].resolve()
    run_id = manifest.get("run_id")
    if run_id:
        repo_candidate = Path.cwd() / "Logs" / "ExperimentDataValidation" / f"{run_id}__experiment_data_validation.json"
        if repo_candidate.exists():
            return repo_candidate.resolve()
    return None


def _read_json(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8-sig") as handle:
        value = json.load(handle)
    if not isinstance(value, dict):
        raise ValueError(f"Expected JSON object in {path}")
    return value


def _json_scalar(value: Any) -> Any:
    if isinstance(value, (str, int, float, bool)) or value is None:
        return value
    return json.dumps(value, ensure_ascii=False, sort_keys=True)


def _logical_trial_id(value: Any) -> str:
    if value is None or pd.isna(value):
        return ""
    return re.sub(r"_dup\d+$", "", str(value).strip(), flags=re.IGNORECASE)
