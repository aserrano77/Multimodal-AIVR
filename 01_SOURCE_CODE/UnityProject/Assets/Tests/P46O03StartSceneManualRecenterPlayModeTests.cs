using System.Collections;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

public sealed class P46O03StartSceneManualRecenterPlayModeTests
{
    [UnityTest]
    public IEnumerator ReferenceSpaceOffsetsAndYaw_AreRealignedBeforeUiGateRelease()
    {
        yield return LoadStartSceneAndWaitForInitialAlignment();
        ExperimentStartSceneXrPoseAligner aligner = FindAligner();
        Component ui = FindComponentByTypeName("ExperimentRuntimeStartScreenUI");
        XROrigin origin = Object.FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
        Transform anchor = FindTransform("StartSceneHeadPoseAnchor");
        Assert.That(ui, Is.Not.Null);
        Assert.That(origin, Is.Not.Null);
        Assert.That(origin.Camera, Is.Not.Null);
        Assert.That(anchor, Is.Not.Null);

        aligner.SetTrackingReadyForTests(true);
        aligner.SetApplicationActiveForTests(true, false);
        aligner.ConfigureRealignmentForTests(trackingTimeoutSeconds: 0.2f, debounceSeconds: 0f, maxPendingReplays: 1);
        yield return new WaitForSecondsRealtime(0.4f);
        Vector3 cameraLocalPosition = origin.Camera.transform.localPosition;
        Quaternion cameraLocalRotation = origin.Camera.transform.localRotation;
        var changes = new[]
        {
            new PoseChange(new Vector3(0.5f, 0f, 0f), 0f),
            new PoseChange(new Vector3(0f, 0f, -0.7f), 90f),
            new PoseChange(new Vector3(0f, 0.3f, 0f), 180f),
            new PoseChange(new Vector3(-0.4f, -0.2f, 0.6f), 270f)
        };

        foreach (PoseChange change in changes)
        {
            origin.transform.position += change.Offset;
            origin.transform.rotation = Quaternion.Euler(0f, change.YawDegrees, 0f) * origin.transform.rotation;
            int releaseBefore = aligner.RecenterGateReleaseCount;
            int repositionBefore = ReadIntProperty(ui, "RecenterCanvasRepositionCount");

            Assert.That(aligner.RequestRealignment(ExperimentStartSceneAlignmentReason.TrackingOriginUpdate), Is.True);
            yield return WaitForGateRelease(aligner, releaseBefore, 2f);

            Assert.That(aligner.RecenterGateReleaseCount, Is.EqualTo(releaseBefore + 1));
            Assert.That(ReadIntProperty(ui, "RecenterCanvasRepositionCount"), Is.EqualTo(repositionBefore + 1));
            Assert.That(Vector3.Distance(origin.Camera.transform.position, anchor.position), Is.LessThanOrEqualTo(0.03f));
            Assert.That(ExperimentStartSceneXrPoseMath.CalculateHorizontalYawErrorDegrees(
                origin.Camera.transform.forward, anchor.forward), Is.LessThanOrEqualTo(2f));
            Assert.That(origin.Camera.transform.localPosition, Is.EqualTo(cameraLocalPosition));
            Assert.That(Quaternion.Angle(origin.Camera.transform.localRotation, cameraLocalRotation), Is.LessThan(0.0001f));

            ExperimentStartSceneCanvasDiagnostics diagnostics = ((IExperimentStartSceneRealignmentUiGate)ui).CaptureDiagnostics();
            Assert.That(diagnostics.CanvasEnabled, Is.True);
            Assert.That(diagnostics.PositionErrorMeters, Is.LessThanOrEqualTo(0.001f));
            Assert.That(diagnostics.YawErrorDegrees, Is.LessThanOrEqualTo(0.1f));
            yield return new WaitForSecondsRealtime(0.4f);
        }
    }

