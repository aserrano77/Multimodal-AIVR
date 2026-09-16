using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using UnityEngine;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public class ExperimentSessionIndexWriterTests
    {
        private string _tempDirectory;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), $"session_index_tests_{Guid.NewGuid():N}");
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
        public void FromSummary_Marks_Successful_Trial_Valid_By_Default()
        {
            ExperimentSessionTrialRecord record = ExperimentSessionTrialRecord.FromSummary(
                Summary(success: true, aborted: false, failureReason: string.Empty),
                "summary.csv",
                "summary.jsonl",
                "events.jsonl",
                "samples.csv",
                "manifest.json");

            Assert.That(record.ValidForAnalysis, Is.True);
            Assert.That(record.ExclusionReason, Is.Empty);
            Assert.That(record.RoundId, Is.EqualTo("S001_trial_001_round"));
            Assert.That(record.RoundIndex, Is.EqualTo(1));
            Assert.That(record.RoundIndexWithinCondition, Is.EqualTo(1));
            Assert.That(record.RoundsPerCondition, Is.EqualTo(3));
            Assert.That(record.GlobalRoundIndex, Is.EqualTo(1));
        }

        [Test]
        public void FromSummary_Marks_Aborted_Trial_Invalid_By_Default()
        {
            ExperimentSessionTrialRecord record = ExperimentSessionTrialRecord.FromSummary(
                Summary(success: false, aborted: true, failureReason: "manual_abort_key"),
                "summary.csv",
                "summary.jsonl",
                "events.jsonl",
                "samples.csv",
                "manifest.json");

            Assert.That(record.ValidForAnalysis, Is.False);
            Assert.That(record.ExclusionReason, Is.EqualTo("aborted"));
        }

        [Test]
        public void FromSummary_Preserves_Manual_Terminal_State_Without_Abort()
        {
            ExperimentTrialSummary summary = Summary(success: true, aborted: false, failureReason: string.Empty);
            summary.TerminalState = "manually_ended";

            ExperimentSessionTrialRecord record = ExperimentSessionTrialRecord.FromSummary(
                summary,
                "summary.csv",
                "summary.jsonl",
                "events.jsonl",
                "samples.csv",
                "manifest.json");

            Assert.That(record.Aborted, Is.False);
            Assert.That(record.TerminalState, Is.EqualTo("manually_ended"));
            Assert.That(record.ValidForAnalysis, Is.True);
        }

        [Test]
        public void FromSummary_InvalidBeforeStart_Is_Not_Valid_For_Analysis()
        {
            ExperimentTrialSummary summary = Summary(success: false, aborted: false, failureReason: "robot_pose_reset_failed");
            summary.TerminalState = "invalid_before_start";

            ExperimentSessionTrialRecord record = ExperimentSessionTrialRecord.FromSummary(
                summary,
                "summary.csv",
                "summary.jsonl",
                "events.jsonl",
                "samples.csv",
                "manifest.json");

            Assert.That(record.Aborted, Is.False);
            Assert.That(record.TerminalState, Is.EqualTo("invalid_before_start"));
            Assert.That(record.ValidForAnalysis, Is.False);
            Assert.That(record.ExclusionReason, Is.EqualTo("robot_pose_reset_failed"));
        }

        [Test]
        public void Append_Writes_Header_Once_And_Appends_Multiple_Rows()
        {
            ExperimentSessionIndexWriter.Append(_tempDirectory, Record("trial_001"));
            ExperimentSessionIndexWriter.Append(_tempDirectory, Record("trial_002"));

            string csvPath = Path.Combine(_tempDirectory, ExperimentSessionIndexWriter.BuildCsvFileName(Record("trial_001")));
            string[] lines = File.ReadAllLines(csvPath);

            Assert.That(lines.Length, Is.EqualTo(3));
            Assert.That(lines[0], Is.EqualTo(ExperimentSessionIndexWriter.GetCsvHeader()));
            Assert.That(lines.Count(line => line == ExperimentSessionIndexWriter.GetCsvHeader()), Is.EqualTo(1));
            Assert.That(lines[0], Does.Contain("round_index_within_condition"));
            Assert.That(lines[1], Does.Contain(",1,1,3,1,"));
            Assert.That(File.ReadAllLines(Path.Combine(_tempDirectory, ExperimentSessionIndexWriter.BuildJsonlFileName(Record("trial_001")))).Length, Is.EqualTo(2));
            string jsonl = File.ReadAllText(Path.Combine(_tempDirectory, ExperimentSessionIndexWriter.BuildJsonlFileName(Record("trial_001"))));
            Assert.That(jsonl, Does.Contain("\"round_index_within_condition\":1"));
            Assert.That(jsonl, Does.Contain("\"rounds_per_condition\":3"));
        }

        [Test]
        public void Append_Resolves_Duplicate_TrialId_With_Suffix()
        {
            ExperimentSessionIndexWriter.Append(_tempDirectory, Record("trial_001"));
            ExperimentSessionIndexWriteResult result = ExperimentSessionIndexWriter.Append(_tempDirectory, Record("trial_001"));

            Assert.That(result.Success, Is.True);
            Assert.That(result.DuplicateTrialIdDetected, Is.True);
            Assert.That(result.OriginalTrialId, Is.EqualTo("trial_001"));
            Assert.That(result.WrittenTrialId, Is.EqualTo("trial_001_dup01"));

            string csv = File.ReadAllText(Path.Combine(_tempDirectory, ExperimentSessionIndexWriter.BuildCsvFileName(Record("trial_001"))));
            Assert.That(csv, Does.Contain("trial_001_dup01"));
        }

        [Test]
        public void Append_Uses_Global_Session_Index_Across_Conditions()
        {
            ExperimentSessionTrialRecord first = Record("trial_001");
            first.SessionId = "P001_20260528_204255";
            first.ConditionId = "C00_robot_off_voice_off";
            ExperimentSessionTrialRecord second = Record("trial_002");
            second.SessionId = first.SessionId;
            second.ConditionId = "C10_robot_on_voice_off";

            ExperimentSessionIndexWriter.Append(_tempDirectory, first);
            ExperimentSessionIndexWriter.Append(_tempDirectory, second);

            string csvPath = Path.Combine(_tempDirectory, "P001_20260528_204255__session_trials.csv");
            string csv = File.ReadAllText(csvPath);
            Assert.That(csv, Does.Contain("trial_001"));
            Assert.That(csv, Does.Contain("trial_002"));
            Assert.That(Directory.GetFiles(_tempDirectory, "*__session_trials.csv").Length, Is.EqualTo(1));
        }

        [Test]
        public void BuildCsvFileName_Uses_Global_Session_Prefix()
        {
            ExperimentSessionTrialRecord record = Record("trial_001");
            record.SessionId = "P001_20260528_204255";

            string fileName = ExperimentSessionIndexWriter.BuildCsvFileName(record);

            Assert.That(fileName, Is.EqualTo("P001_20260528_204255__session_trials.csv"));
        }

        [Test]
        public void BuildJsonlFileName_Uses_Same_Prefix_As_Csv()
        {
            ExperimentSessionTrialRecord record = Record("trial_001");
            record.SessionId = "P001_20260528_204255";

            string csv = ExperimentSessionIndexWriter.BuildCsvFileName(record);
            string jsonl = ExperimentSessionIndexWriter.BuildJsonlFileName(record);

            Assert.That(jsonl, Is.EqualTo(csv.Replace(".csv", ".jsonl")));
        }

        [Test]
        public void BuildFileName_Sanitizes_Windows_Problematic_Characters()
        {
            ExperimentSessionTrialRecord record = Record("trial_001");
            record.ParticipantId = "P:001";
            record.SessionId = "session/2026 05 28";
            record.ConditionId = "C/01";
            record.DriveProfile = "Arcade Mode";
            record.AutonomyPolicy = "Fast:Demo";

            string fileName = ExperimentSessionIndexWriter.BuildCsvFileName(record);

            Assert.That(fileName, Does.Not.Contain(":"));
            Assert.That(fileName, Does.Not.Contain("/"));
            Assert.That(fileName, Does.Not.Contain(" "));
            Assert.That(fileName, Does.EndWith("__session_trials.csv"));
        }

        [Test]
        public void BuildFileName_Uses_Stable_Session_Fallback_When_Optional_Metadata_Is_Missing()
        {
            ExperimentSessionTrialRecord record = Record("trial_001");
            record.ParticipantId = "";
            record.SessionId = "20260528_204255";
            record.ConditionId = "";
            record.DriveProfile = "";
            record.AutonomyPolicy = "";

            string fileName = ExperimentSessionIndexWriter.BuildCsvFileName(record);

            Assert.That(fileName, Is.EqualTo("session_20260528_204255__session_trials.csv"));
            Assert.That(fileName, Is.Not.EqualTo(ExperimentSessionIndexWriter.LegacyCsvFileName));
        }

        [Test]
        public void Append_Returns_Descriptive_FileNames()
        {
            ExperimentSessionTrialRecord record = Record("trial_001");
            record.SessionId = "P001_20260528_204255";

            ExperimentSessionIndexWriteResult result = ExperimentSessionIndexWriter.Append(_tempDirectory, record);

            Assert.That(result.Success, Is.True);
            Assert.That(result.CsvFileName, Is.EqualTo("P001_20260528_204255__session_trials.csv"));
            Assert.That(result.JsonlFileName, Is.EqualTo("P001_20260528_204255__session_trials.jsonl"));
            Assert.That(File.Exists(result.CsvPath), Is.True);
            Assert.That(File.Exists(result.JsonlPath), Is.True);
        }

        [Test]
        public void CompletedTrial_WritesSummaries_And_DescriptiveSessionIndex()
        {
            var gameObject = new GameObject("experiment_instrumentation_test");
            try
            {
                ExperimentInstrumentationController controller = gameObject.AddComponent<ExperimentInstrumentationController>();
                SetPrivateField(controller, "_fallbackSummaryFolder", _tempDirectory);
                SetPrivateField(controller, "_writeCsvSummary", true);
                ExperimentTrialSummary summary = Summary(success: true, aborted: false, failureReason: string.Empty);
                summary.SessionId = "P001_20260528_204255";

                object summaryPaths = InvokePrivate(controller, "WriteSummary", summary);
                InvokePrivate(controller, "WriteSessionIndex", summary, summaryPaths);

                string expectedPrefix = "P001_20260528_204255__t001__C01__trial_summary";
                Assert.That(File.Exists(Path.Combine(_tempDirectory, $"{expectedPrefix}.csv")), Is.True);
                Assert.That(File.Exists(Path.Combine(_tempDirectory, $"{expectedPrefix}.jsonl")), Is.True);
                Assert.That(File.Exists(Path.Combine(_tempDirectory, "P001_20260528_204255__session_trials.csv")), Is.True);
                Assert.That(File.Exists(Path.Combine(_tempDirectory, "P001_20260528_204255__session_trials.jsonl")), Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void CompletedTrial_WritesShortSummaryName_And_CreatesMissingDirectory()
        {
            string nestedDirectory = Path.Combine(_tempDirectory, "missing", "summaries");
            var gameObject = new GameObject("experiment_instrumentation_short_summary_test");
            try
            {
                ExperimentInstrumentationController controller = gameObject.AddComponent<ExperimentInstrumentationController>();
                SetPrivateField(controller, "_fallbackSummaryFolder", nestedDirectory);
                SetPrivateField(controller, "_writeCsvSummary", true);
                ExperimentTrialSummary summary = Summary(success: true, aborted: false, failureReason: string.Empty);
                summary.SessionId = "P001_20260622_184227";
                summary.ConditionId = "C00_robot_off_voice_off";
                summary.TrialId = "trial_001";
                summary.TrialIndex = 1;

                object summaryPaths = InvokePrivate(controller, "WriteSummary", summary);
                string csvPath = (string)summaryPaths.GetType().GetProperty("CsvPath").GetValue(summaryPaths);
                string jsonlPath = (string)summaryPaths.GetType().GetProperty("JsonlPath").GetValue(summaryPaths);

                Assert.That(Directory.Exists(nestedDirectory), Is.True);
                Assert.That(Path.GetFileName(csvPath), Is.EqualTo("P001_20260622_184227__t001__C00__trial_summary.csv"));
                Assert.That(Path.GetFileName(jsonlPath), Is.EqualTo("P001_20260622_184227__t001__C00__trial_summary.jsonl"));
                Assert.That(csvPath.Length, Is.LessThan(240));
                Assert.That(jsonlPath.Length, Is.LessThan(240));
                Assert.That(File.Exists(csvPath), Is.True);
                Assert.That(File.Exists(jsonlPath), Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Manifest_Update_Includes_Session_Index_File_Metadata()
        {
            string manifestPath = Path.Combine(_tempDirectory, "manifest.json");
            File.WriteAllText(manifestPath, "{\"run_id\":\"run_001\"}");
            var result = new ExperimentSessionIndexWriteResult
            {
                CsvFileName = "P001_20260528_204255__session_trials.csv",
                JsonlFileName = "P001_20260528_204255__session_trials.jsonl",
                CsvPath = Path.Combine(_tempDirectory, "P001_20260528_204255__session_trials.csv"),
                JsonlPath = Path.Combine(_tempDirectory, "P001_20260528_204255__session_trials.jsonl")
            };

            typeof(ExperimentInstrumentationController)
                .GetMethod("UpdateManifestWithSessionIndexFiles", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { manifestPath, result });

            string manifest = File.ReadAllText(manifestPath);
            Assert.That(manifest, Does.Contain("\"session_trials_csv_file\""));
            Assert.That(manifest, Does.Contain("\"session_trials_jsonl_file\""));
            Assert.That(manifest, Does.Contain("\"session_trials_csv_path\""));
            Assert.That(manifest, Does.Contain("\"session_trials_jsonl_path\""));
        }

        private static ExperimentSessionTrialRecord Record(string trialId)
        {
            ExperimentTrialSummary summary = Summary(success: true, aborted: false, failureReason: string.Empty);
            summary.TrialId = trialId;
            return ExperimentSessionTrialRecord.FromSummary(
                summary,
                "summary.csv",
                "summary.jsonl",
                "events.jsonl",
                "samples.csv",
                "manifest.json");
        }

        private static ExperimentTrialSummary Summary(bool success, bool aborted, string failureReason)
        {
            return new ExperimentTrialSummary
            {
                RunId = "run_001",
                ParticipantId = "P001",
                SessionId = "S001",
                TrialId = "trial_001",
                TrialIndex = 1,
                ConditionId = "C01",
                ConditionName = "Robot OFF + Voice ON",
                ConditionOrderIndex = 1,
                RobotEnabled = false,
                VoiceEnabled = true,
                AssistanceMode = "Disabled",
                SpawnGenerationMode = "RandomBalanced",
                RoundId = "S001_trial_001_round",
                RoundIndex = 1,
                RoundIndexWithinCondition = 1,
                RoundsPerCondition = 3,
                GlobalRoundIndex = 1,
                TaskId = "PickAndPlace_A_to_ZoneA",
                InputMode = "MultimodalSimulated",
                DriveProfile = "Arcade",
                AutonomyPolicy = "FastDemo",
                SceneName = "scene",
                SelectedTargetId = "STEP19_TestBox_A_01",
                SelectedTargetCategory = "A",
                PlaceTargetId = "ZoneA",
                Success = success,
                Aborted = aborted,
                FailureReason = failureReason,
                TotalDurationSeconds = 10f,
                NavigationToPickDurationSeconds = 2f,
                PickDurationSeconds = 1f,
                NavigationToPlaceDurationSeconds = 3f,
                PlaceDurationSeconds = 1f,
                ErrorCount = success ? 0 : 1,
                TimestampStart = "start",
                TimestampEnd = "end",
                Notes = "notes"
            };
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
    }
}
