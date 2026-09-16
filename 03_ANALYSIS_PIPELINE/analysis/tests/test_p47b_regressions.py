from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

import pandas as pd


ANALYSIS_ROOT = Path(__file__).resolve().parents[1]
if str(ANALYSIS_ROOT) not in sys.path:
    sys.path.insert(0, str(ANALYSIS_ROOT))

from offline_experiment_analysis import main as offline_main, write_batch_dataset
from src.condition_resolution import resolve_condition_from_row
from src.experiment_loader import load_experiment_run
from src.experiment_metrics import build_trial_metrics


class P47BOfflineRegressionTests(unittest.TestCase):
    R1_DATASET = ANALYSIS_ROOT.parent / "Logs" / "P47B_R1_PhysicalRegression_20260813_111309" / "After" / "ExperimentData_NewSessions"

    def test_saved_exit_restart_joins_attempt_files_and_supersedes_only_restarted_condition(self) -> None:
        with tempfile.TemporaryDirectory(prefix="p47b_restart_") as temp:
            run_dir = Path(temp)
            self._write_resumed_session(run_dir)

            data = load_experiment_run(run_dir)
            trials, _ = build_trial_metrics(data)

            self.assertEqual(2, data.events_df["_source_file"].nunique())
            self.assertEqual(2, data.samples_df["_source_file"].nunique())
            self.assertEqual("completed", data.runs_df.iloc[0]["session_history_status"])
            original = trials.loc[trials["trial_id"] == "trial_003"].iloc[0]
            resumed = trials.loc[trials["trial_id"] == "trial_003_dup01"].iloc[0]
            c00 = trials.loc[trials["trial_id"] == "trial_001"].iloc[0]
            self.assertFalse(bool(original["valid_for_analysis"]))
            self.assertTrue(bool(original["superseded_by_restart"]))
            self.assertTrue(bool(resumed["valid_for_analysis"]))
            self.assertEqual("trial_003", resumed["canonical_trial_id"])
            self.assertTrue(bool(c00["valid_for_analysis"]))
            resumed_events = data.events_df[data.events_df["event_type"] == "resumed_trial_event"]
            self.assertEqual(["trial_003_dup01"], resumed_events["trial_id"].tolist())
            stale = data.events_df[data.events_df["event_type"] == "stale_pretrial_context"].iloc[0]
            self.assertTrue(pd.isna(stale["trial_id"]))

    def test_batch_linkage_excludes_restarted_session_and_requires_unique_kobo_code(self) -> None:
        with tempfile.TemporaryDirectory(prefix="p47b_batch_") as temp:
            root = Path(temp)
            run_outputs = []
            for session_id, code, status, valid in [
                ("S20260811_100000", "ABC123", "completed", True),
                ("S20260811_100100", "ZZZ999", "restarted", True),
            ]:
                run_dir = root / "source" / session_id
                out_dir = root / "output" / session_id
                run_dir.mkdir(parents=True)
                out_dir.mkdir(parents=True)
                payload = run_dir / "events.jsonl"
                payload.write_text("{}\n", encoding="utf-8")
                pd.DataFrame([{
                    "relative_path": payload.name,
                    "size_bytes": payload.stat().st_size,
                }]).to_csv(run_dir / "file_index.csv", index=False)
                pd.DataFrame([{
                    "participant_id": "U" + session_id[1:],
                    "session_id": session_id,
                    "questionnaire_code": code,
                    "session_history_status": status,
                    "analysis_eligible": status == "completed",
                    "event_source_file_count": 1,
                    "sample_source_file_count": 1,
                    "run_dir": str(run_dir),
                }]).to_csv(out_dir / "run_metadata.csv", index=False)
                pd.DataFrame([{
                    "trial_id": "trial_001",
                    "valid_for_analysis": valid,
                    "failure_reason": "",
                    "superseded_by_restart": False,
                }]).to_csv(out_dir / "trial_metrics.csv", index=False)
                run_outputs.append((run_dir, out_dir))

            kobo_path = root / "kobo.xlsx"
            pd.DataFrame([{
                "code_normalized": "ABC123",
                "_id": 1,
                "_uuid": "uuid-1",
            }]).to_excel(kobo_path, index=False)
            args = SimpleNamespace(
                kobo_xlsx=kobo_path,
                expect_linked_sessions=1,
                expect_valid_trials=1,
            )

            self.assertTrue(write_batch_dataset(run_outputs, args, root / "batch"))
            linkage = pd.read_csv(root / "batch" / "kobo_session_linkage.csv")
            analytic = pd.read_csv(root / "batch" / "analytic_trial_metrics.csv")
            self.assertEqual(["S20260811_100000"], linkage.loc[linkage["analysis_included"], "session_id"].tolist())
            self.assertEqual(1, len(analytic))

    def test_multicondition_trials_use_per_condition_maps_not_terminal_manifest_snapshot(self) -> None:
        manifest = {
            "condition_order_ids": [
                "C10_robot_on_voice_off",
                "C11_robot_on_voice_on",
                "C00_robot_off_voice_off",
            ],
            "condition_id": "C11_robot_on_voice_on",
            "condition_log_context": "C11_robot_on_voice_on",
            "robot_enabled": True,
            "voice_enabled": True,
            "robot_enabled_by_condition": {
                "C00_robot_off_voice_off": False,
                "C10_robot_on_voice_off": True,
                "C11_robot_on_voice_on": True,
            },
            "voice_enabled_by_condition": {
                "C00_robot_off_voice_off": False,
                "C10_robot_on_voice_off": False,
                "C11_robot_on_voice_on": True,
            },
        }
        row = {
            "condition_id": "C10_robot_on_voice_off",
            "robot_enabled": True,
            "voice_enabled": False,
        }

        resolution = resolve_condition_from_row(row, manifest=manifest)

        self.assertEqual("C10_robot_on_voice_off", resolution.condition_id)
        self.assertEqual([], resolution.errors)
        self.assertEqual("C11_robot_on_voice_on", resolution.raw_manifest_condition_id)
        self.assertTrue(resolution.raw_manifest_voice_enabled)

    @unittest.skipUnless(R1_DATASET.is_dir(), "P47B-R1 immutable regression dataset is not available")
    def test_r1_dataset_canonicalization_and_multicondition_consistency(self) -> None:
        with tempfile.TemporaryDirectory(prefix="p47b_r1_pipeline_") as temp:
            output = Path(temp)
            exit_code = offline_main([
                "--input-root", str(self.R1_DATASET),
                "--out-dir", str(output),
                "--expect-linked-sessions", "1",
            ])
            self.assertEqual(0, exit_code)

            all_trials = pd.read_csv(output / "all_session_trial_metrics.csv")
            analytic = pd.read_csv(output / "analytic_trial_metrics.csv")
            completed = all_trials[all_trials["questionnaire_code"].astype(str) == "EUD99P"]
            completed_analytic = analytic[analytic["questionnaire_code"].astype(str) == "EUD99P"]
            self.assertEqual(6, len(completed_analytic))
            self.assertEqual(
                {"trial_001", "trial_002", "trial_003_dup01", "trial_004_dup01", "trial_005", "trial_006"},
                set(completed_analytic["trial_id"].astype(str)),
            )
            self.assertEqual(
                {"trial_003", "trial_004"},
                set(completed.loc[completed["superseded_by_restart"].astype(bool), "canonical_trial_id"].astype(str)),
            )
            for canonical in ["trial_003", "trial_004"]:
                replacements = completed_analytic[completed_analytic["canonical_trial_id"].astype(str) == canonical]
                self.assertEqual(1, len(replacements), canonical)
            self.assertEqual(
                {"trial_001", "trial_002"},
                set(completed_analytic.loc[completed_analytic["trial_index"] < 3, "trial_id"].astype(str)),
            )

            saved_exit_session = "S20260813_095007"
            self.assertEqual(0, len(analytic[analytic["session_id"].astype(str) == saved_exit_session]))
            saved_exit_superseded = all_trials[
                (all_trials["session_id"].astype(str) == saved_exit_session) &
                all_trials["superseded_by_restart"].astype(bool)
            ]
            self.assertEqual(1, len(saved_exit_superseded))
            for session_id in ["S20260813_094823", saved_exit_session]:
                preserved = all_trials[
                    (all_trials["session_id"].astype(str) == session_id) &
                    (all_trials["trial_index"] < 3) &
                    all_trials["valid_for_analysis"].astype(bool) &
                    ~all_trials["superseded_by_restart"].astype(bool)
                ]
                self.assertEqual({"trial_001", "trial_002"}, set(preserved["trial_id"].astype(str)))

            condition_failures = []
            for report in output.rglob("condition_validation_report.csv"):
                rows = pd.read_csv(report)
                condition_failures.extend(rows[
                    (rows["severity"].astype(str) == "Fail") &
                    (rows["rule"].astype(str) == "condition_consistency")
                ].to_dict("records"))
            self.assertEqual([], condition_failures)

    @staticmethod
    def _write_resumed_session(run_dir: Path) -> None:
        manifest = {
            "p45f_storage_schema_version": "P45F",
            "participant_id": "U20260811_081126",
            "session_id": "S20260811_081126",
            "questionnaire_code": "AEKNEW",
            "condition_order_ids": [
                "C00_robot_off_voice_off",
                "C10_robot_on_voice_off",
                "C11_robot_on_voice_on",
            ],
            "events_file": "run_a02_events.jsonl",
            "samples_file": "run_a02_samples.csv",
            "session_trials_csv_file": "session_trials.csv",
        }
        (run_dir / "session_manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        trials = pd.DataFrame([
            {
                "run_id": "run_a01", "session_id": manifest["session_id"], "participant_id": manifest["participant_id"],
                "trial_id": "trial_001", "trial_index": 1, "condition_id": "C00_robot_off_voice_off",
                "timestamp_start": "2026-08-11T08:00:00Z", "timestamp_end": "2026-08-11T08:00:10Z",
                "terminal_state": "completed", "success": True, "aborted": False, "valid_for_analysis": True,
            },
            {
                "run_id": "run_a01", "session_id": manifest["session_id"], "participant_id": manifest["participant_id"],
                "trial_id": "trial_003", "trial_index": 3, "condition_id": "C10_robot_on_voice_off",
                "timestamp_start": "2026-08-11T08:01:00Z", "timestamp_end": "2026-08-11T08:01:10Z",
                "terminal_state": "completed", "success": True, "aborted": False, "valid_for_analysis": True,
            },
            {
                "run_id": "run_a01", "session_id": manifest["session_id"], "participant_id": manifest["participant_id"],
                "trial_id": "trial_004", "trial_index": 4, "condition_id": "C10_robot_on_voice_off",
                "timestamp_start": "2026-08-11T08:02:00Z", "timestamp_end": "2026-08-11T08:02:10Z",
                "terminal_state": "saved_exit_incomplete_condition_restart", "failure_reason": "saved_exit_incomplete_condition_restart",
                "success": False, "aborted": False, "valid_for_analysis": False,
            },
            {
                "run_id": "run_a02", "session_id": manifest["session_id"], "participant_id": manifest["participant_id"],
                "trial_id": "trial_003_dup01", "trial_index": 3, "condition_id": "C10_robot_on_voice_off",
                "timestamp_start": "2026-08-11T08:03:00Z", "timestamp_end": "2026-08-11T08:03:10Z",
                "terminal_state": "completed", "success": True, "aborted": False, "valid_for_analysis": True,
            },
        ])
        trials.to_csv(run_dir / "session_trials.csv", index=False)
        (run_dir / "run_a01_events.jsonl").write_text("\n".join([
            json.dumps({"run_id": "run_a01", "timestamp_wall": "2026-08-11T08:01:05Z", "event_type": "original_trial_event", "trial_id": "trial_003"}),
        ]) + "\n", encoding="utf-8")
        (run_dir / "run_a02_events.jsonl").write_text("\n".join([
            json.dumps({"run_id": "run_a02", "timestamp_wall": "2026-08-11T08:02:30Z", "event_type": "stale_pretrial_context", "trial_id": "trial_002"}),
            json.dumps({"run_id": "run_a02", "timestamp_wall": "2026-08-11T08:02:59Z", "event_type": "experiment_trial_prepare_started", "trial_id": "trial_003"}),
            json.dumps({"run_id": "run_a02", "timestamp_wall": "2026-08-11T08:03:05Z", "event_type": "resumed_trial_event", "trial_id": "trial_003"}),
            json.dumps({"run_id": "run_a02", "timestamp_wall": "2026-08-11T08:04:00Z", "event_type": "experiment_session_completed", "payload": {"session_history_status": "completed"}}),
        ]) + "\n", encoding="utf-8")
        for attempt in ["a01", "a02"]:
            pd.DataFrame([{"run_id": "run_" + attempt, "timestamp_wall": "2026-08-11T08:00:00Z"}]).to_csv(run_dir / f"run_{attempt}_samples.csv", index=False)


if __name__ == "__main__":
    unittest.main()
