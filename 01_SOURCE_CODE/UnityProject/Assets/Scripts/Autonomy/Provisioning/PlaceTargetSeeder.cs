using System.Collections.Generic;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using UnityEngine;
using UnityEngine.AI;

namespace Autonomy.Provisioning
{
    [DisallowMultipleComponent]
    public sealed class PlaceTargetSeeder : MonoBehaviour
    {
        private const string LogPrefix = "[PlaceTargetSeeder]";

        [Header("References")]
        [SerializeField] private AutonomousRobotAdapter _robotAdapter;

        [Header("Place Target Injection")]
        [SerializeField] private KeyCode _seedKey = KeyCode.L;
        [SerializeField] private bool _seedOnStart = true;
        [Tooltip("Explicit semantic release pose. Its GameObject name is written as the place target id.")]
        [SerializeField] private Transform _placeTransform;
        [Tooltip("Optional navigable approach point for the drop zone. If empty, Place Transform is used for navigation too.")]
        [SerializeField] private Transform _navigationTransform;
        [SerializeField] private Vector3 _navigationPositionOffset = Vector3.zero;
        [SerializeField] private bool _forceGroundY = true;
        [SerializeField] private float _navMeshSampleRadius = 0.75f;

        private void Awake()
        {
            TryResolveRobotAdapter();
        }

        private void Start()
        {
            if (_seedOnStart)
            {
                SeedPlaceTarget();
            }
        }

        private void Update()
        {
            if (RuntimeHotkeyInput.GetKeyDown(_seedKey))
            {
                SeedPlaceTarget();
            }
        }

        public void SeedPlaceTarget()
        {
            if (_robotAdapter == null || _robotAdapter.PlaceTargetSeeder == null)
            {
                Debug.LogError($"{LogPrefix} Missing ready {nameof(AutonomousRobotAdapter)} place target provisioner. Injection skipped.", this);
                return;
            }

            if (_placeTransform == null)
            {
                Debug.LogWarning($"{LogPrefix} Place Transform is not assigned. Injection skipped.", this);
                return;
            }

            Transform navigation = _navigationTransform != null ? _navigationTransform : _placeTransform;
            Vector3 navigationPosition = navigation.position + _navigationPositionOffset;
            if (_forceGroundY)
            {
                navigationPosition.y = 0f;
            }

            var target = new TargetDescriptor(
                _placeTransform.gameObject.name,
                new System.Numerics.Vector3(navigationPosition.x, navigationPosition.y, navigationPosition.z));

            WarnIfNavigationTargetIsOffNavMesh(navigationPosition, target.Id);
            _robotAdapter.PlaceTargetSeeder.SeedTarget(target);

            TiagoExperimentTelemetry.LogEvent(
                "place_target_seeded",
                new Dictionary<string, object>
                {
                    ["place_target_id"] = target.Id,
                    ["place_transform_name"] = _placeTransform.name,
                    ["navigation_transform_name"] = navigation.name,
                    ["place_position"] = _placeTransform.position,
                    ["navigation_position"] = navigationPosition,
                    ["force_ground_y"] = _forceGroundY,
                    ["navmesh_sample_radius"] = Mathf.Max(0.05f, _navMeshSampleRadius)
                });

            Debug.Log(
                $"{LogPrefix} Injected {TaskBlackboardKeys.PlaceTarget.Id}: id={target.Id}, placePosition={FormatVector(_placeTransform.position)}, navigationPosition={FormatVector(navigationPosition)}",
                this);
        }

        private void WarnIfNavigationTargetIsOffNavMesh(Vector3 unityPosition, string targetId)
        {
            float sampleRadius = Mathf.Max(0.05f, _navMeshSampleRadius);
            if (NavMesh.SamplePosition(unityPosition, out _, sampleRadius, NavMesh.AllAreas))
            {
                return;
            }

            Debug.LogWarning(
                $"{LogPrefix} place_target_not_on_navmesh | id={targetId} position={FormatVector(unityPosition)} sampleRadius={sampleRadius:F2}",
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

        private void Reset()
        {
            TryResolveRobotAdapter();
        }

        private void OnValidate()
        {
            if (_navMeshSampleRadius < 0.05f)
            {
                _navMeshSampleRadius = 0.05f;
            }
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F2},{value.y:F2},{value.z:F2})";
        }
    }
}