    [UnityTest]
    public IEnumerator RepeatedSignals_AreCoalescedAndTimeoutRestoresExactUiState()
    {
        yield return LoadStartSceneAndWaitForInitialAlignment();
        ExperimentStartSceneXrPoseAligner aligner = FindAligner();
        Component ui = FindComponentByTypeName("ExperimentRuntimeStartScreenUI");
        XROrigin origin = Object.FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
        Assert.That(ui, Is.Not.Null);
        Assert.That(origin, Is.Not.Null);

        Canvas canvas = ui.GetComponent<Canvas>();
        GraphicRaycaster graphic = ui.GetComponent<GraphicRaycaster>();
        bool canvasBefore = canvas.enabled;
        bool graphicBefore = graphic.enabled;
        aligner.SetTrackingReadyForTests(true);
        aligner.SetApplicationActiveForTests(true, false);
        aligner.ConfigureRealignmentForTests(trackingTimeoutSeconds: 0.2f, debounceSeconds: 0.02f, maxPendingReplays: 1);
        yield return new WaitForSecondsRealtime(0.4f);
        origin.transform.position += new Vector3(0.6f, 0.2f, -0.5f);

        int operationsBefore = aligner.RecenterOperationCount;
        for (int signal = 0; signal < 6; signal++)
        {
            aligner.SimulateTrackingOriginUpdatedForTests();
        }

        yield return WaitForNoRealignment(aligner, 3f);
        Assert.That(aligner.RecenterOperationCount - operationsBefore, Is.InRange(1, 2));
        Assert.That(aligner.RecenterReplayCount, Is.LessThanOrEqualTo(1));
        Assert.That(canvas.enabled, Is.EqualTo(canvasBefore));
        Assert.That(graphic.enabled, Is.EqualTo(graphicBefore));

        aligner.SetTrackingReadyForTests(false);
        aligner.ConfigureRealignmentForTests(trackingTimeoutSeconds: 0.03f, debounceSeconds: 0f, maxPendingReplays: 0);
        yield return new WaitForSecondsRealtime(0.4f);
        origin.transform.position += Vector3.right;
        int releaseBefore = aligner.RecenterGateReleaseCount;
        Assert.That(aligner.RequestRealignment(ExperimentStartSceneAlignmentReason.PoseDriftObserved), Is.True);
        yield return WaitForGateRelease(aligner, releaseBefore, 2f);

        Assert.That(canvas.enabled, Is.EqualTo(canvasBefore), "Fail-soft timeout did not restore canvas visibility.");
        Assert.That(graphic.enabled, Is.EqualTo(graphicBefore), "Fail-soft timeout did not restore raycasts.");
        Assert.That(aligner.IsRealignmentRunning, Is.False);
    }

    [UnityTest]
    public IEnumerator FocusRecoveryInsideTolerance_DoesNotStartAnInfiniteRealignmentChain()
    {
        yield return LoadStartSceneAndWaitForInitialAlignment();
        ExperimentStartSceneXrPoseAligner aligner = FindAligner();
        aligner.SetTrackingReadyForTests(true);
        aligner.SetApplicationActiveForTests(true, false);
        aligner.ConfigureRealignmentForTests(trackingTimeoutSeconds: 0.1f, debounceSeconds: 0f, maxPendingReplays: 1);
        Assert.That(aligner.TryAlignCameraToAnchor(out _, out _), Is.True);

        int operationsBefore = aligner.RecenterOperationCount;
        aligner.SimulateApplicationFocusForTests(false);
        aligner.SimulateApplicationFocusForTests(true);
        for (int frame = 0; frame < 10; frame++) yield return null;

        Assert.That(aligner.RecenterOperationCount, Is.EqualTo(operationsBefore));
        Assert.That(aligner.IsRealignmentPendingOrRunning, Is.False);
    }

    private static IEnumerator LoadStartSceneAndWaitForInitialAlignment()
    {
        AsyncOperation operation = SceneManager.LoadSceneAsync("experiment_start_scene", LoadSceneMode.Single);
        while (!operation.isDone) yield return null;
        ExperimentStartSceneXrPoseAligner aligner = FindAligner();
        float deadline = Time.realtimeSinceStartup + 12f;
        while (!aligner.IsAlignmentComplete && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.That(aligner.IsAlignmentComplete, Is.True, "Initial P46O-01 alignment did not reach a terminal state.");
        yield return null;
    }

    private static ExperimentStartSceneXrPoseAligner FindAligner()
    {
        ExperimentStartSceneXrPoseAligner aligner = Object.FindFirstObjectByType<ExperimentStartSceneXrPoseAligner>(FindObjectsInactive.Include);
        Assert.That(aligner, Is.Not.Null);
        return aligner;
    }

    private static Transform FindTransform(string name)
    {
        foreach (Transform transform in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (transform.name == name) return transform;
        }

        return null;
    }

    private static Component FindComponentByTypeName(string typeName)
    {
        foreach (MonoBehaviour component in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (component != null && component.GetType().Name == typeName) return component;
        }

        return null;
    }

    private static int ReadIntProperty(Component component, string propertyName)
    {
        object value = component.GetType().GetProperty(propertyName)?.GetValue(component);
        return value is int result ? result : -1;
    }

    private static IEnumerator WaitForGateRelease(ExperimentStartSceneXrPoseAligner aligner, int releaseBefore, float seconds)
    {
        float deadline = Time.realtimeSinceStartup + seconds;
        while (aligner.RecenterGateReleaseCount == releaseBefore && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.That(aligner.RecenterGateReleaseCount, Is.GreaterThan(releaseBefore), "Recenter gate did not release within timeout.");
    }

    private static IEnumerator WaitForNoRealignment(ExperimentStartSceneXrPoseAligner aligner, float seconds)
    {
        float deadline = Time.realtimeSinceStartup + seconds;
        while (aligner.IsRealignmentPendingOrRunning && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.That(aligner.IsRealignmentPendingOrRunning, Is.False, "Recenter operation did not settle within timeout.");
    }

    private readonly struct PoseChange
    {
        public PoseChange(Vector3 offset, float yawDegrees)
        {
            Offset = offset;
            YawDegrees = yawDegrees;
        }

        public Vector3 Offset { get; }
        public float YawDegrees { get; }
    }
}
