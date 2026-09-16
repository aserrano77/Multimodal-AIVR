from __future__ import annotations

from collections.abc import Iterable
import re
from typing import Any

import pandas as pd

from .condition_resolution import CANONICAL_CONDITIONS
from .experiment_loader import ExperimentRunData


TRIAL_METRIC_COLUMNS = [
    "run_id",
    "session_id",
    "trial_id",
    "source_trial_id",
    "canonical_trial_id",
    "event_count",
    "trial_index",
    "condition_id",
    "condition_name",
    "raw_condition_id",
    "condition_resolution_source",
    "condition_resolution_warnings",
    "condition_resolution_errors",
    "robot_enabled",
    "voice_enabled",
    "assistance_mode",
    "terminal_state",
    "success",
    "aborted",
    "valid_for_analysis",
    "superseded_by_restart",
    "analytic_exclusion_reason",
    "failure_reason",
    "error_count",
    "non_terminal_warning_count",
    "non_terminal_warnings",
    "trial_start_time",
    "trial_end_time",
    "trial_duration_s",
    "round_id",
    "expected_boxes",
    "deposited_boxes",
    "classification_correct_count",
    "classification_error_count",
    "voice_event_count_total",
    "voice_listening_started_count",
    "voice_listening_stopped_count",
    "voice_vad_event_count",
    "voice_transcription_count",
    "voice_command_normalized_count",
    "voice_intent_mapped_count",
    "voice_routing_count",
    "voice_feedback_count",
    "voice_confirmation_pending_count",
    "voice_condition_blocked_count",
    "voice_blocked_by_condition_count",
    "voice_command_executed_count",
    "voice_command_allowed_count",
    "voice_command_rejected_count",
    "autonomy_request_count",
    "assisted_task_count",
    "assistance_blocked_count",
    "autonomous_selection_policy_evaluated_count",
    "autonomous_selection_candidate_selected_count",
    "p41_local_pick_cost_event_count",
    "p41_robot_to_pickup_metric_event_count",
    "pick_place_benchmark_diagnostic_count",
    "pick_place_runtime_invalid_count",
    "startup_alignment_soft_exit_count",
    "pick_succeeded_count",
    "place_succeeded_count",
    "manipulation_started_count",
    "manipulation_completed_count",
    "navigation_started_count",
    "navigation_completed_count",
    "robot_pose_reset_completed_count",
    "robot_pose_reset_failed_count",
    "time_to_first_voice_event_seconds",
    "time_to_first_voice_command_seconds",
    "time_to_first_autonomy_request_seconds",
    "time_to_first_assistance_seconds",
    "reset_started_count",
    "reset_applied_count",
    "reset_completed_count",
    "reset_failed_count",
    "reset_consistent",
    "validation_status",
    "validation_warning_count",
    "validation_red_flag_count",
    "warnings",
    "red_flags",
]


EVENT_RULES = {
    "classification_correct_count": ["round_correct_deposit_registered"],
    "classification_error_count": [
        "round_incorrect_deposit_registered",
        "round_wrong_deposit_registered",
        "classification_error",
    ],
    "voice_transcription_count": [
        "voice_transcription",
        "voice_transcription_received",
        "voice_text_recognized",
        "speech_recognized",
    ],
    "voice_command_normalized_count": ["voice_command_normalized", "voice_text_normalized"],
    "voice_intent_mapped_count": ["voice_intent_mapped"],
    "voice_routing_count": ["voice_routing_started", "voice_routing_completed", "voice_command_routed"],
    "voice_feedback_count": ["voice_feedback", "voice_feedback_started", "voice_feedback_completed"],
    "voice_confirmation_pending_count": ["voice_confirmation_pending"],
    "voice_condition_blocked_count": ["voice_command_blocked_by_condition", "voice_condition_blocked"],
    "voice_blocked_by_condition_count": ["voice_command_blocked_by_condition", "voice_condition_blocked"],
    "voice_command_executed_count": ["voice_command_execution"],
    "voice_command_allowed_count": ["voice_command_allowed_by_condition"],
    "voice_command_rejected_count": ["voice_command_rejected", "voice_command_rejected_diagnostic"],
    "autonomy_request_count": ["autonomy_request_submitted", "autonomy_request_received_by_adapter"],
    "assisted_task_count": ["assisted_task_submitted"],
    "assistance_blocked_count": ["robot_assistance_blocked_by_condition", "assisted_selection_blocked_by_condition"],
    "autonomous_selection_policy_evaluated_count": ["autonomous_selection_policy_evaluated"],
    "autonomous_selection_candidate_selected_count": ["autonomous_selection_candidate_selected"],
    "startup_alignment_soft_exit_count": ["startup_alignment_soft_exit_after_no_progress"],
    "pick_succeeded_count": ["manipulation_pick_succeeded"],
    "place_succeeded_count": ["manipulation_place_succeeded"],
    "manipulation_started_count": ["manipulation_pick_requested", "manipulation_place_requested"],
    "manipulation_completed_count": ["manipulation_pick_succeeded", "manipulation_place_succeeded"],
    "navigation_started_count": ["navigation_to_pick_started", "navigation_to_place_started"],
    "navigation_completed_count": ["navigation_to_pick_succeeded", "navigation_to_place_succeeded"],
    "robot_pose_reset_completed_count": ["robot_pose_reset_completed"],
    "robot_pose_reset_failed_count": ["robot_pose_reset_failed"],
    "reset_started_count": ["experiment_trial_reset_started"],
    "reset_applied_count": ["experiment_trial_reset_applied", "robot_pose_reset_applied", "round_reset"],
    "reset_completed_count": ["experiment_trial_reset_completed", "robot_pose_reset_completed"],
    "reset_failed_count": ["experiment_trial_reset_failed", "robot_pose_reset_failed"],
}


