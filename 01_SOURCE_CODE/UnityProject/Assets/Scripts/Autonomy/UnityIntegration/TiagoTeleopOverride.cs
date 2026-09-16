using UnityEngine;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Control manual experimental para conducir el TIAGo por el mismo bridge diferencial que la autonomia.
    /// Se activa temporalmente y bloquea la escritura autonoma mediante TiagoLocomotionControlGate.
    /// </summary>
    public sealed class TiagoTeleopOverride : MonoBehaviour
    {
        private const string LogPrefix = "[TiagoTeleopOverride]";

        [Header("Override")]
        [SerializeField] private bool _manualOverrideActive;
        [SerializeField] private KeyCode _toggleKey = KeyCode.M;

        [Header("Manual / Teleop Drive Profile")]
        [Tooltip("Manual-only profile. Applies teleop speed, acceleration, slow, and boost values; it does not affect autonomous navigation.")]
        [InspectorName("Manual Drive Profile")]
        [SerializeField] private TiagoDriveProfile _driveProfile = TiagoDriveProfile.Arcade;
        [Tooltip("Applies the manual profile to Linear Speed, Angular Speed, Acceleration, Slow Multiplier, and Boost Multiplier during Awake.")]
        [SerializeField] private bool _applyProfileOnAwake = true;
        [Tooltip("Keeps manual speed fields synchronized when Manual Drive Profile is changed at runtime in the Inspector.")]
        [SerializeField] private bool _detectProfileChangesAtRuntime = true;

        [Header("Manual Input")]
        [SerializeField] private KeyCode _forwardKey = KeyCode.W;
        [SerializeField] private KeyCode _backwardKey = KeyCode.S;
        [SerializeField] private KeyCode _turnLeftKey = KeyCode.A;
        [SerializeField] private KeyCode _turnRightKey = KeyCode.D;
        [SerializeField] private KeyCode _slowKey = KeyCode.LeftControl;
        [SerializeField] private KeyCode _boostKey = KeyCode.LeftShift;
        [Tooltip("Manual linear speed. Overwritten by Manual Drive Profile when profile application is enabled.")]
        [SerializeField] private float _linearSpeed = 0.35f;
        [Tooltip("Manual angular speed. Overwritten by Manual Drive Profile when profile application is enabled.")]
        [SerializeField] private float _angularSpeed = 1.0f;
        [Tooltip("Manual command acceleration. Overwritten by Manual Drive Profile when profile application is enabled.")]
        [SerializeField] private float _acceleration = 10f;
        [Tooltip("Manual slow-key multiplier. Overwritten by Manual Drive Profile when profile application is enabled.")]
        [SerializeField] private float _slowMultiplier = 0.4f;
        [Tooltip("Manual boost-key multiplier. Overwritten by Manual Drive Profile when profile application is enabled.")]
        [SerializeField] private float _boostMultiplier = 1.4f;
        [SerializeField] private bool _scaleAccelerationWithSpeed = true;

        [Header("Robot Differential Drive")]
        [SerializeField] private Transform _navigationReference;
        [SerializeField] private Transform _targetReference;
        [SerializeField] private ArticulationBody _wheelLeft;
        [SerializeField] private ArticulationBody _wheelRight;
        [SerializeField] private float _wheelRadius = 0.098f;
        [SerializeField] private float _wheelSeparation = 0.404f;
        [SerializeField] private float _wheelForceLimit = 2e6f;
        [SerializeField] private float _wheelDriveStiffness = 0f;
        [SerializeField] private float _wheelDriveDamping = 10000f;
        [SerializeField] private int _leftWheelSign = -1;
        [SerializeField] private int _rightWheelSign = 1;
        [SerializeField] private float _linearVelocityCommandScale = 1f;
        [SerializeField] private float _angularVelocityCommandScale = 1f;
        [SerializeField] private float _minWheelTargetDegS = 0f;
        [SerializeField] private float _maxWheelTargetDegS = 0f;

        private static TiagoTeleopOverride _activeInstance;

        private TiagoDifferentialDriveBridge _driveBridge;
        private bool _lastManualOverrideActive;
        private TiagoDriveProfile _lastAppliedProfile;
        private float _currentLinearCommand;
        private float _currentAngularCommand;

        public static bool HasActiveTarget =>
            _activeInstance != null &&
            _activeInstance._manualOverrideActive &&
            _activeInstance._targetReference != null;

        public static Vector3 ActiveTargetPosition =>
            _activeInstance != null && _activeInstance._targetReference != null
                ? _activeInstance._targetReference.position
                : Vector3.zero;

        private void Awake()
        {
            if (_applyProfileOnAwake && !TiagoDriveProfileSettings.IsCustomProfile(_driveProfile))
            {
                ApplyDriveProfile(_driveProfile, "awake");
            }

            _activeInstance = this;
            _lastAppliedProfile = _driveProfile;
            Transform navigationReference = _navigationReference != null ? _navigationReference : transform;
            _navigationReference = navigationReference;
            ValidateConfiguration();
            _driveBridge = new TiagoDifferentialDriveBridge(
                _wheelLeft,
                _wheelRight,
                _wheelRadius,
                _wheelSeparation,
                _leftWheelSign,
                _rightWheelSign,
                _wheelForceLimit,
                _linearVelocityCommandScale,
                _angularVelocityCommandScale,
                _minWheelTargetDegS,
                _maxWheelTargetDegS,
                _wheelDriveStiffness,
                _wheelDriveDamping);

            if (!_driveBridge.IsValid)
            {
                Debug.LogWarning($"{LogPrefix} Invalid wheel configuration. Teleop will not apply wheel commands.", this);
                _manualOverrideActive = false;
                return;
            }

            _driveBridge.Initialize();
            _lastManualOverrideActive = !_manualOverrideActive;
            SyncManualOverride("awake");
        }

        private void Update()
        {
            if (RuntimeHotkeyInput.GetKeyDown(_toggleKey))
            {
                _manualOverrideActive = !_manualOverrideActive;
                SyncManualOverride($"toggle_key={_toggleKey}");
            }
            else if (_manualOverrideActive != _lastManualOverrideActive)
            {
                SyncManualOverride("inspector_changed");
            }

            CheckProfileChangeRuntime();

            if (!_manualOverrideActive || _driveBridge == null || !_driveBridge.IsValid)
            {
                return;
            }

            float multiplier = 1f;

            if (RuntimeHotkeyInput.GetKey(_slowKey))
            {
                multiplier *= Mathf.Clamp(_slowMultiplier, 0.05f, 1f);
            }

            if (RuntimeHotkeyInput.GetKey(_boostKey))
            {
                multiplier *= Mathf.Max(1f, _boostMultiplier);
            }

            float linearTarget = ReadAxis(_forwardKey, _backwardKey) * _linearSpeed * multiplier;
            float angularTarget = ReadAxis(_turnLeftKey, _turnRightKey) * _angularSpeed * multiplier;
            float effectiveAcceleration = _scaleAccelerationWithSpeed
                ? Mathf.Max(0.01f, _acceleration) * Mathf.Abs(multiplier)
                : Mathf.Max(0.01f, _acceleration);
            float step = effectiveAcceleration * Time.deltaTime;

            _currentLinearCommand = Mathf.MoveTowards(_currentLinearCommand, linearTarget, step);
            _currentAngularCommand = Mathf.MoveTowards(_currentAngularCommand, angularTarget, step);

            bool applied = TiagoLocomotionControlGate.TryApplyManualCommand(_driveBridge, _currentLinearCommand, _currentAngularCommand);
            PublishTelemetry(_currentLinearCommand, _currentAngularCommand, commandApplied: applied);
        }

        private void OnDisable()
        {
            bool wasManual = _manualOverrideActive;
            if (_manualOverrideActive)
            {
                _manualOverrideActive = false;
                SyncManualOverride("teleop_disabled");
            }

            if (wasManual)
            {
                TiagoLocomotionControlGate.TryStopManual(_driveBridge);
            }
        }

        private void OnDestroy()
        {
            if (_activeInstance == this)
            {
                _activeInstance = null;
            }

            if (_manualOverrideActive)
            {
                TiagoLocomotionControlGate.SetManualOverride(false, "teleop_destroyed");
            }
        }

        private void SyncManualOverride(string reason)
        {
            _lastManualOverrideActive = _manualOverrideActive;
            bool changed = TiagoLocomotionControlGate.SetManualOverride(_manualOverrideActive, reason);

            if (_manualOverrideActive)
            {
                Debug.Log($"{LogPrefix} Manual override enabled | keys={_forwardKey}/{_backwardKey}/{_turnLeftKey}/{_turnRightKey}", this);
                PublishTelemetry(0f, 0f, commandApplied: false);
                return;
            }

            if (changed)
            {
                TiagoLocomotionControlGate.TryStopManual(_driveBridge);
            }

            _currentLinearCommand = 0f;
            _currentAngularCommand = 0f;
            PublishTelemetry(0f, 0f, commandApplied: changed);
        }

        private void CheckProfileChangeRuntime()
        {
            if (!_detectProfileChangesAtRuntime || _driveProfile == _lastAppliedProfile)
            {
                return;
            }

            if (!TiagoDriveProfileSettings.IsCustomProfile(_driveProfile))
            {
                ApplyDriveProfile(_driveProfile, "runtime_changed");
            }

            _lastAppliedProfile = _driveProfile;
        }

        private void ApplyDriveProfile(TiagoDriveProfile profile, string reason)
        {
            if (!TiagoDriveProfileSettings.IsCustomProfile(profile))
            {
                TiagoDriveProfile publicProfile = TiagoDriveProfileSettings.NormalizePublicProfile(profile);
                TiagoDriveProfileSettings settings = TiagoDriveProfileSettings.Resolve(publicProfile);
                _linearSpeed = settings.MaxLinearSpeed;
                _angularSpeed = settings.MaxAngularSpeed;
                _acceleration = settings.Acceleration;
                _slowMultiplier = 0.5f;
                _boostMultiplier = publicProfile == TiagoDriveProfile.Conservative ? 1.6f : (publicProfile == TiagoDriveProfile.Realistic ? 2.0f : 2.2f);
            }

            string payload = $"profile={profile} reason={reason} linear={_linearSpeed:F2} angular={_angularSpeed:F2} accel={_acceleration:F1} slow={_slowMultiplier:F2} boost={_boostMultiplier:F2}";
            Debug.Log($"{LogPrefix} Drive profile | {payload}", this);
            TiagoExperimentTelemetry.RecordEvent("manual_drive_profile_applied", payload);
        }

        private void ValidateConfiguration()
        {
            if (_wheelLeft == null)
            {
                Debug.LogWarning($"{LogPrefix} Missing left wheel ArticulationBody.", this);
            }

            if (_wheelRight == null)
            {
                Debug.LogWarning($"{LogPrefix} Missing right wheel ArticulationBody.", this);
            }

            if (_navigationReference == null)
            {
                Debug.LogWarning($"{LogPrefix} Missing navigation reference. Falling back to this transform for manual distance telemetry.", this);
            }

            if (_wheelRadius <= 0f || _wheelSeparation <= 0f)
            {
                Debug.LogWarning($"{LogPrefix} Invalid wheel geometry radius={_wheelRadius:F3} separation={_wheelSeparation:F3}.", this);
            }

            if (_linearSpeed <= 0f || _angularSpeed <= 0f || _acceleration <= 0f)
            {
                Debug.LogWarning($"{LogPrefix} Invalid drive profile values linear={_linearSpeed:F2} angular={_angularSpeed:F2} accel={_acceleration:F2}.", this);
            }
        }

        private void PublishTelemetry(float linear, float angular, bool commandApplied)
        {
            Vector3 target = _targetReference != null ? _targetReference.position : Vector3.zero;
            bool hasTarget = _targetReference != null;
            float remaining = hasTarget && _navigationReference != null
                ? Vector3.Distance(Flatten(_navigationReference.position), Flatten(target))
                : float.NaN;

            TiagoExperimentTelemetry.PublishCommand(
                "Manual",
                commandApplied,
                target,
                hasTarget,
                remaining,
                linear,
                angular,
                _driveBridge != null ? _driveBridge.LastDiagnostics : default,
                "ManualOverride",
                activeCorner: false,
                obstacleFront: 0f);
        }

        private static float ReadAxis(KeyCode positiveKey, KeyCode negativeKey)
        {
            float value = 0f;

            if (RuntimeHotkeyInput.GetKey(positiveKey))
            {
                value += 1f;
            }

            if (RuntimeHotkeyInput.GetKey(negativeKey))
            {
                value -= 1f;
            }

            return Mathf.Clamp(value, -1f, 1f);
        }

        private static Vector3 Flatten(Vector3 value)
        {
            value.y = 0f;
            return value;
        }
    }
}
