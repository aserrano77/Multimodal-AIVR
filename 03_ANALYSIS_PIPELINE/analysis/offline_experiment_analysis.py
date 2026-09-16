from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import sys
import tempfile
from pathlib import Path

try:
    import pandas as pd
except ImportError as exc:
    raise SystemExit(
        "Missing dependency: pandas. Install with `python -m pip install -r analysis/requirements.txt`."
    ) from exc

from src.experiment_loader import load_experiment_run, read_jsonl
from src.experiment_metrics import (
    build_condition_metrics,
    build_condition_validation_report,
    event_counts_by_condition,
    build_trial_metrics,
    condition_reporting_summary,
    enforce_analysis_eligibility,
    pretrial_or_unassigned_events,
    reset_metrics_by_trial,
    voice_metrics_by_trial,
)
from src.experiment_plots import write_optional_plots
from src.questionnaire_code import (
    SCHEME as QUESTIONNAIRE_CODE_SCHEME,
    compare_with_stored_order,
    normalize_code,
    self_test as questionnaire_code_self_test,
)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Offline analysis for Multimodal AI-VR experiment runs.")
    parser.add_argument("--run-dir", type=Path, help="Experiment run folder containing manifest/events/samples.")
    parser.add_argument("--input", type=Path, help="P45F alias for a session folder, exported Quest folder, or legacy run folder.")
    parser.add_argument("--input-root", type=Path, help="P45F exported ExperimentData root containing participant/session folders.")
    parser.add_argument("--out-dir", type=Path, default=Path("analysis/output/offline_run"), help="Export folder.")
    parser.add_argument("--validation-file", type=Path, help="Optional explicit experiment_data_validation.json path.")
    parser.add_argument("--include-invalid", action="store_true", help="Include invalid trials in condition aggregations.")
    parser.add_argument("--allow-red-flags", action="store_true", help="Allow export when validation red flags are present.")
    parser.add_argument("--plots", action="store_true", help="Write optional matplotlib charts if matplotlib is installed.")
    parser.add_argument("--strict-acceptance", action="store_true", help="Require lossless, complete P45F/P47A acceptance criteria.")
    parser.add_argument("--kobo-xlsx", type=Path, help="Optional Kobo XLSX export used only for unique questionnaire-code linkage.")
    parser.add_argument("--expect-linked-sessions", type=int, help="Expected number of completed sessions linked uniquely to Kobo.")
    parser.add_argument("--expect-valid-trials", type=int, help="Expected number of valid analytical trial rows in the linked dataset.")
    parser.add_argument("--expect-participant-id", help="Expected participant identifier for strict acceptance.")
    parser.add_argument("--expect-session-id", help="Expected session identifier for strict acceptance.")
    parser.add_argument("--self-test", action="store_true", help="Run a synthetic parser/metrics smoke test.")
    args = parser.parse_args(argv)

    if args.self_test:
        return run_self_test()
    selected_input = args.input or args.run_dir
    if not selected_input and not args.input_root:
        parser.error("--run-dir, --input, or --input-root is required unless --self-test is used")

    run_dirs = discover_input_run_dirs(args.input_root or selected_input)
    print(f"p45f_offline_analysis_input_detected input={args.input_root or selected_input} run_count={len(run_dirs)}")
    if not run_dirs:
        print(f"p45f_offline_analysis_validation_failed input={args.input_root or selected_input} error_reason=no_session_manifest_found")
        return 2
    if args.input_root:
        root_errors = validate_p45f_input_root_shape(args.input_root)
        if root_errors:
            print(f"p45f_offline_analysis_validation_failed input={args.input_root} error_reason={';'.join(root_errors)}")
            return 2
    if len(run_dirs) > 1:
        args.out_dir.mkdir(parents=True, exist_ok=True)
        failures = 0
        run_outputs: list[tuple[Path, Path]] = []
        for run_dir in run_dirs:
            run_out = args.out_dir / sanitize_path_token(run_dir.parent.name) / sanitize_path_token(run_dir.name)
            try:
                if analyze_one_run(run_dir, args, run_out) != 0:
                    failures += 1
                else:
                    run_outputs.append((run_dir, run_out))
            except Exception as exc:  # keep batch export useful while surfacing failed sessions
                failures += 1
                print(f"p45f_offline_analysis_validation_failed input={run_dir} error_reason={type(exc).__name__}:{exc}")
        if failures:
            return 2
        if not write_batch_dataset(run_outputs, args, args.out_dir):
            return 2
        print(f"Offline analysis exported to {args.out_dir.resolve()}")
        print(f"p45f_offline_analysis_validation_passed input={args.input_root or selected_input} run_count={len(run_dirs)}")
        return 0

    return analyze_one_run(run_dirs[0], args, args.out_dir)


def build_questionnaire_code_validation(frame: "pd.DataFrame") -> "pd.DataFrame":
    """Validate order_checksum_v1 for every session in ``frame``.

    Applies rules 1-6 of the data dictionary (length, alphabet, A-F prefix and
    control character) and contrasts the order encoded by the prefix with the
    order the application persisted in the manifest. Shared by the single-session
    and the multi-session paths so both produce the same evidence.
    """
    columns = [
        "participant_id", "session_id", "questionnaire_code", "questionnaire_code_scheme",
        "persisted_condition_order", "session_history_status",
    ]
    result = frame[[column for column in columns if column in frame.columns]].copy()
    if "questionnaire_code" not in result.columns:
        result["questionnaire_code"] = ""
    result["questionnaire_code"] = result["questionnaire_code"].fillna("").astype(str).map(normalize_code)
    stored = (
        result.get("persisted_condition_order", pd.Series("", index=result.index))
        .fillna("").astype(str)
    )
    rows = [compare_with_stored_order(code, value) for code, value in zip(result["questionnaire_code"], stored)]
    result["questionnaire_code_scheme_expected"] = QUESTIONNAIRE_CODE_SCHEME
    result["questionnaire_code_valid"] = [not issue.startswith("questionnaire_code_") for _, issue, _, _ in rows]
    result["questionnaire_code_error"] = [
        issue.replace("questionnaire_code_", "") if issue.startswith("questionnaire_code_") else ""
        for _, issue, _, _ in rows
    ]
    result["decoded_condition_order"] = ["->".join(decoded) for _, _, decoded, _ in rows]
    result["code_order_matches_persisted"] = [matches for matches, _, _, _ in rows]
    result["code_order_issue"] = [issue for _, issue, _, _ in rows]
    return result


def analyze_one_run(run_dir: Path, args: argparse.Namespace, out_dir: Path) -> int:
    data = load_experiment_run(run_dir, validation_path=args.validation_file)
    validation_errors = validate_p45f_export(data)
    if validation_errors:
        print(f"p45f_offline_analysis_validation_failed input={run_dir} error_reason={';'.join(validation_errors)}")
        if is_p45f_manifest(data.manifest) and not args.allow_red_flags:
            raise ValueError(";".join(validation_errors))
    else:
        print(f"p45f_offline_analysis_validation_passed input={run_dir} participant_id={data.manifest.get('participant_id', '')} session_id={data.manifest.get('session_id', '')}")
    enforce_analysis_eligibility(data, allow_red_flags=args.allow_red_flags)
    trial_metrics_df, event_counts_df = build_trial_metrics(data)
    condition_metrics_df = build_condition_metrics(trial_metrics_df, include_invalid=args.include_invalid)
    event_counts_by_condition_df = event_counts_by_condition(data.events_df, trial_metrics_df, include_invalid=args.include_invalid)
    condition_reporting_df = condition_reporting_summary(trial_metrics_df, condition_metrics_df)
    condition_validation_df = build_condition_validation_report(data, trial_metrics_df, include_invalid=True)
    voice_metrics_df = voice_metrics_by_trial(trial_metrics_df)
    reset_metrics_df = reset_metrics_by_trial(trial_metrics_df)
    unassigned_events_df = pretrial_or_unassigned_events(data.events_df)
    acceptance_df = build_strict_acceptance_report(
        data,
        trial_metrics_df,
        condition_metrics_df,
        event_counts_df,
        event_counts_by_condition_df,
        condition_reporting_df,
        condition_validation_df,
        voice_metrics_df,
        reset_metrics_df,
        unassigned_events_df,
        expected_participant_id=args.expect_participant_id,
        expected_session_id=args.expect_session_id,
    ) if args.strict_acceptance else pd.DataFrame()
    integrity_df = build_run_integrity_report(data, trial_metrics_df, unassigned_events_df, acceptance_df)

    out_dir.mkdir(parents=True, exist_ok=True)
    trial_metrics_df.to_csv(out_dir / "trial_metrics.csv", index=False)
    data.runs_df.to_csv(out_dir / "run_metadata.csv", index=False)
    build_questionnaire_code_validation(data.runs_df).to_csv(
        out_dir / "questionnaire_code_validation.csv", index=False
    )
    condition_metrics_df.to_csv(out_dir / "condition_metrics.csv", index=False)
    event_counts_df.to_csv(out_dir / "event_counts_by_trial.csv", index=False)
    event_counts_by_condition_df.to_csv(out_dir / "event_counts_by_condition.csv", index=False)
    condition_reporting_df.to_csv(out_dir / "condition_reporting_summary.csv", index=False)
    condition_validation_df.to_csv(out_dir / "condition_validation_report.csv", index=False)
    write_condition_validation_json(condition_validation_df, out_dir / "condition_validation_report.json")
    voice_metrics_df.to_csv(out_dir / "voice_metrics_by_trial.csv", index=False)
    reset_metrics_df.to_csv(out_dir / "reset_metrics_by_trial.csv", index=False)
    integrity_df.to_csv(out_dir / "run_integrity_report.csv", index=False)
    write_integrity_markdown(data, integrity_df, trial_metrics_df, unassigned_events_df, out_dir / "run_integrity_report.md")
    write_condition_validation_markdown(condition_validation_df, condition_reporting_df, out_dir / "condition_validation_report.md")
    if args.strict_acceptance:
        acceptance_df.to_csv(out_dir / "acceptance_validation_report.csv", index=False)
        (out_dir / "acceptance_validation_report.json").write_text(
            json.dumps(acceptance_df.to_dict(orient="records"), indent=2, ensure_ascii=False),
            encoding="utf-8",
        )
        write_acceptance_markdown(acceptance_df, out_dir / "acceptance_validation_report.md")
    if not unassigned_events_df.empty:
        unassigned_events_df.to_csv(out_dir / "pretrial_or_unassigned_events.csv", index=False)
    write_summary_markdown(data, trial_metrics_df, condition_metrics_df, out_dir / "analysis_summary.md")
    if args.plots:
        write_optional_plots(condition_metrics_df, out_dir)

    print(f"Offline analysis exported to {out_dir.resolve()}")
    print(f"Trials: {len(trial_metrics_df)} | Conditions: {condition_metrics_df['condition_id'].nunique() if not condition_metrics_df.empty else 0}")
    print(f"JSONL parse errors: {len(data.jsonl_errors)} | CSV warnings: {len(data.csv_warnings)}")
    if args.strict_acceptance:
        failures = acceptance_df[acceptance_df["status"] == "FAIL"]
        status = "passed" if failures.empty else "failed"
        print(f"p47a_offline_acceptance_{status} criteria={len(acceptance_df)} failures={len(failures)}")
        if not failures.empty:
            return 2
    return 0


