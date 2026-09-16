using Autonomy.Domain;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public sealed class ExperimentTrialAdvanceReconcilerTests
    {
        [Test]
        public void CanFinalize_WhenInstrumentationAlreadyClosedCurrentTrial()
        {
            var snapshot = new ExperimentTrialAdvanceSnapshot(
                orchestratorTrialActive: true,
                instrumentationTrialActive: false,
                instrumentationTrialCompleted: true,
                roundCompleted: false,
                robotRuntimeBusy: false,
                totalBoxes: 5,
                completedCount: 5,
                pendingCount: 0,
                assignedCount: 0,
                currentTrialId: "trial_001",
                instrumentationLastTerminalTrialId: "trial_001");

            bool canAdvance = ExperimentTrialAdvanceReconciler.CanFinalizeCompletedTrialForAdvance(snapshot, out string reason);

            Assert.That(canAdvance, Is.True);
            Assert.That(reason, Is.EqualTo("instrumentation_terminal_trial"));
        }

        [Test]
        public void CanFinalize_WhenManualRoundHasAllBoxesDeposited()
        {
            var snapshot = new ExperimentTrialAdvanceSnapshot(
                orchestratorTrialActive: true,
                instrumentationTrialActive: false,
                instrumentationTrialCompleted: false,
                roundCompleted: false,
                robotRuntimeBusy: false,
                totalBoxes: 5,
                completedCount: 5,
                pendingCount: 0,
                assignedCount: 0,
                currentTrialId: "trial_001",
                instrumentationLastTerminalTrialId: string.Empty);

            bool canAdvance = ExperimentTrialAdvanceReconciler.CanFinalizeCompletedTrialForAdvance(snapshot, out string reason);

            Assert.That(canAdvance, Is.True);
            Assert.That(reason, Is.EqualTo("all_boxes_completed"));
        }

        [Test]
        public void Blocks_WhenBoxesRemainPending()
        {
            var snapshot = new ExperimentTrialAdvanceSnapshot(
                orchestratorTrialActive: true,
                instrumentationTrialActive: true,
                instrumentationTrialCompleted: false,
                roundCompleted: false,
                robotRuntimeBusy: false,
                totalBoxes: 5,
                completedCount: 4,
                pendingCount: 1,
                assignedCount: 0,
                currentTrialId: "trial_001",
                instrumentationLastTerminalTrialId: string.Empty);

            bool canAdvance = ExperimentTrialAdvanceReconciler.CanFinalizeCompletedTrialForAdvance(snapshot, out string reason);

            Assert.That(canAdvance, Is.False);
            Assert.That(reason, Is.EqualTo("trial_incomplete"));
        }

        [Test]
        public void Blocks_WhenAllBoxesCompletedButRobotStillBusy()
        {
            var snapshot = new ExperimentTrialAdvanceSnapshot(
                orchestratorTrialActive: true,
                instrumentationTrialActive: true,
                instrumentationTrialCompleted: false,
                roundCompleted: false,
                robotRuntimeBusy: true,
                totalBoxes: 5,
                completedCount: 5,
                pendingCount: 0,
                assignedCount: 0,
                currentTrialId: "trial_002",
                instrumentationLastTerminalTrialId: string.Empty);

            bool canAdvance = ExperimentTrialAdvanceReconciler.CanFinalizeCompletedTrialForAdvance(snapshot, out string reason);

            Assert.That(canAdvance, Is.False);
            Assert.That(reason, Is.EqualTo("runtime_busy"));
        }
    }
}
