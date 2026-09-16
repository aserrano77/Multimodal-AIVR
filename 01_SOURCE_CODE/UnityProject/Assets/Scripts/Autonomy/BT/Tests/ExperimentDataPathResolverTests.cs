using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public sealed class ExperimentDataPathResolverTests
    {
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "p45f03_resolver_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            ExperimentDataPathResolver.ResetForTests(_tempRoot);
        }

        [TearDown]
        public void TearDown()
        {
            ExperimentDataPathResolver.EndCurrentSession();
            ExperimentDataPathResolver.ResetForTests(null);
            if (!string.IsNullOrWhiteSpace(_tempRoot) && Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }

        [Test]
        public void RegisterSessionContextFromPayload_Is_Idempotent_For_Active_Session()
        {
            ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.ConfigureSession(
                string.Empty,
                string.Empty,
                FinalConditions(),
                roundsPerCondition: 2);

            for (int i = 0; i < 1000; i++)
            {
                var payload = new Dictionary<string, object>
                {
                    ["participant_id"] = "U19990101_010101",
                    ["session_id"] = "S19990101_010101",
                    ["rounds_per_condition"] = 2
                };
                ExperimentDataPathResolver.RegisterSessionContextFromPayload(payload);

                Assert.That(payload["participant_id"], Is.EqualTo(context.ParticipantId));
                Assert.That(payload["session_id"], Is.EqualTo(context.SessionId));
            }

            Assert.That(Directory.GetDirectories(_tempRoot, "U*"), Has.Length.EqualTo(1));
            Assert.That(Directory.GetDirectories(Path.Combine(_tempRoot, context.ParticipantId), "S*"), Has.Length.EqualTo(1));
            Assert.That(ExperimentDataPathResolver.CurrentParticipantId, Is.EqualTo(context.ParticipantId));
            Assert.That(ExperimentDataPathResolver.CurrentSessionId, Is.EqualTo(context.SessionId));
        }

        [Test]
        public void ConfigureSession_Uses_Collision_Suffix_Only_During_Initial_Creation()
        {
            string baseParticipant = "U20260627_120000";
            string baseSession = "S20260627_120000";
            Directory.CreateDirectory(Path.Combine(_tempRoot, baseParticipant));

            ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.ConfigureSession(
                baseParticipant,
                baseSession,
                FinalConditions(),
                roundsPerCondition: 2);

            Assert.That(context.ParticipantId, Is.EqualTo("U20260627_120000_02"));
            Assert.That(context.SessionId, Is.EqualTo(baseSession));

            for (int i = 0; i < 1000; i++)
            {
                ExperimentDataPathResolver.RegisterSessionContextFromPayload(new Dictionary<string, object>
                {
                    ["participant_id"] = baseParticipant,
                    ["session_id"] = baseSession
                });
            }

            Assert.That(Directory.Exists(Path.Combine(_tempRoot, "U20260627_120000_03")), Is.False);
            Assert.That(Directory.GetDirectories(_tempRoot, "U20260627_120000*"), Has.Length.EqualTo(2));
            Assert.That(Directory.GetDirectories(Path.Combine(_tempRoot, context.ParticipantId), "S*"), Has.Length.EqualTo(1));
        }

        [Test]
        public void ConfigureSessionPersistsCodeSchemeAndAuthoritativeOrderInExportMetadata()
        {
            ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.ConfigureSession(
                "U20260718_100000",
                "S20260718_100000",
                FinalConditions(),
                roundsPerCondition: 2);

            string exportInfo = File.ReadAllText(Path.Combine(context.SessionRoot, "session_export_info.json"));
            Assert.That(context.QuestionnaireCodeScheme, Is.EqualTo(QuestionnaireCodeCodec.Scheme));
            Assert.That(QuestionnaireCodeCodec.TryDecodeOrder(context.QuestionnaireCode, out string[] decoded, out _, out string error), Is.True, error);
            Assert.That(decoded, Is.EqualTo(context.ConditionOrder));
            Assert.That(exportInfo, Does.Contain("\"questionnaire_code\":\"" + context.QuestionnaireCode + "\""));
            Assert.That(exportInfo, Does.Contain("\"questionnaire_code_scheme\":\"order_checksum_v1\""));
            Assert.That(exportInfo, Does.Contain("\"condition_order_ids\""));
            Assert.That(exportInfo, Does.Contain("\"participant_id\":\"" + context.ParticipantId + "\""));
            Assert.That(exportInfo, Does.Contain("\"session_id\":\"" + context.SessionId + "\""));
        }

        [Test]
        public void ConfigureSessionRejectsInvalidAuthoritativeOrderWithoutCreatingIdentity()
        {
            string[] invalidOrder =
            {
                ExperimentCompensatedConditionOrder.C00,
                ExperimentCompensatedConditionOrder.C00,
                ExperimentCompensatedConditionOrder.C11
            };

            ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.ConfigureSession(
                "U_INVALID",
                "S_INVALID",
                invalidOrder,
                roundsPerCondition: 2);

            Assert.That(context.SessionRoot, Is.Null.Or.Empty);
            Assert.That(ExperimentDataPathResolver.HasActiveSession, Is.False);
            Assert.That(File.Exists(ExperimentSessionIdHistoryStore.DefaultPath), Is.False);
        }

        [Test]
        public void LateEvents_After_EndCurrentSession_Do_Not_Create_New_Participant()
        {
            ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.ConfigureSession(
                string.Empty,
                string.Empty,
                FinalConditions(),
                roundsPerCondition: 2);

            ExperimentDataPathResolver.EndCurrentSession();
            Assert.That(ExperimentDataPathResolver.IsSessionClosed, Is.True);

            for (int i = 0; i < 10; i++)
            {
                var payload = new Dictionary<string, object>
                {
                    ["participant_id"] = "U19990101_010101",
                    ["session_id"] = "S19990101_010101",
                    ["event_index"] = i
                };
                ExperimentDataPathResolver.RegisterSessionContextFromPayload(payload);

                Assert.That(payload["participant_id"], Is.EqualTo(context.ParticipantId));
                Assert.That(payload["session_id"], Is.EqualTo(context.SessionId));
            }

            LogAssert.Expect(LogType.Error, new Regex("active_session_must_be_closed_and_prepared"));
            ExperimentDataPathResolver.SessionContext repeated = ExperimentDataPathResolver.ConfigureSession(
                string.Empty,
                string.Empty,
                FinalConditions(),
                roundsPerCondition: 2);

            Assert.That(repeated.SessionRoot, Is.Null.Or.Empty);
            Assert.That(Directory.GetDirectories(_tempRoot, "U*"), Has.Length.EqualTo(1));

            ExperimentDataPathResolver.PrepareForNewSessionStart();
            Assert.That(ExperimentDataPathResolver.HasActiveSession, Is.False);
        }

        [Test]
        public void ConsecutiveSessionsKeepExclusiveIdentityAndExportDirectories()
        {
            string[][] orders =
            {
                new[] { ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C10, ExperimentCompensatedConditionOrder.C11 },
                new[] { ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C11, ExperimentCompensatedConditionOrder.C10 },
                new[] { ExperimentCompensatedConditionOrder.C10, ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C11 },
                new[] { ExperimentCompensatedConditionOrder.C10, ExperimentCompensatedConditionOrder.C11, ExperimentCompensatedConditionOrder.C00 }
            };
            var contexts = new List<ExperimentDataPathResolver.SessionContext>();
            for (int i = 0; i < orders.Length; i++)
            {
                ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.ConfigureSession(
                    $"U20260718_12000{i}",
                    $"S20260718_12000{i}",
                    orders[i],
                    roundsPerCondition: 2);
                contexts.Add(context);
                Assert.That(context.Identity, Is.Not.Null);
                Assert.That(ExperimentDataPathResolver.TryValidateCurrentIdentity(context.Identity, out string identityError), Is.True, identityError);
                Assert.That(File.Exists(Path.Combine(context.SessionRoot, "session_export_info.json")), Is.True);
                ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.CompletedStatus);
                ExperimentDataPathResolver.PrepareForNewSessionStart();
            }

            Assert.That(contexts.Select(context => context.SessionRoot).Distinct().Count(), Is.EqualTo(4));
            foreach (ExperimentDataPathResolver.SessionContext context in contexts)
            {
                string export = File.ReadAllText(Path.Combine(context.SessionRoot, "session_export_info.json"));
                Assert.That(export, Does.Contain("\"participant_id\":\"" + context.ParticipantId + "\""));
                Assert.That(export, Does.Contain("\"session_id\":\"" + context.SessionId + "\""));
                Assert.That(export, Does.Contain("\"questionnaire_code\":\"" + context.QuestionnaireCode + "\""));
                foreach (ExperimentDataPathResolver.SessionContext other in contexts.Where(other => other.SessionId != context.SessionId))
                {
                    Assert.That(export, Does.Not.Contain(other.SessionId));
                    Assert.That(Directory.GetFiles(context.SessionRoot, "*" + other.SessionId + "*", SearchOption.AllDirectories), Is.Empty);
                }
            }
        }

        [Test]
        public void ExportWriteRejectsDifferentSessionIdentityAndPath()
        {
            ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.ConfigureSession(
                "U20260718_130000",
                "S20260718_130000",
                FinalConditions(),
                roundsPerCondition: 2);
            string foreignRoot = Path.Combine(_tempRoot, "U_FOREIGN", "S_FOREIGN");
            LogAssert.Expect(LogType.Error, new Regex("session_export_write_rejected"));

            bool written = ExperimentDataPathResolver.WriteSessionExportInfo(
                foreignRoot,
                "S_FOREIGN",
                "U_FOREIGN",
                FinalConditions(),
                context.QuestionnaireCode,
                context.QuestionnaireCodeScheme);

            Assert.That(written, Is.False);
            Assert.That(Directory.Exists(foreignRoot), Is.False);
        }

        [Test]
        public void LoggerTransitionWritesGenerationEventOnlyInsideItsBoundSession()
        {
            var loggerObject = new GameObject("SessionBoundLoggerTest");
            try
            {
                ExperimentDataPathResolver.SessionContext first = ExperimentDataPathResolver.ConfigureSession(
                    "U20260718_140000",
                    "S20260718_140000",
                    FinalConditions(),
                    roundsPerCondition: 2);
                TiagoExperimentLogger logger = loggerObject.AddComponent<TiagoExperimentLogger>();
                Assert.That(logger.BindToSession(first, "test_first"), Is.True);
                logger.LogEvent("questionnaire_code_generated", QuestionnaireGenerationPayload(first));
                logger.LogEvent("test_metadata_ready", LoggerMetadata(first));
                Dictionary<string, object> foreignPayload = LoggerMetadata(first);
                foreignPayload["session_id"] = "S_FOREIGN";
                LogAssert.Expect(LogType.Error, new Regex("payload_session_id_mismatch"));
                logger.LogEvent("test_foreign_identity_rejected", foreignPayload);
                string firstEventPath = logger.EventPath;
                logger.StopRun("test_first_snapshot");
                Assert.That(firstEventPath, Does.StartWith(first.SessionRoot));
                Assert.That(File.ReadAllText(firstEventPath), Does.Contain("\"event_type\":\"questionnaire_code_generated\""));
                Assert.That(File.ReadAllText(firstEventPath), Does.Contain("\"session_id\":\"" + first.SessionId + "\""));
                Assert.That(File.ReadAllText(firstEventPath), Does.Not.Contain("S_FOREIGN"));

                ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.CompletedStatus);
                ExperimentDataPathResolver.PrepareForNewSessionStart();
                ExperimentDataPathResolver.SessionContext second = ExperimentDataPathResolver.ConfigureSession(
                    "U20260718_140001",
                    "S20260718_140001",
                    new[]
                    {
                        ExperimentCompensatedConditionOrder.C10,
                        ExperimentCompensatedConditionOrder.C11,
                        ExperimentCompensatedConditionOrder.C00
                    },
                    roundsPerCondition: 2);
                Assert.That(logger.BindToSession(second, "test_second"), Is.True);
                string firstAfterTransition = File.ReadAllText(firstEventPath);
                logger.LogEvent("questionnaire_code_generated", QuestionnaireGenerationPayload(second));
                logger.LogEvent("test_metadata_ready", LoggerMetadata(second));

                Assert.That(logger.EventPath, Does.StartWith(second.SessionRoot));
                Assert.That(logger.EventPath, Is.Not.EqualTo(firstEventPath));
                logger.StopRun("test_second_snapshot");
                string secondEvents = File.ReadAllText(logger.EventPath);
                Assert.That(secondEvents, Does.Contain("\"event_type\":\"questionnaire_code_generated\""));
                Assert.That(secondEvents, Does.Contain("\"session_id\":\"" + second.SessionId + "\""));
                Assert.That(secondEvents, Does.Not.Contain(first.SessionId));
                Assert.That(File.ReadAllText(firstEventPath), Is.EqualTo(firstAfterTransition));
                Assert.That(File.ReadAllText(firstEventPath), Does.Not.Contain(second.SessionId));
                AssertSessionTreeConsistent(first, second.SessionId, second.ParticipantId);
                AssertSessionTreeConsistent(second, first.SessionId, first.ParticipantId);
            }
            finally
            {
                Object.DestroyImmediate(loggerObject);
            }
        }

        [Test]
        public void LoggerStopRunRegeneratesFileIndexAfterTerminalEventsWithExactSizes()
        {
            var loggerObject = new GameObject("FinalFileIndexSizeTest");
            try
            {
                ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.ConfigureSession(
                    "U20260811_180000",
                    "S20260811_180000",
                    FinalConditions(),
                    roundsPerCondition: 2);
                TiagoExperimentLogger logger = loggerObject.AddComponent<TiagoExperimentLogger>();
                Assert.That(logger.BindToSession(context, "test_final_file_index"), Is.True);
                logger.LogEvent("test_metadata_ready", LoggerMetadata(context));
                logger.LogEvent("experiment_session_completed", new Dictionary<string, object>
                {
                    ["session_history_status"] = ExperimentSessionIdHistoryStore.CompletedStatus
                });

                logger.StopRun("test_completed");

                string indexPath = Path.Combine(context.SessionRoot, "file_index.csv");
                Assert.That(File.Exists(indexPath), Is.True);
                foreach (string line in File.ReadAllLines(indexPath).Skip(1).Where(item => !string.IsNullOrWhiteSpace(item)))
                {
                    string[] columns = line.Split(',');
                    string relativePath = columns[0].Trim('"').Replace("\"\"", "\"");
                    long indexedSize = long.Parse(columns[columns.Length - 1]);
                    string actualPath = Path.Combine(context.SessionRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    Assert.That(File.Exists(actualPath), Is.True, relativePath);
                    Assert.That(new FileInfo(actualPath).Length, Is.EqualTo(indexedSize), relativePath);
                }
            }
            finally
            {
                Object.DestroyImmediate(loggerObject);
            }
        }

        [Test]
        public void PrepareForNewSessionClearsTrialAndRoundContextBeforeFirstTrialPrepare()
        {
            var instrumentationObject = new GameObject("PretrialContextResetTest");
            PropertyInfo activeProperty = typeof(ExperimentInstrumentationController).GetProperty(
                "Active",
                BindingFlags.Public | BindingFlags.Static);
            MethodInfo activeSetter = activeProperty?.GetSetMethod(true);
            ExperimentInstrumentationController previousActive =
                activeProperty?.GetValue(null) as ExperimentInstrumentationController;
            try
            {
                ExperimentInstrumentationController instrumentation =
                    instrumentationObject.AddComponent<ExperimentInstrumentationController>();
                activeSetter?.Invoke(null, new object[] { instrumentation });
                instrumentation.SetRunMode(ExperimentRunMode.Orchestrated2x2);
                instrumentation.ApplyOrchestratedContext(new ExperimentRuntimeContext(
                    "U20260811_170000",
                    "S20260811_170000",
                    "trial_002",
                    2,
                    ExperimentCompensatedConditionOrder.C00,
                    "Robot OFF + Voice OFF",
                    0,
                    robotEnabled: false,
                    voiceEnabled: false,
                    RobotAssistanceMode.Disabled,
                    SpawnGenerationMode.RandomBalanced,
                    "AssistedRoundPickAndPlace",
                    "MultimodalSimulated",
                    "S20260811_170000_trial_002_round_02"));

                SetPrivateField(instrumentation, "_lastTerminalTrialId", "trial_002");
                SetPrivateField(instrumentation, "_lastTerminalTrialIndex", 2);
                SetPrivateField(instrumentation, "_lastTerminalEventType", "experiment_trial_failed");
                SetPrivateField(instrumentation, "_lastTerminalState", "manually_ended_incomplete");

                instrumentation.PrepareForNewSession();
                ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.ConfigureSession(
                    "U20260811_180001",
                    "S20260811_180001",
                    FinalConditions(),
                    roundsPerCondition: 2);
                Assert.That(instrumentation.ApplyAuthoritativeSessionIdentity(context), Is.True);
                var payload = new Dictionary<string, object>();

                ExperimentInstrumentationController.EnrichPayloadWithActiveContext(payload);

                Assert.That(payload["participant_id"], Is.EqualTo(context.ParticipantId));
                Assert.That(payload["session_id"], Is.EqualTo(context.SessionId));
                Assert.That(payload["trial_id"], Is.EqualTo(string.Empty));
                Assert.That(payload["trial_index"], Is.EqualTo(0));
                Assert.That(payload["round_id"], Is.EqualTo(string.Empty));
                Assert.That(payload["round_index"], Is.EqualTo(0));
                Assert.That(payload["condition_id"], Is.EqualTo(string.Empty));
                Assert.That(payload["condition_order_index"], Is.EqualTo(-1));
                Assert.That(instrumentation.LastTerminalTrialId, Is.EqualTo(string.Empty));
                Assert.That(instrumentation.LastTerminalTrialIndex, Is.EqualTo(0));
                Assert.That(instrumentation.LastTerminalEventType, Is.EqualTo(string.Empty));
                Assert.That(instrumentation.LastTerminalState, Is.EqualTo(string.Empty));
            }
            finally
            {
                activeSetter?.Invoke(null, new object[] { previousActive });
                Object.DestroyImmediate(instrumentationObject);
            }
        }

        [Test]
        public void OrchestratorPreSessionResetClearsCurrentConditionBeforeLoggerBinding()
        {
            var root = new GameObject("PreSessionOrchestratorResetTest");
            try
            {
                ExperimentConditionConfigBehaviour condition = root.AddComponent<ExperimentConditionConfigBehaviour>();
                condition.SetRunMode(ExperimentRunMode.Orchestrated2x2);
                condition.ApplyConditionFromOrchestrator(
                    true,
                    true,
                    "Robot ON + Voice ON",
                    RobotAssistanceMode.AssistedSelection,
                    "test_setup");
                System.Type orchestratorType = System.AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("Autonomy.UnityIntegration.ExperimentSessionOrchestrator"))
                    .FirstOrDefault(type => type != null);
                Assert.That(orchestratorType, Is.Not.Null);
                Component orchestrator = root.AddComponent(orchestratorType);
                SetPrivateField(orchestrator, "_conditionConfig", condition);
                SetPrivateField(orchestrator, "_currentTrialId", "trial_003");
                SetPrivateField(orchestrator, "_currentTrialIndex", 3);
                SetPrivateField(orchestrator, "_currentCondition", new Experiment2x2ConditionDefinition(
                    ExperimentCompensatedConditionOrder.C11,
                    "Robot ON + Voice ON",
                    true,
                    true,
                    RobotAssistanceMode.AssistedSelection,
                    SpawnGenerationMode.RandomBalanced));

                MethodInfo reset = orchestratorType.GetMethod(
                    "ResetPreSessionDiagnosticContext",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(reset, Is.Not.Null);
                reset.Invoke(orchestrator, null);

                Assert.That(orchestratorType.GetProperty("CurrentTrialId")?.GetValue(orchestrator), Is.EqualTo(string.Empty));
                Assert.That(orchestratorType.GetProperty("CurrentConditionId")?.GetValue(orchestrator), Is.EqualTo("uninitialized"));
                Assert.That(condition.CurrentCondition.RobotEnabled, Is.False);
                Assert.That(condition.CurrentCondition.VoiceEnabled, Is.False);
                Assert.That(condition.CurrentCondition.AssistanceMode, Is.EqualTo(RobotAssistanceMode.Disabled));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SavedExitResumeRebindsLiteralIdentityAndDoesNotRegenerateCodeEvent()
        {
            var loggerObject = new GameObject("SavedExitResumeLoggerTest");
            try
            {
                ExperimentDataPathResolver.SessionContext original = ExperimentDataPathResolver.ConfigureSession(
                    "U20260718_150000",
                    "S20260718_150000",
                    FinalConditions(),
                    roundsPerCondition: 2);
                TiagoExperimentLogger logger = loggerObject.AddComponent<TiagoExperimentLogger>();
                Assert.That(logger.BindToSession(original, "test_original"), Is.True);
                logger.LogEvent("questionnaire_code_generated", QuestionnaireGenerationPayload(original));
                logger.LogEvent("test_metadata_ready", LoggerMetadata(original));
                logger.StopRun("test_saved_exit_close");
                Assert.That(ExperimentSessionIdHistoryStore.UpdateSavedExitCheckpoint(
                    original.SessionId,
                    original.ConditionOrder[0],
                    1,
                    1,
                    string.Join(",", original.ConditionOrder),
                    "condition_start",
                    original.ConditionOrder,
                    0), Is.True);
                ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.SavedExitStatus);
                ExperimentDataPathResolver.PrepareForNewSessionStart();

                ExperimentDataPathResolver.SessionContext resumed = ExperimentDataPathResolver.ConfigureExistingSessionForResume(
                    original.ParticipantId,
                    original.SessionId,
                    original.QuestionnaireCode,
                    original.ConditionOrder,
                    roundsPerCondition: 2,
                    questionnaireCodeScheme: original.QuestionnaireCodeScheme);
                Assert.That(logger.BindToSession(resumed, "test_resume"), Is.True);
                logger.LogEvent("test_resume_metadata_ready", LoggerMetadata(resumed));
                logger.StopRun("test_resume_snapshot");

                Assert.That(resumed.ParticipantId, Is.EqualTo(original.ParticipantId));
                Assert.That(resumed.SessionId, Is.EqualTo(original.SessionId));
                Assert.That(resumed.QuestionnaireCode, Is.EqualTo(original.QuestionnaireCode));
                Assert.That(resumed.QuestionnaireCodeScheme, Is.EqualTo(original.QuestionnaireCodeScheme));
                Assert.That(resumed.ConditionOrder, Is.EqualTo(original.ConditionOrder));
                Assert.That(resumed.SessionRoot, Is.EqualTo(original.SessionRoot));
                string allEvents = string.Join(
                    "\n",
                    Directory.GetFiles(original.SessionRoot, "*events.jsonl", SearchOption.TopDirectoryOnly)
                        .Select(File.ReadAllText));
                Assert.That(CountOccurrences(allEvents, "\"event_type\":\"questionnaire_code_generated\""), Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(loggerObject);
            }
        }

        [Test]
        public void SanitizePayloadForCurrentSession_Replaces_Legacy_P001_Without_Leaking_Value()
        {
            ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.ConfigureSession(
                "U20260627_201131",
                "S20260627_201131",
                FinalConditions(),
                roundsPerCondition: 2);

            var payload = new Dictionary<string, object>
            {
                ["participant_id"] = "P001",
                ["session_id"] = "",
                ["event_type"] = "experiment_orchestrator_round_completion_subscription_added"
            };

            ExperimentDataPathResolver.SanitizePayloadForCurrentSession(payload, "test_legacy_payload");

            Assert.That(payload["participant_id"], Is.EqualTo(context.ParticipantId));
            Assert.That(payload["session_id"], Is.EqualTo(context.SessionId));
            Assert.That(payload["p45f_participant_id_repaired"], Is.EqualTo(true));
            Assert.That(payload["p45f_session_id_repaired"], Is.EqualTo(true));
            foreach (object value in payload.Values)
            {
                Assert.That(value?.ToString() ?? string.Empty, Does.Not.Contain("P001"));
            }
        }

        [Test]
        public void ProvisionalInstrumentationIdentityIsReplacedByAuthoritativeSessionBeforeEmission()
        {
            var instrumentationObject = new GameObject("AuthoritativeInstrumentationIdentityTest");
            try
            {
                ExperimentInstrumentationController instrumentation =
                    instrumentationObject.AddComponent<ExperimentInstrumentationController>();
                instrumentation.SetRunMode(ExperimentRunMode.Orchestrated2x2);
                SetPrivateField(instrumentation, "_participantId", "U_PROVISIONAL");
                SetPrivateField(instrumentation, "_sessionId", "S_PROVISIONAL");

                ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.ConfigureSession(
                    "U20260718_160000",
                    "S20260718_160000",
                    new[]
                    {
                        ExperimentCompensatedConditionOrder.C10,
                        ExperimentCompensatedConditionOrder.C11,
                        ExperimentCompensatedConditionOrder.C00
                    },
                    roundsPerCondition: 2);

                Assert.That(instrumentation.ApplyAuthoritativeSessionIdentity(context), Is.True);
                Assert.That(GetPrivateField<string>(instrumentation, "_participantId"), Is.EqualTo(context.ParticipantId));
                Assert.That(GetPrivateField<string>(instrumentation, "_sessionId"), Is.EqualTo(context.SessionId));

                var payload = new Dictionary<string, object>
                {
                    ["participant_id"] = "U_PROVISIONAL",
                    ["session_id"] = "S_PROVISIONAL",
                    ["session_root"] = "provisional/root"
                };
                Assert.That(
                    ExperimentDataPathResolver.TryStampPayloadWithCurrentSessionIdentity(payload, out string error),
                    Is.True,
                    error);
                Assert.That(payload["participant_id"], Is.EqualTo(context.ParticipantId));
                Assert.That(payload["session_id"], Is.EqualTo(context.SessionId));
                Assert.That(payload["session_root"], Is.EqualTo(context.SessionRoot));
                Assert.That(payload["questionnaire_code"], Is.EqualTo(context.QuestionnaireCode));
                Assert.That(payload["questionnaire_code_scheme"], Is.EqualTo(context.QuestionnaireCodeScheme));
                Assert.That((string[])payload["condition_order_ids"], Is.EqualTo(context.ConditionOrder));
            }
            finally
            {
                Object.DestroyImmediate(instrumentationObject);
            }
        }

        [Test]
        public void UiAndInstructionEventsWithProvisionalIdsAreStampedAndWrittenToAuthoritativeJsonl()
        {
            var loggerObject = new GameObject("AuthoritativeUiEventLoggerTest");
            TiagoExperimentLogger previousActiveLogger = TiagoExperimentLogger.Active;
            try
            {
                SetActiveLoggerForTests(null);
                string[] orderD =
                {
                    ExperimentCompensatedConditionOrder.C10,
                    ExperimentCompensatedConditionOrder.C11,
                    ExperimentCompensatedConditionOrder.C00
                };
                ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.ConfigureSession(
                    "U20260718_161000",
                    "S20260718_161000",
                    orderD,
                    roundsPerCondition: 2);
                TiagoExperimentLogger logger = loggerObject.AddComponent<TiagoExperimentLogger>();
                SetActiveLoggerForTests(logger);
                Assert.That(TiagoExperimentLogger.Active, Is.SameAs(logger));
                Assert.That(logger.BindToSession(context, "test_authoritative_ui_events"), Is.True);

                string[] affectedEvents =
                {
                    "experiment_instruction_narration_available",
                    "experiment_instruction_narration_started",
                    "experiment_instruction_canvas_rendered",
                    "runtime_ui_rays_disabled_on_quest",
                    "xr_ui_runtime_ray_disabled",
                    "ui_native_xri_interactor_audit",
                    "stable_ui_pointer_direct_ray_target_acquired",
                    "stable_ui_pointer_click_attempt",
                    "ui_click_result",
                    "p46i_ui_hit_marker_visible_changed",
                    "stable_ui_pointer_visual_and_click_use_direct_ray",
                    "stable_ui_pointer_visual_uses_same_ray_as_click",
                    "stable_ui_pointer_direct_ray_target_lost",
                    "stable_ui_pointer_click_sent",
                    "global_instructions_front_canvas_pointer_disabled",
                    "stable_ui_pointer_direct_ray_click_sent",
                    "stable_ui_pointer_side_click_attempt",
                    "ui_click_attempt",
                    "ui_canvas_raycaster_checked",
                    "experiment_instruction_narration_stopped",
                    "p46i_ui_hit_marker_host_bound",
                    "experiment_runtime_ui_ray_bootstrap_started",
                    "experiment_runtime_user_facing_label_resolved",
                    "experiment_runtime_ui_ray_visual_consolidated",
                    "experiment_runtime_ui_ray_rebound",
                    "stable_ui_pointer_side_click_sent",
                    "experiment_runtime_ui_ray_visual_audit",
                    "xr_ui_ray_owner_selected",
                    "ui_runtime_ray_creation_skipped",
                    "experiment_runtime_ui_ray_bootstrap_result"
                };

                foreach (string eventType in affectedEvents)
                {
                    Dictionary<string, object> payload = LoggerMetadata(context);
                    payload["participant_id"] = "U_PROVISIONAL";
                    payload["session_id"] = "S_PROVISIONAL";
                    payload["session_root"] = "provisional/root";
                    Assert.That(TiagoExperimentTelemetry.LogEvent(eventType, payload), Is.True, eventType);
                    Assert.That(payload["participant_id"], Is.EqualTo(context.ParticipantId), eventType);
                    Assert.That(payload["session_id"], Is.EqualTo(context.SessionId), eventType);
                    Assert.That(payload["session_root"], Is.EqualTo(context.SessionRoot), eventType);
                }

                logger.StopRun("test_authoritative_ui_events_complete");
                string eventsJsonl = File.ReadAllText(logger.EventPath);
                Assert.That(logger.EventPath, Does.StartWith(context.SessionRoot));
                foreach (string eventType in affectedEvents)
                {
                    Assert.That(eventsJsonl, Does.Contain("\"event_type\":\"" + eventType + "\""), eventType);
                }

                Assert.That(eventsJsonl, Does.Contain("\"participant_id\":\"" + context.ParticipantId + "\""));
                Assert.That(eventsJsonl, Does.Contain("\"session_id\":\"" + context.SessionId + "\""));
                Assert.That(eventsJsonl, Does.Not.Contain("U_PROVISIONAL"));
                Assert.That(eventsJsonl, Does.Not.Contain("S_PROVISIONAL"));
                Assert.That(eventsJsonl, Does.Not.Contain("provisional/root"));
            }
            finally
            {
                Object.DestroyImmediate(loggerObject);
                if (previousActiveLogger != null)
                {
                    SetActiveLoggerForTests(previousActiveLogger);
                }
            }
        }

        [TestCaseSource(nameof(AllQuestionnaireOrders))]
        public void ManifestExportHistoryCodeAndTrialEventsUseOneAuthoritativeOrder(
            char expectedPrefix,
            string[] authoritativeOrder)
        {
            var loggerObject = new GameObject("ManifestAuthoritativeOrderTest_" + expectedPrefix);
            try
            {
                ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.ConfigureSession(
                    "U20260718_17" + expectedPrefix + "000",
                    "S20260718_17" + expectedPrefix + "000",
                    authoritativeOrder,
                    roundsPerCondition: 2);
                TiagoExperimentLogger logger = loggerObject.AddComponent<TiagoExperimentLogger>();
                Assert.That(logger.BindToSession(context, "test_manifest_order_" + expectedPrefix), Is.True);
                logger.LogEvent("test_manifest_metadata_ready", LoggerMetadata(context));

                for (int conditionOrderIndex = 0; conditionOrderIndex < authoritativeOrder.Length; conditionOrderIndex++)
                {
                    Dictionary<string, object> trialPayload = LoggerMetadata(context);
                    trialPayload["trial_id"] = "trial_" + (conditionOrderIndex + 1).ToString("000");
                    trialPayload["trial_index"] = conditionOrderIndex + 1;
                    trialPayload["condition_id"] = authoritativeOrder[conditionOrderIndex];
                    trialPayload["condition_order_index"] = conditionOrderIndex;
                    logger.LogEvent("experiment_trial_prepare_started", trialPayload);
                }

                logger.StopRun("test_manifest_order_complete");
                string manifest = File.ReadAllText(logger.ManifestPath);
                string eventsJsonl = File.ReadAllText(logger.EventPath);
                string exportInfo = File.ReadAllText(Path.Combine(context.SessionRoot, "session_export_info.json"));
                string expectedOrderJson = JsonArray(authoritativeOrder);
                foreach (string field in new[] { "condition_order_ids", "condition_order", "conditions", "active_condition_plan" })
                {
                    Assert.That(manifest, Does.Contain("\"" + field + "\":" + expectedOrderJson), field);
                }

                Assert.That(context.QuestionnaireCode[0], Is.EqualTo(expectedPrefix));
                Assert.That(QuestionnaireCodeCodec.TryDecodeOrder(
                    context.QuestionnaireCode,
                    out string[] decodedOrder,
                    out _,
                    out string codeError), Is.True, codeError);
                Assert.That(decodedOrder, Is.EqualTo(authoritativeOrder));
                Assert.That(exportInfo, Does.Contain("\"condition_order_ids\":" + expectedOrderJson));
                Assert.That(manifest, Does.Contain("\"questionnaire_code\":\"" + context.QuestionnaireCode + "\""));

                ExperimentSessionIdHistoryEntry history = ExperimentSessionIdHistoryStore.LoadRecent(20)
                    .Single(entry => entry.session_id == context.SessionId);
                Assert.That(history.condition_order_ids, Is.EqualTo(authoritativeOrder));
                Assert.That(history.questionnaire_code, Is.EqualTo(context.QuestionnaireCode));
                for (int conditionOrderIndex = 0; conditionOrderIndex < authoritativeOrder.Length; conditionOrderIndex++)
                {
                    Assert.That(eventsJsonl, Does.Contain(
                        "\"condition_id\":\"" + authoritativeOrder[conditionOrderIndex] + "\",\"condition_order_index\":" + conditionOrderIndex));
                }

                Dictionary<string, object> manifestPayload = ManifestPayload(context);
                var validTrial = new Dictionary<string, object>
                {
                    ["trial_index"] = 1,
                    ["condition_id"] = authoritativeOrder[0],
                    ["condition_order_index"] = 0
                };
                Assert.That(TiagoExperimentLogger.TryValidateManifestOrderForDiagnostics(
                    manifestPayload,
                    authoritativeOrder,
                    context.QuestionnaireCode,
                    context.QuestionnaireCodeScheme,
                    validTrial,
                    out string manifestError), Is.True, manifestError);

                if (expectedPrefix == 'D')
                {
                    Assert.That(expectedOrderJson, Is.EqualTo(
                        "[\"C10_robot_on_voice_off\",\"C11_robot_on_voice_on\",\"C00_robot_off_voice_off\"]"));
                }
            }
            finally
            {
                Object.DestroyImmediate(loggerObject);
            }
        }

        [Test]
        public void ManifestValidationRejectsContradictoryOrderAndTrialButAcceptsLegacyCodeLiterally()
        {
            string[] orderD =
            {
                ExperimentCompensatedConditionOrder.C10,
                ExperimentCompensatedConditionOrder.C11,
                ExperimentCompensatedConditionOrder.C00
            };
            string generatedCode = QuestionnaireCodeCodec.GenerateForOrder(orderD);
            Dictionary<string, object> contradictory = ManifestPayload(
                orderD,
                generatedCode,
                QuestionnaireCodeCodec.Scheme);
            contradictory["conditions"] = FinalConditions().ToArray();
            Assert.That(TiagoExperimentLogger.TryValidateManifestOrderForDiagnostics(
                contradictory,
                orderD,
                generatedCode,
                QuestionnaireCodeCodec.Scheme,
                null,
                out string orderError), Is.False);
            Assert.That(orderError, Is.EqualTo("manifest_conditions_mismatch"));

            Dictionary<string, object> coherent = ManifestPayload(
                orderD,
                generatedCode,
                QuestionnaireCodeCodec.Scheme);
            var wrongTrial = new Dictionary<string, object>
            {
                ["trial_index"] = 1,
                ["condition_id"] = ExperimentCompensatedConditionOrder.C00,
                ["condition_order_index"] = 0
            };
            Assert.That(TiagoExperimentLogger.TryValidateManifestOrderForDiagnostics(
                coherent,
                orderD,
                generatedCode,
                QuestionnaireCodeCodec.Scheme,
                wrongTrial,
                out string trialError), Is.False);
            Assert.That(trialError, Is.EqualTo("trial_condition_order_mismatch"));

            const string legacyCode = "01ILOA";
            Dictionary<string, object> legacy = ManifestPayload(orderD, legacyCode, string.Empty);
            Assert.That(TiagoExperimentLogger.TryValidateManifestOrderForDiagnostics(
                legacy,
                orderD,
                legacyCode,
                string.Empty,
                null,
                out string legacyError), Is.True, legacyError);
            Assert.That(legacy["questionnaire_code"], Is.EqualTo(legacyCode));
            Assert.That(legacy["questionnaire_code_scheme"], Is.EqualTo(string.Empty));
        }

        [Test]
        public void ManifestValidation_AcceptsTransitionTelemetryWhenPreviewFieldsDoNotReplaceActiveContext()
        {
            string[] order =
            {
                ExperimentCompensatedConditionOrder.C00,
                ExperimentCompensatedConditionOrder.C10,
                ExperimentCompensatedConditionOrder.C11
            };
            string code = QuestionnaireCodeCodec.GenerateForOrder(order);
            Dictionary<string, object> manifest = ManifestPayload(order, code, QuestionnaireCodeCodec.Scheme);
            var correctedTransitionPayloads = new[]
            {
                new Dictionary<string, object>
                {
                    ["trial_id"] = "trial_002", ["trial_index"] = 2,
                    ["condition_id"] = order[0], ["condition_order_index"] = 0,
                    ["internal_condition_id"] = order[1], ["resolved_condition_order_index"] = 1
                },
                new Dictionary<string, object>
                {
                    ["trial_id"] = "trial_002", ["trial_index"] = 2,
                    ["condition_id"] = order[0], ["condition_order_index"] = 0,
                    ["next_condition_id"] = order[1], ["next_condition_order_index"] = 1
                },
                new Dictionary<string, object>
                {
                    ["trial_id"] = "trial_004", ["trial_index"] = 4,
                    ["condition_id"] = order[1], ["condition_order_index"] = 1,
                    ["internal_condition_id"] = order[2], ["resolved_condition_order_index"] = 2
                },
                new Dictionary<string, object>
                {
                    ["trial_id"] = "trial_004", ["trial_index"] = 4,
                    ["condition_id"] = order[1], ["condition_order_index"] = 1,
                    ["preflight_condition_id"] = order[2]
                },
                new Dictionary<string, object>
                {
                    ["trial_id"] = "trial_004", ["trial_index"] = 4,
                    ["condition_id"] = order[1], ["condition_order_index"] = 1,
                    ["next_condition_id"] = order[2], ["next_condition_order_index"] = 2
                },
                new Dictionary<string, object>
                {
                    ["trial_id"] = "trial_006", ["trial_index"] = 6,
                    ["condition_id"] = order[2], ["condition_order_index"] = 2,
                    ["next_condition_id"] = string.Empty, ["next_condition_order_index"] = -1
                }
            };

            foreach (Dictionary<string, object> payload in correctedTransitionPayloads)
            {
                Assert.That(
                    TiagoExperimentLogger.TryValidateManifestOrderForDiagnostics(
                        manifest,
                        order,
                        code,
                        QuestionnaireCodeCodec.Scheme,
                        payload,
                        out string error),
                    Is.True,
                    error);
            }
        }

        private static IReadOnlyList<string> FinalConditions()
        {
            return new[]
            {
                "C00_robot_off_voice_off",
                "C10_robot_on_voice_off",
                "C11_robot_on_voice_on"
            };
        }

        private static Dictionary<string, object> LoggerMetadata(ExperimentDataPathResolver.SessionContext context)
        {
            return new Dictionary<string, object>
            {
                ["participant_id"] = context.ParticipantId,
                ["session_id"] = context.SessionId,
                ["session_root"] = context.SessionRoot,
                ["experiment_run_mode"] = "Orchestrated2x2",
                ["scene_name"] = "identity_test",
                ["drive_profile"] = "Realistic",
                ["autonomy_policy"] = "Safe"
            };
        }

        private static Dictionary<string, object> QuestionnaireGenerationPayload(
            ExperimentDataPathResolver.SessionContext context)
        {
            QuestionnaireCodeCodec.TryGetPrefixForOrder(context.ConditionOrder, out char prefix);
            return new Dictionary<string, object>
            {
                ["participant_id"] = context.ParticipantId,
                ["session_id"] = context.SessionId,
                ["session_root"] = context.SessionRoot,
                ["questionnaire_code"] = context.QuestionnaireCode,
                ["scheme"] = context.QuestionnaireCodeScheme,
                ["prefix"] = prefix.ToString(),
                ["condition_order_ids"] = context.ConditionOrder
            };
        }

        private static IEnumerable<TestCaseData> AllQuestionnaireOrders()
        {
            yield return new TestCaseData('A', new[] { ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C10, ExperimentCompensatedConditionOrder.C11 });
            yield return new TestCaseData('B', new[] { ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C11, ExperimentCompensatedConditionOrder.C10 });
            yield return new TestCaseData('C', new[] { ExperimentCompensatedConditionOrder.C10, ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C11 });
            yield return new TestCaseData('D', new[] { ExperimentCompensatedConditionOrder.C10, ExperimentCompensatedConditionOrder.C11, ExperimentCompensatedConditionOrder.C00 });
            yield return new TestCaseData('E', new[] { ExperimentCompensatedConditionOrder.C11, ExperimentCompensatedConditionOrder.C00, ExperimentCompensatedConditionOrder.C10 });
            yield return new TestCaseData('F', new[] { ExperimentCompensatedConditionOrder.C11, ExperimentCompensatedConditionOrder.C10, ExperimentCompensatedConditionOrder.C00 });
        }

        private static Dictionary<string, object> ManifestPayload(
            ExperimentDataPathResolver.SessionContext context)
        {
            return ManifestPayload(
                context.ConditionOrder,
                context.QuestionnaireCode,
                context.QuestionnaireCodeScheme);
        }

        private static Dictionary<string, object> ManifestPayload(
            IReadOnlyList<string> order,
            string questionnaireCode,
            string questionnaireCodeScheme)
        {
            string[] copy = order.ToArray();
            return new Dictionary<string, object>
            {
                ["condition_order_ids"] = (string[])copy.Clone(),
                ["condition_order"] = (string[])copy.Clone(),
                ["conditions"] = (string[])copy.Clone(),
                ["active_condition_plan"] = (string[])copy.Clone(),
                ["questionnaire_code"] = questionnaireCode,
                ["questionnaire_code_scheme"] = questionnaireCodeScheme
            };
        }

        private static string JsonArray(IReadOnlyList<string> values)
        {
            return "[\"" + string.Join("\",\"", values) + "\"]";
        }

        private static void SetPrivateField<T>(object target, string fieldName, T value)
        {
            target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(target, value);
        }

        private static T GetPrivateField<T>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName);
            return (T)field.GetValue(target);
        }

        private static void SetActiveLoggerForTests(TiagoExperimentLogger logger)
        {
            FieldInfo field = typeof(TiagoExperimentLogger).GetField(
                "<Active>k__BackingField",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(null, logger);
        }

        private static void AssertSessionTreeConsistent(
            ExperimentDataPathResolver.SessionContext context,
            string foreignSessionId,
            string foreignParticipantId)
        {
            Assert.That(Path.GetFileName(context.SessionRoot), Is.EqualTo(context.SessionId));
            Assert.That(Path.GetFileName(Path.GetDirectoryName(context.SessionRoot)), Is.EqualTo(context.ParticipantId));
            Assert.That(QuestionnaireCodeCodec.TryDecodeOrder(
                context.QuestionnaireCode,
                out string[] decodedOrder,
                out _,
                out string validationError), Is.True, validationError);
            Assert.That(decodedOrder, Is.EqualTo(context.ConditionOrder));

            string exportPath = Path.Combine(context.SessionRoot, "session_export_info.json");
            string manifestPath = Path.Combine(context.SessionRoot, "session_manifest.json");
            Assert.That(File.Exists(exportPath), Is.True);
            Assert.That(File.Exists(manifestPath), Is.True);
            foreach (string path in Directory.GetFiles(context.SessionRoot, "*", SearchOption.AllDirectories))
            {
                string contents = File.ReadAllText(path);
                Assert.That(contents, Does.Not.Contain(foreignSessionId), path);
                Assert.That(contents, Does.Not.Contain(foreignParticipantId), path);
                Assert.That(Path.GetFileName(path), Does.Not.Contain(foreignSessionId), path);
                Assert.That(Path.GetFileName(path), Does.Not.Contain(foreignParticipantId), path);
            }

            string export = File.ReadAllText(exportPath);
            string manifest = File.ReadAllText(manifestPath);
            foreach (string identityValue in new[]
                     {
                         context.ParticipantId,
                         context.SessionId,
                         context.QuestionnaireCode,
                         context.QuestionnaireCodeScheme
                     })
            {
                Assert.That(export, Does.Contain(identityValue));
                Assert.That(manifest, Does.Contain(identityValue));
            }
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = 0;
            while (!string.IsNullOrEmpty(text) &&
                   !string.IsNullOrEmpty(value) &&
                   (index = text.IndexOf(value, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }
    }

    [TestFixture]
    public sealed class QuestLoggingPolicyTests
    {
        [Test]
        public void AndroidPlayer_DisablesLegacyContinuousDiagnostics()
        {
            Assert.That(
                QuestLoggingPolicy.ShouldEmitLegacyContinuousDiagnostics(RuntimePlatform.Android, isEditor: false),
                Is.False);
            Assert.That(
                QuestLoggingPolicy.ShouldEmitLegacyContinuousDiagnostics(RuntimePlatform.Android, isEditor: true),
                Is.True);
            Assert.That(
                QuestLoggingPolicy.ShouldEmitLegacyContinuousDiagnostics(RuntimePlatform.WindowsEditor, isEditor: true),
                Is.True);
        }

        [Test]
        public void AndroidPlayer_RemovesOnlyNormalLogStackTrace()
        {
            const StackTraceLogType configured = StackTraceLogType.ScriptOnly;

            Assert.That(
                QuestLoggingPolicy.ResolveStackTraceLogType(LogType.Log, RuntimePlatform.Android, false, configured),
                Is.EqualTo(StackTraceLogType.None));
            Assert.That(
                QuestLoggingPolicy.ResolveStackTraceLogType(LogType.Warning, RuntimePlatform.Android, false, configured),
                Is.EqualTo(configured));
            Assert.That(
                QuestLoggingPolicy.ResolveStackTraceLogType(LogType.Error, RuntimePlatform.Android, false, configured),
                Is.EqualTo(configured));
            Assert.That(
                QuestLoggingPolicy.ResolveStackTraceLogType(LogType.Exception, RuntimePlatform.Android, false, configured),
                Is.EqualTo(configured));
        }

        [Test]
        public void NonAndroidRuntime_PreservesConfiguredStackTraceType()
        {
            Assert.That(
                QuestLoggingPolicy.ResolveStackTraceLogType(
                    LogType.Log,
                    RuntimePlatform.WindowsPlayer,
                    false,
                    StackTraceLogType.Full),
                Is.EqualTo(StackTraceLogType.Full));
        }
    }
}
