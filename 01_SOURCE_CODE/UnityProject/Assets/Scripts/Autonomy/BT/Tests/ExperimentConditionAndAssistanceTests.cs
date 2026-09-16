using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using UnityEngine;

namespace Autonomy.BT.Tests
{
    public sealed class ExperimentConditionAndAssistanceTests
    {
        [TestCase(false, false, false, false, "voice_disabled_by_condition")]
        [TestCase(false, true, false, false, "robot_disabled_by_condition")]
        [TestCase(true, false, false, true, "voice_disabled_by_condition")]
        [TestCase(true, true, true, true, "condition_allows_voice_robot_execution")]
        public void ConditionMatrix_Controls_Voice_And_Robot_Assistance(
            bool robotEnabled,
            bool voiceEnabled,
            bool expectedVoiceAllowed,
            bool expectedAssistanceAllowed,
            string expectedVoiceReason)
        {
            var condition = new ExperimentConditionConfig(robotEnabled, voiceEnabled);

            VoiceCommandExecutionGateResult voice = condition.EvaluateVoiceCommand(
                MultimodalTaskIntent.PickAndPlaceByCategory("A", "ZoneA", "test"));
            AssistanceGateResult assistance = condition.EvaluateRobotAssistance(
                robotBusy: false,
                explicitIntentProcessing: false,
                hasAssignedBox: false);

            Assert.That(voice.Allowed, Is.EqualTo(expectedVoiceAllowed));
            Assert.That(voice.Reason, Is.EqualTo(expectedVoiceReason));
            Assert.That(assistance.Allowed, Is.EqualTo(expectedAssistanceAllowed));
        }

        [Test]
        public void RobotDisabled_Blocks_Assistance_Even_When_Voice_Enabled()
        {
            var condition = new ExperimentConditionConfig(robotEnabled: false, voiceEnabled: true);

            AssistanceGateResult assistance = condition.EvaluateRobotAssistance(
                robotBusy: false,
                explicitIntentProcessing: false,
                hasAssignedBox: false);

            Assert.That(assistance.Allowed, Is.False);
            Assert.That(assistance.Reason, Is.EqualTo("robot_disabled_by_condition"));
        }

        [Test]
        public void StartupAlignment_Exits_When_Hard_Threshold_Is_Stable()
        {
            TiagoNavMeshNavigationService.StartupAlignmentExitDecision decision =
                TiagoNavMeshNavigationService.EvaluateStartupAlignmentExit(
                    angleErrorDeg: 10.5f,
                    exitDeg: 12f,
                    hardExitStableSeconds: 0.30f,
                    hardExitRequiredSeconds: 0.25f,
                    softToleranceDeg: 5f,
                    noProgressSeconds: 0f,
                    softExitRequiredNoProgressSeconds: 1.0f,
                    hasActivePath: true,
                    emergencyBlocked: false);

            Assert.That(decision, Is.EqualTo(TiagoNavMeshNavigationService.StartupAlignmentExitDecision.HardExit));
        }

        [Test]
        public void StartupAlignment_SoftExits_When_Near_Threshold_And_NoProgress()
        {
            TiagoNavMeshNavigationService.StartupAlignmentExitDecision decision =
                TiagoNavMeshNavigationService.EvaluateStartupAlignmentExit(
                    angleErrorDeg: 14.1f,
                    exitDeg: 12f,
                    hardExitStableSeconds: 0f,
                    hardExitRequiredSeconds: 0.25f,
                    softToleranceDeg: 5f,
                    noProgressSeconds: 1.10f,
                    softExitRequiredNoProgressSeconds: 1.0f,
                    hasActivePath: true,
                    emergencyBlocked: false);

            Assert.That(decision, Is.EqualTo(TiagoNavMeshNavigationService.StartupAlignmentExitDecision.SoftExitAfterNoProgress));
        }

        [TestCase(25f, true, false)]
        [TestCase(14.1f, true, true)]
        [TestCase(14.1f, false, false)]
        public void StartupAlignment_Does_Not_SoftExit_When_Angle_Path_Or_Safety_Gates_Block(
            float angleErrorDeg,
            bool hasActivePath,
            bool emergencyBlocked)
        {
            TiagoNavMeshNavigationService.StartupAlignmentExitDecision decision =
                TiagoNavMeshNavigationService.EvaluateStartupAlignmentExit(
                    angleErrorDeg,
                    exitDeg: 12f,
                    hardExitStableSeconds: 0f,
                    hardExitRequiredSeconds: 0.25f,
                    softToleranceDeg: 5f,
                    noProgressSeconds: 1.10f,
                    softExitRequiredNoProgressSeconds: 1.0f,
                    hasActivePath: hasActivePath,
                    emergencyBlocked: emergencyBlocked);

            Assert.That(decision, Is.EqualTo(TiagoNavMeshNavigationService.StartupAlignmentExitDecision.Stay));
        }

        [Test]
        public void LoggerManifest_Enriches_Legacy_VoiceOnly_Context_With_Final_C10_Metadata()
        {
            var manifest = new Dictionary<string, object>
            {
                ["log_context"] = "voice_only",
                ["condition_log_context"] = string.Empty,
                ["legacy_log_context"] = string.Empty,
                ["log_context_condition_mismatch_warning"] = false
            };
            var conditionPayload = new Dictionary<string, object>
            {
                ["condition_id"] = "C10_robot_on_voice_off",
                ["condition_name"] = "Robot ON + Voice OFF",
                ["robot_enabled"] = true,
                ["voice_enabled"] = false,
                ["assistance_mode"] = "AssistedSelection"
            };

            bool changed = TiagoExperimentLogger.TryApplyConditionManifestMetadataForDiagnostics(
                manifest,
                conditionPayload,
                "experiment_condition_orchestrator_applied");

            Assert.That(changed, Is.True);
            Assert.That(manifest["condition_id"], Is.EqualTo("C10_robot_on_voice_off"));
            Assert.That(manifest["condition_name"], Is.EqualTo("Robot ON + Voice OFF"));
            Assert.That(manifest["robot_enabled"], Is.EqualTo(true));
            Assert.That(manifest["voice_enabled"], Is.EqualTo(false));
            Assert.That(manifest["assistance_mode"], Is.EqualTo("AssistedSelection"));
            Assert.That(manifest["condition_log_context"], Is.EqualTo("C10_robot_on_voice_off"));
            Assert.That(manifest["legacy_log_context"], Is.EqualTo("voice_only"));
            Assert.That(manifest["log_context_condition_mismatch_warning"], Is.EqualTo(true));
            Assert.That(manifest["condition_metadata_source_event"], Is.EqualTo("experiment_condition_orchestrator_applied"));
            Assert.That(manifest.ContainsKey("condition_metadata_updated_utc"), Is.True);

            bool secondPassChanged = TiagoExperimentLogger.TryApplyConditionManifestMetadataForDiagnostics(
                manifest,
                conditionPayload,
                "experiment_condition_orchestrator_applied");

            Assert.That(secondPassChanged, Is.False);
        }