VOICE_METRIC_COLUMNS = [
    "trial_id",
    "voice_event_count_total",
    "voice_listening_started_count",
    "voice_listening_stopped_count",
    "voice_vad_event_count",
    "voice_transcription_count",
    "voice_command_normalized_count",
    "voice_intent_mapped_count",
    "voice_routing_count",
    "voice_feedback_count",
    "voice_confirmation_pending_count",
    "voice_condition_blocked_count",
    "time_to_first_voice_event_seconds",
    "time_to_first_voice_command_seconds",
]


RESET_METRIC_COLUMNS = [
    "trial_id",
    "reset_started_count",
    "reset_applied_count",
    "reset_completed_count",
    "reset_failed_count",
    "reset_consistent",
]


def build_trial_metrics(data: ExperimentRunData) -> tuple[pd.DataFrame, pd.DataFrame]:
    base = _base_trial_frame(data)
    event_counts = event_counts_by_trial(data.events_df)
    if base.empty:
        trial_metrics = event_counts.copy()
    else:
        trial_metrics = base.merge(event_counts, on="trial_id", how="left")
    timed = time_to_first_event_metrics(data.events_df, base)
    if not trial_metrics.empty:
        trial_metrics = trial_metrics.merge(timed, on="trial_id", how="left")

    for column in [*EVENT_RULES.keys(), "voice_event_count_total", "voice_listening_started_count", "voice_listening_stopped_count", "voice_vad_event_count"]:
        if column not in trial_metrics.columns:
            trial_metrics[column] = 0
        trial_metrics[column] = trial_metrics[column].fillna(0).astype("int64")
    for column in [
        "p41_local_pick_cost_event_count",
        "p41_robot_to_pickup_metric_event_count",
        "pick_place_benchmark_diagnostic_count",
        "pick_place_runtime_invalid_count",
    ]:
        if column not in trial_metrics.columns:
            trial_metrics[column] = 0
        trial_metrics[column] = trial_metrics[column].fillna(0).astype("int64")

    trial_metrics["deposited_boxes"] = _first_existing_numeric(
        trial_metrics,
        ["deposited_boxes", "deposited_box_count", "classification_correct_count"],
        default=trial_metrics["classification_correct_count"],
    )
    trial_metrics["expected_boxes"] = _first_existing_numeric(
        trial_metrics,
        ["expected_boxes", "expected_box_count", "target_box_count"],
        default=(trial_metrics["deposited_boxes"] + trial_metrics["classification_error_count"]).clip(lower=1),
    )

    validation = data.validation or {}
    warnings = validation.get("warnings", []) or []
    red_flags = validation.get("red_flags", []) or []
    trial_metrics["validation_status"] = validation.get("status")
    trial_metrics["validation_warning_count"] = len(warnings)
    trial_metrics["validation_red_flag_count"] = len(red_flags)
    trial_metrics["warnings"] = "; ".join(map(str, warnings[:20]))
    trial_metrics["red_flags"] = "; ".join(map(str, red_flags))
    trial_metrics["reset_consistent"] = infer_reset_consistency(trial_metrics, validation)

    for column in TRIAL_METRIC_COLUMNS:
        if column not in trial_metrics.columns:
            trial_metrics[column] = pd.NA
    trial_metrics = trial_metrics[TRIAL_METRIC_COLUMNS]
    return trial_metrics.sort_values(["trial_index", "trial_id"], na_position="last"), event_counts


