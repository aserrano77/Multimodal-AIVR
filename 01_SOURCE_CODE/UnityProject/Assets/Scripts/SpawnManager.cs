using UnityEngine;
using System.Collections.Generic;
using Autonomy.Domain;
using Autonomy.UnityIntegration;

/// <summary>
/// Genera pallets y cajas en los SpawnPoints.
/// Cada SpawnPoint representa la posición final del centro de la caja.
/// </summary>
public class SpawnManager : MonoBehaviour
{
    public GameObject[] boxPrefabs;
    public GameObject palletPrefab;
    public Transform[] spawnPoints;
    public Transform activeRoundContainer;
    public bool spawnOnStart = true;
    [Tooltip("Legacy/debug fallback. In Orchestrated2x2, ExperimentSessionOrchestrator overwrites this with the trial context value before spawning.")]
    public SpawnGenerationMode spawnGenerationMode = SpawnGenerationMode.RandomBalanced;
    public List<int> deterministicPrefabIndices = new List<int>();

    public float boxHeight = 0.5f;
    public float palletHeight = 0.15f;

    public RoundManager roundManager;
    [Header("Debug")]
    [SerializeField] private bool debugDumpSpawnStateNow;
    private int roundSequence = 0;
    private int registryOwnerInstanceId;

    private void Awake()
    {
        registryOwnerInstanceId = GetInstanceID();
        ActiveRoundBoxRegistry.BeginOwner(registryOwnerInstanceId);
    }

    private void OnDestroy()
    {
        ActiveRoundBoxRegistry.ReleaseOwner(registryOwnerInstanceId);
    }

    private void Start()
    {
        if (spawnOnStart)
            SpawnNewRound();
    }

    private void Update()
    {
        if (!debugDumpSpawnStateNow)
            return;

        debugDumpSpawnStateNow = false;
        DebugDumpSpawnState("inspector_flag");
    }

    /// <summary>
    /// Genera una nueva ronda completa.
    /// </summary>
    public void SpawnNewRound()
    {
        if (activeRoundContainer == null)
        {
            Debug.LogWarning("SpawnManager: activeRoundContainer no está asignado.");
            return;
        }

        if (spawnPoints == null || spawnPoints.Length == 0)
        {
            Debug.LogError("SpawnManager: spawnPoints no está asignado o está vacío.", this);
            return;
        }

        if (boxPrefabs == null || boxPrefabs.Length == 0)
        {
            Debug.LogError("SpawnManager: boxPrefabs no está asignado o está vacío.", this);
            return;
        }

        if (palletPrefab == null)
        {
            Debug.LogError("SpawnManager: palletPrefab no está asignado.", this);
            return;
        }

        for (int i = 0; i < spawnPoints.Length; i++)
        {
            if (spawnPoints[i] == null)
            {
                Debug.LogError($"SpawnManager: spawnPoints[{i}] no está asignado.", this);
                return;
            }
        }

        List<int> boxIndices = BuildBoxSequence(spawnPoints.Length, boxPrefabs.Length);
        for (int i = 0; i < spawnPoints.Length; i++)
        {
            if (i >= boxIndices.Count || boxIndices[i] < 0 || boxIndices[i] >= boxPrefabs.Length || boxPrefabs[boxIndices[i]] == null)
            {
                string prefabIndex = i < boxIndices.Count ? boxIndices[i].ToString() : "missing";
                Debug.LogError($"SpawnManager: prefab inválido para spawnPoints[{i}] (prefabIndex={prefabIndex}).", this);
                return;
            }
        }

        int roundIndex = ++roundSequence;
        LogSpawnRoundStarted(boxIndices, roundIndex);
        Dictionary<string, int> categoryCounts = new Dictionary<string, int>();

        for (int i = 0; i < spawnPoints.Length; i++)
        {
            Transform point = spawnPoints[i];

            // 1. Instanciar pallet
            Vector3 palletPosition = point.position;
            palletPosition.y -= (boxHeight / 2f + palletHeight / 2f);

            Instantiate(
                palletPrefab,
                palletPosition,
                Quaternion.identity,
                activeRoundContainer
            );

            // 2. Instanciar caja
            int prefabIndex = boxIndices[i];

            GameObject box = Instantiate(
                boxPrefabs[prefabIndex],
                point.position,
                Quaternion.identity,
                activeRoundContainer
            );
            AssignUniqueRoundBoxId(box, roundIndex, i, categoryCounts);
            RegisterActiveRoundBox(box);
            ManualBoxGrabExclusivityGuard.EnsureInstalled(box);
            LogSpawnedBox(box, prefabIndex, point, i, roundIndex);
        }

        if (roundManager != null)
            roundManager.StartRound(spawnPoints.Length);
    }

