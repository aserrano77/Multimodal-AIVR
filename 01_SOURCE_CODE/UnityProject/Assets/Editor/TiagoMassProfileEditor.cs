#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(TiagoMassProfile))]
public class TiagoMassProfileInspector : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var p = (TiagoMassProfile)target;
        GUILayout.Space(8);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Apply", GUILayout.Height(24)))
            {
                Undo.RecordObjects(p.GetComponentsInChildren<ArticulationBody>(true), "Apply Mass Profile");
                p.ApplyProfile();
                EditorUtility.SetDirty(p);
            }
            if (GUILayout.Button("Revert", GUILayout.Height(24)))
            {
                Undo.RecordObjects(p.GetComponentsInChildren<ArticulationBody>(true), "Revert Mass Profile");
                p.RevertProfile();
                EditorUtility.SetDirty(p);
            }
        }
        EditorGUILayout.HelpBox("Apply/Revert también disponibles en Tools ▸ Tiago Mass Profile.", MessageType.Info);
    }
}

public static class TiagoMassProfileMenu
{
    [MenuItem("Tools/Tiago Mass Profile/Apply (selected)")]
    public static void ApplyOnSelected()
    {
        var go = Selection.activeGameObject;
        var prof = go ? go.GetComponentInChildren<TiagoMassProfile>(true) : null;
        if (!prof) { Debug.LogWarning("Seleccione un objeto que tenga TiagoMassProfile."); return; }

        Undo.RecordObjects(prof.GetComponentsInChildren<ArticulationBody>(true), "Apply Mass Profile");
        prof.ApplyProfile();
        EditorUtility.SetDirty(prof);
    }

    [MenuItem("Tools/Tiago Mass Profile/Revert (selected)")]
    public static void RevertOnSelected()
    {
        var go = Selection.activeGameObject;
        var prof = go ? go.GetComponentInChildren<TiagoMassProfile>(true) : null;
        if (!prof) { Debug.LogWarning("Seleccione un objeto que tenga TiagoMassProfile."); return; }

        Undo.RecordObjects(prof.GetComponentsInChildren<ArticulationBody>(true), "Revert Mass Profile");
        prof.RevertProfile();
        EditorUtility.SetDirty(prof);
    }
}
#endif
