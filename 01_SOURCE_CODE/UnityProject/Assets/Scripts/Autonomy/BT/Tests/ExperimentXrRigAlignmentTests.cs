using System.IO;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using UnityEngine;

namespace Autonomy.BT.Tests
{
    public sealed class ExperimentXrRigAlignmentTests
    {
        [Test]
        public void HorizontalDistance_IgnoresTrackedHeadHeight()
        {
            float distance = ExperimentXrPoseAlignmentMath.HorizontalDistance(
                new Vector3(3f, 1.2f, -4f),
                new Vector3(3f, 2.1f, -4f));

            Assert.That(distance, Is.EqualTo(0f).Within(0.000001f));
        }

        [TestCase(0.03f, 2f, true)]
        [TestCase(0.03001f, 2f, false)]
        [TestCase(0.03f, 2.001f, false)]
        public void HorizontalTolerance_IsInclusive(float positionError, float yawError, bool expected)
        {
            Assert.That(
                ExperimentXrPoseAlignmentMath.IsWithinHorizontalTolerance(
                    positionError,
                    yawError,
                    0.03f,
                    2f),
                Is.EqualTo(expected));
        }

        [TestCase(false, false, "Device", "floor_unsupported_keep_Device")]
        [TestCase(true, false, "Device", "floor_request_failed_keep_Device")]
        [TestCase(true, true, "Floor", "none")]
        public void FloorFallback_IsExplicit(
            bool floorSupported,
            bool requestSucceeded,
            string effective,
            string expected)
        {
            Assert.That(
                ExperimentXrPoseAlignmentMath.DescribeFloorFallback(
                    floorSupported,
                    requestSucceeded,
                    effective),
                Is.EqualTo(expected));
        }

