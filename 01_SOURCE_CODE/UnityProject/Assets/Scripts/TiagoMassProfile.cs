using System.Collections.Generic;
using UnityEngine;

/// Aplica un perfil de masas al robot (URDF/ArticulationBody).
/// Perfiles: Physical (revert) y Heavywheels (ruedas pesadas, base ligera).
[DefaultExecutionOrder(-1000)]
public class TiagoMassProfile : MonoBehaviour
{
    public enum MassProfile { Physical, Heavywheels }

    [Header("Perfil activo")]
    public MassProfile profile = MassProfile.Heavywheels;

    [Header("Nombres de links (exactos)")]
    public string baseLinkName   = "base_link";
    public string leftWheelName  = "wheel_left_link";
    public string rightWheelName = "wheel_right_link";

    [Header("Heavywheels (ajustable)")]
    [Tooltip("Masa ABSOLUTA para cada rueda (kg).")]
    public float heavywheelsWheelMass = 110f;
    [Tooltip("Masa ABSOLUTA para la base_link (kg).")]
    public float heavywheelsBaseMass  = 10f;
    [Tooltip("Escala multiplicativa para todos los DEMÁS eslabones (1 = sin cambio).")]
    public float heavywheelsOtherScale = 1f;

    [Header("Inercia")]
    [Tooltip("Si está activo, deja que PhysX recalcule el tensor de inercia al cambiar la masa.")]
    public bool autoInertia = true;

    [Header("Ejecución")]
    [Tooltip("Aplicar automáticamente al arrancar la escena (Awake).")]
    public bool applyOnAwake = true;

    struct Orig
    {
        public float mass;
        public Vector3 inertia;
        public bool auto;
    }

    readonly Dictionary<ArticulationBody, Orig> _orig = new();
    bool _haveSnapshot;

    void Awake()
    {
        if (applyOnAwake) ApplyProfile();
    }

    // Guarda las masas/inercia originales (una sola vez)
    void SnapshotIfNeeded()
    {
        if (_haveSnapshot) return;
        _orig.Clear();
        foreach (var ab in GetComponentsInChildren<ArticulationBody>(true))
        {
            if (!_orig.ContainsKey(ab))
                _orig.Add(ab, new Orig {
                    mass = ab.mass,
                    inertia = ab.inertiaTensor,
                    auto = ab.automaticInertiaTensor
                });
        }
        _haveSnapshot = true;
    }

    public void ApplyProfile()
    {
        SnapshotIfNeeded();

        // ---- Heavywheels ----
        var abs = GetComponentsInChildren<ArticulationBody>(true);
        foreach (var ab in abs)
        {
            if (!_orig.TryGetValue(ab, out var o)) continue;

            float newMass = o.mass; // por defecto, original
            if (ab.name == baseLinkName)
                newMass = Mathf.Max(0.01f, heavywheelsBaseMass);
            else if (ab.name == leftWheelName || ab.name == rightWheelName)
                newMass = Mathf.Max(0.01f, heavywheelsWheelMass);
            else
                newMass = Mathf.Max(0.01f, o.mass * Mathf.Max(0.0001f, heavywheelsOtherScale));

            ab.mass = newMass;

            if (autoInertia)
            {
                ab.automaticInertiaTensor = true;
            }
            else
            {
                ab.automaticInertiaTensor = false;
                // escala simple del tensor original proporcional a la masa
                float k = newMass / Mathf.Max(0.0001f, o.mass);
                ab.inertiaTensor = o.inertia * k;
            }
        }

        //Debug.Log($"[TiagoMassProfile] Applied: Heavywheels (wheel={heavywheelsWheelMass} kg, base={heavywheelsBaseMass} kg, other×={heavywheelsOtherScale}).");
    }

    public void RevertProfile()
    {
        if (!_haveSnapshot) return;

        foreach (var kv in _orig)
        {
            var ab = kv.Key; if (!ab) continue;
            var o = kv.Value;

            ab.mass = o.mass;
            ab.automaticInertiaTensor = o.auto;
            if (!o.auto) ab.inertiaTensor = o.inertia;
        }
        Debug.Log("[TiagoMassProfile] Reverted to URDF masses/inertia.");
    }
}
