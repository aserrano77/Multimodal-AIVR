using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using Autonomy.Domain;
using Autonomy.UnityIntegration;

public class RoundManager : MonoBehaviour, IExperimentalRoundLifecycle, IExperimentalRoundDepositRegistrar
{
    public SpawnManager spawnManager;
    public float delayBeforeNextRound = 2f;
    public bool autoStartNextRound = true;

    [Header("Debug")]
    [SerializeField] private bool debugDumpRoundStateNow;

    private int totalBoxesInRound = 0;
    private int depositedBoxes = 0;
    private bool roundFinished = false;
    private bool roundActive = false;
    private string lastRegisterRejectionReason = string.Empty;
    private string lastRegisteredBoxId = string.Empty;
    private Coroutine nextRoundCoroutine;

    private HashSet<BoxMetadata> countedBoxes = new HashSet<BoxMetadata>();

    public event System.Action<ExperimentalRoundSnapshot> RoundStarted;
    public event System.Action<Component, ExperimentalRoundSnapshot> CorrectDepositRegistered;
    public event System.Action<ExperimentalRoundSnapshot> RoundCompleted;
    public event System.Action<ExperimentalRoundSnapshot> RoundReset;

    public int TotalBoxesInRound => totalBoxesInRound;
    public int DepositedBoxes => depositedBoxes;
    public bool RoundActive => roundActive;
    public bool RoundFinished => roundFinished;
    public Transform ActiveRoundContainer => spawnManager != null ? spawnManager.activeRoundContainer : null;
    public ExperimentalRoundSnapshot CurrentRound => BuildSnapshot();
    public IReadOnlyCollection<BoxMetadata> CountedBoxes => countedBoxes;

    private void Update()
    {
        if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
        {
            return;
        }

        if (!debugDumpRoundStateNow)
        {
            return;
        }

        debugDumpRoundStateNow = false;
        DebugDumpRoundState("inspector_flag");
    }

    public void StartRound(int boxCount)
    {
        if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
        {
            TiagoExperimentTelemetry.LogEvent(
                "round_start_blocked_while_experiment_paused",
                new Dictionary<string, object>
                {
                    ["reason"] = ExperimentRuntimePauseCoordinator.AutonomyRequestRejectionReason,
                    ["requested_box_count"] = boxCount
                });
            return;
        }

        totalBoxesInRound = boxCount;
        depositedBoxes = 0;
        roundFinished = false;
        roundActive = true;
        countedBoxes.Clear();
        lastRegisterRejectionReason = string.Empty;
        lastRegisteredBoxId = string.Empty;

        Debug.Log("Nueva ronda iniciada con " + totalBoxesInRound + " cajas.");
        LogRoundEvent("round_started");
        RoundStarted?.Invoke(BuildSnapshot());
    }

    public void RegisterCorrectDeposit(BoxMetadata box)
    {
        TryRegisterCorrectDeposit(box, out _);
    }

    public bool TryRegisterCorrectDeposit(Component box, out string rejectionReason)
    {
        rejectionReason = string.Empty;
        BoxMetadata metadata = box as BoxMetadata;
        if (metadata == null && box != null)
        {
            metadata = box.GetComponent<BoxMetadata>();
        }

        return TryRegisterCorrectDeposit(metadata, lockBeforeEvents: false, out rejectionReason);
    }

    public bool TryRegisterReleasedCorrectDeposit(BoxMetadata box, out string rejectionReason)
    {
        return TryRegisterCorrectDeposit(box, lockBeforeEvents: true, out rejectionReason);
    }

