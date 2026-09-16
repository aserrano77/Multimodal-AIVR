using System;
using System.Collections;
using System.Reflection;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class P46O02ExperimentalXrPoseAlignmentPlayModeTests
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
    public IEnumerator LocalOffsetsCardinalYawAndDifferentHeights_ConvergeWithoutWritingCameraLocalPose()
    {
        var cases = new[]
        {
            new SimulatedPose(new Vector3(0.42f, 1.25f, -0.31f), 90f),
            new SimulatedPose(new Vector3(-0.65f, 1.55f, 0.27f), 180f),
            new SimulatedPose(new Vector3(1.1f, 1.92f, 0.8f), 270f),
            new SimulatedPose(new Vector3(-0.2f, 2.05f, -1.3f), 0f)
        };

        foreach (SimulatedPose simulated in cases)
        {
            PoseFixture fixture = CreateFixture(simulated.LocalPosition, simulated.LocalYawDegrees);
            Vector3 cameraLocalPositionBefore = fixture.Camera.transform.localPosition;
            Quaternion cameraLocalRotationBefore = fixture.Camera.transform.localRotation;
            float expectedCameraHeight = fixture.FloorAnchor.position.y + simulated.LocalPosition.y;

            bool aligned = fixture.Resetter.TryAlignCameraToExperimentalAnchor(
                out _, out float horizontalError, out float yawError);

            Assert.That(aligned, Is.True);
            Assert.That(horizontalError, Is.LessThan(0.0001f));
            Assert.That(yawError, Is.LessThan(0.0001f));
            Assert.That(fixture.Camera.transform.position.x, Is.EqualTo(fixture.HeadAnchor.position.x).Within(0.0001f));
            Assert.That(fixture.Camera.transform.position.z, Is.EqualTo(fixture.HeadAnchor.position.z).Within(0.0001f));
            Assert.That(fixture.Camera.transform.position.y, Is.EqualTo(expectedCameraHeight).Within(0.0001f));
            Assert.That(fixture.Origin.transform.position.y, Is.EqualTo(fixture.FloorAnchor.position.y).Within(0.0001f));
            Assert.That(fixture.Camera.transform.localPosition, Is.EqualTo(cameraLocalPositionBefore));
            Assert.That(Quaternion.Angle(fixture.Camera.transform.localRotation, cameraLocalRotationBefore), Is.LessThan(0.0001f));

            Vector3 originPositionAfterFirst = fixture.Origin.transform.position;
            Quaternion originRotationAfterFirst = fixture.Origin.transform.rotation;
            Assert.That(fixture.Resetter.TryAlignCameraToExperimentalAnchor(out _, out horizontalError, out yawError), Is.True);
            Assert.That(Vector3.Distance(fixture.Origin.transform.position, originPositionAfterFirst), Is.LessThan(0.0001f));
            Assert.That(Quaternion.Angle(fixture.Origin.transform.rotation, originRotationAfterFirst), Is.LessThan(0.0001f));
            Assert.That(fixture.Camera.transform.localPosition, Is.EqualTo(cameraLocalPositionBefore));
            Assert.That(Quaternion.Angle(fixture.Camera.transform.localRotation, cameraLocalRotationBefore), Is.LessThan(0.0001f));

            Object.Destroy(fixture.Root);
            _testRoot = null;
            yield return null;
        }
    }

    [UnityTest]
    public IEnumerator MissingTracking_ReleasesGateFailSoftExactlyOnce()
    {
        PoseFixture fixture = CreateFixture(new Vector3(0.2f, 1.7f, -0.4f), 90f);
        fixture.Resetter.ConfigureForTests(
            fixture.Origin, fixture.FloorAnchor, fixture.HeadAnchor,
            trackingTimeoutSeconds: 0.05f, stableFramesRequired: 2,
            maxAlignmentAttempts: 2, maxTrackingOriginUpdates: 2);
        int releaseCount = 0;
        fixture.Resetter.AlignmentGateReleased += (_, _) => releaseCount++;

        ExperimentXrRigAlignmentResult result = null;
        yield return fixture.Resetter.AlignBeforeProtocolUiCoroutine(value => result = value);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.TimedOut, Is.True);
        Assert.That(result.FailureReason, Does.StartWith("tracking_timeout:"));
        Assert.That(releaseCount, Is.EqualTo(1));
        Assert.That(fixture.Resetter.IsAlignmentWindowActive, Is.False);
    }

    [UnityTest]
    public IEnumerator TrackingOriginUpdates_AreBoundedAndIgnoredAfterGateRelease()
    {
        PoseFixture fixture = CreateFixture(new Vector3(0f, 1.7f, 0f), 0f);
        fixture.Resetter.ConfigureForTests(
            fixture.Origin, fixture.FloorAnchor, fixture.HeadAnchor,
            trackingTimeoutSeconds: 0.08f, stableFramesRequired: 2,
            maxAlignmentAttempts: 2, maxTrackingOriginUpdates: 2);
        MethodInfo registerUpdate = typeof(ExperimentXrRigResetter).GetMethod(
            "RegisterTrackingOriginUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(registerUpdate, Is.Not.Null);

        Coroutine alignment = fixture.Resetter.StartCoroutine(fixture.Resetter.AlignBeforeProtocolUiCoroutine());
        yield return null;
        for (int i = 0; i < 8; i++)
        {
            registerUpdate.Invoke(fixture.Resetter, null);
        }

        Assert.That(fixture.Resetter.TrackingOriginUpdateCount, Is.EqualTo(2));
        float deadline = Time.realtimeSinceStartup + 1f;
        while (fixture.Resetter.IsAlignmentWindowActive && Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }

        Assert.That(fixture.Resetter.IsAlignmentWindowActive, Is.False);
        int countAfterRelease = fixture.Resetter.TrackingOriginUpdateCount;
        registerUpdate.Invoke(fixture.Resetter, null);
        Assert.That(fixture.Resetter.TrackingOriginUpdateCount, Is.EqualTo(countAfterRelease));
        fixture.Resetter.StopCoroutine(alignment);
    }

    [UnityTest]
    public IEnumerator RealStartToExperimentalTransition_KeepsUiAndBoxesBehindAlignmentGate()
    {
        AsyncOperation startLoad = SceneManager.LoadSceneAsync("experiment_start_scene", LoadSceneMode.Single);
        while (!startLoad.isDone)
        {
            yield return null;
        }

        Type protocolUiType = Type.GetType(
            "Autonomy.UnityIntegration.ExperimentRuntimeProtocolUI, Assembly-CSharp", throwOnError: true);
        protocolUiType.GetMethod("MarkLaunchFromStartScene", BindingFlags.Public | BindingFlags.Static)
            ?.Invoke(null, null);
        AsyncOperation experimentLoad = SceneManager.LoadSceneAsync("final_scene", LoadSceneMode.Single);
        while (!experimentLoad.isDone)
        {
            yield return null;
        }

        yield return null;
        ExperimentXrRigResetter resetter = Object.FindFirstObjectByType<ExperimentXrRigResetter>(FindObjectsInactive.Include);
        Component protocolUi = FindComponentByTypeName("ExperimentRuntimeProtocolUI");
        Component spawnManager = FindComponentByTypeName("SpawnManager");
        Assert.That(resetter, Is.Not.Null);
        Assert.That(protocolUi, Is.Not.Null);
        Canvas protocolCanvas = protocolUi.GetComponent<Canvas>();
        Assert.That(protocolCanvas, Is.Not.Null);
        Assert.That(protocolCanvas.enabled, Is.False);
        Assert.That(ReadActiveRoundChildCount(spawnManager), Is.EqualTo(0));

        float deadline = Time.realtimeSinceStartup + 12f;
        while (resetter.LastResult == null && Time.realtimeSinceStartup < deadline)
        {
            Assert.That(protocolCanvas.enabled, Is.False, "Protocol UI escaped the XR alignment gate.");
            Assert.That(ReadActiveRoundChildCount(spawnManager), Is.EqualTo(0),
                "Boxes were generated before the XR alignment gate released.");
            yield return null;
        }

        Assert.That(resetter.LastResult, Is.Not.Null);
        yield return null;
        Assert.That(protocolCanvas.enabled, Is.True, "Fail-soft alignment completion did not release the protocol UI.");
        Assert.That(ReadActiveRoundChildCount(spawnManager), Is.EqualTo(0));
    }

    private static Component FindComponentByTypeName(string typeName)
    {
        MonoBehaviour[] behaviours = Object.FindObjectsByType<MonoBehaviour>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (MonoBehaviour behaviour in behaviours)
        {
            if (behaviour != null && string.Equals(behaviour.GetType().Name, typeName, StringComparison.Ordinal))
            {
                return behaviour;
            }
        }

        return null;
    }

    private static int ReadActiveRoundChildCount(Component spawnManager)
    {
        if (spawnManager == null)
        {
            return 0;
        }

        PropertyInfo property = spawnManager.GetType().GetProperty(
            "ActiveRoundChildCount", BindingFlags.Instance | BindingFlags.Public);
        return property != null ? (int)property.GetValue(spawnManager) : 0;
    }

    private PoseFixture CreateFixture(Vector3 cameraLocalPosition, float cameraLocalYawDegrees)
    {
        _testRoot = new GameObject("P46O02_TestRoot");
        GameObject originObject = new GameObject("XR Origin");
        originObject.transform.SetParent(_testRoot.transform, false);
        originObject.transform.position = new Vector3(8f, 4.25f, -7f);
        originObject.transform.rotation = Quaternion.Euler(0f, 37f, 0f);
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

        GameObject floorAnchorObject = new GameObject("ParticipantStartAnchor");
        floorAnchorObject.transform.SetParent(_testRoot.transform, false);
        floorAnchorObject.transform.SetPositionAndRotation(
            new Vector3(-6.9f, 0.35f, -4.6598907f), Quaternion.Euler(0f, 22f, 0f));
        GameObject headAnchorObject = new GameObject("ExperimentalHeadPoseAnchor");
        headAnchorObject.transform.SetParent(floorAnchorObject.transform, false);
        headAnchorObject.transform.localPosition = new Vector3(0f, 1.7f, 0f);
        headAnchorObject.transform.localRotation = Quaternion.identity;

        ExperimentXrRigResetter resetter = _testRoot.AddComponent<ExperimentXrRigResetter>();
        resetter.ConfigureForTests(origin, floorAnchorObject.transform, headAnchorObject.transform);
        return new PoseFixture(
            _testRoot, origin, camera, floorAnchorObject.transform, headAnchorObject.transform, resetter);
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
            GameObject root, XROrigin origin, Camera camera, Transform floorAnchor,
            Transform headAnchor, ExperimentXrRigResetter resetter)
        {
            Root = root;
            Origin = origin;
            Camera = camera;
            FloorAnchor = floorAnchor;
            HeadAnchor = headAnchor;
            Resetter = resetter;
        }

        public GameObject Root { get; }
        public XROrigin Origin { get; }
        public Camera Camera { get; }
        public Transform FloorAnchor { get; }
        public Transform HeadAnchor { get; }
        public ExperimentXrRigResetter Resetter { get; }
    }
}
