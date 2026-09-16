using UnityEngine;

[DisallowMultipleComponent]
public class RobotMovementDebugger : MonoBehaviour
{
    [SerializeField] private bool drawMovementGizmos = false;

    public KeyCode forward = KeyCode.Keypad8;
    public KeyCode back = KeyCode.Keypad5;
    public KeyCode left = KeyCode.Keypad4;
    public KeyCode right = KeyCode.Keypad6;
    public KeyCode speedUp = KeyCode.Keypad9;
    public KeyCode slowDown = KeyCode.Keypad7;

    public Vector3 requestedMoveVector;
    public Vector3 lastDeltaPosition;
    public Vector3 lastVelocity;

    private Vector3 lastPosition;
    private Rigidbody rb;
    private ArticulationBody ab;

    void Awake()
    {
        lastPosition = transform.position;
        rb = GetComponent<Rigidbody>();
        ab = GetComponent<ArticulationBody>();

        var colliders = GetComponentsInChildren<Collider>(true);
        foreach (var c in colliders)
        {
            Debug.Log($"[DBG] collider {c.name}  enabled={c.enabled} isTrigger={c.isTrigger} material={(c.sharedMaterial != null ? c.sharedMaterial.name : "none")}");
        }

        if (rb != null)
            Debug.Log($"[DBG] Rigidbody encontrado en {name}: mass={rb.mass} drag={rb.linearDamping} angularDrag={rb.angularDamping} isKinematic={rb.isKinematic}");
        if (ab != null)
            Debug.Log($"[DBG] ArticulationBody encontrado en {name}: isRoot={ab.isRoot} jointFriction={ab.jointFriction}");
    }

    void Update()
    {
        Vector3 desired = Vector3.zero;
        bool inputDetected = false;

        if (Input.GetKey(forward)) { desired += transform.forward; inputDetected = true; }
        if (Input.GetKey(back))    { desired -= transform.forward; inputDetected = true; }
        if (Input.GetKey(left))    { desired -= transform.right;   inputDetected = true; }
        if (Input.GetKey(right))   { desired += transform.right;   inputDetected = true; }

        requestedMoveVector = desired.normalized;

        if (inputDetected)
        {
            Debug.Log($"[DBG] Input detectado: 8={Input.GetKey(forward)} 5={Input.GetKey(back)} 4={Input.GetKey(left)} 6={Input.GetKey(right)} (vector={requestedMoveVector})");
        }
    }

    void FixedUpdate()
    {
        Vector3 currentPosition = transform.position;
        lastDeltaPosition = currentPosition - lastPosition;
        lastVelocity = lastDeltaPosition / Time.fixedDeltaTime;
        lastPosition = currentPosition;

        if (rb != null)
        {
            Debug.Log($"[DBG] Rigidbody state: pos={currentPosition:F3} vel={rb.linearVelocity:F3} delta={lastDeltaPosition:F3} requested={requestedMoveVector:F3}");
        }
        else if (ab != null)
        {
            Debug.Log($"[DBG] ArticulationBody state: pos={currentPosition:F3} vel={ab.linearVelocity:F3} delta={lastDeltaPosition:F3} requested={requestedMoveVector:F3}");
        }
        else
        {
            Debug.Log($"[DBG] Transform state: pos={currentPosition:F3} delta={lastDeltaPosition:F3} requested={requestedMoveVector:F3}");
        }
    }

    void OnCollisionEnter(Collision c)
    {
        Debug.Log($"[DBG] OnCollisionEnter with {c.collider.name}, relativeVelocity={c.relativeVelocity:F3}." );
    }

    void OnCollisionStay(Collision c)
    {
        Debug.Log($"[DBG] OnCollisionStay with {c.collider.name}, relativeVelocity={c.relativeVelocity:F3}." );
    }

    void OnCollisionExit(Collision c)
    {
        Debug.Log($"[DBG] OnCollisionExit with {c.collider.name}." );
    }

    void OnDrawGizmos()
    {
        if (!drawMovementGizmos) return;
        if (!Application.isPlaying) return;
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(transform.position, transform.position + requestedMoveVector * 1.0f);
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position, transform.position + lastVelocity * 0.25f);

        // Dibujar velocidad de ruedas si hay ArticulationBody
        if (ab != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawLine(transform.position, transform.position + ab.linearVelocity * 0.1f);
        }
    }
}