        [Test]
        public void Default_Final_Presets_Are_Exportable_And_Gate_Conditions()
        {
            List<Experiment2x2ConditionDefinition> presets =
                Experiment2x2ConditionDefinition.CreateDefaultPresets(SpawnGenerationMode.DeterministicDebug);

            Assert.That(presets, Has.Count.EqualTo(3));
            Assert.That(presets[0].ConditionId, Is.EqualTo("C00_robot_off_voice_off"));
            Assert.That(presets[1].ConditionId, Is.EqualTo("C10_robot_on_voice_off"));
            Assert.That(presets[2].ConditionId, Is.EqualTo("C11_robot_on_voice_on"));
            Assert.That(presets[0].ToPayload(0)["spawn_generation_mode"], Is.EqualTo("DeterministicDebug"));
            Assert.That(presets[0].ToConditionConfig().RobotAssistanceEnabled, Is.False);
            Assert.That(presets[2].ToConditionConfig().RobotAssistanceEnabled, Is.True);
        }

        [Test]
        public void TrialSummary_Carries_Orchestrated_Condition_Metadata()
        {
            var session = new ExperimentSessionMetadata(
                "P001",
                "S001",
                "C10_robot_on_voice_off",
                "AssistedRoundPickAndPlace",
                "MultimodalSimulated",
                "Realistic",
                "Safe",
                "TestScene",
                "notes",
                conditionName: "Robot ON + Voice OFF",
                conditionOrderIndex: 2,
                robotEnabled: true,
                voiceEnabled: false,
                assistanceMode: "AssistedSelection",
                spawnGenerationMode: "DeterministicDebug",
                roundId: "S001_trial_001_round",
                roundIndex: 1,
                roundIndexWithinCondition: 1,
                roundsPerCondition: 1,
                globalRoundIndex: 1,
                allowNonSlotDynamicPlaceFallback: false,
                useDynamicPlacePose: true,
                useDepositZoneSlotAllocator: true,
                maxExpectedPlaceDistance: 1.25f,
                maxRelaxedPlaceDistance: 1.45f,
                placeCandidateReachabilityMargin: 0.20f,
                maxPlaceApproachRetries: 2,
                placeFailureRecoveryMode: "RelaxedRange",
                postPlaceEgressMode: "LocalRetreat");
            var metadata = new ExperimentTrialMetadata(session, "run_001", "trial_001", 1, "2026-06-01T00:00:00Z");
            var accumulator = new ExperimentTrialMetricsAccumulator(metadata, 10f);

            ExperimentTrialSummary summary = accumulator.BuildSummary(true, false, string.Empty, 15f, "2026-06-01T00:00:05Z");

            Assert.That(summary.ConditionId, Is.EqualTo("C10_robot_on_voice_off"));
            Assert.That(summary.ConditionName, Is.EqualTo("Robot ON + Voice OFF"));
            Assert.That(summary.ConditionOrderIndex, Is.EqualTo(2));
            Assert.That(summary.RobotEnabled, Is.True);
            Assert.That(summary.VoiceEnabled, Is.False);
            Assert.That(summary.AssistanceMode, Is.EqualTo("AssistedSelection"));
            Assert.That(summary.SpawnGenerationMode, Is.EqualTo("DeterministicDebug"));
            Assert.That(summary.RoundId, Is.EqualTo("S001_trial_001_round"));
            Assert.That(summary.RoundIndex, Is.EqualTo(1));
            Assert.That(summary.RoundIndexWithinCondition, Is.EqualTo(1));
            Assert.That(summary.RoundsPerCondition, Is.EqualTo(1));
            Assert.That(summary.GlobalRoundIndex, Is.EqualTo(1));
            Assert.That(summary.AllowNonSlotDynamicPlaceFallback, Is.False);
            Assert.That(summary.UseDynamicPlacePose, Is.True);
            Assert.That(summary.UseDepositZoneSlotAllocator, Is.True);
            Assert.That(summary.MaxExpectedPlaceDistance, Is.EqualTo(1.25f));
            Assert.That(summary.MaxRelaxedPlaceDistance, Is.EqualTo(1.45f));
            Assert.That(summary.PlaceCandidateReachabilityMargin, Is.EqualTo(0.20f));
            Assert.That(summary.MaxPlaceApproachRetries, Is.EqualTo(2));
            Assert.That(summary.PlaceFailureRecoveryMode, Is.EqualTo("RelaxedRange"));
            Assert.That(summary.PostPlaceEgressMode, Is.EqualTo("LocalRetreat"));
        }

        [Test]
        public void RuntimeContext_Detects_Condition_Id_Flag_Mismatch()
        {
            var context = new ExperimentRuntimeContext(
                "P001",
                "S001",
                "trial_001",
                1,
                "C01_robot_off_voice_on",
                "Robot OFF + Voice ON",
                1,
                robotEnabled: true,
                voiceEnabled: true,
                RobotAssistanceMode.AssistedSelection,
                SpawnGenerationMode.DeterministicDebug,
                "AssistedRoundPickAndPlace",
                "MultimodalSimulated",
                "S001_trial_001_round");

            Assert.That(context.MatchesConditionPreset(out string reason), Is.False);
            Assert.That(reason, Does.Contain("condition_id_flags_mismatch"));
        }

