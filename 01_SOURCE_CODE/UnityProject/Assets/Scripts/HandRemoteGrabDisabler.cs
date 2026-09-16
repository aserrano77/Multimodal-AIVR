using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;

/// <summary>
/// Disables hand far-cast/ray manipulation so hand mode only allows near/contact interaction.
/// </summary>
[DefaultExecutionOrder(-100)]
public class HandRemoteGrabDisabler : MonoBehaviour
{
    [SerializeField] private bool applyOnAwake = true;
    [SerializeField] private bool enforceContinuously = true;
    [SerializeField] private float enforcementIntervalSeconds = 0.5f;
    [SerializeField] private bool logChanges = false;

    private float nextEnforcementTime;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void ApplyAfterSceneLoad()
    {
        HandRemoteGrabDisabler existing = FindAnyObjectByType<HandRemoteGrabDisabler>(FindObjectsInactive.Include);
        if (existing != null)
        {
            existing.Apply();
            return;
        }

        GameObject guardObject = new GameObject(nameof(HandRemoteGrabDisabler));
        DontDestroyOnLoad(guardObject);
        guardObject.AddComponent<HandRemoteGrabDisabler>().Apply();
    }

    private void Awake()
    {
        if (applyOnAwake)
            Apply();
    }

    private void OnEnable()
    {
        if (applyOnAwake)
            Apply();
    }

    private void Update()
    {
        if (!enforceContinuously || Time.unscaledTime < nextEnforcementTime)
            return;

        nextEnforcementTime = Time.unscaledTime + Mathf.Max(0.1f, enforcementIntervalSeconds);
        Apply();
    }

    [ContextMenu("Apply Hand Direct-Only Interaction")]
    public void Apply()
    {
        DisableHandNearFarInteractors();
        DisableHandRayInteractors();
    }

    private void DisableHandNearFarInteractors()
    {
        NearFarInteractor[] interactors = FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (NearFarInteractor interactor in interactors)
        {
            if (interactor == null || !IsHandInteractor(interactor.transform))
                continue;

            interactor.enableFarCasting = false;
            DisableLineVisuals(interactor.transform);

            if (logChanges)
                Debug.Log($"HandRemoteGrabDisabler: disabled far casting on {GetPath(interactor.transform)}", interactor);
        }
    }

    private void DisableHandRayInteractors()
    {
        XRRayInteractor[] rayInteractors = FindObjectsByType<XRRayInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (XRRayInteractor rayInteractor in rayInteractors)
        {
            if (rayInteractor == null || !IsHandInteractor(rayInteractor.transform))
                continue;

            rayInteractor.enabled = false;
            DisableLineVisuals(rayInteractor.transform);

            if (logChanges)
                Debug.Log($"HandRemoteGrabDisabler: disabled hand ray interactor on {GetPath(rayInteractor.transform)}", rayInteractor);
        }
    }

    private void DisableLineVisuals(Transform root)
    {
        XRInteractorLineVisual[] lineVisuals = root.GetComponentsInChildren<XRInteractorLineVisual>(true);
        foreach (XRInteractorLineVisual lineVisual in lineVisuals)
            lineVisual.enabled = false;

        CurveVisualController[] curveVisuals = root.GetComponentsInChildren<CurveVisualController>(true);
        foreach (CurveVisualController curveVisual in curveVisuals)
            curveVisual.enabled = false;

        LineRenderer[] lineRenderers = root.GetComponentsInChildren<LineRenderer>(true);
        foreach (LineRenderer lineRenderer in lineRenderers)
            lineRenderer.enabled = false;
    }

    private bool IsHandInteractor(Transform candidate)
    {
        Transform current = candidate;
        while (current != null)
        {
            string objectName = current.name;
            if (objectName.Contains("Hand") || objectName.Contains("Near-Far Interactor"))
                return true;

            current = current.parent;
        }

        return false;
    }

    private string GetPath(Transform transform)
    {
        if (transform == null)
            return string.Empty;

        string path = transform.name;
        Transform current = transform.parent;
        while (current != null)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }

        return path;
    }
}
