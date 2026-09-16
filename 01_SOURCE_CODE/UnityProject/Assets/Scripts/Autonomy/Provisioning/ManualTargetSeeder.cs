using System;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using UnityEngine;
using UnityEngine.AI;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Autonomy.Provisioning
{
    /// <summary>
    /// Componente de runtime para inyectar manualmente un target en el Blackboard.
    /// Usa el acceso controlado expuesto por el adapter y evita escribir directamente sobre el Blackboard.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ManualTargetSeeder : MonoBehaviour
    {
        private const string LogPrefix = "[ManualTargetSeeder]";
        private const string ManualTargetIdPrefix = "manual_target";

        private enum ManualTargetSource
        {
            Coordinates,
            Transform
        }

        [Header("References")]
        [SerializeField] private AutonomousRobotAdapter _robotAdapter;

        [Header("Manual Injection")]
        [Tooltip("Coordinates uses Target Position and ignores Target Transform. Transform uses Target Transform and ignores Target Position except for Transform Position Offset.")]
        [SerializeField] private ManualTargetSource _targetSource = ManualTargetSource.Coordinates;
        [SerializeField] private KeyCode _seedKey = KeyCode.T;
        [Tooltip("Used only when Target Source is Coordinates. Kept for legacy/manual navigation validation.")]
        [SerializeField] private Vector3 _targetPosition = new Vector3(2f, 0f, 2f);
        [Tooltip("Used only when Target Source is Transform. The injected TargetDescriptor.Id will be this GameObject name.")]
        [SerializeField] private Transform _targetTransform;
        [Tooltip("Optional. Used only when Target Source is Transform. Target Transform remains the semantic object; this transform provides the navigation approach point on/near the NavMesh.")]
        [SerializeField] private Transform _approachTransform;
        [Tooltip("Used only when Target Source is Transform. Offsets the injected navigation target without moving the scene object.")]
        [SerializeField] private Vector3 _transformPositionOffset = Vector3.zero;
        [Tooltip("Used only when Target Source is Transform. Forces the injected target Y to 0 for flat NavMesh navigation.")]
        [SerializeField] private bool _forceGroundY = true;
        [Tooltip("Diagnostic radius used to warn when the final manual navigation target is not close to the NavMesh. The target is not corrected automatically.")]
        [SerializeField] private float _navMeshSampleRadius = 0.75f;

        private void Awake()
        {
            TryResolveRobotAdapter();
        }

        private void Reset()
        {
            TryResolveRobotAdapter();
        }

        private void Update()
        {
            if (RuntimeHotkeyInput.GetKeyDown(_seedKey))
            {
                SeedCurrentTarget();
            }
        }

        public void SeedCurrentTarget()
        {
            if (_robotAdapter == null)
            {
                Debug.LogError($"{LogPrefix} Missing {nameof(AutonomousRobotAdapter)} reference. Injection skipped.", this);
                return;
            }

            if (_robotAdapter.TargetSeeder == null)
            {
                Debug.LogError($"{LogPrefix} Adapter target provisioner is not ready yet. Injection skipped.", this);
                return;
            }

            if (!TryBuildTargetDescriptor(out TargetDescriptor target, out Vector3 unityPosition, out string sourceDetails))
            {
                return;
            }

            WarnIfNavigationTargetIsOffNavMesh(unityPosition, target.Id);

            _robotAdapter.TargetSeeder.SeedTarget(target);

            Debug.Log(
                $"{LogPrefix} Injected {TaskBlackboardKeys.CurrentTarget.Id}: source={_targetSource}, id={target.Id}, position={FormatVector(unityPosition)}{sourceDetails}",
                this);
        }

        private bool TryBuildTargetDescriptor(out TargetDescriptor target, out Vector3 unityPosition, out string sourceDetails)
        {
            target = null;
            unityPosition = Vector3.zero;
            sourceDetails = string.Empty;

            if (_targetSource == ManualTargetSource.Transform)
            {
                if (_targetTransform == null)
                {
                    Debug.LogWarning($"{LogPrefix} Target Source is Transform but Target Transform is null. Injection skipped.", this);
                    return false;
                }

                bool usesApproachTransform = _approachTransform != null;
                unityPosition = usesApproachTransform
                    ? _approachTransform.position
                    : _targetTransform.position + _transformPositionOffset;
                if (_forceGroundY)
                {
                    unityPosition.y = 0f;
                }

                target = new TargetDescriptor(
                    _targetTransform.gameObject.name,
                    new System.Numerics.Vector3(unityPosition.x, unityPosition.y, unityPosition.z));
                sourceDetails = usesApproachTransform
                    ? $", targetTransform={_targetTransform.name}, approachTransform={_approachTransform.name}"
                    : $", targetTransform={_targetTransform.name}, transformOffset={FormatVector(_transformPositionOffset)}";
                return true;
            }

            unityPosition = _targetPosition;
            target = new TargetDescriptor(
                BuildUniqueTargetId(),
                new System.Numerics.Vector3(unityPosition.x, unityPosition.y, unityPosition.z));
            return true;
        }

        private static string BuildUniqueTargetId()
        {
            return $"{ManualTargetIdPrefix}_{Guid.NewGuid():N}";
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F2},{value.y:F2},{value.z:F2})";
        }

        private void WarnIfNavigationTargetIsOffNavMesh(Vector3 unityPosition, string targetId)
        {
            float sampleRadius = Mathf.Max(0.05f, _navMeshSampleRadius);
            if (NavMesh.SamplePosition(unityPosition, out _, sampleRadius, NavMesh.AllAreas))
            {
                return;
            }

            Debug.LogWarning(
                $"{LogPrefix} manual_target_not_on_navmesh | id={targetId} position={FormatVector(unityPosition)} sampleRadius={sampleRadius:F2} source={_targetSource}",
                this);
        }

        private void TryResolveRobotAdapter()
        {
            if (_robotAdapter == null)
            {
                _robotAdapter = GetComponent<AutonomousRobotAdapter>();
            }

            if (_robotAdapter == null)
            {
                _robotAdapter = GetComponentInParent<AutonomousRobotAdapter>();
            }
        }

        private void OnValidate()
        {
            if (ShouldWarnAboutInvalidEditorConfiguration() &&
                _targetSource == ManualTargetSource.Transform &&
                _targetTransform == null)
            {
                Debug.LogWarning($"{LogPrefix} Target Source is Transform but Target Transform is not assigned. Coordinates are ignored in this mode.", this);
            }

            if (_navMeshSampleRadius < 0.05f)
            {
                _navMeshSampleRadius = 0.05f;
            }
        }

        private bool ShouldWarnAboutInvalidEditorConfiguration()
        {
#if UNITY_EDITOR
            if (this == null || gameObject == null)
            {
                return false;
            }

            if (!isActiveAndEnabled || !gameObject.activeInHierarchy || !gameObject.scene.IsValid())
            {
                return false;
            }

            if (EditorUtility.IsPersistent(this) ||
                EditorUtility.IsPersistent(gameObject) ||
                PrefabUtility.IsPartOfPrefabAsset(gameObject))
            {
                return false;
            }
#endif

            return true;
        }
    }
}