def write_batch_dataset(run_outputs: list[tuple[Path, Path]], args: argparse.Namespace, out_dir: Path) -> bool:
    out_dir.mkdir(parents=True, exist_ok=True)
    metadata_frames: list[pd.DataFrame] = []
    trial_frames: list[pd.DataFrame] = []
    for run_dir, run_out in run_outputs:
        metadata = pd.read_csv(run_out / "run_metadata.csv")
        trials = pd.read_csv(run_out / "trial_metrics.csv")
        if metadata.empty:
            continue
        row = metadata.iloc[0]
        for column in ["participant_id", "session_id", "questionnaire_code", "session_history_status"]:
            trials[column] = row.get(column, "")
        trials["source_session_dir"] = str(run_dir)
        metadata_frames.append(metadata)
        trial_frames.append(trials)

    metadata_df = pd.concat(metadata_frames, ignore_index=True, sort=False) if metadata_frames else pd.DataFrame()
    all_trials_df = pd.concat(trial_frames, ignore_index=True, sort=False) if trial_frames else pd.DataFrame()
    linkage_df = metadata_df[[
        column for column in [
            "participant_id", "session_id", "questionnaire_code", "questionnaire_code_scheme",
            "persisted_condition_order", "session_history_status",
            "analysis_eligible", "event_source_file_count", "sample_source_file_count", "run_dir",
        ] if column in metadata_df.columns
    ]].copy()
    linkage_df["questionnaire_code"] = linkage_df.get("questionnaire_code", pd.Series("", index=linkage_df.index)).fillna("").astype(str).str.strip().str.upper()
    linkage_df["kobo_match_count"] = 0
    linkage_df["kobo_submission_id"] = ""
    linkage_df["kobo_submission_uuid"] = ""

    # order_checksum_v1: validate the code and contrast the order it encodes with
    # the order the application persisted. Same specification as the Unity
    # generator, the deployed XLSForm and analysis/tools/session_history_order_audit.py.
    code_validation_df = build_questionnaire_code_validation(linkage_df)
    for column in [
        "questionnaire_code_scheme_expected", "questionnaire_code_valid", "questionnaire_code_error",
        "decoded_condition_order", "code_order_matches_persisted", "code_order_issue",
    ]:
        linkage_df[column] = code_validation_df[column].values

    kobo_df = pd.DataFrame()
    if args.kobo_xlsx:
        kobo_path = args.kobo_xlsx.expanduser().resolve()
        if not kobo_path.is_file():
            raise FileNotFoundError(f"Kobo export does not exist: {kobo_path}")
        kobo_df = pd.read_excel(kobo_path, sheet_name=0, engine="openpyxl")
        code_column = next((name for name in ["code_normalized", "A2_questionnaire_code"] if name in kobo_df.columns), None)
        if code_column is None:
            raise ValueError("Kobo export lacks code_normalized/A2_questionnaire_code")
        kobo_df["_link_code"] = kobo_df[code_column].map(normalize_code)
        kobo_df["_link_code_valid"] = kobo_df["_link_code"].map(lambda value: compare_with_stored_order(value, ())[1] == "stored_order_missing")
        grouped = kobo_df.groupby("_link_code", dropna=False)
        for index, session in linkage_df.iterrows():
            code = session["questionnaire_code"]
            matches = grouped.get_group(code) if code in grouped.groups else pd.DataFrame()
            linkage_df.at[index, "kobo_match_count"] = len(matches)
            if len(matches) == 1:
                match = matches.iloc[0]
                linkage_df.at[index, "kobo_submission_id"] = str(match.get("_id", "") or "")
                linkage_df.at[index, "kobo_submission_uuid"] = str(match.get("_uuid", "") or "")
        metadata_path = out_dir / "kobo_source_metadata.json"
        metadata_path.write_text(json.dumps({
            "path": str(kobo_path),
            "size_bytes": kobo_path.stat().st_size,
            "sha256": hashlib.sha256(kobo_path.read_bytes()).hexdigest(),
            "rows": len(kobo_df),
            "unique_questionnaire_codes": int(kobo_df["_link_code"].nunique()),
            "questionnaire_code_scheme": QUESTIONNAIRE_CODE_SCHEME,
            "codes_failing_checksum": int((~kobo_df["_link_code_valid"]).sum()),
        }, indent=2, ensure_ascii=False), encoding="utf-8")

    status = linkage_df.get("session_history_status", pd.Series("", index=linkage_df.index)).fillna("").astype(str).str.lower()
    linkage_df["analysis_included"] = status.eq("completed")
    if args.kobo_xlsx:
        linkage_df["analysis_included"] &= linkage_df["kobo_match_count"].eq(1)

    included_sessions = set(linkage_df.loc[linkage_df["analysis_included"], "session_id"].astype(str))
    valid = all_trials_df.get("valid_for_analysis", pd.Series(False, index=all_trials_df.index)).map(_acceptance_bool).fillna(False)
    analytic_trials_df = all_trials_df[all_trials_df.get("session_id", pd.Series("", index=all_trials_df.index)).astype(str).isin(included_sessions) & valid].copy()
    all_trials_df.to_csv(out_dir / "all_session_trial_metrics.csv", index=False)
    analytic_trials_df.to_csv(out_dir / "analytic_trial_metrics.csv", index=False)
    linkage_df.to_csv(out_dir / "kobo_session_linkage.csv", index=False)
    linkage_df[[
        column for column in [
            "participant_id", "session_id", "questionnaire_code", "questionnaire_code_scheme",
            "questionnaire_code_scheme_expected", "questionnaire_code_valid", "questionnaire_code_error",
            "decoded_condition_order", "persisted_condition_order", "code_order_matches_persisted",
            "code_order_issue", "session_history_status",
        ] if column in linkage_df.columns
    ]].to_csv(out_dir / "questionnaire_code_validation.csv", index=False)

    file_index_rows: list[dict[str, object]] = []
    for run_dir, _ in run_outputs:
        index_path = run_dir / "file_index.csv"
        if not index_path.is_file():
            file_index_rows.append({"session_id": run_dir.name, "relative_path": "file_index.csv", "status": "missing", "indexed_size": -1, "actual_size": -1, "delta_bytes": 0})
            continue
        index_df = pd.read_csv(index_path, encoding="utf-8-sig")
        for _, indexed in index_df.iterrows():
            relative = str(indexed.get("relative_path", "") or "")
            target = run_dir / relative
            indexed_size = int(indexed.get("size_bytes", -1))
            actual_size = target.stat().st_size if target.is_file() else -1
            file_index_rows.append({
                "session_id": run_dir.name,
                "relative_path": relative,
                "status": "exact" if indexed_size == actual_size else ("missing" if actual_size < 0 else "size_mismatch"),
                "indexed_size": indexed_size,
                "actual_size": actual_size,
                "delta_bytes": actual_size - indexed_size if actual_size >= 0 else 0,
            })
    file_index_df = pd.DataFrame(file_index_rows)
    file_index_df.to_csv(out_dir / "source_file_index_integrity.csv", index=False)

    criteria: list[dict[str, object]] = []
    def add(name: str, passed: bool, expected: object, actual: object, details: str = "") -> None:
        criteria.append({"criterion": name, "status": "PASS" if passed else "FAIL", "expected": expected, "actual": actual, "details": details})

    linked_count = int(linkage_df["analysis_included"].sum())
    add("session_status_resolved", bool(status.ne("").all()), len(linkage_df), int(status.ne("").sum()))

    # order_checksum_v1 acceptance. Sessions written before the scheme existed have
    # no code at all; they are reported separately instead of failing the criterion.
    coded = linkage_df[linkage_df["questionnaire_code"].astype(str).str.len() > 0]
    add(
        "questionnaire_code_checksum_valid",
        bool(coded["questionnaire_code_valid"].all()),
        len(coded),
        int(coded["questionnaire_code_valid"].sum()),
        f"scheme={QUESTIONNAIRE_CODE_SCHEME}; without_code={len(linkage_df) - len(coded)}",
    )
    with_order = coded[coded["persisted_condition_order"].astype(str).str.len() > 0] if "persisted_condition_order" in coded.columns else coded.iloc[0:0]
    add(
        "questionnaire_code_order_matches_persisted",
        bool(with_order["code_order_matches_persisted"].all()),
        len(with_order),
        int(with_order["code_order_matches_persisted"].sum()),
        "; ".join(sorted({issue for issue in with_order["code_order_issue"] if issue})),
    )
    add("noncompleted_sessions_excluded", not bool(linkage_df.loc[linkage_df["analysis_included"], "session_history_status"].astype(str).str.lower().ne("completed").any()), 0, 0)
    if args.kobo_xlsx:
        completed = linkage_df[status.eq("completed")]
        add("completed_sessions_link_uniquely_to_kobo", bool(completed["kobo_match_count"].eq(1).all()), len(completed), int(completed["kobo_match_count"].eq(1).sum()))
    if args.expect_linked_sessions is not None:
        add("expected_linked_session_count", linked_count == args.expect_linked_sessions, args.expect_linked_sessions, linked_count)
    valid_trial_count = len(analytic_trials_df)
    if args.expect_valid_trials is not None:
        add("expected_valid_analytic_trial_count", valid_trial_count == args.expect_valid_trials, args.expect_valid_trials, valid_trial_count)
    technical = all_trials_df.get("failure_reason", pd.Series("", index=all_trials_df.index)).fillna("").astype(str).str.lower().eq("technical_incident_runtime_operator")
    technical_in_analytic = int(analytic_trials_df.get("failure_reason", pd.Series("", index=analytic_trials_df.index)).fillna("").astype(str).str.lower().eq("technical_incident_runtime_operator").sum())
    add("technical_incident_rounds_excluded", technical_in_analytic == 0, 0, technical_in_analytic, f"source_rows={int(technical.sum())}")
    superseded = all_trials_df.get("superseded_by_restart", pd.Series(False, index=all_trials_df.index)).map(_acceptance_bool).fillna(False)
    superseded_in_analytic = int(analytic_trials_df.get("superseded_by_restart", pd.Series(False, index=analytic_trials_df.index)).map(_acceptance_bool).fillna(False).sum())
    add("saved_exit_superseded_rows_excluded", superseded_in_analytic == 0, 0, superseded_in_analytic, f"source_rows={int(superseded.sum())}")
    add("all_event_attempt_files_loaded", bool(metadata_df.get("event_source_file_count", pd.Series(dtype=float)).fillna(0).ge(1).all()), ">=1 per session", metadata_df.get("event_source_file_count", pd.Series(dtype=float)).tolist())
    mismatches = file_index_df[file_index_df["status"] != "exact"] if not file_index_df.empty else pd.DataFrame()
    add("source_file_index_audit_complete", len(file_index_df) > 0, ">0 indexed files audited", len(file_index_df), f"historical_mismatches={len(mismatches)}")

    validation_df = pd.DataFrame(criteria)
    validation_df.to_csv(out_dir / "batch_validation.csv", index=False)
    return bool(validation_df["status"].eq("PASS").all())