        [Test]
        public void Default_Final_Presets_Match_RuntimeContext_Validator()
        {
            List<Experiment2x2ConditionDefinition> presets =
                Experiment2x2ConditionDefinition.CreateDefaultPresets(SpawnGenerationMode.DeterministicDebug);

            for (int i = 0; i < presets.Count; i++)
            {
                Experiment2x2ConditionDefinition preset = presets[i];
                var context = new ExperimentRuntimeContext(
                    "P001",
                    "S001",
                    $"trial_{i + 1:000}",
                    i + 1,
                    preset.ConditionId,
                    preset.ConditionName,
                    i,
                    preset.RobotEnabled,
                    preset.VoiceEnabled,
                    preset.AssistanceMode,
                    preset.SpawnGenerationMode,
                    "AssistedRoundPickAndPlace",
                    "MultimodalSimulated",
                    $"S001_trial_{i + 1:000}_round");

                Assert.That(context.MatchesConditionPreset(out string reason), Is.True, $"{preset.ConditionId}: {reason}");
                Assert.That(context.AssistanceMode, Is.EqualTo(preset.RobotEnabled ? preset.AssistanceMode : RobotAssistanceMode.Disabled));
            }
        }

        [Test]
        public void TrialPlan_With_One_Round_Per_Condition_Matches_Final_Three_Trials()
        {
            List<Experiment2x2ConditionDefinition> presets =
                Experiment2x2ConditionDefinition.CreateDefaultPresets(SpawnGenerationMode.DeterministicDebug);

            List<ExperimentTrialPlanEntry> plan = ExperimentTrialPlanBuilder.Build(presets, roundsPerCondition: 1);

            Assert.That(plan, Has.Count.EqualTo(3));
            Assert.That(plan[0].TrialIndex, Is.EqualTo(1));
            Assert.That(plan[0].Condition.ConditionId, Is.EqualTo("C00_robot_off_voice_off"));
            Assert.That(plan[0].RoundIndexWithinCondition, Is.EqualTo(1));
            Assert.That(plan[0].RoundsPerCondition, Is.EqualTo(1));
            Assert.That(plan[1].Condition.ConditionId, Is.EqualTo("C10_robot_on_voice_off"));
            Assert.That(plan[2].Condition.ConditionId, Is.EqualTo("C11_robot_on_voice_on"));
        }

        [TestCase(1, new[] { "C00_robot_off_voice_off", "C10_robot_on_voice_off", "C11_robot_on_voice_on" })]
        [TestCase(2, new[] { "C10_robot_on_voice_off", "C11_robot_on_voice_on", "C00_robot_off_voice_off" })]
        [TestCase(3, new[] { "C11_robot_on_voice_on", "C00_robot_off_voice_off", "C10_robot_on_voice_off" })]
        public void CompensatedConditionOrder_Uses_ThreeCondition_LatinSquare(int participantId, string[] expected)
        {
            IReadOnlyList<string> order = ExperimentCompensatedConditionOrder.BuildForParticipant(participantId);

            Assert.That(order, Is.EqualTo(expected));
            Assert.That(ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(order), Is.True);
            Assert.That(order, Does.Not.Contain("C01_robot_off_voice_on"));
        }

        [Test]
        public void CompensatedConditionOrder_Accepts_AutomaticTimestampParticipantId()
        {
            int parsed = ExperimentCompensatedConditionOrder.ParseParticipantNumber("U20260627_120000");
            IReadOnlyList<string> order = ExperimentCompensatedConditionOrder.BuildForParticipant("U20260627_120000");

            Assert.That(parsed, Is.GreaterThan(1));
            Assert.That(ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(order), Is.True);
            Assert.That(order, Does.Not.Contain("C01_robot_off_voice_on"));
        }

        [Test]
        public void CompensatedConditionOrder_Expands_Homogeneous_Rounds_Without_C01()
        {
            List<Experiment2x2ConditionDefinition> canonical =
                Experiment2x2ConditionDefinition.CreateDefaultPresets(SpawnGenerationMode.DeterministicDebug);
            IReadOnlyList<string> order = ExperimentCompensatedConditionOrder.BuildForParticipant(3);
            List<Experiment2x2ConditionDefinition> ordered = order
                .Select(conditionId => canonical.Single(condition => condition.ConditionId == conditionId))
                .ToList();

            List<ExperimentTrialPlanEntry> plan = ExperimentTrialPlanBuilder.Build(ordered, roundsPerCondition: 2);

            Assert.That(plan, Has.Count.EqualTo(6));
            Assert.That(plan.Select(entry => entry.Condition.ConditionId), Is.EqualTo(new[]
            {
                "C11_robot_on_voice_on",
                "C11_robot_on_voice_on",
                "C00_robot_off_voice_off",
                "C00_robot_off_voice_off",
                "C10_robot_on_voice_off",
                "C10_robot_on_voice_off"
            }));
            Assert.That(plan.Select(entry => entry.RoundIndexWithinCondition), Is.EqualTo(new[] { 1, 2, 1, 2, 1, 2 }));
            Assert.That(plan.All(entry => entry.RoundsPerCondition == 2), Is.True);
            Assert.That(plan.Any(entry => entry.Condition.ConditionId == "C01_robot_off_voice_on"), Is.False);
        }

        [Test]
        public void TrialPlan_With_Three_Rounds_Per_Condition_Expands_Condition_Blocks()
        {
            List<Experiment2x2ConditionDefinition> presets =
                Experiment2x2ConditionDefinition.CreateDefaultPresets(SpawnGenerationMode.DeterministicDebug);

            List<ExperimentTrialPlanEntry> plan = ExperimentTrialPlanBuilder.Build(presets, roundsPerCondition: 3);

            Assert.That(plan, Has.Count.EqualTo(9));
            Assert.That(plan[0].TrialIndex, Is.EqualTo(1));
            Assert.That(plan[0].Condition.ConditionId, Is.EqualTo("C00_robot_off_voice_off"));
            Assert.That(plan[0].RoundIndexWithinCondition, Is.EqualTo(1));
            Assert.That(plan[0].RoundsPerCondition, Is.EqualTo(3));
            Assert.That(plan[1].Condition.ConditionId, Is.EqualTo("C00_robot_off_voice_off"));
            Assert.That(plan[1].RoundIndexWithinCondition, Is.EqualTo(2));
            Assert.That(plan[2].Condition.ConditionId, Is.EqualTo("C00_robot_off_voice_off"));
            Assert.That(plan[2].RoundIndexWithinCondition, Is.EqualTo(3));
            Assert.That(plan[3].TrialIndex, Is.EqualTo(4));
            Assert.That(plan[3].Condition.ConditionId, Is.EqualTo("C10_robot_on_voice_off"));
            Assert.That(plan[3].RoundIndexWithinCondition, Is.EqualTo(1));
            Assert.That(plan.Select(entry => entry.TrialIndex).Distinct().Count(), Is.EqualTo(9));
            Assert.That(plan.Select(entry => $"{entry.Condition.ConditionId}:{entry.RoundIndexWithinCondition}").Distinct().Count(), Is.EqualTo(9));
        }

