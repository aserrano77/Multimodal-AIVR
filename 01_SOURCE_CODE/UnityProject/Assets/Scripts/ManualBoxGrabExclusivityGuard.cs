using Autonomy.Domain;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

[DisallowMultipleComponent]
[RequireComponent(typeof(XRGrabInteractable))]
public sealed class ManualBoxGrabExclusivityGuard : MonoBehaviour, IXRSelectFilter
{
    private static readonly ManualBoxGrabExclusivityState<ManualBoxGrabExclusivityGuard, IXRSelectInteractor> LockState = new();

    [SerializeField] private bool diagnosticsEnabled = true;

    private XRGrabInteractable grabInteractable;
    private bool registered;

    public bool canProcess => isActiveAndEnabled;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        LockState.Clear();
    }

    private void Awake()
    {
        grabInteractable = GetComponent<XRGrabInteractable>();
    }

    private void OnEnable()
    {
        EnsureRegistered();
    }

    private void OnDisable()
    {
        ReleaseAllManualSelectors();
        Unregister();
    }

    public bool Process(IXRSelectInteractor interactor, IXRSelectInteractable interactable)
    {
        if (!IsManualInteractor(interactor))
            return true;

        bool allowed = LockState.CanSelect(this);
        if (!allowed)
        {
            LogEvent(
                "selection_rejected_different_box",
                $"ManualBoxGrabExclusivityGuard: rejected manual selection of '{name}' by '{DescribeInteractor(interactor)}' because '{DescribeBox(LockState.ActiveBox)}' is already held.",
                this);
        }

        return allowed;
    }

    internal static bool IsManualInteractor(IXRSelectInteractor interactor)
    {
        return interactor is XRBaseInputInteractor && interactor is not XRSocketInteractor;
    }

    internal static void InstallOnSceneBoxes()
    {
        XRGrabInteractable[] interactables = FindObjectsByType<XRGrabInteractable>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (XRGrabInteractable interactable in interactables)
        {
            if (interactable == null || !IsLogisticsBox(interactable.gameObject))
                continue;

            EnsureInstalled(interactable.gameObject);
        }
    }

    internal static void EnsureInstalled(GameObject candidate)
    {
        if (candidate == null || !IsLogisticsBox(candidate))
            return;

        if (candidate.GetComponent<XRGrabInteractable>() == null)
            return;

        if (candidate.GetComponent<ManualBoxGrabExclusivityGuard>() == null)
            candidate.AddComponent<ManualBoxGrabExclusivityGuard>();
    }

    private static bool IsLogisticsBox(GameObject candidate)
    {
        if (candidate == null)
            return false;

        if (candidate.GetComponent<BoxMetadata>() != null)
            return true;

        string objectName = candidate.name;
        return objectName.Contains("box") ||
            objectName.Contains("Box") ||
            objectName.Contains("Cardboard_Box") ||
            objectName.Contains("Stack_Box");
    }

    private void EnsureRegistered()
    {
        if (registered)
            return;

        if (grabInteractable == null)
            grabInteractable = GetComponent<XRGrabInteractable>();

        if (grabInteractable == null)
            return;

        grabInteractable.selectFilters.Add(this);
        grabInteractable.selectEntered.AddListener(OnSelectEntered);
        grabInteractable.selectExited.AddListener(OnSelectExited);
        registered = true;
    }

    private void Unregister()
    {
        if (!registered || grabInteractable == null)
            return;

        grabInteractable.selectFilters.Remove(this);
        grabInteractable.selectEntered.RemoveListener(OnSelectEntered);
        grabInteractable.selectExited.RemoveListener(OnSelectExited);
        registered = false;
    }

    private void OnSelectEntered(SelectEnterEventArgs args)
    {
        IXRSelectInteractor interactor = args.interactorObject;
        if (!IsManualInteractor(interactor))
            return;

        ManualBoxGrabSelectionResult result = LockState.Select(this, interactor);
        switch (result)
        {
            case ManualBoxGrabSelectionResult.LockAcquired:
                LogEvent("lock_acquired", $"ManualBoxGrabExclusivityGuard: lock acquired by '{name}' via '{DescribeInteractor(interactor)}'.", this);
                break;
            case ManualBoxGrabSelectionResult.SameBoxAllowed:
                LogEvent("same_box_allowed", $"ManualBoxGrabExclusivityGuard: same-box manual selection allowed for '{name}' via '{DescribeInteractor(interactor)}'.", this);
                break;
        }
    }

    private void OnSelectExited(SelectExitEventArgs args)
    {
        IXRSelectInteractor interactor = args.interactorObject;
        if (!IsManualInteractor(interactor))
            return;

        ManualBoxGrabReleaseResult result = LockState.Release(this, interactor);
        if (result == ManualBoxGrabReleaseResult.LockReleased)
            LogEvent("lock_released", $"ManualBoxGrabExclusivityGuard: lock released by '{name}'.", this);
    }

    private void ReleaseAllManualSelectors()
    {
        if (grabInteractable == null || !grabInteractable.isSelected)
            return;

        for (int i = grabInteractable.interactorsSelecting.Count - 1; i >= 0; i--)
        {
            IXRSelectInteractor interactor = grabInteractable.interactorsSelecting[i];
            if (IsManualInteractor(interactor) && LockState.Release(this, interactor) == ManualBoxGrabReleaseResult.LockReleased)
                LogEvent("lock_released", $"ManualBoxGrabExclusivityGuard: lock released by disabled '{name}'.", this);
        }
    }

    private void LogEvent(string eventName, string message, Object context)
    {
        if (!diagnosticsEnabled)
            return;

        Debug.Log($"[P46C-01:{eventName}] {message}", context);
    }

    private static string DescribeBox(ManualBoxGrabExclusivityGuard box)
    {
        return box != null ? box.name : string.Empty;
    }

    private static string DescribeInteractor(IXRSelectInteractor interactor)
    {
        return interactor?.transform != null ? interactor.transform.name : string.Empty;
    }
}
