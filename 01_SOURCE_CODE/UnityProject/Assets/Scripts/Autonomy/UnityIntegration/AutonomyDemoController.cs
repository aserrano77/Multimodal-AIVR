using UnityEngine;
using Autonomy.Domain;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Controlador de interacción para la demo controlada del módulo autónomo.
    /// Gestiona la entrada de usuario (Espacio) para disparar el flujo end-to-end.
    /// </summary>
    public class AutonomyDemoController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private AutonomousRobotAdapter _robotAdapter;
        [SerializeField] private Transform _targetVisual;
        [SerializeField] private bool _enableLegacyTargetVisualSync = true;

        [Header("Configuration")]
        [SerializeField] private string _targetId = "demo_box";
        [SerializeField] private Vector3 _targetPosition = new Vector3(2, 0, 2);

        private void Update()
        {
            if (RuntimeHotkeyInput.GetKeyDown(KeyCode.Space))
            {
                RunDemoCycle();
            }
        }

        private void RunDemoCycle()
        {
            if (_robotAdapter == null)
            {
                Debug.LogError("[DEMO] RobotAdapter reference is missing!");
                return;
            }

            Debug.Log("[DEMO] Space pressed. Starting demo cycle...");

            // 1. Posicionar la esfera visual
            if (_targetVisual != null && _enableLegacyTargetVisualSync)
            {
                _targetVisual.position = _targetPosition;
                Debug.Log($"[DEMO] Legacy target visual moved -> position={_targetPosition} object={_targetVisual.name}", this);
            }

            // 2. Sembrar el objetivo en el blackboard
            var descriptor = new TargetDescriptor(
                _targetId,
                new System.Numerics.Vector3(_targetPosition.x, _targetPosition.y, _targetPosition.z)
            );

            _robotAdapter.TargetSeeder.SeedTarget(descriptor);
            Debug.Log($"[DEMO] Target Seeded: {_targetId} at {_targetPosition}");

            // 3. Activar el modo autónomo para que el BT empiece a trabajar
            _robotAdapter.TrySetAutonomousMode();
        }
    }
}