def build_condition_metrics(trial_metrics_df: pd.DataFrame, include_invalid: bool = False) -> pd.DataFrame:
    if trial_metrics_df.empty:
        return pd.DataFrame()
    df = trial_metrics_df.copy()
    if not include_invalid and "valid_for_analysis" in df.columns:
        df = df[df["valid_for_analysis"].map(_as_bool).fillna(False)]
    if df.empty:
        return pd.DataFrame(
            columns=[
                "condition_id",
                "condition_name",
                "valid_trial_count",
                "success_rate",
                "duration_mean_s",
                "duration_median_s",
                "deposited_boxes_mean",
                "voice_events_mean",
                "autonomy_requests_mean",
                "condition_blocks_mean",
                "assisted_tasks_mean",
                "assistance_blocked_mean",
            ]
        )
    for column in [
        "trial_duration_s",
        "deposited_boxes",
        "voice_transcription_count",
        "voice_event_count_total",
        "voice_intent_mapped_count",
        "voice_condition_blocked_count",
        "autonomy_request_count",
        "assisted_task_count",
        "assistance_blocked_count",
    ]:
        df[column] = pd.to_numeric(df[column], errors="coerce").fillna(0)
    df["success_bool"] = df["success"].map(_as_bool).fillna(False).astype(int)
    df["voice_events_total"] = df["voice_event_count_total"]
    grouped = df.groupby(["condition_id", "condition_name"], dropna=False)
    out = grouped.agg(
        valid_trial_count=("trial_id", "count"),
        success_rate=("success_bool", "mean"),
        duration_mean_s=("trial_duration_s", "mean"),
        duration_median_s=("trial_duration_s", "median"),
        deposited_boxes_mean=("deposited_boxes", "mean"),
        voice_events_mean=("voice_events_total", "mean"),
        autonomy_requests_mean=("autonomy_request_count", "mean"),
        condition_blocks_mean=("voice_condition_blocked_count", "mean"),
        assisted_tasks_mean=("assisted_task_count", "mean"),
        assistance_blocked_mean=("assistance_blocked_count", "mean"),
    ).reset_index()
    return out.sort_values("condition_id")


def event_counts_by_trial(events_df: pd.DataFrame) -> pd.DataFrame:
    if events_df.empty or "trial_id" not in events_df.columns:
        return pd.DataFrame(columns=["trial_id", *EVENT_RULES.keys()])
    rows: list[dict[str, Any]] = []
    assigned = events_df[events_df["trial_id"].map(is_real_trial_id)]
    for trial_id, group in assigned.groupby("trial_id", dropna=False):
        event_types = group.get("event_type", pd.Series(dtype=str)).fillna("").astype(str)
        row: dict[str, Any] = {"trial_id": trial_id, "event_count": len(group)}
        for metric, exact_names in EVENT_RULES.items():
            row[metric] = int(event_types.isin(exact_names).sum())
        row.update(_p41_counts(group))
        row.update(_voice_prefix_counts(event_types))
        rows.append(row)
    return pd.DataFrame(rows)


def event_counts_by_condition(
    events_df: pd.DataFrame,
    trial_metrics_df: pd.DataFrame,
    include_invalid: bool = False,
) -> pd.DataFrame:
    if events_df.empty or trial_metrics_df.empty or "trial_id" not in events_df.columns:
        return pd.DataFrame()
    trials = trial_metrics_df.copy()
    if not include_invalid and "valid_for_analysis" in trials.columns:
        trials = trials[trials["valid_for_analysis"].map(_as_bool).fillna(False)]
    trial_lookup = trials[["trial_id", "condition_id", "condition_name"]].dropna(subset=["trial_id"]).rename(
        columns={"condition_id": "condition_id_for_analysis", "condition_name": "condition_name_for_analysis"}
    )
    assigned = events_df[events_df["trial_id"].map(is_real_trial_id)].merge(trial_lookup, on="trial_id", how="inner")
    if assigned.empty:
        return pd.DataFrame()
    rows: list[dict[str, Any]] = []
    for (condition_id, condition_name), group in assigned.groupby(["condition_id_for_analysis", "condition_name_for_analysis"], dropna=False):
        event_types = group.get("event_type", pd.Series(dtype=str)).fillna("").astype(str)
        row: dict[str, Any] = {
            "condition_id": condition_id,
            "condition_name": condition_name,
            "event_count": len(group),
            "trial_count": group["trial_id"].nunique(),
        }
        for metric, exact_names in EVENT_RULES.items():
            row[metric] = int(event_types.isin(exact_names).sum())
        row.update(_p41_counts(group))
        row.update(_voice_prefix_counts(event_types))
        rows.append(row)
    return pd.DataFrame(rows).sort_values("condition_id")


