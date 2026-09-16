using System;
using System.Collections.Generic;
using Autonomy.Domain;
using Autonomy.Perception;
using Autonomy.UnityIntegration;
using UnityEngine;
using UnityEngine.AI;

namespace Autonomy.Provisioning
{
    [DisallowMultipleComponent]
    public sealed class PerceptionTargetSeeder : MonoBehaviour
    {
        private const string LogPrefix = "[PerceptionTargetSeeder]";
        private const string ApproachSourceApproachChild = "ApproachChild";
        private const string ApproachSourceBoundsCenterFallback = "BoundsCenterFallback";
        private const string ApproachSourceAutoComputed = "AutoComputed";

        [Header("References")]
        [SerializeField] private AutonomousRobotAdapter _robotAdapter;
        [SerializeField] private UnityScenePerceptionService _perceptionService;
        [SerializeField] private Transform _referenceTransform;

        [Header("Perception Target Injection")]
        [SerializeField] private KeyCode _seedKey = KeyCode.P;
        [SerializeField] private bool _seedOnStart = false;
        [SerializeField] private PerceptionSelectionStrategy _selectionStrategy = PerceptionSelectionStrategy.NearestAvailable;
        [SerializeField] private string _objectId = "";
        [SerializeField] private string _category = "";
        [SerializeField] private bool _rejectDeposited = true;
        [SerializeField] private bool _rejectGrabbed = true;
        [SerializeField] private bool _rejectHeld = true;

        [Header("Navigation Position")]
        [Tooltip("Optional child name under the perceived object. If found, its position is used for navigation while the object id remains semantic.")]
        [SerializeField] private string _approachChildName = "ApproachPoint";
        [SerializeField] private Vector3 _navigationPositionOffset = Vector3.zero;
        [SerializeField] private bool _useBoundsCenterForNavigation = true;
        [SerializeField] private bool _forceGroundY = true;
        [SerializeField] private float _navMeshSampleRadius = 0.75f;

        private void Awake()
        {
            TryResolveReferences();
        }

        private void Start()
        {
            if (_seedOnStart)
            {
                SeedPerceivedTarget();
            }
        }

        private void Update()
        {
            if (RuntimeHotkeyInput.GetKeyDown(_seedKey))
            {
                SeedPerceivedTarget();
            }
        }

        public void SeedPerceivedTarget()
        {
            if (_robotAdapter == null || _robotAdapter.TargetSeeder == null)
            {
                Debug.LogError($"{LogPrefix} Missing ready {nameof(AutonomousRobotAdapter)} target provisioner. Injection skipped.", this);
                return;
            }

            if (_perceptionService == null)
            {
                Debug.LogError($"{LogPrefix} Missing {nameof(UnityScenePerceptionService)} reference. Injection skipped.", this);
                return;
            }

            Vector3 referencePosition = ResolveReferencePosition();
            var query = new PerceptionQuery(
                _selectionStrategy,
                _objectId,
                _category,
                referencePosition,
                _rejectDeposited,
                _rejectGrabbed,
                _rejectHeld);

            if (!_perceptionService.TrySelectTarget(query, out PerceivedObject selected, out _, out string failureReason))
            {
                Debug.LogWarning($"{LogPrefix} perception_target_selection_failed | reason={failureReason}", this);
                return;
            }

            NavigationTargetResolution navigationTarget = ResolveNavigationTarget(selected);
            WarnIfBoundsCenterFallback(navigationTarget, selected);
            WarnIfNavigationTargetIsOffNavMesh(navigationTarget.Position, selected.ObjectId);
            TargetDescriptor target = PerceptionTargetSelector.ToTargetDescriptor(selected, navigationTarget.Position);
            bool blackboardWriteSuccess = TrySeedTargetDescriptor(target);
            LogTargetSeededToBlackboard(selected, target, navigationTarget, referencePosition, blackboardWriteSuccess);

            Debug.Log(
                $"{LogPrefix} Injected {TaskBlackboardKeys.CurrentTarget.Id}: source=Perception, id={target.Id}, strategy={_selectionStrategy}, navigationPosition={FormatVector(navigationTarget.Position)}, boundsCenter={FormatVector(selected.BoundsCenter)}, approachSource={navigationTarget.ApproachSource}, blackboardWriteSuccess={blackboardWriteSuccess}",
                this);
        }

        private NavigationTargetResolution ResolveNavigationTarget(PerceivedObject selected)
        {
            Transform approach = FindChildRecursive(selected.Transform, _approachChildName);
            Vector3 position;
            string approachSource;

            if (approach != null)
            {
                position = approach.position;
                approachSource = ApproachSourceApproachChild;
            }
            else if (_useBoundsCenterForNavigation)
            {
                position = selected.BoundsCenter;
                approachSource = ApproachSourceBoundsCenterFallback;
            }
            else
            {
                position = selected.WorldPosition;
                approachSource = ApproachSourceAutoComputed;
            }

            position += _navigationPositionOffset;
            if (_forceGroundY)
            {
                position.y = 0f;
            }

            return new NavigationTargetResolution(position, approachSource, approach != null ? approach.name : string.Empty);
        }

        private bool TrySeedTargetDescriptor(TargetDescriptor target)
        {
            try
            {
                _robotAdapter.TargetSeeder.SeedTarget(target);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"{LogPrefix} Failed to write perceived target to blackboard: {exception.Message}", this);
                return false;
            }
        }

