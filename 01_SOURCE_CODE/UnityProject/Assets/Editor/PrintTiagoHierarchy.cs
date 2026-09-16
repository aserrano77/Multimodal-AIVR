using UnityEngine;
using UnityEditor;
using System.Text;
using System.IO;

public class PrintTiagoHierarchy : EditorWindow
{
    [MenuItem("Tools/Tiago/Imprimir jerarquía con materiales")]
    public static void PrintHierarchy()
    {
        GameObject root = GameObject.Find("tiago_dual");
        if (root == null)
        {
            Debug.LogError("No se encontró un GameObject llamado 'tiago_dual'.");
            return;
        }

        StringBuilder sb = new StringBuilder();
        PrintChildrenRecursive(root.transform, 0, sb);

        string output = sb.ToString();

        // Mostrar en consola
        Debug.Log(output);

        // Guardar en archivo
        string path = "Assets/tiago_hierarchy.txt";
        File.WriteAllText(path, output);
        Debug.Log("Jerarquía con materiales guardada en: " + path);
        AssetDatabase.Refresh();
    }

    private static void PrintChildrenRecursive(Transform current, int depth, StringBuilder sb)
    {
        string indent = new string(' ', depth * 2);
        string line = indent + current.name;

        Renderer renderer = current.GetComponent<Renderer>();
        if (renderer != null && renderer.sharedMaterial != null)
        {
            line += $"  [Material: {renderer.sharedMaterial.name}]";
        }
        else if (renderer != null)
        {
            line += "  [Material: none]";
        }

        sb.AppendLine(line);

        foreach (Transform child in current)
        {
            PrintChildrenRecursive(child, depth + 1, sb);
        }
    }
}