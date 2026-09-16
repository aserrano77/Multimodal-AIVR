using UnityEngine;

public class PrintSize : MonoBehaviour
{
    void Start()
    {
        Renderer r = GetComponentInChildren<Renderer>();

        if (r != null)
        {
            Debug.Log("Size (meters): " + r.bounds.size);
        }
        else
        {
            Debug.Log("No renderer found in this object or its children.");
        }
    }
}