        private void WarnIfBoundsCenterFallback(NavigationTargetResolution navigationTarget, PerceivedObject selected)
        {
            if (navigationTarget.ApproachSource != ApproachSourceBoundsCenterFallback)
            {
                return;
            }

            string message = "bounds_center_fallback_used";
            Debug.LogWarning(
                $"{LogPrefix} {message} | id={selected.ObjectId} boundsCenter={FormatVector(selected.BoundsCenter)} navigationPosition={FormatVector(navigationTarget.Position)} may_not_be_navigable_if_inside_or_near_obstacle_carving",
                this);

            TiagoExperimentTelemetry.LogEvent(
                "perception_bounds_center_fallback_used",
                new Dictionary<string, object>
                {
                    ["object_id"] = selected.ObjectId,
                    ["object_name"] = selected.DisplayName,
                    ["bounds_center"] = selected.BoundsCenter,
                    ["bounds_extents"] = selected.BoundsExtents,
                    ["bounds_source"] = selected.BoundsSource.ToString(),
                    ["navigation_position"] = navigationTarget.Position,
                    ["approach_child_name"] = _approachChildName,
                    ["warning"] = "bounds_center_may_not_be_navigable_if_inside_or_near_obstacle_carving"
                });
        }

        private void LogTargetSeededToBlackboard(
            PerceivedObject selected,
            TargetDescriptor target,
            NavigationTargetResolution navigationTarget,
            Vector3 referencePosition,
            bool blackboardWriteSuccess)
        {
            TiagoExperimentTelemetry.LogEvent(
                "perception_target_seeded_to_blackboard",
                new Dictionary<string, object>
                {
                    ["object_id"] = selected.ObjectId,
                    ["object_name"] = selected.DisplayName,
                    ["object_center"] = selected.BoundsCenter,
                    ["bounds_center"] = selected.BoundsCenter,
                    ["bounds_extents"] = selected.BoundsExtents,
                    ["bounds_source"] = selected.BoundsSource.ToString(),
                    ["target_descriptor_object_id"] = target.Id,
                    ["target_descriptor_position"] = navigationTarget.Position,
                    ["navigation_position"] = navigationTarget.Position,
                    ["approach_source"] = navigationTarget.ApproachSource,
                    ["approach_child_name"] = !string.IsNullOrWhiteSpace(navigationTarget.ApproachChildName)
                        ? navigationTarget.ApproachChildName
                        : _approachChildName,
                    ["reference_position"] = referencePosition,
                    ["selection_strategy"] = _selectionStrategy.ToString(),
                    ["reject_deposited"] = _rejectDeposited,
                    ["reject_grabbed"] = _rejectGrabbed,
                    ["reject_held"] = _rejectHeld,
                    ["force_ground_y"] = _forceGroundY,
                    ["navmesh_sample_radius"] = Mathf.Max(0.05f, _navMeshSampleRadius),
                    ["blackboard_write_success"] = blackboardWriteSuccess
                });
        }

        private Vector3 ResolveReferencePosition()
        {
            if (_referenceTransform != null)
            {
                return _referenceTransform.position;
            }

            if (_robotAdapter != null)
            {
                return _robotAdapter.NavigationReference.position;
            }

            return transform.position;
        }

        private static Transform FindChildRecursive(Transform root, string childName)
        {
            if (root == null || string.IsNullOrWhiteSpace(childName))
            {
                return null;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.name == childName)
                {
                    return child;
                }

                Transform nested = FindChildRecursive(child, childName);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private void WarnIfNavigationTargetIsOffNavMesh(Vector3 unityPosition, string targetId)
        {
            float sampleRadius = Mathf.Max(0.05f, _navMeshSampleRadius);
            if (NavMesh.SamplePosition(unityPosition, out _, sampleRadius, NavMesh.AllAreas))
            {
                return;
            }

            Debug.LogWarning(
                $"{LogPrefix} perception_target_not_on_navmesh | id={targetId} position={FormatVector(unityPosition)} sampleRadius={sampleRadius:F2}",
                this);
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F2},{value.y:F2},{value.z:F2})";
        }

        private void TryResolveReferences()
        {
            if (_robotAdapter == null)
            {
                _robotAdapter = GetComponent<AutonomousRobotAdapter>();
            }

            if (_robotAdapter == null)
            {
                _robotAdapter = GetComponentInParent<AutonomousRobotAdapter>();
            }

            if (_perceptionService == null)
            {
                _perceptionService = GetComponent<UnityScenePerceptionService>();
            }

            if (_perceptionService == null)
            {
                _perceptionService = GetComponentInParent<UnityScenePerceptionService>();
            }

            if (_referenceTransform == null && _robotAdapter != null)
            {
                _referenceTransform = _robotAdapter.NavigationReference;
            }
        }

        private void Reset()
        {
            TryResolveReferences();
        }

        private void OnValidate()
        {
            if (_navMeshSampleRadius < 0.05f)
            {
                _navMeshSampleRadius = 0.05f;
            }
        }

        private readonly struct NavigationTargetResolution
        {
            public Vector3 Position { get; }
            public string ApproachSource { get; }
            public string ApproachChildName { get; }

            public NavigationTargetResolution(Vector3 position, string approachSource, string approachChildName)
            {
                Position = position;
                ApproachSource = approachSource;
                ApproachChildName = approachChildName;
            }
        }
    }
}
