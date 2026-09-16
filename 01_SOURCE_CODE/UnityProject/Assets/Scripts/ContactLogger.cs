using UnityEngine;

public class ContactLogger : MonoBehaviour
{
    string PathOf(Transform t)
    {
        if (!t) return "(null)";
        string p = t.name;
        var u = t.parent;
        while (u != null) { p = u.name + "/" + p; u = u.parent; }
        return p;
    }

    float nextLog;                 // para no spamear
    void OnEnable() { nextLog = 0f; }

    void OnCollisionStay(Collision c)
    {
        if (Time.time < nextLog) return;
        nextLog = Time.time + 0.25f;

        var other = c.collider ? c.collider.transform : null;
        string a = PathOf(transform);
        string b = PathOf(other);
        string layer = c.collider ? LayerMask.LayerToName(c.collider.gameObject.layer) : "(none)";

        Debug.Log($"CONTACTO: {a} ↔ {b} [layer={layer}]");
    }
}
