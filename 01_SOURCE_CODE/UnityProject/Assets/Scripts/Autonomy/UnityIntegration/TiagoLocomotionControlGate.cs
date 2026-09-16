using UnityEngine;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Arbitro minimo para experimentos de locomocion.
    /// No cambia contratos globales: solo decide si el canal diferencial lo manda autonomia o teleop.
    /// </summary>
    public sealed class TiagoLocomotionControlGate : MonoBehaviour
    {
        private const string LogPrefix = "[TiagoLocomotionGate]";

        private static TiagoLocomotionControlGate _activeInstance;
        private static int _lastAppliedFrame = -1;
        private static string _lastAppliedOwner = "None";

        public static bool ManualOverrideActive { get; private set; }
        public static string ActiveControlMode => ManualOverrideActive ? "Manual" : "Autonomous";

        private void Awake()
        {
            if (_activeInstance != null && _activeInstance != this)
            {
                Debug.LogWarning($"{LogPrefix} Multiple gates in scene. Keeping first instance on '{_activeInstance.name}', disabling duplicate on '{name}'.", this);
                enabled = false;
                return;
            }

            _activeInstance = this;
            Debug.Log($"{LogPrefix} Ready | controlMode={ActiveControlMode}", this);
            TiagoExperimentTelemetry.RecordEvent("control_gate_ready", $"controlMode={ActiveControlMode} object={name}");
        }

        private void OnDestroy()
        {
            if (_activeInstance == this)
            {
                _activeInstance = null;
            }
        }

        public static bool SetManualOverride(bool active, string reason)
        {
            if (ManualOverrideActive == active)
            {
                return false;
            }

            string previous = ActiveControlMode;
            ManualOverrideActive = active;
            string current = ActiveControlMode;
            string payload = $"previous={previous} current={current} reason={reason}";

            Debug.Log($"{LogPrefix} Control | {previous} -> {current} | reason={reason}");
            TiagoExperimentTelemetry.RecordEvent("control_mode_changed", payload);
            return true;
        }

        public static bool TryApplyAutonomousCommand(TiagoDifferentialDriveBridge driveBridge, float linearVelocity, float angularVelocity)
        {
            if (ManualOverrideActive)
            {
                return false;
            }

            return TryApplyCommand("Autonomous", driveBridge, linearVelocity, angularVelocity);
        }

        public static bool TryApplyManualCommand(TiagoDifferentialDriveBridge driveBridge, float linearVelocity, float angularVelocity)
        {
            if (!ManualOverrideActive)
            {
                return false;
            }

            return TryApplyCommand("Manual", driveBridge, linearVelocity, angularVelocity);
        }

        public static bool TryStopAutonomous(TiagoDifferentialDriveBridge driveBridge)
        {
            if (ManualOverrideActive || driveBridge == null || !driveBridge.IsValid)
            {
                return false;
            }

            driveBridge.Stop();
            RegisterAppliedOwner("AutonomousStop");
            return true;
        }

        public static bool TryStopManual(TiagoDifferentialDriveBridge driveBridge)
        {
            if (driveBridge == null || !driveBridge.IsValid)
            {
                return false;
            }

            driveBridge.Stop();
            RegisterAppliedOwner("ManualStop");
            return true;
        }

        private static bool TryApplyCommand(string owner, TiagoDifferentialDriveBridge driveBridge, float linearVelocity, float angularVelocity)
        {
            if (driveBridge == null || !driveBridge.IsValid)
            {
                return false;
            }

            if (_lastAppliedFrame == Time.frameCount && _lastAppliedOwner != owner)
            {
                string payload = $"existingOwner={_lastAppliedOwner} rejectedOwner={owner} frame={Time.frameCount}";
                Debug.LogWarning($"{LogPrefix} Ownership conflict blocked | {payload}");
                TiagoExperimentTelemetry.RecordEvent("drive_ownership_conflict_blocked", payload);
                return false;
            }

            driveBridge.ApplyCommand(linearVelocity, angularVelocity);
            RegisterAppliedOwner(owner);
            return true;
        }

        private static void RegisterAppliedOwner(string owner)
        {
            _lastAppliedFrame = Time.frameCount;
            _lastAppliedOwner = owner;
        }
    }
}
