using System.Collections;
using System.Collections.Generic;
using Autonomy.UnityIntegration;
using UnityEngine;

public class DepositZone : MonoBehaviour
{
    public BoxMetadata.BoxType acceptedType;
    public float requiredStayTime = 0.5f;
    [SerializeField] private bool enableP44DepositDiagnostics = true;

    private readonly Dictionary<BoxMetadata, Coroutine> pendingValidations = new();
    private readonly HashSet<Collider> stayDiagnosticsLogged = new();

    private void OnTriggerEnter(Collider other)
    {
        LogTriggerEvent("deposit_zone_trigger_entered", other, ResolveBox(other), string.Empty);
        EvaluateCandidate(other, "trigger_enter", logRejected: true);
    }

    private void OnTriggerStay(Collider other)
    {
        BoxMetadata box = ResolveBox(other);
        if (box != null && box.isDeposited)
        {
            return;
        }

        bool logStay = ShouldLogTriggerStay(other);
        if (logStay)
        {
            LogTriggerEvent("deposit_zone_trigger_stayed", other, box, string.Empty);
        }

        EvaluateCandidate(other, "trigger_stay", logRejected: logStay);
    }

    private void OnTriggerExit(Collider other)
    {
        stayDiagnosticsLogged.Remove(other);
        BoxMetadata box = ResolveBox(other);
        LogTriggerEvent("deposit_zone_trigger_exited", other, box, string.Empty);
        if (box == null) return;

        if (pendingValidations.TryGetValue(box, out Coroutine routine))
        {
            StopCoroutine(routine);
            pendingValidations.Remove(box);
            LogCandidateRejected(other, box, "trigger_exit_before_validation_completed");
        }
    }

    private void EvaluateCandidate(Collider other, string source, bool logRejected)
    {
        BoxMetadata box = ResolveBox(other);
        if (source != "trigger_stay")
        {
            LogCandidateEvaluated(other, box, source);
        }

        if (box == null)
        {
            if (logRejected)
            {
                LogCandidateRejected(other, null, "box_metadata_missing");
            }
            return;
        }

        if (box.isDeposited)
        {
            if (logRejected)
            {
                LogCandidateRejected(other, box, "box_already_deposited");
            }
            return;
        }

        if (box.boxType != acceptedType)
        {
            if (logRejected)
            {
                LogCandidateRejected(other, box, "category_mismatch");
            }
            return;
        }

        if (box.IsGrabbedOrSelected)
        {
            if (logRejected)
            {
                LogCandidateRejected(other, box, "box_still_grabbed");
            }
            return;
        }

        if (!pendingValidations.ContainsKey(box))
        {
            Coroutine routine = StartCoroutine(ValidateAfterDelay(box, other));
            pendingValidations.Add(box, routine);
            LogCandidateAccepted(other, box, "validation_started");
        }
    }

