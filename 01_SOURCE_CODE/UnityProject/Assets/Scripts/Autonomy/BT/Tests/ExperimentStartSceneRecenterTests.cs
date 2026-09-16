using System.IO;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using UnityEngine;

namespace Autonomy.BT.Tests
{
    public sealed class ExperimentStartSceneRecenterTests
    {
        [Test]
        public void SuccessiveSignals_AreGroupedIntoOneScheduledOperation()
        {
            var tracker = new ExperimentStartSceneRecenterRequestTracker();

            Assert.That(
                tracker.RegisterSignal(ExperimentStartSceneAlignmentReason.TrackingOriginUpdate, true, false),
                Is.EqualTo(ExperimentStartSceneRecenterSignalDisposition.Scheduled));
            Assert.That(
                tracker.RegisterSignal(ExperimentStartSceneAlignmentReason.ApplicationFocusRegained, true, false),
                Is.EqualTo(ExperimentStartSceneRecenterSignalDisposition.Coalesced));

            ExperimentStartSceneRecenterOperation operation = tracker.BeginOperation();
            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.GroupedSignalCount, Is.EqualTo(2));
            Assert.That(operation.Reasons, Is.EqualTo(
                ExperimentStartSceneAlignmentReason.TrackingOriginUpdate |
                ExperimentStartSceneAlignmentReason.ApplicationFocusRegained));
        }

        [Test]
        public void SignalDuringOperation_RequestsAtMostOneReplay()
        {
            var tracker = new ExperimentStartSceneRecenterRequestTracker(maxPendingReplays: 1);
            tracker.RegisterSignal(ExperimentStartSceneAlignmentReason.TrackingOriginUpdate, true, false);
            tracker.BeginOperation();

            Assert.That(
                tracker.RegisterSignal(ExperimentStartSceneAlignmentReason.TrackingOriginUpdate, true, false),
                Is.EqualTo(ExperimentStartSceneRecenterSignalDisposition.PendingReplayRequested));
            for (int i = 0; i < 5; i++)
            {
                tracker.RegisterSignal(ExperimentStartSceneAlignmentReason.ApplicationFocusRegained, true, false);
            }

            Assert.That(tracker.CompleteOperation(), Is.True);
            ExperimentStartSceneRecenterOperation replay = tracker.BeginOperation();
            Assert.That(replay.IsReplay, Is.True);
            tracker.RegisterSignal(ExperimentStartSceneAlignmentReason.TrackingOriginUpdate, true, false);
            Assert.That(tracker.CompleteOperation(), Is.False);
            Assert.That(tracker.ReplayCount, Is.EqualTo(1));
        }

        [Test]
        public void InternalSuppression_DoesNotAdvanceGenerationOrScheduleWork()
        {
            var tracker = new ExperimentStartSceneRecenterRequestTracker();

            ExperimentStartSceneRecenterSignalDisposition result = tracker.RegisterSignal(
                ExperimentStartSceneAlignmentReason.TrackingOriginUpdate,
                inStartScene: true,
                internalSuppressed: true);

            Assert.That(result, Is.EqualTo(ExperimentStartSceneRecenterSignalDisposition.InternalEventSuppressed));
            Assert.That(tracker.SignalGeneration, Is.Zero);
            Assert.That(tracker.HasScheduledSignals, Is.False);
        }

        [Test]
        public void SignalOutsideStartScene_IsIgnoredWithoutMutation()
        {
            var tracker = new ExperimentStartSceneRecenterRequestTracker();

            Assert.That(
                tracker.RegisterSignal(ExperimentStartSceneAlignmentReason.TrackingOriginUpdate, false, false),
                Is.EqualTo(ExperimentStartSceneRecenterSignalDisposition.IgnoredWrongScene));
            Assert.That(tracker.SignalGeneration, Is.Zero);
            Assert.That(tracker.HasScheduledSignals, Is.False);
        }

        [Test]
        public void ReasonsSurviveCoalescingForTelemetry()
        {
            var tracker = new ExperimentStartSceneRecenterRequestTracker();
            tracker.RegisterSignal(ExperimentStartSceneAlignmentReason.TrackingOriginUpdate, true, false);
            tracker.RegisterSignal(ExperimentStartSceneAlignmentReason.ApplicationResume, true, false);
            tracker.RegisterSignal(ExperimentStartSceneAlignmentReason.PoseDriftObserved, true, false);

            ExperimentStartSceneRecenterOperation operation = tracker.BeginOperation();

            Assert.That(operation.Reasons.HasFlag(ExperimentStartSceneAlignmentReason.TrackingOriginUpdate), Is.True);
            Assert.That(operation.Reasons.HasFlag(ExperimentStartSceneAlignmentReason.ApplicationResume), Is.True);
            Assert.That(operation.Reasons.HasFlag(ExperimentStartSceneAlignmentReason.PoseDriftObserved), Is.True);
        }

        [Test]
        public void CanvasReposition_IsOrderedAfterCameraVerificationAndBeforeGateRelease()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Autonomy",
                "UnityIntegration",
                "ExperimentStartSceneXrPoseAligner.cs"));
            int verification = source.IndexOf("MeasureErrors(out float verifiedPositionError", System.StringComparison.Ordinal);
            int reposition = source.IndexOf("SafeRepositionCanvas(operation.Generation)", verification, System.StringComparison.Ordinal);
            int release = source.IndexOf("SafeReleaseRealignmentUiGate(", reposition, System.StringComparison.Ordinal);

            Assert.That(verification, Is.GreaterThanOrEqualTo(0));
            Assert.That(reposition, Is.GreaterThan(verification));
            Assert.That(release, Is.GreaterThan(reposition));
        }

        [Test]
        public void LifecycleAndUiGate_AreFailSoftAndRestoreExactStates()
        {
            string aligner = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Autonomy", "UnityIntegration", "ExperimentStartSceneXrPoseAligner.cs"));
            string ui = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "ExperimentRuntimeStartScreenUI.cs"));

            Assert.That(aligner, Does.Contain("UnsubscribeTrackingOriginUpdates();"));
            Assert.That(aligner, Does.Contain("SafeReleaseRealignmentUiGate(\"aligner_disabled\""));
            Assert.That(aligner, Does.Contain("catch (Exception exception)"));
            Assert.That(ui, Does.Contain("trackedRaycaster.enabled = false;"));
            Assert.That(ui, Does.Contain("trackedRaycaster.enabled = _recenterUiGateSnapshot.TrackedRaycasterEnabled;"));
            Assert.That(ui, Does.Contain("_canvas.enabled = _recenterUiGateSnapshot.CanvasEnabled;"));
            Assert.That(ui, Does.Contain("PositionAtDeterministicStartAnchor();"));
        }

        [Test]
        public void NoContinuousCameraFollowingOrDirectTrackedCameraWritesWereIntroduced()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Autonomy",
                "UnityIntegration",
                "ExperimentStartSceneXrPoseAligner.cs"));

            Assert.That(source, Does.Not.Contain("private void Update()"));
            Assert.That(source, Does.Not.Contain("private void LateUpdate()"));
            Assert.That(source, Does.Not.Contain("Camera.transform.localPosition"));
            Assert.That(source, Does.Not.Contain("Camera.transform.localRotation"));
            Assert.That(source, Does.Contain("MatchOriginUpCameraForward"));
            Assert.That(source, Does.Contain("MoveCameraToWorldLocation"));
        }
    }
}
