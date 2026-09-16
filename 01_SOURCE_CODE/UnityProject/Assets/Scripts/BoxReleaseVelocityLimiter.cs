using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(XRGrabInteractable))]
public sealed class BoxReleaseVelocityLimiter : MonoBehaviour
{
    [SerializeField, Min(0f)] private float maxReleaseHorizontalSpeed = 0.65f;
    [SerializeField, Min(0f)] private float maxReleaseUpwardSpeed = 0.15f;
    [SerializeField, Min(0f)] private float maxReleaseAngularSpeed = 2.5f;

    private Rigidbody rb;
    private XRGrabInteractable grabInteractable;

    public float MaxReleaseHorizontalSpeed => maxReleaseHorizontalSpeed;
    public float MaxReleaseUpwardSpeed => maxReleaseUpwardSpeed;
    public float MaxReleaseAngularSpeed => maxReleaseAngularSpeed;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        grabInteractable = GetComponent<XRGrabInteractable>();
    }

    private void OnEnable()
    {
        if (grabInteractable == null)
            grabInteractable = GetComponent<XRGrabInteractable>();

        if (grabInteractable != null)
            grabInteractable.selectExited.AddListener(OnSelectExited);
    }

    private void OnDisable()
    {
        if (grabInteractable != null)
            grabInteractable.selectExited.RemoveListener(OnSelectExited);
    }

    private void OnSelectExited(SelectExitEventArgs args)
    {
        if (rb == null)
            rb = GetComponent<Rigidbody>();

        if (rb == null || rb.isKinematic)
            return;

        rb.linearVelocity = ClampReleaseVelocity(rb.linearVelocity);
        rb.angularVelocity = ClampMagnitude(rb.angularVelocity, maxReleaseAngularSpeed);
    }

    private Vector3 ClampReleaseVelocity(Vector3 velocity)
    {
        Vector3 horizontal = new(velocity.x, 0f, velocity.z);
        horizontal = ClampMagnitude(horizontal, maxReleaseHorizontalSpeed);

        float vertical = velocity.y;
        if (vertical > maxReleaseUpwardSpeed)
            vertical = maxReleaseUpwardSpeed;

        return new Vector3(horizontal.x, vertical, horizontal.z);
    }

    private static Vector3 ClampMagnitude(Vector3 value, float maxMagnitude)
    {
        if (maxMagnitude <= 0f)
            return Vector3.zero;

        float sqrMagnitude = value.sqrMagnitude;
        float maxSqrMagnitude = maxMagnitude * maxMagnitude;
        return sqrMagnitude > maxSqrMagnitude ? value.normalized * maxMagnitude : value;
    }
}
