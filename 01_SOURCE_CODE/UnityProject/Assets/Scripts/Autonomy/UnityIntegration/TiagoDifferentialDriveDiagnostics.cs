using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Standalone differential-drive diagnostic runner. It bypasses NavMesh, BT and path tracking,
    /// and applies open-loop commands directly to the wheel bridge for physical validation.
    /// </summary>
    public sealed class TiagoDifferentialDriveDiagnostics : MonoBehaviour
    {
        private enum DriveDiagnosticTest
        {
            None,
            StraightForward,
            RotateInPlace,
            RotateInPlaceSweep,
            DriveDampingSweep,
            StraightReverse,
            ReverseArc
        }

        private const string LogPrefix = "[DriveDiagnostic]";
        private const string BuildStampVersion = "DriveDiagnosticsSignAxisDebug";
        private const float SampleLogIntervalSeconds = 0.25f;
        private const float WheelAsymmetryRatioTolerance = 0.35f;
        private const float UnderperformanceRatio = 0.45f;
        private const float LinearWarningRatio = 0.50f;
        private const float RotationUnderperformanceRatio = 0.40f;
        private const float RotationUnderperformanceYawDeg = 20f;
        private const float RotationDriftWarningMeters = 0.15f;
        private const float LowWheelFollowRatio = 0.20f;
        private const float NoMotionDisplacementMeters = 0.03f;
        private const float ProbeWheelTargetDegPerSecond = 180f;
        private const float RecommendationEpsilon = 0.001f;

        [Header("Robot References")]
        [SerializeField] private Transform _robotReference;
        [SerializeField] private ArticulationBody _wheelLeft;
        [SerializeField] private ArticulationBody _wheelRight;

        [Header("Bridge Parameters")]
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

        [Header("Debug Keys")]
        public bool RunDiagnosticsOnPlay = true;
        public bool DisableAutonomousAdapterDuringDiagnostics = true;
        [SerializeField] private bool _runSignAxisProbe = true;
        [SerializeField] private bool _runRotateInPlaceSweep = true;
        [SerializeField] private bool _runDriveDampingSweep = false;
        [SerializeField] private bool _enableKeyboardShortcuts = true;
        [SerializeField] private KeyCode _straightForwardKey = KeyCode.Alpha1;
        [SerializeField] private KeyCode _rotateInPlaceKey = KeyCode.Alpha2;
        [SerializeField] private KeyCode _straightReverseKey = KeyCode.Alpha3;
        [SerializeField] private KeyCode _reverseArcKey = KeyCode.Alpha4;
        [SerializeField] private KeyCode _stopKey = KeyCode.Alpha0;

        private TiagoDifferentialDriveBridge _driveBridge;
        private Coroutine _runningSequence;
        private readonly List<AutonomousRobotAdapter> _disabledAdapters = new();
        private DriveDiagnosticTest _activeTest = DriveDiagnosticTest.None;
        private float _activeLinearCommand;
        private float _activeAngularCommand;
        private float _activeDurationSeconds;
        private float _activeStartedAt;
        private float _nextSampleLogAt;
        private Vector3 _startPosition;
        private Vector3 _startForward;
        private Vector3 _startRight;
        private float _startYawDeg;
        private Vector3 _previousPosition;
        private float _previousYawDeg;
        private float _previousTime;
        private float _sumObservedV;
        private float _sumObservedW;
        private float _sumObservedVForwardCurrent;
        private float _sumObservedVFromArticulationBody;
        private float _sumObservedWFromArticulationBody;
        private float _sumLeftJointVelocityDegS;
        private float _sumRightJointVelocityDegS;
        private int _sampleCount;
        private int _articulationBodySampleCount;
        private float _maxSampleDisplacement;
        private float _maxSampleAbsYawDeltaDeg;
        private float _maxAbsWheelJointVelocityDegS;
        private TiagoDifferentialDriveBridge.DriveDiagnostics _lastDiagnostics;
        private ArticulationBody _robotReferenceArticulationBody;
        private bool _loggerMissingWarningIssued;

        private void Awake()
        {
            Transform reference = _robotReference != null ? _robotReference : transform;
            _robotReference = reference;
            _robotReferenceArticulationBody = _robotReference != null ? _robotReference.GetComponent<ArticulationBody>() : null;
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

            if (_driveBridge.IsValid)
            {
                _driveBridge.Initialize();
            }

            LogReady();
            LogConfigurationErrors();
            LogBridgeConfiguration();
        }

        private void Start()
        {
            LogReady();
            LogBuildStamp();
            LogConfigurationErrors();
            if (RunDiagnosticsOnPlay)
            {
                _runningSequence = StartCoroutine(RunAutomaticSequence());
            }
        }

        private void FixedUpdate()
        {
            if (_activeTest == DriveDiagnosticTest.None)
            {
                return;
            }

            ApplyActiveCommandAndSample();
            if (Time.time - _activeStartedAt >= _activeDurationSeconds)
            {
                CompleteActiveTest();
            }
        }

        private void Update()
        {
            if (!_enableKeyboardShortcuts)
            {
                return;
            }

            if (RuntimeHotkeyInput.GetKeyDown(_straightForwardKey))
            {
                StartStraightForward();
            }
            else if (RuntimeHotkeyInput.GetKeyDown(_rotateInPlaceKey))
            {
                StartRotateInPlace();
            }
            else if (RuntimeHotkeyInput.GetKeyDown(_straightReverseKey))
            {
                StartStraightReverse();
            }
            else if (RuntimeHotkeyInput.GetKeyDown(_reverseArcKey))
            {
                StartReverseArc();
            }
            else if (RuntimeHotkeyInput.GetKeyDown(_stopKey))
            {
                StopDiagnostic();
            }
        }

        [ContextMenu("Drive Diagnostic/Straight Forward")]
        public void StartStraightForward()
        {
            StartTest(DriveDiagnosticTest.StraightForward, 0.30f, 0f, 5f);
        }

        [ContextMenu("Drive Diagnostic/Rotate In Place")]
        public void StartRotateInPlace()
        {
            StartTest(DriveDiagnosticTest.RotateInPlace, 0f, 0.50f, 5f);
        }

        [ContextMenu("Drive Diagnostic/Rotate In Place Sweep")]
        public void StartRotateInPlaceSweep()
        {
            if (_runningSequence != null)
            {
                StopCoroutine(_runningSequence);
                _runningSequence = null;
                _driveBridge?.Stop();
            }

            DisableAutonomousAdaptersIfRequested();
            _runningSequence = StartCoroutine(RunRotateInPlaceSweepStandalone());
        }

        [ContextMenu("Drive Diagnostic/Drive Damping Sweep")]
        public void StartDriveDampingSweep()
        {
            if (_runningSequence != null)
            {
                StopCoroutine(_runningSequence);
                _runningSequence = null;
                _driveBridge?.Stop();
            }

            DisableAutonomousAdaptersIfRequested();
            _runningSequence = StartCoroutine(RunDriveDampingSweepStandalone());
        }

        [ContextMenu("Drive Diagnostic/Straight Reverse")]
        public void StartStraightReverse()
        {
            StartTest(DriveDiagnosticTest.StraightReverse, -0.20f, 0f, 3f);
        }

        [ContextMenu("Drive Diagnostic/Reverse Arc")]
        public void StartReverseArc()
        {
            StartTest(DriveDiagnosticTest.ReverseArc, -0.25f, 0.60f, 2f);
        }

        [ContextMenu("Drive Diagnostic/Stop")]
        public void StopDiagnostic()
        {
            if (_runningSequence != null)
            {
                StopCoroutine(_runningSequence);
                _runningSequence = null;
            }

            _activeTest = DriveDiagnosticTest.None;
            _driveBridge?.Stop();
            RestoreDisabledAdapters();
            Debug.Log($"{LogPrefix} stop | reason=user_requested");
            EmitDiagnosticEvent("drive_diagnostic_stop", new Dictionary<string, object> { ["reason"] = "user_requested" });
        }

        private void StartTest(DriveDiagnosticTest test, float linearCommand, float angularCommand, float durationSeconds)
        {
            if (_driveBridge == null || !_driveBridge.IsValid || _robotReference == null)
            {
                Debug.LogWarning($"{LogPrefix} start_failed | test={test} reason=invalid_references");
                EmitDiagnosticEvent("drive_diagnostic_config_error", new Dictionary<string, object> { ["test"] = test.ToString(), ["reason"] = "invalid_references" });
                return;
            }

            if (_runningSequence != null)
            {
                StopCoroutine(_runningSequence);
                _runningSequence = null;
                _driveBridge.Stop();
            }

            DisableAutonomousAdaptersIfRequested();
            BeginTest(test, linearCommand, angularCommand, durationSeconds);
        }

        private IEnumerator RunAutomaticSequence()
        {
            DisableAutonomousAdaptersIfRequested();
            yield return new WaitForSeconds(1f);
            yield return RunTestAndWait(DriveDiagnosticTest.StraightForward, 0.30f, 0f, 5f);
            yield return new WaitForSeconds(1f);
            yield return RunTestAndWait(DriveDiagnosticTest.RotateInPlace, 0f, 0.50f, 5f);
            if (_runRotateInPlaceSweep)
            {
                yield return new WaitForSeconds(1f);
                yield return RunRotateInPlaceSweep();
            }

            yield return new WaitForSeconds(1f);
            yield return RunTestAndWait(DriveDiagnosticTest.StraightReverse, -0.20f, 0f, 3f);
            yield return new WaitForSeconds(1f);
            yield return RunTestAndWait(DriveDiagnosticTest.ReverseArc, -0.25f, 0.60f, 2f);
            if (_runDriveDampingSweep)
            {
                yield return new WaitForSeconds(1f);
                yield return RunDriveDampingSweep();
            }

            if (_runSignAxisProbe)
            {
                yield return new WaitForSeconds(1f);
                yield return RunSignAxisProbe();
            }

            _driveBridge?.Stop();
            _runningSequence = null;
            RestoreDisabledAdapters();
        }

        private IEnumerator RunTestAndWait(DriveDiagnosticTest test, float linearCommand, float angularCommand, float durationSeconds)
        {
            BeginTest(test, linearCommand, angularCommand, durationSeconds);
            while (_activeTest != DriveDiagnosticTest.None)
            {
                yield return null;
            }
        }

        private IEnumerator RunRotateInPlaceSweep()
        {
            float[] angularCommands = { 0.25f, 0.50f, 0.75f, 1.00f, 1.25f };
            Debug.Log($"{LogPrefix} rotate_sweep_start | levels={angularCommands.Length} | duration=3.00");
            EmitDiagnosticEvent(
                "drive_diagnostic_rotate_in_place_sweep_start",
                new Dictionary<string, object>
                {
                    ["test"] = "RotateInPlaceSweep",
                    ["levels"] = angularCommands.Length,
                    ["durationPerLevel"] = 3f,
                    ["angularVelocityCommandScale"] = _angularVelocityCommandScale
                });

            for (int i = 0; i < angularCommands.Length; i++)
            {
                yield return RunTestAndWait(DriveDiagnosticTest.RotateInPlaceSweep, 0f, angularCommands[i], 3f);
                yield return new WaitForSeconds(0.5f);
            }

            _driveBridge?.Stop();
        }

        private IEnumerator RunRotateInPlaceSweepStandalone()
        {
            yield return RunRotateInPlaceSweep();
            _runningSequence = null;
            RestoreDisabledAdapters();
        }

        private IEnumerator RunDriveDampingSweep()
        {
            float originalDamping = _wheelDriveDamping;
            float[] dampingValues = { 0f, 1000f, 5000f, 10000f, 20000f };
            Debug.Log($"{LogPrefix} damping_sweep_start | levels={dampingValues.Length} | wCmd=0.50 | duration=3.00");
            EmitDiagnosticEvent(
                "drive_diagnostic_damping_sweep_start",
                new Dictionary<string, object>
                {
                    ["test"] = "DriveDampingSweep",
                    ["levels"] = dampingValues.Length,
                    ["wCmd"] = 0.5f,
                    ["durationPerLevel"] = 3f,
                    ["wheelDriveStiffness"] = _wheelDriveStiffness,
                    ["wheelForceLimit"] = _wheelForceLimit
                });

            for (int i = 0; i < dampingValues.Length; i++)
            {
                _wheelDriveDamping = dampingValues[i];
                _driveBridge.ConfigureWheelDrive(_wheelDriveStiffness, _wheelDriveDamping, _wheelForceLimit);
                yield return RunTestAndWait(DriveDiagnosticTest.DriveDampingSweep, 0f, 0.5f, 3f);
                yield return new WaitForSeconds(0.5f);
            }

            _wheelDriveDamping = originalDamping;
            _driveBridge.ConfigureWheelDrive(_wheelDriveStiffness, _wheelDriveDamping, _wheelForceLimit);
            _driveBridge.Stop();
        }

        private IEnumerator RunDriveDampingSweepStandalone()
        {
            yield return RunDriveDampingSweep();
            _runningSequence = null;
            RestoreDisabledAdapters();
        }

        private void BeginTest(DriveDiagnosticTest test, float linearCommand, float angularCommand, float durationSeconds)
        {
            if (_driveBridge == null || !_driveBridge.IsValid || _robotReference == null)
            {
                Debug.LogWarning($"{LogPrefix} start_failed | test={test} reason=invalid_references");
                EmitDiagnosticEvent("drive_diagnostic_config_error", new Dictionary<string, object> { ["test"] = test.ToString(), ["reason"] = "invalid_references" });
                return;
            }

            _activeTest = test;
            _activeLinearCommand = linearCommand;
            _activeAngularCommand = angularCommand;
            _activeDurationSeconds = durationSeconds;
            _activeStartedAt = Time.time;
            _startPosition = Flatten(_robotReference.position);
            _startYawDeg = _robotReference.eulerAngles.y;
            _startForward = Flatten(_robotReference.forward).sqrMagnitude > 0.0001f
                ? Flatten(_robotReference.forward).normalized
                : Vector3.forward;
            _startRight = Vector3.Cross(Vector3.up, _startForward).normalized;
            _previousPosition = _startPosition;
            _previousYawDeg = _startYawDeg;
            _previousTime = Time.time;
            _nextSampleLogAt = Time.time;
            _sumObservedV = 0f;
            _sumObservedW = 0f;
            _sumObservedVForwardCurrent = 0f;
            _sumObservedVFromArticulationBody = 0f;
            _sumObservedWFromArticulationBody = 0f;
            _sumLeftJointVelocityDegS = 0f;
            _sumRightJointVelocityDegS = 0f;
            _sampleCount = 0;
            _articulationBodySampleCount = 0;
            _maxSampleDisplacement = 0f;
            _maxSampleAbsYawDeltaDeg = 0f;
            _maxAbsWheelJointVelocityDegS = 0f;
            _lastDiagnostics = default;
            _robotReferenceArticulationBody = _robotReference.GetComponent<ArticulationBody>();

            Debug.Log($"{LogPrefix} start | test={test} | vCmd={linearCommand:F2} | wCmd={angularCommand:F2} | duration={durationSeconds:F2} | robotReference={_robotReference.name} | startPosition={_robotReference.position} | startYaw={_startYawDeg:F1}");
            EmitDiagnosticEvent(
                "drive_diagnostic_start",
                new Dictionary<string, object>
                {
                    ["test"] = test.ToString(),
                    ["vCmd"] = linearCommand,
                    ["wCmd"] = angularCommand,
                    ["duration"] = durationSeconds,
                    ["robotReference"] = _robotReference.name,
                    ["robotReferenceHasArticulationBody"] = _robotReferenceArticulationBody != null,
                    ["startPosition"] = _robotReference.position,
                    ["startRotationEuler"] = _robotReference.rotation.eulerAngles,
                    ["startYawDeg"] = _startYawDeg,
                    ["time"] = Time.time,
                    ["fixedTime"] = Time.fixedTime
                });
        }

        private void ApplyActiveCommandAndSample()
        {
            _driveBridge.ApplyCommand(_activeLinearCommand, _activeAngularCommand);
            _lastDiagnostics = _driveBridge.LastDiagnostics;
            Debug.Log($"{LogPrefix} apply_wheel_command | test={_activeTest} | vCmd={_activeLinearCommand:F2} | wCmd={_activeAngularCommand:F2} | leftTargetDegS={_lastDiagnostics.LeftTargetDegPerSec:F1} | rightTargetDegS={_lastDiagnostics.RightTargetDegPerSec:F1}");
            EmitDiagnosticEvent(
                "drive_diagnostic_apply_wheel_command",
                new Dictionary<string, object>
                {
                    ["test"] = _activeTest.ToString(),
                    ["vCmd"] = _activeLinearCommand,
                    ["wCmd"] = _activeAngularCommand,
                    ["effectiveVCmd"] = _lastDiagnostics.EffectiveCommandLinear,
                    ["effectiveWCmd"] = _lastDiagnostics.EffectiveCommandAngular,
                    ["leftTargetDegS"] = _lastDiagnostics.LeftTargetDegPerSec,
                    ["rightTargetDegS"] = _lastDiagnostics.RightTargetDegPerSec,
                    ["leftJointVelocity"] = _lastDiagnostics.LeftJointVelocityDegPerSec * Mathf.Deg2Rad,
                    ["rightJointVelocity"] = _lastDiagnostics.RightJointVelocityDegPerSec * Mathf.Deg2Rad,
                    ["leftJointVelocityDegS"] = _lastDiagnostics.LeftJointVelocityDegPerSec,
                    ["rightJointVelocityDegS"] = _lastDiagnostics.RightJointVelocityDegPerSec,
                    ["leftDriveStiffness"] = _lastDiagnostics.LeftDriveStiffness,
                    ["rightDriveStiffness"] = _lastDiagnostics.RightDriveStiffness,
                    ["leftDriveDamping"] = _lastDiagnostics.LeftDriveDamping,
                    ["rightDriveDamping"] = _lastDiagnostics.RightDriveDamping,
                    ["leftForceLimit"] = _lastDiagnostics.LeftForceLimit,
                    ["rightForceLimit"] = _lastDiagnostics.RightForceLimit
                });

            float now = Time.time;
            float dt = Mathf.Max(0.0001f, now - _previousTime);
            Vector3 positionWorld = _robotReference.position;
            Vector3 position = Flatten(positionWorld);
            Vector3 rotationEuler = _robotReference.rotation.eulerAngles;
            float yawDeg = rotationEuler.y;
            Vector3 delta = position - _previousPosition;
            Vector3 forwardCurrent = Flatten(_robotReference.forward).sqrMagnitude > 0.0001f
                ? Flatten(_robotReference.forward).normalized
                : _startForward;
            float observedVForwardInitial = Vector3.Dot(delta, _startForward) / dt;
            float observedVForwardCurrent = Vector3.Dot(delta, forwardCurrent) / dt;
            float observedWFromTransform = Mathf.DeltaAngle(_previousYawDeg, yawDeg) * Mathf.Deg2Rad / dt;
            bool hasArticulationBody = _robotReferenceArticulationBody != null;
            Vector3 articulationVelocity = hasArticulationBody ? _robotReferenceArticulationBody.linearVelocity : Vector3.zero;
            Vector3 articulationAngularVelocity = hasArticulationBody ? _robotReferenceArticulationBody.angularVelocity : Vector3.zero;
            float observedVFromArticulationBody = hasArticulationBody ? Vector3.Dot(Flatten(articulationVelocity), _startForward) : 0f;
            float observedWFromArticulationBody = hasArticulationBody ? articulationAngularVelocity.y : 0f;
            float yawDeltaDeg = Mathf.DeltaAngle(_startYawDeg, yawDeg);
            float displacement = Vector3.Distance(_startPosition, position);
            float lateralDrift = Vector3.Dot(position - _startPosition, _startRight);

            _sumObservedV += observedVForwardInitial;
            _sumObservedW += observedWFromTransform;
            _sumObservedVForwardCurrent += observedVForwardCurrent;
            _sumLeftJointVelocityDegS += _lastDiagnostics.LeftJointVelocityDegPerSec;
            _sumRightJointVelocityDegS += _lastDiagnostics.RightJointVelocityDegPerSec;
            _maxSampleDisplacement = Mathf.Max(_maxSampleDisplacement, displacement);
            _maxSampleAbsYawDeltaDeg = Mathf.Max(_maxSampleAbsYawDeltaDeg, Mathf.Abs(yawDeltaDeg));
            _maxAbsWheelJointVelocityDegS = Mathf.Max(
                _maxAbsWheelJointVelocityDegS,
                Mathf.Max(Mathf.Abs(_lastDiagnostics.LeftJointVelocityDegPerSec), Mathf.Abs(_lastDiagnostics.RightJointVelocityDegPerSec)));
            if (hasArticulationBody)
            {
                _sumObservedVFromArticulationBody += observedVFromArticulationBody;
                _sumObservedWFromArticulationBody += observedWFromArticulationBody;
                _articulationBodySampleCount++;
            }

            _sampleCount++;

            if (now >= _nextSampleLogAt)
            {
                Debug.Log($"{LogPrefix} sample | test={_activeTest} | robotReference={_robotReference.name} | pos={positionWorld} | rot={rotationEuler} | hasChanged={_robotReference.hasChanged} | observedVInitial={observedVForwardInitial:F3} | observedVCurrent={observedVForwardCurrent:F3} | observedWTransform={observedWFromTransform:F3} | observedVAb={observedVFromArticulationBody:F3} | observedWAb={observedWFromArticulationBody:F3} | yawDelta={yawDeltaDeg:F1} | displacement={displacement:F2}");
                Dictionary<string, object> samplePayload = BuildMotionPayload(
                    _activeTest.ToString(),
                    _activeLinearCommand,
                    _activeAngularCommand,
                    now - _activeStartedAt,
                    positionWorld,
                    yawDeg,
                    yawDeltaDeg,
                    observedVForwardInitial,
                    observedWFromTransform,
                    displacement,
                    lateralDrift,
                    _lastDiagnostics);
                AddPoseMeasurementPayload(
                    samplePayload,
                    positionWorld,
                    rotationEuler,
                    delta,
                    yawDeg,
                    yawDeltaDeg,
                    displacement,
                    observedVForwardInitial,
                    observedVForwardCurrent,
                    observedWFromTransform,
                    hasArticulationBody,
                    articulationVelocity,
                    articulationAngularVelocity,
                    observedVFromArticulationBody,
                    observedWFromArticulationBody);
                EmitDiagnosticEvent("drive_diagnostic_sample", samplePayload);
                EmitDiagnosticEvent("drive_diagnostic_pose_sample", samplePayload);
                _nextSampleLogAt = now + SampleLogIntervalSeconds;
            }

            _previousPosition = position;
            _previousYawDeg = yawDeg;
            _previousTime = now;
        }

        private void CompleteActiveTest()
        {
            DriveDiagnosticTest completedTest = _activeTest;
            float linearCommand = _activeLinearCommand;
            float angularCommand = _activeAngularCommand;
            float durationSeconds = _activeDurationSeconds;
            _activeTest = DriveDiagnosticTest.None;
            _driveBridge.Stop();
            Vector3 finalPosition = Flatten(_robotReference.position);
            float finalYawDeg = _robotReference.eulerAngles.y;
            float displacement = Vector3.Distance(_startPosition, finalPosition);
            float yawDeltaDeg = Mathf.DeltaAngle(_startYawDeg, finalYawDeg);
            float avgObservedV = _sampleCount > 0 ? _sumObservedV / _sampleCount : 0f;
            float avgObservedW = _sampleCount > 0 ? _sumObservedW / _sampleCount : 0f;
            float avgObservedVForwardCurrent = _sampleCount > 0 ? _sumObservedVForwardCurrent / _sampleCount : 0f;
            float avgObservedVFromArticulationBody = _articulationBodySampleCount > 0 ? _sumObservedVFromArticulationBody / _articulationBodySampleCount : 0f;
            float avgObservedWFromArticulationBody = _articulationBodySampleCount > 0 ? _sumObservedWFromArticulationBody / _articulationBodySampleCount : 0f;
            float avgLeftJointVelocity = _sampleCount > 0 ? _sumLeftJointVelocityDegS / _sampleCount : 0f;
            float avgRightJointVelocity = _sampleCount > 0 ? _sumRightJointVelocityDegS / _sampleCount : 0f;
            float lateralDrift = Vector3.Dot(finalPosition - _startPosition, _startRight);
            string verdict = ResolveVerdict(completedTest, linearCommand, angularCommand, displacement, yawDeltaDeg, avgObservedV, avgObservedW, _lastDiagnostics, out string reason);
            ApplyPoseMeasurementSanityChecks(displacement, yawDeltaDeg, _lastDiagnostics, ref verdict, ref reason);
            float recommendedLinearScale = Mathf.Abs(linearCommand) > 0.05f
                ? Mathf.Abs(linearCommand) / Mathf.Max(Mathf.Abs(avgObservedV), RecommendationEpsilon)
                : _linearVelocityCommandScale;
            float recommendedAngularScale = Mathf.Abs(angularCommand) > 0.05f
                ? Mathf.Abs(angularCommand) / Mathf.Max(Mathf.Abs(avgObservedW), RecommendationEpsilon)
                : _angularVelocityCommandScale;

            string result =
                $"test={completedTest} | duration={durationSeconds:F2} | vCmd={linearCommand:F2} | wCmd={angularCommand:F2} | displacement={displacement:F2} | yawDelta={yawDeltaDeg:F1} | " +
                $"avgObservedV={avgObservedV:F3} | avgObservedW={avgObservedW:F3} | lateralDrift={lateralDrift:F2} | " +
                $"avgLeftJointVelocity={avgLeftJointVelocity:F1} | avgRightJointVelocity={avgRightJointVelocity:F1} | " +
                $"leftFollowRatio={_lastDiagnostics.LeftAbsFollowRatio:F2} | rightFollowRatio={_lastDiagnostics.RightAbsFollowRatio:F2} | verdict={verdict} | reason={reason}";
            Debug.Log($"{LogPrefix} result | {result}");
            Dictionary<string, object> resultPayload = new()
            {
                ["test"] = completedTest.ToString(),
                ["duration"] = durationSeconds,
                ["vCmd"] = linearCommand,
                ["wCmd"] = angularCommand,
                ["effectiveVCmd"] = _lastDiagnostics.EffectiveCommandLinear,
                ["effectiveWCmd"] = _lastDiagnostics.EffectiveCommandAngular,
                ["linearVelocityCommandScale"] = _lastDiagnostics.LinearVelocityCommandScale,
                ["angularVelocityCommandScale"] = _lastDiagnostics.AngularVelocityCommandScale,
                ["displacement"] = displacement,
                ["maxSampleDisplacement"] = _maxSampleDisplacement,
                ["yawDeltaDeg"] = yawDeltaDeg,
                ["maxSampleAbsYawDeltaDeg"] = _maxSampleAbsYawDeltaDeg,
                ["avgObservedV"] = avgObservedV,
                ["avgObservedW"] = avgObservedW,
                ["avgObservedV_forwardInitial"] = avgObservedV,
                ["avgObservedV_forwardCurrent"] = avgObservedVForwardCurrent,
                ["avgObservedV_fromTransform"] = avgObservedV,
                ["avgObservedW_fromTransform"] = avgObservedW,
                ["avgObservedV_fromArticulationBody"] = avgObservedVFromArticulationBody,
                ["avgObservedW_fromArticulationBody"] = avgObservedWFromArticulationBody,
                ["articulationBodySampleCount"] = _articulationBodySampleCount,
                ["robotReference"] = _robotReference.name,
                ["robotReferenceHasArticulationBody"] = _robotReferenceArticulationBody != null,
                ["startPosition"] = _startPosition,
                ["finalPosition"] = finalPosition,
                ["startYawDeg"] = _startYawDeg,
                ["finalYawDeg"] = finalYawDeg,
                ["deltaPosition"] = finalPosition - _startPosition,
                ["lateralDrift"] = lateralDrift,
                ["avgLeftJointVelocity"] = avgLeftJointVelocity,
                ["avgRightJointVelocity"] = avgRightJointVelocity,
                ["leftFollowRatio"] = _lastDiagnostics.LeftAbsFollowRatio,
                ["rightFollowRatio"] = _lastDiagnostics.RightAbsFollowRatio,
                ["leftTargetDegS"] = _lastDiagnostics.LeftTargetDegPerSec,
                ["rightTargetDegS"] = _lastDiagnostics.RightTargetDegPerSec,
                ["leftDriveTargetDegS"] = _lastDiagnostics.LeftDriveTargetDegPerSec,
                ["rightDriveTargetDegS"] = _lastDiagnostics.RightDriveTargetDegPerSec,
                ["recommendedLinearScale"] = recommendedLinearScale,
                ["recommendedAngularScale"] = recommendedAngularScale,
                ["verdict"] = verdict,
                ["reason"] = reason
            };
            AddDriveConfigurationPayload(resultPayload, _lastDiagnostics);
            EmitDiagnosticEvent("drive_diagnostic_result", resultPayload);
            if (completedTest == DriveDiagnosticTest.RotateInPlaceSweep)
            {
                EmitDiagnosticEvent("drive_diagnostic_rotate_in_place_sweep_result", resultPayload);
            }
            else if (completedTest == DriveDiagnosticTest.DriveDampingSweep)
            {
                EmitDiagnosticEvent("drive_diagnostic_damping_sweep_result", resultPayload);
            }
            if (_runningSequence == null)
            {
                RestoreDisabledAdapters();
            }
        }

        private IEnumerator RunSignAxisProbe()
        {
            if (_driveBridge == null || !_driveBridge.IsValid || _robotReference == null)
            {
                Debug.LogWarning($"{LogPrefix} sign_axis_probe_skipped | reason=invalid_references");
                EmitDiagnosticEvent("drive_diagnostic_config_error", new Dictionary<string, object> { ["test"] = "SignAxisProbe", ["reason"] = "invalid_references" });
                yield break;
            }

            yield return RunSignAxisProbePhase("LeftPositive", ProbeWheelTargetDegPerSecond, 0f, 1f);
            yield return StopProbeForSeconds(0.5f);
            yield return RunSignAxisProbePhase("LeftNegative", -ProbeWheelTargetDegPerSecond, 0f, 1f);
            yield return StopProbeForSeconds(0.5f);
            yield return RunSignAxisProbePhase("RightPositive", 0f, ProbeWheelTargetDegPerSecond, 1f);
            yield return StopProbeForSeconds(0.5f);
            yield return RunSignAxisProbePhase("RightNegative", 0f, -ProbeWheelTargetDegPerSecond, 1f);
            _driveBridge.Stop();
        }

        private IEnumerator StopProbeForSeconds(float seconds)
        {
            _driveBridge.Stop();
            yield return new WaitForSeconds(seconds);
        }

        private IEnumerator RunSignAxisProbePhase(string phase, float leftTargetDegS, float rightTargetDegS, float durationSeconds)
        {
            Vector3 startPosition = Flatten(_robotReference.position);
            float startYawDeg = _robotReference.eulerAngles.y;
            Vector3 startForward = Flatten(_robotReference.forward).sqrMagnitude > 0.0001f
                ? Flatten(_robotReference.forward).normalized
                : Vector3.forward;
            Vector3 startRight = Vector3.Cross(Vector3.up, startForward).normalized;
            Vector3 previousPosition = startPosition;
            float previousYawDeg = startYawDeg;
            float previousTime = Time.time;
            float startedAt = Time.time;
            float nextSampleAt = Time.time;
            float sumObservedV = 0f;
            float sumObservedW = 0f;
            float sumLeftJointVelocity = 0f;
            float sumRightJointVelocity = 0f;
            int samples = 0;

            Debug.Log($"{LogPrefix} sign_axis_probe_start | phase={phase} | leftTargetDegS={leftTargetDegS:F1} | rightTargetDegS={rightTargetDegS:F1} | duration={durationSeconds:F2}");
            EmitDiagnosticEvent(
                "drive_diagnostic_start",
                new Dictionary<string, object>
                {
                    ["test"] = "SignAxisProbe",
                    ["phase"] = phase,
                    ["leftTargetDegS"] = leftTargetDegS,
                    ["rightTargetDegS"] = rightTargetDegS,
                    ["duration"] = durationSeconds
                });

            while (Time.time - startedAt < durationSeconds)
            {
                _driveBridge.ApplyRawWheelTargetsDegPerSecond(leftTargetDegS, rightTargetDegS);
                TiagoDifferentialDriveBridge.DriveDiagnostics diagnostics = _driveBridge.LastDiagnostics;
                float now = Time.time;
                float dt = Mathf.Max(0.0001f, now - previousTime);
                Vector3 position = Flatten(_robotReference.position);
                float yawDeg = _robotReference.eulerAngles.y;
                Vector3 delta = position - previousPosition;
                Vector3 forward = Flatten(_robotReference.forward).sqrMagnitude > 0.0001f
                    ? Flatten(_robotReference.forward).normalized
                    : startForward;
                float observedV = Vector3.Dot(delta, forward) / dt;
                float observedW = Mathf.DeltaAngle(previousYawDeg, yawDeg) * Mathf.Deg2Rad / dt;
                float yawDeltaDeg = Mathf.DeltaAngle(startYawDeg, yawDeg);
                float displacement = Vector3.Distance(startPosition, position);
                float lateralDrift = Vector3.Dot(position - startPosition, startRight);

                sumObservedV += observedV;
                sumObservedW += observedW;
                sumLeftJointVelocity += diagnostics.LeftJointVelocityDegPerSec;
                sumRightJointVelocity += diagnostics.RightJointVelocityDegPerSec;
                samples++;

                if (now >= nextSampleAt)
                {
                    Debug.Log($"{LogPrefix} sign_axis_probe_sample | phase={phase} | leftTargetDegS={leftTargetDegS:F1} | rightTargetDegS={rightTargetDegS:F1} | observedV={observedV:F3} | observedW={observedW:F3} | yawDelta={yawDeltaDeg:F1} | displacement={displacement:F2}");
                    Dictionary<string, object> payload = BuildMotionPayload(
                        "SignAxisProbe",
                        0f,
                        0f,
                        now - startedAt,
                        position,
                        yawDeg,
                        yawDeltaDeg,
                        observedV,
                        observedW,
                        displacement,
                        lateralDrift,
                        diagnostics);
                    payload["phase"] = phase;
                    payload["commandedLeftOnly"] = Mathf.Abs(leftTargetDegS) > 0.1f && Mathf.Abs(rightTargetDegS) <= 0.1f;
                    payload["commandedRightOnly"] = Mathf.Abs(rightTargetDegS) > 0.1f && Mathf.Abs(leftTargetDegS) <= 0.1f;
                    EmitDiagnosticEvent("drive_diagnostic_sign_axis_probe_sample", payload);
                    nextSampleAt = now + SampleLogIntervalSeconds;
                }

                previousPosition = position;
                previousYawDeg = yawDeg;
                previousTime = now;
                yield return new WaitForFixedUpdate();
            }

            _driveBridge.Stop();
            Vector3 finalPosition = Flatten(_robotReference.position);
            float finalYawDeg = _robotReference.eulerAngles.y;
            float finalDisplacement = Vector3.Distance(startPosition, finalPosition);
            float finalYawDeltaDeg = Mathf.DeltaAngle(startYawDeg, finalYawDeg);
            float finalLateralDrift = Vector3.Dot(finalPosition - startPosition, startRight);
            float avgObservedV = samples > 0 ? sumObservedV / samples : 0f;
            float avgObservedW = samples > 0 ? sumObservedW / samples : 0f;
            float avgLeftJointVelocity = samples > 0 ? sumLeftJointVelocity / samples : 0f;
            float avgRightJointVelocity = samples > 0 ? sumRightJointVelocity / samples : 0f;
            string forwardContribution = avgObservedV > 0.02f ? "forward" : (avgObservedV < -0.02f ? "reverse" : "none");

            Debug.Log($"{LogPrefix} sign_axis_probe_result | phase={phase} | leftTargetDegS={leftTargetDegS:F1} | rightTargetDegS={rightTargetDegS:F1} | displacement={finalDisplacement:F2} | yawDelta={finalYawDeltaDeg:F1} | avgObservedV={avgObservedV:F3} | avgObservedW={avgObservedW:F3} | forwardContribution={forwardContribution}");
            EmitDiagnosticEvent(
                "drive_diagnostic_sign_axis_probe_result",
                new Dictionary<string, object>
                {
                    ["test"] = "SignAxisProbe",
                    ["phase"] = phase,
                    ["duration"] = durationSeconds,
                    ["leftTargetDegS"] = leftTargetDegS,
                    ["rightTargetDegS"] = rightTargetDegS,
                    ["displacement"] = finalDisplacement,
                    ["yawDeltaDeg"] = finalYawDeltaDeg,
                    ["avgObservedV"] = avgObservedV,
                    ["avgObservedW"] = avgObservedW,
                    ["lateralDrift"] = finalLateralDrift,
                    ["avgLeftJointVelocity"] = avgLeftJointVelocity,
                    ["avgRightJointVelocity"] = avgRightJointVelocity,
                    ["forwardContribution"] = forwardContribution,
                    ["leftWheelPositiveGeneratesForward"] = phase == "LeftPositive" && avgObservedV > 0.02f,
                    ["leftWheelNegativeGeneratesForward"] = phase == "LeftNegative" && avgObservedV > 0.02f,
                    ["rightWheelPositiveGeneratesForward"] = phase == "RightPositive" && avgObservedV > 0.02f,
                    ["rightWheelNegativeGeneratesForward"] = phase == "RightNegative" && avgObservedV > 0.02f
                });
        }

        private string ResolveVerdict(
            DriveDiagnosticTest test,
            float linearCommand,
            float angularCommand,
            float displacement,
            float yawDeltaDeg,
            float avgObservedV,
            float avgObservedW,
            TiagoDifferentialDriveBridge.DriveDiagnostics diagnostics,
            out string reason)
        {
            if (TiagoExperimentLogger.Active == null)
            {
                reason = "logger_not_found";
                return "FAIL_LOGGER_NOT_FOUND";
            }

            bool wheelTargetsNotFollowed = diagnostics.LeftAbsFollowRatio < LowWheelFollowRatio && diagnostics.RightAbsFollowRatio < LowWheelFollowRatio;
            bool isRotationTest = test == DriveDiagnosticTest.RotateInPlace ||
                test == DriveDiagnosticTest.RotateInPlaceSweep ||
                test == DriveDiagnosticTest.DriveDampingSweep;
            float angularReference = Mathf.Abs(diagnostics.EffectiveCommandAngular) > 0.05f
                ? Mathf.Abs(diagnostics.EffectiveCommandAngular)
                : Mathf.Abs(angularCommand);

            if (isRotationTest && Mathf.Abs(angularCommand) > 0.05f)
            {
                if (Mathf.Abs(yawDeltaDeg) < RotationUnderperformanceYawDeg)
                {
                    reason = wheelTargetsNotFollowed ? "wheel_targets_not_followed;yaw_delta_below_threshold" : "yaw_delta_below_threshold";
                    return "FAIL_ROTATION_UNDERPERFORMANCE";
                }

                if (Mathf.Abs(avgObservedW) < angularReference * RotationUnderperformanceRatio)
                {
                    reason = wheelTargetsNotFollowed ? "wheel_targets_not_followed;observed_w_below_40_percent" : "observed_w_below_40_percent";
                    return "FAIL_ROTATION_UNDERPERFORMANCE";
                }

                if (displacement > RotationDriftWarningMeters)
                {
                    reason = "rotation_has_translational_drift";
                    return "WARNING_TRANSLATIONAL_DRIFT";
                }
            }

            if (!isRotationTest && displacement < NoMotionDisplacementMeters && (Mathf.Abs(linearCommand) > 0.05f || Mathf.Abs(angularCommand) > 0.05f))
            {
                reason = "displacement_below_threshold";
                return "FAIL_NO_MOTION";
            }

            if ((test == DriveDiagnosticTest.StraightForward || test == DriveDiagnosticTest.StraightReverse) && Mathf.Abs(yawDeltaDeg) > 5f)
            {
                reason = "yaw_drift_above_5_deg";
                return "FAIL_YAW_DRIFT";
            }

            if (Mathf.Abs(angularCommand) <= 0.01f && Mathf.Abs(avgObservedW) > 0.08f)
            {
                reason = "uncommanded_angular_velocity";
                return "FAIL_UNCOMMANDED_ROTATION";
            }

            float ratioDiff = Mathf.Abs(diagnostics.LeftAbsFollowRatio - diagnostics.RightAbsFollowRatio);
            if (ratioDiff > WheelAsymmetryRatioTolerance)
            {
                reason = "wheel_follow_ratio_asymmetry";
                return "FAIL_WHEEL_ASYMMETRY";
            }

            if (Mathf.Abs(linearCommand) > 0.05f && Mathf.Abs(avgObservedV) < Mathf.Abs(linearCommand) * UnderperformanceRatio)
            {
                reason = "observed_v_below_fail_threshold";
                return "FAIL_DRIVE_UNDERPERFORMANCE";
            }

            if ((test == DriveDiagnosticTest.StraightForward || test == DriveDiagnosticTest.StraightReverse) &&
                Mathf.Abs(linearCommand) > 0.05f &&
                Mathf.Abs(avgObservedV) < Mathf.Abs(linearCommand) * LinearWarningRatio)
            {
                reason = "observed_v_below_warning_threshold";
                return "WARNING_LINEAR_UNDERPERFORMANCE";
            }

            reason = "within_thresholds";
            return "PASS";
        }

        private void ApplyPoseMeasurementSanityChecks(
            float displacement,
            float yawDeltaDeg,
            TiagoDifferentialDriveBridge.DriveDiagnostics diagnostics,
            ref string verdict,
            ref string reason)
        {
            bool wheelsMoved = _maxAbsWheelJointVelocityDegS > 5f ||
                Mathf.Abs(diagnostics.LeftJointVelocityDegPerSec) > 5f ||
                Mathf.Abs(diagnostics.RightJointVelocityDegPerSec) > 5f;
            bool poseNeverMoved = _maxSampleDisplacement < 0.005f && _maxSampleAbsYawDeltaDeg < 1f;
            if (wheelsMoved && poseNeverMoved)
            {
                verdict = "WARNING_POSE_MEASUREMENT_SUSPECT";
                reason = AppendReason(reason, "wheel_joint_velocity_without_pose_change");
                return;
            }

            bool sampleMovedButResultZero = (_maxSampleDisplacement > 0.03f && displacement < 0.005f) ||
                (_maxSampleAbsYawDeltaDeg > 5f && Mathf.Abs(yawDeltaDeg) < 0.5f);
            if (sampleMovedButResultZero)
            {
                verdict = "FAIL_RESULT_AGGREGATION_BUG";
                reason = AppendReason(reason, "pose_samples_changed_but_result_zero");
            }
        }

        private void LogBridgeConfiguration()
        {
            string details =
                $"valid={_driveBridge != null && _driveBridge.IsValid} left={(_wheelLeft != null ? _wheelLeft.name : "<null>")} right={(_wheelRight != null ? _wheelRight.name : "<null>")} " +
                $"wheelRadius={_wheelRadius:F3} wheelSeparation={_wheelSeparation:F3} leftSign={_leftWheelSign} rightSign={_rightWheelSign} forceLimit={_wheelForceLimit:F0} driveStiffness={_wheelDriveStiffness:F0} driveDamping={_wheelDriveDamping:F0} " +
                $"leftJoint={(_wheelLeft != null ? _wheelLeft.jointType.ToString() : "<null>")} rightJoint={(_wheelRight != null ? _wheelRight.jointType.ToString() : "<null>")} " +
                $"leftDriveTargetDeg={(_wheelLeft != null ? _wheelLeft.xDrive.targetVelocity : 0f):F1} rightDriveTargetDeg={(_wheelRight != null ? _wheelRight.xDrive.targetVelocity : 0f):F1}";
            Debug.Log($"{LogPrefix} bridge_config | {details}");
            EmitDiagnosticEvent(
                "drive_diagnostic_bridge_config",
                new Dictionary<string, object>
                {
                    ["valid"] = _driveBridge != null && _driveBridge.IsValid,
                    ["left"] = _wheelLeft != null ? _wheelLeft.name : "<null>",
                    ["right"] = _wheelRight != null ? _wheelRight.name : "<null>",
                    ["wheelRadius"] = _wheelRadius,
                    ["wheelSeparation"] = _wheelSeparation,
                    ["leftWheelSign"] = _leftWheelSign,
                    ["rightWheelSign"] = _rightWheelSign,
                    ["forceLimit"] = _wheelForceLimit,
                    ["wheelDriveStiffness"] = _wheelDriveStiffness,
                    ["wheelDriveDamping"] = _wheelDriveDamping,
                    ["linearVelocityCommandScale"] = _linearVelocityCommandScale,
                    ["angularVelocityCommandScale"] = _angularVelocityCommandScale,
                    ["minWheelTargetDegS"] = _minWheelTargetDegS,
                    ["maxWheelTargetDegS"] = _maxWheelTargetDegS,
                    ["leftJoint"] = _wheelLeft != null ? _wheelLeft.jointType.ToString() : "<null>",
                    ["rightJoint"] = _wheelRight != null ? _wheelRight.jointType.ToString() : "<null>",
                    ["leftDriveTargetDeg"] = _wheelLeft != null ? _wheelLeft.xDrive.targetVelocity : 0f,
                    ["rightDriveTargetDeg"] = _wheelRight != null ? _wheelRight.xDrive.targetVelocity : 0f,
                    ["leftDriveStiffness"] = _wheelLeft != null ? _wheelLeft.xDrive.stiffness : 0f,
                    ["rightDriveStiffness"] = _wheelRight != null ? _wheelRight.xDrive.stiffness : 0f,
                    ["leftDriveDamping"] = _wheelLeft != null ? _wheelLeft.xDrive.damping : 0f,
                    ["rightDriveDamping"] = _wheelRight != null ? _wheelRight.xDrive.damping : 0f,
                    ["leftDriveForceLimit"] = _wheelLeft != null ? _wheelLeft.xDrive.forceLimit : 0f,
                    ["rightDriveForceLimit"] = _wheelRight != null ? _wheelRight.xDrive.forceLimit : 0f
                });
        }

        private void LogReady()
        {
            string details =
                $"enabled={enabled} | robotReference={(_robotReference != null ? _robotReference.name : "<null>")} | wheelLeft={(_wheelLeft != null ? _wheelLeft.name : "<null>")} | wheelRight={(_wheelRight != null ? _wheelRight.name : "<null>")} | " +
                $"wheelRadius={_wheelRadius:F3} | wheelSeparation={_wheelSeparation:F3} | forceLimit={_wheelForceLimit:F0} | driveDamping={_wheelDriveDamping:F0}";
            Debug.Log($"{LogPrefix} ready | {details}");
            EmitDiagnosticEvent(
                "drive_diagnostic_ready",
                new Dictionary<string, object>
                {
                    ["enabled"] = enabled,
                    ["robotReference"] = _robotReference != null ? _robotReference.name : "<null>",
                    ["wheelLeft"] = _wheelLeft != null ? _wheelLeft.name : "<null>",
                    ["wheelRight"] = _wheelRight != null ? _wheelRight.name : "<null>",
                    ["wheelRadius"] = _wheelRadius,
                    ["wheelSeparation"] = _wheelSeparation,
                    ["forceLimit"] = _wheelForceLimit,
                    ["wheelDriveStiffness"] = _wheelDriveStiffness,
                    ["wheelDriveDamping"] = _wheelDriveDamping,
                    ["linearVelocityCommandScale"] = _linearVelocityCommandScale,
                    ["angularVelocityCommandScale"] = _angularVelocityCommandScale,
                    ["minWheelTargetDegS"] = _minWheelTargetDegS,
                    ["maxWheelTargetDegS"] = _maxWheelTargetDegS
                });
        }

        private void LogBuildStamp()
        {
            Debug.Log($"{LogPrefix} build_stamp | version={BuildStampVersion} | robotReference={(_robotReference != null ? _robotReference.name : "<null>")} | wheelLeft={(_wheelLeft != null ? _wheelLeft.name : "<null>")} | wheelRight={(_wheelRight != null ? _wheelRight.name : "<null>")}");
            EmitDiagnosticEvent(
                "drive_diagnostic_build_stamp",
                new Dictionary<string, object>
                {
                    ["version"] = BuildStampVersion,
                    ["robotReference"] = _robotReference != null ? _robotReference.name : "<null>",
                    ["wheelLeft"] = _wheelLeft != null ? _wheelLeft.name : "<null>",
                    ["wheelRight"] = _wheelRight != null ? _wheelRight.name : "<null>",
                    ["wheelRadius"] = _wheelRadius,
                    ["wheelSeparation"] = _wheelSeparation,
                    ["leftWheelSign"] = _leftWheelSign,
                    ["rightWheelSign"] = _rightWheelSign,
                    ["forceLimit"] = _wheelForceLimit,
                    ["wheelDriveStiffness"] = _wheelDriveStiffness,
                    ["wheelDriveDamping"] = _wheelDriveDamping,
                    ["linearVelocityCommandScale"] = _linearVelocityCommandScale,
                    ["angularVelocityCommandScale"] = _angularVelocityCommandScale,
                    ["minWheelTargetDegS"] = _minWheelTargetDegS,
                    ["maxWheelTargetDegS"] = _maxWheelTargetDegS,
                    ["runDiagnosticsOnPlay"] = RunDiagnosticsOnPlay,
                    ["disableAutonomousAdapterDuringDiagnostics"] = DisableAutonomousAdapterDuringDiagnostics,
                    ["runRotateInPlaceSweep"] = _runRotateInPlaceSweep,
                    ["runDriveDampingSweep"] = _runDriveDampingSweep,
                    ["runSignAxisProbe"] = _runSignAxisProbe
                });
        }

        private void LogConfigurationErrors()
        {
            string missing = string.Empty;
            AppendMissing(ref missing, _robotReference == null, "robotReference");
            AppendMissing(ref missing, _wheelLeft == null, "wheelLeft");
            AppendMissing(ref missing, _wheelRight == null, "wheelRight");
            AppendMissing(ref missing, _wheelRadius <= 0f, "wheelRadius");
            AppendMissing(ref missing, _wheelSeparation <= 0f, "wheelSeparation");
            if (string.IsNullOrEmpty(missing))
            {
                return;
            }

            Debug.LogWarning($"{LogPrefix} config_error | missing={missing}");
            EmitDiagnosticEvent("drive_diagnostic_config_error", new Dictionary<string, object> { ["missing"] = missing });
        }

        private void DisableAutonomousAdaptersIfRequested()
        {
            if (!DisableAutonomousAdapterDuringDiagnostics)
            {
                return;
            }

            AutonomousRobotAdapter[] adapters = FindObjectsByType<AutonomousRobotAdapter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (AutonomousRobotAdapter adapter in adapters)
            {
                if (adapter == null || !adapter.enabled)
                {
                    continue;
                }

                adapter.enabled = false;
                _disabledAdapters.Add(adapter);
                Debug.Log($"{LogPrefix} disabled_autonomous_adapter | object={adapter.name}");
                EmitDiagnosticEvent("drive_diagnostic_disabled_autonomous_adapter", new Dictionary<string, object> { ["object"] = adapter.name });
            }
        }

        private void EmitDiagnosticEvent(string eventType, Dictionary<string, object> payload)
        {
            bool logged = TiagoExperimentTelemetry.LogEvent(eventType, payload);
            if (logged || _loggerMissingWarningIssued)
            {
                return;
            }

            _loggerMissingWarningIssued = true;
            Debug.LogWarning($"{LogPrefix} logger_not_found | eventType={eventType} | expected=TiagoExperimentLogger.Active");
        }

        private static Dictionary<string, object> BuildMotionPayload(
            string test,
            float linearCommand,
            float angularCommand,
            float elapsed,
            Vector3 position,
            float baseYawDeg,
            float yawDeltaDeg,
            float observedV,
            float observedW,
            float displacement,
            float lateralDrift,
            TiagoDifferentialDriveBridge.DriveDiagnostics diagnostics)
        {
            Dictionary<string, object> payload = new()
            {
                ["test"] = test,
                ["elapsed"] = elapsed,
                ["vCmd"] = linearCommand,
                ["wCmd"] = angularCommand,
                ["effectiveVCmd"] = diagnostics.EffectiveCommandLinear,
                ["effectiveWCmd"] = diagnostics.EffectiveCommandAngular,
                ["leftTargetDegS"] = diagnostics.LeftTargetDegPerSec,
                ["rightTargetDegS"] = diagnostics.RightTargetDegPerSec,
                ["leftDriveTargetDegS"] = diagnostics.LeftDriveTargetDegPerSec,
                ["rightDriveTargetDegS"] = diagnostics.RightDriveTargetDegPerSec,
                ["leftJointVelocity"] = diagnostics.LeftJointVelocityDegPerSec * Mathf.Deg2Rad,
                ["rightJointVelocity"] = diagnostics.RightJointVelocityDegPerSec * Mathf.Deg2Rad,
                ["leftJointVelocityDegS"] = diagnostics.LeftJointVelocityDegPerSec,
                ["rightJointVelocityDegS"] = diagnostics.RightJointVelocityDegPerSec,
                ["leftLogicalJointVelocityDegS"] = diagnostics.LeftLogicalJointVelocityDegPerSec,
                ["rightLogicalJointVelocityDegS"] = diagnostics.RightLogicalJointVelocityDegPerSec,
                ["baseYawDeg"] = baseYawDeg,
                ["yawDeltaDeg"] = yawDeltaDeg,
                ["observedV"] = observedV,
                ["observedW"] = observedW,
                ["displacement"] = displacement,
                ["lateralDrift"] = lateralDrift,
                ["position"] = position,
                ["leftFollowRatio"] = diagnostics.LeftAbsFollowRatio,
                ["rightFollowRatio"] = diagnostics.RightAbsFollowRatio,
                ["leftSignedDirectionMatchesTarget"] = diagnostics.LeftSignedDirectionMatchesTarget,
                ["rightSignedDirectionMatchesTarget"] = diagnostics.RightSignedDirectionMatchesTarget
            };
            AddDriveConfigurationPayload(payload, diagnostics);
            return payload;
        }

        private static void AddDriveConfigurationPayload(Dictionary<string, object> payload, TiagoDifferentialDriveBridge.DriveDiagnostics diagnostics)
        {
            payload["linearVelocityCommandScale"] = diagnostics.LinearVelocityCommandScale;
            payload["angularVelocityCommandScale"] = diagnostics.AngularVelocityCommandScale;
            payload["minWheelTargetDegS"] = diagnostics.MinWheelTargetDegPerSec;
            payload["maxWheelTargetDegS"] = diagnostics.MaxWheelTargetDegPerSec;
            payload["leftDriveStiffness"] = diagnostics.LeftDriveStiffness;
            payload["rightDriveStiffness"] = diagnostics.RightDriveStiffness;
            payload["leftDriveDamping"] = diagnostics.LeftDriveDamping;
            payload["rightDriveDamping"] = diagnostics.RightDriveDamping;
            payload["leftDriveForceLimit"] = diagnostics.LeftForceLimit;
            payload["rightDriveForceLimit"] = diagnostics.RightForceLimit;
            payload["leftJointType"] = diagnostics.LeftJointType;
            payload["rightJointType"] = diagnostics.RightJointType;
        }

        private void AddPoseMeasurementPayload(
            Dictionary<string, object> payload,
            Vector3 positionWorld,
            Vector3 rotationEuler,
            Vector3 deltaPosition,
            float yawDeg,
            float yawDeltaDeg,
            float displacement,
            float observedVForwardInitial,
            float observedVForwardCurrent,
            float observedWFromTransform,
            bool hasArticulationBody,
            Vector3 articulationVelocity,
            Vector3 articulationAngularVelocity,
            float observedVFromArticulationBody,
            float observedWFromArticulationBody)
        {
            payload["robotReference"] = _robotReference != null ? _robotReference.name : "<null>";
            payload["robotReferencePosition"] = positionWorld;
            payload["robotReferenceRotationEuler"] = rotationEuler;
            payload["robotReferenceHasChanged"] = _robotReference != null && _robotReference.hasChanged;
            payload["time"] = Time.time;
            payload["fixedTime"] = Time.fixedTime;
            payload["startPosition"] = _startPosition;
            payload["currentPosition"] = Flatten(positionWorld);
            payload["deltaPosition"] = deltaPosition;
            payload["deltaX"] = deltaPosition.x;
            payload["deltaZ"] = deltaPosition.z;
            payload["x"] = positionWorld.x;
            payload["y"] = positionWorld.y;
            payload["z"] = positionWorld.z;
            payload["startYawDeg"] = _startYawDeg;
            payload["currentYawDeg"] = yawDeg;
            payload["yawDeg"] = yawDeg;
            payload["yawDeltaDeg"] = yawDeltaDeg;
            payload["displacementFromStart"] = displacement;
            payload["observedV_forwardInitial"] = observedVForwardInitial;
            payload["observedV_forwardCurrent"] = observedVForwardCurrent;
            payload["observedV_fromTransform"] = observedVForwardInitial;
            payload["observedW_fromTransform"] = observedWFromTransform;
            payload["robotReferenceHasArticulationBody"] = hasArticulationBody;
            payload["articulationBodyVelocity"] = articulationVelocity;
            payload["articulationBodyAngularVelocity"] = articulationAngularVelocity;
            payload["observedV_fromArticulationBody"] = observedVFromArticulationBody;
            payload["observedW_fromArticulationBody"] = observedWFromArticulationBody;
        }

        private static string AppendReason(string existing, string addition)
        {
            if (string.IsNullOrEmpty(existing) || existing == "within_thresholds")
            {
                return addition;
            }

            return $"{existing};{addition}";
        }

        private void RestoreDisabledAdapters()
        {
            for (int i = 0; i < _disabledAdapters.Count; i++)
            {
                if (_disabledAdapters[i] != null)
                {
                    _disabledAdapters[i].enabled = true;
                }
            }

            _disabledAdapters.Clear();
        }

        private static void AppendMissing(ref string missing, bool condition, string name)
        {
            if (!condition)
            {
                return;
            }

            missing = string.IsNullOrEmpty(missing) ? name : $"{missing},{name}";
        }

        private static Vector3 Flatten(Vector3 value)
        {
            value.y = 0f;
            return value;
        }
    }
}