    private bool TryRegisterCorrectDeposit(BoxMetadata box, bool lockBeforeEvents, out string rejectionReason)
    {
        LogRegisterCorrectDepositAttempt(box, "attempt");

        if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
        {
            rejectionReason = "experiment_paused_deposit_rejected";
            LogRegisterCorrectDepositRejected(box, rejectionReason);
            return false;
        }

        if (roundFinished)
        {
            rejectionReason = "round_finished";
            LogRegisterCorrectDepositRejected(box, "round_finished");
            return false;
        }

        if (box == null)
        {
            rejectionReason = "box_null";
            LogRegisterCorrectDepositRejected(box, "box_null");
            return false;
        }

        if (countedBoxes.Contains(box))
        {
            rejectionReason = "already_counted";
            LogRegisterCorrectDepositRejected(box, "already_counted");
            return false;
        }

        if (box.IsGrabbedOrSelected)
        {
            rejectionReason = "box_still_grabbed_or_selected";
            LogRegisterCorrectDepositRejected(box, rejectionReason);
            return false;
        }

        if (lockBeforeEvents && !box.TryLockAfterDepositIfReleased())
        {
            rejectionReason = "box_could_not_lock_after_release";
            LogRegisterCorrectDepositRejected(box, rejectionReason);
            return false;
        }

        countedBoxes.Add(box);
        depositedBoxes++;
        rejectionReason = string.Empty;
        lastRegisterRejectionReason = string.Empty;
        lastRegisteredBoxId = box.gameObject.name;

        Debug.Log("Caja depositada correctamente. Progreso: " + depositedBoxes + "/" + totalBoxesInRound);
        LogCorrectDeposit(box);
        CorrectDepositRegistered?.Invoke(box, BuildSnapshot());

        if (depositedBoxes >= totalBoxesInRound)
        {
            roundFinished = true;
            roundActive = false;
            Debug.Log("Ronda completada.");
            LogRoundEvent("round_completed");
            RoundCompleted?.Invoke(BuildSnapshot());
            if (autoStartNextRound)
            {
                nextRoundCoroutine = StartCoroutine(BeginNextRound());
            }
        }

        return true;
    }

    public void NotifyRoundReset()
    {
        CancelPendingNextRound("round_reset");
        roundActive = false;
        roundFinished = false;
        totalBoxesInRound = 0;
        depositedBoxes = 0;
        countedBoxes.Clear();
        lastRegisterRejectionReason = string.Empty;
        lastRegisteredBoxId = string.Empty;
        LogRoundEvent("round_reset");
        RoundReset?.Invoke(BuildSnapshot());
    }

    public void SetAutoStartNextRound(bool enabled, string reason = "")
    {
        autoStartNextRound = enabled;
        if (!enabled)
        {
            CancelPendingNextRound(string.IsNullOrWhiteSpace(reason) ? "auto_start_disabled" : reason);
        }

        TiagoExperimentTelemetry.LogEvent(
            "round_auto_start_next_round_configured",
            new Dictionary<string, object>
            {
                ["auto_start_next_round"] = autoStartNextRound,
                ["reason"] = reason ?? string.Empty,
                ["frame_count"] = Time.frameCount,
                ["time_since_start"] = Time.time
            });
    }