    private IEnumerator ValidateAfterDelay(BoxMetadata box, Collider sourceCollider)
    {
        float elapsed = 0f;

        while (elapsed < requiredStayTime)
        {
            if (box == null)
            {
                yield break;
            }

            if (box.isDeposited)
            {
                pendingValidations.Remove(box);
                yield break;
            }

            if (box.IsGrabbedOrSelected)
            {
                pendingValidations.Remove(box);
                LogCandidateRejected(sourceCollider, box, "box_regrabbed_during_validation");
                yield break;
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        ConfirmDeposit(box, sourceCollider);

        if (pendingValidations.ContainsKey(box))
            pendingValidations.Remove(box);
    }

    private void ConfirmDeposit(BoxMetadata box, Collider sourceCollider)
    {
        if (box == null || box.isDeposited)
        {
            return;
        }

        RoundManager roundManager = FindFirstObjectByType<RoundManager>();
        if (roundManager != null)
        {
            if (roundManager.TryRegisterReleasedCorrectDeposit(box, out string rejectionReason))
            {
                LogCandidateAccepted(sourceCollider, box, "deposit_confirmed");
            }
            else
            {
                LogCandidateRejected(sourceCollider, box, rejectionReason);
            }
        }
        else
        {
            LogCandidateRejected(sourceCollider, box, "round_manager_missing");
        }
    }

    private static BoxMetadata ResolveBox(Collider other)
    {
        if (other == null)
        {
            return null;
        }

        return other.GetComponent<BoxMetadata>() ??
            other.GetComponentInParent<BoxMetadata>() ??
            other.GetComponentInChildren<BoxMetadata>();
    }

    private bool ShouldLogTriggerStay(Collider other)
    {
        if (other == null)
        {
            return false;
        }

        return stayDiagnosticsLogged.Add(other);
    }

    private void LogTriggerEvent(string eventType, Collider other, BoxMetadata box, string rejectionReason)
    {
        if (!enableP44DepositDiagnostics)
        {
            return;
        }

        TiagoExperimentTelemetry.LogEvent(eventType, BuildDepositPayload(other, box, rejectionReason));
    }

    private void LogCandidateEvaluated(Collider other, BoxMetadata box, string source)
    {
        if (!enableP44DepositDiagnostics)
        {
            return;
        }

        Dictionary<string, object> payload = BuildDepositPayload(other, box, string.Empty);
        payload["candidate_source"] = source ?? string.Empty;
        TiagoExperimentTelemetry.LogEvent("deposit_zone_candidate_evaluated", payload);
    }

    private void LogCandidateRejected(Collider other, BoxMetadata box, string rejectionReason)
    {
        if (!enableP44DepositDiagnostics)
        {
            return;
        }

        TiagoExperimentTelemetry.LogEvent("deposit_zone_candidate_rejected", BuildDepositPayload(other, box, rejectionReason));
    }

    private void LogCandidateAccepted(Collider other, BoxMetadata box, string reason)
    {
        if (!enableP44DepositDiagnostics)
        {
            return;
        }

        TiagoExperimentTelemetry.LogEvent("deposit_zone_candidate_accepted", BuildDepositPayload(other, box, reason));
    }

    private Dictionary<string, object> BuildDepositPayload(Collider other, BoxMetadata box, string rejectionReason)
    {
        Collider zoneCollider = GetComponent<Collider>();
        Vector3 candidateCenter = other != null ? other.bounds.center : (box != null ? box.transform.position : Vector3.zero);
        bool insideBounds = zoneCollider != null && other != null && zoneCollider.bounds.Intersects(other.bounds);
        RoundManager roundManager = FindFirstObjectByType<RoundManager>();
        string boxCategory = box != null ? box.boxType.ToString() : string.Empty;
        string expectedCategory = acceptedType.ToString();
        int otherLayer = other != null ? other.gameObject.layer : -1;
        return new Dictionary<string, object>
        {
            ["zone_id"] = gameObject.name,
            ["zone_category"] = expectedCategory,
            ["box_id"] = box != null ? box.gameObject.name : string.Empty,
            ["box_alias"] = box != null ? ResolveBoxAlias(box) : string.Empty,
            ["box_category"] = boxCategory,
            ["expected_category"] = expectedCategory,
            ["category_match"] = box != null && box.boxType == acceptedType,
            ["belongs_to_active_round"] = box != null && roundManager != null && roundManager.IsInActiveRound(box),
            ["box_is_deposited"] = box != null && box.isDeposited,
            ["box_is_grabbed"] = box != null && box.isGrabbed,
            ["box_is_selected"] = box != null && box.IsSelected,
            ["distance_to_zone_center"] = zoneCollider != null ? Vector3.Distance(candidateCenter, zoneCollider.bounds.center) : 0f,
            ["inside_bounds"] = insideBounds,
            ["collider_name"] = zoneCollider != null ? zoneCollider.name : string.Empty,
            ["other_collider_name"] = other != null ? other.name : string.Empty,
            ["layer"] = otherLayer,
            ["layer_name"] = otherLayer >= 0 ? LayerMask.LayerToName(otherLayer) : string.Empty,
            ["tag"] = other != null ? other.tag : string.Empty,
            ["rejection_reason"] = rejectionReason ?? string.Empty,
            ["zone_position"] = transform.position,
            ["zone_bounds_center"] = zoneCollider != null ? zoneCollider.bounds.center : Vector3.zero,
            ["zone_bounds_size"] = zoneCollider != null ? zoneCollider.bounds.size : Vector3.zero,
            ["other_bounds_center"] = other != null ? other.bounds.center : Vector3.zero,
            ["other_bounds_size"] = other != null ? other.bounds.size : Vector3.zero,
            ["required_stay_time"] = requiredStayTime,
            ["frame_count"] = Time.frameCount,
            ["time_since_start"] = Time.time
        };
    }

    private static string ResolveBoxAlias(BoxMetadata box)
    {
        if (box == null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(box.displayLabel))
        {
            return box.displayLabel;
        }

        if (!string.IsNullOrWhiteSpace(box.voiceAlias))
        {
            return box.voiceAlias;
        }

        return box.spokenLabel ?? string.Empty;
    }
}
