using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autonomy.Domain;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    public static class ExperimentSessionIdHistoryStore
    {
        public const int CurrentHistorySchemaVersion = 3;
        public const int CurrentSavedExitCheckpointSchemaVersion = 2;
        public const string SavedExitIncompleteConditionRestartReason = "saved_exit_incomplete_condition_restart";
        public const string CreatedStatus = "created";
        public const string ActiveStatus = "active";
        public const string StartedStatus = ActiveStatus;
        public const string LegacyStartedStatus = "started";
        public const string CompletedStatus = "completed";
        public const string AbandonedStatus = "abandoned";
        public const string RestartedStatus = "restarted";
        public const string SavedExitStatus = "saved_exit";
        public const string ClosedWithoutCompletionStatus = "closed_without_completion";
        public const string SupersededAfterSavedExitStatus = "superseded_after_saved_exit";
        private const string FolderName = "ExperimentData";
        private const string FileName = "session_id_history.json";
        private const int MaxCodeGenerationAttempts = 32;
        private static string s_defaultPathOverride;
#if UNITY_INCLUDE_TESTS || UNITY_EDITOR
        private static bool s_forceSaveFailureForTests;
        private static Func<IReadOnlyList<string>, string> s_questionnaireCodeGeneratorForTests;
#endif

        public static string DefaultPath => !string.IsNullOrWhiteSpace(s_defaultPathOverride)
            ? s_defaultPathOverride
            : Path.Combine(Application.persistentDataPath, FolderName, FileName);

#if UNITY_INCLUDE_TESTS || UNITY_EDITOR
        public static void OverrideDefaultPathForTests(string path)
        {
            s_defaultPathOverride = path;
        }

        public static void ForceSaveFailureForTests(bool forceFailure)
        {
            s_forceSaveFailureForTests = forceFailure;
        }

        public static void OverrideQuestionnaireCodeGeneratorForTests(
            Func<IReadOnlyList<string>, string> generator)
        {
            s_questionnaireCodeGeneratorForTests = generator;
        }
#endif

        public static IReadOnlyList<ExperimentSessionIdHistoryEntry> LoadRecent(int maxCount = 50)
        {
            List<ExperimentSessionIdHistoryEntry> entries = Load(DefaultPath);
            SortNewestFirst(entries);
            if (maxCount > 0 && entries.Count > maxCount)
            {
                entries.RemoveRange(maxCount, entries.Count - maxCount);
            }

            return entries;
        }

        public static ExperimentSessionHistoryPage ResolvePage(int totalEntries, int requestedPageIndex, int entriesPerPage)
        {
            return new ExperimentSessionHistoryPage(totalEntries, requestedPageIndex, entriesPerPage);
        }

        public static ExperimentSessionIdHistoryEntry RegisterSessionStarted(
            string userId,
            string sessionId,
            string startedAtIso,
            IReadOnlyList<string> conditionOrderIds)
        {
            if (!QuestionnaireCodeCodec.TryGetPrefixForOrder(conditionOrderIds, out _))
            {
                throw new ArgumentException(
                    "A new session requires an exact supported condition order before questionnaire code generation.",
                    nameof(conditionOrderIds));
            }

            string path = DefaultPath;
            List<ExperimentSessionIdHistoryEntry> entries;
            try
            {
                entries = LoadForRegistration(path);
            }
            catch (Exception ex)
            {
                LogHistoryStoreEvent(
                    "p46d03_history_register_load_failed",
                    path,
                    0,
                    "load_failed:" + ex.GetType().Name,
                    false,
                    sessionId,
                    string.Empty,
                    StartedStatus);
                throw;
            }

            if (entries.Exists(entry => entry != null &&
                string.Equals(entry.session_id, sessionId ?? string.Empty, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Session identity '{sessionId}' is already registered and cannot be overwritten.");
            }

            string questionnaireCode = GenerateUniqueCode(entries, conditionOrderIds);
            int attemptIndex = ResolveNextAttemptIndex(entries, userId);
            ExperimentSessionIdHistoryEntry entry = ExperimentSessionIdHistoryEntry.Create(
                userId,
                sessionId,
                questionnaireCode,
                startedAtIso,
                StartedStatus,
                attemptIndex,
                QuestionnaireCodeCodec.Scheme,
                conditionOrderIds);

            try
            {
                Upsert(entries, entry);
                Save(path, entries);
                LogHistoryStoreEvent(
                    "p46d_history_session_registered",
                    path,
                    entries.Count,
                    "registered",
                    true,
                    entry.session_id,
                    entry.questionnaire_code,
                    entry.status);
            }
            catch (Exception ex)
            {
                LogHistoryStoreEvent(
                    "p46d03_history_register_save_failed",
                    path,
                    entries.Count,
                    "save_failed:" + ex.GetType().Name,
                    false,
                    entry.session_id,
                    entry.questionnaire_code,
                    entry.status);
                throw;
            }

            return entry;
        }

        public static void UpdateSessionStatus(string sessionId, string status)
        {
            TryUpdateSessionStatus(sessionId, status, "legacy_update_session_status_call", out _);
        }

        public static bool TryUpdateSessionStatus(
            string sessionId,
            string requestedStatus,
            string reason,
            out string resultingStatus)
        {
            resultingStatus = string.Empty;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            string path = DefaultPath;
            try
            {
                List<ExperimentSessionIdHistoryEntry> entries = Load(path);
                bool changed = false;
                bool found = false;
                bool transitionAccepted = false;
                string updatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                foreach (ExperimentSessionIdHistoryEntry entry in entries)
                {
                    if (entry == null || !string.Equals(entry.session_id, sessionId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    found = true;
                    if (!TryTransitionSessionStatus(
                            entry.status,
                            requestedStatus,
                            reason,
                            out resultingStatus,
                            out string transitionError))
                    {
                        Debug.LogError(
                            $"[ExperimentSessionIdHistoryStore] session_status_transition_rejected | session_id={sessionId} current_status={entry.status ?? string.Empty} requested_status={requestedStatus ?? string.Empty} reason={reason ?? string.Empty} error_reason={transitionError}");
                        break;
                    }

                    transitionAccepted = true;
                    if (!string.Equals(entry.status, resultingStatus, StringComparison.Ordinal))
                    {
                        entry.status = resultingStatus;
                        entry.last_updated_at_iso = updatedAt;
                        if (string.Equals(resultingStatus, CompletedStatus, StringComparison.Ordinal) &&
                            string.IsNullOrWhiteSpace(entry.completed_at_iso))
                        {
                            entry.completed_at_iso = updatedAt;
                        }

                        changed = true;
                    }
                    break;
                }

                if (changed)
                {
                    Save(path, entries);
                }

                LogHistoryStoreEvent(
                    "p46d_history_status_update_result",
                    path,
                    entries.Count,
                    changed ? "updated" : transitionAccepted ? "unchanged" : found ? "transition_rejected" : "session_not_found",
                    transitionAccepted,
                    sessionId,
                    string.Empty,
                    requestedStatus);
                return transitionAccepted;
            }
            catch (Exception ex)
            {
                LogHistoryStoreEvent(
                    "p46d03_history_status_update_failed_fail_soft",
                    path,
                    0,
                    "update_failed:" + ex.GetType().Name,
                    false,
                    sessionId,
                    string.Empty,
                    requestedStatus);
                return false;
            }
        }

        public static bool TryTransitionSessionStatus(
            string currentStatus,
            string requestedStatus,
            string reason,
            out string resultingStatus,
            out string errorReason)
        {
            string current = NormalizeStatus(currentStatus);
            string requested = NormalizeStatus(requestedStatus);
            resultingStatus = current;
            errorReason = string.Empty;
            if (string.IsNullOrWhiteSpace(requested))
            {
                errorReason = "requested_status_missing";
                return false;
            }

            if (string.Equals(current, requested, StringComparison.Ordinal))
            {
                resultingStatus = requested;
                return true;
            }

            bool allowed = current switch
            {
                CreatedStatus => string.Equals(requested, ActiveStatus, StringComparison.Ordinal) ||
                    IsAbandoningStatus(requested),
                ActiveStatus => string.Equals(requested, SavedExitStatus, StringComparison.Ordinal) ||
                    string.Equals(requested, CompletedStatus, StringComparison.Ordinal) ||
                    IsAbandoningStatus(requested),
                SavedExitStatus => string.Equals(requested, ActiveStatus, StringComparison.Ordinal) ||
                    string.Equals(requested, CompletedStatus, StringComparison.Ordinal) ||
                    string.Equals(requested, SupersededAfterSavedExitStatus, StringComparison.Ordinal) ||
                    IsAbandoningStatus(requested),
                _ => false
            };
            if (!allowed)
            {
                errorReason = IsTerminalStatus(current)
                    ? "current_status_is_terminal"
                    : "transition_not_allowed";
                return false;
            }

            resultingStatus = requested;
            return true;
        }

        public static bool IsTerminalStatus(string status)
        {
            string normalized = NormalizeStatus(status);
            return string.Equals(normalized, CompletedStatus, StringComparison.Ordinal) ||
                string.Equals(normalized, AbandonedStatus, StringComparison.Ordinal) ||
                string.Equals(normalized, RestartedStatus, StringComparison.Ordinal) ||
                string.Equals(normalized, ClosedWithoutCompletionStatus, StringComparison.Ordinal) ||
                string.Equals(normalized, SupersededAfterSavedExitStatus, StringComparison.Ordinal);
        }

        private static bool IsAbandoningStatus(string status)
        {
            return string.Equals(status, AbandonedStatus, StringComparison.Ordinal) ||
                string.Equals(status, RestartedStatus, StringComparison.Ordinal) ||
                string.Equals(status, ClosedWithoutCompletionStatus, StringComparison.Ordinal);
        }

        private static string NormalizeStatus(string status)
        {
            string normalized = status?.Trim() ?? string.Empty;
            if (string.Equals(normalized, LegacyStartedStatus, StringComparison.Ordinal))
            {
                return ActiveStatus;
            }

            return normalized;
        }

        public static void MarkQuestionnaireCodeAcknowledged(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            string path = DefaultPath;
            try
            {
                List<ExperimentSessionIdHistoryEntry> entries = Load(path);
                bool changed = false;
                string acknowledgedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                string questionnaireCode = string.Empty;
                string status = string.Empty;
                foreach (ExperimentSessionIdHistoryEntry entry in entries)
                {
                    if (entry == null || !string.Equals(entry.session_id, sessionId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    entry.questionnaire_code_acknowledged = true;
                    entry.questionnaire_code_acknowledged_at_iso = acknowledgedAt;
                    entry.last_updated_at_iso = acknowledgedAt;
                    questionnaireCode = entry.questionnaire_code ?? string.Empty;
                    status = entry.status ?? string.Empty;
                    changed = true;
                    break;
                }

                if (changed)
                {
                    Save(path, entries);
                }

                LogHistoryStoreEvent(
                    "p46g_questionnaire_code_acknowledgement_update_result",
                    path,
                    entries.Count,
                    changed ? "updated" : "session_not_found",
                    changed,
                    sessionId,
                    questionnaireCode,
                    status);
            }
            catch (Exception ex)
            {
                LogHistoryStoreEvent(
                    "p46g_questionnaire_code_acknowledgement_update_failed_fail_soft",
                    path,
                    0,
                    "update_failed:" + ex.GetType().Name,
                    false,
                    sessionId,
                    string.Empty,
                    string.Empty);
            }
        }

        public static bool UpdateSavedExitCheckpoint(
            string sessionId,
            string conditionId,
            int visiblePrueba,
            int roundIndex,
            string conditionOrder,
            string resumePolicy,
            IReadOnlyList<string> conditionOrderIds = null,
            int conditionOrderIndex = -1,
            int internalTrialAttemptIndex = 0,
            string partialTrialCloseReason = SavedExitIncompleteConditionRestartReason)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            string path = DefaultPath;
            try
            {
                List<ExperimentSessionIdHistoryEntry> entries = Load(path);
                bool changed = false;
                string updatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                foreach (ExperimentSessionIdHistoryEntry entry in entries)
                {
                    if (entry == null || !string.Equals(entry.session_id, sessionId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string currentStatus = NormalizeStatus(entry.status);
                    if (!string.Equals(currentStatus, ActiveStatus, StringComparison.Ordinal) &&
                        !string.Equals(currentStatus, SavedExitStatus, StringComparison.Ordinal))
                    {
                        Debug.LogError(
                            $"[ExperimentSessionIdHistoryStore] saved_exit_checkpoint_update_rejected | session_id={sessionId} current_status={entry.status ?? string.Empty} reason=status_not_checkpointable");
                        break;
                    }

                    List<string> persistedIdentityOrder = NormalizeConditionOrder(entry.condition_order_ids);
                    if (persistedIdentityOrder.Count == 0)
                    {
                        persistedIdentityOrder = ParseLegacyConditionOrder(entry.condition_order);
                    }

                    List<string> suppliedOrder = NormalizeConditionOrder(conditionOrderIds);
                    if (suppliedOrder.Count == 0)
                    {
                        suppliedOrder = ParseLegacyConditionOrder(conditionOrder);
                    }

                    if (persistedIdentityOrder.Count == 0 ||
                        suppliedOrder.Count == 0 ||
                        !OrdersMatch(persistedIdentityOrder, suppliedOrder))
                    {
                        Debug.LogError(
                            $"[ExperimentSessionIdHistoryStore] saved_exit_checkpoint_update_rejected | session_id={sessionId} reason=condition_order_identity_mismatch persisted_order={string.Join(",", persistedIdentityOrder)} supplied_order={string.Join(",", suppliedOrder)}");
                        break;
                    }

                    int persistedConditionIndex = persistedIdentityOrder.FindIndex(id =>
                        string.Equals(id, conditionId, StringComparison.Ordinal));
                    if (persistedConditionIndex < 0 ||
                        (conditionOrderIndex >= 0 && conditionOrderIndex != persistedConditionIndex) ||
                        visiblePrueba != persistedConditionIndex + 1)
                    {
                        Debug.LogError(
                            $"[ExperimentSessionIdHistoryStore] saved_exit_checkpoint_update_rejected | session_id={sessionId} reason=checkpoint_position_identity_mismatch condition_id={conditionId ?? string.Empty} requested_index={conditionOrderIndex} persisted_index={persistedConditionIndex} visible_prueba={visiblePrueba}");
                        break;
                    }

                    int resolvedConditionOrderIndex = conditionOrderIndex;
                    if (resolvedConditionOrderIndex < 0)
                    {
                        resolvedConditionOrderIndex = persistedConditionIndex;
                    }

                    entry.saved_exit_checkpoint_schema_version = CurrentSavedExitCheckpointSchemaVersion;
                    entry.saved_condition_id = conditionId ?? string.Empty;
                    entry.saved_visible_prueba = Math.Max(1, visiblePrueba);
                    entry.saved_round_index = Math.Max(1, roundIndex);
                    entry.saved_condition_order_index = resolvedConditionOrderIndex;
                    entry.resume_policy = string.IsNullOrWhiteSpace(resumePolicy) ? "condition_start" : resumePolicy.Trim();
                    entry.internal_trial_attempt_index = Math.Max(0, internalTrialAttemptIndex);
                    entry.partial_trial_close_reason = partialTrialCloseReason?.Trim() ?? string.Empty;
                    entry.last_updated_at_iso = updatedAt;
                    changed = true;
                    break;
                }

                if (changed)
                {
                    Save(path, entries);
                }

                LogHistoryStoreEvent(
                    "p46d_saved_exit_checkpoint_update_result",
                    path,
                    entries.Count,
                    changed ? "updated" : "session_not_found",
                    changed,
                    sessionId,
                    string.Empty,
                    ExperimentSessionIdHistoryStore.SavedExitStatus);
                return changed;
            }
            catch (Exception ex)
            {
                LogHistoryStoreEvent(
                    "p46d_saved_exit_checkpoint_update_failed_fail_soft",
                    path,
                    0,
                    "update_failed:" + ex.GetType().Name,
                    false,
                    sessionId,
                    string.Empty,
                    ExperimentSessionIdHistoryStore.SavedExitStatus);
                return false;
            }
        }

        public static bool TryResolveSavedExitCheckpoint(
            ExperimentSessionIdHistoryEntry entry,
            IReadOnlyList<string> sessionMetadataOrder,
            IReadOnlyList<string> requestedOrder,
            IReadOnlyList<string> participantAssignmentOrder,
            out ExperimentSavedExitCheckpointResolution resolution,
            out string reason)
        {
            resolution = null;
            reason = string.Empty;
            if (entry == null)
            {
                reason = "checkpoint_missing";
                return false;
            }

            List<string> checkpointV2Order = NormalizeConditionOrder(entry.condition_order_ids);
            bool hasCheckpointV2Order = entry.condition_order_ids != null && entry.condition_order_ids.Count > 0;
            if (hasCheckpointV2Order && checkpointV2Order.Count == 0)
            {
                reason = "condition_order_ids_invalid";
                return false;
            }

            List<string> legacyOrder = ParseLegacyConditionOrder(entry.condition_order);
            bool hasLegacyOrder = !string.IsNullOrWhiteSpace(entry.condition_order);
            if (hasLegacyOrder && legacyOrder.Count == 0)
            {
                reason = "legacy_condition_order_invalid";
                return false;
            }

            List<string> metadataOrder = NormalizeConditionOrder(sessionMetadataOrder);
            List<string> suppliedOrder = NormalizeConditionOrder(requestedOrder);
            List<string> fallbackOrder = NormalizeConditionOrder(participantAssignmentOrder);
            List<string> authoritativeOrder;
            string orderSource;
            bool compatibilityFallback;
            if (checkpointV2Order.Count > 0)
            {
                authoritativeOrder = checkpointV2Order;
                orderSource = "checkpoint_v2_condition_order_ids";
                compatibilityFallback = false;
            }
            else if (legacyOrder.Count > 0)
            {
                authoritativeOrder = legacyOrder;
                orderSource = "legacy_checkpoint_condition_order";
                compatibilityFallback = true;
            }
            else if (metadataOrder.Count > 0)
            {
                authoritativeOrder = metadataOrder;
                orderSource = "session_export_info_condition_order";
                compatibilityFallback = true;
            }
            else if (suppliedOrder.Count > 0)
            {
                authoritativeOrder = suppliedOrder;
                orderSource = "caller_provided_legacy_order";
                compatibilityFallback = true;
            }
            else if (fallbackOrder.Count > 0)
            {
                authoritativeOrder = fallbackOrder;
                orderSource = "participant_assignment_store";
                compatibilityFallback = true;
            }
            else
            {
                reason = "condition_order_unrecoverable";
                return false;
            }

            if (metadataOrder.Count > 0 && !OrdersMatch(authoritativeOrder, metadataOrder))
            {
                reason = "checkpoint_session_metadata_order_mismatch";
                return false;
            }

            if (suppliedOrder.Count > 0 && !OrdersMatch(authoritativeOrder, suppliedOrder))
            {
                reason = "checkpoint_requested_order_mismatch";
                return false;
            }

            if (checkpointV2Order.Count > 0 && legacyOrder.Count > 0 && !OrdersMatch(checkpointV2Order, legacyOrder))
            {
                reason = "checkpoint_v2_legacy_order_mismatch";
                return false;
            }

            string savedConditionId = entry.saved_condition_id ?? string.Empty;
            int derivedIndex = authoritativeOrder.FindIndex(id =>
                string.Equals(id, savedConditionId, StringComparison.Ordinal));
            if (derivedIndex < 0)
            {
                reason = "saved_condition_not_in_condition_order";
                return false;
            }

            if (entry.saved_exit_checkpoint_schema_version >= CurrentSavedExitCheckpointSchemaVersion &&
                entry.saved_condition_order_index != derivedIndex)
            {
                reason = "saved_condition_order_index_mismatch";
                return false;
            }

            int visiblePrueba = derivedIndex + 1;
            if (entry.saved_visible_prueba > 0 && entry.saved_visible_prueba != visiblePrueba)
            {
                reason = "saved_visible_prueba_mismatch";
                return false;
            }

            string resumePolicy = string.IsNullOrWhiteSpace(entry.resume_policy)
                ? "condition_start"
                : entry.resume_policy.Trim();
            if (!string.Equals(resumePolicy, "condition_start", StringComparison.Ordinal))
            {
                reason = "unsupported_resume_policy";
                return false;
            }

            resolution = new ExperimentSavedExitCheckpointResolution(
                authoritativeOrder,
                savedConditionId,
                derivedIndex,
                visiblePrueba,
                savedRoundIndex: Math.Max(1, entry.saved_round_index),
                restoredRoundIndex: 1,
                resumePolicy,
                Math.Max(0, entry.internal_trial_attempt_index),
                entry.saved_exit_checkpoint_schema_version >= CurrentSavedExitCheckpointSchemaVersion
                    ? entry.partial_trial_close_reason ?? string.Empty
                    : string.IsNullOrWhiteSpace(entry.partial_trial_close_reason)
                        ? SavedExitIncompleteConditionRestartReason
                        : entry.partial_trial_close_reason,
                orderSource,
                compatibilityFallback);
            reason = compatibilityFallback ? "valid_compatibility_fallback" : "valid_checkpoint_v2";
            return true;
        }

        public static List<string> ParseLegacyConditionOrder(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new List<string>();
            }

            string normalized = value.Replace("->", ",").Replace("|", ",").Replace(";", ",");
            var result = new List<string>();
            foreach (string rawToken in normalized.Split(','))
            {
                string token = rawToken?.Trim() ?? string.Empty;
                string conditionId = token.ToUpperInvariant() switch
                {
                    "C00" => ExperimentCompensatedConditionOrder.C00,
                    "C10" => ExperimentCompensatedConditionOrder.C10,
                    "C11" => ExperimentCompensatedConditionOrder.C11,
                    _ => token
                };
                if (!string.IsNullOrWhiteSpace(conditionId))
                {
                    result.Add(conditionId);
                }
            }

            return ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(result)
                ? result
                : new List<string>();
        }

        private static List<string> NormalizeConditionOrder(IReadOnlyList<string> conditionOrder)
        {
            if (!ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(conditionOrder))
            {
                return new List<string>();
            }

            return conditionOrder.Select(id => id?.Trim() ?? string.Empty).ToList();
        }

        private static bool OrdersMatch(IReadOnlyList<string> first, IReadOnlyList<string> second)
        {
            return first != null && second != null && first.SequenceEqual(second, StringComparer.Ordinal);
        }

        public static List<ExperimentSessionIdHistoryEntry> Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                LogHistoryStoreEvent(
                    "p46d_history_load_result",
                    path,
                    0,
                    string.IsNullOrWhiteSpace(path) ? "path_empty" : "file_missing",
                    true);
                return new List<ExperimentSessionIdHistoryEntry>();
            }

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    LogHistoryStoreEvent("p46d_history_load_result", path, 0, "file_empty", true);
                    return new List<ExperimentSessionIdHistoryEntry>();
                }

                ExperimentSessionIdHistoryDocument document = JsonUtility.FromJson<ExperimentSessionIdHistoryDocument>(json);
                List<ExperimentSessionIdHistoryEntry> entries = document?.sessions != null
                    ? new List<ExperimentSessionIdHistoryEntry>(document.sessions.FindAll(entry => entry != null))
                    : new List<ExperimentSessionIdHistoryEntry>();
                LogHistoryStoreEvent("p46d_history_load_result", path, entries.Count, "loaded", true);
                return entries;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ExperimentSessionIdHistoryStore] Could not read session history '{path}': {ex.Message}");
                LogHistoryStoreEvent("p46d_history_load_result", path, 0, "read_failed:" + ex.GetType().Name, false);
                return new List<ExperimentSessionIdHistoryEntry>();
            }
        }

        private static List<ExperimentSessionIdHistoryEntry> LoadForRegistration(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Session history path is required.", nameof(path));
            }

            if (!File.Exists(path))
            {
                return new List<ExperimentSessionIdHistoryEntry>();
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<ExperimentSessionIdHistoryEntry>();
            }

            ExperimentSessionIdHistoryDocument document = JsonUtility.FromJson<ExperimentSessionIdHistoryDocument>(json);
            if (document == null || document.sessions == null)
            {
                throw new InvalidDataException("Session history JSON could not be deserialized for uniqueness checking.");
            }

            return new List<ExperimentSessionIdHistoryEntry>(document.sessions.FindAll(entry => entry != null));
        }

        public static void Save(string path, List<ExperimentSessionIdHistoryEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

#if UNITY_INCLUDE_TESTS || UNITY_EDITOR
            if (s_forceSaveFailureForTests)
            {
                throw new IOException("p46d03_forced_history_save_failure");
            }
#endif

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            List<ExperimentSessionIdHistoryEntry> safeEntries = entries ?? new List<ExperimentSessionIdHistoryEntry>();
            SortNewestFirst(safeEntries);
            var document = new ExperimentSessionIdHistoryDocument
            {
                schema_version = CurrentHistorySchemaVersion,
                updated_at_iso = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                sessions = safeEntries
            };
            File.WriteAllText(path, JsonUtility.ToJson(document, prettyPrint: true) + Environment.NewLine, Encoding.UTF8);
            LogHistoryStoreEvent("p46d_history_save_result", path, safeEntries.Count, "saved", true);
        }

        private static void LogHistoryStoreEvent(
            string eventType,
            string path,
            int entryCount,
            string result,
            bool success,
            string sessionId = "",
            string questionnaireCode = "",
            string status = "")
        {
            string message =
                $"[P46D-03] {eventType} | history_path={path ?? string.Empty} " +
                $"history_file_exists={!string.IsNullOrWhiteSpace(path) && File.Exists(path)} " +
                $"entry_count={entryCount} result={result ?? string.Empty} success={success} " +
                $"session_id={sessionId ?? string.Empty} questionnaire_code={questionnaireCode ?? string.Empty} " +
                $"status={status ?? string.Empty} persistent_data_path={Application.persistentDataPath} " +
                $"platform={Application.platform}";
            if (success)
            {
                Debug.Log(message);
            }
            else
            {
                Debug.LogWarning(message);
            }
        }

        private static string GenerateUniqueCode(
            List<ExperimentSessionIdHistoryEntry> entries,
            IReadOnlyList<string> conditionOrderIds)
        {
            for (int i = 0; i < MaxCodeGenerationAttempts; i++)
            {
                string code;
#if UNITY_INCLUDE_TESTS || UNITY_EDITOR
                code = s_questionnaireCodeGeneratorForTests != null
                    ? s_questionnaireCodeGeneratorForTests(conditionOrderIds)
                    : QuestionnaireCodeCodec.GenerateForOrder(conditionOrderIds);
#else
                code = QuestionnaireCodeCodec.GenerateForOrder(conditionOrderIds);
#endif
                if (!QuestionnaireCodeCodec.TryDecodeOrder(
                        code,
                        out string[] decodedOrder,
                        out _,
                        out string validationError) ||
                    !decodedOrder.SequenceEqual(conditionOrderIds, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Generated questionnaire code failed validation or encoded a different order: " + validationError);
                }

                if (!ContainsQuestionnaireCode(entries, code))
                {
                    return code;
                }
            }

            Debug.LogError(
                $"[ExperimentSessionIdHistoryStore] questionnaire_code_generation_failed | " +
                $"reason=collision_retry_limit_exceeded attempts={MaxCodeGenerationAttempts} " +
                $"scheme={QuestionnaireCodeCodec.Scheme} order={ExperimentCompensatedConditionOrder.FormatOrder(conditionOrderIds)}");
            throw new InvalidOperationException(
                $"Could not generate a unique questionnaire code after {MaxCodeGenerationAttempts} attempts.");
        }

        private static bool ContainsQuestionnaireCode(List<ExperimentSessionIdHistoryEntry> entries, string code)
        {
            if (entries == null || string.IsNullOrWhiteSpace(code))
            {
                return false;
            }

            foreach (ExperimentSessionIdHistoryEntry entry in entries)
            {
                if (entry != null && string.Equals(entry.questionnaire_code, code, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static int ResolveNextAttemptIndex(List<ExperimentSessionIdHistoryEntry> entries, string userId)
        {
            int max = 0;
            if (entries == null)
            {
                return 1;
            }

            foreach (ExperimentSessionIdHistoryEntry entry in entries)
            {
                if (entry == null || !string.Equals(entry.user_id, userId ?? string.Empty, StringComparison.Ordinal))
                {
                    continue;
                }

                max = Math.Max(max, entry.attempt_index);
            }

            return max + 1;
        }

        private static void Upsert(List<ExperimentSessionIdHistoryEntry> entries, ExperimentSessionIdHistoryEntry newEntry)
        {
            if (entries == null || newEntry == null)
            {
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] != null && string.Equals(entries[i].session_id, newEntry.session_id, StringComparison.Ordinal))
                {
                    entries[i] = newEntry;
                    return;
                }
            }

            entries.Add(newEntry);
        }

        private static void SortNewestFirst(List<ExperimentSessionIdHistoryEntry> entries)
        {
            entries?.Sort((left, right) =>
                ParseIso(right?.started_at_iso).CompareTo(ParseIso(left?.started_at_iso)));
        }

        private static DateTime ParseIso(string value)
        {
            return DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTime parsed)
                ? parsed
                : DateTime.MinValue;
        }
    }

    public readonly struct ExperimentSessionHistoryPage
    {
        public ExperimentSessionHistoryPage(int totalEntries, int requestedPageIndex, int entriesPerPage)
        {
            TotalEntries = Math.Max(0, totalEntries);
            EntriesPerPage = Math.Max(1, entriesPerPage);
            PageCount = TotalEntries == 0
                ? 0
                : (TotalEntries + EntriesPerPage - 1) / EntriesPerPage;

            if (PageCount == 0)
            {
                PageIndex = 0;
                StartIndex = 0;
                EndExclusive = 0;
            }
            else
            {
                PageIndex = Math.Max(0, Math.Min(requestedPageIndex, PageCount - 1));
                StartIndex = PageIndex * EntriesPerPage;
                EndExclusive = Math.Min(StartIndex + EntriesPerPage, TotalEntries);
            }
        }

        public int TotalEntries { get; }
        public int EntriesPerPage { get; }
        public int PageIndex { get; }
        public int PageCount { get; }
        public int StartIndex { get; }
        public int EndExclusive { get; }
        public bool HasPreviousPage => PageIndex > 0;
        public bool HasNextPage => PageIndex + 1 < PageCount;
    }

    [Serializable]
    public sealed class ExperimentSessionIdHistoryDocument
    {
        public int schema_version = 1;
        public string updated_at_iso = "";
        public List<ExperimentSessionIdHistoryEntry> sessions = new();
    }

    [Serializable]
    public sealed class ExperimentSessionIdHistoryEntry
    {
        public string full_id = "";
        public string session_id = "";
        public string user_id = "";
        public string questionnaire_code = "";
        public string questionnaire_code_scheme = "";
        public string started_at_iso = "";
        public string started_at_display = "";
        public string status = "";
        public int attempt_index;
        public string last_updated_at_iso = "";
        public string completed_at_iso = "";
        public string saved_condition_id = "";
        public int saved_visible_prueba;
        public int saved_round_index;
        public int saved_exit_checkpoint_schema_version;
        public List<string> condition_order_ids = new();
        public int saved_condition_order_index = -1;
        public string condition_order = "";
        public string resume_policy = "";
        public int internal_trial_attempt_index;
        public string partial_trial_close_reason = "";
        public bool questionnaire_code_acknowledged;
        public string questionnaire_code_acknowledged_at_iso = "";

        public static ExperimentSessionIdHistoryEntry Create(
            string userId,
            string sessionId,
            string questionnaireCode,
            string startedAtIso,
            string status,
            int attemptIndex,
            string questionnaireCodeScheme = "",
            IReadOnlyList<string> conditionOrderIds = null)
        {
            string safeStartedAt = string.IsNullOrWhiteSpace(startedAtIso)
                ? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                : startedAtIso;
            string display = DateTime.TryParse(
                safeStartedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTime parsed)
                ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : safeStartedAt;

            return new ExperimentSessionIdHistoryEntry
            {
                full_id = string.IsNullOrWhiteSpace(sessionId) ? userId ?? string.Empty : sessionId,
                session_id = sessionId ?? string.Empty,
                user_id = userId ?? string.Empty,
                questionnaire_code = questionnaireCode ?? string.Empty,
                questionnaire_code_scheme = questionnaireCodeScheme ?? string.Empty,
                started_at_iso = safeStartedAt,
                started_at_display = display,
                status = string.IsNullOrWhiteSpace(status) ? ExperimentSessionIdHistoryStore.StartedStatus : status,
                attempt_index = Math.Max(1, attemptIndex),
                last_updated_at_iso = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                saved_visible_prueba = 1,
                saved_round_index = 1,
                condition_order_ids = conditionOrderIds == null
                    ? new List<string>()
                    : new List<string>(conditionOrderIds),
                saved_condition_order_index = -1,
                condition_order = conditionOrderIds == null
                    ? string.Empty
                    : string.Join(",", conditionOrderIds),
                resume_policy = "condition_start"
            };
        }
    }

    public sealed class ExperimentSavedExitCheckpointResolution
    {
        public ExperimentSavedExitCheckpointResolution(
            IReadOnlyList<string> conditionOrderIds,
            string conditionId,
            int conditionOrderIndex,
            int visiblePruebaNumber,
            int savedRoundIndex,
            int restoredRoundIndex,
            string resumePolicy,
            int internalTrialAttemptIndex,
            string partialTrialCloseReason,
            string orderSource,
            bool compatibilityFallback)
        {
            ConditionOrderIds = conditionOrderIds == null ? Array.Empty<string>() : conditionOrderIds.ToArray();
            ConditionId = conditionId ?? string.Empty;
            ConditionOrderIndex = conditionOrderIndex;
            VisiblePruebaNumber = visiblePruebaNumber;
            SavedRoundIndex = savedRoundIndex;
            RestoredRoundIndex = restoredRoundIndex;
            ResumePolicy = resumePolicy ?? string.Empty;
            InternalTrialAttemptIndex = internalTrialAttemptIndex;
            PartialTrialCloseReason = partialTrialCloseReason ?? string.Empty;
            OrderSource = orderSource ?? string.Empty;
            CompatibilityFallback = compatibilityFallback;
        }

        public IReadOnlyList<string> ConditionOrderIds { get; }
        public string ConditionId { get; }
        public int ConditionOrderIndex { get; }
        public int VisiblePruebaNumber { get; }
        public int SavedRoundIndex { get; }
        public int RestoredRoundIndex { get; }
        public string ResumePolicy { get; }
        public int InternalTrialAttemptIndex { get; }
        public string PartialTrialCloseReason { get; }
        public string OrderSource { get; }
        public bool CompatibilityFallback { get; }
    }
}