def discover_input_run_dirs(path: Path | None) -> list[Path]:
    if path is None:
        return []
    root = path.expanduser().resolve()
    if not root.exists() or not root.is_dir():
        raise FileNotFoundError(f"Input directory does not exist: {root}")
    if list(root.glob("*manifest.json")):
        return [root]
    matches = sorted({candidate.parent.resolve() for candidate in root.rglob("*manifest.json")})
    return matches


def is_p45f_manifest(manifest: dict) -> bool:
    return str(manifest.get("p45f_storage_schema_version", "")).upper().startswith("P45F") or "session_root" in manifest


def validate_p45f_export(data) -> list[str]:
    errors: list[str] = []
    manifest = data.manifest or {}
    if not is_p45f_manifest(manifest):
        return errors
    participant_id = str(manifest.get("participant_id", "")).strip()
    session_id = str(manifest.get("session_id", "")).strip()
    if not participant_id:
        errors.append("participant_id_empty")
    elif participant_id == "P001" and not bool(manifest.get("p45f_allow_test_participant_id", False)):
        errors.append("participant_id_p001_not_allowed")
    elif not re.fullmatch(r"U\d{8}_\d{6}(?:_\d{2})?", participant_id) and participant_id != "PILOT_UNSET":
        errors.append(f"participant_id_unexpected_format:{participant_id}")
    if not session_id:
        errors.append("session_id_empty")
    elif not re.fullmatch(r"S\d{8}_\d{6}(?:_\d{2})?", session_id):
        errors.append(f"session_id_unexpected_format:{session_id}")
    expected = {"C00_robot_off_voice_off", "C10_robot_on_voice_off", "C11_robot_on_voice_on"}
    condition_order = set(map(str, manifest.get("condition_order") or manifest.get("conditions") or []))
    if "C01_robot_off_voice_on" in condition_order or "C01" in condition_order:
        errors.append("c01_condition_present")
    if condition_order and condition_order != expected:
        errors.append(f"condition_order_unexpected:{sorted(condition_order)}")
    detected: set[str] = set()
    for frame in [data.events_df, data.session_trials_df, data.trial_summaries_df]:
        if not frame.empty and "condition_id" in frame.columns:
            detected.update(frame["condition_id"].dropna().astype(str).unique().tolist())
    invalid = sorted(item for item in detected if item and item not in expected)
    if invalid:
        errors.append(f"invalid_condition_id:{invalid}")
    if any(item == "C01" or item.startswith("C01_") for item in detected):
        errors.append("c01_condition_detected")
    participants: set[str] = set()
    for frame in [data.events_df, data.session_trials_df, data.trial_summaries_df]:
        for column in ["participant_id", "payload_participant_id"]:
            if not frame.empty and column in frame.columns:
                participants.update(frame[column].dropna().astype(str).str.strip().loc[lambda s: s != ""].unique().tolist())
    if len(participants) > 1:
        errors.append(f"mixed_participants:{sorted(participants)}")
    if participants and participant_id and participants != {participant_id}:
        errors.append(f"participant_manifest_mismatch:{participant_id}!={sorted(participants)}")
    for required in [data.manifest_path, *data.events_paths, *data.samples_paths]:
        if not required.exists():
            errors.append(f"missing_file:{required.name}")
    return errors


def validate_p45f_input_root_shape(root: Path) -> list[str]:
    errors: list[str] = []
    if root is None:
        return errors
    resolved = root.expanduser().resolve()
    if not resolved.exists() or not resolved.is_dir():
        return errors
    participant_dirs = [
        path for path in resolved.iterdir()
        if path.is_dir() and re.fullmatch(r"U\d{8}_\d{6}(?:_\d{2})?", path.name)
    ]
    malformed_u_dirs = [
        path.name for path in resolved.iterdir()
        if path.is_dir() and path.name.startswith("U") and path not in participant_dirs
    ]
    if len(participant_dirs) > 100:
        errors.append(f"participant_directory_proliferation:{len(participant_dirs)}")
    if malformed_u_dirs:
        errors.append(f"malformed_participant_directories:{malformed_u_dirs[:20]}")
    for participant_dir in participant_dirs:
        session_dirs = [
            path for path in participant_dir.iterdir()
            if path.is_dir() and re.fullmatch(r"S\d{8}_\d{6}(?:_\d{2})?", path.name)
        ]
        malformed_sessions = [
            path.name for path in participant_dir.iterdir()
            if path.is_dir() and path.name.startswith("S") and path not in session_dirs
        ]
        if len(session_dirs) > 20:
            errors.append(f"session_directory_proliferation:{participant_dir.name}:{len(session_dirs)}")
        if malformed_sessions:
            errors.append(f"malformed_session_directories:{participant_dir.name}:{malformed_sessions[:20]}")
    return errors


