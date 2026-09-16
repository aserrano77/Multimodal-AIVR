using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class P47ARuntimeSafetyPlayModeTests
{
    private GameObject _roundObject;
    private GameObject _boxObject;

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (_roundObject != null)
        {
            Object.Destroy(_roundObject);
        }

        if (_boxObject != null)
        {
            Object.Destroy(_boxObject);
        }

        yield return null;
    }

    [UnityTest]
    public IEnumerator ReleasedDepositRegistration_RejectsHeldBoxThenCompletesAfterRelease()
    {
        Type roundManagerType = Type.GetType("RoundManager, Assembly-CSharp", throwOnError: true);
        Type boxMetadataType = Type.GetType("BoxMetadata, Assembly-CSharp", throwOnError: true);
        _roundObject = new GameObject("P47A Round Manager");
        _boxObject = new GameObject("P47A Box");
        Component roundManager = _roundObject.AddComponent(roundManagerType);
        Component box = _boxObject.AddComponent(boxMetadataType);

        roundManagerType.GetField("autoStartNextRound")?.SetValue(roundManager, false);
        roundManagerType.GetMethod("StartRound")?.Invoke(roundManager, new object[] { 1 });
        FieldInfo grabbedField = boxMetadataType.GetField("isGrabbed");
        FieldInfo depositedField = boxMetadataType.GetField("isDeposited");
        MethodInfo registerReleased = roundManagerType.GetMethod("TryRegisterReleasedCorrectDeposit");
        PropertyInfo depositedCount = roundManagerType.GetProperty("DepositedBoxes");
        PropertyInfo roundFinished = roundManagerType.GetProperty("RoundFinished");

        grabbedField?.SetValue(box, true);
        object[] heldArguments = { box, null };
        bool registeredWhileHeld = (bool)registerReleased.Invoke(roundManager, heldArguments);

        Assert.That(registeredWhileHeld, Is.False);
        Assert.That(heldArguments[1], Is.EqualTo("box_still_grabbed_or_selected"));
        Assert.That(depositedCount?.GetValue(roundManager), Is.EqualTo(0));
        Assert.That(depositedField?.GetValue(box), Is.False);
        Assert.That(roundFinished?.GetValue(roundManager), Is.False);

        grabbedField?.SetValue(box, false);
        object[] releasedArguments = { box, null };
        bool registeredAfterRelease = (bool)registerReleased.Invoke(roundManager, releasedArguments);

        Assert.That(registeredAfterRelease, Is.True);
        Assert.That(releasedArguments[1], Is.EqualTo(string.Empty));
        Assert.That(depositedCount?.GetValue(roundManager), Is.EqualTo(1));
        Assert.That(depositedField?.GetValue(box), Is.True);
        Assert.That(roundFinished?.GetValue(roundManager), Is.True);
        yield return null;
    }
}
