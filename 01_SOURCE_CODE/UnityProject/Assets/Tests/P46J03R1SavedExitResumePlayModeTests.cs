using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

public sealed class P46J03R1SavedExitResumePlayModeTests
{
    private const string StartScene = "experiment_start_scene";
    private const string ProtocolScene = "final_scene";
    private const int AssignmentSeed = 4;
    private string _tempDirectory;
    private string _assignmentPath;
    private readonly List<string> _runtimeExceptions = new List<string>();

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "P46J03R1PlayMode_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _assignmentPath = Path.Combine(_tempDirectory, "condition_order_assignments.json");
        ExperimentConditionOrderAssignmentStore.OverrideDefaultPathForTests(_assignmentPath);
        ExperimentConditionOrderAssignmentStore.OverrideBaseSeedForTests(AssignmentSeed);
        ExperimentDataPathResolver.ResetForTests(_tempDirectory);
        _runtimeExceptions.Clear();
        Application.logMessageReceived += CaptureRuntimeException;
        LogAssert.ignoreFailingMessages = true;
        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        LogAssert.ignoreFailingMessages = true;
        Application.logMessageReceived -= CaptureRuntimeException;
        if (Application.isPlaying && SceneManager.GetActiveScene().name != StartScene)
        {
            yield return SceneManager.LoadSceneAsync(StartScene, LoadSceneMode.Single);
            yield return null;
        }