        [Test]
        public void TrialPlanCursor_Advances_Without_Returning_To_C00()
        {
            List<Experiment2x2ConditionDefinition> presets =
                Experiment2x2ConditionDefinition.CreateDefaultPresets(SpawnGenerationMode.DeterministicDebug);
            var cursor = new ExperimentTrialPlanCursor();
            cursor.Reset(ExperimentTrialPlanBuilder.Build(presets, roundsPerCondition: 3));

            Assert.That(cursor.TryAdvance(out ExperimentTrialPlanEntry first), Is.True);
            Assert.That(cursor.TryAdvance(out ExperimentTrialPlanEntry second), Is.True);
            Assert.That(cursor.TryAdvance(out ExperimentTrialPlanEntry third), Is.True);
            Assert.That(cursor.TryAdvance(out ExperimentTrialPlanEntry fourth), Is.True);

            Assert.That(first.Condition.ConditionId, Is.EqualTo("C00_robot_off_voice_off"));
            Assert.That(first.RoundIndexWithinCondition, Is.EqualTo(1));
            Assert.That(second.Condition.ConditionId, Is.EqualTo("C00_robot_off_voice_off"));
            Assert.That(second.RoundIndexWithinCondition, Is.EqualTo(2));
            Assert.That(third.Condition.ConditionId, Is.EqualTo("C00_robot_off_voice_off"));
            Assert.That(third.RoundIndexWithinCondition, Is.EqualTo(3));
            Assert.That(fourth.Condition.ConditionId, Is.EqualTo("C10_robot_on_voice_off"));
            Assert.That(fourth.RoundIndexWithinCondition, Is.EqualTo(1));
            Assert.That(cursor.CurrentEntryIndex, Is.EqualTo(3));
            Assert.That(cursor.NextEntryIndex, Is.EqualTo(4));
        }

        [Test]
        public void TrialPlanCursor_ManualSelectedCondition_Does_Not_Override_Expanded_Plan()
        {
            List<Experiment2x2ConditionDefinition> presets =
                Experiment2x2ConditionDefinition.CreateDefaultPresets(SpawnGenerationMode.DeterministicDebug);
            int manualSelectedConditionIndex = 1;
            var cursor = new ExperimentTrialPlanCursor();
            cursor.Reset(ExperimentTrialPlanBuilder.Build(presets, roundsPerCondition: 3));

            Assert.That(presets[manualSelectedConditionIndex].ConditionId, Is.EqualTo("C10_robot_on_voice_off"));
            Assert.That(cursor.TryAdvance(out ExperimentTrialPlanEntry first), Is.True);
            Assert.That(cursor.TryAdvance(out _), Is.True);
            Assert.That(cursor.TryAdvance(out _), Is.True);
            Assert.That(cursor.TryAdvance(out ExperimentTrialPlanEntry fourth), Is.True);

            Assert.That(first.Condition.ConditionId, Is.EqualTo("C00_robot_off_voice_off"));
            Assert.That(first.RoundIndexWithinCondition, Is.EqualTo(1));
            Assert.That(fourth.Condition.ConditionId, Is.EqualTo("C10_robot_on_voice_off"));
            Assert.That(fourth.RoundIndexWithinCondition, Is.EqualTo(1));
        }

        [Test]
        public void TrialPlanCursor_CurrentEntry_Does_Not_Advance_Cursor()
        {
            List<Experiment2x2ConditionDefinition> presets =
                Experiment2x2ConditionDefinition.CreateDefaultPresets(SpawnGenerationMode.DeterministicDebug);
            var cursor = new ExperimentTrialPlanCursor();
            cursor.Reset(ExperimentTrialPlanBuilder.Build(presets, roundsPerCondition: 3));
            cursor.TryAdvance(out _);
            cursor.TryAdvance(out _);

            Assert.That(cursor.TryGetCurrent(out ExperimentTrialPlanEntry current), Is.True);
            Assert.That(current.TrialIndex, Is.EqualTo(2));
            Assert.That(current.Condition.ConditionId, Is.EqualTo("C00_robot_off_voice_off"));
            Assert.That(current.RoundIndexWithinCondition, Is.EqualTo(2));
            Assert.That(cursor.CurrentEntryIndex, Is.EqualTo(1));
            Assert.That(cursor.NextEntryIndex, Is.EqualTo(2));
        }

        [Test]
        public void TrialPlanCursor_Clear_Resets_State()
        {
            List<Experiment2x2ConditionDefinition> presets =
                Experiment2x2ConditionDefinition.CreateDefaultPresets(SpawnGenerationMode.DeterministicDebug);
            var cursor = new ExperimentTrialPlanCursor();
            cursor.Reset(ExperimentTrialPlanBuilder.Build(presets, roundsPerCondition: 3));
            cursor.TryAdvance(out _);

            cursor.Clear();

            Assert.That(cursor.CurrentEntryIndex, Is.EqualTo(-1));
            Assert.That(cursor.NextEntryIndex, Is.EqualTo(0));
            Assert.That(cursor.Count, Is.EqualTo(0));
        }

