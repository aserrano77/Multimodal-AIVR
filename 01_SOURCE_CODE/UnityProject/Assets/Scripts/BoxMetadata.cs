using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public class BoxMetadata : MonoBehaviour
{
    private const string EdgeFrameName = "BoxEdgeFrame";
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    public enum BoxType { A, B, C }

    [Header("Semantic")]
    public BoxType boxType;
    public string spokenLabel = string.Empty;
    public string displayLabel = string.Empty;
    public string voiceAlias = string.Empty;

    [Header("Runtime state")]
    public bool isGrabbed = false;
    public bool isDeposited = false;

    [Header("Visual feedback")]
    public Renderer targetRenderer;
    public Material depositedMaterial;

    private XRGrabInteractable grabInteractable;
    private Rigidbody rb;

    public bool IsSelected
    {
        get
        {
            if (grabInteractable == null)
                grabInteractable = GetComponent<XRGrabInteractable>();

            return grabInteractable != null && grabInteractable.isSelected;
        }
    }

    public bool IsGrabbedOrSelected => isGrabbed || IsSelected;

    private void Awake()
    {
        grabInteractable = GetComponent<XRGrabInteractable>();
        rb = GetComponent<Rigidbody>();

        if (targetRenderer == null)
            targetRenderer = FindCoreRenderer();
    }

    public void SetGrabbedState(bool grabbed)
    {
        if (isDeposited) return;
        isGrabbed = grabbed;
    }

    public void AssignVoiceLabels(string label)
    {
        string normalized = string.IsNullOrWhiteSpace(label) ? string.Empty : label.Trim().ToUpperInvariant();
        displayLabel = normalized;
        spokenLabel = normalized;
        voiceAlias = normalized;
    }

    public void LockAfterDeposit()
    {
        if (isDeposited) return;

        isDeposited = true;
        isGrabbed = false;

        if (grabInteractable != null)
            grabInteractable.enabled = false;

        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
        }

        ApplyDepositedVisualToBodyOnly();
    }

    public bool TryLockAfterDepositIfReleased()
    {
        if (isDeposited || IsGrabbedOrSelected)
            return false;

        LockAfterDeposit();
        return true;
    }

    private void ApplyDepositedVisualToBodyOnly()
    {
        if (targetRenderer == null || IsInsideEdgeFrame(targetRenderer.transform))
        {
            targetRenderer = FindCoreRenderer();
        }

        if (targetRenderer == null || depositedMaterial == null)
            return;

        if (TryGetMaterialColor(depositedMaterial, out Color depositedColor))
        {
            var block = new MaterialPropertyBlock();
            targetRenderer.GetPropertyBlock(block);
            Material shared = targetRenderer.sharedMaterial;
            if (shared != null && shared.HasProperty(BaseColorId))
                block.SetColor(BaseColorId, depositedColor);
            else if (shared != null && shared.HasProperty(ColorId))
                block.SetColor(ColorId, depositedColor);
            else
                block.SetColor(ColorId, depositedColor);

            targetRenderer.SetPropertyBlock(block);
            return;
        }

        targetRenderer.material = depositedMaterial;
    }

    private static bool TryGetMaterialColor(Material material, out Color color)
    {
        if (material != null && material.HasProperty(BaseColorId))
        {
            color = material.GetColor(BaseColorId);
            return true;
        }

        if (material != null && material.HasProperty(ColorId))
        {
            color = material.GetColor(ColorId);
            return true;
        }

        color = default;
        return false;
    }

    private Renderer FindCoreRenderer()
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null)
                continue;

            if (renderer.GetComponent<TMPro.TextMeshPro>() != null)
                continue;

            if (IsInsideEdgeFrame(renderer.transform))
                continue;

            return renderer;
        }

        return null;
    }

    private bool IsInsideEdgeFrame(Transform candidate)
    {
        Transform current = candidate;
        while (current != null && current != transform)
        {
            if (current.name == EdgeFrameName)
                return true;

            current = current.parent;
        }

        return false;
    }
}
