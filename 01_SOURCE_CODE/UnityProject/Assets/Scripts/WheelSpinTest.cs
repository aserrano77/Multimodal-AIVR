using UnityEngine;

public class WheelSpinTest : MonoBehaviour
{
    public ArticulationBody wheelLeft;
    public ArticulationBody wheelRight;
    public float testDegPerSec = 360f;   // velocidad de prueba

    void FixedUpdate()
    {
        Spin(wheelLeft,  testDegPerSec);
        Spin(wheelRight, testDegPerSec);
    }

    void Spin(ArticulationBody ab, float degPerSec)
    {
        if (!ab) return;
        var d = ab.xDrive;
        d.driveType  = ArticulationDriveType.Velocity; // modo velocidad
        d.stiffness  = 0f;
        d.damping    = 0f;                             // <- clave: anula el 1e+25
        d.forceLimit = Mathf.Max(d.forceLimit, 1000000f);
        d.targetVelocity = degPerSec;
        ab.xDrive = d;
        ab.jointFriction = 0f;
    }
}
