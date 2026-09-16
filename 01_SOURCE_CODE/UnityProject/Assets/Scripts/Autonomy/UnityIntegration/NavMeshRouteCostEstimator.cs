using Autonomy.Domain;
using UnityEngine;
using UnityEngine.AI;

namespace Autonomy.UnityIntegration
{
    public sealed class NavMeshRouteCostEstimator : IAssistedRouteCostEstimator
    {
        private readonly float _sampleRadius;
        private readonly int _areaMask;

        public NavMeshRouteCostEstimator(float sampleRadius = 0.75f, int areaMask = NavMesh.AllAreas)
        {
            _sampleRadius = Mathf.Max(0.05f, sampleRadius);
            _areaMask = areaMask;
        }

        public bool TryEstimatePathLength(Vector3 from, Vector3 to, out float length, out string failureReason)
        {
            length = 0f;
            failureReason = string.Empty;

            if (!NavMesh.SamplePosition(from, out NavMeshHit fromHit, _sampleRadius, _areaMask))
            {
                failureReason = "navmesh_start_sample_failed";
                return false;
            }

            if (!NavMesh.SamplePosition(to, out NavMeshHit toHit, _sampleRadius, _areaMask))
            {
                failureReason = "navmesh_end_sample_failed";
                return false;
            }

            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(fromHit.position, toHit.position, _areaMask, path) ||
                path.status != NavMeshPathStatus.PathComplete ||
                path.corners == null ||
                path.corners.Length == 0)
            {
                failureReason = "navmesh_path_incomplete";
                return false;
            }

            Vector3 previous = path.corners[0];
            for (int i = 1; i < path.corners.Length; i++)
            {
                length += Vector3.Distance(previous, path.corners[i]);
                previous = path.corners[i];
            }

            return true;
        }
    }
}