def sanitize_path_token(value: str) -> str:
    safe = "".join(ch if ch.isalnum() or ch in {"_", "-"} else "_" for ch in value)
    return safe.strip("_") or "run"


def build_strict_acceptance_report(
    data,
    trial_metrics_df: pd.DataFrame,
    condition_metrics_df: pd.DataFrame,
    event_counts_df: pd.DataFrame,
    event_counts_by_condition_df: pd.DataFrame,
    condition_reporting_df: pd.DataFrame,
    condition_validation_df: pd.DataFrame,
    voice_metrics_df: pd.DataFrame,
    reset_metrics_df: pd.DataFrame,
    unassigned_events_df: pd.DataFrame,
    expected_participant_id: str | None = None,
    expected_session_id: str | None = None,
) -> pd.DataFrame:
    rows: list[dict[str, str]] = []

    def add(name: str, passed: bool, expected, actual, details: str = "") -> None:
        rows.append(
            {
                "criterion": name,
                "status": "PASS" if passed else "FAIL",
                "expected": str(expected),
                "actual": str(actual),
                "details": details,
            }
        )

    manifest = data.manifest or {}
    participant_id = str(manifest.get("participant_id", "")).strip()
    session_id = str(manifest.get("session_id", "")).strip()
    add("manifest_readable", bool(manifest), "non-empty JSON object", bool(manifest), str(data.manifest_path))
    add(
        "participant_id",
        not expected_participant_id or participant_id == expected_participant_id,
        expected_participant_id or "manifest value",
        participant_id,
    )
    add(
        "session_id",
        not expected_session_id or session_id == expected_session_id,
        expected_session_id or "manifest value",
        session_id,
    )

    canonical_conditions = {
        "C00_robot_off_voice_off",
        "C10_robot_on_voice_off",
        "C11_robot_on_voice_on",
    }
    manifest_order = [str(item) for item in (manifest.get("condition_order") or manifest.get("conditions") or [])]
    actual_conditions = set(trial_metrics_df.get("condition_id", pd.Series(dtype=str)).dropna().astype(str))
    add("manifest_condition_order", set(manifest_order) == canonical_conditions and len(manifest_order) == 3, canonical_conditions, manifest_order)
    add("resolved_conditions", actual_conditions == canonical_conditions, canonical_conditions, sorted(actual_conditions))

    expected_rounds = int(manifest.get("rounds_per_condition") or 0)
    expected_trial_count = len(manifest_order) * expected_rounds
    trial_count = len(trial_metrics_df)
    add("trial_count", expected_trial_count == 6 and trial_count == expected_trial_count, 6, trial_count)
    trial_ids = trial_metrics_df.get("trial_id", pd.Series(dtype=str)).dropna().astype(str)
    trial_indices = pd.to_numeric(trial_metrics_df.get("trial_index", pd.Series(dtype=float)), errors="coerce").dropna().astype(int)
    add("trial_ids_unique", trial_ids.nunique() == trial_count, trial_count, trial_ids.nunique())
    add("trial_indices_sequential", sorted(trial_indices.tolist()) == list(range(1, trial_count + 1)), list(range(1, trial_count + 1)), sorted(trial_indices.tolist()))
    condition_counts = trial_metrics_df.groupby("condition_id")["trial_id"].count().to_dict() if not trial_metrics_df.empty else {}
    add(
        "two_trials_per_condition",
        set(condition_counts) == canonical_conditions and all(int(value) == 2 for value in condition_counts.values()),
        {item: 2 for item in sorted(canonical_conditions)},
        condition_counts,
    )
    completed = trial_metrics_df.get("terminal_state", pd.Series("", index=trial_metrics_df.index)).fillna("").astype(str).str.lower().eq("completed")
    successful = trial_metrics_df.get("success", pd.Series(False, index=trial_metrics_df.index)).map(_acceptance_bool).fillna(False)
    valid = trial_metrics_df.get("valid_for_analysis", pd.Series(False, index=trial_metrics_df.index)).map(_acceptance_bool).fillna(False)
    aborted = trial_metrics_df.get("aborted", pd.Series(True, index=trial_metrics_df.index)).map(_acceptance_bool).fillna(True)
    add("trials_completed", int(completed.sum()) == 6, 6, int(completed.sum()))
    add("trials_successful", int(successful.sum()) == 6, 6, int(successful.sum()))
    add("trials_valid_for_analysis", int(valid.sum()) == 6, 6, int(valid.sum()))
    add("trials_not_aborted", int(aborted.sum()) == 0, 0, int(aborted.sum()))

    add("jsonl_parse_errors", len(data.jsonl_errors) == 0, 0, len(data.jsonl_errors))
    add("csv_structural_warnings", len(data.csv_warnings) == 0, 0, len(data.csv_warnings))
    resolution_errors = int(trial_metrics_df.get("condition_resolution_errors", pd.Series("", index=trial_metrics_df.index)).fillna("").astype(str).str.len().gt(0).sum())
    resolution_warnings = int(trial_metrics_df.get("condition_resolution_warnings", pd.Series("", index=trial_metrics_df.index)).fillna("").astype(str).str.len().gt(0).sum())
    add("condition_resolution_errors", resolution_errors == 0, 0, resolution_errors)
    add("condition_resolution_warnings", resolution_warnings == 0, 0, resolution_warnings)

    event_types = data.events_df.get("event_type", pd.Series("", index=data.events_df.index)).fillna("").astype(str)
    invariant_failures = int(event_types.eq("session_manifest_invariant_failed").sum())
    error_reasons = data.events_df.get("payload_error_reason", pd.Series("", index=data.events_df.index)).fillna("").astype(str)
    order_mismatches = int(error_reasons.eq("trial_condition_order_mismatch").sum())
    add("session_manifest_invariant_failed", invariant_failures == 0, 0, invariant_failures)
    add("trial_condition_order_mismatch", order_mismatches == 0, 0, order_mismatches)

    raw_event_lines = sum(
        1
        for path in data.events_paths
        for line in path.open("r", encoding="utf-8-sig")
        if line.strip()
    )
    loaded_events = len(data.events_df)
    if {"_source_file", "_line_number"}.issubset(data.events_df.columns):
        unique_event_lines = len(data.events_df[["_source_file", "_line_number"]].drop_duplicates())
    else:
        unique_event_lines = int(data.events_df.get("_line_number", pd.Series(dtype=int)).nunique())
    add("events_loaded_losslessly", loaded_events + len(data.jsonl_errors) == raw_event_lines, raw_event_lines, loaded_events + len(data.jsonl_errors))
    add("events_not_duplicated", unique_event_lines == loaded_events, loaded_events, unique_event_lines)
    assigned_count = loaded_events - len(unassigned_events_df)
    by_trial_count = int(pd.to_numeric(event_counts_df.get("event_count", pd.Series(dtype=float)), errors="coerce").fillna(0).sum())
    by_condition_count = int(pd.to_numeric(event_counts_by_condition_df.get("event_count", pd.Series(dtype=float)), errors="coerce").fillna(0).sum())
    add("event_partition_complete", by_trial_count + len(unassigned_events_df) == loaded_events, loaded_events, by_trial_count + len(unassigned_events_df))
    add("event_counts_by_trial_complete", by_trial_count == assigned_count, assigned_count, by_trial_count)
    add("event_counts_by_condition_complete", by_condition_count == assigned_count, assigned_count, by_condition_count)

    source_trial_ids = set(data.session_trials_df.get("trial_id", pd.Series(dtype=str)).dropna().astype(str))
    output_trial_ids = set(trial_ids)
    summary_csv_ids = set(data.trial_summaries_df.get("trial_id", pd.Series(dtype=str)).dropna().astype(str))
    session_json_errors = []
    session_json_ids: set[str] = set()
    if data.session_trials_jsonl_path:
        session_json_df, session_json_errors = read_jsonl(data.session_trials_jsonl_path)
        session_json_ids = set(session_json_df.get("trial_id", pd.Series(dtype=str)).dropna().astype(str))
    summary_json_ids: set[str] = set()
    summary_json_errors = []
    for path in data.trial_summary_jsonl_paths:
        frame, errors = read_jsonl(path)
        summary_json_errors.extend(errors)
        summary_json_ids.update(frame.get("trial_id", pd.Series(dtype=str)).dropna().astype(str))
    add("trial_ids_preserved", source_trial_ids == output_trial_ids, sorted(source_trial_ids), sorted(output_trial_ids))
    add("session_trials_jsonl_matches_csv", not session_json_errors and session_json_ids == source_trial_ids, sorted(source_trial_ids), sorted(session_json_ids))
    add("trial_summary_csv_matches_session", summary_csv_ids == source_trial_ids, sorted(source_trial_ids), sorted(summary_csv_ids))
    add("trial_summary_jsonl_matches_session", not summary_json_errors and summary_json_ids == source_trial_ids, sorted(source_trial_ids), sorted(summary_json_ids))

    condition_metric_trials = int(pd.to_numeric(condition_metrics_df.get("valid_trial_count", pd.Series(dtype=float)), errors="coerce").fillna(0).sum())
    reporting_trials = int(pd.to_numeric(condition_reporting_df.get("trial_count", pd.Series(dtype=float)), errors="coerce").fillna(0).sum())
    reporting_valid = int(pd.to_numeric(condition_reporting_df.get("valid_trial_count", pd.Series(dtype=float)), errors="coerce").fillna(0).sum())
    add("condition_metrics_cover_trials", condition_metric_trials == 6, 6, condition_metric_trials)
    add("reporting_summary_covers_trials", reporting_trials == 6 and reporting_valid == 6, "6 total / 6 valid", f"{reporting_trials} total / {reporting_valid} valid")
    add("voice_metrics_cover_trials", set(voice_metrics_df.get("trial_id", pd.Series(dtype=str)).dropna().astype(str)) == source_trial_ids, sorted(source_trial_ids), sorted(set(voice_metrics_df.get("trial_id", pd.Series(dtype=str)).dropna().astype(str))))
    add("reset_metrics_cover_trials", set(reset_metrics_df.get("trial_id", pd.Series(dtype=str)).dropna().astype(str)) == source_trial_ids, sorted(source_trial_ids), sorted(set(reset_metrics_df.get("trial_id", pd.Series(dtype=str)).dropna().astype(str))))
    reset_failed = int(pd.to_numeric(reset_metrics_df.get("reset_failed_count", pd.Series(dtype=float)), errors="coerce").fillna(0).sum())
    reset_started = pd.to_numeric(reset_metrics_df.get("reset_started_count", pd.Series(dtype=float)), errors="coerce").fillna(0)
    reset_completed = pd.to_numeric(reset_metrics_df.get("reset_completed_count", pd.Series(dtype=float)), errors="coerce").fillna(0)
    reset_coherent = reset_failed == 0 and len(reset_metrics_df) == 6 and bool((reset_started >= 1).all()) and bool((reset_completed >= 1).all())
    add("reset_sequences_coherent", reset_coherent, "6 started/completed sequences and 0 failures", f"rows={len(reset_metrics_df)} failed={reset_failed}")
    validation_failures = int(condition_validation_df.get("severity", pd.Series(dtype=str)).fillna("").astype(str).str.lower().eq("fail").sum())
    add("condition_validation_failures", validation_failures == 0, 0, validation_failures)

    generated = set(map(str, manifest.get("generated_files") or []))
    actual_files = {path.name for path in data.run_dir.iterdir() if path.is_file()}
    add("manifest_generated_files_present", bool(generated) and generated == actual_files, sorted(generated), sorted(actual_files))

    file_index_path = data.run_dir / "file_index.csv"
    indexed_paths_present = False
    index_size_consistent = False
    index_details = "file_index.csv missing"
    if file_index_path.exists():
        index_df = pd.read_csv(file_index_path, encoding="utf-8-sig")
        indexed_paths = [str(item) for item in index_df.get("relative_path", pd.Series(dtype=str)).dropna()]
        indexed_paths_present = bool(indexed_paths) and all((data.run_dir / item).is_file() for item in indexed_paths)
        mismatches = []
        for _, item in index_df.iterrows():
            relative = str(item.get("relative_path", ""))
            target = data.run_dir / relative
            if not relative or not target.is_file():
                continue
            indexed_size = int(item.get("size_bytes", -1))
            actual_size = target.stat().st_size
            if indexed_size != actual_size:
                mismatches.append((relative, indexed_size, actual_size, actual_size - indexed_size))
        if not mismatches:
            index_size_consistent = True
            index_details = "all indexed sizes exact"
        else:
            index_details = str(mismatches)
    add("file_index_paths_present", indexed_paths_present, "all indexed paths exist", indexed_paths_present)
    add("file_index_sizes_consistent", index_size_consistent, "all indexed sizes exact", index_size_consistent, index_details)

    prior_failures = sum(row["status"] == "FAIL" for row in rows)
    add(
        "external_or_internal_integrity_evidence",
        data.validation_path is not None or prior_failures == 0,
        "external validation or complete strict internal evidence",
        f"external_validation_present={data.validation_path is not None}; strict_failures={prior_failures}",
    )
    return pd.DataFrame(rows)