    public bool IsInActiveRound(Component component)
    {
        if (component == null)
        {
            return false;
        }

        Transform container = ActiveRoundContainer;
        if (container == null)
        {
            return true;
        }

        Transform current = component.transform;
        while (current != null)
        {
            if (current == container)
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private IEnumerator BeginNextRound()
    {
        yield return new WaitForSeconds(delayBeforeNextRound);

        nextRoundCoroutine = null;
        if (!autoStartNextRound)
        {
            yield break;
        }

        if (spawnManager == null)
        {
            Debug.LogError("RoundManager: spawnManager no está asignado.", this);
            yield break;
        }

        spawnManager.ClearCurrentRound();
        spawnManager.SpawnNewRound();
    }

    private void CancelPendingNextRound(string reason)
    {
        if (nextRoundCoroutine == null)
        {
            return;
        }

        StopCoroutine(nextRoundCoroutine);
        nextRoundCoroutine = null;
        TiagoExperimentTelemetry.LogEvent(
            "round_pending_next_round_cancelled",
            new Dictionary<string, object>
            {
                ["reason"] = reason ?? string.Empty,
                ["frame_count"] = Time.frameCount,
                ["time_since_start"] = Time.time
            });
    }

    private ExperimentalRoundSnapshot BuildSnapshot()
    {
        var boxes = new List<Component>();
        foreach (BoxMetadata box in countedBoxes)
        {
            if (box != null)
            {
                boxes.Add(box);
            }
        }

        return new ExperimentalRoundSnapshot(
            totalBoxesInRound,
            depositedBoxes,
            roundActive,
            roundFinished,
            ActiveRoundContainer,
            boxes);
    }

    private void LogRoundEvent(string eventType)
    {
        TiagoExperimentTelemetry.LogEvent(
            eventType,
            new Dictionary<string, object>
            {
                ["total_boxes"] = totalBoxesInRound,
                ["deposited_boxes"] = depositedBoxes,
                ["round_active"] = roundActive,
                ["round_finished"] = roundFinished,
                ["active_round_container"] = ActiveRoundContainer != null ? ActiveRoundContainer.name : string.Empty,
                ["auto_start_next_round"] = autoStartNextRound,
                ["frame_count"] = Time.frameCount,
                ["time_since_start"] = Time.time
            });
    }

    private void LogCorrectDeposit(BoxMetadata box)
    {
        TiagoExperimentTelemetry.LogEvent(
            "round_correct_deposit_registered",
            BuildRegisterPayload(box, "registered"));
    }

    private void LogRegisterCorrectDepositAttempt(BoxMetadata box, string reason)
    {
        TiagoExperimentTelemetry.LogEvent("round_register_correct_deposit_attempt", BuildRegisterPayload(box, reason));
    }

    private void LogRegisterCorrectDepositRejected(BoxMetadata box, string reason)
    {
        lastRegisterRejectionReason = reason ?? string.Empty;
        TiagoExperimentTelemetry.LogEvent("round_register_correct_deposit_rejected", BuildRegisterPayload(box, reason));
    }

    private Dictionary<string, object> BuildRegisterPayload(BoxMetadata box, string reason)
    {
        return new Dictionary<string, object>
        {
            ["box_id"] = box != null ? box.gameObject.name : string.Empty,
            ["box_name"] = box != null ? box.name : string.Empty,
            ["box_category"] = box != null ? box.boxType.ToString() : string.Empty,
            ["box_is_deposited"] = box != null && box.isDeposited,
            ["belongs_to_active_round"] = box != null && IsInActiveRound(box),
            ["already_counted"] = box != null && countedBoxes.Contains(box),
            ["round_active"] = roundActive,
            ["round_completed"] = roundFinished,
            ["total_boxes"] = totalBoxesInRound,
            ["deposited_count"] = depositedBoxes,
            ["reason"] = reason ?? string.Empty,
            ["frame_count"] = Time.frameCount,
            ["time_since_start"] = Time.time
        };
    }

    public void DebugDumpRoundState(string reason = "manual")
    {
        var counted = new List<Dictionary<string, object>>();
        foreach (BoxMetadata box in countedBoxes)
        {
            if (box == null)
            {
                continue;
            }

            counted.Add(new Dictionary<string, object>
            {
                ["box_id"] = box.gameObject.name,
                ["box_name"] = box.name,
                ["box_category"] = box.boxType.ToString(),
                ["is_deposited"] = box.isDeposited,
                ["transform_path"] = GetTransformPath(box.transform)
            });
        }

        var payload = new Dictionary<string, object>
        {
            ["reason"] = reason ?? string.Empty,
            ["round_active"] = roundActive,
            ["round_completed"] = roundFinished,
            ["total_boxes"] = totalBoxesInRound,
            ["deposited_count"] = depositedBoxes,
            ["counted_boxes_count"] = countedBoxes.Count,
            ["counted_boxes"] = counted,
            ["auto_start_next_round"] = autoStartNextRound,
            ["active_round_container_name"] = ActiveRoundContainer != null ? ActiveRoundContainer.name : string.Empty,
            ["active_round_container_path"] = ActiveRoundContainer != null ? GetTransformPath(ActiveRoundContainer) : string.Empty,
            ["last_registered_box_id"] = lastRegisteredBoxId,
            ["last_register_rejection_reason"] = lastRegisterRejectionReason,
            ["frame_count"] = Time.frameCount,
            ["time_since_start"] = Time.time
        };
        Debug.Log($"[RoundManager] round_debug_state_snapshot | active={roundActive} deposited={depositedBoxes}/{totalBoxesInRound} last_rejection='{lastRegisterRejectionReason}'", this);
        TiagoExperimentTelemetry.LogEvent("round_debug_state_snapshot", payload);
    }

    private static string GetTransformPath(Transform transform)
    {
        if (transform == null)
        {
            return string.Empty;
        }

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
