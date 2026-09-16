using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autonomy.Domain;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    public sealed class ExperimentSessionIdentity
    {
        private readonly string[] _conditionOrderIds;

        private ExperimentSessionIdentity(
            string participantId,
            string sessionId,
            string questionnaireCode,
            string questionnaireCodeScheme,
            string[] conditionOrderIds,
            string sessionRoot)
        {
            ParticipantId = participantId;
            SessionId = sessionId;
            QuestionnaireCode = questionnaireCode;
            QuestionnaireCodeScheme = questionnaireCodeScheme;
            _conditionOrderIds = (string[])conditionOrderIds.Clone();
            SessionRoot = sessionRoot;
        }

        public string ParticipantId { get; }
        public string SessionId { get; }
        public string QuestionnaireCode { get; }
        public string QuestionnaireCodeScheme { get; }
        public IReadOnlyList<string> ConditionOrderIds => Array.AsReadOnly(_conditionOrderIds);
        public string SessionRoot { get; }

        public string[] CopyConditionOrderIds()
        {
            return (string[])_conditionOrderIds.Clone();
        }

        public bool Matches(ExperimentSessionIdentity other)
        {
            if (other == null ||
                !string.Equals(ParticipantId, other.ParticipantId, StringComparison.Ordinal) ||
                !string.Equals(SessionId, other.SessionId, StringComparison.Ordinal) ||
                !string.Equals(QuestionnaireCode, other.QuestionnaireCode, StringComparison.Ordinal) ||
                !string.Equals(QuestionnaireCodeScheme, other.QuestionnaireCodeScheme, StringComparison.Ordinal) ||
                !PathsMatch(SessionRoot, other.SessionRoot) ||
                _conditionOrderIds.Length != other._conditionOrderIds.Length)
            {
                return false;
            }

            for (int i = 0; i < _conditionOrderIds.Length; i++)
            {
                if (!string.Equals(_conditionOrderIds[i], other._conditionOrderIds[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public static bool TryCreate(
            string participantId,
            string sessionId,
            string questionnaireCode,
            string questionnaireCodeScheme,
            IEnumerable<string> conditionOrderIds,
            string sessionRoot,
            out ExperimentSessionIdentity identity,
            out string errorReason)
        {
            identity = null;
            errorReason = string.Empty;
            string participant = participantId?.Trim() ?? string.Empty;
            string session = sessionId?.Trim() ?? string.Empty;
            string code = questionnaireCode ?? string.Empty;
            string scheme = questionnaireCodeScheme ?? string.Empty;
            string root = sessionRoot?.Trim() ?? string.Empty;
            string[] order = ToIdentityOrder(conditionOrderIds);
            if (string.IsNullOrWhiteSpace(participant) || string.IsNullOrWhiteSpace(session))
            {
                errorReason = "participant_or_session_id_missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                errorReason = "questionnaire_code_missing";
                return false;
            }

            if (!ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(order))
            {
                errorReason = "condition_order_invalid";
                return false;
            }

            if (string.IsNullOrWhiteSpace(root) ||
                !string.Equals(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), session, StringComparison.Ordinal) ||
                !string.Equals(Path.GetFileName(Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))), participant, StringComparison.Ordinal))
            {
                errorReason = "session_root_identity_mismatch";
                return false;
            }

            if (string.Equals(scheme, QuestionnaireCodeCodec.Scheme, StringComparison.Ordinal))
            {
                if (!QuestionnaireCodeCodec.TryDecodeOrder(code, out string[] decodedOrder, out _, out string validationError) ||
                    !OrdersMatch(decodedOrder, order))
                {
                    errorReason = "questionnaire_code_order_mismatch:" + validationError;
                    return false;
                }
            }
            else if (!string.IsNullOrWhiteSpace(scheme))
            {
                errorReason = "questionnaire_code_scheme_unsupported";
                return false;
            }

            identity = new ExperimentSessionIdentity(participant, session, code, scheme, order, root);
            return true;
        }

        public static bool PathsMatch(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                string normalizedLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string[] ToIdentityOrder(IEnumerable<string> conditionOrderIds)
        {
            if (conditionOrderIds == null)
            {
                return Array.Empty<string>();
            }

            var values = new List<string>();
            foreach (string conditionId in conditionOrderIds)
            {
                values.Add(conditionId?.Trim() ?? string.Empty);
            }

            return values.ToArray();
        }

        private static bool OrdersMatch(IReadOnlyList<string> first, IReadOnlyList<string> second)
        {
            if (first == null || second == null || first.Count != second.Count)
            {
                return false;
            }

            for (int i = 0; i < first.Count; i++)
            {
                if (!string.Equals(first[i], second[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public static class ExperimentDataPathResolver
    {
        private const string ExperimentDataFolderName = "ExperimentData";
        private const string P45FTag = "P45F";
        private const string FallbackParticipantId = "PILOT_UNSET";
        private static string s_cachedDataRoot;
        private static bool s_loggedDataRoot;
        private static bool s_inTelemetryLog;
        private static SessionContext s_currentSession;
        private static bool s_sessionClosed;

#if UNITY_INCLUDE_TESTS || UNITY_EDITOR
        public static void ResetForTests(string dataRootOverride)
        {
            s_cachedDataRoot = dataRootOverride;
            s_loggedDataRoot = false;
            s_inTelemetryLog = false;
            s_currentSession = default;
            s_sessionClosed = false;
            ExperimentSessionIdHistoryStore.OverrideDefaultPathForTests(
                string.IsNullOrWhiteSpace(dataRootOverride)
                    ? null
                    : Path.Combine(dataRootOverride, "session_id_history.json"));
        }
#endif

        public static string ResolveDataRoot()
        {
            if (!string.IsNullOrWhiteSpace(s_cachedDataRoot))
            {
                Directory.CreateDirectory(s_cachedDataRoot);
                LogDataRoot(s_cachedDataRoot);
                WriteRootExportReadme(s_cachedDataRoot);
                return s_cachedDataRoot;
            }

            string root = Path.Combine(Application.persistentDataPath, ExperimentDataFolderName);
            Directory.CreateDirectory(root);
            s_cachedDataRoot = root;
            LogDataRoot(root);
            WriteRootExportReadme(root);
            return root;
        }

        public static SessionContext CurrentSession => s_currentSession;

        public static string CurrentParticipantId => !string.IsNullOrWhiteSpace(s_currentSession.ParticipantId)
            ? s_currentSession.ParticipantId
            : FallbackParticipantId;

        public static string CurrentSessionId => s_currentSession.SessionId ?? string.Empty;
        public static string CurrentQuestionnaireCode => s_currentSession.QuestionnaireCode ?? string.Empty;
        public static string CurrentQuestionnaireCodeScheme => s_currentSession.QuestionnaireCodeScheme ?? string.Empty;

        public static bool HasActiveSession => !string.IsNullOrWhiteSpace(s_currentSession.SessionRoot);
        public static bool IsSessionClosed => HasActiveSession && s_sessionClosed;

        public static string CreateParticipantId()
        {
            return $"U{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        }

        public static string CreateSessionId()
        {
            return $"S{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        }

        public static string CreateSessionId(string participantId)
        {
            return CreateSessionId();
        }

        public static SessionContext ConfigureSession(
            string participantId,
            string sessionId,
            IEnumerable<string> conditionOrder,
            int roundsPerCondition,
            bool allowOverwrite = false)
        {
            if (HasActiveSession)
            {
                Debug.LogError(
                    $"[ExperimentDataPathResolver] session_identity_configuration_rejected | reason=active_session_must_be_closed_and_prepared current_participant_id={s_currentSession.ParticipantId} current_session_id={s_currentSession.SessionId} requested_participant_id={participantId ?? string.Empty} requested_session_id={sessionId ?? string.Empty}");
                return default;
            }

            string[] conditions = ToArray(conditionOrder);
            if (!QuestionnaireCodeCodec.TryGetPrefixForOrder(conditions, out char questionnairePrefix))
            {
                LogDiagnostic(
                    "questionnaire_code_generation_failed",
                    new Dictionary<string, object>
                    {
                        ["participant_id"] = participantId ?? string.Empty,
                        ["session_id"] = sessionId ?? string.Empty,
                        ["scheme"] = QuestionnaireCodeCodec.Scheme,
                        ["condition_order_ids"] = conditions,
                        ["error_reason"] = "unsupported_authoritative_condition_order"
                    },
                    asWarning: true);
                return default;
            }

            bool automaticParticipant = RequiresAutomaticParticipantId(participantId);
            bool automaticSession = RequiresAutomaticSessionId(sessionId);
            string participant = automaticParticipant
                ? CreateParticipantId()
                : SanitizeParticipantId(participantId);
            if (string.Equals(participant, FallbackParticipantId, StringComparison.Ordinal))
            {
                LogDiagnostic(
                    "p45f_data_storage_failed",
                    new Dictionary<string, object>
                    {
                        ["participant_id"] = participant,
                        ["session_id"] = sessionId ?? string.Empty,
                        ["error_reason"] = "participant_id_missing_using_pilot_unset"
                    },
                    asWarning: true);
            }

            string resolvedSessionId = automaticSession
                ? CreateSessionId()
                : Sanitize(sessionId);
            string persistentRoot = Application.persistentDataPath;
            string dataRoot = ResolveDataRoot();
            string participantRoot = Path.Combine(dataRoot, participant);
            if (!allowOverwrite && (automaticParticipant || IsGeneratedParticipantId(participant)))
            {
                participantRoot = ResolveUniqueDirectory(participantRoot, out participant);
            }

            string sessionRoot = Path.Combine(participantRoot, resolvedSessionId);
            if (!allowOverwrite)
            {
                sessionRoot = ResolveUniqueDirectory(sessionRoot, out resolvedSessionId);
                resolvedSessionId = Path.GetFileName(sessionRoot);
            }

            Debug.Log($"[P46D-03] configure_session_before_directory | participant_id={participant} session_id={resolvedSessionId} session_root={sessionRoot} data_root={dataRoot} persistent_root={persistentRoot}");
            Directory.CreateDirectory(sessionRoot);
            Debug.Log($"[P46D-03] configure_session_after_directory | participant_id={participant} session_id={resolvedSessionId} session_root={sessionRoot}");
            string startedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            Debug.Log($"[P46D-03] configure_session_before_history_register | participant_id={participant} session_id={resolvedSessionId} history_path={ExperimentSessionIdHistoryStore.DefaultPath}");
            ExperimentSessionIdHistoryEntry historyEntry = null;
            try
            {
                historyEntry = ExperimentSessionIdHistoryStore.RegisterSessionStarted(
                    participant,
                    resolvedSessionId,
                    startedAtUtc,
                    conditions);
                Debug.Log($"[P46D-03] configure_session_after_history_register | participant_id={participant} session_id={resolvedSessionId} questionnaire_code={historyEntry?.questionnaire_code ?? string.Empty} status={historyEntry?.status ?? string.Empty}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[P46D-03] configure_session_history_register_failed | participant_id={participant} session_id={resolvedSessionId} history_path={ExperimentSessionIdHistoryStore.DefaultPath} error={ex.GetType().Name}:{ex.Message}");
                return default;
            }

            string questionnaireCode = historyEntry?.questionnaire_code;
            if (string.IsNullOrWhiteSpace(questionnaireCode))
            {
                Debug.LogError($"[P46D-03] configure_session_questionnaire_code_missing | participant_id={participant} session_id={resolvedSessionId}");
                return default;
            }

            Debug.Log($"[P46D-03] configure_session_before_context_assign | participant_id={participant} session_id={resolvedSessionId} questionnaire_code={questionnaireCode}");
            if (!ExperimentSessionIdentity.TryCreate(
                    participant,
                    resolvedSessionId,
                    questionnaireCode,
                    historyEntry.questionnaire_code_scheme,
                    conditions,
                    sessionRoot,
                    out ExperimentSessionIdentity identity,
                    out string identityError))
            {
                Debug.LogError($"[P46D-03] configure_session_identity_invalid | participant_id={participant} session_id={resolvedSessionId} error_reason={identityError}");
                ExperimentSessionIdHistoryStore.TryUpdateSessionStatus(
                    resolvedSessionId,
                    ExperimentSessionIdHistoryStore.AbandonedStatus,
                    "session_identity_validation_failed",
                    out _);
                return default;
            }

            s_currentSession = new SessionContext(
                identity,
                participant,
                resolvedSessionId,
                sessionRoot,
                dataRoot,
                persistentRoot,
                startedAtUtc,
                questionnaireCode,
                historyEntry.questionnaire_code_scheme,
                conditions,
                Mathf.Max(1, roundsPerCondition));
            s_sessionClosed = false;

            Debug.Log($"[P46D-03] configure_session_before_export_info | participant_id={participant} session_id={resolvedSessionId} questionnaire_code={s_currentSession.QuestionnaireCode}");
            WriteSessionExportInfo(
                sessionRoot,
                resolvedSessionId,
                participant,
                conditions,
                s_currentSession.QuestionnaireCode,
                s_currentSession.QuestionnaireCodeScheme);
            Debug.Log($"[P46D-03] configure_session_after_export_info | participant_id={participant} session_id={resolvedSessionId} questionnaire_code={s_currentSession.QuestionnaireCode}");
            LogDiagnostic(
                "p45f_participant_id_selected",
                BuildDiagnosticPayload(s_currentSession, string.Empty));
            LogDiagnostic(
                "p45f_session_id_created",
                BuildDiagnosticPayload(s_currentSession, string.Empty));
            LogDiagnostic(
                "p45f_data_storage_ready",
                BuildDiagnosticPayload(s_currentSession, string.Empty));
            LogDiagnostic(
                "p45f_adb_pull_hint",
                BuildDiagnosticPayload(s_currentSession, BuildAdbPullHintForRoot()));
            return s_currentSession;
        }

        public static SessionContext ConfigureExistingSessionForResume(
            string participantId,
            string sessionId,
            string questionnaireCode,
            IEnumerable<string> conditionOrder,
            int roundsPerCondition,
            string questionnaireCodeScheme = "")
        {
            if (HasActiveSession)
            {
                Debug.LogError(
                    $"[ExperimentDataPathResolver] saved_exit_resume_identity_configuration_rejected | reason=active_session_must_be_closed_and_prepared current_participant_id={s_currentSession.ParticipantId} current_session_id={s_currentSession.SessionId} requested_participant_id={participantId ?? string.Empty} requested_session_id={sessionId ?? string.Empty}");
                return default;
            }

            string[] conditions = ToArray(conditionOrder);
            if (!ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(conditions))
            {
                LogDiagnostic(
                    "p46d_saved_exit_resume_storage_failed",
                    new Dictionary<string, object>
                    {
                        ["participant_id"] = participantId ?? string.Empty,
                        ["session_id"] = sessionId ?? string.Empty,
                        ["condition_order_ids"] = conditions,
                        ["error_reason"] = "invalid_authoritative_condition_order"
                    },
                    asWarning: true);
                return default;
            }

            string participant = RequiresAutomaticParticipantId(participantId)
                ? CreateParticipantId()
                : SanitizeParticipantId(participantId);
            string resolvedSessionId = RequiresAutomaticSessionId(sessionId)
                ? CreateSessionId()
                : Sanitize(sessionId);
            string persistentRoot = Application.persistentDataPath;
            string dataRoot = ResolveDataRoot();
            string participantRoot = Path.Combine(dataRoot, participant);
            string sessionRoot = Path.Combine(participantRoot, resolvedSessionId);
            Directory.CreateDirectory(sessionRoot);

            string startedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            if (!ExperimentSessionIdentity.TryCreate(
                    participant,
                    resolvedSessionId,
                    questionnaireCode,
                    questionnaireCodeScheme,
                    conditions,
                    sessionRoot,
                    out ExperimentSessionIdentity identity,
                    out string identityError))
            {
                Debug.LogError($"[ExperimentDataPathResolver] saved_exit_resume_identity_invalid | participant_id={participant} session_id={resolvedSessionId} error_reason={identityError}");
                return default;
            }

            s_currentSession = new SessionContext(
                identity,
                participant,
                resolvedSessionId,
                sessionRoot,
                dataRoot,
                persistentRoot,
                startedAtUtc,
                questionnaireCode ?? string.Empty,
                questionnaireCodeScheme ?? string.Empty,
                conditions,
                Mathf.Max(1, roundsPerCondition));
            s_sessionClosed = false;

            WriteSessionExportInfo(
                sessionRoot,
                resolvedSessionId,
                participant,
                conditions,
                s_currentSession.QuestionnaireCode,
                s_currentSession.QuestionnaireCodeScheme);
            LogDiagnostic(
                "p46d_saved_exit_resume_storage_ready",
                BuildDiagnosticPayload(s_currentSession, string.Empty));
            return s_currentSession;
        }

        public static void RegisterSessionContextFromPayload(Dictionary<string, object> payload)
        {
            if (payload == null)
            {
                return;
            }

            if (HasActiveSession)
            {
                SanitizePayloadForCurrentSession(payload, "register_session_context");
                return;
            }

            string participantId = TryGetPayloadString(payload, "participant_id", out string participant)
                ? participant
                : s_currentSession.ParticipantId;
            string sessionId = TryGetPayloadString(payload, "session_id", out string session)
                ? session
                : s_currentSession.SessionId;
            int roundsPerCondition = TryGetPayloadInt(payload, "rounds_per_condition", out int rounds)
                ? rounds
                : s_currentSession.RoundsPerCondition;
            if (string.IsNullOrWhiteSpace(participantId) && string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            if (!IsGeneratedParticipantId(Sanitize(participantId)) || !IsGeneratedSessionId(Sanitize(sessionId)))
            {
                return;
            }

            if (string.Equals(SanitizeParticipantId(participantId), s_currentSession.ParticipantId, StringComparison.Ordinal) &&
                string.Equals(Sanitize(sessionId), s_currentSession.SessionId, StringComparison.Ordinal))
            {
                return;
            }

            ConfigureSession(participantId, sessionId, ResolveConditionOrderFromPayload(payload), roundsPerCondition);
        }

        public static void SanitizePayloadForCurrentSession(Dictionary<string, object> payload, string source)
        {
            if (payload == null || !HasActiveSession)
            {
                return;
            }

            bool repairedParticipant = ShouldRepairParticipant(payload);
            bool repairedSession = ShouldRepairSession(payload);
            StampPayloadWithCurrentSession(payload);

            if (repairedParticipant)
            {
                payload["p45f_participant_id_repaired"] = true;
                payload["p45f_participant_id_repair_reason"] = "legacy_or_mismatched_participant_id";
            }

            if (repairedSession)
            {
                payload["p45f_session_id_repaired"] = true;
                payload["p45f_session_id_repair_reason"] = "missing_or_mismatched_session_id";
            }

            if (repairedParticipant || repairedSession)
            {
                Debug.LogWarning(
                    $"[ExperimentDataPathResolver] p45f_runtime_context_payload_repaired | source={source ?? string.Empty} participant_id={s_currentSession.ParticipantId} session_id={s_currentSession.SessionId} session_root={s_currentSession.SessionRoot}");
            }
        }

        public static bool TryStampPayloadWithCurrentSessionIdentity(
            Dictionary<string, object> payload,
            out string errorReason)
        {
            errorReason = string.Empty;
            if (payload == null)
            {
                errorReason = "payload_missing";
                return false;
            }

            if (!HasActiveSession || s_currentSession.Identity == null)
            {
                errorReason = "active_session_identity_missing";
                return false;
            }

            if (!TryValidateCurrentIdentity(s_currentSession.Identity, out errorReason))
            {
                return false;
            }

            StampPayloadWithCurrentSession(payload);
            return true;
        }

        public static string ResolveSessionDirectory(string sessionId)
        {
            if (HasActiveSession)
            {
                Directory.CreateDirectory(s_currentSession.SessionRoot);
                return s_currentSession.SessionRoot;
            }

            string participant = string.IsNullOrWhiteSpace(s_currentSession.ParticipantId)
                ? FallbackParticipantId
                : s_currentSession.ParticipantId;
            return ResolveSessionDirectory(participant, sessionId);
        }

        public static string ResolveSessionDirectory(string participantId, string sessionId)
        {
            if (HasActiveSession)
            {
                string requestedParticipant = SanitizeParticipantId(participantId);
                string requestedSession = Sanitize(sessionId);
                if (string.Equals(requestedParticipant, s_currentSession.ParticipantId, StringComparison.Ordinal) &&
                    string.Equals(requestedSession, s_currentSession.SessionId, StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(s_currentSession.SessionRoot);
                    return s_currentSession.SessionRoot;
                }
            }

            string safeParticipantId = SanitizeParticipantId(participantId);
            if (RequiresAutomaticParticipantId(participantId))
            {
                safeParticipantId = CreateParticipantId();
            }

            string safeSessionId = Sanitize(RequiresAutomaticSessionId(sessionId) ? CreateSessionId() : sessionId);
            string directory = Path.Combine(ResolveDataRoot(), safeParticipantId, safeSessionId);
            Directory.CreateDirectory(directory);
            return directory;
        }

        public static bool TryReadPersistedSessionConditionOrder(
            string participantId,
            string sessionId,
            out List<string> conditionOrder,
            out string reason)
        {
            conditionOrder = new List<string>();
            reason = string.Empty;
            try
            {
                string sessionDirectory = ResolveSessionDirectory(participantId, sessionId);
                string exportInfoPath = Path.Combine(sessionDirectory, "session_export_info.json");
                if (!File.Exists(exportInfoPath))
                {
                    reason = "session_export_info_missing";
                    return false;
                }

                string json = File.ReadAllText(exportInfoPath, Encoding.UTF8);
                SessionExportInfoDocument document = JsonUtility.FromJson<SessionExportInfoDocument>(json);
                string[] persistedOrder = document?.condition_order_ids != null && document.condition_order_ids.Length > 0
                    ? document.condition_order_ids
                    : document?.condition_order;
                if (!ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(persistedOrder))
                {
                    reason = "session_export_info_condition_order_invalid";
                    return false;
                }

                conditionOrder.AddRange(persistedOrder);
                reason = "session_export_info_condition_order_loaded";
                return true;
            }
            catch (Exception ex)
            {
                reason = "session_export_info_read_failed:" + ex.GetType().Name;
                return false;
            }
        }

        public static string ResolveCurrentSessionDirectory(string fallbackSessionId, IEnumerable<string> conditionOrder = null, int roundsPerCondition = 1)
        {
            if (!string.IsNullOrWhiteSpace(s_currentSession.SessionRoot))
            {
                Directory.CreateDirectory(s_currentSession.SessionRoot);
                return s_currentSession.SessionRoot;
            }

            SessionContext context = ConfigureSession(
                string.Empty,
                string.Empty,
                conditionOrder,
                roundsPerCondition);
            return context.SessionRoot;
        }

        public static void EndCurrentSession()
        {
            EndCurrentSession(ExperimentSessionIdHistoryStore.CompletedStatus);
        }

        public static void EndCurrentSession(string status)
        {
            if (!HasActiveSession)
            {
                return;
            }

            if (Directory.Exists(s_currentSession.SessionRoot))
            {
                UpdateFileIndex(s_currentSession.SessionRoot, emitLog: false);
            }

            ExperimentSessionIdHistoryStore.TryUpdateSessionStatus(
                s_currentSession.SessionId,
                string.IsNullOrWhiteSpace(status) ? ExperimentSessionIdHistoryStore.CompletedStatus : status,
                "data_path_resolver_end_current_session",
                out _);
            s_sessionClosed = true;
        }

        public static bool EmitQuestionnaireCodeGenerated(SessionContext context)
        {
            if (!TryValidateCurrentIdentity(context.Identity, out string errorReason) ||
                !QuestionnaireCodeCodec.TryGetPrefixForOrder(context.ConditionOrder, out char prefix))
            {
                Debug.LogError(
                    $"[ExperimentDataPathResolver] questionnaire_code_generated_rejected | participant_id={context.ParticipantId} session_id={context.SessionId} error_reason={errorReason}");
                return false;
            }

            LogDiagnostic(
                "questionnaire_code_generated",
                new Dictionary<string, object>
                {
                    ["scheme"] = context.QuestionnaireCodeScheme,
                    ["questionnaire_code"] = context.QuestionnaireCode,
                    ["prefix"] = prefix.ToString(),
                    ["condition_order_ids"] = context.ConditionOrder,
                    ["condition_order"] = ExperimentCompensatedConditionOrder.FormatOrder(context.ConditionOrder),
                    ["session_id"] = context.SessionId,
                    ["participant_id"] = context.ParticipantId,
                    ["session_root"] = context.SessionRoot
                });
            return true;
        }

        public static bool TryValidateCurrentIdentity(ExperimentSessionIdentity identity, out string errorReason)
        {
            errorReason = string.Empty;
            if (!HasActiveSession || s_currentSession.Identity == null)
            {
                errorReason = "active_session_identity_missing";
                return false;
            }

            if (identity == null || !s_currentSession.Identity.Matches(identity))
            {
                errorReason = "identity_does_not_match_active_session";
                return false;
            }

            string expectedRoot = Path.Combine(s_currentSession.DataRoot, identity.ParticipantId, identity.SessionId);
            if (!ExperimentSessionIdentity.PathsMatch(identity.SessionRoot, expectedRoot))
            {
                errorReason = "session_root_does_not_match_active_identity";
                return false;
            }

            return true;
        }

        public static void PrepareForNewSessionStart()
        {
            if (!HasActiveSession)
            {
                s_sessionClosed = false;
                return;
            }

            if (!s_sessionClosed)
            {
                return;
            }

            s_currentSession = default;
            s_sessionClosed = false;
        }

        public static bool WriteSessionExportInfo(
            string sessionDirectory,
            string sessionId,
            string participantId,
            IEnumerable<string> conditionOrder,
            string questionnaireCode = null,
            string questionnaireCodeScheme = null)
        {
            if (string.IsNullOrWhiteSpace(sessionDirectory))
            {
                return false;
            }

            string[] conditions = conditionOrder == null
                ? Array.Empty<string>()
                : new List<string>(conditionOrder).ToArray();
            string resolvedCode = questionnaireCode ?? CurrentQuestionnaireCode;
            string resolvedScheme = questionnaireCodeScheme ?? CurrentQuestionnaireCodeScheme;
            if (HasActiveSession &&
                (!string.Equals(sessionId ?? string.Empty, s_currentSession.SessionId, StringComparison.Ordinal) ||
                 !string.Equals(participantId ?? string.Empty, s_currentSession.ParticipantId, StringComparison.Ordinal) ||
                 !string.Equals(resolvedCode, s_currentSession.QuestionnaireCode, StringComparison.Ordinal) ||
                 !string.Equals(resolvedScheme, s_currentSession.QuestionnaireCodeScheme, StringComparison.Ordinal) ||
                 !ExperimentSessionIdentity.PathsMatch(sessionDirectory, s_currentSession.SessionRoot) ||
                 !OrdersMatch(conditions, s_currentSession.ConditionOrder)))
            {
                Debug.LogError(
                    $"[ExperimentDataPathResolver] session_export_write_rejected | reason=identity_or_path_mismatch participant_id={participantId ?? string.Empty} session_id={sessionId ?? string.Empty} session_root={sessionDirectory}");
                return false;
            }

            Directory.CreateDirectory(sessionDirectory);
            string dataRoot = ResolveDataRoot();
            string timestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            string adbRoot = BuildAdbExperimentDataPath();
            string adbSession = BuildAdbSessionPath(participantId, sessionId);
            Dictionary<string, object> payload = new()
            {
                ["data_root"] = dataRoot,
                ["persistent_root"] = Application.persistentDataPath,
                ["application_persistent_data_path"] = Application.persistentDataPath,
                ["session_id"] = sessionId ?? string.Empty,
                ["participant_id"] = participantId ?? string.Empty,
                ["questionnaire_code"] = resolvedCode,
                ["questionnaire_code_scheme"] = resolvedScheme,
                ["session_root"] = sessionDirectory,
                ["condition_order"] = conditions,
                ["condition_order_ids"] = conditions,
                ["platform"] = Application.platform.ToString(),
                ["unity_platform"] = Application.platform.ToString(),
                ["package_name"] = Application.identifier,
                ["p45f_storage_schema_version"] = "P45F",
                ["session_identity_schema_version"] = 2,
                ["timestamp_utc"] = timestampUtc,
                ["adb_pull_hint"] = $"adb pull \"{adbRoot}\" \"<DESTINATION>\"",
                ["adb_pull_session_hint"] = $"adb pull \"{adbSession}\" \"<DESTINATION>\""
            };

            File.WriteAllText(
                Path.Combine(sessionDirectory, "session_export_info.json"),
                SerializeJsonObject(payload) + Environment.NewLine,
                Encoding.UTF8);

            File.WriteAllText(
                Path.Combine(sessionDirectory, "README_EXPORT.txt"),
                BuildExportReadme(dataRoot, sessionDirectory, participantId, sessionId, adbRoot, adbSession),
                Encoding.UTF8);
            WriteRootExportReadme(dataRoot);
            UpdateFileIndex(sessionDirectory, emitLog: false);
            return true;
        }

        public static void UpdateFileIndex(string sessionDirectory, bool emitLog = true)
        {
            if (string.IsNullOrWhiteSpace(sessionDirectory) || !Directory.Exists(sessionDirectory))
            {
                return;
            }

            if (HasActiveSession && !ExperimentSessionIdentity.PathsMatch(sessionDirectory, s_currentSession.SessionRoot))
            {
                Debug.LogError(
                    $"[ExperimentDataPathResolver] file_index_write_rejected | reason=session_root_mismatch active_session_id={s_currentSession.SessionId} active_session_root={s_currentSession.SessionRoot} requested_session_root={sessionDirectory}");
                return;
            }

            string indexPath = Path.Combine(sessionDirectory, "file_index.csv");
            var builder = new StringBuilder();
            builder.AppendLine("relative_path,file_type,condition_id,round_id,created_at,size_bytes");
            foreach (string path in Directory.GetFiles(sessionDirectory, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(path, indexPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                FileInfo info = new(path);
                string relativePath = ToRelativePath(sessionDirectory, path).Replace('\\', '/');
                builder.Append(EscapeCsv(relativePath)).Append(',');
                builder.Append(EscapeCsv(ClassifyFile(path))).Append(',');
                builder.Append(EscapeCsv(DetectConditionId(relativePath))).Append(',');
                builder.Append(EscapeCsv(DetectRoundId(relativePath))).Append(',');
                builder.Append(EscapeCsv(info.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture))).Append(',');
                builder.Append(info.Length.ToString(CultureInfo.InvariantCulture)).AppendLine();
            }

            File.WriteAllText(indexPath, builder.ToString(), Encoding.UTF8);
            if (emitLog)
            {
                LogDiagnostic(
                    "p45f_file_index_updated",
                    new Dictionary<string, object>
                    {
                        ["participant_id"] = CurrentParticipantId,
                        ["session_id"] = CurrentSessionId,
                        ["session_root"] = sessionDirectory,
                        ["persistent_root"] = Application.persistentDataPath,
                        ["file_path"] = indexPath,
                        ["platform"] = Application.platform.ToString(),
                        ["package_name"] = Application.identifier
                    });
            }
        }

        public static bool IsAndroidRuntime()
        {
            return Application.platform == RuntimePlatform.Android && !Application.isEditor;
        }

        private static void LogDataRoot(string root)
        {
            if (s_loggedDataRoot)
            {
                return;
            }

            s_loggedDataRoot = true;
            Dictionary<string, object> payload = new()
            {
                ["data_root"] = root,
                ["persistent_root"] = Application.persistentDataPath,
                ["application_persistent_data_path"] = Application.persistentDataPath,
                ["platform"] = Application.platform.ToString(),
                ["unity_platform"] = Application.platform.ToString(),
                ["android_runtime"] = IsAndroidRuntime(),
                ["package_name"] = Application.identifier
            };
            LogDiagnostic("p45f_data_storage_preflight_started", payload);
            Debug.Log($"[ExperimentDataPathResolver] p45f_data_storage_preflight_started | data_root={root} persistent={Application.persistentDataPath} platform={Application.platform} package={Application.identifier}");
        }

        private static Dictionary<string, object> BuildDiagnosticPayload(SessionContext context, string adbPullHint)
        {
            return new Dictionary<string, object>
            {
                ["participant_id"] = context.ParticipantId,
                ["session_id"] = context.SessionId,
                ["questionnaire_code"] = context.QuestionnaireCode,
                ["questionnaire_code_scheme"] = context.QuestionnaireCodeScheme,
                ["condition_order_ids"] = context.ConditionOrder,
                ["session_root"] = context.SessionRoot,
                ["persistent_root"] = context.PersistentRoot,
                ["data_root"] = context.DataRoot,
                ["platform"] = Application.platform.ToString(),
                ["package_name"] = Application.identifier,
                ["adb_pull_hint"] = adbPullHint ?? string.Empty
            };
        }

        private static void LogDiagnostic(string eventType, Dictionary<string, object> payload, bool asWarning = false)
        {
            payload ??= new Dictionary<string, object>();
            payload["p45f_resolver_diagnostic"] = true;
            if (!payload.ContainsKey("platform"))
            {
                payload["platform"] = Application.platform.ToString();
            }

            if (!payload.ContainsKey("package_name"))
            {
                payload["package_name"] = Application.identifier;
            }

            if (!s_inTelemetryLog)
            {
                try
                {
                    s_inTelemetryLog = true;
                    TiagoExperimentTelemetry.LogEvent(eventType, payload);
                }
                finally
                {
                    s_inTelemetryLog = false;
                }
            }

            string message = $"[ExperimentDataPathResolver] {eventType} | participant_id={Value(payload, "participant_id")} session_id={Value(payload, "session_id")} session_root={Value(payload, "session_root")} persistent_root={Value(payload, "persistent_root")} platform={Value(payload, "platform")} package_name={Value(payload, "package_name")} file_path={Value(payload, "file_path")} error_reason={Value(payload, "error_reason")}";
            if (asWarning)
            {
                Debug.LogWarning(message);
            }
            else
            {
                Debug.Log(message);
            }
        }

        private static string Value(Dictionary<string, object> payload, string key)
        {
            return payload.TryGetValue(key, out object value) && value != null ? value.ToString() : string.Empty;
        }

        private static void StampPayloadWithCurrentSession(Dictionary<string, object> payload)
        {
            if (payload == null || !HasActiveSession)
            {
                return;
            }

            payload["participant_id"] = s_currentSession.ParticipantId;
            payload["session_id"] = s_currentSession.SessionId;
            payload["session_root"] = s_currentSession.SessionRoot;
            payload["persistent_root"] = s_currentSession.PersistentRoot;
            payload["questionnaire_code"] = s_currentSession.QuestionnaireCode;
            payload["questionnaire_code_scheme"] = s_currentSession.QuestionnaireCodeScheme;
            payload["condition_order_ids"] = s_currentSession.ConditionOrder != null
                ? (string[])s_currentSession.ConditionOrder.Clone()
                : Array.Empty<string>();
        }

        private static bool ShouldRepairParticipant(Dictionary<string, object> payload)
        {
            if (!TryGetPayloadString(payload, "participant_id", out string value))
            {
                return true;
            }

            string sanitized = Sanitize(value);
            return string.IsNullOrWhiteSpace(sanitized) ||
                IsLegacyManualParticipantId(sanitized) ||
                !string.Equals(sanitized, s_currentSession.ParticipantId, StringComparison.Ordinal);
        }

        private static bool ShouldRepairSession(Dictionary<string, object> payload)
        {
            if (!TryGetPayloadString(payload, "session_id", out string value))
            {
                return true;
            }

            string sanitized = Sanitize(value);
            return string.IsNullOrWhiteSpace(value) ||
                string.Equals(sanitized, "session", StringComparison.Ordinal) ||
                !string.Equals(sanitized, s_currentSession.SessionId, StringComparison.Ordinal);
        }

        private static string ResolveUniqueDirectory(string requestedPath)
        {
            return ResolveUniqueDirectory(requestedPath, out _);
        }

        private static string ResolveUniqueDirectory(string requestedPath, out string resolvedToken)
        {
            if (!Directory.Exists(requestedPath) && !File.Exists(requestedPath))
            {
                resolvedToken = Path.GetFileName(requestedPath);
                return requestedPath;
            }

            for (int i = 2; i < 1000; i++)
            {
                string candidate = $"{requestedPath}_{i:00}";
                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                {
                    resolvedToken = Path.GetFileName(candidate);
                    return candidate;
                }
            }

            string fallback = $"{requestedPath}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            resolvedToken = Path.GetFileName(fallback);
            return fallback;
        }

        private static string[] ResolveConditionOrderFromPayload(Dictionary<string, object> payload)
        {
            if (payload != null && payload.TryGetValue("condition_order", out object rawOrder) && rawOrder is IEnumerable<string> strings)
            {
                return ToArray(strings);
            }

            if (s_currentSession.ConditionOrder != null && s_currentSession.ConditionOrder.Length > 0)
            {
                return s_currentSession.ConditionOrder;
            }

            return new[]
            {
                "C00_robot_off_voice_off",
                "C10_robot_on_voice_off",
                "C11_robot_on_voice_on"
            };
        }

        private static bool TryGetPayloadString(Dictionary<string, object> payload, string key, out string value)
        {
            value = string.Empty;
            if (payload == null || !payload.TryGetValue(key, out object raw) || raw == null)
            {
                return false;
            }

            value = raw.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryGetPayloadInt(Dictionary<string, object> payload, string key, out int value)
        {
            value = 0;
            if (payload == null || !payload.TryGetValue(key, out object raw) || raw == null)
            {
                return false;
            }

            if (raw is int intValue)
            {
                value = intValue;
                return true;
            }

            return int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static string BuildExportReadme(
            string dataRoot,
            string sessionRoot,
            string participantId,
            string sessionId,
            string adbRoot,
            string adbSession)
        {
            string projectDestination = @"C:\UnityProjects\Multomodal_AI_VR_URP\ExportedQuestData";
            return
                "P45F Quest experiment data export\n" +
                $"participant_id={participantId ?? string.Empty}\n" +
                $"session_id={sessionId ?? string.Empty}\n" +
                $"questionnaire_code={CurrentQuestionnaireCode}\n" +
                $"questionnaire_code_scheme={CurrentQuestionnaireCodeScheme}\n" +
                $"package_name={Application.identifier}\n" +
                $"persistent_root={Application.persistentDataPath}\n" +
                $"data_root={dataRoot}\n" +
                $"session_root={sessionRoot}\n" +
                "\nPowerShell export commands:\n" +
                $"New-Item -ItemType Directory -Force \"{projectDestination}\"\n" +
                $"adb pull \"{adbRoot}\" \"{projectDestination}\"\n" +
                "\nSingle-session export:\n" +
                $"adb pull \"{adbSession}\" \"{projectDestination}\"\n" +
                "\nIf the package name changes, get it with:\n" +
                "adb shell pm list packages | Select-String -Pattern \"Multomodal|Unity|urp\"\n";
        }

        private static void WriteRootExportReadme(string dataRoot)
        {
            if (string.IsNullOrWhiteSpace(dataRoot))
            {
                return;
            }

            Directory.CreateDirectory(dataRoot);
            string destination = @"C:\UnityProjects\Multomodal_AI_VR_URP\ExportedQuestData";
            File.WriteAllText(
                Path.Combine(dataRoot, "README_EXPORT.txt"),
                "P45F ExperimentData root\n" +
                $"package_name={Application.identifier}\n" +
                $"persistent_root={Application.persistentDataPath}\n" +
                $"data_root={dataRoot}\n" +
                $"adb_pull_all=adb pull \"{BuildAdbExperimentDataPath()}\" \"{destination}\"\n",
                Encoding.UTF8);
        }

        private static string BuildAdbPullHintForRoot()
        {
            return $"adb pull \"{BuildAdbExperimentDataPath()}\" \"C:\\UnityProjects\\Multomodal_AI_VR_URP\\ExportedQuestData\"";
        }

        private static string BuildAdbExperimentDataPath()
        {
            string packageName = string.IsNullOrWhiteSpace(Application.identifier)
                ? "<package_name>"
                : Application.identifier;
            return $"/sdcard/Android/data/{packageName}/files/{ExperimentDataFolderName}";
        }

        private static string BuildAdbSessionPath(string participantId, string sessionId)
        {
            return $"{BuildAdbExperimentDataPath()}/{SanitizeParticipantId(participantId)}/{Sanitize(sessionId)}";
        }

        private static string ToRelativePath(string root, string path)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(fullRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return Path.GetFileName(path);
        }

        private static string ClassifyFile(string path)
        {
            string name = Path.GetFileName(path).ToLowerInvariant();
            if (name == "session_manifest.json")
            {
                return "session_manifest";
            }

            if (name == "session_export_info.json")
            {
                return "export_info";
            }

            if (name.Contains("events") && name.EndsWith(".jsonl", StringComparison.Ordinal))
            {
                return "events_jsonl";
            }

            if (name.Contains("samples") && name.EndsWith(".csv", StringComparison.Ordinal))
            {
                return "samples_csv";
            }

            if (name.Contains("session_trials"))
            {
                return name.EndsWith(".jsonl", StringComparison.Ordinal) ? "session_trials_jsonl" : "session_trials_csv";
            }

            if (name.Contains("trial_summary"))
            {
                return name.EndsWith(".jsonl", StringComparison.Ordinal) ? "trial_summary_jsonl" : "trial_summary_csv";
            }

            if (name.EndsWith(".md", StringComparison.Ordinal))
            {
                return "markdown";
            }

            return Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        }

        private static string DetectConditionId(string value)
        {
            if (value.IndexOf("C00", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "C00_robot_off_voice_off";
            }

            if (value.IndexOf("C10", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "C10_robot_on_voice_off";
            }

            if (value.IndexOf("C11", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "C11_robot_on_voice_on";
            }

            return string.Empty;
        }

        private static string DetectRoundId(string value)
        {
            int index = value.IndexOf("_round_", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                index = value.IndexOf("_round", StringComparison.OrdinalIgnoreCase);
            }

            if (index < 0)
            {
                return string.Empty;
            }

            int start = Math.Max(0, value.LastIndexOf('/', index) + 1);
            int end = value.IndexOf('.', index);
            if (end < 0)
            {
                end = value.Length;
            }

            return value.Substring(start, end - start);
        }

        private static string[] ToArray(IEnumerable<string> values)
        {
            if (values == null)
            {
                return Array.Empty<string>();
            }

            var list = new List<string>();
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    list.Add(value);
                }
            }

            return list.ToArray();
        }

        private static bool OrdersMatch(IReadOnlyList<string> first, IReadOnlyList<string> second)
        {
            if (first == null || second == null || first.Count != second.Count)
            {
                return false;
            }

            for (int i = 0; i < first.Count; i++)
            {
                if (!string.Equals(first[i], second[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "session";
            }

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return value.Replace(' ', '_');
        }

        private static string SanitizeParticipantId(string value)
        {
            string sanitized = Sanitize(value);
            return string.IsNullOrWhiteSpace(value) || string.Equals(sanitized, "session", StringComparison.Ordinal)
                ? FallbackParticipantId
                : sanitized;
        }

        public static bool RequiresAutomaticParticipantId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            string sanitized = Sanitize(value);
            return string.Equals(sanitized, "session", StringComparison.Ordinal) ||
                string.Equals(sanitized, FallbackParticipantId, StringComparison.OrdinalIgnoreCase) ||
                IsLegacyManualParticipantId(sanitized);
        }

        public static bool RequiresAutomaticSessionId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            string sanitized = Sanitize(value);
            return string.Equals(sanitized, "session", StringComparison.Ordinal) ||
                IsLegacyManualParticipantId(sanitized);
        }

        private static bool IsLegacyManualParticipantId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 2 || value[0] != 'P')
            {
                return false;
            }

            for (int i = 1; i < value.Length; i++)
            {
                char ch = value[i];
                if (ch == '_' || ch == '-')
                {
                    return i > 1;
                }

                if (!char.IsDigit(ch))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsGeneratedParticipantId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 16 || value[0] != 'U')
            {
                return false;
            }

            return IsTimestampToken(value, 1);
        }

        private static bool IsGeneratedSessionId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 16 || value[0] != 'S')
            {
                return false;
            }

            return IsTimestampToken(value, 1);
        }

        private static bool IsTimestampToken(string value, int offset)
        {
            if (value.Length < offset + 15)
            {
                return false;
            }

            for (int i = offset; i < offset + 8; i++)
            {
                if (!char.IsDigit(value[i]))
                {
                    return false;
                }
            }

            if (value[offset + 8] != '_')
            {
                return false;
            }

            for (int i = offset + 9; i < offset + 15; i++)
            {
                if (!char.IsDigit(value[i]))
                {
                    return false;
                }
            }

            return value.Length == offset + 15 ||
                (value.Length == offset + 18 && value[offset + 15] == '_' && char.IsDigit(value[offset + 16]) && char.IsDigit(value[offset + 17]));
        }

        private static string EscapeCsv(string value)
        {
            value ??= string.Empty;
            if (!value.Contains(",") && !value.Contains("\"") && !value.Contains("\n") && !value.Contains("\r"))
            {
                return value;
            }

            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        private static string SerializeJsonObject(Dictionary<string, object> payload)
        {
            StringBuilder builder = new();
            builder.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, object> pair in payload)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                builder.Append('"');
                builder.Append(EscapeJson(pair.Key));
                builder.Append("\":");
                builder.Append(SerializeJsonValue(pair.Value));
                first = false;
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static string SerializeJsonValue(object value)
        {
            switch (value)
            {
                case null:
                    return "null";
                case string text:
                    return $"\"{EscapeJson(text)}\"";
                case bool flag:
                    return flag ? "true" : "false";
                case int intValue:
                    return intValue.ToString(CultureInfo.InvariantCulture);
                case long longValue:
                    return longValue.ToString(CultureInfo.InvariantCulture);
                case float floatValue:
                    return float.IsNaN(floatValue) || float.IsInfinity(floatValue)
                        ? "null"
                        : floatValue.ToString("G9", CultureInfo.InvariantCulture);
                case double doubleValue:
                    return double.IsNaN(doubleValue) || double.IsInfinity(doubleValue)
                        ? "null"
                        : doubleValue.ToString("G9", CultureInfo.InvariantCulture);
                case IDictionary dictionary:
                    return SerializeJsonDictionary(dictionary);
                case IEnumerable<string> strings:
                    return SerializeJsonArray(strings);
                case IEnumerable enumerable:
                    return SerializeJsonArray(enumerable);
                default:
                    return $"\"{EscapeJson(value.ToString())}\"";
            }
        }

        private static string SerializeJsonArray(IEnumerable<string> values)
        {
            StringBuilder builder = new();
            builder.Append('[');
            bool first = true;
            foreach (string value in values)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                builder.Append('"');
                builder.Append(EscapeJson(value ?? string.Empty));
                builder.Append('"');
                first = false;
            }

            builder.Append(']');
            return builder.ToString();
        }

        private static string SerializeJsonArray(IEnumerable values)
        {
            StringBuilder builder = new();
            builder.Append('[');
            bool first = true;
            foreach (object value in values)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                builder.Append(SerializeJsonValue(value));
                first = false;
            }

            builder.Append(']');
            return builder.ToString();
        }

        private static string SerializeJsonDictionary(IDictionary values)
        {
            StringBuilder builder = new();
            builder.Append('{');
            bool first = true;
            foreach (DictionaryEntry entry in values)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                builder.Append('"').Append(EscapeJson(entry.Key?.ToString() ?? string.Empty)).Append("\":");
                builder.Append(SerializeJsonValue(entry.Value));
                first = false;
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
        }

        public readonly struct SessionContext
        {
            public SessionContext(
                ExperimentSessionIdentity identity,
                string participantId,
                string sessionId,
                string sessionRoot,
                string dataRoot,
                string persistentRoot,
                string startedAtUtc,
                string questionnaireCode,
                string questionnaireCodeScheme,
                string[] conditionOrder,
                int roundsPerCondition)
            {
                Identity = identity;
                ParticipantId = participantId ?? string.Empty;
                SessionId = sessionId ?? string.Empty;
                SessionRoot = sessionRoot ?? string.Empty;
                DataRoot = dataRoot ?? string.Empty;
                PersistentRoot = persistentRoot ?? string.Empty;
                StartedAtUtc = startedAtUtc ?? string.Empty;
                QuestionnaireCode = questionnaireCode ?? string.Empty;
                QuestionnaireCodeScheme = questionnaireCodeScheme ?? string.Empty;
                ConditionOrder = conditionOrder ?? Array.Empty<string>();
                RoundsPerCondition = roundsPerCondition < 1 ? 1 : roundsPerCondition;
            }

            public ExperimentSessionIdentity Identity { get; }
            public string ParticipantId { get; }
            public string SessionId { get; }
            public string SessionRoot { get; }
            public string DataRoot { get; }
            public string PersistentRoot { get; }
            public string StartedAtUtc { get; }
            public string QuestionnaireCode { get; }
            public string QuestionnaireCodeScheme { get; }
            public string[] ConditionOrder { get; }
            public int RoundsPerCondition { get; }
        }

        [Serializable]
        private sealed class SessionExportInfoDocument
        {
            public string questionnaire_code = "";
            public string questionnaire_code_scheme = "";
            public string[] condition_order = Array.Empty<string>();
            public string[] condition_order_ids = Array.Empty<string>();
        }
    }
}