        [Test]
        public void TrialPreparation_GatesCanvasRobotSpawnAndTrialStartInThatOrder()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "ExperimentSessionOrchestrator.cs"));
            int alignment = source.IndexOf("yield return ApplyUserRigResetForTrial", System.StringComparison.Ordinal);
            int gateReleased = source.IndexOf("\"experiment_xr_alignment_gate_released\"", alignment, System.StringComparison.Ordinal);
            int robotReset = source.IndexOf("ApplyRobotPoseResetForTrial(context)", alignment, System.StringComparison.Ordinal);
            int spawn = source.IndexOf("SpawnRoundForCurrentTrial(context)", alignment, System.StringComparison.Ordinal);
            int trial = source.IndexOf("_instrumentation?.StartTrial()", alignment, System.StringComparison.Ordinal);
            int autonomy = source.IndexOf("InitializeRoundFromScene()", alignment, System.StringComparison.Ordinal);

            Assert.That(alignment, Is.GreaterThanOrEqualTo(0));
            Assert.That(gateReleased, Is.GreaterThan(alignment));
            Assert.That(robotReset, Is.GreaterThan(gateReleased));
            Assert.That(spawn, Is.GreaterThan(robotReset));
            Assert.That(trial, Is.GreaterThan(spawn));
            Assert.That(autonomy, Is.GreaterThan(trial));
        }

        [Test]
        public void RuntimeUi_HidesBeforeAlignmentAndRepositionsOnlyWhenGateReleases()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "ExperimentRuntimeProtocolUI.cs")).Replace("\r\n", "\n");

            Assert.That(source, Does.Contain("SetProtocolVisible(false);"));
            Assert.That(source, Does.Contain("yield return _xrRigResetter.AlignBeforeProtocolUiCoroutine"));
            Assert.That(source, Does.Contain("HandleXrAlignmentGateReleased"));
            Assert.That(
                source,
                Does.Contain("_screen = ScreenState.Progress;\n            if (_xrRigResetter == null)"));
            Assert.That(source, Does.Contain("PositionAtProtocolStation();\n            SetProtocolVisible(true);"));
        }

        [Test]
        public void P47A_DepositAndAdvanceRequireReleasedUnselectedRoundBoxes()
        {
            string scripts = Path.Combine(Application.dataPath, "Scripts");
            string boxMetadata = File.ReadAllText(Path.Combine(scripts, "BoxMetadata.cs"));
            string depositZone = File.ReadAllText(Path.Combine(scripts, "DepositZone.cs"));
            string roundManager = File.ReadAllText(Path.Combine(scripts, "RoundManager.cs"));
            string orchestrator = File.ReadAllText(Path.Combine(scripts, "ExperimentSessionOrchestrator.cs"));
            string runtimeUi = File.ReadAllText(Path.Combine(scripts, "ExperimentRuntimeProtocolUI.cs"));

            Assert.That(boxMetadata, Does.Contain("grabInteractable.isSelected"));
            Assert.That(boxMetadata, Does.Contain("TryLockAfterDepositIfReleased"));
            Assert.That(depositZone, Does.Contain("box.IsGrabbedOrSelected"));
            Assert.That(depositZone, Does.Contain("TryRegisterReleasedCorrectDeposit"));
            Assert.That(roundManager, Does.Contain("box_still_grabbed_or_selected"));
            Assert.That(orchestrator, Does.Contain("TryGetHeldOrSelectedActiveRoundBox"));
            Assert.That(orchestrator, Does.Contain("reason = \"round_not_completed\""));
            Assert.That(orchestrator, Does.Contain("HasCurrentTrialForAdvance() && !CanAdvanceCurrentTrialFromRuntime"));
            Assert.That(runtimeUi, Does.Contain("RevalidateAdvanceCallback(beforeSnapshot, \"begin_condition\")"));
            Assert.That(runtimeUi, Does.Contain("OpenNextConditionInstructions"));
        }

        [Test]
        public void P47A_WatchdogDiagnosticsOnlyMaterializeWhenStateChangesOrWorkOccurs()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "ExperimentRuntimeUiRayInteractorBootstrap.cs"));

            Assert.That(source, Does.Contain("_hasLoggedCanvasRaycasterState"));
            Assert.That(source, Does.Contain("if (stateChanged || raycasterCreated || raycasterReenabled || eventCameraAssigned)"));
            Assert.That(source, Does.Contain("_hasLoggedRuntimeUiRayPolicyState"));
            Assert.That(source, Does.Contain("bool shouldLogPolicyState = (disabled > 0 || questPolicy)"));
            Assert.That(source, Does.Contain("if (shouldLogPolicyState)"));
        }

        [Test]
        public void P47A_TransitionTelemetrySeparatesActiveContextFromUpcomingCondition()
        {
            string scripts = Path.Combine(Application.dataPath, "Scripts");
            string orchestrator = File.ReadAllText(Path.Combine(scripts, "ExperimentSessionOrchestrator.cs"));
            string runtimeUi = File.ReadAllText(Path.Combine(scripts, "ExperimentRuntimeProtocolUI.cs"));

            Assert.That(orchestrator, Does.Contain("payload[\"condition_order_index\"] = currentEntry.ConditionOrderIndex"));
            Assert.That(orchestrator, Does.Contain("payload[\"next_condition_order_index\"] = nextEntry != null"));
            Assert.That(orchestrator, Does.Not.Contain("payload[\"condition_order_index\"] = nextEntry != null"));
            Assert.That(runtimeUi, Does.Contain("[\"resolved_condition_order_index\"] = label.SequenceIndex - 1"));
            Assert.That(runtimeUi, Does.Contain("[\"instruction_condition_id\"] = conditionId"));
            Assert.That(runtimeUi, Does.Contain("[\"preflight_condition_id\"] = conditionId"));
            Assert.That(runtimeUi, Does.Not.Contain("[\"condition_order_index\"] = label.SequenceIndex - 1"));
        }

        [Test]
        public void P47A_HighVolumeTelemetrySkipsPollingNoOpsAndPreservesAggregates()
        {
            string scripts = Path.Combine(Application.dataPath, "Scripts");
            string depositZone = File.ReadAllText(Path.Combine(scripts, "DepositZone.cs"));
            string manipulation = File.ReadAllText(Path.Combine(
                scripts,
                "Autonomy",
                "UnityIntegration",
                "TiagoUnityManipulationService.cs"));
            string assistance = File.ReadAllText(Path.Combine(
                scripts,
                "Autonomy",
                "UnityIntegration",
                "RobotAssistanceRoundCoordinator.cs"));

            Assert.That(depositZone, Does.Contain("if (box != null && box.isDeposited)"));
            Assert.That(depositZone, Does.Contain("if (source != \"trigger_stay\")"));
            Assert.That(depositZone, Does.Contain("return stayDiagnosticsLogged.Add(other);"));
            Assert.That(manipulation, Does.Contain("_driftEpisodeLogged"));
            Assert.That(manipulation, Does.Contain("if (logDrift || logEnforcement)"));
            Assert.That(assistance, Does.Contain("place_navigation_candidates_evaluated"));
            Assert.That(assistance, Does.Contain("BuildTelemetrySignature(records)"));
            Assert.That(assistance, Does.Not.Contain("\"post_place_egress_candidate_evaluated\""));
            Assert.That(assistance, Does.Not.Contain("\"place_navigation_candidate_rejected\""));
            Assert.That(assistance, Does.Not.Contain("\"deposit_slot_candidate_rejected\""));
        }
    }
}
