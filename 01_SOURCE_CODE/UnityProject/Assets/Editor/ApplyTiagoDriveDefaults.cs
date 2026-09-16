// Assets/Editor/ApplyTiagoDriveDefaults.cs
using UnityEditor;
using UnityEngine;

public static class ApplyTiagoDriveDefaults
{
    [MenuItem("Tools/Tiago/Aplicar defaults de drives al robot (seleccionado)")]
    public static void ApplyToSelection()
    {
        var root = Selection.activeTransform;
        if (!root) { Debug.LogWarning("Selecciona el root del robot en la Hierarchy."); return; }

        int count = 0;
        Undo.RecordObject(root, "Apply Tiago Drive Defaults");

        foreach (var ab in root.GetComponentsInChildren<ArticulationBody>(true))
        {
            var d = ab.xDrive;
            bool isPrismatic = ab.jointType == ArticulationJointType.PrismaticJoint;

            d.stiffness  = isPrismatic ? 25000f : 15000f;
            d.damping    = isPrismatic ?   600f :   300f;
            d.forceLimit = isPrismatic ?  8000f :  5000f;

            d.target = Mathf.Clamp(d.target, d.lowerLimit, d.upperLimit);
            ab.xDrive = d;
            ab.immovable = false;

            EditorUtility.SetDirty(ab);
            count++;
        }

        Debug.Log($"[ApplyTiagoDriveDefaults] Aplicados defaults a {count} ArticulationBody.");
    }
}