def build_condition_validation_report(
    data: ExperimentRunData,
    trial_metrics_df: pd.DataFrame,
    include_invalid: bool = True,
) -> pd.DataFrame:
    if trial_metrics_df.empty:
        return pd.DataFrame(columns=["condition_id", "severity", "rule", "message"])
    df = trial_metrics_df.copy()
    if not include_invalid and "valid_for_analysis" in df.columns:
        df = df[df["valid_for_analysis"].map(_as_bool).fillna(False)]
    rows: list[dict[str, Any]] = []
    for _, trial in df.iterrows():
        condition_id = str(trial.get("condition_id", "") or "")
        trial_id = str(trial.get("trial_id", "") or "")
        resolution_errors = str(trial.get("condition_resolution_errors", "") or "")
        if not condition_id or condition_id not in CANONICAL_CONDITIONS:
            rows.append(_validation_row(condition_id, "Fail", "condition_resolved", f"{trial_id}: condition unresolved"))
        if resolution_errors:
            rows.append(_validation_row(condition_id, "Fail", "condition_consistency", f"{trial_id}: {resolution_errors}"))
        canonical = CANONICAL_CONDITIONS.get(condition_id)
        if not canonical:
            continue

        robot_activity = _sum_trial(
            trial,
            "autonomy_request_count",
            "assisted_task_count",
            "manipulation_started_count",
            "manipulation_completed_count",
            "navigation_started_count",
            "navigation_completed_count",
            "autonomous_selection_policy_evaluated_count",
            "autonomous_selection_candidate_selected_count",
        )
        voice_executed = _sum_trial(trial, "voice_command_executed_count", "voice_command_allowed_count")
        if condition_id == "C00_robot_off_voice_off":
            if robot_activity > 0:
                rows.append(_validation_row(condition_id, "Fail", "c00_no_robot_activity", f"{trial_id}: robot activity count={robot_activity}"))
            if voice_executed > 0:
                rows.append(_validation_row(condition_id, "Fail", "c00_no_voice_execution", f"{trial_id}: voice execution count={voice_executed}"))
        if condition_id == "C10_robot_on_voice_off" and voice_executed > 0:
            rows.append(_validation_row(condition_id, "Fail", "c10_voice_execution_blocked", f"{trial_id}: voice execution count={voice_executed}"))
        if condition_id in {"C10_robot_on_voice_off", "C11_robot_on_voice_on"} and robot_activity > 0:
            if _sum_trial(trial, "p41_local_pick_cost_event_count") == 0:
                rows.append(_validation_row(condition_id, "Warn", "p41_policy_observed", f"{trial_id}: LocalPickCost not observed"))
            if _sum_trial(trial, "p41_robot_to_pickup_metric_event_count") == 0:
                rows.append(_validation_row(condition_id, "Warn", "p41_metric_observed", f"{trial_id}: RobotToPickupCost metric not observed"))
        if _sum_trial(trial, "pick_place_runtime_invalid_count") > 0:
            rows.append(_validation_row(condition_id, "Fail", "p41_pick_place_not_runtime", f"{trial_id}: PickPlaceCost detected as runtime policy"))
        if _sum_trial(trial, "pick_place_benchmark_diagnostic_count") > 0:
            rows.append(_validation_row(condition_id, "Info", "p41_pick_place_benchmark_diagnostic", f"{trial_id}: pick+place benchmark present as diagnostic"))
        if _sum_trial(trial, "startup_alignment_soft_exit_count") > 0:
            rows.append(_validation_row(condition_id, "Warn", "navigation_soft_exit_diagnostic", f"{trial_id}: startup alignment soft-exit observed"))
        if _is_manual_partial(trial):
            rows.append(_validation_row(condition_id, "Info", "manual_partial_not_functional_failure", f"{trial_id}: manually ended partial/invalid run"))

    if not rows:
        rows.append(_validation_row("", "Pass", "aggregate_condition_validation", "No aggregate condition red flags detected."))
    return pd.DataFrame(rows)


