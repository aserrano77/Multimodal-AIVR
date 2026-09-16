using System.Collections;
using System.Reflection;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public sealed class P46O01StartSceneXrPoseAlignmentPlayModeTests
{
    private GameObject _testRoot;

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (_testRoot != null)
        {
            Object.Destroy(_testRoot);
        }

        yield return null;
    }

    [UnityTest]
    public IEnumerator SimulatedLocalHeadPoses_ConvergeWithoutWritingCameraLocalPose_AndAreIdempotent()
    {
        var cases = new[]
        {
            new SimulatedPose(new Vector3(0.42f, 1.45f, -0.31f), 90f),
            new SimulatedPose(new Vector3(-0.65f, 1.82f, 0.27f), 180f),
            new SimulatedPose(new Vector3(1.1f, 1.25f, 0.8f), 270f),
            new SimulatedPose(new Vector3(-0.2f, 2.05f, -1.3f), 0f)
        };

        foreach (SimulatedPose simulated in cases)
        {
            PoseFixture fixture = CreateFixture(simulated.LocalPosition, simulated.LocalYawDegrees);
            Vector3 cameraLocalPositionBefore = fixture.Camera.transform.localPosition;
            Quaternion cameraLocalRotationBefore = fixture.Camera.transform.localRotation;

            bool aligned = fixture.Aligner.TryAlignCameraToAnchor(
                out float positionError,
                out float yawError);

            Assert.That(aligned, Is.True);
            Assert.That(positionError, Is.LessThan(0.0001f));
            Assert.That(yawError, Is.LessThan(0.0001f));
            Assert.That(Vector3.Distance(fixture.Camera.transform.position, fixture.Anchor.position), Is.LessThan(0.0001f));
            Assert.That(fixture.Camera.transform.localPosition, Is.EqualTo(cameraLocalPositionBefore));
            Assert.That(Quaternion.Angle(fixture.Camera.transform.localRotation, cameraLocalRotationBefore), Is.LessThan(0.0001f));

            Vector3 originPositionAfterFirst = fixture.Origin.transform.position;
            Quaternion originRotationAfterFirst = fixture.Origin.transform.rotation;
            Assert.That(fixture.Aligner.TryAlignCameraToAnchor(out positionError, out yawError), Is.True);
            Assert.That(Vector3.Distance(fixture.Origin.transform.position, originPositionAfterFirst), Is.LessThan(0.0001f));
            Assert.That(Quaternion.Angle(fixture.Origin.transform.rotation, originRotationAfterFirst), Is.LessThan(0.0001f));
            Assert.That(fixture.Camera.transform.localPosition, Is.EqualTo(cameraLocalPositionBefore));
            Assert.That(Quaternion.Angle(fixture.Camera.transform.localRotation, cameraLocalRotationBefore), Is.LessThan(0.0001f));

            Object.Destroy(fixture.Root);
            yield return null;
        }
    }

    [UnityTest]
    public IEnumerator MissingTracking_TimesOutFailSoftAndReachesTerminalState()
    {
        PoseFixture fixture = CreateFixture(new Vector3(0.2f, 1.7f, -0.4f), 90f);
        fixture.Aligner.ConfigureForTests(
            fixture.Origin,
            fixture.Anchor,
            trackingTimeoutSeconds: 0.05f,
            stableFramesRequired: 2,
            maxAlignmentAttempts: 2,
            maxTrackingOriginUpdates: 2);

        Assert.That(fixture.Aligner.BeginAlignment(), Is.True);
        float deadline = Time.realtimeSinceStartup + 1f;
        while (!fixture.Aligner.IsAlignmentComplete && Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }

        Assert.That(fixture.Aligner.IsAlignmentComplete, Is.True);
        Assert.That(fixture.Aligner.AlignmentSucceeded, Is.False);
        Assert.That(fixture.Aligner.FailureReason, Does.StartWith("tracking_timeout:"));
    }

    [UnityTest]
    public IEnumerator TrackingOriginUpdates_AreBoundedDuringInitializationWindow()
    {
        PoseFixture fixture = CreateFixture(new Vector3(0f, 1.7f, 0f), 0f);
        fixture.Aligner.ConfigureForTests(
            fixture.Origin,
            fixture.Anchor,
            trackingTimeoutSeconds: 0.2f,
            stableFramesRequired: 2,
            maxAlignmentAttempts: 2,
            maxTrackingOriginUpdates: 2);
        Assert.That(fixture.Aligner.BeginAlignment(), Is.True);

        MethodInfo registerUpdate = typeof(ExperimentStartSceneXrPoseAligner).GetMethod(
            "RegisterTrackingOriginUpdate",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(registerUpdate, Is.Not.Null);
        for (int i = 0; i < 8; i++)
        {
            registerUpdate.Invoke(fixture.Aligner, null);
        }

        Assert.That(fixture.Aligner.TrackingOriginUpdateCount, Is.EqualTo(2));
        yield return null;
    }

    [UnityTest]
    public IEnumerator StartSceneUi_RemainsHiddenUntilAlignmentTerminates()
    {
        AsyncOperation load = SceneManager.LoadSceneAsync("experiment_start_scene", LoadSceneMode.Single);
        while (!load.isDone)
        {
            yield return null;
        }

        ExperimentStartSceneXrPoseAligner aligner = Object.FindFirstObjectByType<ExperimentStartSceneXrPoseAligner>(
            FindObjectsInactive.Include);
        Assert.That(aligner, Is.Not.Null);
        Canvas canvas = aligner.GetComponent<Canvas>();
        Assert.That(canvas, Is.Not.Null);
        Assert.That(canvas.enabled, Is.False, "The start UI became visible before alignment completed or timed out.");

        float deadline = Time.realtimeSinceStartup + 12f;
        while (!aligner.IsAlignmentComplete && Time.realtimeSinceStartup < deadline)
        {
            Assert.That(canvas.enabled, Is.False, "The start UI was released while alignment was still active.");
            yield return null;
        }

        Assert.That(aligner.IsAlignmentComplete, Is.True);
        yield return null;
        Assert.That(canvas.enabled, Is.True, "Fail-soft completion did not release the start UI.");
    }

    private PoseFixture CreateFixture(Vector3 cameraLocalPosition, float cameraLocalYawDegrees)
    {
        _testRoot = new GameObject("P46O01_TestRoot");
        GameObject originObject = new GameObject("XR Origin");
        originObject.transform.SetParent(_testRoot.transform, false);
        GameObject offsetObject = new GameObject("Camera Offset");
        offsetObject.transform.SetParent(originObject.transform, false);
        GameObject cameraObject = new GameObject("Main Camera");
        cameraObject.transform.SetParent(offsetObject.transform, false);
        cameraObject.transform.localPosition = cameraLocalPosition;
        cameraObject.transform.localRotation = Quaternion.Euler(0f, cameraLocalYawDegrees, 0f);
        Camera camera = cameraObject.AddComponent<Camera>();

        XROrigin origin = originObject.AddComponent<XROrigin>();
        origin.Origin = originObject;
        origin.CameraFloorOffsetObject = offsetObject;
        origin.Camera = camera;

        GameObject anchorObject = new GameObject("StartSceneHeadPoseAnchor");
        anchorObject.transform.SetParent(_testRoot.transform, false);
        anchorObject.transform.SetPositionAndRotation(
            new Vector3(3.25f, 1.73f, -2.6f),
            Quaternion.Euler(0f, 37f, 0f));

        ExperimentStartSceneXrPoseAligner aligner = _testRoot.AddComponent<ExperimentStartSceneXrPoseAligner>();
        aligner.ConfigureForTests(origin, anchorObject.transform);
        return new PoseFixture(_testRoot, origin, camera, anchorObject.transform, aligner);
    }

    private readonly struct SimulatedPose
    {
        public SimulatedPose(Vector3 localPosition, float localYawDegrees)
        {
            LocalPosition = localPosition;
            LocalYawDegrees = localYawDegrees;
        }

        public Vector3 LocalPosition { get; }
        public float LocalYawDegrees { get; }
    }

    private readonly struct PoseFixture
    {
        public PoseFixture(
            GameObject root,
            XROrigin origin,
            Camera camera,
            Transform anchor,
            ExperimentStartSceneXrPoseAligner aligner)
        {
            Root = root;
            Origin = origin;
            Camera = camera;
            Anchor = anchor;
            Aligner = aligner;
        }

        public GameObject Root { get; }
        public XROrigin Origin { get; }
        public Camera Camera { get; }
        public Transform Anchor { get; }
        public ExperimentStartSceneXrPoseAligner Aligner { get; }
    }
}
