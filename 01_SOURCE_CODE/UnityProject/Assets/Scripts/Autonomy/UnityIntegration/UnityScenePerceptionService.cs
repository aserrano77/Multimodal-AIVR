using System;
using System.Collections.Generic;
using System.Reflection;
using Autonomy.Perception;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class UnityScenePerceptionService : MonoBehaviour, IPerceptionService
    {
        private const string BoxMetadataTypeName = "BoxMetadata";
        private const string XrGrabInteractableTypeName = "XRGrabInteractable";
        private const string EdgeFrameName = "BoxEdgeFrame";

        [Header("Scene Scan")]
        [SerializeField] private bool _includeInactiveObjects = false;
        [SerializeField] private bool _logCandidateDetails = false;
        [SerializeField] private bool _useTagFilter = false;
        [SerializeField] private string _requiredTag = "Manipulable";
        [SerializeField] private bool _useLayerMask = false;
        [SerializeField] private LayerMask _layerMask = ~0;

        [Header("Held State")]
        [SerializeField] private AutonomousRobotAdapter _robotAdapter;

        public IReadOnlyList<PerceivedObject> Scan(PerceptionQuery query)
        {
            return Scan(query, true);
        }

        private IReadOnlyList<PerceivedObject> Scan(PerceptionQuery query, bool logCompletion)
        {
            Vector3 reference = query.ReferencePosition ?? Vector3.zero;
            LogScanStarted(query, reference);

            var candidates = new List<PerceivedObject>();
            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(
                _includeInactiveObjects ? FindObjectsInactive.Include : FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            string heldObjectId = _robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty;

            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null || !IsBoxMetadata(behaviour))
                {
                    continue;
                }

                PerceivedObject candidate = BuildCandidate(behaviour, query, heldObjectId);
                if (candidate != null)
                {
                    candidates.Add(candidate);
                    LogCandidateIfEnabled(candidate);
                }
            }

            if (logCompletion)
            {
                int acceptedCount = CountAccepted(candidates);
                LogScanCompleted(candidates.Count, acceptedCount, candidates.Count - acceptedCount, string.Empty);
            }

            return candidates;
        }

        public bool TrySelectTarget(
            PerceptionQuery query,
            out PerceivedObject selected,
            out IReadOnlyList<PerceivedObject> candidates,
            out string failureReason)
        {
            candidates = Scan(query, false);
            bool success = PerceptionTargetSelector.TrySelect(candidates, query, out selected, out failureReason);
            int acceptedCount = CountAccepted(candidates);

            if (success)
            {
                LogScanCompleted(candidates.Count, acceptedCount, candidates.Count - acceptedCount, selected.ObjectId);
                LogTargetSelected(selected, query.SelectionStrategy.ToString());
                return true;
            }

            LogTargetSelectionFailed(failureReason, candidates.Count, acceptedCount);
            return false;
        }

        private PerceivedObject BuildCandidate(MonoBehaviour metadata, PerceptionQuery query, string heldObjectId)
        {
            GameObject gameObject = metadata.gameObject;
            Transform candidateTransform = metadata.transform;
            bool isActive = gameObject.activeInHierarchy;
            bool passesTag = !_useTagFilter || string.Equals(gameObject.tag, _requiredTag, StringComparison.Ordinal);
            bool passesLayer = !_useLayerMask || ((_layerMask.value & (1 << gameObject.layer)) != 0);
            bool isDeposited = GetBool(metadata, "isDeposited");
            bool isGrabbed = GetBool(metadata, "isGrabbed") || IsSelectedByXr(gameObject);
            bool isHeld = !string.IsNullOrWhiteSpace(heldObjectId) &&
                          string.Equals(gameObject.name, heldObjectId, StringComparison.OrdinalIgnoreCase);
            bool isManipulable = passesTag && passesLayer;

            Bounds bounds = ResolveBounds(gameObject, out BoundsSource boundsSource);
            Vector3 reference = query.ReferencePosition ?? Vector3.zero;
            float distance = query.ReferencePosition.HasValue
                ? Vector3.Distance(bounds.center, reference)
                : float.NaN;

            string rejectionReason = ResolveInitialRejectionReason(
                isActive,
                isManipulable,
                isDeposited,
                isGrabbed,
                isHeld,
                query);

            return new PerceivedObject(
                gameObject.name,
                gameObject.name,
                GetMemberText(metadata, "boxType"),
                candidateTransform,
                candidateTransform.position,
                bounds.center,
                bounds.extents,
                boundsSource,
                isActive,
                isManipulable,
                isDeposited,
                isGrabbed,
                isHeld,
                distance,
                rejectionReason);
        }

        private static string ResolveInitialRejectionReason(
            bool isActive,
            bool isManipulable,
            bool isDeposited,
            bool isGrabbed,
            bool isHeld,
            PerceptionQuery query)
        {
            if (!isActive)
            {
                return "object_inactive";
            }

            if (!isManipulable)
            {
                return "object_not_manipulable";
            }

            if (query.RejectDeposited && isDeposited)
            {
                return "object_already_deposited";
            }

            if (query.RejectGrabbed && isGrabbed)
            {
                return "object_grabbed";
            }

            if (query.RejectHeld && isHeld)
            {
                return "object_already_held";
            }

            return string.Empty;
        }

        private static Bounds ResolveBounds(GameObject root, out BoundsSource source)
        {
            Collider[] colliders = root.GetComponentsInChildren<Collider>(false);
            if (TryCombineColliderBounds(root.transform, colliders, out Bounds colliderBounds))
            {
                source = BoundsSource.Collider;
                return colliderBounds;
            }

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(false);
            if (TryCombineRendererBounds(root.transform, renderers, out Bounds rendererBounds))
            {
                source = BoundsSource.Renderer;
                return rendererBounds;
            }

            source = BoundsSource.TransformFallback;
            return new Bounds(root.transform.position, Vector3.zero);
        }

        private static bool TryCombineColliderBounds(Transform root, Collider[] colliders, out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;

            foreach (Collider collider in colliders)
            {
                if (collider == null || !collider.enabled || collider.isTrigger || IsInsideEdgeFrame(root, collider.transform))
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = collider.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }

            return hasBounds;
        }

        private static bool TryCombineRendererBounds(Transform root, Renderer[] renderers, out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;

            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || !renderer.enabled || IsInsideEdgeFrame(root, renderer.transform))
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasBounds;
        }

        private static bool IsInsideEdgeFrame(Transform root, Transform candidate)
        {
            Transform current = candidate;
            while (current != null && current != root)
            {
                if (current.name == EdgeFrameName)
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private static bool IsBoxMetadata(Component component)
        {
            return component != null && string.Equals(component.GetType().Name, BoxMetadataTypeName, StringComparison.Ordinal);
        }

        private static bool IsSelectedByXr(GameObject gameObject)
        {
            Component grabInteractable = FindComponentByTypeName(gameObject, XrGrabInteractableTypeName);
            return grabInteractable != null && GetBool(grabInteractable, "isSelected");
        }

        private static Component FindComponentByTypeName(GameObject root, string typeName)
        {
            Component[] components = root.GetComponentsInChildren<Component>(true);
            foreach (Component component in components)
            {
                if (component != null && string.Equals(component.GetType().Name, typeName, StringComparison.Ordinal))
                {
                    return component;
                }
            }

            return null;
        }

        private static bool GetBool(Component component, string memberName)
        {
            if (component == null || string.IsNullOrWhiteSpace(memberName))
            {
                return false;
            }

            Type type = component.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo field = type.GetField(memberName, flags);
            if (field != null && field.FieldType == typeof(bool))
            {
                return (bool)field.GetValue(component);
            }

            PropertyInfo property = type.GetProperty(memberName, flags);
            if (property != null && property.PropertyType == typeof(bool) && property.GetIndexParameters().Length == 0)
            {
                return (bool)property.GetValue(component);
            }

            return false;
        }

        private static string GetMemberText(Component component, string memberName)
        {
            if (component == null || string.IsNullOrWhiteSpace(memberName))
            {
                return string.Empty;
            }

            Type type = component.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo field = type.GetField(memberName, flags);
            if (field != null)
            {
                object value = field.GetValue(component);
                return value != null ? value.ToString() : string.Empty;
            }

            PropertyInfo property = type.GetProperty(memberName, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                object value = property.GetValue(component);
                return value != null ? value.ToString() : string.Empty;
            }

            return string.Empty;
        }

        private static int CountAccepted(IReadOnlyList<PerceivedObject> candidates)
        {
            int count = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i] != null && candidates[i].IsAccepted)
                {
                    count++;
                }
            }

            return count;
        }

        private void LogScanStarted(PerceptionQuery query, Vector3 reference)
        {
            TiagoExperimentTelemetry.LogEvent(
                "perception_scan_started",
                new Dictionary<string, object>
                {
                    ["query_type"] = query.SelectionStrategy.ToString(),
                    ["requested_object_id"] = query.ObjectId,
                    ["category"] = query.Category,
                    ["reference_position"] = reference
                });
        }

        private static void LogScanCompleted(int totalCandidates, int acceptedCount, int rejectedCount, string selectedObjectId)
        {
            TiagoExperimentTelemetry.LogEvent(
                "perception_scan_completed",
                new Dictionary<string, object>
                {
                    ["total_candidates_found"] = totalCandidates,
                    ["accepted_count"] = acceptedCount,
                    ["rejected_count"] = rejectedCount,
                    ["selected_object_id"] = selectedObjectId ?? string.Empty
                });
        }

        private void LogCandidateIfEnabled(PerceivedObject candidate)
        {
            if (!_logCandidateDetails || candidate == null)
            {
                return;
            }

            TiagoExperimentTelemetry.LogEvent(
                "perception_object_candidate",
                new Dictionary<string, object>
                {
                    ["object_id"] = candidate.ObjectId,
                    ["name"] = candidate.DisplayName,
                    ["position"] = candidate.WorldPosition,
                    ["bounds_source"] = candidate.BoundsSource.ToString(),
                    ["is_manipulable"] = candidate.IsManipulable,
                    ["is_deposited"] = candidate.IsDeposited,
                    ["is_grabbed"] = candidate.IsGrabbed,
                    ["is_held"] = candidate.IsHeld,
                    ["accepted"] = candidate.IsAccepted,
                    ["rejection_reason"] = candidate.ReasonIfRejected
                });
        }

        private static void LogTargetSelected(PerceivedObject selected, string strategy)
        {
            TiagoExperimentTelemetry.LogEvent(
                "perception_target_selected",
                new Dictionary<string, object>
                {
                    ["object_id"] = selected.ObjectId,
                    ["name"] = selected.DisplayName,
                    ["position"] = selected.WorldPosition,
                    ["bounds_center"] = selected.BoundsCenter,
                    ["bounds_extents"] = selected.BoundsExtents,
                    ["bounds_source"] = selected.BoundsSource.ToString(),
                    ["selection_strategy"] = strategy,
                    ["distance_to_reference"] = selected.DistanceToReference
                });
        }

        private static void LogTargetSelectionFailed(string reason, int candidatesFound, int acceptedCount)
        {
            TiagoExperimentTelemetry.LogEvent(
                "perception_target_selection_failed",
                new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["candidates_found"] = candidatesFound,
                    ["accepted_count"] = acceptedCount
                });
        }

        private void Reset()
        {
            if (_robotAdapter == null)
            {
                _robotAdapter = GetComponent<AutonomousRobotAdapter>();
            }
        }
    }
}
