using UnityEngine;

public class ABPrismaticSetpointTest : MonoBehaviour
{
    public ArticulationBody joint; // BottomShoulder (Prismatic Y)

    void Update()
    {
        if (!joint) return;

        var d = joint.yDrive;            // EJE Y (porque Axis = Y)
        d.stiffness = 8000f;             // set-point “duro”
        d.damping   = 400f;

        if (Input.GetKeyDown(KeyCode.I)) // SUBIR a 0.1 m
        {
            d.target = 0.2f;
            joint.yDrive = d;
        }
        if (Input.GetKeyDown(KeyCode.K)) // BAJAR a 0.0 m
        {
            d.target = 0.0f;
            joint.yDrive = d;
        }

        // Debug en consola: posición actual del DOF (m)
        float q = (float)joint.jointPosition[0];
        Debug.Log($"Prismatic q = {q:F4} m");
    }
}