using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[RequireComponent(typeof(XRGrabInteractable))]
public class BoxGrabStateSync : MonoBehaviour
{
    private XRGrabInteractable grabInteractable;
    private BoxMetadata metadata;

    private void Awake()
    {
        grabInteractable = GetComponent<XRGrabInteractable>();
        metadata = GetComponent<BoxMetadata>();
    }

    private void OnEnable()
    {
        grabInteractable.selectEntered.AddListener(OnGrab);
        grabInteractable.selectExited.AddListener(OnRelease);
    }

    private void OnDisable()
    {
        grabInteractable.selectEntered.RemoveListener(OnGrab);
        grabInteractable.selectExited.RemoveListener(OnRelease);
    }

    private void OnGrab(SelectEnterEventArgs args)
    {
        if (metadata != null)
            metadata.SetGrabbedState(true);
    }

    private void OnRelease(SelectExitEventArgs args)
    {
        if (metadata != null)
            metadata.SetGrabbedState(false);
    }
}