def voice_metrics_by_trial(trial_metrics_df: pd.DataFrame) -> pd.DataFrame:
    columns = [column for column in VOICE_METRIC_COLUMNS if column in trial_metrics_df.columns]
    out = trial_metrics_df[columns].copy() if columns else pd.DataFrame(columns=VOICE_METRIC_COLUMNS)
    for column in VOICE_METRIC_COLUMNS:
        if column not in out.columns:
            out[column] = pd.NA if column.startswith("time_to_") else 0
    return out[VOICE_METRIC_COLUMNS]


def reset_metrics_by_trial(trial_metrics_df: pd.DataFrame) -> pd.DataFrame:
    columns = [column for column in RESET_METRIC_COLUMNS if column in trial_metrics_df.columns]
    out = trial_metrics_df[columns].copy() if columns else pd.DataFrame(columns=RESET_METRIC_COLUMNS)
    for column in RESET_METRIC_COLUMNS:
        if column not in out.columns:
            out[column] = pd.NA if column == "reset_consistent" else 0
    return out[RESET_METRIC_COLUMNS]


def condition_reporting_summary(trial_metrics_df: pd.DataFrame, condition_metrics_df: pd.DataFrame) -> pd.DataFrame:
    if trial_metrics_df.empty:
        return pd.DataFrame()
    rows: list[dict[str, Any]] = []
    for condition_id, group in trial_metrics_df.groupby("condition_id", dropna=False):
        valid = group["valid_for_analysis"].map(_as_bool).fillna(False) if "valid_for_analysis" in group.columns else pd.Series(False, index=group.index)
        expected = _numeric(group, "expected_boxes")
        deposited = _numeric(group, "deposited_boxes")
        condition_row = condition_metrics_df[condition_metrics_df["condition_id"] == condition_id] if not condition_metrics_df.empty else pd.DataFrame()
        rows.append(
            {
                "condition_id": condition_id,
                "condition_name": _first_group_text(group, "condition_name"),
                "run_count": group["run_id"].dropna().astype(str).nunique() if "run_id" in group.columns else 1,
                "trial_count": len(group),
                "valid_trial_count": int(valid.sum()),
                "invalid_trial_count": int((~valid).sum()),
                "complete_round_count": int((deposited >= expected).fillna(False).sum()),
                "round_completeness_mean": float((deposited / expected.replace(0, pd.NA)).mean()) if len(group) else 0.0,
                "picks_correct": int(_numeric(group, "pick_succeeded_count").sum()),
                "places_correct": int(_numeric(group, "place_succeeded_count").sum()),
                "errors": int(_numeric(group, "classification_error_count").sum()),
                "warnings": int(
                    group.get("condition_resolution_warnings", pd.Series("", index=group.index)).astype(str).str.len().gt(0).sum()
                    + _numeric(group, "non_terminal_warning_count").sum()
                ),
                "non_terminal_warnings": int(_numeric(group, "non_terminal_warning_count").sum()),
                "aborts_or_manual_stops": int(group.get("aborted", pd.Series(False, index=group.index)).map(_as_bool).fillna(False).sum() + group.apply(_is_manual_partial, axis=1).sum()),
                "robot_intervention_present": bool(
                    _numeric(group, "autonomy_request_count").sum()
                    + _numeric(group, "assisted_task_count").sum()
                    + _numeric(group, "autonomous_selection_candidate_selected_count").sum()
                ),
                "voice_intervention_present": bool(
                    _numeric(group, "voice_command_executed_count").sum()
                    + _numeric(group, "voice_command_allowed_count").sum()
                    + _numeric(group, "voice_routing_count").sum()
                ),
                "voice_blocked_or_rejected_count": int(_numeric(group, "voice_blocked_by_condition_count").sum() + _numeric(group, "voice_command_rejected_count").sum()),
                "voice_executed_count": int(_numeric(group, "voice_command_executed_count").sum() + _numeric(group, "voice_command_allowed_count").sum()),
                "p41_local_pick_cost_event_count": int(_numeric(group, "p41_local_pick_cost_event_count").sum()),
                "p41_robot_to_pickup_metric_event_count": int(_numeric(group, "p41_robot_to_pickup_metric_event_count").sum()),
                "pick_place_benchmark_diagnostic_count": int(_numeric(group, "pick_place_benchmark_diagnostic_count").sum()),
                "pick_place_runtime_invalid_count": int(_numeric(group, "pick_place_runtime_invalid_count").sum()),
                "startup_alignment_soft_exit_count": int(_numeric(group, "startup_alignment_soft_exit_count").sum()),
                "success_rate": condition_row["success_rate"].iloc[0] if not condition_row.empty and "success_rate" in condition_row.columns else pd.NA,
            }
        )
    return pd.DataFrame(rows).sort_values("condition_id")