    /// <summary>
    /// Elimina todos los objetos de la ronda actual.
    /// </summary>
    public void ClearCurrentRound()
    {
        ActiveRoundBoxRegistry.ClearRound(registryOwnerInstanceId);

        if (activeRoundContainer == null)
            return;

        if (roundManager != null)
            roundManager.NotifyRoundReset();

        int beforeCount = activeRoundContainer.childCount;
        List<GameObject> childrenToDestroy = new List<GameObject>();

        foreach (Transform child in activeRoundContainer)
            childrenToDestroy.Add(child.gameObject);

        foreach (GameObject obj in childrenToDestroy)
        {
            if (obj == null)
                continue;

            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }

        TiagoExperimentTelemetry.LogEvent(
            "active_round_cleanup_requested",
            new Dictionary<string, object>
            {
                ["active_round_container"] = activeRoundContainer != null ? activeRoundContainer.name : string.Empty,
                ["active_round_children_before_reset"] = beforeCount,
                ["active_round_children_destroy_requested"] = childrenToDestroy.Count,
                ["active_round_children_after_reset"] = activeRoundContainer != null ? activeRoundContainer.childCount : 0,
                ["frame_count"] = Time.frameCount,
                ["time_since_start"] = Time.time
            });
    }

    public int ActiveRoundChildCount => activeRoundContainer != null ? activeRoundContainer.childCount : 0;

    public string DescribeActiveRoundChildren(int maxChildren = 12)
    {
        if (activeRoundContainer == null)
            return string.Empty;

        List<string> names = new List<string>();
        foreach (Transform child in activeRoundContainer)
        {
            if (child == null)
                continue;

            names.Add($"{child.name}:{child.gameObject.GetType().Name}");
            if (names.Count >= maxChildren)
                break;
        }

        return string.Join("|", names);
    }

