using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class P46O04ExperimentalLocomotionRecoveryPlayModeTests
{
    [UnitySetUp]
    public IEnumerator SetUp()
    {
        ExperimentSimulationPauseAuthority.ResetForTests();
        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        ExperimentRuntimePauseCoordinator coordinator = Object.FindFirstObjectByType<ExperimentRuntimePauseCoordinator>(FindObjectsInactive.Include);
        coordinator?.CleanupPauseState("p46o04_playmode_teardown");
        ExperimentSimulationPauseAuthority.ResetForTests();
        yield return null;
    }

    [UnityTest]
    public IEnumerator OrphanedZeroAtFinalSceneEntry_IsReleasedAndXriPolicyIsExclusive()
    {
        Time.timeScale = 0f;
        yield return LoadFinalSceneAndWaitForGate();

        ExperimentLocomotionStateReport report = ExperimentLocomotionStateGuard.RestoreAndValidate("p46o04_playmode_scene_entry", true);

        Assert.That(Time.timeScale, Is.EqualTo(1f));
        Assert.That(ExperimentSimulationPauseAuthority.ActiveOwnerCount, Is.Zero);
        Assert.That(report.IsValid, Is.True, report.FailureReason);
        Assert.That(report.EnabledMoveProviders, Is.EqualTo(1));
        Assert.That(report.TurnPolicy, Is.EqualTo(ExperimentTurnPolicy.Snap));
        Assert.That(report.EnabledSnapTurnProviders, Is.EqualTo(1));
        Assert.That(report.EnabledContinuousTurnProviders, Is.Zero);
        Assert.That(report.EnabledMoveActions, Is.GreaterThanOrEqualTo(1));
        Assert.That(report.EnabledExpectedTurnActions, Is.GreaterThanOrEqualTo(1));
        Assert.That(report.EnabledIncompatibleTurnActions, Is.Zero);
    }

    [UnityTest]
    public IEnumerator SimulatedMoveAndTurnInput_ChangesOriginWithoutCompetingTurnProvider()
    {
        yield return LoadFinalSceneAndWaitForGate();
        ExperimentLocomotionStateReport report = ExperimentLocomotionStateGuard.RestoreAndValidate("p46o04_playmode_input", true);
        Assert.That(report.IsValid, Is.True, report.FailureReason);

        XROrigin origin = Object.FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
        Assert.That(origin, Is.Not.Null);
        Vector3 positionBefore = origin.transform.position;

        Component moveProvider = FindComponentByTypeName("DynamicMoveProvider");
        Assert.That(moveProvider, Is.Not.Null);
        SetManualVector2Input(moveProvider, "leftHandMoveInput", Vector2.up);
        for (int frame = 0; frame < 20; frame++) yield return null;
        SetManualVector2Input(moveProvider, "leftHandMoveInput", Vector2.zero);
        yield return null;

        float horizontalMovement = Vector2.Distance(
            new Vector2(positionBefore.x, positionBefore.z),
            new Vector2(origin.transform.position.x, origin.transform.position.z));
        Assert.That(horizontalMovement, Is.GreaterThan(0.01f), "Move action did not produce gradual XR Origin displacement.");

        float yawBefore = origin.transform.eulerAngles.y;
        Component snapTurnProvider = FindComponentByTypeName("SnapTurnProvider");
        Assert.That(snapTurnProvider, Is.Not.Null);
        SetManualVector2Input(snapTurnProvider, "rightHandTurnInput", Vector2.right);
        yield return null;
        yield return null;
        SetManualVector2Input(snapTurnProvider, "rightHandTurnInput", Vector2.zero);
        yield return null;

        float yawDelta = Mathf.Abs(Mathf.DeltaAngle(yawBefore, origin.transform.eulerAngles.y));
        Assert.That(yawDelta, Is.GreaterThan(20f).And.LessThan(70f),
            "Turn input was missing or was applied by competing providers.");
    }

    [UnityTest]
    public IEnumerator PauseContinueAndStartSceneRecenterPath_RestoreRunningState()
    {
        yield return LoadScene("experiment_start_scene");
        ExperimentStartSceneXrPoseAligner aligner = Object.FindFirstObjectByType<ExperimentStartSceneXrPoseAligner>(FindObjectsInactive.Include);
        Assert.That(aligner, Is.Not.Null);
        aligner.RequestRealignment(ExperimentStartSceneAlignmentReason.PoseDriftObserved);
        yield return null;

        Time.timeScale = 0f;
        yield return LoadFinalSceneAndWaitForGate();
        Assert.That(Time.timeScale, Is.EqualTo(1f));

        ExperimentLocomotionStateReport beforePause = ExperimentLocomotionStateGuard.RestoreAndValidate("p46o04_before_pause", true);
        Assert.That(beforePause.IsValid, Is.True, beforePause.FailureReason);
        ExperimentRuntimePauseCoordinator coordinator = Object.FindFirstObjectByType<ExperimentRuntimePauseCoordinator>(FindObjectsInactive.Include);
        Assert.That(coordinator, Is.Not.Null);

        Assert.That(coordinator.EnterPause("p46o04_playmode_pause"), Is.True);
        Assert.That(Time.timeScale, Is.Zero);
        Assert.That(ExperimentSimulationPauseAuthority.ActiveOwnerCount, Is.EqualTo(1));

        Assert.That(coordinator.ResumeFromPause("p46o04_playmode_continue"), Is.True);
        Assert.That(Time.timeScale, Is.EqualTo(1f));
        Assert.That(ExperimentSimulationPauseAuthority.ActiveOwnerCount, Is.Zero);

        ExperimentLocomotionStateReport afterPause = ExperimentLocomotionStateGuard.RestoreAndValidate("p46o04_after_pause", true);
        Assert.That(afterPause.IsValid, Is.True, afterPause.FailureReason);
        Assert.That(afterPause.ProviderStates, Is.EqualTo(beforePause.ProviderStates));
    }

    private static IEnumerator LoadFinalSceneAndWaitForGate()
    {
        Type protocolUiType = Type.GetType("Autonomy.UnityIntegration.ExperimentRuntimeProtocolUI, Assembly-CSharp", throwOnError: true);
        protocolUiType.GetMethod("MarkLaunchFromStartScene", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        yield return LoadScene("final_scene");
        float deadline = Time.realtimeSinceStartup + 12f;
        Component protocolUi = null;
        while (Time.realtimeSinceStartup < deadline)
        {
            protocolUi = FindComponentByTypeName("ExperimentRuntimeProtocolUI");
            if (protocolUi != null && ReadBoolProperty(protocolUi, "ExperimentalXrGateReleased")) break;
            yield return null;
        }

        Assert.That(protocolUi, Is.Not.Null);
        Assert.That(ReadBoolProperty(protocolUi, "ExperimentalXrGateReleased"), Is.True, "Experimental XR gate did not release within the bounded timeout.");
        yield return null;
    }

    private static IEnumerator LoadScene(string sceneName)
    {
        AsyncOperation operation = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
        while (!operation.isDone) yield return null;
        yield return null;
    }

    private static Component FindComponentByTypeName(string typeName)
    {
        return Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .FirstOrDefault(component => component != null && string.Equals(component.GetType().Name, typeName, StringComparison.Ordinal));
    }

    private static bool ReadBoolProperty(Component component, string propertyName)
    {
        PropertyInfo property = component?.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        return property != null && property.GetValue(component) is bool value && value;
    }

    private static void SetManualVector2Input(Component provider, string propertyName, Vector2 value)
    {
        PropertyInfo inputProperty = provider.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.That(inputProperty, Is.Not.Null, $"{provider.GetType().Name}.{propertyName} was not found.");
        object inputReader = inputProperty.GetValue(provider);
        Assert.That(inputReader, Is.Not.Null);

        Type readerType = inputReader.GetType();
        PropertyInfo sourceModeProperty = readerType.GetProperty("inputSourceMode", BindingFlags.Instance | BindingFlags.Public);
        PropertyInfo manualValueProperty = readerType.GetProperty("manualValue", BindingFlags.Instance | BindingFlags.Public);
        Assert.That(sourceModeProperty, Is.Not.Null);
        Assert.That(manualValueProperty, Is.Not.Null);

        object manualMode = Enum.Parse(sourceModeProperty.PropertyType, "ManualValue");
        sourceModeProperty.SetValue(inputReader, manualMode);
        manualValueProperty.SetValue(inputReader, value);
    }
}