        [Test]
        public void CanonicalPresetMatrix_Rejects_And_Repairs_Legacy_C01_Or_Copied_C00_Flags()
        {
            var corrupted = new List<Experiment2x2ConditionDefinition>
            {
                new Experiment2x2ConditionDefinition("C00_robot_off_voice_off", "", false, false, RobotAssistanceMode.Disabled, SpawnGenerationMode.DeterministicDebug),
                new Experiment2x2ConditionDefinition("C10_robot_on_voice_off", "", false, false, RobotAssistanceMode.Disabled, SpawnGenerationMode.DeterministicDebug),
                new Experiment2x2ConditionDefinition("C11_robot_on_voice_on", "", false, false, RobotAssistanceMode.Disabled, SpawnGenerationMode.DeterministicDebug),
                new Experiment2x2ConditionDefinition("C01_robot_off_voice_on", "", false, true, RobotAssistanceMode.Disabled, SpawnGenerationMode.DeterministicDebug)
            };

            Assert.That(Experiment2x2ConditionDefinition.IsCanonicalPresetMatrix(corrupted, out string reason), Is.False);
            Assert.That(reason, Is.EqualTo("condition_count_mismatch"));

            bool changed = Experiment2x2ConditionDefinition.EnsureCanonicalPresetMatrix(corrupted, SpawnGenerationMode.DeterministicDebug);

            Assert.That(changed, Is.True);
            Assert.That(Experiment2x2ConditionDefinition.IsCanonicalPresetMatrix(corrupted, out reason), Is.True, reason);
            Assert.That(corrupted, Has.Count.EqualTo(3));
            Assert.That(corrupted.Select(condition => condition.ConditionId), Is.EqualTo(new[]
            {
                "C00_robot_off_voice_off",
                "C10_robot_on_voice_off",
                "C11_robot_on_voice_on"
            }));
            Assert.That(corrupted[1].RobotEnabled, Is.True);
            Assert.That(corrupted[1].VoiceEnabled, Is.False);
            Assert.That(corrupted[1].AssistanceMode, Is.EqualTo(RobotAssistanceMode.AssistedSelection));
            Assert.That(corrupted[2].RobotEnabled, Is.True);
            Assert.That(corrupted[2].VoiceEnabled, Is.True);
            Assert.That(corrupted[2].AssistanceMode, Is.EqualTo(RobotAssistanceMode.AssistedSelection));
        }

        [Test]
        public void VoiceOn_RobotOff_LogsIntentButBlocksExecutableBridgeRoute()
        {
            var condition = new ExperimentConditionConfig(
                robotEnabled: false,
                voiceEnabled: true,
                conditionName: "Robot OFF + Voice ON",
                assistanceMode: RobotAssistanceMode.Disabled);

            VoiceCommandExecutionGateResult voice = condition.EvaluateVoiceCommand(
                MultimodalTaskIntent.PickAndPlaceByCategory("A", "ZoneA", "voice_command"));
            AssistanceGateResult assistance = condition.EvaluateRobotAssistance(
                robotBusy: false,
                explicitIntentProcessing: false,
                hasAssignedBox: false);

            Assert.That(voice.Allowed, Is.False);
            Assert.That(voice.Reason, Is.EqualTo("robot_disabled_by_condition"));
            Assert.That(assistance.Allowed, Is.False);
            Assert.That(assistance.Reason, Is.EqualTo("robot_disabled_by_condition"));
        }

        [Test]
        public void Instrumentation_Enriches_PreStartTrial_Events_With_RuntimeContext()
        {
            GameObject gameObject = new GameObject("InstrumentationTest");
            ExperimentInstrumentationController instrumentation = gameObject.AddComponent<ExperimentInstrumentationController>();
            PropertyInfo activeProperty = typeof(ExperimentInstrumentationController).GetProperty(
                "Active",
                BindingFlags.Public | BindingFlags.Static);
            MethodInfo activeSetter = activeProperty?.GetSetMethod(true);
            ExperimentInstrumentationController previousActive =
                activeProperty?.GetValue(null) as ExperimentInstrumentationController;
            Dictionary<string, object> capturedPayload = null;
            TiagoExperimentTelemetry.StructuredEventLogged += Capture;

            try
            {
                activeSetter?.Invoke(null, new object[] { instrumentation });
                var context = new ExperimentRuntimeContext(
                    "P001",
                    "S001",
                    "trial_003",
                    3,
                    "C10_robot_on_voice_off",
                    "Robot ON + Voice OFF",
                    2,
                    robotEnabled: true,
                    voiceEnabled: false,
                    RobotAssistanceMode.AssistedSelection,
                    SpawnGenerationMode.DeterministicDebug,
                    "AssistedRoundPickAndPlace",
                    "MultimodalSimulated",
                    "S001_trial_003_round");

                instrumentation.ApplyOrchestratedContext(context);
                TiagoExperimentTelemetry.LogEvent(
                    "spawn_round_started",
                    new Dictionary<string, object>
                    {
                        ["trial_id"] = string.Empty,
                        ["trial_index"] = 0
                    });

                Assert.That(capturedPayload, Is.Not.Null);
                Assert.That(capturedPayload["session_id"], Is.EqualTo("S001"));
                Assert.That(capturedPayload["trial_id"], Is.EqualTo("trial_003"));
                Assert.That(capturedPayload["trial_index"], Is.EqualTo(3));
                Assert.That(capturedPayload["condition_id"], Is.EqualTo("C10_robot_on_voice_off"));
                Assert.That(capturedPayload["condition_name"], Is.EqualTo("Robot ON + Voice OFF"));
                Assert.That(capturedPayload["robot_enabled"], Is.EqualTo(true));
                Assert.That(capturedPayload["voice_enabled"], Is.EqualTo(false));
                Assert.That(capturedPayload["assistance_mode"], Is.EqualTo("AssistedSelection"));
                Assert.That(capturedPayload["spawn_generation_mode"], Is.EqualTo("DeterministicDebug"));
                Assert.That(capturedPayload["round_id"], Is.EqualTo("S001_trial_003_round"));
            }
            finally
            {
                TiagoExperimentTelemetry.StructuredEventLogged -= Capture;
                activeSetter?.Invoke(null, new object[] { previousActive });
                Object.DestroyImmediate(gameObject);
            }

            void Capture(string eventType, Dictionary<string, object> payload, float unityTime)
            {
                if (eventType == "spawn_round_started")
                {
                    capturedPayload = new Dictionary<string, object>(payload);
                }
            }
        }

