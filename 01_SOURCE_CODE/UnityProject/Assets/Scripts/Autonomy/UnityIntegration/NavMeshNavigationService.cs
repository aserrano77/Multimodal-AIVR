using Autonomy.BT.Core;
using Autonomy.Services;
using UnityEngine;
using UnityEngine.AI;
using DomainVector3 = System.Numerics.Vector3;
using UnityVector3 = UnityEngine.Vector3;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Implementacion real de navegacion basada en NavMeshAgent.
    /// Mantiene el contrato de INavigationService y traduce el estado del agente a NodeStatus.
    /// </summary>
    public sealed class NavMeshNavigationService : INavigationService
    {
        private readonly NavMeshAgent _agent;
        private readonly float _arrivalTolerance;
        private UnityVector3? _currentDestination;

        public NavMeshNavigationService(NavMeshAgent agent, float arrivalTolerance = 0.1f)
        {
            _agent = agent;
            _arrivalTolerance = Mathf.Max(0f, arrivalTolerance);
        }

        public NodeStatus MoveTo(DomainVector3 position)
        {
            if (_agent == null || !_agent.isOnNavMesh)
            {
                return NodeStatus.Failure;
            }

            UnityVector3 unityTarget = new UnityVector3(position.X, position.Y, position.Z);

            if (_currentDestination == null || !AreEquivalent(unityTarget, _currentDestination.Value))
            {
                if (!_agent.SetDestination(unityTarget))
                {
                    ClearNavigationState();
                    return NodeStatus.Failure;
                }

                _agent.isStopped = false;
                _currentDestination = unityTarget;
            }

            if (_agent.pathPending)
            {
                return NodeStatus.Running;
            }

            if (_agent.pathStatus == NavMeshPathStatus.PathInvalid || _agent.pathStatus == NavMeshPathStatus.PathPartial)
            {
                ClearNavigationState();
                return NodeStatus.Failure;
            }

            if (HasArrived())
            {
                ClearNavigationState();
                return NodeStatus.Success;
            }

            return NodeStatus.Running;
        }

        public void Stop()
        {
            if (_agent == null)
            {
                _currentDestination = null;
                return;
            }

            _agent.isStopped = true;

            if (_agent.isOnNavMesh)
            {
                _agent.ResetPath();
            }

            _currentDestination = null;
        }

        private bool HasArrived()
        {
            if (_agent.pathPending)
            {
                return false;
            }

            if (_agent.remainingDistance > _agent.stoppingDistance + _arrivalTolerance)
            {
                return false;
            }

            return !_agent.hasPath || _agent.velocity.sqrMagnitude <= 0.01f;
        }

        private void ClearNavigationState()
        {
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.isStopped = true;
                _agent.ResetPath();
            }

            _currentDestination = null;
        }

        private static bool AreEquivalent(UnityVector3 left, UnityVector3 right)
        {
            return (left - right).sqrMagnitude <= 0.0001f;
        }
    }
}
