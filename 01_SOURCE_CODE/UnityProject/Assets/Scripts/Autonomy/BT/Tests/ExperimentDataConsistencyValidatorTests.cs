using System;
using System.IO;
using System.Text;
using Autonomy.Domain;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public sealed class ExperimentDataConsistencyValidatorTests
    {
        private string _tempDirectory;
        private string _outputDirectory;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), $"experiment_data_validator_{Guid.NewGuid():N}");
            _outputDirectory = Path.Combine(_tempDirectory, "validation");
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public void Valid_Minimal_Run_Passes_And_Exports_Reports()
        {
            RunPaths paths = WriteValidRun();

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.Not.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RunId, Is.EqualTo("run_valid"));
            Assert.That(report.EventCount, Is.GreaterThan(0));
            Assert.That(report.SampleCount, Is.EqualTo(2));
            Assert.That(File.Exists(report.MarkdownPath), Is.True);
            Assert.That(File.Exists(report.JsonPath), Is.True);
            Assert.That(File.ReadAllText(report.MarkdownPath), Does.Contain("status: Pass"));
            Assert.That(File.ReadAllText(report.JsonPath), Does.Contain("\"status\":\"Pass\""));
        }

        [Test]
        public void Missing_Manifest_Events_And_Samples_Fails()
        {
            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(_tempDirectory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RedFlags, Does.Contain("manifest_missing"));
            Assert.That(report.RedFlags, Does.Contain("events_missing"));
            Assert.That(report.RedFlags, Does.Contain("samples_missing"));
        }

        [Test]
        public void Jsonl_Corrupt_Line_Warns_Without_Aborting()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, "not-json\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Warn));
            Assert.That(report.Warnings, Has.Some.Contains("json_corrupt"));
            Assert.That(report.EventCount, Is.GreaterThan(0));
        }

        [Test]
        public void Event_Missing_Trial_Context_When_Required_Warns()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(99, "experiment_trial_started_by_orchestrator", "\"session_id\":\"S001\"") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Warn));
            Assert.That(report.MissingFields, Has.Some.Contains("trial_id"));
        }

        [Test]
        public void C01_Autonomy_Request_Fails_Because_Robot_Is_Disabled()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(99, "autonomy_request_submitted", Context("C01_robot_off_voice_on", false, true, "Disabled")) + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RedFlags, Has.Some.Contains("autonomy_request_with_robot_disabled"));
        }

        [Test]
        public void C10_Executable_Voice_Not_Blocked_Fails()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(99, "voice_command_execution", Context("C10_robot_on_voice_off", true, false, "AssistedSelection") + ",\"status\":\"accepted\"") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RedFlags, Has.Some.Contains("voice_executable_not_blocked_when_voice_disabled"));
        }

        [Test]
        public void Timestamps_Out_Of_Order_Warn()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(1, "late_low_timestamp_event", "\"payload_value\":\"x\"") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Warn));
            Assert.That(report.OutOfOrderEvents, Has.Some.Contains("late_low_timestamp_event"));
        }

        [Test]
        public void Non_Canonical_Condition_Fails()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(99, "experiment_condition_applied", Context("C99_custom", true, true, "AssistedSelection")) + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RedFlags, Has.Some.Contains("condition_id_non_canonical"));
        }

        [Test]
        public void Flags_Incompatible_With_Condition_Fail()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(99, "experiment_condition_applied", Context("C10_robot_on_voice_off", false, false, "AssistedSelection")) + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.ConditionFlagInconsistencies, Has.Some.Contains("robot_enabled"));
        }

        [Test]
        public void Final_Conditions_Map_To_Canonical_Flags()
        {
            RunPaths paths = WriteFinalConditionRun();

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);
            string diagnostics = DescribeValidationResult(nameof(Final_Conditions_Map_To_Canonical_Flags), report, paths);
            TestContext.WriteLine(diagnostics);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Pass), diagnostics);
            Assert.That(report.ConditionsDetected, Does.Contain("C00_robot_off_voice_off"));
            Assert.That(report.ConditionsDetected, Does.Contain("C10_robot_on_voice_off"));
            Assert.That(report.ConditionsDetected, Does.Contain("C11_robot_on_voice_on"));
            Assert.That(report.ConditionFlagInconsistencies, Is.Empty, diagnostics);
        }

        [Test]
        public void Legacy_VoiceOnly_LogContext_With_C10_Is_Warning_Not_Failure()
        {
            RunPaths paths = WriteFinalConditionRun("voice_only");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Warn));
            Assert.That(report.RedFlags, Is.Empty);
            Assert.That(report.Warnings, Has.Some.Contains("log_context_condition_mismatch_warning"));
        }

        [Test]
        public void C00_Autonomous_Selection_Is_Red_Flag()
        {
            RunPaths paths = WriteFinalConditionRun();
            File.AppendAllText(paths.Events, Event(99, "autonomous_selection_candidate_selected", Context("C00_robot_off_voice_off", false, false, "Disabled") + ",\"policy\":\"LocalPickCost\",\"selection_metric\":\"robot_to_pickup_cost\",\"benchmark_pick_place_total_active\":false") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RedFlags, Has.Some.Contains("c00_robot_activity_not_allowed"));
        }

        [Test]
        public void C10_C11_Selection_Require_P41_Runtime_Metadata_Diagnostically()
        {
            RunPaths paths = WriteFinalConditionRun(includeP41Metadata: false);

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Warn));
            Assert.That(report.Warnings, Has.Some.Contains("p41_local_pick_cost_policy_not_observed:C10_robot_on_voice_off"));
            Assert.That(report.Warnings, Has.Some.Contains("p41_robot_to_pickup_metric_not_observed:C11_robot_on_voice_on"));
        }

        [Test]
        public void P41_PickPlace_Total_Cannot_Be_Runtime_Policy()
        {
            RunPaths paths = WriteFinalConditionRun();
            File.AppendAllText(paths.Events, Event(100, "autonomous_selection_policy_evaluated", Context("C10_robot_on_voice_off", true, false, "AssistedSelection") + ",\"policy\":\"PickPlaceTotalCost\",\"selection_metric\":\"total_cost\",\"benchmark_pick_place_total_active\":true") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RedFlags, Has.Some.Contains("p41_pick_place_benchmark"));
        }

        [Test]
        public void Missing_Metric_Instrumentation_Produces_Warn_Not_Fail()
        {
            RunPaths paths = WriteValidRun(includeMetricEvents: false);

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Warn));
            Assert.That(report.MetricSufficiencyWarnings, Has.Some.Contains("classification_counts_not_instrumented"));
        }

        [Test]
        public void Uninitialized_Before_Trial_Active_Warns_Without_Failing()
        {
            RunPaths paths = WriteValidRun(beforeSessionEvents: new[] { Event(0.5, "run_pending_metadata", "\"session_id\":\"pre\",\"condition_id\":\"uninitialized\"") });

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Warn));
            Assert.That(report.RedFlags, Is.Empty);
            Assert.That(report.ConditionsDetected, Does.Not.Contain("uninitialized"));
            Assert.That(report.Warnings, Does.Contain("pretrial_uninitialized_context"));
        }

        [Test]
        public void Uninitialized_During_Trial_Active_Fails()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(99, "voice_command_execution", Context("uninitialized", true, true, "AssistedSelection") + ",\"status\":\"accepted\"") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RedFlags, Has.Some.Contains("uninitialized_condition_in_active_experiment_context"));
        }

        [Test]
        public void Provisional_PreSession_SessionId_Different_From_Effective_Is_Warning()
        {
            RunPaths paths = WriteValidRun(beforeSessionEvents: new[] { Event(0.5, "run_pending_metadata", "\"session_id\":\"provisional\",\"condition_id\":\"uninitialized\"") });

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Warn));
            Assert.That(report.RedFlags, Is.Empty);
            Assert.That(report.Warnings, Has.Some.Contains("pre_session_provisional_session_id"));
        }

        [Test]
        public void AssistedRoundStateCleared_With_Assistance_Disabled_Is_Cleanup_Not_Failure()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(99, "assisted_round_state_cleared", Context("C01_robot_off_voice_on", false, true, "Disabled") + ",\"reason\":\"experiment_trial_reset\"") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Pass));
            Assert.That(report.RedFlags, Is.Empty);
        }

        [Test]
        public void AssistedSelection_Real_With_Assistance_Disabled_Fails()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(99, "assisted_selection_assigned", Context("C01_robot_off_voice_on", false, true, "Disabled")) + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RedFlags, Has.Some.Contains("assistance_effective_while_disabled"));
        }

        [Test]
        public void Repeated_Assistance_Blocks_In_Robot_Off_Warn_Not_Fail()
        {
            RunPaths paths = WriteValidRun();
            for (int i = 0; i < 4; i++)
            {
                File.AppendAllText(paths.Events, Event(99 + i, "robot_assistance_blocked_by_condition", Context("C01_robot_off_voice_on", false, true, "Disabled") + ",\"reason\":\"robot_disabled_by_condition\"") + "\n");
            }

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Warn));
            Assert.That(report.RedFlags, Is.Empty);
            Assert.That(report.Warnings, Has.Some.Contains("high_frequency_condition_block_events"));
        }

        [Test]
        public void Voice_Command_In_Voice_Off_When_Blocked_Does_Not_Fail()
        {
            RunPaths paths = WriteValidRun();
            string c10 = Context("C10_robot_on_voice_off", true, false, "AssistedSelection");
            File.AppendAllText(paths.Events, Event(99, "voice_transcription_received", c10) + "\n");
            File.AppendAllText(paths.Events, Event(100, "voice_command_normalized", c10) + "\n");
            File.AppendAllText(paths.Events, Event(101, "voice_intent_mapped", c10) + "\n");
            File.AppendAllText(paths.Events, Event(102, "voice_command_blocked_by_condition", c10 + ",\"reason\":\"voice_disabled_by_condition\",\"status\":\"blocked\"") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Pass));
            Assert.That(report.RedFlags, Is.Empty);
        }

        [Test]
        public void Voice_Off_With_BridgeInvoked_Or_AutonomyRequest_Fails()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(99, "voice_route_result", Context("C10_robot_on_voice_off", true, false, "AssistedSelection") + ",\"bridge_invoked\":true,\"status\":\"accepted\"") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RedFlags, Has.Some.Contains("voice_bridge_invoked_when_voice_disabled"));
        }

        [Test]
        public void C10_Assisted_Navmesh_Autonomy_Request_With_Voice_Off_Does_Not_Fail_As_Voice()
        {
            RunPaths paths = WriteValidRun();
            string c10 = Context("C10_robot_on_voice_off", true, false, "AssistedSelection");
            File.AppendAllText(paths.Events, Event(99, "autonomy_request_submitted", c10 + ",\"intent_source\":\"assisted_navmesh_selection\"") + "\n");
            File.AppendAllText(paths.Events, Event(100, "assisted_task_submitted", c10 + ",\"intent_source\":\"assisted_navmesh_selection\",\"bridge_invoked\":true") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.RedFlags, Has.None.Contains("autonomy_request_with_voice_disabled"));
            Assert.That(report.RedFlags, Has.None.Contains("voice_bridge_invoked_when_voice_disabled"));
        }

        [Test]
        public void Voice_Off_With_Vocal_Autonomy_Request_Fails()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(99, "autonomy_request_submitted", Context("C10_robot_on_voice_off", true, false, "AssistedSelection") + ",\"intent_source\":\"manual_transcript\"") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RedFlags, Has.Some.Contains("autonomy_request_with_voice_disabled"));
        }

        [Test]
        public void Autonomy_Request_After_Trial_Completed_Fails_As_Post_Trial_Activity()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(99, "experiment_trial_completed_by_orchestrator", Context("C11_robot_on_voice_on", true, true, "AssistedSelection")) + "\n");
            File.AppendAllText(paths.Events, Event(100, "autonomy_request_submitted", Context("C11_robot_on_voice_on", true, true, "AssistedSelection")) + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RedFlags, Has.Some.Contains("autonomy_request_after_trial_completed"));
        }

        [Test]
        public void Assisted_Selection_After_Trial_Aborted_Fails_As_Post_Trial_Activity()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(99, "experiment_trial_aborted", Context("C11_robot_on_voice_on", true, true, "AssistedSelection")) + "\n");
            File.AppendAllText(paths.Events, Event(100, "assisted_box_selected", Context("C11_robot_on_voice_on", true, true, "AssistedSelection")) + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RedFlags, Has.Some.Contains("post_trial_operational_activity"));
        }

        [Test]
        public void Condition_Applied_But_Sanity_Failed_Is_Not_Started_Coverage()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(99, "experiment_condition_applied", Context("C00_robot_off_voice_off", false, false, "Disabled")) + "\n");
            File.AppendAllText(paths.Events, Event(100, "experiment_trial_sanity_check_failed", Context("C00_robot_off_voice_off", false, false, "Disabled")) + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.ConditionsDetected, Does.Contain("C00_robot_off_voice_off"));
            Assert.That(report.TrialsFailedSanity, Does.Contain("T001"));
            Assert.That(report.ConditionsStarted, Does.Not.Contain("C00_robot_off_voice_off"));
        }

        [Test]
        public void Manifest_Session_Summary_Partial_Coverage_Is_Diagnosed()
        {
            RunPaths paths = WriteValidRun();
            File.WriteAllText(
                paths.Manifest,
                "{\"run_id\":\"run_valid\",\"scene\":\"autonomous_demo_step22_multimodal_bridge\",\"timestamp\":\"2026-06-03T10:00:00Z\",\"events_file\":\"run_valid_events.jsonl\",\"samples_file\":\"run_valid_samples.csv\",\"manifest_file\":\"run_valid_manifest.json\",\"session_trials_csv_file\":\"partial_session_trials.csv\"}\n");
            File.WriteAllText(Path.Combine(paths.Directory, "partial_session_trials.csv"), "trial_id,condition_id\nT999,C10_robot_on_voice_off\n");
            File.AppendAllText(paths.Events, Event(99, "experiment_trial_started_by_orchestrator", ContextForTrial("T002", "R002", "C10_robot_on_voice_off", true, false, "AssistedSelection")) + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RedFlags, Has.Some.Contains("session_summary_missing_started_trials"));
        }

        [Test]
        public void Multiple_Rounds_Per_Condition_With_Coherent_Round_Metadata_Passes()
        {
            RunPaths paths = WriteMultiRoundRun();

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Pass));
            Assert.That(report.IdInconsistencies, Is.Empty);
            Assert.That(report.RedFlags, Is.Empty);
            Assert.That(report.TrialsStarted, Is.EquivalentTo(new[] { "trial_001", "trial_002", "trial_003", "trial_004" }));
            Assert.That(report.ConditionsStarted, Is.EquivalentTo(new[] { "C00_robot_off_voice_off", "C01_robot_off_voice_on" }));
        }

        [Test]
        public void Robot_Pose_Reset_Uses_Post_Sample_And_Ignores_Pre_Reset_Sample()
        {
            RunPaths paths = WriteValidRun();
            WritePoseSamples(
                paths.Samples,
                "9.99,8.0,9.0,180.0",
                "10.20,1.0,2.0,90.0");
            File.AppendAllText(paths.Events, Event(10.0, "robot_pose_reset_completed", Context("C11_robot_on_voice_on", true, true, "AssistedSelection") + ",\"robot_position_target\":{\"x\":1.0,\"y\":0.0,\"z\":2.0},\"robot_yaw_target_deg\":90.0") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.RedFlags, Has.None.Contains("robot_pose_reset_mismatch"));
            Assert.That(report.Warnings, Has.None.Contains("robot_pose_reset_no_post_sample_available"));
        }

        [Test]
        public void Robot_Pose_Reset_Mismatch_With_Post_Sample_Fails()
        {
            RunPaths paths = WriteValidRun();
            WritePoseSamples(
                paths.Samples,
                "9.99,1.0,2.0,90.0",
                "10.20,8.0,9.0,180.0");
            File.AppendAllText(paths.Events, Event(10.0, "robot_pose_reset_completed", Context("C11_robot_on_voice_on", true, true, "AssistedSelection") + ",\"robot_position_target\":{\"x\":1.0,\"y\":0.0,\"z\":2.0},\"robot_yaw_target_deg\":90.0") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RedFlags, Has.Some.Contains("robot_pose_reset_mismatch"));
            Assert.That(report.RedFlags, Has.Some.Contains("sample_timing=post"));
        }

        [Test]
        public void Robot_Pose_Reset_Without_Post_Sample_Warns_Without_Mismatch()
        {
            RunPaths paths = WriteValidRun();
            WritePoseSamples(paths.Samples, "9.99,8.0,9.0,180.0");
            File.AppendAllText(paths.Events, Event(10.0, "robot_pose_reset_completed", Context("C11_robot_on_voice_on", true, true, "AssistedSelection") + ",\"robot_position_target\":{\"x\":1.0,\"y\":0.0,\"z\":2.0},\"robot_yaw_target_deg\":90.0") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.RedFlags, Has.None.Contains("robot_pose_reset_mismatch"));
            Assert.That(report.Warnings, Has.Some.Contains("robot_pose_reset_no_post_sample_available"));
        }

        [Test]
        public void Robot_Pose_Reset_Rebound_Confirmed_By_Post_Sample_Fails()
        {
            RunPaths paths = WriteValidRun();
            WritePoseSamples(
                paths.Samples,
                "9.99,1.0,2.0,90.0",
                "10.20,8.0,9.0,180.0");
            File.AppendAllText(paths.Events, Event(10.0, "robot_pose_reset_completed", Context("C11_robot_on_voice_on", true, true, "AssistedSelection") + ",\"robot_position_target\":{\"x\":1.0,\"y\":0.0,\"z\":2.0},\"robot_yaw_target_deg\":90.0") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RedFlags, Has.Some.Contains("robot_pose_reset_mismatch"));
        }

        [Test]
        public void Operational_Activity_After_Robot_Pose_Reset_Failed_Fails()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(20, "robot_pose_reset_failed", ContextForTrial("T004", "round004", "C11_robot_on_voice_on", true, true, "AssistedSelection")) + "\n");
            File.AppendAllText(paths.Events, Event(21, "navigation_to_pick_started", ContextForTrial("T004", "round004", "C11_robot_on_voice_on", true, true, "AssistedSelection") + ",\"pickup_target_id\":\"round004_box00_A\"") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RedFlags, Has.Some.Contains("operational_activity_after_robot_pose_reset_failed"));
        }

        [Test]
        public void Robot_Pose_Reset_Failed_Invalid_Before_Start_Is_Not_Aborted()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(20, "robot_pose_reset_failed", ContextForTrial("T004", "round004", "C11_robot_on_voice_on", true, true, "AssistedSelection")) + "\n");
            File.AppendAllText(paths.Events, Event(21, "experiment_prepared_trial_invalid_summary_written", ContextForTrial("T004", "round004", "C11_robot_on_voice_on", true, true, "AssistedSelection") + ",\"terminal_state\":\"invalid_before_start\",\"failure_reason\":\"robot_pose_reset_failed\",\"aborted\":false") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.TrialsInvalidBeforeStart, Does.Contain("T004"));
            Assert.That(report.TrialsFailedReset, Does.Contain("T004"));
            Assert.That(report.TrialsAborted, Does.Not.Contain("T004"));
        }

        [Test]
        public void Stale_Target_From_Previous_Round_Fails()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(20, "navigation_to_pick_started", ContextForTrial("T004", "round004", "C11_robot_on_voice_on", true, true, "AssistedSelection") + ",\"pickup_target_id\":\"round003_box00_A\"") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Fail));
            Assert.That(report.RedFlags, Has.Some.Contains("stale_target_cross_trial"));
        }

        [Test]
        public void Terminal_Timestamp_Reset_Warns_Without_Failing()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(0, "final_sample_skipped", "\"reason\":\"invalid_unity_timestamp\"") + "\n");
            File.AppendAllText(paths.Events, Event(0, "run_finished", "\"reason\":\"disabled\"") + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Warn));
            Assert.That(report.RedFlags, Is.Empty);
            Assert.That(report.Warnings, Has.Some.Contains("terminal_timestamp_reset"));
        }

        [Test]
        public void Real_Timestamp_Inversion_During_Trial_Is_Reported()
        {
            RunPaths paths = WriteValidRun();
            File.AppendAllText(paths.Events, Event(2, "experiment_trial_started_by_orchestrator", Context("C11_robot_on_voice_on", true, true, "AssistedSelection")) + "\n");

            ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(paths.Directory, _outputDirectory);

            Assert.That(report.Status, Is.EqualTo(ExperimentDataValidationStatus.Warn));
            Assert.That(report.OutOfOrderEvents, Has.Some.Contains("experiment_trial_started_by_orchestrator"));
        }

        private RunPaths WriteValidRun(bool includeMetricEvents = true, string[] beforeSessionEvents = null)
        {
            string runDirectory = Path.Combine(_tempDirectory, "run_valid");
            Directory.CreateDirectory(runDirectory);
            string manifest = Path.Combine(runDirectory, "run_valid_manifest.json");
            string events = Path.Combine(runDirectory, "run_valid_events.jsonl");
            string samples = Path.Combine(runDirectory, "run_valid_samples.csv");
            File.WriteAllText(
                manifest,
                "{\"run_id\":\"run_valid\",\"scene\":\"autonomous_demo_step22_multimodal_bridge\",\"timestamp\":\"2026-06-03T10:00:00Z\",\"events_file\":\"run_valid_events.jsonl\",\"samples_file\":\"run_valid_samples.csv\",\"manifest_file\":\"run_valid_manifest.json\"}\n");
            File.WriteAllText(
                samples,
                "timestamp_unity,timestamp_wall,run_id,run_elapsed_time,control_mode,session_id,trial_id,condition_id,condition_name,robot_enabled,voice_enabled,assistance_mode,robot_x,robot_z,robot_yaw_deg\n" +
                "1,2026-06-03T10:00:00Z,run_valid,0.1,Autonomous,S001,T001,C11_robot_on_voice_on,Condition,true,true,AssistedSelection,1.0,2.0,90.0\n" +
                "2,2026-06-03T10:00:01Z,run_valid,0.2,Autonomous,S001,T001,C11_robot_on_voice_on,Condition,true,true,AssistedSelection,1.0,2.0,90.0\n");

            string context = Context("C11_robot_on_voice_on", true, true, "AssistedSelection");
            using (var writer = new StreamWriter(events))
            {
                if (beforeSessionEvents != null)
                {
                    foreach (string beforeSessionEvent in beforeSessionEvents)
                    {
                        writer.WriteLine(beforeSessionEvent);
                    }
                }

                writer.WriteLine(Event(1, "experiment_session_started", "\"session_id\":\"S001\""));
                writer.WriteLine(Event(2, "experiment_trial_prepare_started", context));
                writer.WriteLine(Event(3, "experiment_condition_applied", context));
                writer.WriteLine(Event(4, "experiment_condition_orchestrator_applied", context));
                writer.WriteLine(Event(5, "experiment_trial_reset_started", context));
                writer.WriteLine(Event(6, "experiment_trial_sanity_check_passed", context));
                writer.WriteLine(Event(7, "experiment_trial_round_spawn_requested", context));
                writer.WriteLine(Event(8, "spawn_round_started", context));
                writer.WriteLine(Event(9, "round_started", context));
                writer.WriteLine(Event(10, "experiment_trial_started", context));
                writer.WriteLine(Event(11, "experiment_trial_started_by_orchestrator", context));
                writer.WriteLine(Event(12, "autonomy_request_submitted", context));
                if (includeMetricEvents)
                {
                    writer.WriteLine(Event(13, "round_register_correct_deposit_attempt", context));
                    writer.WriteLine(Event(14, "box_incident_logged", context));
                    writer.WriteLine(Event(15, "pick_manipulation_completed", context));
                    writer.WriteLine(Event(16, "voice_command_execution", context + ",\"status\":\"accepted\""));
                    writer.WriteLine(Event(17, "assisted_pick_started", context));
                    writer.WriteLine(Event(18, "autonomous_selection_policy_evaluated", context + ",\"policy\":\"LocalPickCost\",\"selection_metric\":\"robot_to_pickup_cost\",\"robot_to_pickup_cost\":1.2,\"benchmark_pick_place_total_cost\":3.4,\"benchmark_pick_place_total_active\":false"));
                    writer.WriteLine(Event(19, "autonomous_selection_candidate_selected", context + ",\"policy\":\"LocalPickCost\",\"selection_metric\":\"robot_to_pickup_cost\",\"robot_to_pickup_cost\":1.2,\"benchmark_pick_place_total_cost\":3.4,\"benchmark_pick_place_total_active\":false"));
                    writer.WriteLine(Event(20, "condition_gate_blocked", Context("C01_robot_off_voice_on", false, true, "Disabled") + ",\"reason\":\"robot_disabled_by_condition\""));
                }
            }

            return new RunPaths(runDirectory, manifest, events, samples);
        }

        private RunPaths WriteFinalConditionRun(string logContext = "C10_robot_on_voice_off", bool includeP41Metadata = true)
        {
            string runDirectory = Path.Combine(_tempDirectory, $"run_valid_final_conditions_{Guid.NewGuid():N}");
            Directory.CreateDirectory(runDirectory);
            string manifest = Path.Combine(runDirectory, "run_valid_manifest.json");
            string events = Path.Combine(runDirectory, "run_valid_events.jsonl");
            string samples = Path.Combine(runDirectory, "run_valid_samples.csv");
            File.WriteAllText(
                manifest,
                "{\"run_id\":\"run_valid\",\"scene\":\"autonomous_demo_step22_multimodal_bridge\",\"timestamp\":\"2026-06-03T10:00:00Z\",\"events_file\":\"run_valid_events.jsonl\",\"samples_file\":\"run_valid_samples.csv\",\"manifest_file\":\"run_valid_manifest.json\",\"log_context\":\"" + logContext + "\",\"condition_log_context\":\"" + (logContext.StartsWith("C", StringComparison.Ordinal) ? logContext : string.Empty) + "\"}\n");
            File.WriteAllText(
                samples,
                "timestamp_unity,timestamp_wall,run_id,run_elapsed_time,control_mode,session_id,trial_id,condition_id,condition_name,robot_enabled,voice_enabled,assistance_mode,robot_x,robot_z,robot_yaw_deg\n" +
                "1,2026-06-03T10:00:00Z,run_valid,0.1,Autonomous,S001,trial_001,C00_robot_off_voice_off,Robot OFF + Voice OFF,false,false,Disabled,1.0,2.0,90.0\n" +
                "2,2026-06-03T10:00:01Z,run_valid,0.2,Autonomous,S001,trial_002,C10_robot_on_voice_off,Robot ON + Voice OFF,true,false,AssistedSelection,1.0,2.0,90.0\n" +
                "3,2026-06-03T10:00:02Z,run_valid,0.3,Autonomous,S001,trial_003,C11_robot_on_voice_on,Robot ON + Voice ON,true,true,AssistedSelection,1.0,2.0,90.0\n");

            using (var writer = new StreamWriter(events))
            {
                writer.WriteLine(Event(1, "experiment_session_started", "\"session_id\":\"S001\""));
                WriteStartedTrial(writer, 2, "trial_001", "R001", "C00_robot_off_voice_off", false, false, "Disabled", 1, 1, 1);
                WriteStartedTrial(writer, 20, "trial_002", "R002", "C10_robot_on_voice_off", true, false, "AssistedSelection", 1, 1, 2, includeP41Selection: true, includeP41Metadata: includeP41Metadata);
                WriteStartedTrial(writer, 40, "trial_003", "R003", "C11_robot_on_voice_on", true, true, "AssistedSelection", 1, 1, 3, includeP41Selection: true, includeP41Metadata: includeP41Metadata, includeVoiceUsageEvidence: true);
            }

            return new RunPaths(runDirectory, manifest, events, samples);
        }

        private static void WriteP41Selection(StreamWriter writer, double startTime, string trialId, string roundId, string conditionId, bool includeMetadata)
        {
            string context = ContextForTrial(trialId, roundId, conditionId, true, conditionId == "C11_robot_on_voice_on", "AssistedSelection", 1, 1, conditionId == "C10_robot_on_voice_off" ? 2 : 3);
            string p41 = includeMetadata
                ? ",\"policy\":\"LocalPickCost\",\"selection_metric\":\"robot_to_pickup_cost\",\"robot_to_pickup_cost\":1.2,\"benchmark_pick_place_total_cost\":3.4,\"benchmark_pick_place_total_active\":false"
                : string.Empty;
            writer.WriteLine(Event(startTime, "autonomous_selection_policy_evaluated", context + p41));
            writer.WriteLine(Event(startTime + 1, "autonomous_selection_candidate_selected", context + p41));
        }

        private RunPaths WriteMultiRoundRun()
        {
            string runDirectory = Path.Combine(_tempDirectory, "run_valid_multi_round");
            Directory.CreateDirectory(runDirectory);
            string manifest = Path.Combine(runDirectory, "run_valid_manifest.json");
            string events = Path.Combine(runDirectory, "run_valid_events.jsonl");
            string samples = Path.Combine(runDirectory, "run_valid_samples.csv");
            File.WriteAllText(
                manifest,
                "{\"run_id\":\"run_valid\",\"scene\":\"autonomous_demo_step22_multimodal_bridge\",\"timestamp\":\"2026-06-03T10:00:00Z\",\"events_file\":\"run_valid_events.jsonl\",\"samples_file\":\"run_valid_samples.csv\",\"manifest_file\":\"run_valid_manifest.json\"}\n");
            File.WriteAllText(
                samples,
                "timestamp_unity,timestamp_wall,run_id,run_elapsed_time,control_mode,session_id,trial_id,condition_id,condition_name,robot_enabled,voice_enabled,assistance_mode,robot_x,robot_z,robot_yaw_deg\n" +
                "1,2026-06-03T10:00:00Z,run_valid,0.1,Autonomous,S001,trial_001,C00_robot_off_voice_off,Condition,false,false,Disabled,1.0,2.0,90.0\n" +
                "2,2026-06-03T10:00:01Z,run_valid,0.2,Autonomous,S001,trial_004,C01_robot_off_voice_on,Condition,false,true,Disabled,1.0,2.0,90.0\n");

            using (var writer = new StreamWriter(events))
            {
                writer.WriteLine(Event(1, "experiment_session_started", "\"session_id\":\"S001\""));
                WriteStartedTrial(writer, 2, "trial_001", "R001", "C00_robot_off_voice_off", false, false, "Disabled", 1, 3, 1);
                WriteStartedTrial(writer, 20, "trial_002", "R002", "C00_robot_off_voice_off", false, false, "Disabled", 2, 3, 2);
                WriteStartedTrial(writer, 40, "trial_003", "R003", "C00_robot_off_voice_off", false, false, "Disabled", 3, 3, 3);
                WriteStartedTrial(writer, 60, "trial_004", "R004", "C01_robot_off_voice_on", false, true, "Disabled", 1, 3, 4);
            }

            return new RunPaths(runDirectory, manifest, events, samples);
        }

        private static void WriteStartedTrial(
            StreamWriter writer,
            double startTime,
            string trialId,
            string roundId,
            string conditionId,
            bool robotEnabled,
            bool voiceEnabled,
            string assistanceMode,
            int roundWithinCondition,
            int roundsPerCondition,
            int globalRoundIndex,
            bool includeP41Selection = false,
            bool includeP41Metadata = true,
            bool includeVoiceUsageEvidence = false)
        {
            string context = ContextForTrial(
                trialId,
                roundId,
                conditionId,
                robotEnabled,
                voiceEnabled,
                assistanceMode,
                roundWithinCondition,
                roundsPerCondition,
                globalRoundIndex);
            writer.WriteLine(Event(startTime, "experiment_trial_prepare_started", context));
            writer.WriteLine(Event(startTime + 1, "experiment_condition_applied", context));
            writer.WriteLine(Event(startTime + 2, "experiment_condition_orchestrator_applied", context));
            writer.WriteLine(Event(startTime + 3, "experiment_trial_reset_started", context));
            writer.WriteLine(Event(startTime + 4, "experiment_trial_sanity_check_passed", context));
            writer.WriteLine(Event(startTime + 5, "experiment_trial_round_spawn_requested", context));
            writer.WriteLine(Event(startTime + 6, "spawn_round_started", context));
            writer.WriteLine(Event(startTime + 7, "round_started", context));
            writer.WriteLine(Event(startTime + 8, "experiment_trial_started", context));
            writer.WriteLine(Event(startTime + 9, "experiment_trial_started_by_orchestrator", context));
            double cursor = startTime + 10;
            if (includeVoiceUsageEvidence && voiceEnabled)
            {
                writer.WriteLine(Event(cursor, "voice_command_execution", context + ",\"status\":\"accepted\""));
                cursor += 1;
            }

            if (includeP41Selection && robotEnabled)
            {
                WriteP41Selection(writer, cursor, trialId, roundId, conditionId, includeP41Metadata);
                cursor += 2;
            }

            writer.WriteLine(Event(cursor, "round_register_correct_deposit_attempt", context));
            cursor += 1;
            writer.WriteLine(Event(cursor, "box_incident_logged", context));
            cursor += 1;
            writer.WriteLine(Event(cursor, "pick_manipulation_completed", context));
            cursor += 1;
            if (voiceEnabled && !robotEnabled)
            {
                writer.WriteLine(Event(cursor, "voice_command_blocked_by_condition", context + ",\"reason\":\"robot_disabled_by_condition\",\"status\":\"blocked\""));
                cursor += 1;
            }

            if (!robotEnabled)
            {
                writer.WriteLine(Event(cursor, "condition_gate_blocked", context + ",\"reason\":\"robot_disabled_by_condition\""));
            }
        }

        private static string Event(double timestamp, string eventType, string payload)
        {
            return $"{{\"timestamp_unity\":{timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"run_elapsed_time\":{timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"run_id\":\"run_valid\",\"event_type\":\"{eventType}\",\"payload\":{{{payload}}}}}";
        }

        private static string Context(string conditionId, bool robotEnabled, bool voiceEnabled, string assistanceMode)
        {
            return ContextForTrial("T001", "R001", conditionId, robotEnabled, voiceEnabled, assistanceMode);
        }

        private static string ContextForTrial(string trialId, string roundId, string conditionId, bool robotEnabled, bool voiceEnabled, string assistanceMode)
        {
            return ContextForTrial(trialId, roundId, conditionId, robotEnabled, voiceEnabled, assistanceMode, 1, 1, 1);
        }

        private static string ContextForTrial(
            string trialId,
            string roundId,
            string conditionId,
            bool robotEnabled,
            bool voiceEnabled,
            string assistanceMode,
            int roundIndexWithinCondition,
            int roundsPerCondition,
            int globalRoundIndex)
        {
            return "\"session_id\":\"S001\",\"trial_id\":\"" + trialId + "\",\"trial_index\":1,\"condition_id\":\"" + conditionId +
                "\",\"condition_name\":\"Condition\",\"robot_enabled\":" + robotEnabled.ToString().ToLowerInvariant() +
                ",\"voice_enabled\":" + voiceEnabled.ToString().ToLowerInvariant() +
                ",\"assistance_mode\":\"" + assistanceMode + "\",\"spawn_generation_mode\":\"RandomBalanced\",\"round_id\":\"" + roundId +
                "\",\"round_index\":" + globalRoundIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"round_index_within_condition\":" + roundIndexWithinCondition.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"rounds_per_condition\":" + roundsPerCondition.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"global_round_index\":" + globalRoundIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void WritePoseSamples(string samplesPath, params string[] rows)
        {
            using var writer = new StreamWriter(samplesPath, append: false);
            writer.WriteLine("timestamp_unity,timestamp_wall,run_id,run_elapsed_time,control_mode,session_id,trial_id,condition_id,condition_name,robot_enabled,voice_enabled,assistance_mode,robot_x,robot_z,robot_yaw_deg");
            int index = 0;
            foreach (string row in rows)
            {
                string[] parts = row.Split(',');
                writer.WriteLine($"{index + 1},2026-06-03T10:00:{index:00}Z,run_valid,{parts[0]},Autonomous,S001,T001,C11_robot_on_voice_on,Condition,true,true,AssistedSelection,{parts[1]},{parts[2]},{parts[3]}");
                index++;
            }
        }

        private static string DescribeValidationResult(string testName, ExperimentDataValidationReport report, RunPaths paths)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"{testName} validation result");
            builder.AppendLine($"status={report.Status}");
            builder.AppendLine($"run_id={report.RunId}");
            builder.AppendLine($"scene={report.Scene}");
            builder.AppendLine($"run_directory={paths.Directory}");
            builder.AppendLine($"manifest={paths.Manifest}");
            builder.AppendLine($"events={paths.Events}");
            builder.AppendLine($"samples={paths.Samples}");
            builder.AppendLine($"event_count={report.EventCount}");
            builder.AppendLine($"sample_count={report.SampleCount}");
            builder.AppendLine($"session_detected={report.SessionDetected}");
            AppendList(builder, "conditions_detected", "Info", "ValidateCondition", report.ConditionsDetected);
            AppendList(builder, "conditions_started", "Info", "ValidateEventSemantics", report.ConditionsStarted);
            AppendList(builder, "trials_detected", "Info", "TrackIds", report.TrialsDetected);
            AppendList(builder, "trials_started", "Info", "ValidateEventSemantics", report.TrialsStarted);
            AppendList(builder, "metric_sufficiency_passes", "Info", "ValidateMetricSufficiency/AddMetric", report.MetricSufficiencyPasses);
            AppendList(builder, "warnings", "Warn", "Validator warning", report.Warnings);
            AppendList(builder, "metric_sufficiency_warnings", "Warn", "ValidateMetricSufficiency/AddMetric", report.MetricSufficiencyWarnings);
            AppendList(builder, "missing_fields", "Warn", "ValidateContextFields", report.MissingFields);
            AppendList(builder, "out_of_order_events", "Warn", "ValidateEvents", report.OutOfOrderEvents);
            AppendList(builder, "red_flags", "Fail", "Validator red flag", report.RedFlags);
            AppendList(builder, "id_inconsistencies", "Fail", "TrackIds", report.IdInconsistencies);
            AppendList(builder, "condition_flag_inconsistencies", "Fail", "ValidateCondition/ValidateFinalConditionFlags", report.ConditionFlagInconsistencies);
            builder.AppendLine($"markdown_report={report.MarkdownPath}");
            builder.AppendLine($"json_report={report.JsonPath}");
            return builder.ToString();
        }

        private static void AppendList(StringBuilder builder, string name, string severity, string rule, System.Collections.Generic.IReadOnlyCollection<string> values)
        {
            builder.AppendLine($"{name}.count={values.Count}");
            foreach (string value in values)
            {
                builder.AppendLine($"  severity={severity}; rule={rule}; id={value}");
            }
        }

        private readonly struct RunPaths
        {
            public RunPaths(string directory, string manifest, string events, string samples)
            {
                Directory = directory;
                Manifest = manifest;
                Events = events;
                Samples = samples;
            }

            public string Directory { get; }
            public string Manifest { get; }
            public string Events { get; }
            public string Samples { get; }
        }
    }
}