def write_acceptance_markdown(df: pd.DataFrame, path: Path) -> None:
    failures = int(df["status"].eq("FAIL").sum()) if not df.empty else 1
    lines = [
        "# P47A Offline Acceptance Validation",
        "",
        f"- status: `{'PASS' if failures == 0 else 'FAIL'}`",
        f"- criteria: `{len(df)}`",
        f"- failures: `{failures}`",
        "",
    ]
    lines.extend(_markdown_table(df))
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def _acceptance_bool(value):
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


def build_run_integrity_report(
    data,
    trial_metrics_df: pd.DataFrame,
    unassigned_events_df: pd.DataFrame,
    acceptance_df: pd.DataFrame | None = None,
) -> pd.DataFrame:
    validation = data.validation or {}
    warnings = validation.get("warnings", []) or []
    red_flags = validation.get("red_flags", []) or []
    invalid_trials = []
    if not trial_metrics_df.empty and "valid_for_analysis" in trial_metrics_df.columns:
        invalid_trials = trial_metrics_df.loc[~trial_metrics_df["valid_for_analysis"].fillna(False).astype(bool), "trial_id"].dropna().astype(str).tolist()
    conditions = []
    if not trial_metrics_df.empty and "condition_id" in trial_metrics_df.columns:
        conditions = sorted(trial_metrics_df["condition_id"].dropna().astype(str).unique().tolist())
    resolution_error_count = int(trial_metrics_df.get("condition_resolution_errors", pd.Series(dtype=str)).fillna("").astype(str).str.len().gt(0).sum()) if not trial_metrics_df.empty else 0
    structural_errors = []
    if data.jsonl_errors:
        structural_errors.append(f"jsonl_parse_errors:{len(data.jsonl_errors)}")
    if data.csv_warnings:
        structural_errors.append(f"csv_warnings:{len(data.csv_warnings)}")
    if invalid_trials:
        structural_errors.append(f"invalid_trials:{len(invalid_trials)}")
    if resolution_error_count:
        structural_errors.append(f"condition_resolution_errors:{resolution_error_count}")
    if acceptance_df is not None and not acceptance_df.empty:
        structural_errors.extend(
            f"acceptance:{criterion}"
            for criterion in acceptance_df.loc[acceptance_df["status"] == "FAIL", "criterion"].astype(str)
        )
    row = {
        "run_id": data.manifest.get("run_id") or validation.get("run_id"),
        "scene": data.manifest.get("scene") or validation.get("scene"),
        "validation_status": validation.get("status", "not_found"),
        "external_validation_present": data.validation_path is not None,
        "structural_integrity_status": "Pass" if not structural_errors else "Fail",
        "structural_error_count": len(structural_errors),
        "structural_errors": "; ".join(structural_errors),
        "red_flag_count": len(red_flags),
        "warning_count": len(warnings),
        "main_warnings": "; ".join(map(str, warnings[:20])),
        "jsonl_parse_error_count": len(data.jsonl_errors),
        "jsonl_parse_errors": "; ".join(f"{error.path.name}:{error.line_number}:{error.message}" for error in data.jsonl_errors[:20]),
        "csv_warning_count": len(data.csv_warnings),
        "csv_warnings": "; ".join(data.csv_warnings[:20]),
        "unassigned_event_count": len(unassigned_events_df),
        "invalid_trial_count": len(invalid_trials),
        "invalid_trials": "; ".join(invalid_trials),
        "conditions_detected": "; ".join(conditions),
        "condition_resolution_error_count": resolution_error_count,
        "condition_resolution_warning_count": int(trial_metrics_df.get("condition_resolution_warnings", pd.Series(dtype=str)).fillna("").astype(str).str.len().gt(0).sum()) if not trial_metrics_df.empty else 0,
        "analysis_eligible": len(red_flags) == 0 and not structural_errors,
    }
    return pd.DataFrame([row])


def write_condition_validation_json(df: pd.DataFrame, path: Path) -> None:
    records = df.fillna("").to_dict(orient="records") if not df.empty else []
    path.write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding="utf-8")


def write_condition_validation_markdown(validation_df: pd.DataFrame, reporting_df: pd.DataFrame, path: Path) -> None:
    lines = [
        "# Condition Validation Report",
        "",
        "## Reporting Summary",
        "",
    ]
    lines.extend(_markdown_table(reporting_df))
    lines.extend(["", "## Aggregate Validation", ""])
    lines.extend(_markdown_table(validation_df))
    lines.append("")
    path.write_text("\n".join(lines), encoding="utf-8")