        [Test]
        public void Instrumentation_DoesNotReplaceValidZeroConditionOrderIndex_DuringConditionTransition()
        {
            GameObject gameObject = new GameObject("InstrumentationConditionTransitionTest");
            ExperimentInstrumentationController instrumentation = gameObject.AddComponent<ExperimentInstrumentationController>();
            PropertyInfo activeProperty = typeof(ExperimentInstrumentationController).GetProperty(
                "Active",
                BindingFlags.Public | BindingFlags.Static);
            MethodInfo activeSetter = activeProperty?.GetSetMethod(true);
            ExperimentInstrumentationController previousActive =
                activeProperty?.GetValue(null) as ExperimentInstrumentationController;

            try
            {
                activeSetter?.Invoke(null, new object[] { instrumentation });
                instrumentation.ApplyOrchestratedContext(new ExperimentRuntimeContext(
                    "P001",
                    "S001",
                    "trial_003",
                    3,
                    "C10_robot_on_voice_off",
                    "Robot ON + Voice OFF",
                    1,
                    robotEnabled: true,
                    voiceEnabled: false,
                    RobotAssistanceMode.AssistedSelection,
                    SpawnGenerationMode.DeterministicDebug,
                    "AssistedRoundPickAndPlace",
                    "MultimodalSimulated",
                    "S001_trial_003_round"));

                var previousConditionPayload = new Dictionary<string, object>
                {
                    ["condition_id"] = "C00_robot_off_voice_off",
                    ["condition_order_index"] = 0,
                    ["trial_id"] = "trial_002",
                    ["trial_index"] = 2
                };

                ExperimentInstrumentationController.EnrichPayloadWithActiveContext(previousConditionPayload);

                Assert.That(previousConditionPayload["condition_id"], Is.EqualTo("C00_robot_off_voice_off"));
                Assert.That(previousConditionPayload["condition_order_index"], Is.EqualTo(0));
                Assert.That(previousConditionPayload["trial_id"], Is.EqualTo("trial_002"));
                Assert.That(previousConditionPayload["trial_index"], Is.EqualTo(2));
            }
            finally
            {
                activeSetter?.Invoke(null, new object[] { previousActive });
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void RuntimeContext_Is_Single_Source_For_Condition_Payload()
        {
            var config = new RobotAssistancePlaceRecoveryConfig(
                allowNonSlotDynamicPlaceFallback: false,
                useDynamicPlacePose: true,
                useDepositZoneSlotAllocator: true,
                maxExpectedPlaceDistance: 1.25f,
                maxRelaxedPlaceDistance: 1.45f,
                placeCandidateReachabilityMargin: 0.20f,
                maxPlaceApproachRetries: 2);
            var context = new ExperimentRuntimeContext(
                "P001",
                "S001",
                "trial_001",
                1,
                "C10_robot_on_voice_off",
                "Robot ON + Voice OFF",
                2,
                robotEnabled: true,
                voiceEnabled: false,
                RobotAssistanceMode.AssistedSelection,
                SpawnGenerationMode.DeterministicDebug,
                "AssistedRoundPickAndPlace",
                "MultimodalSimulated",
                "S001_trial_001_round",
                placeRecoveryConfig: config,
                placeFailureRecoveryMode: "RelaxedRange",
                postPlaceEgressMode: "LocalRetreat");

            Dictionary<string, object> payload = context.ToPayload();

            Assert.That(context.MatchesConditionPreset(out _), Is.True);
            Assert.That(payload["condition_id"], Is.EqualTo("C10_robot_on_voice_off"));
            Assert.That(payload["robot_enabled"], Is.True);
            Assert.That(payload["voice_enabled"], Is.False);
            Assert.That(payload["spawn_generation_mode"], Is.EqualTo("DeterministicDebug"));
            Assert.That(payload["round_index"], Is.EqualTo(1));
            Assert.That(payload["round_index_within_condition"], Is.EqualTo(1));
            Assert.That(payload["rounds_per_condition"], Is.EqualTo(1));
            Assert.That(payload["global_round_index"], Is.EqualTo(1));
            Assert.That(payload["place_failure_recovery_mode"], Is.EqualTo("RelaxedRange"));
            Assert.That(payload["post_place_egress_mode"], Is.EqualTo("LocalRetreat"));
        }

        [Test]
        public void SelectionPolicy_Chooses_Lowest_Local_Pick_Cost()
        {
            var boxes = new[]
            {
                new BoxRoundItem("box_low_total", "A", "ZoneA", new Vector3(2f, 0f, 0f), new Vector3(3f, 0f, 0f)),
                new BoxRoundItem("box_low_pick", "B", "ZoneB", new Vector3(1f, 0f, 0f), new Vector3(101f, 0f, 0f))
            };
            var state = new BoxRoundState();
            state.Initialize(boxes);

            AssistedBoxSelectionResult result = new AssistedBoxSelectionPolicy().SelectNext(
                boxes,
                state,
                Vector3.zero,
                new FixedRouteCostEstimator(new Dictionary<string, float>
                {
                    ["0,0,0->1,0,0"] = 1f,
                    ["1,0,0->101,0,0"] = 100f,
                    ["0,0,0->2,0,0"] = 2f,
                    ["2,0,0->3,0,0"] = 1f
                }));

            Assert.That(result.Policy, Is.EqualTo(AutonomousSelectionPolicy.LocalPickCost));
            Assert.That(result.SelectedBox.BoxId, Is.EqualTo("box_low_pick"));
            Assert.That(result.Evaluations, Has.Count.EqualTo(2));
            Assert.That(result.Evaluations[0].UsedEuclideanFallback, Is.False);
            Assert.That(result.Evaluations.Single(evaluation => evaluation.BoxId == "box_low_pick").SelectionCost, Is.EqualTo(1f));
            Assert.That(result.Evaluations.Single(evaluation => evaluation.BoxId == "box_low_pick").TotalCost, Is.EqualTo(101f));
            Assert.That(result.Evaluations.Single(evaluation => evaluation.BoxId == "box_low_total").TotalCost, Is.EqualTo(3f));
        }

        [Test]
        public void Completed_Box_Is_Not_Reselected()
        {
            var boxes = new[]
            {
                new BoxRoundItem("box_done", "A", "ZoneA", new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f)),
                new BoxRoundItem("box_pending", "B", "ZoneB", new Vector3(5f, 0f, 0f), new Vector3(6f, 0f, 0f))
            };
            var state = new BoxRoundState();
            state.Initialize(boxes);
            state.TryMarkCompleted("box_done");

            AssistedBoxSelectionResult result = new AssistedBoxSelectionPolicy().SelectNext(
                boxes,
                state,
                Vector3.zero,
                new AlwaysFailsRouteCostEstimator());

            Assert.That(result.SelectedBox.BoxId, Is.EqualTo("box_pending"));
            Assert.That(result.Evaluations, Has.Count.EqualTo(2));
            Assert.That(result.Evaluations.Single(evaluation => evaluation.BoxId == "box_done").Eligible, Is.False);
            Assert.That(result.Evaluations.Single(evaluation => evaluation.BoxId == "box_done").IgnoredReason, Is.EqualTo("deposited"));
            Assert.That(result.Evaluations.Single(evaluation => evaluation.BoxId == "box_pending").UsedEuclideanFallback, Is.True);
        }

        [Test]
        public void SelectionPolicy_Excludes_Assigned_And_Unavailable_Boxes()
        {
            var boxes = new[]
            {
                new BoxRoundItem("box_assigned", "A", "ZoneA", new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f)),
                new BoxRoundItem("box_unavailable", "B", "ZoneB", new Vector3(0.5f, 0f, 0f), new Vector3(1f, 0f, 0f)),
                new BoxRoundItem("box_pending", "C", "ZoneC", new Vector3(4f, 0f, 0f), new Vector3(5f, 0f, 0f))
            };
            var state = new BoxRoundState();
            state.Initialize(boxes);
            state.TryMarkAssigned("box_assigned");
            state.TryMarkExcluded("box_unavailable");

            AssistedBoxSelectionResult result = new AssistedBoxSelectionPolicy().SelectNext(
                boxes,
                state,
                Vector3.zero,
                new AlwaysFailsRouteCostEstimator());

            Assert.That(result.SelectedBox.BoxId, Is.EqualTo("box_pending"));
            Assert.That(result.Evaluations.Single(evaluation => evaluation.BoxId == "box_assigned").IgnoredReason, Is.EqualTo("assigned"));
            Assert.That(result.Evaluations.Single(evaluation => evaluation.BoxId == "box_unavailable").IgnoredReason, Is.EqualTo("unavailable"));
            Assert.That(result.Evaluations.Single(evaluation => evaluation.BoxId == "box_pending").Eligible, Is.True);
        }

