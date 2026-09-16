using UnityEngine;

/// <summary>
/// Cámara libre tipo Scene View para capturas en Play Mode.
/// WASD = mover en plano local
/// Q/E = bajar/subir
/// Ratón derecho = mirar
/// Shift = acelerar
/// </summary>
public class FlyCamera : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 5f;
    public float fastMoveMultiplier = 3f;
    public float verticalSpeed = 4f;

    [Header("Look")]
    public float lookSensitivity = 2f;
    public bool requireRightMouseButton = true;

    private float yaw;
    private float pitch;

    private void Start()
    {
        Vector3 euler = transform.rotation.eulerAngles;
        yaw = euler.y;
        pitch = euler.x;
    }

    private void Update()
    {
        HandleLook();
        HandleMovement();
    }

    private void HandleLook()
    {
        bool allowLook = !requireRightMouseButton || Input.GetMouseButton(1);
        if (!allowLook) return;

        float mouseX = Input.GetAxis("Mouse X") * lookSensitivity;
        float mouseY = Input.GetAxis("Mouse Y") * lookSensitivity;

        yaw += mouseX;
        pitch -= mouseY;
        pitch = Mathf.Clamp(pitch, -89f, 89f);

        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    private void HandleMovement()
    {
        float speed = moveSpeed;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            speed *= fastMoveMultiplier;

        Vector3 move = Vector3.zero;

        if (Input.GetKey(KeyCode.W)) move += transform.forward;
        if (Input.GetKey(KeyCode.S)) move -= transform.forward;
        if (Input.GetKey(KeyCode.A)) move -= transform.right;
        if (Input.GetKey(KeyCode.D)) move += transform.right;
        if (Input.GetKey(KeyCode.E)) move += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) move += Vector3.down;

        if (move.sqrMagnitude > 0f)
        {
            transform.position += move.normalized * speed * Time.deltaTime;
        }
    }
}