def write_integrity_markdown(
    data,
    integrity_df: pd.DataFrame,
    trial_metrics_df: pd.DataFrame,
    unassigned_events_df: pd.DataFrame,
    path: Path,
) -> None:
    row = integrity_df.iloc[0].to_dict() if not integrity_df.empty else {}
    validation = data.validation or {}
    warnings = validation.get("warnings", []) or []
    red_flags = validation.get("red_flags", []) or []
    lines = [
        "# Run Integrity Report",
        "",
        f"- run_id: `{row.get('run_id', '')}`",
        f"- scene: `{row.get('scene', '')}`",
        f"- validation_status: `{row.get('validation_status', 'not_found')}`",
        f"- red_flags: `{row.get('red_flag_count', 0)}`",
        f"- warnings: `{row.get('warning_count', 0)}`",
        f"- JSONL parse errors: `{row.get('jsonl_parse_error_count', 0)}`",
        f"- CSV warnings: `{row.get('csv_warning_count', 0)}`",
        f"- events without real trial_id: `{row.get('unassigned_event_count', 0)}`",
        f"- invalid trials: `{row.get('invalid_trial_count', 0)}`",
        f"- conditions_detected: `{row.get('conditions_detected', '')}`",
        f"- condition resolution errors: `{row.get('condition_resolution_error_count', 0)}`",
        f"- condition resolution warnings: `{row.get('condition_resolution_warning_count', 0)}`",
        f"- eligible_for_main_analysis: `{row.get('analysis_eligible', False)}`",
        "",
    ]
    if red_flags:
        lines.append("## Red Flags")
        lines.extend(f"- {item}" for item in red_flags)
        lines.append("")
    lines.append("## Main Warnings")
    if warnings:
        lines.extend(f"- {item}" for item in warnings[:20])
        if len(warnings) > 20:
            lines.append(f"- ... {len(warnings) - 20} additional warnings omitted")
    else:
        lines.append("No validator warnings.")
    lines.append("")
    lines.append("## Parser Diagnostics")
    if data.jsonl_errors:
        lines.extend(f"- JSONL `{error.path.name}:{error.line_number}` {error.message}" for error in data.jsonl_errors[:20])
    else:
        lines.append("- JSONL parse errors: 0")
    if data.csv_warnings:
        lines.extend(f"- CSV {warning}" for warning in data.csv_warnings[:20])
    else:
        lines.append("- CSV warnings: 0")
    lines.append("")
    lines.append("## Trial Validity")
    invalid = pd.DataFrame()
    if not trial_metrics_df.empty and "valid_for_analysis" in trial_metrics_df.columns:
        invalid = trial_metrics_df[~trial_metrics_df["valid_for_analysis"].fillna(False).astype(bool)]
    if invalid.empty:
        lines.append("No invalid trials detected.")
    else:
        for _, trial in invalid.iterrows():
            lines.append(f"- `{trial.get('trial_id', '')}` condition=`{trial.get('condition_id', '')}` reason=`{trial.get('failure_reason', '')}`")
    lines.append("")
    lines.append("## Unassigned Events")
    if unassigned_events_df.empty:
        lines.append("No pretrial/postrun/unassigned events detected after timestamp-window attribution.")
    else:
        counts = unassigned_events_df.get("event_type", pd.Series(dtype=str)).fillna("").astype(str).value_counts().head(20)
        lines.append(f"Total unassigned events: `{len(unassigned_events_df)}`")
        for event_type, count in counts.items():
            lines.append(f"- `{event_type}`: {count}")
    lines.append("")
    path.write_text("\n".join(lines), encoding="utf-8")


def write_summary_markdown(data, trial_metrics_df: pd.DataFrame, condition_metrics_df: pd.DataFrame, path: Path) -> None:
    validation = data.validation or {}
    warnings = validation.get("warnings", []) or []
    red_flags = validation.get("red_flags", []) or []
    lines = [
        "# Offline Experiment Analysis Summary",
        "",
        f"- run_id: `{data.manifest.get('run_id', '')}`",
        f"- scene: `{data.manifest.get('scene') or validation.get('scene', '')}`",
        f"- run_dir: `{data.run_dir}`",
        f"- events: `{len(data.events_df)}`",
        f"- samples: `{len(data.samples_df)}`",
        f"- trials_detected: `{len(trial_metrics_df)}`",
        f"- conditions_detected: `{', '.join(map(str, sorted(trial_metrics_df['condition_id'].dropna().unique()))) if not trial_metrics_df.empty else ''}`",
        f"- validation_status: `{validation.get('status', 'not_found')}`",
        f"- validation_red_flags: `{len(red_flags)}`",
        f"- validation_warnings: `{len(warnings)}`",
        f"- metric_sufficiency_passes: `{len(validation.get('metric_sufficiency_passes', []) or [])}`",
        f"- metric_sufficiency_warnings: `{len(validation.get('metric_sufficiency_warnings', []) or [])}`",
        "",
        "## Validator Notes",
        "",
    ]
    if red_flags:
        lines.append("### Red Flags")
        lines.extend(f"- {item}" for item in red_flags)
        lines.append("")
    if warnings:
        lines.append("### Main Warnings")
        lines.extend(f"- {item}" for item in warnings[:20])
        if len(warnings) > 20:
            lines.append(f"- ... {len(warnings) - 20} additional warnings omitted")
        lines.append("")
    if data.jsonl_errors:
        lines.append("### JSONL Parse Errors")
        for error in data.jsonl_errors[:20]:
            lines.append(f"- `{error.path.name}:{error.line_number}` {error.message}")
        if len(data.jsonl_errors) > 20:
            lines.append(f"- ... {len(data.jsonl_errors) - 20} additional JSONL errors omitted")
        lines.append("")
    lines.append("## Condition Summary")
    lines.append("")
    lines.extend(_markdown_table(condition_metrics_df))
    lines.append("")
    path.write_text("\n".join(lines), encoding="utf-8")


def _markdown_table(df: pd.DataFrame) -> list[str]:
    if df.empty:
        return ["No condition metrics available."]
    display = df.copy()
    for column in display.columns:
        if pd.api.types.is_float_dtype(display[column]):
            display[column] = display[column].map(lambda value: f"{value:.3f}")
    columns = list(display.columns)
    lines = [
        "| " + " | ".join(columns) + " |",
        "| " + " | ".join("---" for _ in columns) + " |",
    ]
    for _, row in display.iterrows():
        lines.append("| " + " | ".join(str(row[col]) for col in columns) + " |")
    return lines