        [Test]
        public void SelectionPolicy_TieBreaks_By_Stable_Candidate_Order()
        {
            var boxes = new[]
            {
                new BoxRoundItem("box_first", "A", "ZoneA", new Vector3(1f, 0f, 0f), new Vector3(10f, 0f, 0f)),
                new BoxRoundItem("box_second", "B", "ZoneB", new Vector3(-1f, 0f, 0f), new Vector3(2f, 0f, 0f))
            };
            var state = new BoxRoundState();
            state.Initialize(boxes);

            AssistedBoxSelectionResult result = new AssistedBoxSelectionPolicy().SelectNext(
                boxes,
                state,
                Vector3.zero,
                new AlwaysFailsRouteCostEstimator());

            Assert.That(result.SelectedBox.BoxId, Is.EqualTo("box_first"));
            Assert.That(result.Evaluations.All(evaluation => evaluation.SelectionCost == 1f), Is.True);
        }

        [Test]
        public void Assistance_Is_Blocked_While_Explicit_Intent_Is_Processing()
        {
            var condition = new ExperimentConditionConfig(robotEnabled: true, voiceEnabled: true);

            AssistanceGateResult assistance = condition.EvaluateRobotAssistance(
                robotBusy: false,
                explicitIntentProcessing: true,
                hasAssignedBox: false);

            Assert.That(assistance.Allowed, Is.False);
            Assert.That(assistance.Reason, Is.EqualTo("explicit_intent_processing"));
        }

        [Test]
        public void Round_Completes_When_No_Pending_Boxes_Remain()
        {
            var boxes = new[]
            {
                new BoxRoundItem("box_a", "A", "ZoneA", Vector3.zero, Vector3.one)
            };
            var state = new BoxRoundState();
            state.Initialize(boxes);

            Assert.That(state.RoundCompleted, Is.False);
            state.TryMarkCompleted("box_a");

            Assert.That(state.RoundCompleted, Is.True);
        }

        [Test]
        public void RoundState_Can_Rearm_Assigned_Box_For_Retry()
        {
            var boxes = new[]
            {
                new BoxRoundItem("box_retry", "A", "ZoneA", Vector3.zero, Vector3.one)
            };
            var state = new BoxRoundState();
            state.Initialize(boxes);
            Assert.That(state.TryMarkAssigned("box_retry"), Is.True);

            Assert.That(state.TryMarkPendingFromAssigned("box_retry"), Is.True);

            Assert.That(state.IsSelectable(boxes[0]), Is.True);
            Assert.That(state.PendingCount, Is.EqualTo(1));
            Assert.That(state.AssignedCount, Is.EqualTo(0));
        }

        [Test]
        public void RoundState_Tracks_Excluded_Count_For_Incomplete_Rounds()
        {
            var boxes = new[]
            {
                new BoxRoundItem("box_done", "A", "ZoneA", Vector3.zero, Vector3.one),
                new BoxRoundItem("box_failed", "B", "ZoneB", Vector3.zero, Vector3.one)
            };
            var state = new BoxRoundState();
            state.Initialize(boxes);
            state.TryMarkCompleted("box_done");
            state.TryMarkExcluded("box_failed");

            Assert.That(state.RoundCompleted, Is.True);
            Assert.That(state.CompletedCount, Is.EqualTo(1));
            Assert.That(state.ExcludedCount, Is.EqualTo(1));
        }

        [Test]
        public void SelectionPolicy_Preserves_Selected_Approach_Metadata()
        {
            var boxes = new[]
            {
                new BoxRoundItem(
                    "box_approach",
                    "A",
                    "ZoneA",
                    new Vector3(1f, 0f, 0f),
                    new Vector3(2f, 0f, 0f),
                    "box_approach:East",
                    "East",
                    "DynamicBoundsSide")
            };
            var state = new BoxRoundState();
            state.Initialize(boxes);

            AssistedBoxSelectionResult result = new AssistedBoxSelectionPolicy().SelectNext(
                boxes,
                state,
                Vector3.zero,
                new AlwaysFailsRouteCostEstimator());

            Assert.That(result.SelectedBox.PickupApproachCandidateId, Is.EqualTo("box_approach:East"));
            Assert.That(result.Evaluations[0].PickupApproachCandidateId, Is.EqualTo("box_approach:East"));
            Assert.That(result.Evaluations[0].PickupApproachSide, Is.EqualTo("East"));
        }

        [Test]
        public void DeterministicDebug_SpawnSequence_Is_Stable()
        {
            List<int> first = SpawnSequenceBuilder.Build(
                spawnCount: 5,
                prefabCount: 3,
                mode: SpawnGenerationMode.DeterministicDebug);
            List<int> second = SpawnSequenceBuilder.Build(
                spawnCount: 5,
                prefabCount: 3,
                mode: SpawnGenerationMode.DeterministicDebug);

            Assert.That(first, Is.EqualTo(new[] { 0, 1, 2, 0, 1 }));
            Assert.That(second, Is.EqualTo(first));
        }

