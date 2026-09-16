using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class BoxEdgeFrameSceneCleanup
{
    private const string ScenePath = "Assets/Scenes/autonomous_demo_step22_multimodal_bridge.unity";
    private const string EdgeFrameName = "BoxEdgeFrame";

    [MenuItem("Tools/Multimodal AI-VR/Scene Cleanup/Delete Root BoxEdgeFrame Objects")]
    public static void DeleteRootBoxEdgeFramesInOpenScene()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid())
        {
            Debug.LogError("Root BoxEdgeFrame cleanup aborted: no valid active scene.");
            return;
        }

        int deleted = DeleteRootBoxEdgeFrames(scene, useUndo: true);
        if (deleted > 0)
        {
            EditorSceneManager.MarkSceneDirty(scene);
        }

        Debug.Log($"Root BoxEdgeFrame cleanup completed. Deleted root objects: {deleted}.");
    }

    public static void DeleteTargetSceneRootBoxEdgeFramesForBatch()
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        int deleted = DeleteRootBoxEdgeFrames(scene, useUndo: false);
        if (deleted > 0)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        Debug.Log($"Root BoxEdgeFrame batch cleanup completed. Deleted root objects: {deleted}.");
    }

    private static int DeleteRootBoxEdgeFrames(Scene scene, bool useUndo)
    {
        var targets = new List<GameObject>();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root != null && string.Equals(root.name, EdgeFrameName, StringComparison.Ordinal))
            {
                targets.Add(root);
            }
        }

        foreach (GameObject target in targets)
        {
            if (useUndo)
            {
                Undo.DestroyObjectImmediate(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        return targets.Count;
    }
}
