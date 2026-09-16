using UnityEngine;
#if UNITY_EDITOR
[ExecuteAlways]
#endif
public class MeasureWheel : MonoBehaviour
{
    public Transform leftWheel;   // centro de la rueda izquierda (el objeto que rota)
    public Transform rightWheel;  // centro de la rueda derecha
    public Renderer leftWheelRenderer; // Renderer (MeshRenderer) de la rueda izq.

    void Start() { Measure(); }
#if UNITY_EDITOR
    void OnValidate() { if (leftWheelRenderer && leftWheel && rightWheel) Measure(); }
#endif
    void Measure()
    {
        if (!leftWheelRenderer) return;
        var size = leftWheelRenderer.bounds.size; // en metros (1 unidad = 1 m)
        float diameter = Mathf.Max(size.x, size.y, size.z);
        float thickness = Mathf.Min(size.x, size.y, size.z);
        float radius = diameter * 0.5f;
        float separation = (leftWheel && rightWheel)
            ? Vector3.Distance(leftWheel.position, rightWheel.position) : -1f;

        Debug.Log($"WHEEL → diameter ≈ {diameter:F3} m, radius ≈ {radius:F3} m, " +
                  $"thickness ≈ {thickness:F3} m, separation ≈ {separation:F3} m");
    }
}