def run_self_test() -> int:
    # order_checksum_v1 must hold before anything else: if the checksum or the
    # prefix table drift, the questionnaire cannot be linked to its session.
    questionnaire_code_self_test()
    print("[self-test] questionnaire_code order_checksum_v1: OK")

    with tempfile.TemporaryDirectory(prefix="offline_experiment_selftest_") as tmp:
        root = Path(tmp)
        run_dir = root / "synthetic_run"
        run_dir.mkdir()
        manifest = {
            "run_id": "synthetic_run",
            "scene": "autonomous_demo_step22_multimodal_bridge",
            "events_file": "events.jsonl",
            "samples_file": "samples.csv",
            "session_trials_csv_file": "session_trials.csv",
            "session_trials_jsonl_file": "session_trials.jsonl",
            "log_context": "voice_only",
        }
        (run_dir / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        (run_dir / "samples.csv").write_text("timestamp_wall,run_id,run_elapsed_time\n2026-06-03T21:00:00Z,synthetic_run,0\n", encoding="utf-8")
        (run_dir / "session_trials.csv").write_text(
            "session_id,participant_id,condition_id,condition_log_context,log_context,trial_id,trial_index,run_id,scene,success,aborted,terminal_state,total_duration_seconds,timestamp_start,timestamp_end,valid_for_analysis,failure_reason,condition_name,robot_enabled,voice_enabled,assistance_mode,round_id,expected_boxes\n"
            "S001,P001,C00_robot_off_voice_off,C00_robot_off_voice_off,voice_only,trial_c00,1,synthetic_run,autonomous_demo_step22_multimodal_bridge,true,false,completed,12.5,2026-06-03T21:00:00Z,2026-06-03T21:00:12Z,true,,Robot OFF + Voice OFF,false,false,Disabled,S001_trial_c00_round,5\n"
            "S001,P001,C10_robot_on_voice_off,C10_robot_on_voice_off,voice_only,trial_c10,2,synthetic_run,autonomous_demo_step22_multimodal_bridge,true,false,completed,13.0,2026-06-03T21:01:00Z,2026-06-03T21:01:13Z,true,,Robot ON + Voice OFF,true,false,AssistedSelection,S001_trial_c10_round,5\n"
            "S001,P001,C11_robot_on_voice_on,C11_robot_on_voice_on,voice_only,trial_c11,3,synthetic_run,autonomous_demo_step22_multimodal_bridge,true,false,completed,14.0,2026-06-03T21:02:00Z,2026-06-03T21:02:14Z,true,,Robot ON + Voice ON,true,true,AssistedSelection,S001_trial_c11_round,5\n"
            "S001,P001,,,,trial_legacy,4,synthetic_run,autonomous_demo_step22_multimodal_bridge,true,false,completed,9.0,2026-06-03T21:03:00Z,2026-06-03T21:03:09Z,true,,Legacy C10,,,,S001_trial_legacy_round,1\n"
            "S001,P001,C10_robot_on_voice_off,C10_robot_on_voice_off,voice_only,trial_manual,5,synthetic_run,autonomous_demo_step22_multimodal_bridge,false,true,manually_ended_incomplete,5.0,2026-06-03T21:04:00Z,2026-06-03T21:04:05Z,false,manual_stop,Robot ON + Voice OFF,true,false,AssistedSelection,S001_trial_manual_round,5\n",
            encoding="utf-8",
        )
        (run_dir / "session_trials.jsonl").write_text("", encoding="utf-8")
        (run_dir / "trial_c00_trial_summary.csv").write_text(
            "runId,participantId,sessionId,trialId,trialIndex,conditionId,conditionName,terminalState,success,aborted,totalDurationSeconds,timestampStart,timestampEnd,robotEnabled,voiceEnabled,assistanceMode,roundId\n"
            "synthetic_run,P001,S001,trial_c00,1,C00_robot_off_voice_off,Robot OFF + Voice OFF,completed,true,false,12.5,2026-06-03T21:00:00Z,2026-06-03T21:00:12Z,false,false,Disabled,S001_trial_c00_round\n",
            encoding="utf-8",
        )
        events = [
            {"timestamp_wall": "2026-06-03T20:59:59Z", "run_id": "synthetic_run", "event_type": "experiment_run_started", "payload": {}},
        ]
        for index in range(5):
            events.append({"timestamp_wall": f"2026-06-03T21:00:0{index + 1}Z", "run_id": "synthetic_run", "event_type": "round_correct_deposit_registered", "payload": {"trial_id": "trial_c00"}})
        events.extend(
            [
                {"timestamp_wall": "2026-06-03T21:00:06Z", "run_id": "synthetic_run", "event_type": "experiment_trial_reset_started", "payload": {"trial_id": "trial_c00"}},
                {"timestamp_wall": "2026-06-03T21:00:07Z", "run_id": "synthetic_run", "event_type": "experiment_trial_reset_applied", "payload": {"trial_id": "trial_c00"}},
                {"timestamp_wall": "2026-06-03T21:00:08Z", "run_id": "synthetic_run", "event_type": "experiment_trial_reset_completed", "payload": {"trial_id": "trial_c00"}},
            ]
        )
        for index in range(5):
            events.extend(
                [
                    {"timestamp_wall": f"2026-06-03T21:01:0{index + 1}.100Z", "run_id": "synthetic_run", "event_type": "autonomous_selection_policy_evaluated", "payload": {"trial_id": "trial_c10", "policy": "LocalPickCost", "selection_metric": "robot_to_pickup_cost", "robot_to_pickup_cost": 1.2, "benchmark_pick_place_total_cost": 3.4, "benchmark_pick_place_total_active": False}},
                    {"timestamp_wall": f"2026-06-03T21:01:0{index + 1}.200Z", "run_id": "synthetic_run", "event_type": "autonomous_selection_candidate_selected", "payload": {"trial_id": "trial_c10", "policy": "LocalPickCost", "selection_metric": "robot_to_pickup_cost", "robot_to_pickup_cost": 1.2, "benchmark_pick_place_total_cost": 3.4, "benchmark_pick_place_total_active": False}},
                    {"timestamp_wall": f"2026-06-03T21:01:0{index + 1}.300Z", "run_id": "synthetic_run", "event_type": "manipulation_pick_succeeded", "payload": {"trial_id": "trial_c10"}},
                    {"timestamp_wall": f"2026-06-03T21:01:0{index + 1}.400Z", "run_id": "synthetic_run", "event_type": "manipulation_place_succeeded", "payload": {"trial_id": "trial_c10"}},
                    {"timestamp_wall": f"2026-06-03T21:01:0{index + 1}.500Z", "run_id": "synthetic_run", "event_type": "round_correct_deposit_registered", "payload": {"trial_id": "trial_c10"}},
                ]
            )
        events.extend(
            [
                {"timestamp_wall": "2026-06-03T21:01:11Z", "run_id": "synthetic_run", "event_type": "voice_command_normalized", "payload": {"trial_id": "trial_c10"}},
                {"timestamp_wall": "2026-06-03T21:01:12Z", "run_id": "synthetic_run", "event_type": "voice_command_blocked_by_condition", "payload": {"trial_id": "trial_c10", "reason": "voice_disabled_by_condition", "status": "blocked"}},
                {"timestamp_wall": "2026-06-03T21:01:13Z", "run_id": "synthetic_run", "event_type": "startup_alignment_soft_exit_after_no_progress", "payload": {"trial_id": "trial_c10"}},
                {"timestamp_wall": "2026-06-03T21:02:01Z", "run_id": "synthetic_run", "event_type": "voice_command_execution", "payload": {"trial_id": "trial_c11", "status": "accepted"}},
                {"timestamp_wall": "2026-06-03T21:02:02Z", "run_id": "synthetic_run", "event_type": "autonomous_selection_candidate_selected", "payload": {"trial_id": "trial_c11", "policy": "LocalPickCost", "selection_metric": "robot_to_pickup_cost", "robot_to_pickup_cost": 2.0, "benchmark_pick_place_total_cost": 4.0, "benchmark_pick_place_total_active": False}},
                {"timestamp_wall": "2026-06-03T21:02:03Z", "run_id": "synthetic_run", "event_type": "autonomous_selection_policy_evaluated", "payload": {"trial_id": "trial_c11", "policy": "PickPlaceCost", "selection_metric": "pick_place_total_cost", "benchmark_pick_place_total_active": True}},
                {"timestamp_wall": "2026-06-03T21:02:04Z", "run_id": "synthetic_run", "event_type": "round_correct_deposit_registered", "payload": {"trial_id": "trial_c11"}},
                {"timestamp_wall": "2026-06-03T21:03:01Z", "run_id": "synthetic_run", "event_type": "round_correct_deposit_registered", "payload": {"trial_id": "trial_legacy"}},
                {"timestamp_wall": "2026-06-03T21:04:01Z", "run_id": "synthetic_run", "event_type": "autonomy_request_submitted", "payload": {"trial_id": "trial_manual"}},
                {"timestamp_wall": "2026-06-03T21:04:02Z", "run_id": "synthetic_run", "event_type": "robot_pose_reset_failed", "payload": {"trial_id": "trial_manual"}},
            ]
        )
        (run_dir / "events.jsonl").write_text("\n".join(json.dumps(item) for item in events) + "\n{bad-json\n", encoding="utf-8-sig")
        (run_dir / "experiment_data_validation.json").write_text(
            json.dumps({"status": "Warn", "run_id": "synthetic_run", "warnings": ["synthetic_warning"], "red_flags": []}),
            encoding="utf-8",
        )
        out_dir = root / "out"
        code = main(["--run-dir", str(run_dir), "--out-dir", str(out_dir)])
        trial_metrics = pd.read_csv(out_dir / "trial_metrics.csv")
        condition_metrics = pd.read_csv(out_dir / "condition_metrics.csv")
        event_counts = pd.read_csv(out_dir / "event_counts_by_trial.csv")
        condition_reporting = pd.read_csv(out_dir / "condition_reporting_summary.csv")
        condition_validation = pd.read_csv(out_dir / "condition_validation_report.csv")
        voice_metrics = pd.read_csv(out_dir / "voice_metrics_by_trial.csv")
        reset_metrics = pd.read_csv(out_dir / "reset_metrics_by_trial.csv")
        unassigned_events = pd.read_csv(out_dir / "pretrial_or_unassigned_events.csv")
        integrity = pd.read_csv(out_dir / "run_integrity_report.csv")
        assert code == 0
        assert len(trial_metrics) == 5
        assert set(trial_metrics["condition_id"].dropna().astype(str)) == {"C00_robot_off_voice_off", "C10_robot_on_voice_off", "C11_robot_on_voice_on"}
        assert set(condition_metrics["condition_id"].dropna().astype(str)) == {"C00_robot_off_voice_off", "C10_robot_on_voice_off", "C11_robot_on_voice_on"}
        assert set(event_counts["trial_id"].dropna().astype(str)) == {"trial_c00", "trial_c10", "trial_c11", "trial_legacy", "trial_manual"}
        c00 = trial_metrics[trial_metrics["trial_id"] == "trial_c00"].iloc[0]
        c10 = trial_metrics[trial_metrics["trial_id"] == "trial_c10"].iloc[0]
        c11 = trial_metrics[trial_metrics["trial_id"] == "trial_c11"].iloc[0]
        legacy = trial_metrics[trial_metrics["trial_id"] == "trial_legacy"].iloc[0]
        manual = trial_metrics[trial_metrics["trial_id"] == "trial_manual"].iloc[0]
        assert c00["condition_id"] == "C00_robot_off_voice_off"
        assert int(c00["autonomy_request_count"]) == 0
        assert str(reset_metrics.loc[reset_metrics["trial_id"] == "trial_c00", "reset_consistent"].iloc[0]).lower() == "true"
        assert c10["condition_id"] == "C10_robot_on_voice_off"
        assert str(c10["condition_resolution_warnings"]).find("legacy_log_context_voice_only") >= 0
        assert int(c10["voice_blocked_by_condition_count"]) == 1
        assert int(c10["voice_command_executed_count"]) == 0
        assert int(c10["p41_local_pick_cost_event_count"]) >= 1
        assert int(c10["p41_robot_to_pickup_metric_event_count"]) >= 1
        assert int(c10["pick_place_benchmark_diagnostic_count"]) >= 1
        assert int(c10["startup_alignment_soft_exit_count"]) == 1
        assert c11["condition_id"] == "C11_robot_on_voice_on"
        assert int(c11["voice_command_executed_count"]) == 1
        assert int(c11["pick_place_runtime_invalid_count"]) == 1
        assert legacy["condition_id"] == "C10_robot_on_voice_off"
        assert str(legacy["condition_resolution_source"]) == "manifest.legacy.log_context"
        assert str(manual["valid_for_analysis"]).lower() == "false"
        assert int(reset_metrics.loc[reset_metrics["trial_id"] == "trial_manual", "reset_failed_count"].iloc[0]) == 1
        assert "p41_pick_place_not_runtime" in set(condition_validation["rule"].astype(str))
        assert "manual_partial_not_functional_failure" in set(condition_validation["rule"].astype(str))
        assert int(condition_reporting.loc[condition_reporting["condition_id"] == "C10_robot_on_voice_off", "voice_blocked_or_rejected_count"].iloc[0]) >= 1
        assert len(unassigned_events) == 1
        loaded_self_test = load_experiment_run(run_dir)
        normalized = loaded_self_test.trial_summaries_df
        assert normalized.iloc[0]["trial_id"] == "trial_c00"
        assert normalized.iloc[0]["condition_id"] == "C00_robot_off_voice_off"
        assert normalized.iloc[0]["terminal_state"] == "completed"
        assert not bool(loaded_self_test.runs_df.iloc[0]["analysis_eligible"])
        assert str(integrity.iloc[0]["structural_integrity_status"]) == "Fail"
        assert not bool(integrity.iloc[0]["analysis_eligible"])
        strict_out = root / "strict_rejected"
        assert main(["--run-dir", str(run_dir), "--out-dir", str(strict_out), "--strict-acceptance"]) == 2
        strict_report = pd.read_csv(strict_out / "acceptance_validation_report.csv")
        assert strict_report.loc[strict_report["criterion"] == "jsonl_parse_errors", "status"].iloc[0] == "FAIL"
        assert (out_dir / "analysis_summary.md").exists()
        assert (out_dir / "run_integrity_report.csv").exists()
        assert (out_dir / "run_integrity_report.md").exists()
        assert (out_dir / "event_counts_by_condition.csv").exists()
        assert (out_dir / "condition_reporting_summary.csv").exists()
        assert (out_dir / "condition_validation_report.md").exists()
        assert (out_dir / "condition_validation_report.json").exists()
        exported_root = root / "ExperimentData"
        exported_participant_id = "U20260627_120000"
        exported_session_id = "S20260627_120000"
        exported_session = exported_root / exported_participant_id / exported_session_id
        shutil.copytree(run_dir, exported_session)
        session_trials_text = (exported_session / "session_trials.csv").read_text(encoding="utf-8")
        (exported_session / "session_trials.csv").write_text(
            session_trials_text.replace("S001,P001,", f"{exported_session_id},{exported_participant_id},"),
            encoding="utf-8",
        )
        summary_text = (exported_session / "trial_c00_trial_summary.csv").read_text(encoding="utf-8")
        (exported_session / "trial_c00_trial_summary.csv").write_text(
            summary_text.replace("synthetic_run,P001,S001,", f"synthetic_run,{exported_participant_id},{exported_session_id},"),
            encoding="utf-8",
        )
        p45f_manifest = dict(manifest)
        p45f_manifest.update(
            {
                "p45f_storage_schema_version": "P45F",
                "participant_id": exported_participant_id,
                "session_id": exported_session_id,
                "condition_order": ["C00_robot_off_voice_off", "C10_robot_on_voice_off", "C11_robot_on_voice_on"],
                "conditions": ["C00_robot_off_voice_off", "C10_robot_on_voice_off", "C11_robot_on_voice_on"],
            }
        )
        (exported_session / "session_manifest.json").write_text(json.dumps(p45f_manifest), encoding="utf-8")
        assert main(["--input", str(exported_session), "--out-dir", str(root / "exported_session_out"), "--allow-red-flags"]) == 0
        assert (root / "exported_session_out" / "trial_metrics.csv").exists()
        assert main(["--input-root", str(exported_root), "--out-dir", str(root / "exported_root_out"), "--allow-red-flags"]) == 0
        assert (root / "exported_root_out" / "trial_metrics.csv").exists()
        mixed_payload_session = root / "bad_mixed_payload" / "U20260627_120004" / "S20260627_120004"
        shutil.copytree(exported_session, mixed_payload_session)
        mixed_manifest = dict(p45f_manifest)
        mixed_manifest.update({"participant_id": "U20260627_120004", "session_id": "S20260627_120004"})
        (mixed_payload_session / "session_manifest.json").write_text(json.dumps(mixed_manifest), encoding="utf-8")
        with (mixed_payload_session / "events.jsonl").open("a", encoding="utf-8") as handle:
            handle.write(
                json.dumps(
                    {
                        "timestamp_unity": 1.0,
                        "run_elapsed_time": 1.0,
                        "timestamp_wall": "2026-06-27T20:11:31Z",
                        "run_id": "synthetic_run",
                        "event_type": "experiment_orchestrator_round_completion_subscription_added",
                        "payload": {"participant_id": "P001", "session_id": ""},
                    }
                )
                + "\n"
            )
        try:
            main(["--input", str(mixed_payload_session), "--out-dir", str(root / "blocked_mixed_payload")])
            raise AssertionError("Legacy P001 payload should block P45F export")
        except (RuntimeError, ValueError):
            pass
        p001_session = root / "bad_p001" / "P001" / "S20260627_120001"
        shutil.copytree(run_dir, p001_session)
        p001_manifest = dict(p45f_manifest)
        p001_manifest.update({"participant_id": "P001", "session_id": "S20260627_120001"})
        (p001_session / "session_manifest.json").write_text(json.dumps(p001_manifest), encoding="utf-8")
        try:
            main(["--input", str(p001_session), "--out-dir", str(root / "blocked_p001")])
            raise AssertionError("P001 should block P45F export")
        except (RuntimeError, ValueError):
            pass
        c01_session = root / "bad_c01" / "U20260627_120002" / "S20260627_120002"
        shutil.copytree(exported_session, c01_session)
        c01_manifest = dict(p45f_manifest)
        c01_manifest.update(
            {
                "participant_id": "U20260627_120002",
                "session_id": "S20260627_120002",
                "condition_order": ["C00_robot_off_voice_off", "C01_robot_off_voice_on", "C11_robot_on_voice_on"],
            }
        )
        (c01_session / "session_manifest.json").write_text(json.dumps(c01_manifest), encoding="utf-8")
        try:
            main(["--input", str(c01_session), "--out-dir", str(root / "blocked_c01")])
            raise AssertionError("C01 should block P45F export")
        except (RuntimeError, ValueError):
            pass
        anomalous_root = root / "anomalous_ExperimentData"
        anomalous_root.mkdir()
        for index in range(101):
            (anomalous_root / f"U20260627_12{index:04d}").mkdir()
        anomaly_errors = validate_p45f_input_root_shape(anomalous_root)
        assert any(str(error).startswith("participant_directory_proliferation:") for error in anomaly_errors)
        diagnostic_root = root / "diagnostic_ExperimentData"
        (diagnostic_root / "U20260627_120003" / "S20260627_120003").mkdir(parents=True)
        (diagnostic_root / "VoiceFeedbackDiagnostics").mkdir()
        (diagnostic_root / "AsrDiagnostics").mkdir()
        assert validate_p45f_input_root_shape(diagnostic_root) == []
        red_flag_dir = root / "red_flag_run"
        red_flag_dir.mkdir()
        for source in ["manifest.json", "samples.csv", "session_trials.csv", "session_trials.jsonl", "events.jsonl"]:
            (red_flag_dir / source).write_bytes((run_dir / source).read_bytes())
        (red_flag_dir / "experiment_data_validation.json").write_text(
            json.dumps({"status": "Fail", "run_id": "synthetic_run", "warnings": [], "red_flags": ["synthetic_red_flag"]}),
            encoding="utf-8",
        )
        try:
            main(["--run-dir", str(red_flag_dir), "--out-dir", str(root / "blocked")])
            raise AssertionError("red flags should block export")
        except RuntimeError:
            pass
        assert main(["--run-dir", str(red_flag_dir), "--out-dir", str(root / "allowed"), "--allow-red-flags"]) == 0
    print("Self-test passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
