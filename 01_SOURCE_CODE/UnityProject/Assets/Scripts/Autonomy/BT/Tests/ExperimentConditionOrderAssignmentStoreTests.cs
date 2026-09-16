using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    public sealed class ExperimentConditionOrderAssignmentStoreTests
    {
        private const int AcceptanceSeed = 409731812;
        private string _tempDirectory;
        private string _assignmentPath;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "ExperimentConditionOrderAssignmentStoreTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
            _assignmentPath = Path.Combine(_tempDirectory, "condition_order_assignments.json");
            ExperimentConditionOrderAssignmentStore.OverrideDefaultPathForTests(_assignmentPath);
            ExperimentConditionOrderAssignmentStore.OverrideBaseSeedForTests(AcceptanceSeed);
        }

        [TearDown]
        public void TearDown()
        {
            ExperimentDataPathResolver.ResetForTests(null);
            ExperimentConditionOrderAssignmentStore.OverrideDefaultPathForTests(null);
            ExperimentConditionOrderAssignmentStore.OverrideBaseSeedForTests(null);
            if (!string.IsNullOrWhiteSpace(_tempDirectory) && Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public void CompleteBlockContainsEverySequenceExactlyOnce()
        {
            List<string> prefixes = Enumerable.Range(1, 6)
                .Select(index => ExperimentConditionOrderAssignmentStore
                    .GetOrCreate("U_BLOCK_" + index)
                    .sequence_prefix)
                .ToList();

            Assert.That(prefixes, Is.EqualTo(new[] { "B", "E", "A", "F", "D", "C" }));
            Assert.That(prefixes.OrderBy(value => value), Is.EqualTo(new[] { "A", "B", "C", "D", "E", "F" }));
        }

        [Test]
        public void ConsecutiveParticipationsConsumePositionsWithoutRestartingBlock()
        {
            ExperimentConditionOrderAssignment first = ExperimentConditionOrderAssignmentStore.GetOrCreate("U_ONE");
            ExperimentConditionOrderAssignment second = ExperimentConditionOrderAssignmentStore.GetOrCreate("U_TWO");
            ExperimentConditionOrderAssignment third = ExperimentConditionOrderAssignmentStore.GetOrCreate("U_THREE");

            Assert.That(new[] { first.sequence_prefix, second.sequence_prefix, third.sequence_prefix },
                Is.EqualTo(new[] { "B", "E", "A" }));
            Assert.That(new[] { first.block_position, second.block_position, third.block_position },
                Is.EqualTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void ApplicationRestartReloadsStateAndKeepsNextPosition()
        {
            ExperimentConditionOrderAssignmentStore.GetOrCreate("U_BEFORE_RESTART");
            ExperimentConditionOrderAssignmentStore.OverrideDefaultPathForTests(null);
            ExperimentConditionOrderAssignmentStore.OverrideBaseSeedForTests(null);
            ExperimentConditionOrderAssignmentStore.OverrideDefaultPathForTests(_assignmentPath);
            ExperimentConditionOrderAssignmentStore.OverrideBaseSeedForTests(AcceptanceSeed);

            ExperimentConditionOrderAssignment afterRestart =
                ExperimentConditionOrderAssignmentStore.GetOrCreate("U_AFTER_RESTART");

            Assert.That(afterRestart.sequence_prefix, Is.EqualTo("E"));
            Assert.That(afterRestart.block_position, Is.EqualTo(1));
        }

        [Test]
        public void SameParticipationKeepsAssignmentAcrossResumeAndRestart()
        {
            ExperimentConditionOrderAssignment original = ExperimentConditionOrderAssignmentStore.GetOrCreate("U_SAME");
            ExperimentConditionOrderAssignment resumed = ExperimentConditionOrderAssignmentStore.GetOrCreate("U_SAME");
            ExperimentConditionOrderAssignment restarted = ExperimentConditionOrderAssignmentStore.GetOrCreate("U_SAME");
            ExperimentConditionOrderAssignment next = ExperimentConditionOrderAssignmentStore.GetOrCreate("U_NEXT");

            Assert.That(resumed.sequence_prefix, Is.EqualTo(original.sequence_prefix));
            Assert.That(restarted.condition_order_ids, Is.EqualTo(original.condition_order_ids));
            Assert.That(next.sequence_prefix, Is.EqualTo("E"));
            Assert.That(next.block_position, Is.EqualTo(1));
        }

        [Test]
        public void ExhaustedBlockCreatesAnotherCompleteRandomizedBlock()
        {
            List<ExperimentConditionOrderAssignment> assignments = Enumerable.Range(1, 12)
                .Select(index => ExperimentConditionOrderAssignmentStore.GetOrCreate("U_" + index))
                .ToList();

            Assert.That(assignments.Take(6).Select(item => item.sequence_prefix).OrderBy(value => value),
                Is.EqualTo(new[] { "A", "B", "C", "D", "E", "F" }));
            Assert.That(assignments.Skip(6).Take(6).Select(item => item.sequence_prefix).OrderBy(value => value),
                Is.EqualTo(new[] { "A", "B", "C", "D", "E", "F" }));
            Assert.That(assignments[6].block_index, Is.EqualTo(1));
            Assert.That(assignments[6].block_position, Is.Zero);
        }

        [Test]
        public void QuestionnaireCodePersistedOrderAndExpandedProtocolPlanRemainCoherent()
        {
            string dataRoot = Path.Combine(_tempDirectory, "ExperimentData");
            ExperimentDataPathResolver.ResetForTests(dataRoot);
            ExperimentConditionOrderAssignmentStore.OverrideDefaultPathForTests(_assignmentPath);
            ExperimentConditionOrderAssignment assignment =
                ExperimentConditionOrderAssignmentStore.GetOrCreate("U20260806_200000");

            ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.ConfigureSession(
                "U20260806_200000",
                "S20260806_200000",
                assignment.condition_order_ids,
                roundsPerCondition: 2);
            Assert.That(context.QuestionnaireCode, Is.Not.Empty);
            Assert.That(
                QuestionnaireCodeCodec.TryDecodeOrder(
                    context.QuestionnaireCode,
                    out string[] decodedOrder,
                    out _,
                    out string error),
                Is.True,
                error);

            List<Experiment2x2ConditionDefinition> canonical =
                Experiment2x2ConditionDefinition.CreateDefaultPresets(SpawnGenerationMode.RandomBalanced);
            List<Experiment2x2ConditionDefinition> ordered = assignment.condition_order_ids
                .Select(id => canonical.Single(condition => condition.ConditionId == id))
                .ToList();
            List<ExperimentTrialPlanEntry> plan = ExperimentTrialPlanBuilder.Build(ordered, roundsPerCondition: 2);
            string[] executedConditionOrder = plan
                .Select(entry => entry.Condition.ConditionId)
                .Distinct()
                .ToArray();

            Assert.That(context.ConditionOrder, Is.EqualTo(assignment.condition_order_ids));
            Assert.That(decodedOrder, Is.EqualTo(assignment.condition_order_ids));
            Assert.That(executedConditionOrder, Is.EqualTo(assignment.condition_order_ids));
            Assert.That(context.QuestionnaireCode[0].ToString(), Is.EqualTo(assignment.sequence_prefix));
        }

        [Test]
        public void DeletingSessionHistoryAndSessionFoldersDoesNotResetCounterbalanceState()
        {
            ExperimentConditionOrderAssignmentStore.GetOrCreate("U_FIRST");
            string historyPath = Path.Combine(_tempDirectory, "session_id_history.json");
            ExperimentSessionIdHistoryStore.Save(historyPath, new List<ExperimentSessionIdHistoryEntry>());
            string sessionFolder = Path.Combine(_tempDirectory, "U_FIRST", "S_FIRST");
            Directory.CreateDirectory(sessionFolder);

            File.Delete(historyPath);
            Directory.Delete(Path.Combine(_tempDirectory, "U_FIRST"), recursive: true);
            ExperimentConditionOrderAssignment second = ExperimentConditionOrderAssignmentStore.GetOrCreate("U_SECOND");

            Assert.That(File.Exists(_assignmentPath), Is.True);
            Assert.That(second.sequence_prefix, Is.EqualTo("E"));
            Assert.That(second.block_position, Is.EqualTo(1));
        }

        [Test]
        public void ExistingQuestSchemaStateContinuesAtNextBlockWithoutRewritingAssignments()
        {
            const string existingState = @"{
  ""schema_version"": 1,
  ""algorithm"": ""randomized_complete_blocks_v1"",
  ""base_seed"": 409731812,
  ""block_index"": 0,
  ""cursor"": 6,
  ""current_block"": [""B"", ""E"", ""A"", ""F"", ""D"", ""C""],
  ""assignments"": [
    {
      ""participation_id"": ""U20260731_071647"",
      ""sequence_prefix"": ""C"",
      ""condition_order_ids"": [""C10_robot_on_voice_off"", ""C00_robot_off_voice_off"", ""C11_robot_on_voice_on""],
      ""block_index"": 0,
      ""block_position"": 5,
      ""consumed_block_position"": true,
      ""assignment_source"": ""new_participation"",
      ""assigned_at_iso"": ""2026-07-31T07:16:47.1056440Z""
    }
  ]
}";
            File.WriteAllText(_assignmentPath, existingState);

            ExperimentConditionOrderAssignment existing =
                ExperimentConditionOrderAssignmentStore.GetOrCreate("U20260731_071647");
            ExperimentConditionOrderAssignment next =
                ExperimentConditionOrderAssignmentStore.GetOrCreate("U20260806_210000");

            Assert.That(existing.sequence_prefix, Is.EqualTo("C"));
            Assert.That(existing.block_index, Is.Zero);
            Assert.That(next.block_index, Is.EqualTo(1));
            Assert.That(next.block_position, Is.Zero);
            Assert.That(File.ReadAllText(_assignmentPath), Does.Contain("U20260731_071647"));
        }

        [Test]
        public void CorruptPrimaryAndBackupFailClosedInsteadOfFallingBackToSequenceA()
        {
            File.WriteAllText(_assignmentPath, "{not-json");
            File.WriteAllText(_assignmentPath + ".bak", "{also-not-json");

            InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
                ExperimentConditionOrderAssignmentStore.GetOrCreate("U_MUST_NOT_DEFAULT"));

            Assert.That(exception.Message, Does.Contain("state is unreadable"));
            Assert.That(File.ReadAllText(_assignmentPath), Is.EqualTo("{not-json"));
        }
    }
}