def pretrial_or_unassigned_events(events_df: pd.DataFrame) -> pd.DataFrame:
    if events_df.empty:
        return pd.DataFrame()
    if "trial_id" not in events_df.columns:
        return events_df.copy()
    return events_df[~events_df["trial_id"].map(is_real_trial_id)].copy()


def time_to_first_event_metrics(events_df: pd.DataFrame, base_trials_df: pd.DataFrame) -> pd.DataFrame:
    columns = [
        "trial_id",
        "time_to_first_voice_event_seconds",
        "time_to_first_voice_command_seconds",
        "time_to_first_autonomy_request_seconds",
        "time_to_first_assistance_seconds",
    ]
    if events_df.empty or base_trials_df.empty or "trial_id" not in base_trials_df.columns:
        return pd.DataFrame(columns=columns)
    trials = base_trials_df.copy()
    if "trial_start_time_dt" not in trials.columns and "trial_start_time" in trials.columns:
        trials["trial_start_time_dt"] = pd.to_datetime(trials["trial_start_time"], errors="coerce", utc=True)
    if "trial_start_time_dt" not in trials.columns:
        return pd.DataFrame(columns=columns)
    rows: list[dict[str, Any]] = []
    events = events_df[events_df.get("trial_id", pd.Series(dtype=object)).map(is_real_trial_id)].copy()
    if "timestamp_wall_dt" not in events.columns:
        return pd.DataFrame(columns=columns)
    events["event_type"] = events.get("event_type", pd.Series(dtype=str)).fillna("").astype(str)
    for _, trial in trials[trials["trial_id"].map(is_real_trial_id)].iterrows():
        start = trial.get("trial_start_time_dt")
        trial_id = trial["trial_id"]
        group = events[events["trial_id"] == trial_id].dropna(subset=["timestamp_wall_dt"])
        row = {column: pd.NA for column in columns}
        row["trial_id"] = trial_id
        if pd.isna(start) or group.empty:
            rows.append(row)
            continue
        row["time_to_first_voice_event_seconds"] = _seconds_to_first(start, group[group["event_type"].str.startswith("voice_")])
        row["time_to_first_voice_command_seconds"] = _seconds_to_first(start, group[group["event_type"].map(_is_voice_command_event)])
        row["time_to_first_autonomy_request_seconds"] = _seconds_to_first(
            start,
            group[group["event_type"].isin(EVENT_RULES["autonomy_request_count"])],
        )
        row["time_to_first_assistance_seconds"] = _seconds_to_first(
            start,
            group[group["event_type"].isin([*EVENT_RULES["assisted_task_count"], *EVENT_RULES["assistance_blocked_count"]])],
        )
        rows.append(row)
    return pd.DataFrame(rows, columns=columns)


def enforce_analysis_eligibility(data: ExperimentRunData, allow_red_flags: bool = False) -> None:
    red_flags = (data.validation or {}).get("red_flags", []) or []
    if red_flags and not allow_red_flags:
        joined = "; ".join(map(str, red_flags))
        raise RuntimeError(
            "experiment_data_validation.json contains red flags. "
            f"Use --allow-red-flags to export diagnostics anyway. Red flags: {joined}"
        )


def infer_reset_consistency(trial_metrics_df: pd.DataFrame, validation: dict[str, Any]) -> pd.Series:
    values: list[str | bool] = []
    failed_reset_trials = set(map(str, validation.get("trials_failed_reset", []) or []))
    reset_passes = [str(item) for item in (validation.get("metric_sufficiency_passes", []) or []) if "reset" in str(item).lower()]
    for _, row in trial_metrics_df.iterrows():
        trial_id = str(row.get("trial_id", ""))
        failed_count = pd.to_numeric(pd.Series([row.get("reset_failed_count", 0)]), errors="coerce").fillna(0).iloc[0]
        started_count = pd.to_numeric(pd.Series([row.get("reset_started_count", 0)]), errors="coerce").fillna(0).iloc[0]
        applied_count = pd.to_numeric(pd.Series([row.get("reset_applied_count", 0)]), errors="coerce").fillna(0).iloc[0]
        completed_count = pd.to_numeric(pd.Series([row.get("reset_completed_count", 0)]), errors="coerce").fillna(0).iloc[0]
        if failed_count > 0 or trial_id in failed_reset_trials:
            values.append(False)
        elif any(trial_id and trial_id in item for item in reset_passes):
            values.append(True)
        elif started_count > 0 and applied_count >= started_count and completed_count >= started_count:
            values.append(True)
        elif started_count > 0 or applied_count > 0 or completed_count > 0:
            values.append(False)
        else:
            values.append("unknown")
    return pd.Series(values, index=trial_metrics_df.index)


