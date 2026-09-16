using System.Collections;
using UnityEngine;

public sealed class ManualBoxGrabExclusivityBootstrap : MonoBehaviour
{
    [SerializeField, Min(0.1f)] private float installIntervalSeconds = 0.5f;
    [SerializeField, Min(1f)] private float installWindowSeconds = 10f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateAfterSceneLoad()
    {
        if (FindAnyObjectByType<ManualBoxGrabExclusivityBootstrap>(FindObjectsInactive.Include) != null)
            return;

        GameObject bootstrapObject = new(nameof(ManualBoxGrabExclusivityBootstrap));
        DontDestroyOnLoad(bootstrapObject);
        bootstrapObject.AddComponent<ManualBoxGrabExclusivityBootstrap>();
    }

    private void OnEnable()
    {
        StartCoroutine(InstallDuringInitialSpawnWindow());
    }

    private IEnumerator InstallDuringInitialSpawnWindow()
    {
        float startTime = Time.unscaledTime;
        do
        {
            ManualBoxGrabExclusivityGuard.InstallOnSceneBoxes();
            yield return new WaitForSecondsRealtime(installIntervalSeconds);
        }
        while (Time.unscaledTime - startTime < installWindowSeconds);
    }
}
