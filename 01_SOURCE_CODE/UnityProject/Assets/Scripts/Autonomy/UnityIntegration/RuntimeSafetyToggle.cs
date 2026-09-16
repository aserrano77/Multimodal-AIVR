using Autonomy.Services;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Instrumentación de runtime para alternar el estado de seguridad durante Play Mode.
    /// Se mantiene fuera del dominio y solo actúa sobre el stub expuesto por el adapter.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RuntimeSafetyToggle : MonoBehaviour
    {
        private const string LogPrefix = "[RuntimeSafetyToggle]";

        [Header("References")]
        [SerializeField] private AutonomousRobotAdapter _robotAdapter;

        [Header("Input")]
        [SerializeField] private KeyCode _toggleKey = KeyCode.Y;

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
            if (RuntimeHotkeyInput.GetKeyDown(_toggleKey))
            {
                ToggleSafety();
            }
        }

        public void ToggleSafety()
        {
            if (_robotAdapter == null)
            {
                Debug.LogError($"{LogPrefix} Missing {nameof(AutonomousRobotAdapter)} reference. Toggle skipped.", this);
                return;
            }

            SafetyServiceStub safetyService = _robotAdapter.SafetyServiceStub;
            if (safetyService == null)
            {
                Debug.LogError($"{LogPrefix} Safety service stub is not available. Toggle skipped.", this);
                return;
            }

            safetyService.IsSafe = !safetyService.IsSafe;
            Debug.Log($"{LogPrefix} IsSafeToOperate = {safetyService.IsSafe}", this);
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
    }
}
