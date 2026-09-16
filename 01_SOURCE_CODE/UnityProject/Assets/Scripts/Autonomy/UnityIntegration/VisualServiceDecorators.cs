using UnityEngine;
using Autonomy.Services;
using Autonomy.BT.Core;
using Autonomy.Domain;
using System.Numerics;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Decorador visual para el servicio de navegación. 
    /// Delega la lógica al servicio base y añade feedback visual en Unity.
    /// </summary>
    public class VisualNavigationDecorator : INavigationService
    {
        private readonly INavigationService _inner;
        private readonly Renderer _robotRenderer;
        private readonly Color _navColor = Color.blue;

        public VisualNavigationDecorator(INavigationService inner, Renderer robotRenderer)
        {
            _inner = inner;
            _robotRenderer = robotRenderer;
        }

        public NodeStatus MoveTo(System.Numerics.Vector3 position)
        {
            var status = _inner.MoveTo(position);

            if (status == NodeStatus.Running)
            {
                if (_robotRenderer != null) _robotRenderer.material.color = _navColor;
            }

            return status;
        }

        public void Stop()
        {
            _inner.Stop();
        }
    }

    /// <summary>
    /// Decorador visual para el servicio de manipulación.
    /// Delega la lógica al servicio base y añade feedback visual en Unity.
    /// </summary>
    public class VisualManipulationDecorator : IManipulationService
    {
        private readonly IManipulationService _inner;
        private readonly Renderer _robotRenderer;
        private readonly Color _manipColor = Color.green;

        public VisualManipulationDecorator(IManipulationService inner, Renderer robotRenderer)
        {
            _inner = inner;
            _robotRenderer = robotRenderer;
        }

        public NodeStatus Pick(string objectId)
        {
            var status = _inner.Pick(objectId);

            if (status == NodeStatus.Running)
            {
                if (_robotRenderer != null) _robotRenderer.material.color = _manipColor;
                Debug.Log($"[DEMO] Pickup Activated -> Object: {objectId}");
            }

            return status;
        }

        public NodeStatus Pick(TargetDescriptor target)
        {
            var status = _inner.Pick(target);

            if (status == NodeStatus.Running)
            {
                if (_robotRenderer != null) _robotRenderer.material.color = _manipColor;
                Debug.Log($"[DEMO] Pickup Activated -> Object: {target?.Id}");
            }

            return status;
        }

        public NodeStatus Place(string destinationId)
        {
            return _inner.Place(destinationId);
        }

        public void Stop()
        {
            _inner.Stop();
        }
    }
}
