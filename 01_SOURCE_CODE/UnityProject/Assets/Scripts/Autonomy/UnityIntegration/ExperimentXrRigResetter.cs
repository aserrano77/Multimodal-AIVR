using System;
using System.Collections;
using System.Collections.Generic;
using Autonomy.Domain;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class ExperimentXrRigResetter : MonoBehaviour
    {
        private const string LogPrefix = "[ExperimentXrRigResetter]";
        private const int BatchModeTrackingTimeoutFrames = 30;

        [Header("Participant floor/root anchor")]
        [SerializeField] private bool _enabled = true;
        [SerializeField] private Transform _rigRoot;
        [SerializeField] private XROrigin _xrOrigin;
        [SerializeField] private CharacterController _characterController;
        [SerializeField] private Transform _participantStartAnchor;
        [SerializeField] private Transform _experimentalHeadPoseAnchor;
        [SerializeField] private bool _disableCharacterControllerDuringReset = true;

        [Header("Tracking readiness")]
        [SerializeField, Min(0.1f)] private float _trackingTimeoutSeconds = 8f;
        [SerializeField, Min(1)] private int _stableFramesRequired = 5;
        [SerializeField, Min(0f)] private float _stablePositionDeltaMeters = 0.01f;
        [SerializeField, Min(0f)] private float _stableRotationDeltaDegrees = 1f;

        [Header("Bounded camera alignment")]
        [SerializeField, Min(1)] private int _maxAlignmentAttempts = 4;
        [SerializeField, Min(0)] private int _maxTrackingOriginUpdates = 4;
        [SerializeField, Min(0f)] private float _horizontalToleranceMeters = 0.03f;
        [SerializeField, Min(0f)] private float _yawToleranceDegrees = 2f;

        private readonly List<XRInputSubsystem> _inputSubsystems = new List<XRInputSubsystem>();
        private XRInputSubsystem _subscribedSubsystem;
        private bool _trackingOriginChanged;
        private bool _alignmentWindowActive;
        private ExperimentXrRigAlignmentResult _lastResult;

        public event Action<ExperimentRuntimeContext, ExperimentXrRigAlignmentResult> AlignmentGateReleased;

        public bool IsConfigured => _xrOrigin != null && _xrOrigin.Camera != null &&
            _participantStartAnchor != null && _experimentalHeadPoseAnchor != null;
        public Transform RigRoot => ResolveRigRoot();
        public XROrigin Origin => _xrOrigin;
        public Camera HeadCamera => _xrOrigin != null ? _xrOrigin.Camera : null;
        public Transform ParticipantStartAnchor => _participantStartAnchor;
        public Transform ExperimentalHeadPoseAnchor => _experimentalHeadPoseAnchor;
        public bool IsAlignmentWindowActive => _alignmentWindowActive;
        public ExperimentXrRigAlignmentResult LastResult => _lastResult;
        public int TrackingOriginUpdateCount { get; private set; }

        private void Awake()
        {
            ResolveReferences();
        }

        private void OnDisable()
        {
            UnsubscribeTrackingOriginUpdates();
            _alignmentWindowActive = false;
        }

        /// <summary>
        /// Pre-session alignment used before the experimental-scene protocol UI is revealed.
        /// It deliberately logs through Debug only because no authoritative session exists yet.
        /// </summary>
        public IEnumerator AlignBeforeProtocolUiCoroutine(Action<ExperimentXrRigAlignmentResult> completed = null)
        {
            yield return AlignCameraCoroutine(null, "experimental_scene_ui_initialization", completed);
        }

        /// <summary>
        /// Session-scoped alignment gate. The caller must yield this coroutine before spawning a round.
        /// </summary>
        public IEnumerator AlignForTrialCoroutine(
            ExperimentRuntimeContext context,
            Action<ExperimentXrRigAlignmentResult> completed = null)
        {
            yield return AlignCameraCoroutine(context, "trial_or_condition_transition", completed);
        }

        /// <summary>
        /// Deterministic test seam and the only pose mutation used by the runtime alignment loop.
        /// It moves the XROrigin, never the tracked camera or Camera Offset.
        /// </summary>
        public bool TryAlignCameraToExperimentalAnchor(
            out float positionErrorMeters,
            out float horizontalErrorMeters,
            out float yawErrorDegrees)
        {
            ResolveReferences();
            positionErrorMeters = float.PositiveInfinity;
            horizontalErrorMeters = float.PositiveInfinity;
            yawErrorDegrees = float.PositiveInfinity;
            if (!IsConfigured ||
                !ExperimentStartSceneXrPoseMath.TryGetHorizontalForward(
                    _experimentalHeadPoseAnchor.forward,
                    out Vector3 desiredHorizontalForward))
            {
                return false;
            }

            Transform root = ResolveRigRoot();
            Transform cameraTransform = _xrOrigin.Camera.transform;
            CharacterController controller = ResolveCharacterController(root);
            bool controllerWasEnabled = controller != null && controller.enabled;
            if (_disableCharacterControllerDuringReset && controllerWasEnabled)
            {
                controller.enabled = false;
            }

            bool yawAligned;
            bool positionAligned;
            try
            {
                // ParticipantStartAnchor is a floor/root anchor. Normalizing only root Y preserves
                // room-scale head height while the official XROrigin APIs compensate HMD X/Z and yaw.
                Vector3 rootPosition = root.position;
                rootPosition.y = _participantStartAnchor.position.y;
                root.position = rootPosition;

                yawAligned = _xrOrigin.MatchOriginUpCameraForward(Vector3.up, desiredHorizontalForward);
                Vector3 targetHeadPosition = new Vector3(
                    _experimentalHeadPoseAnchor.position.x,
                    cameraTransform.position.y,
                    _experimentalHeadPoseAnchor.position.z);
                positionAligned = _xrOrigin.MoveCameraToWorldLocation(targetHeadPosition);
                Physics.SyncTransforms();
            }
            finally
            {
                if (_disableCharacterControllerDuringReset && controller != null)
                {
                    controller.enabled = controllerWasEnabled;
                }
            }

            MeasureErrors(out positionErrorMeters, out horizontalErrorMeters, out yawErrorDegrees);
            return yawAligned && positionAligned;
        }

        /// <summary>Test-only configuration without scene dependencies.</summary>
        public void ConfigureForTests(
            XROrigin xrOrigin,
            Transform participantStartAnchor,
            Transform experimentalHeadPoseAnchor,
            float trackingTimeoutSeconds = 0.1f,
            int stableFramesRequired = 1,
            int maxAlignmentAttempts = 2,
            int maxTrackingOriginUpdates = 2)
        {
            _xrOrigin = xrOrigin;
            _rigRoot = xrOrigin != null ? xrOrigin.transform : null;
            _participantStartAnchor = participantStartAnchor;
            _experimentalHeadPoseAnchor = experimentalHeadPoseAnchor;
            _trackingTimeoutSeconds = Mathf.Max(0.01f, trackingTimeoutSeconds);
            _stableFramesRequired = Mathf.Max(1, stableFramesRequired);
            _maxAlignmentAttempts = Mathf.Max(1, maxAlignmentAttempts);
            _maxTrackingOriginUpdates = Mathf.Max(0, maxTrackingOriginUpdates);
        }

        private IEnumerator AlignCameraCoroutine(
            ExperimentRuntimeContext context,
            string source,
            Action<ExperimentXrRigAlignmentResult> completed)
        {
            while (_alignmentWindowActive)
            {
                yield return null;
            }

            ResolveReferences();
            _alignmentWindowActive = true;
            _trackingOriginChanged = false;
            TrackingOriginUpdateCount = 0;
            int attempts = 0;
            bool sessionScoped = HasAuthoritativeContext(context);
            string cameraBefore = DescribePose(HeadCamera != null ? HeadCamera.transform : null);
            string cameraLocalBefore = DescribeLocalPose(HeadCamera != null ? HeadCamera.transform : null);
            string rootBefore = DescribePose(ResolveRigRoot());
            LogAlignmentEvent(
                "experiment_xr_alignment_begin",
                context,
                BuildDetails(source, cameraBefore, cameraLocalBefore, rootBefore, attempts, string.Empty),
                sessionScoped);

            if (!_enabled || !IsConfigured)
            {
                string reason = !_enabled
                    ? "xr_alignment_disabled"
                    : _xrOrigin == null
                        ? "xr_origin_missing"
                        : _xrOrigin.Camera == null
                            ? "head_camera_missing"
                            : _participantStartAnchor == null
                                ? "participant_floor_anchor_missing"
                                : "experimental_head_pose_anchor_missing";
                FinishAlignment(context, source, false, false, reason, attempts, sessionScoped, completed);
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + _trackingTimeoutSeconds;
            int stableFrames = 0;
            bool hasPreviousPose = false;
            Vector3 previousPosition = Vector3.zero;
            Quaternion previousRotation = Quaternion.identity;
            string readinessReason = "active_xr_input_subsystem_missing";
            bool trackingOriginModeResolved = false;
            bool trackingStable = false;
            int trackingWaitFrames = 0;

            while (Time.realtimeSinceStartup <= deadline)
            {
                trackingWaitFrames++;
                if (Application.isBatchMode && trackingWaitFrames > BatchModeTrackingTimeoutFrames)
                {
                    readinessReason = "batch_mode_tracking_unavailable";
                    break;
                }

                XRInputSubsystem subsystem = ResolveActiveInputSubsystem();
                if (subsystem == null)
                {
                    readinessReason = "active_xr_input_subsystem_missing";
                    stableFrames = 0;
                    yield return null;
                    continue;
                }

                SubscribeTrackingOriginUpdates(subsystem);
                if (!trackingOriginModeResolved)
                {
                    trackingOriginModeResolved = TryRequestFloorTrackingOrigin(subsystem, context, sessionScoped);
                    if (!trackingOriginModeResolved)
                    {
                        readinessReason = "tracking_origin_modes_unknown";
                        stableFrames = 0;
                        yield return null;
                        continue;
                    }
                }

                if (!TryReadTrackedHeadPose(out Vector3 localPosition, out Quaternion localRotation, out readinessReason))
                {
                    stableFrames = 0;
                    hasPreviousPose = false;
                    yield return null;
                    continue;
                }

                if (_trackingOriginChanged)
                {
                    _trackingOriginChanged = false;
                    stableFrames = 0;
                    hasPreviousPose = false;
                    yield return null;
                    continue;
                }

                if (hasPreviousPose &&
                    Vector3.Distance(previousPosition, localPosition) <= _stablePositionDeltaMeters &&
                    Quaternion.Angle(previousRotation, localRotation) <= _stableRotationDeltaDegrees)
                {
                    stableFrames++;
                }
                else
                {
                    stableFrames = 1;
                }

                previousPosition = localPosition;
                previousRotation = localRotation;
                hasPreviousPose = true;
                if (stableFrames >= _stableFramesRequired)
                {
                    trackingStable = true;
                    break;
                }

                yield return null;
            }

            if (!trackingStable)
            {
                string timeoutReason = string.IsNullOrEmpty(readinessReason)
                    ? "head_pose_not_stable"
                    : readinessReason;
                FinishAlignment(
                    context,
                    source,
                    false,
                    true,
                    "tracking_timeout:" + timeoutReason,
                    attempts,
                    sessionScoped,
                    completed);
                yield break;
            }

            for (int attempt = 1; attempt <= _maxAlignmentAttempts; attempt++)
            {
                attempts = attempt;
                string attemptCameraBefore = DescribePose(HeadCamera.transform);
                string attemptCameraLocalBefore = DescribeLocalPose(HeadCamera.transform);
                string attemptRootBefore = DescribePose(ResolveRigRoot());
                bool apiSucceeded = TryAlignCameraToExperimentalAnchor(
                    out float immediatePositionError,
                    out float immediateHorizontalError,
                    out float immediateYawError);
                var attemptDetails = BuildDetails(
                    source,
                    attemptCameraBefore,
                    attemptCameraLocalBefore,
                    attemptRootBefore,
                    attempt,
                    apiSucceeded ? "official_xr_origin_apis_succeeded" : "official_xr_origin_apis_failed");
                attemptDetails["position_error_m"] = SanitizeError(immediatePositionError);
                attemptDetails["horizontal_error_m"] = SanitizeError(immediateHorizontalError);
                attemptDetails["yaw_error_deg"] = SanitizeError(immediateYawError);
                LogAlignmentEvent("experiment_xr_alignment_attempt", context, attemptDetails, sessionScoped);

                yield return new WaitForEndOfFrame();
                MeasureErrors(out float positionError, out float horizontalError, out float yawError);
                if (_trackingOriginChanged)
                {
                    _trackingOriginChanged = false;
                    continue;
                }

                if (apiSucceeded &&
                    ExperimentXrPoseAlignmentMath.IsWithinHorizontalTolerance(
                        horizontalError,
                        yawError,
                        _horizontalToleranceMeters,
                        _yawToleranceDegrees))
                {
                    FinishAlignment(context, source, true, false, string.Empty, attempts, sessionScoped, completed);
                    yield break;
                }
            }

            FinishAlignment(
                context,
                source,
                false,
                false,
                "alignment_tolerance_not_reached",
                attempts,
                sessionScoped,
                completed);
        }

        private void FinishAlignment(
            ExperimentRuntimeContext context,
            string source,
            bool succeeded,
            bool timedOut,
            string reason,
            int attempts,
            bool sessionScoped,
            Action<ExperimentXrRigAlignmentResult> completed)
        {
            MeasureErrors(out float positionError, out float horizontalError, out float yawError);
            _lastResult = new ExperimentXrRigAlignmentResult(
                succeeded,
                timedOut,
                reason,
                attempts,
                TrackingOriginUpdateCount,
                positionError,
                horizontalError,
                yawError);

            var details = BuildDetails(
                source,
                string.Empty,
                string.Empty,
                string.Empty,
                attempts,
                reason);
            details["timed_out"] = timedOut;
            details["position_error_m"] = SanitizeError(positionError);
            details["horizontal_error_m"] = SanitizeError(horizontalError);
            details["yaw_error_deg"] = SanitizeError(yawError);
            details["fallback"] = succeeded ? "none" : "release_gate_fail_soft";
            LogAlignmentEvent(
                succeeded ? "experiment_xr_alignment_succeeded" : "experiment_xr_alignment_failed",
                context,
                details,
                sessionScoped,
                warning: !succeeded);

            UnsubscribeTrackingOriginUpdates();
            _alignmentWindowActive = false;
            AlignmentGateReleased?.Invoke(context, _lastResult);
            completed?.Invoke(_lastResult);
        }

        private bool TryRequestFloorTrackingOrigin(
            XRInputSubsystem subsystem,
            ExperimentRuntimeContext context,
            bool sessionScoped)
        {
            TrackingOriginModeFlags supported = subsystem.GetSupportedTrackingOriginModes();
            if (supported == TrackingOriginModeFlags.Unknown)
            {
                var unknown = BuildTrackingOriginDetails(
                    supported,
                    TrackingOriginModeFlags.Unknown,
                    TrackingOriginModeFlags.Unknown,
                    false,
                    "defer_until_supported_modes_known");
                LogAlignmentEvent("experiment_xr_tracking_origin_mode", context, unknown, sessionScoped);
                return false;
            }

            TrackingOriginModeFlags before = subsystem.GetTrackingOriginMode();
            bool floorSupported = (supported & TrackingOriginModeFlags.Floor) != 0;
            bool requestSucceeded = floorSupported && subsystem.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
            TrackingOriginModeFlags effective = subsystem.GetTrackingOriginMode();
            string fallback = ExperimentXrPoseAlignmentMath.DescribeFloorFallback(
                floorSupported,
                requestSucceeded,
                effective.ToString());
            LogAlignmentEvent(
                "experiment_xr_tracking_origin_mode",
                context,
                BuildTrackingOriginDetails(supported, before, effective, requestSucceeded, fallback),
                sessionScoped);
            return true;
        }

        private XRInputSubsystem ResolveActiveInputSubsystem()
        {
            if (_subscribedSubsystem != null && _subscribedSubsystem.running)
            {
                return _subscribedSubsystem;
            }

            _inputSubsystems.Clear();
            SubsystemManager.GetSubsystems(_inputSubsystems);
            for (int i = 0; i < _inputSubsystems.Count; i++)
            {
                XRInputSubsystem candidate = _inputSubsystems[i];
                if (candidate != null && candidate.running)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool TryReadTrackedHeadPose(
            out Vector3 localPosition,
            out Quaternion localRotation,
            out string reason)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!head.isValid)
            {
                reason = "head_device_invalid";
                return false;
            }

            if (!head.TryGetFeatureValue(CommonUsages.isTracked, out bool isTracked) || !isTracked)
            {
                reason = "head_not_tracked";
                return false;
            }

            if (!head.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState state) ||
                (state & InputTrackingState.Position) == 0 ||
                (state & InputTrackingState.Rotation) == 0)
            {
                reason = "head_pose_components_invalid";
                return false;
            }

            if (!head.TryGetFeatureValue(CommonUsages.devicePosition, out localPosition) ||
                !head.TryGetFeatureValue(CommonUsages.deviceRotation, out localRotation))
            {
                reason = "head_pose_unavailable";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private void SubscribeTrackingOriginUpdates(XRInputSubsystem subsystem)
        {
            if (ReferenceEquals(_subscribedSubsystem, subsystem))
            {
                return;
            }

            UnsubscribeTrackingOriginUpdates();
            _subscribedSubsystem = subsystem;
            _subscribedSubsystem.trackingOriginUpdated += OnTrackingOriginUpdated;
        }

        private void UnsubscribeTrackingOriginUpdates()
        {
            if (_subscribedSubsystem != null)
            {
                _subscribedSubsystem.trackingOriginUpdated -= OnTrackingOriginUpdated;
                _subscribedSubsystem = null;
            }
        }

        private void OnTrackingOriginUpdated(XRInputSubsystem subsystem)
        {
            RegisterTrackingOriginUpdate();
        }

        private void RegisterTrackingOriginUpdate()
        {
            if (!_alignmentWindowActive || TrackingOriginUpdateCount >= _maxTrackingOriginUpdates)
            {
                return;
            }

            TrackingOriginUpdateCount++;
            _trackingOriginChanged = true;
        }

        private void MeasureErrors(
            out float positionErrorMeters,
            out float horizontalErrorMeters,
            out float yawErrorDegrees)
        {
            Transform cameraTransform = HeadCamera != null ? HeadCamera.transform : null;
            if (cameraTransform == null || _experimentalHeadPoseAnchor == null)
            {
                positionErrorMeters = float.PositiveInfinity;
                horizontalErrorMeters = float.PositiveInfinity;
                yawErrorDegrees = float.PositiveInfinity;
                return;
            }

            positionErrorMeters = Vector3.Distance(cameraTransform.position, _experimentalHeadPoseAnchor.position);
            horizontalErrorMeters = ExperimentXrPoseAlignmentMath.HorizontalDistance(
                cameraTransform.position,
                _experimentalHeadPoseAnchor.position);
            yawErrorDegrees = ExperimentStartSceneXrPoseMath.CalculateHorizontalYawErrorDegrees(
                cameraTransform.forward,
                _experimentalHeadPoseAnchor.forward);
        }

        private Transform ResolveRigRoot()
        {
            return _xrOrigin != null ? _xrOrigin.transform : _rigRoot;
        }

        private CharacterController ResolveCharacterController(Transform root)
        {
            return _characterController != null
                ? _characterController
                : root != null
                    ? root.GetComponent<CharacterController>()
                    : null;
        }

        private void ResolveReferences()
        {
            _xrOrigin ??= FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
            _rigRoot ??= _xrOrigin != null ? _xrOrigin.transform : null;
            if (_participantStartAnchor == null)
            {
                GameObject floorAnchor = GameObject.Find("ParticipantStartAnchor");
                _participantStartAnchor = floorAnchor != null ? floorAnchor.transform : null;
            }

            if (_experimentalHeadPoseAnchor == null)
            {
                GameObject headAnchor = GameObject.Find("ExperimentalHeadPoseAnchor");
                _experimentalHeadPoseAnchor = headAnchor != null ? headAnchor.transform : null;
            }
        }

        private Dictionary<string, object> BuildDetails(
            string source,
            string cameraBefore,
            string cameraLocalBefore,
            string rootBefore,
            int attempts,
            string reason)
        {
            return new Dictionary<string, object>
            {
                ["source"] = source ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["attempt"] = attempts,
                ["tracking_origin_updates"] = TrackingOriginUpdateCount,
                ["root_before"] = rootBefore ?? string.Empty,
                ["root_after"] = DescribePose(ResolveRigRoot()),
                ["camera_world_before"] = cameraBefore ?? string.Empty,
                ["camera_local_before"] = cameraLocalBefore ?? string.Empty,
                ["camera_world_after"] = DescribePose(HeadCamera != null ? HeadCamera.transform : null),
                ["camera_local_after"] = DescribeLocalPose(HeadCamera != null ? HeadCamera.transform : null),
                ["participant_floor_anchor"] = DescribePose(_participantStartAnchor),
                ["experimental_head_pose_anchor"] = DescribePose(_experimentalHeadPoseAnchor),
                ["preserve_floor_tracked_head_height"] = true,
                ["max_attempts"] = _maxAlignmentAttempts
            };
        }

        private static Dictionary<string, object> BuildTrackingOriginDetails(
            TrackingOriginModeFlags supported,
            TrackingOriginModeFlags before,
            TrackingOriginModeFlags effective,
            bool requestSucceeded,
            string fallback)
        {
            return new Dictionary<string, object>
            {
                ["supported_modes"] = supported.ToString(),
                ["requested_mode"] = TrackingOriginModeFlags.Floor.ToString(),
                ["mode_before"] = before.ToString(),
                ["request_succeeded"] = requestSucceeded,
                ["effective_mode"] = effective.ToString(),
                ["floor_supported"] = (supported & TrackingOriginModeFlags.Floor) != 0,
                ["fallback"] = fallback ?? string.Empty
            };
        }

        private static bool HasAuthoritativeContext(ExperimentRuntimeContext context)
        {
            return context != null &&
                !string.IsNullOrWhiteSpace(context.ParticipantId) &&
                !string.IsNullOrWhiteSpace(context.SessionId);
        }

        private static void LogAlignmentEvent(
            string eventName,
            ExperimentRuntimeContext context,
            Dictionary<string, object> details,
            bool sessionScoped,
            bool warning = false)
        {
            if (sessionScoped)
            {
                details["participant_id"] = context.ParticipantId;
                details["session_id"] = context.SessionId;
                details["trial_id"] = context.TrialId ?? string.Empty;
                details["trial_index"] = context.TrialIndex;
                details["condition_id"] = context.ConditionId ?? string.Empty;
                details["round_index"] = context.RoundIndexWithinCondition;
                TiagoExperimentTelemetry.LogEvent(eventName, details);
                return;
            }

            string message = $"{LogPrefix} {eventName} | {FormatDetails(details)}";
            if (warning)
            {
                Debug.LogWarning(message);
            }
            else
            {
                Debug.Log(message);
            }
        }

        private static string FormatDetails(Dictionary<string, object> details)
        {
            var parts = new List<string>(details.Count);
            foreach (KeyValuePair<string, object> entry in details)
            {
                parts.Add($"{entry.Key}={entry.Value}");
            }

            return string.Join(" ", parts);
        }

        private static string DescribePose(Transform value)
        {
            return value == null
                ? "missing"
                : $"pos({value.position.x:F5},{value.position.y:F5},{value.position.z:F5})_rot({value.eulerAngles.x:F3},{value.eulerAngles.y:F3},{value.eulerAngles.z:F3})";
        }

        private static float SanitizeError(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? -1f : value;
        }

        private static string DescribeLocalPose(Transform value)
        {
            return value == null
                ? "missing"
                : $"pos({value.localPosition.x:F5},{value.localPosition.y:F5},{value.localPosition.z:F5})_rot({value.localEulerAngles.x:F3},{value.localEulerAngles.y:F3},{value.localEulerAngles.z:F3})";
        }
    }

    public sealed class ExperimentXrRigAlignmentResult
    {
        public ExperimentXrRigAlignmentResult(
            bool succeeded,
            bool timedOut,
            string failureReason,
            int attemptCount,
            int trackingOriginUpdateCount,
            float positionErrorMeters,
            float horizontalErrorMeters,
            float yawErrorDegrees)
        {
            Succeeded = succeeded;
            TimedOut = timedOut;
            FailureReason = failureReason ?? string.Empty;
            AttemptCount = attemptCount;
            TrackingOriginUpdateCount = trackingOriginUpdateCount;
            PositionErrorMeters = positionErrorMeters;
            HorizontalErrorMeters = horizontalErrorMeters;
            YawErrorDegrees = yawErrorDegrees;
        }

        public bool Succeeded { get; }
        public bool TimedOut { get; }
        public string FailureReason { get; }
        public int AttemptCount { get; }
        public int TrackingOriginUpdateCount { get; }
        public float PositionErrorMeters { get; }
        public float HorizontalErrorMeters { get; }
        public float YawErrorDegrees { get; }
    }

    public static class ExperimentXrPoseAlignmentMath
    {
        public static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float z = a.z - b.z;
            return Mathf.Sqrt(x * x + z * z);
        }

        public static bool IsWithinHorizontalTolerance(
            float horizontalErrorMeters,
            float yawErrorDegrees,
            float horizontalToleranceMeters,
            float yawToleranceDegrees)
        {
            return horizontalErrorMeters <= horizontalToleranceMeters &&
                yawErrorDegrees <= yawToleranceDegrees;
        }

        public static string DescribeFloorFallback(
            bool floorSupported,
            bool requestSucceeded,
            string effectiveMode)
        {
            if (!floorSupported)
            {
                return "floor_unsupported_keep_" + (effectiveMode ?? string.Empty);
            }

            return requestSucceeded
                ? "none"
                : "floor_request_failed_keep_" + (effectiveMode ?? string.Empty);
        }
    }
}
