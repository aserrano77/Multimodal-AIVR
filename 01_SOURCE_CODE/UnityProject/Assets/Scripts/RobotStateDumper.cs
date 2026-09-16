using UnityEngine;
using System.Text;
using System.Reflection;
using System.IO;
using System;

public class RobotStateDumper : MonoBehaviour
{
    [Tooltip("El objeto raíz de tu robot. Si se deja en blanco, volcará el objeto al que le añadas este script.")]
    public GameObject targetRoot;

    // Puedes ejecutar esta función haciendo click derecho sobre el nombre del script en el Inspector de Unity
    [ContextMenu("Ejecutar Volcado de Jerarquía (DUMP TO FILE)")]
    public void Dump()
    {
        GameObject root = targetRoot != null ? targetRoot : gameObject;
        
        // El archivo se guardará en la carpeta principal del proyecto de Unity
        string path = Path.Combine(Application.dataPath, "../Robot_Hierarchy_Dump.txt");
        
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"=== DUMP OF ROBOT HIERARCHY: {root.name} ===");
        sb.AppendLine($"Time: {System.DateTime.Now}");
        sb.AppendLine("==========================================\n");

        DumpRecursive(root.transform, sb, 0);

        File.WriteAllText(path, sb.ToString());
        Debug.Log($"<color=green>[Dumper]</color> Información exportada con éxito. Revisa el archivo (fuera de Assets): {path}");
    }

    private void DumpRecursive(Transform t, StringBuilder sb, int depth)
    {
        string indent = new string(' ', depth * 4);
        sb.AppendLine($"\n{indent}■ GameObject: {t.name} (Active: {t.gameObject.activeSelf}, Layer: {LayerMask.LayerToName(t.gameObject.layer)})");

        Component[] components = t.GetComponents<Component>();
        foreach (var comp in components)
        {
            if (comp == null) continue;
            Type compType = comp.GetType();
            sb.AppendLine($"{indent}  ▶ Component: {compType.Name}");

            // ==== EXTRAER CAMPOS PÚBLICOS ====
            FieldInfo[] fields = compType.GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (IsObsolete(field)) continue;

                try
                {
                    object value = field.GetValue(comp);
                    sb.AppendLine($"{indent}      [Field] {field.Name}: {FormatValue(value)}");
                }
                catch (Exception e)
                {
                    sb.AppendLine($"{indent}      [Field] {field.Name}: [Error de lectura: {e.Message}]");
                }
            }

            // ==== EXTRAER PROPIEDADES (Getters) ====
            PropertyInfo[] props = compType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in props)
            {
                if (!prop.CanRead) continue;
                if (prop.GetIndexParameters().Length > 0) continue; // Skip indexers como vertices[0]

                if (IsObsolete(prop)) continue;
                
                // Evitar instanciar materiales automáticamente en Unity o hacer llamadas obsoletas conflictivas
                if (prop.Name == "material" || prop.Name == "materials" || prop.Name == "mesh") continue;
                // Unity a veces tira errores deprecados en Rigidbody y ArticulationBody
                if (prop.DeclaringType == typeof(Rigidbody) && prop.Name == "sleepVelocity") continue;
                if (prop.DeclaringType == typeof(Rigidbody) && prop.Name == "sleepAngularVelocity") continue;
                if (prop.DeclaringType == typeof(ArticulationBody) && prop.Name == "sleepVelocity") continue;
                if (prop.DeclaringType == typeof(ArticulationBody) && prop.Name == "sleepAngularVelocity") continue;

                try
                {
                    object value = prop.GetValue(comp, null);
                    sb.AppendLine($"{indent}      [Prop]  {prop.Name}: {FormatValue(value)}");
                }
                catch (Exception)
                {
                    // Ignoramos silenciosamente propiedades que Unity tiene bloqueadas por contexto (ej: no isPlaying)
                    sb.AppendLine($"{indent}      [Prop]  {prop.Name}: [Dato inaccesible o bloqueado]");
                }
            }
        }

        foreach (Transform child in t)
        {
            DumpRecursive(child, sb, depth + 1);
        }
    }

    private string FormatValue(object value)
    {
        if (value == null) return "null";
        
        // Si es un objeto de Unity (Transform, Mesh, Material...), imprimir solo su nombre y evitar recursion
        if (value is UnityEngine.Object uObj) return uObj ? $"({uObj.GetType().Name}) {uObj.name}" : "null";
        
        // Tipos especiales
        if (value is ArticulationDrive drive)
        {
            return $"DriveType: {drive.driveType}, Stiffness: {drive.stiffness}, Damping: {drive.damping}, ForceLimit: {drive.forceLimit}, Target: {drive.target}, TargetVel: {drive.targetVelocity}";
        }

        return value.ToString();
    }

    private bool IsObsolete(MemberInfo member)
    {
        return Attribute.IsDefined(member, typeof(ObsoleteAttribute));
    }
}