        ExperimentDataPathResolver.ResetForTests(null);
        ExperimentSessionIdHistoryStore.OverrideDefaultPathForTests(null);
        ExperimentConditionOrderAssignmentStore.OverrideDefaultPathForTests(null);
        ExperimentConditionOrderAssignmentStore.OverrideBaseSeedForTests(null);
        if (!string.IsNullOrWhiteSpace(_tempDirectory) && Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Unity may still be disposing the runtime logger at the end of the frame.
            }
        }

        LogAssert.ignoreFailingMessages = false;
    }

    [UnityTest]
    public IEnumerator ContinueFromSavedPrueba_LoadsActualProtocolUiAndLeavesPreparing()
    {
        LogAssert.ignoreFailingMessages = true;
        const string participantId = "U20260715_071930";
        ExperimentConditionOrderAssignment assignment =
            ExperimentConditionOrderAssignmentStore.GetOrCreate(participantId);
        Assert.That(assignment.sequence_prefix, Is.EqualTo("D"));
        string[] checkpointOrder = assignment.condition_order_ids.ToArray();
        ExperimentDataPathResolver.SessionContext session = ExperimentDataPathResolver.ConfigureSession(
            participantId,
            "S20260715_071930",
            checkpointOrder,
            roundsPerCondition: 2);
        ExperimentSessionIdHistoryStore.UpdateSavedExitCheckpoint(
            session.SessionId,
            checkpointOrder[0],
            visiblePrueba: 1,
            roundIndex: 2,
            conditionOrder: ExperimentCompensatedConditionOrder.FormatOrder(checkpointOrder),
            resumePolicy: "condition_start",
            conditionOrderIds: checkpointOrder,
            conditionOrderIndex: 0,
            internalTrialAttemptIndex: 1,
            partialTrialCloseReason: ExperimentSessionIdHistoryStore.SavedExitIncompleteConditionRestartReason);
        ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.SavedExitStatus);

        yield return SceneManager.LoadSceneAsync(StartScene, LoadSceneMode.Single);
        yield return WaitForComponent("ExperimentRuntimeStartScreenUI", 120);

        Button resumeButton = FindButtonByLabel("Continuar desde Prueba 1");
        Assert.That(resumeButton, Is.Not.Null, "La ruta real de StartScreen no mostró la acción de recuperación.");
        Assert.That(resumeButton.interactable, Is.True);
        Assert.That((Color32)resumeButton.colors.normalColor, Is.EqualTo(new Color32(0x00, 0x6E, 0xA6, 0xFF)));

        resumeButton.onClick.Invoke();
        for (int frame = 0; frame < 600 && SceneManager.GetActiveScene().name != ProtocolScene; frame++)
        {
            yield return null;
        }

        Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo(ProtocolScene));
        Component protocolUi = null;
        for (int frame = 0; frame < 600; frame++)
        {
            protocolUi = FindLoadedComponent("ExperimentRuntimeProtocolUI");
            if (protocolUi != null && string.Equals(ReadPrivateField(protocolUi, "_screen")?.ToString(), "Instructions", StringComparison.Ordinal))
            {
                break;
            }

            yield return null;
        }

        Assert.That(protocolUi, Is.Not.Null);
        Assert.That(ReadPrivateField(protocolUi, "_screen")?.ToString(), Is.EqualTo("Instructions"),
            "La recuperación real no abandonó Preparing/Start.");

        Component orchestrator = FindLoadedComponent("ExperimentSessionOrchestrator");
        Assert.That(orchestrator, Is.Not.Null);
        object snapshot = orchestrator.GetType().GetMethod("GetRuntimeProtocolSnapshot", BindingFlags.Instance | BindingFlags.Public)
            ?.Invoke(orchestrator, null);
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(ReadPublicProperty<string>(snapshot, "NextConditionId"), Is.EqualTo(ExperimentCompensatedConditionOrder.C10));
        Assert.That(ReadPublicProperty<int>(snapshot, "NextVisiblePruebaNumber"), Is.EqualTo(1));
        Assert.That(ReadPublicProperty<int>(snapshot, "NextConditionOrderIndex"), Is.EqualTo(0));
        Assert.That(ReadPublicProperty<int>(snapshot, "NextRoundIndexWithinCondition"), Is.EqualTo(1));

        Button beginButton = FindButtonByLabel("Comenzar la prueba");
        Assert.That(beginButton, Is.Not.Null);
        Assert.That(beginButton.interactable, Is.True);
        Assert.That((Color32)beginButton.colors.normalColor, Is.EqualTo(new Color32(0x00, 0x6E, 0xA6, 0xFF)));
        Assert.That(ExperimentInstructionClipResolver.Resolve(
                ReadPublicProperty<string>(snapshot, "NextConditionId"),
                "Prueba " + ReadPublicProperty<int>(snapshot, "NextVisiblePruebaNumber")),
            Is.EqualTo("instruction_prueba_1_robot_autonomous"));
        CollectionAssert.AreEqual(checkpointOrder, ReadPublicProperty<IEnumerable>(snapshot, "ConditionOrderIds"));
        Assert.That(_runtimeExceptions, Is.Empty, "La ruta de recuperación emitió una excepción runtime.");
    }

    private static IEnumerator WaitForComponent(string typeName, int maxFrames)
    {
        for (int frame = 0; frame < maxFrames && FindLoadedComponent(typeName) == null; frame++)
        {
            yield return null;
        }
    }

    private static Component FindLoadedComponent(string typeName)
    {
        Type type = Type.GetType("Autonomy.UnityIntegration." + typeName + ", Assembly-CSharp");
        if (type == null)
        {
            return null;
        }

        foreach (UnityEngine.Object candidate in Resources.FindObjectsOfTypeAll(type))
        {
            if (candidate is Component component && component.gameObject.scene.isLoaded)
            {
                return component;
            }
        }

        return null;
    }

    private static Button FindButtonByLabel(string expectedLabel)
    {
        foreach (Button button in Resources.FindObjectsOfTypeAll<Button>())
        {
            if (button == null || !button.gameObject.scene.isLoaded || !button.gameObject.activeInHierarchy)
            {
                continue;
            }

            foreach (Component component in button.GetComponentsInChildren<Component>(includeInactive: true))
            {
                PropertyInfo textProperty = component?.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (textProperty?.PropertyType == typeof(string) &&
                    string.Equals(textProperty.GetValue(component) as string, expectedLabel, StringComparison.Ordinal))
                {
                    return button;
                }
            }
        }

        return null;
    }

    private static object ReadPrivateField(Component component, string fieldName)
    {
        return component.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(component);
    }

    private static T ReadPublicProperty<T>(object target, string propertyName)
    {
        return (T)target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target);
    }

    private void CaptureRuntimeException(string condition, string stackTrace, LogType type)
    {
        if (type == LogType.Exception ||
            condition.Contains("NullReferenceException") ||
            condition.Contains("MissingReferenceException") ||
            condition.Contains("InvalidOperationException") ||
            condition.Contains("ArgumentException"))
        {
            _runtimeExceptions.Add(condition + "\n" + stackTrace);
        }
    }
}
