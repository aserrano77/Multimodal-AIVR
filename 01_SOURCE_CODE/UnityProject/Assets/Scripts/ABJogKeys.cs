using UnityEngine;

public class ABJogKeys : MonoBehaviour
{
    public ArticulationBody joint;     // Asigna el AB del Shoulder
    public enum Axis { X, Y, Z }
    public Axis driveAxis = Axis.X;    // El eje del drive que uses
    public float speed = 0.5f;         // rad/s para revolute
    public float limitMargin = 0.05f;  // rad de margen a los límites

    void Reset()
    {
        joint = GetComponent<ArticulationBody>();
    }

    void Update()
    {
        if (!joint) return;

        // Lee el drive correcto según el eje
        ArticulationDrive d = GetDrive();
        float q = (float)joint.jointPosition[0];
        float lo = d.lowerLimit;
        float hi = d.upperLimit;

        float v = 0f;
        // Mantén pulsado J (−) o L (+)
        if (Input.GetKey(KeyCode.J)) v = -speed;
        if (Input.GetKey(KeyCode.L)) v = +speed;

        // Frenar cerca del límite
        if (v > 0 && q >= hi - limitMargin) v = 0;
        if (v < 0 && q <= lo + limitMargin) v = 0;

        // Modo velocidad: stiffness=0, damping>0 en el drive
        d.targetVelocity = v;
        SetDrive(d);
    }

    ArticulationDrive GetDrive()
    {
        return driveAxis == Axis.X ? joint.xDrive :
            driveAxis == Axis.Y ? joint.yDrive : joint.zDrive;
    }
    void SetDrive(ArticulationDrive d)
    {
        if (driveAxis == Axis.X) joint.xDrive = d;
        else if (driveAxis == Axis.Y) joint.yDrive = d;
        else joint.zDrive = d;
    }
}