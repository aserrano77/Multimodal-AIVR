using System;

namespace Autonomy.Domain
{
    public readonly struct ExperimentTrialAdvanceSnapshot
    {
        public ExperimentTrialAdvanceSnapshot(
            bool orchestratorTrialActive,
            bool instrumentationTrialActive,
            bool instrumentationTrialCompleted,
            bool roundCompleted,
            bool robotRuntimeBusy,
            int totalBoxes,
            int completedCount,
            int pendingCount,
            int assignedCount,
            string currentTrialId,
            string instrumentationLastTerminalTrialId)
        {
            OrchestratorTrialActive = orchestratorTrialActive;
            InstrumentationTrialActive = instrumentationTrialActive;
            InstrumentationTrialCompleted = instrumentationTrialCompleted;
            RoundCompleted = roundCompleted;
            RobotRuntimeBusy = robotRuntimeBusy;
            TotalBoxes = Math.Max(0, totalBoxes);
            CompletedCount = Math.Max(0, completedCount);
            PendingCount = Math.Max(0, pendingCount);
            AssignedCount = Math.Max(0, assignedCount);
            CurrentTrialId = currentTrialId ?? string.Empty;
            InstrumentationLastTerminalTrialId = instrumentationLastTerminalTrialId ?? string.Empty;
        }

        public bool OrchestratorTrialActive { get; }
        public bool InstrumentationTrialActive { get; }
        public bool InstrumentationTrialCompleted { get; }
        public bool RoundCompleted { get; }
        public bool RobotRuntimeBusy { get; }
        public int TotalBoxes { get; }
        public int CompletedCount { get; }
        public int PendingCount { get; }
        public int AssignedCount { get; }
        public string CurrentTrialId { get; }
        public string InstrumentationLastTerminalTrialId { get; }
    }

    public static class ExperimentTrialAdvanceReconciler
    {
        public static bool CanFinalizeCompletedTrialForAdvance(ExperimentTrialAdvanceSnapshot snapshot, out string reason)
        {
            if (!snapshot.OrchestratorTrialActive && !snapshot.InstrumentationTrialActive)
            {
                reason = "trial_not_active";
                return false;
            }

            if (snapshot.InstrumentationTrialCompleted)
            {
                reason = "instrumentation_terminal_trial";
                return true;
            }

            if (snapshot.RoundCompleted)
            {
                reason = "round_completed";
                return true;
            }

            bool allBoxesCompleted = snapshot.TotalBoxes > 0 &&
                snapshot.CompletedCount >= snapshot.TotalBoxes &&
                snapshot.PendingCount == 0 &&
                snapshot.AssignedCount == 0;
            if (allBoxesCompleted && !snapshot.RobotRuntimeBusy)
            {
                reason = "all_boxes_completed";
                return true;
            }

            reason = snapshot.RobotRuntimeBusy
                ? "runtime_busy"
                : "trial_incomplete";
            return false;
        }
    }
}
