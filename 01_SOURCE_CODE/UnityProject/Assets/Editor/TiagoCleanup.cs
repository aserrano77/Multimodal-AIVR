// Tools/Clean Tiago URDF Components
using UnityEditor;
using UnityEngine;
using Unity.Robotics.UrdfImporter.Control;
using System.Linq;

public static class TiagoCleanup
{
    [MenuItem("Tools/Tiago/Clean URDF Demo Components")]
    public static void CleanSelected()
    {
        var root = Selection.activeGameObject;
        if (!root) { Debug.LogWarning("Selecciona el raíz del robot en Hierarchy."); return; }

        int removed = 0;

        // 1) Quitar Controller del raíz (si existe)
        var ctrl = root.GetComponent<Controller>();
        if (ctrl) { Object.DestroyImmediate(ctrl, true); removed++; }

        // 2) Quitar todos los JointControl heredados
        var joints = root.GetComponentsInChildren<MonoBehaviour>(true)
            .Where(c => c && c.GetType().Name == "JointControl").ToList();
        foreach (var jc in joints) { Object.DestroyImmediate(jc, true); removed++; }

        // 3) (Opcional) Quitar FKRobot si quedó
        var fk = root.GetComponent("FKRobot");
        if (fk) { Object.DestroyImmediate((Component)fk, true); removed++; }

        Debug.Log($"[TiagoCleanup] Eliminados {removed} componentes de demo (Controller/JointControl/FKRobot).");
    }
}