def is_real_trial_id(value: Any) -> bool:
    if pd.isna(value):
        return False
    text = str(value).strip()
    return text != "" and text.lower() not in {"nan", "none", "null", "unassigned", "unknown"}


def _base_trial_frame(data: ExperimentRunData) -> pd.DataFrame:
    if not data.session_trials_df.empty:
        base = data.session_trials_df.copy()
    else:
        base = data.trial_summaries_df.copy()
    if base.empty:
        return base
    base = _apply_analytic_exclusions(base)
    rename = {
        "timestamp_start": "trial_start_time",
        "timestamp_start_dt": "trial_start_time_dt",
        "timestamp_end": "trial_end_time",
        "timestamp_end_dt": "trial_end_time_dt",
        "total_duration_seconds": "trial_duration_s",
        "scene_name": "scene",
    }
    base = base.rename(columns={k: v for k, v in rename.items() if k in base.columns})
    if "trial_duration_s" not in base.columns and {"trial_start_time_dt", "trial_end_time_dt"}.issubset(base.columns):
        base["trial_duration_s"] = (base["trial_end_time_dt"] - base["trial_start_time_dt"]).dt.total_seconds()
    for column in ["success", "aborted", "valid_for_analysis", "robot_enabled", "voice_enabled"]:
        if column in base.columns:
            base[column] = base[column].map(_as_bool)
    if "valid_for_analysis" not in base.columns:
        base["valid_for_analysis"] = True
    return base


def _apply_analytic_exclusions(base: pd.DataFrame) -> pd.DataFrame:
    """Preserve append-only rows while marking superseded attempts analytically invalid."""
    frame = base.copy()
    frame["source_trial_id"] = frame.get("trial_id", pd.Series("", index=frame.index))
    frame["canonical_trial_id"] = frame["source_trial_id"].map(_canonical_trial_id)
    frame["superseded_by_restart"] = False
    frame["analytic_exclusion_reason"] = frame.get("exclusion_reason", pd.Series("", index=frame.index)).fillna("").astype(str)

    terminal = frame.get("terminal_state", pd.Series("", index=frame.index)).fillna("").astype(str).str.lower()
    failure = frame.get("failure_reason", pd.Series("", index=frame.index)).fillna("").astype(str).str.lower()
    exclusion = frame.get("exclusion_reason", pd.Series("", index=frame.index)).fillna("").astype(str).str.lower()
    restart_reason = "saved_exit_incomplete_condition_restart"
    restart_rows = frame[terminal.eq(restart_reason) | failure.eq(restart_reason) | exclusion.eq(restart_reason)]
    for _, marker in restart_rows.iterrows():
        same_attempt = pd.Series(True, index=frame.index)
        for column in ["run_id", "condition_id"]:
            marker_value = str(marker.get(column, "") or "")
            if marker_value and column in frame.columns:
                same_attempt &= frame[column].fillna("").astype(str).eq(marker_value)
        marker_end = pd.to_datetime(marker.get("timestamp_end"), errors="coerce", utc=True)
        if pd.notna(marker_end) and "timestamp_start" in frame.columns:
            starts = pd.to_datetime(frame["timestamp_start"], errors="coerce", utc=True)
            same_attempt &= starts.isna() | starts.le(marker_end)
        frame.loc[same_attempt, "valid_for_analysis"] = False
        frame.loc[same_attempt, "superseded_by_restart"] = True
        frame.loc[same_attempt, "analytic_exclusion_reason"] = restart_reason

    technical_reason = "technical_incident_runtime_operator"
    technical = failure.eq(technical_reason) | exclusion.eq(technical_reason)
    frame.loc[technical, "valid_for_analysis"] = False
    frame.loc[technical, "analytic_exclusion_reason"] = technical_reason
    return frame


def _canonical_trial_id(value: Any) -> str:
    if value is None or pd.isna(value):
        return ""
    return re.sub(r"_dup\d+$", "", str(value).strip(), flags=re.IGNORECASE)