        [Test]
        public void DeterministicDebug_Uses_Configured_Prefab_Index_List()
        {
            List<int> sequence = SpawnSequenceBuilder.Build(
                spawnCount: 5,
                prefabCount: 3,
                mode: SpawnGenerationMode.DeterministicDebug,
                deterministicPrefabIndices: new[] { 2, 2, 1 });

            Assert.That(sequence, Is.EqualTo(new[] { 2, 2, 1, 2, 2 }));
        }

        [Test]
        public void RandomBalanced_Builds_Balanced_Sequence_And_Applies_Shuffle_Hook()
        {
            bool shuffleCalled = false;
            List<int> sequence = SpawnSequenceBuilder.Build(
                spawnCount: 5,
                prefabCount: 3,
                mode: SpawnGenerationMode.RandomBalanced,
                shuffle: values =>
                {
                    shuffleCalled = true;
                    int first = values[0];
                    values[0] = values[4];
                    values[4] = first;
                });

            Assert.That(shuffleCalled, Is.True);
            Assert.That(sequence, Is.EquivalentTo(new[] { 0, 1, 2, 0, 1 }));
            Assert.That(sequence, Is.EqualTo(new[] { 1, 1, 2, 0, 0 }));
        }

        [Test]
        public void RoundState_Reset_Drops_Previous_Assignments()
        {
            var firstRound = new[]
            {
                new BoxRoundItem("old_box", "A", "ZoneA", Vector3.zero, Vector3.one)
            };
            var secondRound = new[]
            {
                new BoxRoundItem("new_box", "B", "ZoneB", Vector3.zero, Vector3.one)
            };
            var state = new BoxRoundState();
            state.Initialize(firstRound);
            state.TryMarkAssigned("old_box");

            state.Initialize(secondRound);

            Assert.That(state.GetStatus("old_box"), Is.EqualTo(BoxRoundStatus.Excluded));
            Assert.That(state.IsSelectable(secondRound[0]), Is.True);
        }

        [Test]
        public void HeldPoseTelemetry_LogsOncePerContinuousDriftEpisodeWhileCorrectionContinues()
        {
            var anchor = new GameObject("P47A Pose Anchor");
            var held = new GameObject("P47A Held Box");
            try
            {
                held.transform.SetParent(anchor.transform, false);
                HeldObjectPoseLock poseLock = held.AddComponent<HeldObjectPoseLock>();
                var counts = new Dictionary<string, int>();
                poseLock.Initialize(
                    anchor.transform,
                    Vector3.zero,
                    Quaternion.identity,
                    null,
                    null,
                    new Collider[0],
                    0.03f,
                    0.01f,
                    "held_box",
                    (eventType, _) => counts[eventType] = counts.TryGetValue(eventType, out int count) ? count + 1 : 1);

                held.transform.localPosition = Vector3.right * 0.1f;
                InvokePrivate(poseLock, "Enforce", "late_update");
                Assert.That(held.transform.localPosition, Is.EqualTo(Vector3.zero));

                held.transform.localPosition = Vector3.right * 0.2f;
                InvokePrivate(poseLock, "Enforce", "late_update");
                Assert.That(held.transform.localPosition, Is.EqualTo(Vector3.zero));
                Assert.That(counts["manipulation_held_pose_drift_detected"], Is.EqualTo(1));
                Assert.That(counts["manipulation_held_pose_enforced"], Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(held);
                Object.DestroyImmediate(anchor);
            }
        }

        [Test]
        public void CandidateTelemetrySignature_ChangesOnlyWhenAggregatePayloadChanges()
        {
            MethodInfo method = typeof(RobotAssistanceRoundCoordinator).GetMethod(
                "BuildTelemetrySignature",
                BindingFlags.Static | BindingFlags.NonPublic);
            var first = new List<Dictionary<string, object>>
            {
                new() { ["candidate_id"] = "ZoneA:North", ["rejection_reason"] = string.Empty }
            };
            var same = new List<Dictionary<string, object>>
            {
                new() { ["candidate_id"] = "ZoneA:North", ["rejection_reason"] = string.Empty }
            };
            var changed = new List<Dictionary<string, object>>
            {
                new() { ["candidate_id"] = "ZoneA:North", ["rejection_reason"] = "post_place_clearance_failed" }
            };

            string firstSignature = (string)method.Invoke(null, new object[] { first });
            string sameSignature = (string)method.Invoke(null, new object[] { same });
            string changedSignature = (string)method.Invoke(null, new object[] { changed });

            Assert.That(sameSignature, Is.EqualTo(firstSignature));
            Assert.That(changedSignature, Is.Not.EqualTo(firstSignature));
        }

        private sealed class FixedRouteCostEstimator : IAssistedRouteCostEstimator
        {
            private readonly Dictionary<string, float> _costs;

            public FixedRouteCostEstimator(Dictionary<string, float> costs)
            {
                _costs = costs;
            }

            public bool TryEstimatePathLength(Vector3 from, Vector3 to, out float length, out string failureReason)
            {
                if (_costs.TryGetValue($"{Format(from)}->{Format(to)}", out length))
                {
                    failureReason = string.Empty;
                    return true;
                }

                failureReason = "missing_cost";
                return false;
            }

            private static string Format(Vector3 value)
            {
                return $"{value.x:0},{value.y:0},{value.z:0}";
            }
        }

        private sealed class AlwaysFailsRouteCostEstimator : IAssistedRouteCostEstimator
        {
            public bool TryEstimatePathLength(Vector3 from, Vector3 to, out float length, out string failureReason)
            {
                length = 0f;
                failureReason = "simulated_navmesh_unavailable";
                return false;
            }
        }

        private static void SetPrivateField(object instance, string fieldName, object value)
        {
            instance.GetType()
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(instance, value);
        }

        private static object InvokePrivate(object instance, string methodName, params object[] arguments)
        {
            return instance.GetType()
                .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(instance, arguments);
        }

        private static T InvokePrivate<T>(object instance, string methodName, params object[] arguments)
        {
            return (T)InvokePrivate(instance, methodName, arguments);
        }
    }
}
