using System;
using System.Collections.Generic;
using Autonomy.Domain;

namespace Autonomy.UnityIntegration
{
    public static class ExperimentSavedExitResumePromptState
    {
        public static bool TryFindPendingSession(
            IReadOnlyList<ExperimentSessionIdHistoryEntry> entries,
            out ExperimentSessionIdHistoryEntry pending)
        {
            pending = null;
            if (entries == null)
            {
                return false;
            }

            foreach (ExperimentSessionIdHistoryEntry entry in entries)
            {
                if (entry == null || !IsPendingPromptStatus(entry.status))
                {
                    continue;
                }

                pending = entry;
                return true;
            }

            return false;
        }

        public static bool IsPendingPromptStatus(string status)
        {
            return string.Equals(status, ExperimentSessionIdHistoryStore.SavedExitStatus, StringComparison.Ordinal) ||
                string.Equals(status, ExperimentSessionIdHistoryStore.ActiveStatus, StringComparison.Ordinal) ||
                string.Equals(status, ExperimentSessionIdHistoryStore.LegacyStartedStatus, StringComparison.Ordinal);
        }

        public static string ToParticipantDisplayStatus(string status)
        {
            if (string.Equals(status, ExperimentSessionIdHistoryStore.SavedExitStatus, StringComparison.Ordinal))
            {
                return "Sesion guardada sin finalizar";
            }

            if (string.Equals(status, ExperimentSessionIdHistoryStore.ActiveStatus, StringComparison.Ordinal) ||
                string.Equals(status, ExperimentSessionIdHistoryStore.LegacyStartedStatus, StringComparison.Ordinal))
            {
                return "Sesion iniciada sin finalizar";
            }

            if (string.Equals(status, ExperimentSessionIdHistoryStore.ClosedWithoutCompletionStatus, StringComparison.Ordinal))
            {
                return "Sesion cerrada sin finalizar";
            }

            if (string.Equals(status, ExperimentSessionIdHistoryStore.RestartedStatus, StringComparison.Ordinal))
            {
                return "Sesion reiniciada";
            }

            if (string.Equals(status, ExperimentSessionIdHistoryStore.SupersededAfterSavedExitStatus, StringComparison.Ordinal))
            {
                return "Sesion guardada sustituida por un nuevo intento";
            }

            if (string.Equals(status, ExperimentSessionIdHistoryStore.AbandonedStatus, StringComparison.Ordinal))
            {
                return "Sesion abandonada";
            }

            return string.IsNullOrWhiteSpace(status) ? "Sesion pendiente" : status.Trim();
        }

        public static int ResolveVisiblePrueba(ExperimentSessionIdHistoryEntry entry)
        {
            if (entry == null || entry.saved_visible_prueba <= 0)
            {
                return 1;
            }

            return entry.saved_visible_prueba;
        }

        public static string BuildResumeButtonLabel(ExperimentSessionIdHistoryEntry entry)
        {
            return $"Continuar desde Prueba {ResolveVisiblePrueba(entry)}";
        }

        public static string BuildCheckpointDescription(ExperimentSessionIdHistoryEntry entry)
        {
            int prueba = ResolveVisiblePrueba(entry);
            return $"Prueba {prueba}";
        }

        public static bool HasValidCheckpoint(ExperimentSessionIdHistoryEntry entry)
        {
            bool basicValid = entry != null &&
                !string.IsNullOrWhiteSpace(entry.saved_condition_id) &&
                entry.saved_visible_prueba > 0 &&
                string.Equals(entry.resume_policy, "condition_start", StringComparison.Ordinal);
            if (!basicValid || entry.saved_exit_checkpoint_schema_version < ExperimentSessionIdHistoryStore.CurrentSavedExitCheckpointSchemaVersion)
            {
                return basicValid;
            }

            return ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(entry.condition_order_ids) &&
                entry.saved_condition_order_index >= 0 &&
                entry.saved_condition_order_index < entry.condition_order_ids.Count &&
                string.Equals(
                    entry.condition_order_ids[entry.saved_condition_order_index],
                    entry.saved_condition_id,
                    StringComparison.Ordinal) &&
                entry.saved_visible_prueba == entry.saved_condition_order_index + 1;
        }
    }
}
