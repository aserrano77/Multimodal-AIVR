using System;
using System.Collections.Generic;
using Autonomy.Domain;
using UnityEngine;
using UnityEngine.AI;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class MultimodalPlaceTargetRegistry : MonoBehaviour
    {
        [Serializable]
        private sealed class PlaceTargetEntry
        {
            [SerializeField] private string _id = "ZoneA";
            [SerializeField] private Transform _placeTransform;
            [SerializeField] private Transform _navigationTransform;
            [SerializeField] private Vector3 _navigationPositionOffset = Vector3.zero;
            [SerializeField] private bool _forceGroundY = true;

            public string Id => _id;
            public Transform PlaceTransform => _placeTransform;
            public Transform NavigationTransform => _navigationTransform;
            public Vector3 NavigationPositionOffset => _navigationPositionOffset;
            public bool ForceGroundY => _forceGroundY;
        }

        [SerializeField] private List<PlaceTargetEntry> _entries = new();
        [SerializeField] private float _navMeshSampleRadius = 0.75f;

        public bool TryResolve(string placeTargetId, out TargetDescriptor target, out Vector3 placePosition, out string failureReason)
        {
            target = null;
            placePosition = Vector3.zero;
            failureReason = string.Empty;

            if (string.IsNullOrWhiteSpace(placeTargetId))
            {
                failureReason = "missing_place_target_id";
                return false;
            }

            PlaceTargetEntry entry = FindEntry(placeTargetId);
            if (entry == null)
            {
                failureReason = "place_target_not_registered";
                return false;
            }

            if (entry.PlaceTransform == null)
            {
                failureReason = "place_transform_missing";
                return false;
            }

            Transform navigation = entry.NavigationTransform != null ? entry.NavigationTransform : entry.PlaceTransform;
            Vector3 navigationPosition = navigation.position + entry.NavigationPositionOffset;
            if (entry.ForceGroundY)
            {
                navigationPosition.y = 0f;
            }

            target = new TargetDescriptor(
                entry.Id,
                new System.Numerics.Vector3(navigationPosition.x, navigationPosition.y, navigationPosition.z));
            placePosition = entry.PlaceTransform.position;

            WarnIfNavigationTargetIsOffNavMesh(navigationPosition, placeTargetId);
            return true;
        }

        public bool TryResolveTransforms(
            string placeTargetId,
            out string semanticId,
            out Transform placeTransform,
            out Transform navigationTransform,
            out string failureReason)
        {
            semanticId = string.Empty;
            placeTransform = null;
            navigationTransform = null;
            failureReason = string.Empty;

            if (string.IsNullOrWhiteSpace(placeTargetId))
            {
                failureReason = "missing_place_target_id";
                return false;
            }

            PlaceTargetEntry entry = FindEntry(placeTargetId);
            if (entry == null)
            {
                failureReason = "place_target_not_registered";
                return false;
            }

            if (entry.PlaceTransform == null)
            {
                failureReason = "place_transform_missing";
                return false;
            }

            semanticId = entry.Id;
            placeTransform = entry.PlaceTransform;
            navigationTransform = entry.NavigationTransform != null ? entry.NavigationTransform : entry.PlaceTransform;
            return true;
        }

        private PlaceTargetEntry FindEntry(string placeTargetId)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                PlaceTargetEntry entry = _entries[i];
                if (entry != null && string.Equals(entry.Id, placeTargetId, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
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
                $"[MultimodalPlaceTargetRegistry] place_target_not_on_navmesh | id={targetId} position=({unityPosition.x:F2},{unityPosition.y:F2},{unityPosition.z:F2}) sampleRadius={sampleRadius:F2}",
                this);
        }

        private void OnValidate()
        {
            if (_navMeshSampleRadius < 0.05f)
            {
                _navMeshSampleRadius = 0.05f;
            }
        }
    }
}