def _voice_prefix_counts(event_types: pd.Series) -> dict[str, int]:
    voice = event_types[event_types.str.startswith("voice_")]
    return {
        "voice_event_count_total": int(len(voice)),
        "voice_listening_started_count": int(event_types.str.contains(r"^voice_.*listening.*start", regex=True).sum()),
        "voice_listening_stopped_count": int(event_types.str.contains(r"^voice_.*listening.*(?:stop|end)", regex=True).sum()),
        "voice_vad_event_count": int(event_types.str.contains(r"^voice_.*vad|^voice_vad", regex=True).sum()),
    }


def _p41_counts(group: pd.DataFrame) -> dict[str, int]:
    if group.empty:
        return {
            "p41_local_pick_cost_event_count": 0,
            "p41_robot_to_pickup_metric_event_count": 0,
            "pick_place_benchmark_diagnostic_count": 0,
            "pick_place_runtime_invalid_count": 0,
        }
    policy = _payload_text(group, "policy")
    metric = _payload_text(group, "selection_metric")
    benchmark_active = _payload_text(group, "benchmark_pick_place_total_active").str.lower().isin({"true", "1", "yes"})
    benchmark_cost_present = group.get("payload_benchmark_pick_place_total_cost", pd.Series(pd.NA, index=group.index)).notna()
    return {
        "p41_local_pick_cost_event_count": int(policy.str.lower().eq("localpickcost").sum()),
        "p41_robot_to_pickup_metric_event_count": int(metric.str.lower().isin({"robot_to_pickup_cost", "robottopickupcost"}).sum()),
        "pick_place_benchmark_diagnostic_count": int((benchmark_cost_present & ~benchmark_active).sum()),
        "pick_place_runtime_invalid_count": int((policy.str.lower().isin({"pickplacecost", "pickplacetotalcost"}) | benchmark_active).sum()),
    }


def _payload_text(group: pd.DataFrame, name: str) -> pd.Series:
    for column in [f"payload_{name}", name]:
        if column in group.columns:
            return group[column].fillna("").astype(str)
    return pd.Series("", index=group.index)


def _validation_row(condition_id: str, severity: str, rule: str, message: str) -> dict[str, Any]:
    return {
        "condition_id": condition_id,
        "severity": severity,
        "rule": rule,
        "message": message,
    }


def _sum_trial(row: pd.Series, *columns: str) -> int:
    total = 0
    for column in columns:
        total += int(pd.to_numeric(pd.Series([row.get(column, 0)]), errors="coerce").fillna(0).iloc[0])
    return total


def _numeric(df: pd.DataFrame, column: str) -> pd.Series:
    if column not in df.columns:
        return pd.Series(0, index=df.index)
    return pd.to_numeric(df[column], errors="coerce").fillna(0)


def _first_group_text(df: pd.DataFrame, column: str) -> str:
    if column not in df.columns:
        return ""
    values = df[column].dropna().astype(str)
    return values.iloc[0] if not values.empty else ""


def _is_manual_partial(row: pd.Series) -> bool:
    terminal = str(row.get("terminal_state", "") or "").lower()
    reason = str(row.get("failure_reason", "") or "").lower()
    valid = _as_bool(row.get("valid_for_analysis"))
    success = _as_bool(row.get("success"))
    manual = "manual" in terminal or "manual" in reason or terminal in {"disabled", "manually_ended_incomplete"}
    return manual and (valid is False or success is False)


def _is_voice_command_event(event_type: str) -> bool:
    text = event_type.lower()
    return text.startswith("voice_command") or text in {
        "voice_command_normalized",
        "voice_intent_mapped",
        "voice_command_blocked_by_condition",
    }


def _seconds_to_first(start: Any, group: pd.DataFrame) -> float | pd.NA:
    if group.empty or "timestamp_wall_dt" not in group.columns:
        return pd.NA
    first = group["timestamp_wall_dt"].min()
    if pd.isna(first):
        return pd.NA
    return max(0.0, float((first - start).total_seconds()))


def _as_bool(value: Any) -> bool | None:
    if pd.isna(value):
        return None
    if isinstance(value, bool):
        return value
    text = str(value).strip().lower()
    if text in {"true", "1", "yes", "y"}:
        return True
    if text in {"false", "0", "no", "n"}:
        return False
    return None


def _first_existing_numeric(df: pd.DataFrame, candidates: Iterable[str], default: Any) -> pd.Series:
    for column in candidates:
        if column in df.columns:
            series = pd.to_numeric(df[column], errors="coerce")
            if series.notna().any():
                return series.fillna(0).astype("int64")
    if isinstance(default, pd.Series):
        return pd.to_numeric(default, errors="coerce").fillna(0).astype("int64")
    return pd.Series(default, index=df.index).fillna(0).astype("int64")
