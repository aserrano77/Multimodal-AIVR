using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    public sealed class ExperimentSavedExitConditionOrderTests
    {
        private static readonly string[][] Orders =
        {
            new[] { ExperimentCompensatedConditionOrder.C10, ExperimentCompensatedConditionOrder.C11, ExperimentCompensatedConditionOrder.C00 },
            new[] { ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C11, ExperimentCompensatedConditionOrder.C10 },
            new[] { ExperimentCompensatedConditionOrder.C11, ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C10 }
        };

        private string _tempDirectory;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "ExperimentSavedExitConditionOrderTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
            ExperimentDataPathResolver.ResetForTests(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            ExperimentDataPathResolver.ResetForTests(null);
            ExperimentSessionIdHistoryStore.OverrideDefaultPathForTests(null);
            if (!string.IsNullOrWhiteSpace(_tempDirectory) && Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public void SavePrueba1Round1_RestoresSameOrderConditionVisibleNumberAndRound1()
        {
            string[] order = Orders[0];
            ExperimentSessionIdHistoryEntry checkpoint = Checkpoint(order, conditionOrderIndex: 0, savedRound: 1, internalAttempt: 1);

            bool valid = Resolve(checkpoint, order, out ExperimentSavedExitCheckpointResolution restored, out string reason);

            Assert.That(valid, Is.True, reason);
            Assert.That(restored.ConditionOrderIds, Is.EqualTo(order));
            Assert.That(restored.ConditionId, Is.EqualTo(ExperimentCompensatedConditionOrder.C10));
            Assert.That(restored.ConditionOrderIndex, Is.Zero);
            Assert.That(restored.VisiblePruebaNumber, Is.EqualTo(1));
            Assert.That(restored.RestoredRoundIndex, Is.EqualTo(1));
            Assert.That(restored.ConditionOrderIds[1], Is.EqualTo(ExperimentCompensatedConditionOrder.C11));
            Assert.That(restored.ConditionOrderIds[2], Is.EqualTo(ExperimentCompensatedConditionOrder.C00));
            Assert.That(ExperimentInstructionClipResolver.Resolve(restored.ConditionId, "Prueba 1"),
                Is.EqualTo("instruction_prueba_1_robot_autonomous"));
        }

        [Test]
        public void SavePrueba1Round2_RestartsSameConditionAtRound1()
        {
            string[] order = Orders[0];
            ExperimentSessionIdHistoryEntry checkpoint = Checkpoint(order, conditionOrderIndex: 0, savedRound: 2, internalAttempt: 2);

            Assert.That(Resolve(checkpoint, order, out ExperimentSavedExitCheckpointResolution restored, out string reason), Is.True, reason);
            Assert.That(restored.SavedRoundIndex, Is.EqualTo(2));
            Assert.That(restored.RestoredRoundIndex, Is.EqualTo(1));
            Assert.That(restored.ConditionId, Is.EqualTo(order[0]));
            Assert.That(restored.VisiblePruebaNumber, Is.EqualTo(1));
        }

        [Test]
        public void SavePrueba2_RestoresCompletedPrefixAndPendingSuffixInOriginalOrder()
        {
            string[] order = Orders[1];
            ExperimentSessionIdHistoryEntry checkpoint = Checkpoint(order, conditionOrderIndex: 1, savedRound: 2, internalAttempt: 4);

            Assert.That(Resolve(checkpoint, order, out ExperimentSavedExitCheckpointResolution restored, out string reason), Is.True, reason);
            Assert.That(restored.ConditionId, Is.EqualTo(ExperimentCompensatedConditionOrder.C11));
            Assert.That(restored.VisiblePruebaNumber, Is.EqualTo(2));
            Assert.That(restored.ConditionOrderIds[0], Is.EqualTo(ExperimentCompensatedConditionOrder.C00));
            Assert.That(restored.ConditionOrderIds[2], Is.EqualTo(ExperimentCompensatedConditionOrder.C10));
            Assert.That(ExperimentInstructionClipResolver.Resolve(restored.ConditionId, "Prueba 2"),
                Is.EqualTo("instruction_prueba_2_robot_voice"));
        }

        [Test]
        public void SavePrueba3_DoesNotCompleteSessionOrWrapToPrueba1()
        {
            string[] order = Orders[2];
            ExperimentSessionIdHistoryEntry checkpoint = Checkpoint(order, conditionOrderIndex: 2, savedRound: 1, internalAttempt: 5);

            Assert.That(Resolve(checkpoint, order, out ExperimentSavedExitCheckpointResolution restored, out string reason), Is.True, reason);
            Assert.That(restored.ConditionId, Is.EqualTo(order[2]));
            Assert.That(restored.ConditionOrderIndex, Is.EqualTo(2));
            Assert.That(restored.VisiblePruebaNumber, Is.EqualTo(3));
            Assert.That(restored.RestoredRoundIndex, Is.EqualTo(1));
        }

        [TestCaseSource(nameof(AllOrderAndPositionCases))]
        public void EveryCounterbalancedOrderAndPositionRemainsImmutable(string[] order, int conditionOrderIndex)
        {
            ExperimentSessionIdHistoryEntry checkpoint = Checkpoint(order, conditionOrderIndex, savedRound: 1, internalAttempt: conditionOrderIndex + 1);

            Assert.That(Resolve(checkpoint, order, out ExperimentSavedExitCheckpointResolution restored, out string reason), Is.True, reason);
            Assert.That(restored.ConditionOrderIds, Is.EqualTo(order));
            Assert.That(restored.ConditionOrderIndex, Is.EqualTo(conditionOrderIndex));
            Assert.That(restored.VisiblePruebaNumber, Is.EqualTo(conditionOrderIndex + 1));
        }

        [Test]
        public void TwoSaveResumeCyclesKeepVisiblePruebaWhileInternalAttemptCanIncrease()
        {
            string[] order = Orders[0];
            ExperimentSessionIdHistoryEntry first = Checkpoint(order, 0, savedRound: 1, internalAttempt: 1);
            Assert.That(Resolve(first, order, out ExperimentSavedExitCheckpointResolution firstResume, out string firstReason), Is.True, firstReason);

            ExperimentSessionIdHistoryEntry second = Checkpoint(order, 0, savedRound: 2, internalAttempt: firstResume.InternalTrialAttemptIndex + 1);
            Assert.That(Resolve(second, order, out ExperimentSavedExitCheckpointResolution secondResume, out string secondReason), Is.True, secondReason);

            Assert.That(secondResume.ConditionOrderIds, Is.EqualTo(firstResume.ConditionOrderIds));
            Assert.That(secondResume.VisiblePruebaNumber, Is.EqualTo(firstResume.VisiblePruebaNumber));
            Assert.That(secondResume.InternalTrialAttemptIndex, Is.EqualTo(firstResume.InternalTrialAttemptIndex + 1));
        }

        [Test]
        public void ResumeIntegration_LoggerRunCannotReplaceCheckpointOrderBeforeProtocolInstructions()
        {
            string[] checkpointOrder = Orders[0];
            string[] loggerScenePlan =
            {
                ExperimentCompensatedConditionOrder.C00,
                ExperimentCompensatedConditionOrder.C10,
                ExperimentCompensatedConditionOrder.C11
            };
            ExperimentDataPathResolver.SessionContext session = ExperimentDataPathResolver.ConfigureSession(
                "U20260715_071930",
                "S20260715_071930",
                checkpointOrder,
                roundsPerCondition: 2);
            ExperimentSessionIdHistoryStore.UpdateSavedExitCheckpoint(
                session.SessionId,
                checkpointOrder[0],
                visiblePrueba: 1,
                roundIndex: 2,
                conditionOrder: ExperimentCompensatedConditionOrder.FormatOrder(checkpointOrder),
                resumePolicy: "condition_start",
                conditionOrderIds: checkpointOrder,
                conditionOrderIndex: 0,
                internalTrialAttemptIndex: 1,
                partialTrialCloseReason: ExperimentSessionIdHistoryStore.SavedExitIncompleteConditionRestartReason);
            ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.SavedExitStatus);

            string[] exportOrder = TiagoExperimentLogger.ResolveSessionExportConditionOrderForDiagnostics(
                session.ConditionOrder,
                loggerScenePlan,
                out bool preservedAuthoritativeOrder);
            ExperimentDataPathResolver.WriteSessionExportInfo(
                session.SessionRoot,
                session.SessionId,
                session.ParticipantId,
                exportOrder,
                session.QuestionnaireCode);

            Assert.That(preservedAuthoritativeOrder, Is.True);
            Assert.That(exportOrder, Is.EqualTo(checkpointOrder));
            Assert.That(
                ExperimentSavedExitResumePromptState.TryFindPendingSession(
                    ExperimentSessionIdHistoryStore.LoadRecent(10),
                    out ExperimentSessionIdHistoryEntry promptCheckpoint),
                Is.True);
            Assert.That(ExperimentSavedExitResumePromptState.BuildResumeButtonLabel(promptCheckpoint),
                Is.EqualTo("Continuar desde Prueba 1"));
            Assert.That(
                ExperimentDataPathResolver.TryReadPersistedSessionConditionOrder(
                    promptCheckpoint.user_id,
                    promptCheckpoint.session_id,
                    out List<string> persistedOrder,
                    out string metadataReason),
                Is.True,
                metadataReason);
            Assert.That(persistedOrder, Is.EqualTo(checkpointOrder));
            Assert.That(
                ExperimentSessionIdHistoryStore.TryResolveSavedExitCheckpoint(
                    promptCheckpoint,
                    persistedOrder,
                    Array.Empty<string>(),
                    ExperimentCompensatedConditionOrder.BuildForParticipant(promptCheckpoint.user_id),
                    out ExperimentSavedExitCheckpointResolution restored,
                    out string resolutionReason),
                Is.True,
                resolutionReason);

            List<Experiment2x2ConditionDefinition> presets =
                Experiment2x2ConditionDefinition.CreateDefaultPresets(SpawnGenerationMode.RandomBalanced);
            List<Experiment2x2ConditionDefinition> ordered = restored.ConditionOrderIds
                .Select(id => presets.Find(condition => condition.ConditionId == id))
                .ToList();
            List<ExperimentTrialPlanEntry> protocolPlan = ExperimentTrialPlanBuilder.Build(ordered, roundsPerCondition: 2);

            Assert.That(restored.ConditionId, Is.EqualTo(ExperimentCompensatedConditionOrder.C10));
            Assert.That(restored.ConditionOrderIndex, Is.Zero);
            Assert.That(restored.VisiblePruebaNumber, Is.EqualTo(1));
            Assert.That(restored.RestoredRoundIndex, Is.EqualTo(1));
            Assert.That(protocolPlan[0].Condition.ConditionId, Is.EqualTo(restored.ConditionId));
            Assert.That(protocolPlan[0].ConditionOrderIndex, Is.EqualTo(restored.ConditionOrderIndex));
            Assert.That(protocolPlan[0].RoundIndexWithinCondition, Is.EqualTo(restored.RestoredRoundIndex));
            Assert.That(ExperimentInstructionClipResolver.Resolve(restored.ConditionId, "Prueba 1"),
                Is.EqualTo("instruction_prueba_1_robot_autonomous"));
            Assert.That((UnityEngine.Color32)ExperimentRuntimeButtonUtility.BuildColorBlock(ExperimentButtonRole.Primary).normalColor,
                Is.EqualTo(new UnityEngine.Color32(0x00, 0x6E, 0xA6, 0xFF)));
        }

        [Test]
        public void Version2PersistenceStoresFullOrderIndexAndExplicitPartialCloseReason()
        {
            string[] order = Orders[0];
            ExperimentSessionIdHistoryEntry session = ExperimentSessionIdHistoryStore.RegisterSessionStarted(
                "U20260714_180000",
                "S20260714_180000",
                "2026-07-14T18:00:00.0000000Z",
                order);

            ExperimentSessionIdHistoryStore.UpdateSavedExitCheckpoint(
                session.session_id,
                order[0],
                visiblePrueba: 1,
                roundIndex: 2,
                conditionOrder: ExperimentCompensatedConditionOrder.FormatOrder(order),
                resumePolicy: "condition_start",
                conditionOrderIds: order,
                conditionOrderIndex: 0,
                internalTrialAttemptIndex: 3,
                partialTrialCloseReason: ExperimentSessionIdHistoryStore.SavedExitIncompleteConditionRestartReason);

            ExperimentSessionIdHistoryEntry stored = ExperimentSessionIdHistoryStore.LoadRecent(1)[0];
            string json = File.ReadAllText(ExperimentSessionIdHistoryStore.DefaultPath);
            Assert.That(json, Does.Contain("\"schema_version\": 3"));
            Assert.That(stored.questionnaire_code_scheme, Is.EqualTo(QuestionnaireCodeCodec.Scheme));
            Assert.That(stored.saved_exit_checkpoint_schema_version, Is.EqualTo(2));
            Assert.That(stored.condition_order_ids, Is.EqualTo(order));
            Assert.That(stored.saved_condition_order_index, Is.Zero);
            Assert.That(stored.internal_trial_attempt_index, Is.EqualTo(3));
            Assert.That(stored.partial_trial_close_reason,
                Is.EqualTo(ExperimentSessionIdHistoryStore.SavedExitIncompleteConditionRestartReason));
        }

        [Test]
        public void LegacyArrowCheckpointIsRecoveredWithoutReordering()
        {
            string[] order = Orders[0];
            var checkpoint = new ExperimentSessionIdHistoryEntry
            {
                saved_condition_id = order[0],
                saved_visible_prueba = 1,
                saved_round_index = 2,
                condition_order = "C10 -> C11 -> C00",
                resume_policy = "condition_start"
            };

            Assert.That(Resolve(checkpoint, order, out ExperimentSavedExitCheckpointResolution restored, out string reason), Is.True, reason);
            Assert.That(restored.ConditionOrderIds, Is.EqualTo(order));
            Assert.That(restored.OrderSource, Is.EqualTo("legacy_checkpoint_condition_order"));
            Assert.That(restored.CompatibilityFallback, Is.True);
        }

        [Test]
        public void LegacyCheckpointWithoutOrderUsesPersistedSessionMetadataBeforeParticipantFallback()
        {
            string[] order = Orders[1];
            var checkpoint = new ExperimentSessionIdHistoryEntry
            {
                saved_condition_id = order[1],
                saved_visible_prueba = 2,
                saved_round_index = 1,
                resume_policy = "condition_start"
            };

            Assert.That(Resolve(checkpoint, order, out ExperimentSavedExitCheckpointResolution restored, out string reason), Is.True, reason);
            Assert.That(restored.ConditionOrderIds, Is.EqualTo(order));
            Assert.That(restored.OrderSource, Is.EqualTo("session_export_info_condition_order"));
        }

        [Test]
        public void MismatchedSavedVisibleNumberIsRejectedInsteadOfSilentlyReordered()
        {
            string[] order = Orders[0];
            ExperimentSessionIdHistoryEntry checkpoint = Checkpoint(order, conditionOrderIndex: 0, savedRound: 1, internalAttempt: 1);
            checkpoint.saved_visible_prueba = 2;

            bool valid = Resolve(checkpoint, order, out _, out string reason);

            Assert.That(valid, Is.False);
            Assert.That(reason, Is.EqualTo("saved_visible_prueba_mismatch"));
        }

        [Test]
        public void CompletingResumedConditionAdvancesToNextConditionInOriginalOrder()
        {
            string[] order = Orders[0];
            ExperimentSessionIdHistoryEntry checkpoint = Checkpoint(order, conditionOrderIndex: 0, savedRound: 2, internalAttempt: 2);
            Assert.That(Resolve(checkpoint, order, out ExperimentSavedExitCheckpointResolution restored, out string reason), Is.True, reason);
            List<Experiment2x2ConditionDefinition> presets =
                Experiment2x2ConditionDefinition.CreateDefaultPresets(SpawnGenerationMode.RandomBalanced);
            List<Experiment2x2ConditionDefinition> ordered = restored.ConditionOrderIds
                .Select(id => presets.Find(condition => condition.ConditionId == id))
                .ToList();
            List<ExperimentTrialPlanEntry> plan = ExperimentTrialPlanBuilder.Build(ordered, roundsPerCondition: 2);

            Assert.That(plan[0].Condition.ConditionId, Is.EqualTo(ExperimentCompensatedConditionOrder.C10));
            Assert.That(plan[1].Condition.ConditionId, Is.EqualTo(ExperimentCompensatedConditionOrder.C10));
            Assert.That(plan[2].Condition.ConditionId, Is.EqualTo(ExperimentCompensatedConditionOrder.C11));
            Assert.That(plan[4].Condition.ConditionId, Is.EqualTo(ExperimentCompensatedConditionOrder.C00));
        }

        [TestCase("C00_robot_off_voice_off", "Prueba 1", "instruction_prueba_1_manual")]
        [TestCase("C10_robot_on_voice_off", "Prueba 2", "instruction_prueba_2_robot_autonomous")]
        [TestCase("C11_robot_on_voice_on", "Prueba 3", "instruction_prueba_3_robot_voice")]
        public void NarrationUsesStableVisiblePruebaNumberNotAttemptIndex(string conditionId, string visibleLabel, string expectedClip)
        {
            Assert.That(ExperimentInstructionClipResolver.Resolve(conditionId, visibleLabel), Is.EqualTo(expectedClip));
        }

        private static IEnumerable<TestCaseData> AllOrderAndPositionCases()
        {
            foreach (string[] order in Orders)
            {
                for (int index = 0; index < order.Length; index++)
                {
                    yield return new TestCaseData(order, index)
                        .SetName($"Order_{ExperimentCompensatedConditionOrder.FormatOrder(order).Replace(" -> ", "_")}_Position_{index + 1}");
                }
            }
        }

        private static ExperimentSessionIdHistoryEntry Checkpoint(
            IReadOnlyList<string> order,
            int conditionOrderIndex,
            int savedRound,
            int internalAttempt)
        {
            return new ExperimentSessionIdHistoryEntry
            {
                session_id = "S20260714_180000",
                user_id = "U20260714_180000",
                questionnaire_code = "ABCDEF",
                status = ExperimentSessionIdHistoryStore.SavedExitStatus,
                saved_exit_checkpoint_schema_version = ExperimentSessionIdHistoryStore.CurrentSavedExitCheckpointSchemaVersion,
                condition_order_ids = new List<string>(order),
                condition_order = string.Join(",", order),
                saved_condition_order_index = conditionOrderIndex,
                saved_condition_id = order[conditionOrderIndex],
                saved_visible_prueba = conditionOrderIndex + 1,
                saved_round_index = savedRound,
                resume_policy = "condition_start",
                internal_trial_attempt_index = internalAttempt,
                partial_trial_close_reason = ExperimentSessionIdHistoryStore.SavedExitIncompleteConditionRestartReason
            };
        }

        private static bool Resolve(
            ExperimentSessionIdHistoryEntry checkpoint,
            IReadOnlyList<string> sessionMetadataOrder,
            out ExperimentSavedExitCheckpointResolution resolution,
            out string reason)
        {
            return ExperimentSessionIdHistoryStore.TryResolveSavedExitCheckpoint(
                checkpoint,
                sessionMetadataOrder,
                Array.Empty<string>(),
                ExperimentCompensatedConditionOrder.BuildForParticipant(checkpoint.user_id),
                out resolution,
                out reason);
        }
    }
}