    [ContextMenu("Debug/Spawn New Round")]
    private void DebugSpawnNewRound()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("SpawnManager: Debug/Spawn New Round solo ejecuta en Play Mode para evitar cambios accidentales en la escena.", this);
            return;
        }

        SpawnNewRound();
    }

    [ContextMenu("Debug/Clear Current Round")]
    private void DebugClearCurrentRound()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("SpawnManager: Debug/Clear Current Round solo ejecuta en Play Mode para evitar cambios accidentales en la escena.", this);
            return;
        }

        ClearCurrentRound();
    }

    public List<int> BuildBoxSequence(int spawnCount, int boxTypeCount)
    {
        return SpawnSequenceBuilder.Build(
            spawnCount,
            boxTypeCount,
            spawnGenerationMode,
            deterministicPrefabIndices,
            ShuffleList);
    }

    private void ShuffleList(IList<int> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int randomIndex = Random.Range(0, i + 1);

            int temp = list[i];
            list[i] = list[randomIndex];
            list[randomIndex] = temp;
        }
    }

    private void AssignUniqueRoundBoxId(GameObject box, int roundIndex, int spawnIndex, Dictionary<string, int> categoryCounts)
    {
        if (box == null)
            return;

        BoxMetadata metadata = box.GetComponent<BoxMetadata>();
        string category = metadata != null ? metadata.boxType.ToString() : "X";
        box.name = $"round{roundIndex:000}_box{spawnIndex:00}_{category}";
        if (metadata == null)
            return;

        int nextCategoryIndex = 1;
        if (categoryCounts != null && categoryCounts.TryGetValue(category, out int count))
        {
            nextCategoryIndex = count + 1;
        }

        if (categoryCounts != null)
        {
            categoryCounts[category] = nextCategoryIndex;
        }

        metadata.AssignVoiceLabels($"{category}{nextCategoryIndex}");
        BoxVisualSync visualSync = box.GetComponent<BoxVisualSync>();
        if (visualSync != null)
        {
            visualSync.ApplyVisuals();
        }
    }

    private void RegisterActiveRoundBox(GameObject box)
    {
        if (box == null || string.IsNullOrWhiteSpace(box.name))
        {
            return;
        }

        ActiveRoundBoxRegistry.Register(registryOwnerInstanceId, box.name, box);
    }

    private void LogSpawnRoundStarted(IReadOnlyList<int> boxIndices, int roundIndex)
    {
        TiagoExperimentTelemetry.LogEvent(
            "spawn_round_started",
            new Dictionary<string, object>
            {
                ["round_index"] = roundIndex,
                ["spawn_generation_mode"] = spawnGenerationMode.ToString(),
                ["spawn_count"] = spawnPoints != null ? spawnPoints.Length : 0,
                ["prefab_count"] = boxPrefabs != null ? boxPrefabs.Length : 0,
                ["active_round_container"] = activeRoundContainer != null ? activeRoundContainer.name : string.Empty,
                ["prefab_sequence"] = boxIndices
            });
    }

    private void LogSpawnedBox(GameObject box, int prefabIndex, Transform spawnPoint, int spawnIndex, int roundIndex)
    {
        BoxMetadata metadata = box != null ? box.GetComponent<BoxMetadata>() : null;
        TiagoExperimentTelemetry.LogEvent(
            "spawn_box_created",
            new Dictionary<string, object>
            {
                ["round_index"] = roundIndex,
                ["box_id"] = box != null ? box.name : string.Empty,
                ["box_type"] = metadata != null ? metadata.boxType.ToString() : string.Empty,
                ["display_label"] = metadata != null ? metadata.displayLabel : string.Empty,
                ["spoken_label"] = metadata != null ? metadata.spokenLabel : string.Empty,
                ["voice_alias"] = metadata != null ? metadata.voiceAlias : string.Empty,
                ["prefab_index"] = prefabIndex,
                ["prefab_name"] = boxPrefabs != null && prefabIndex >= 0 && prefabIndex < boxPrefabs.Length && boxPrefabs[prefabIndex] != null ? boxPrefabs[prefabIndex].name : string.Empty,
                ["spawn_index"] = spawnIndex,
                ["spawn_point"] = spawnPoint != null ? spawnPoint.name : string.Empty,
                ["spawn_position"] = spawnPoint != null ? spawnPoint.position : Vector3.zero,
                ["spawn_generation_mode"] = spawnGenerationMode.ToString()
            });
    }

    public void DebugDumpSpawnState(string reason = "manual")
    {
        var boxes = new List<Dictionary<string, object>>();
        if (activeRoundContainer != null)
        {
            BoxMetadata[] metadata = activeRoundContainer.GetComponentsInChildren<BoxMetadata>(true);
            foreach (BoxMetadata box in metadata)
            {
                if (box == null)
                    continue;

                boxes.Add(new Dictionary<string, object>
                {
                    ["box_id"] = box.gameObject.name,
                    ["box_name"] = box.name,
                    ["box_category"] = box.boxType.ToString(),
                    ["display_label"] = box.displayLabel,
                    ["spoken_label"] = box.spokenLabel,
                    ["voice_alias"] = box.voiceAlias,
                    ["is_deposited"] = box.isDeposited,
                    ["is_grabbed"] = box.isGrabbed,
                    ["transform_path"] = GetTransformPath(box.transform)
                });
            }
        }

        TiagoExperimentTelemetry.LogEvent(
            "spawn_debug_state_snapshot",
            new Dictionary<string, object>
            {
                ["reason"] = reason ?? string.Empty,
                ["spawn_on_start"] = spawnOnStart,
                ["spawn_generation_mode"] = spawnGenerationMode.ToString(),
                ["round_sequence"] = roundSequence,
                ["spawn_point_count"] = spawnPoints != null ? spawnPoints.Length : 0,
                ["prefab_count"] = boxPrefabs != null ? boxPrefabs.Length : 0,
                ["active_round_container_name"] = activeRoundContainer != null ? activeRoundContainer.name : string.Empty,
                ["active_round_container_instance_id"] = activeRoundContainer != null ? activeRoundContainer.GetInstanceID() : 0,
                ["boxes"] = boxes,
                ["frame_count"] = Time.frameCount,
                ["time_since_start"] = Time.time
            });
        Debug.Log($"[SpawnManager] spawn_debug_state_snapshot | boxes={boxes.Count} container='{(activeRoundContainer != null ? activeRoundContainer.name : string.Empty)}'", this);
    }

    private static string GetTransformPath(Transform transform)
    {
        if (transform == null)
            return string.Empty;

        string path = transform.name;
        Transform current = transform.parent;
        while (current != null)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }

        return path;
    }
}
