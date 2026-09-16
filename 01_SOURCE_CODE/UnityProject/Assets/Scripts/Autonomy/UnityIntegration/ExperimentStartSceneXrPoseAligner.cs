using System;
using System.Collections;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Authority that maps the tracked head pose to a deterministic world-space anchor before the
    /// start-scene UI becomes interactive and after observable XR reference-space changes. It never
    /// writes to the camera transform and never follows the head continuously.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ExperimentStartSceneXrPoseAligner : MonoBehaviour
    {
        private const string LogPrefix = "[ExperimentStartSceneXrPoseAligner]";
        private const string StartSceneName = "experiment_start_scene";

        [Header("Start-scene pose authority")]
        [SerializeField] private XROrigin _xrOrigin;
        [SerializeField] private Transform _headPoseAnchor;

        [Header("Tracking readiness")]
        [SerializeField, Min(0.1f)] private float _trackingTimeoutSeconds = 8f;
        [SerializeField, Min(1)] private int _stableFramesRequired = 5;
        [SerializeField, Min(0f)] private float _stablePositionDeltaMeters = 0.01f;
        [SerializeField, Min(0f)] private float _stableRotationDeltaDegrees = 1f;

        [Header("Bounded alignment")]
        [SerializeField, Min(1)] private int _maxAlignmentAttempts = 4;
        [SerializeField, Min(0)] private int _maxTrackingOriginUpdates = 4;
        [SerializeField, Min(0f)] private float _positionToleranceMeters = 0.03f;
        [SerializeField, Min(0f)] private float _yawToleranceDegrees = 2f;

        [Header("Post-start reference-space changes")]
        [SerializeField, Min(0f)] private float _recenterDebounceSeconds = 0.25f;
        [SerializeField, Min(0f)] private float _internalEventSuppressionSeconds = 0.35f;
        [SerializeField, Min(0)] private int _maxPendingRealignmentReplays = 1;

        private readonly List<XRInputSubsystem> _inputSubsystems = new List<XRInputSubsystem>();
        private Coroutine _alignmentCoroutine;
        private XRInputSubsystem _subscribedSubsystem;
        private bool _trackingOriginChanged;
        private bool _bypassSceneGuardForTests;
        private bool _assumeTrackingReadyForTests;
        private Coroutine _recenterCoroutine;
        private bool _recenterTrackingChanged;
        private bool _applicationPaused;
        private bool _applicationFocused = true;
        private int _internalEventSuppressionDepth;
        private float _internalEventSuppressionUntil;
        private string _internalSuppressionSource = string.Empty;
        private IExperimentStartSceneRealignmentUiGate _realignmentUiGate;
        private ExperimentStartSceneRecenterRequestTracker _recenterRequests;
        private int _recenterOperationSequence;
        private int _suppressedInternalEventCount;

        public event Action<ExperimentStartSceneXrPoseAligner> AlignmentFinished;

        public XROrigin Origin => _xrOrigin;
        public Camera HeadCamera => _xrOrigin != null ? _xrOrigin.Camera : null;
        public Transform HeadPoseAnchor => _headPoseAnchor;
        public bool IsAlignmentRunning { get; private set; }
        public bool IsAlignmentComplete { get; private set; }
        public bool AlignmentSucceeded { get; private set; }
        public string FailureReason { get; private set; } = string.Empty;
        public int AlignmentAttemptCount { get; private set; }
        public int TrackingOriginUpdateCount { get; private set; }
        public float FinalPositionErrorMeters { get; private set; } = float.PositiveInfinity;
        public float FinalYawErrorDegrees { get; private set; } = float.PositiveInfinity;
        public bool IsRealignmentRunning { get; private set; }
        public ExperimentStartSceneAlignmentState AlignmentState { get; private set; } = ExperimentStartSceneAlignmentState.Idle;
        public int RecenterOperationCount { get; private set; }
        public int RecenterGateReleaseCount { get; private set; }
        public int SuppressedInternalEventCount => _suppressedInternalEventCount;
        public int RecenterSignalGeneration => EnsureRecenterTracker().SignalGeneration;
        public int RecenterReplayCount => EnsureRecenterTracker().ReplayCount;
        public bool IsRealignmentPendingOrRunning =>
            IsRealignmentRunning || _recenterCoroutine != null || EnsureRecenterTracker().HasScheduledSignals;
        public ExperimentStartSceneAlignmentReason LastRealignmentReason { get; private set; }

        private void Awake()
        {
            ResolveReferences();
            EnsureRecenterTracker();
        }

        private void OnEnable()
        {
            _applicationFocused = Application.isFocused;
            ResolveReferences();
            TrySubscribeToActiveInputSubsystem();
        }

        private void OnDisable()
        {
            UnsubscribeTrackingOriginUpdates();
            if (_alignmentCoroutine != null)
            {
                StopCoroutine(_alignmentCoroutine);
                _alignmentCoroutine = null;
            }

            if (_recenterCoroutine != null)
            {
                StopCoroutine(_recenterCoroutine);
                _recenterCoroutine = null;
            }

            SafeReleaseRealignmentUiGate("aligner_disabled", false);

            IsAlignmentRunning = false;
            IsRealignmentRunning = false;
            AlignmentState = ExperimentStartSceneAlignmentState.Idle;
        }

        public bool BeginAlignment()
        {
            if (IsAlignmentRunning)
            {
                return false;
            }

            ResetResult();
            ResolveReferences();
            if (!_bypassSceneGuardForTests &&
                !string.Equals(SceneManager.GetActiveScene().name, StartSceneName, StringComparison.Ordinal))
            {
                CompleteFailure("outside_start_scene");
                return false;
            }

            if (_xrOrigin == null || _xrOrigin.Camera == null || _headPoseAnchor == null)
            {
                CompleteFailure(_xrOrigin == null
                    ? "xr_origin_missing"
                    : _xrOrigin.Camera == null
                        ? "head_camera_missing"
                        : "head_pose_anchor_missing");
                return false;
            }

            IsAlignmentRunning = true;
            AlignmentState = ExperimentStartSceneAlignmentState.WaitingForTracking;
            LogEvent(
                "start_scene_xr_alignment_begin",
                $"scene={SceneManager.GetActiveScene().name} camera_before={DescribePose(_xrOrigin.Camera.transform)} " +
                $"camera_local_before={DescribeLocalPose(_xrOrigin.Camera.transform)} anchor={DescribePose(_headPoseAnchor)} " +
                $"timeout_seconds={_trackingTimeoutSeconds:F3} stable_frames_required={_stableFramesRequired} " +
                $"max_attempts={_maxAlignmentAttempts}");
            _alignmentCoroutine = StartCoroutine(AlignWhenTrackingReady());
            return true;
        }

        public void RegisterRealignmentUiGate(IExperimentStartSceneRealignmentUiGate gate)
        {
            _realignmentUiGate = gate;
        }

        public void UnregisterRealignmentUiGate(IExperimentStartSceneRealignmentUiGate gate)
        {
            if (ReferenceEquals(_realignmentUiGate, gate))
            {
                _realignmentUiGate = null;
            }
        }

        public bool RequestRealignment(ExperimentStartSceneAlignmentReason reason)
        {
            bool inStartScene = IsStartSceneActive();
            bool internalSuppressed = IsInternalEventSuppressed;
            ExperimentStartSceneRecenterSignalDisposition disposition = EnsureRecenterTracker().RegisterSignal(
                reason,
                inStartScene,
                internalSuppressed);

            string normalizedReason = FormatReason(reason);
            if (disposition == ExperimentStartSceneRecenterSignalDisposition.IgnoredWrongScene)
            {
                LogEvent(
                    "start_scene_recenter_ignored_wrong_scene",
                    $"source={normalizedReason} scene={SceneManager.GetActiveScene().name}");
                return false;
            }

            if (disposition == ExperimentStartSceneRecenterSignalDisposition.InternalEventSuppressed)
            {
                _suppressedInternalEventCount++;
                LogEvent(
                    "start_scene_recenter_internal_event_suppressed",
                    $"source={normalizedReason} suppression_source={_internalSuppressionSource} " +
                    $"suppression_until={_internalEventSuppressionUntil:F3} suppressed_count={_suppressedInternalEventCount}");
                return false;
            }

            LogEvent(
                "start_scene_recenter_signal_detected",
                $"source={normalizedReason} generation={EnsureRecenterTracker().SignalGeneration} " +
                $"state={AlignmentState} application_focused={_applicationFocused} application_paused={_applicationPaused} " +
                $"camera={DescribePose(HeadCamera != null ? HeadCamera.transform : null)} anchor={DescribePose(_headPoseAnchor)}");

            if (!IsAlignmentComplete)
            {
                _trackingOriginChanged = true;
                LogEvent(
                    "start_scene_recenter_ignored_alignment_running",
                    $"source={normalizedReason} generation={EnsureRecenterTracker().SignalGeneration} " +
                    $"action={(IsAlignmentRunning ? "integrate_initial_alignment" : "defer_until_initial_alignment")}");
                return true;
            }

            if (IsFocusOnlyReason(reason) && !IsCameraOutsideTolerance())
            {
                EnsureRecenterTracker().DiscardScheduledSignals();
                LogEvent(
                    "start_scene_recenter_debounced",
                    $"source={normalizedReason} reason=camera_pose_already_within_tolerance generation={EnsureRecenterTracker().SignalGeneration}");
                return false;
            }

            if (disposition == ExperimentStartSceneRecenterSignalDisposition.PendingReplayRequested)
            {
                _recenterTrackingChanged = true;
                LogEvent(
                    "start_scene_recenter_pending_replay_requested",
                    $"source={normalizedReason} generation={EnsureRecenterTracker().SignalGeneration} " +
                    $"active_generation={EnsureRecenterTracker().ActiveGeneration} max_replays={_maxPendingRealignmentReplays}");
                return true;
            }

            if (disposition == ExperimentStartSceneRecenterSignalDisposition.Coalesced)
            {
                LogEvent(
                    "start_scene_recenter_signal_coalesced",
                    $"source={normalizedReason} generation={EnsureRecenterTracker().SignalGeneration} " +
                    $"grouped_signals={EnsureRecenterTracker().GroupedSignalCount}");
                return true;
            }

            if (_recenterCoroutine == null)
            {
                _recenterCoroutine = StartCoroutine(RealignAfterReferenceSpaceChange());
            }

            return true;
        }

        public void SetTrackingReadyForTests(bool ready)
        {
            _assumeTrackingReadyForTests = ready;
        }

        public void ConfigureRealignmentForTests(
            float trackingTimeoutSeconds = 0.1f,
            float debounceSeconds = 0f,
            int maxPendingReplays = 1)
        {
            _trackingTimeoutSeconds = Mathf.Max(0.01f, trackingTimeoutSeconds);
            _recenterDebounceSeconds = Mathf.Max(0f, debounceSeconds);
            _maxPendingRealignmentReplays = Mathf.Max(0, maxPendingReplays);
            _recenterRequests = new ExperimentStartSceneRecenterRequestTracker(_maxPendingRealignmentReplays);
        }

        public void SimulateTrackingOriginUpdatedForTests()
        {
            RegisterTrackingOriginUpdate();
        }

        public void SimulateApplicationFocusForTests(bool hasFocus)
        {
            OnApplicationFocus(hasFocus);
        }

        public void SimulateApplicationPauseForTests(bool paused)
        {
            OnApplicationPause(paused);
        }

        public void SetApplicationActiveForTests(bool focused, bool paused)
        {
            _applicationFocused = focused;
            _applicationPaused = paused;
        }

        public bool TryAlignCameraToAnchor(out float positionErrorMeters, out float yawErrorDegrees)
        {
            ResolveReferences();
            positionErrorMeters = float.PositiveInfinity;
            yawErrorDegrees = float.PositiveInfinity;
            if (_xrOrigin == null || _xrOrigin.Camera == null || _headPoseAnchor == null ||
                !ExperimentStartSceneXrPoseMath.TryGetHorizontalForward(_headPoseAnchor.forward, out Vector3 desiredForward))
            {
                return false;
            }

            bool yawAligned = _xrOrigin.MatchOriginUpCameraForward(Vector3.up, desiredForward);
            bool positionAligned = _xrOrigin.MoveCameraToWorldLocation(_headPoseAnchor.position);
            Physics.SyncTransforms();
            MeasureErrors(out positionErrorMeters, out yawErrorDegrees);
            return yawAligned && positionAligned;
        }

        /// <summary>Test seam for deterministic PlayMode coverage; production uses serialized scene references.</summary>
        public void ConfigureForTests(
            XROrigin xrOrigin,
            Transform headPoseAnchor,
            float trackingTimeoutSeconds = 0.1f,
            int stableFramesRequired = 1,
            int maxAlignmentAttempts = 2,
            int maxTrackingOriginUpdates = 2)
        {
            _xrOrigin = xrOrigin;
            _headPoseAnchor = headPoseAnchor;
            _trackingTimeoutSeconds = Mathf.Max(0.01f, trackingTimeoutSeconds);
            _stableFramesRequired = Mathf.Max(1, stableFramesRequired);
            _maxAlignmentAttempts = Mathf.Max(1, maxAlignmentAttempts);
            _maxTrackingOriginUpdates = Mathf.Max(0, maxTrackingOriginUpdates);
            _bypassSceneGuardForTests = true;
        }

        private IEnumerator AlignWhenTrackingReady()
        {
            float deadline = Time.realtimeSinceStartup + _trackingTimeoutSeconds;
            int stableFrames = 0;
            bool hasPreviousPose = false;
            Vector3 previousPosition = Vector3.zero;
            Quaternion previousRotation = Quaternion.identity;
            string readinessReason = "active_xr_input_subsystem_missing";
            bool trackingOriginModeLogged = false;
            bool unknownTrackingModesLogged = false;
            bool trackingStable = false;

            while (Time.realtimeSinceStartup <= deadline)
            {
                if (_assumeTrackingReadyForTests)
                {
                    stableFrames++;
                    if (stableFrames >= _stableFramesRequired)
                    {
                        readinessReason = string.Empty;
                        trackingStable = true;
                        break;
                    }

                    yield return null;
                    continue;
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
                if (!trackingOriginModeLogged)
                {
                    trackingOriginModeLogged = TryRequestFloorTrackingOrigin(subsystem);
                    if (!trackingOriginModeLogged && !unknownTrackingModesLogged)
                    {
                        unknownTrackingModesLogged = true;
                        LogEvent(
                            "start_scene_tracking_origin_mode",
                            "supported_modes=Unknown requested_mode=Floor request_succeeded=false " +
                            "effective_mode=Unknown fallback=defer_until_subsystem_reports_supported_modes");
                    }

                    if (!trackingOriginModeLogged)
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
                    readinessReason = string.Empty;
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
                CompleteFailure($"tracking_timeout:{timeoutReason}");
                yield break;
            }

            if (_subscribedSubsystem != null)
            {
                TrackingOriginModeFlags effective = _subscribedSubsystem.GetTrackingOriginMode();
                LogEvent(
                    "start_scene_tracking_origin_mode",
                    $"supported_modes={_subscribedSubsystem.GetSupportedTrackingOriginModes()} requested_mode=Floor " +
                    $"effective_mode={effective} fallback={(effective == TrackingOriginModeFlags.Floor ? "none" : $"effective_{effective}")} " +
                    "source=tracking_stabilized");
            }

            AlignmentState = ExperimentStartSceneAlignmentState.Aligning;
            for (int attempt = 1; attempt <= _maxAlignmentAttempts; attempt++)
            {
                AlignmentAttemptCount = attempt;
                Transform cameraTransform = _xrOrigin.Camera.transform;
                string beforeWorldPose = DescribePose(cameraTransform);
                string beforeLocalPose = DescribeLocalPose(cameraTransform);
                BeginInternalEventSuppression("initial_xr_origin_alignment_apis");
                bool apiSucceeded;
                float immediatePositionError;
                float immediateYawError;
                try
                {
                    apiSucceeded = TryAlignCameraToAnchor(out immediatePositionError, out immediateYawError);
                }
                finally
                {
                    EndInternalEventSuppression();
                }
                LogEvent(
                    "start_scene_xr_alignment_attempt",
                    $"attempt={attempt} api_succeeded={apiSucceeded} camera_before={beforeWorldPose} " +
                    $"camera_local_before={beforeLocalPose} camera_after_immediate={DescribePose(cameraTransform)} " +
                    $"camera_local_after_immediate={DescribeLocalPose(cameraTransform)} anchor={DescribePose(_headPoseAnchor)} " +
                    $"position_error_m={immediatePositionError:F6} yaw_error_deg={immediateYawError:F4} " +
                    $"tracking_origin_updates={TrackingOriginUpdateCount}");

                AlignmentState = ExperimentStartSceneAlignmentState.WaitingForVerification;
                if (Application.isBatchMode)
                {
                    yield return null;
                }
                else
                {
                    yield return new WaitForEndOfFrame();
                }
                MeasureErrors(out float positionError, out float yawError);
                FinalPositionErrorMeters = positionError;
                FinalYawErrorDegrees = yawError;
                if (_trackingOriginChanged)
                {
                    _trackingOriginChanged = false;
                    continue;
                }

                if (apiSucceeded &&
                    ExperimentStartSceneXrPoseMath.IsWithinTolerance(
                        positionError,
                        yawError,
                        _positionToleranceMeters,
                        _yawToleranceDegrees))
                {
                    CompleteSuccess();
                    yield break;
                }
            }

            CompleteFailure("alignment_tolerance_not_reached");
        }

        private bool TryRequestFloorTrackingOrigin(XRInputSubsystem subsystem)
        {
            TrackingOriginModeFlags supported = subsystem.GetSupportedTrackingOriginModes();
            if (supported == TrackingOriginModeFlags.Unknown)
            {
                return false;
            }

            TrackingOriginModeFlags before = subsystem.GetTrackingOriginMode();
            bool floorSupported = (supported & TrackingOriginModeFlags.Floor) != 0;
            bool requestSucceeded = false;
            BeginInternalEventSuppression("floor_tracking_origin_request");
            try
            {
                requestSucceeded = floorSupported && subsystem.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
            }
            finally
            {
                EndInternalEventSuppression();
            }
            TrackingOriginModeFlags effective = subsystem.GetTrackingOriginMode();
            string fallback = floorSupported
                ? requestSucceeded ? "none" : $"request_failed_keep_{effective}"
                : $"floor_unsupported_keep_{effective}";

            LogEvent(
                "start_scene_tracking_origin_mode",
                $"supported_modes={supported} requested_mode=Floor mode_before={before} request_succeeded={requestSucceeded} " +
                $"effective_mode={effective} fallback={fallback}");
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
            if (IsInternalEventSuppressed)
            {
                RequestRealignment(ExperimentStartSceneAlignmentReason.TrackingOriginUpdate);
                return;
            }

            if (IsAlignmentRunning && !IsAlignmentComplete)
            {
                if (TrackingOriginUpdateCount < _maxTrackingOriginUpdates)
                {
                    TrackingOriginUpdateCount++;
                    _trackingOriginChanged = true;
                }

                LogEvent(
                    "start_scene_tracking_origin_mode",
                    $"update_received=true phase=initial_alignment update_count={TrackingOriginUpdateCount} " +
                    $"update_limit={_maxTrackingOriginUpdates} effective_mode={ReadEffectiveTrackingOriginMode()}");
                RequestRealignment(ExperimentStartSceneAlignmentReason.TrackingOriginUpdate);
                return;
            }

            RequestRealignment(ExperimentStartSceneAlignmentReason.TrackingOriginUpdate);
        }

        private IEnumerator RealignAfterReferenceSpaceChange()
        {
            ExperimentStartSceneRecenterRequestTracker tracker = EnsureRecenterTracker();
            bool continueWithReplay;
            do
            {
                AlignmentState = ExperimentStartSceneAlignmentState.PendingRealignment;
                float debounceUntil = Time.realtimeSinceStartup + _recenterDebounceSeconds;
                while (Time.realtimeSinceStartup < debounceUntil)
                {
                    yield return null;
                }

                float focusDeadline = Time.realtimeSinceStartup + _trackingTimeoutSeconds;
                while ((_applicationPaused || !_applicationFocused) && Time.realtimeSinceStartup <= focusDeadline)
                {
                    yield return null;
                }

                ExperimentStartSceneRecenterOperation operation = tracker.BeginOperation();
                if (!operation.IsValid)
                {
                    break;
                }

                RecenterOperationCount++;
                _recenterOperationSequence++;
                IsRealignmentRunning = true;
                _recenterTrackingChanged = false;
                LastRealignmentReason = operation.Reasons;
                AlignmentState = ExperimentStartSceneAlignmentState.WaitingForTracking;
                float operationStartedAt = Time.realtimeSinceStartup;
                string cameraBefore = DescribePose(HeadCamera != null ? HeadCamera.transform : null);
                string cameraLocalBefore = DescribeLocalPose(HeadCamera != null ? HeadCamera.transform : null);
                string originBefore = DescribePose(_xrOrigin != null ? _xrOrigin.transform : null);
                ExperimentStartSceneCanvasDiagnostics canvasBefore = CaptureCanvasDiagnostics();
                bool gateEntered = SafeEnterRealignmentUiGate(operation.Generation);

                LogEvent(
                    "start_scene_recenter_alignment_begin",
                    $"operation={_recenterOperationSequence} generation={operation.Generation} source={FormatReason(operation.Reasons)} " +
                    $"grouped_signals={operation.GroupedSignalCount} replay={operation.IsReplay} camera_before={cameraBefore} " +
                    $"camera_local_before={cameraLocalBefore} xr_origin_before={originBefore} anchor={DescribePose(_headPoseAnchor)} " +
                    $"canvas_before={canvasBefore.Pose} canvas_visible_before={canvasBefore.CanvasEnabled} " +
                    $"graphic_raycaster_before={canvasBefore.GraphicRaycasterEnabled} " +
                    $"tracked_raycaster_before={canvasBefore.TrackedDeviceGraphicRaycasterEnabled} gate_entered={gateEntered}");

                bool trackingStable = false;
                string failureReason = string.Empty;
                if (_applicationPaused || !_applicationFocused)
                {
                    failureReason = "application_focus_recovery_timeout";
                }
                else
                {
                    yield return WaitForStableTrackingForRealignment(
                        operation,
                        (success, reason) =>
                        {
                            trackingStable = success;
                            failureReason = reason;
                        });
                }

                bool alignmentSucceeded = false;
                int attempts = 0;
                if (trackingStable)
                {
                    AlignmentState = ExperimentStartSceneAlignmentState.Aligning;
                    for (int attempt = 1; attempt <= _maxAlignmentAttempts; attempt++)
                    {
                        attempts = attempt;
                        string attemptCameraBefore = DescribePose(HeadCamera != null ? HeadCamera.transform : null);
                        string attemptLocalBefore = DescribeLocalPose(HeadCamera != null ? HeadCamera.transform : null);
                        string attemptOriginBefore = DescribePose(_xrOrigin != null ? _xrOrigin.transform : null);
                        BeginInternalEventSuppression("xr_origin_alignment_apis");
                        bool apiSucceeded;
                        float immediatePositionError;
                        float immediateYawError;
                        try
                        {
                            apiSucceeded = TryAlignCameraToAnchor(out immediatePositionError, out immediateYawError);
                        }
                        catch (Exception exception)
                        {
                            apiSucceeded = false;
                            immediatePositionError = float.PositiveInfinity;
                            immediateYawError = float.PositiveInfinity;
                            failureReason = "alignment_exception:" + exception.GetType().Name;
                        }
                        finally
                        {
                            EndInternalEventSuppression();
                        }

                        LogEvent(
                            "start_scene_recenter_alignment_attempt",
                            $"operation={_recenterOperationSequence} generation={operation.Generation} attempt={attempt} " +
                            $"api_succeeded={apiSucceeded} camera_before={attemptCameraBefore} camera_local_before={attemptLocalBefore} " +
                            $"xr_origin_before={attemptOriginBefore} camera_after_immediate={DescribePose(HeadCamera != null ? HeadCamera.transform : null)} " +
                            $"xr_origin_after_immediate={DescribePose(_xrOrigin != null ? _xrOrigin.transform : null)} " +
                            $"position_error_m={SanitizeError(immediatePositionError):F6} yaw_error_deg={SanitizeError(immediateYawError):F4}");

                        AlignmentState = ExperimentStartSceneAlignmentState.WaitingForVerification;
                        if (Application.isBatchMode)
                        {
                            yield return null;
                        }
                        else
                        {
                            yield return new WaitForEndOfFrame();
                        }
                        MeasureErrors(out float verifiedPositionError, out float verifiedYawError);
                        FinalPositionErrorMeters = verifiedPositionError;
                        FinalYawErrorDegrees = verifiedYawError;
                        if (apiSucceeded && ExperimentStartSceneXrPoseMath.IsWithinTolerance(
                                verifiedPositionError,
                                verifiedYawError,
                                _positionToleranceMeters,
                                _yawToleranceDegrees))
                        {
                            alignmentSucceeded = true;
                            failureReason = string.Empty;
                            break;
                        }

                        if (string.IsNullOrEmpty(failureReason))
                        {
                            failureReason = "alignment_tolerance_not_reached";
                        }
                    }
                }

                if (string.IsNullOrEmpty(failureReason) && !alignmentSucceeded)
                {
                    failureReason = trackingStable ? "alignment_tolerance_not_reached" : "tracking_timeout";
                }

                MeasureErrors(out float terminalPositionError, out float terminalYawError);
                FinalPositionErrorMeters = terminalPositionError;
                FinalYawErrorDegrees = terminalYawError;

                bool canvasRepositioned = SafeRepositionCanvas(operation.Generation);
                yield return null;
                ExperimentStartSceneCanvasDiagnostics canvasAfter = CaptureCanvasDiagnostics();
                SafeReleaseRealignmentUiGate(
                    alignmentSucceeded ? "alignment_succeeded" : failureReason,
                    alignmentSucceeded);
                ExperimentStartSceneCanvasDiagnostics canvasReleased = CaptureCanvasDiagnostics();
                RecenterGateReleaseCount++;
                float duration = Time.realtimeSinceStartup - operationStartedAt;
                string terminalEvent = alignmentSucceeded
                    ? "start_scene_recenter_alignment_succeeded"
                    : "start_scene_recenter_alignment_failed";
                LogEvent(
                    terminalEvent,
                    $"operation={_recenterOperationSequence} generation={operation.Generation} source={FormatReason(operation.Reasons)} " +
                    $"attempts={attempts} duration_seconds={duration:F3} result={(alignmentSucceeded ? "succeeded" : "failed")} " +
                    $"reason={failureReason} camera_after={DescribePose(HeadCamera != null ? HeadCamera.transform : null)} " +
                    $"camera_local_after={DescribeLocalPose(HeadCamera != null ? HeadCamera.transform : null)} " +
                    $"xr_origin_after={DescribePose(_xrOrigin != null ? _xrOrigin.transform : null)} anchor={DescribePose(_headPoseAnchor)} " +
                    $"position_error_m={SanitizeError(FinalPositionErrorMeters):F6} yaw_error_deg={SanitizeError(FinalYawErrorDegrees):F4} " +
                    $"canvas_repositioned={canvasRepositioned} canvas_after={canvasAfter.Pose} " +
                    $"canvas_position_error_m={SanitizeError(canvasAfter.PositionErrorMeters):F6} " +
                    $"canvas_yaw_error_deg={SanitizeError(canvasAfter.YawErrorDegrees):F4} fallback={(alignmentSucceeded ? "none" : "release_ui_fail_soft")}",
                    warning: !alignmentSucceeded);
                LogEvent(
                    "start_scene_recenter_alignment_gate_released",
                    $"operation={_recenterOperationSequence} generation={operation.Generation} succeeded={alignmentSucceeded} " +
                    $"canvas_visible={canvasReleased.CanvasEnabled} graphic_raycaster={canvasReleased.GraphicRaycasterEnabled} " +
                    $"tracked_raycaster={canvasReleased.TrackedDeviceGraphicRaycasterEnabled} ray_interactors={canvasReleased.RayInteractorsActive} " +
                    $"duration_seconds={duration:F3}");

                AlignmentState = alignmentSucceeded
                    ? ExperimentStartSceneAlignmentState.Completed
                    : ExperimentStartSceneAlignmentState.Failed;
                IsRealignmentRunning = false;
                continueWithReplay = tracker.CompleteOperation();
                if (continueWithReplay)
                {
                    LogEvent(
                        "start_scene_recenter_pending_replay_started",
                        $"completed_generation={operation.Generation} latest_generation={tracker.SignalGeneration} " +
                        $"replay_count={tracker.ReplayCount} max_replays={_maxPendingRealignmentReplays}");
                }
            }
            while (continueWithReplay && IsStartSceneActive());

            EnsureRecenterTracker().ResetBatchIfIdle();
            _recenterCoroutine = null;
        }

        private IEnumerator WaitForStableTrackingForRealignment(
            ExperimentStartSceneRecenterOperation operation,
            Action<bool, string> completed)
        {
            float deadline = Time.realtimeSinceStartup + _trackingTimeoutSeconds;
            int stableFrames = 0;
            bool hasPreviousPose = false;
            Vector3 previousPosition = Vector3.zero;
            Quaternion previousRotation = Quaternion.identity;
            string readinessReason = "active_xr_input_subsystem_missing";
            bool trackingOriginModeResolved = false;

            while (Time.realtimeSinceStartup <= deadline)
            {
                if (_applicationPaused || !_applicationFocused)
                {
                    readinessReason = "application_not_focused";
                    stableFrames = 0;
                    yield return null;
                    continue;
                }

                if (_assumeTrackingReadyForTests)
                {
                    stableFrames++;
                    if (stableFrames >= _stableFramesRequired)
                    {
                        completed(true, string.Empty);
                        yield break;
                    }

                    yield return null;
                    continue;
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
                    trackingOriginModeResolved = TryRequestFloorTrackingOrigin(subsystem);
                    if (!trackingOriginModeResolved)
                    {
                        readinessReason = "tracking_origin_modes_unknown";
                        yield return null;
                        continue;
                    }

                    LogEvent(
                        "start_scene_tracking_origin_mode",
                        $"source=recenter generation={operation.Generation} supported_modes={subsystem.GetSupportedTrackingOriginModes()} " +
                        $"requested_mode=Floor effective_mode={subsystem.GetTrackingOriginMode()}");
                }

                if (!TryReadTrackedHeadPose(out Vector3 localPosition, out Quaternion localRotation, out readinessReason))
                {
                    stableFrames = 0;
                    hasPreviousPose = false;
                    yield return null;
                    continue;
                }

                if (_recenterTrackingChanged)
                {
                    _recenterTrackingChanged = false;
                    stableFrames = 0;
                    hasPreviousPose = false;
                    yield return null;
                    continue;
                }

                stableFrames = hasPreviousPose &&
                    Vector3.Distance(previousPosition, localPosition) <= _stablePositionDeltaMeters &&
                    Quaternion.Angle(previousRotation, localRotation) <= _stableRotationDeltaDegrees
                        ? stableFrames + 1
                        : 1;
                previousPosition = localPosition;
                previousRotation = localRotation;
                hasPreviousPose = true;
                if (stableFrames >= _stableFramesRequired)
                {
                    completed(true, string.Empty);
                    yield break;
                }

                yield return null;
            }

            completed(false, "tracking_timeout:" + readinessReason);
        }

        private void OnApplicationPause(bool paused)
        {
            _applicationPaused = paused;
            if (!paused)
            {
                RequestRealignment(ExperimentStartSceneAlignmentReason.ApplicationResume);
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            _applicationFocused = hasFocus;
            if (hasFocus)
            {
                RequestRealignment(ExperimentStartSceneAlignmentReason.ApplicationFocusRegained);
            }
        }

        private bool IsStartSceneActive()
        {
            return _bypassSceneGuardForTests ||
                string.Equals(SceneManager.GetActiveScene().name, StartSceneName, StringComparison.Ordinal);
        }

        private bool IsCameraOutsideTolerance()
        {
            MeasureErrors(out float positionError, out float yawError);
            return !ExperimentStartSceneXrPoseMath.IsWithinTolerance(
                positionError,
                yawError,
                _positionToleranceMeters,
                _yawToleranceDegrees);
        }

        private static bool IsFocusOnlyReason(ExperimentStartSceneAlignmentReason reason)
        {
            ExperimentStartSceneAlignmentReason focusReasons =
                ExperimentStartSceneAlignmentReason.ApplicationFocusRegained |
                ExperimentStartSceneAlignmentReason.ApplicationResume;
            return reason != ExperimentStartSceneAlignmentReason.None && (reason & ~focusReasons) == 0;
        }

        private bool IsInternalEventSuppressed =>
            _internalEventSuppressionDepth > 0 || Time.realtimeSinceStartup < _internalEventSuppressionUntil;

        private void BeginInternalEventSuppression(string source)
        {
            _internalEventSuppressionDepth++;
            _internalSuppressionSource = source ?? string.Empty;
        }

        private void EndInternalEventSuppression()
        {
            _internalEventSuppressionDepth = Mathf.Max(0, _internalEventSuppressionDepth - 1);
            _internalEventSuppressionUntil = Mathf.Max(
                _internalEventSuppressionUntil,
                Time.realtimeSinceStartup + _internalEventSuppressionSeconds);
        }

        private ExperimentStartSceneRecenterRequestTracker EnsureRecenterTracker()
        {
            _recenterRequests ??= new ExperimentStartSceneRecenterRequestTracker(_maxPendingRealignmentReplays);
            return _recenterRequests;
        }

        private void TrySubscribeToActiveInputSubsystem()
        {
            XRInputSubsystem subsystem = ResolveActiveInputSubsystem();
            if (subsystem != null)
            {
                SubscribeTrackingOriginUpdates(subsystem);
            }
        }

        private string ReadEffectiveTrackingOriginMode()
        {
            return _subscribedSubsystem != null
                ? _subscribedSubsystem.GetTrackingOriginMode().ToString()
                : "test_or_unavailable";
        }

        private bool SafeEnterRealignmentUiGate(int generation)
        {
            if (_realignmentUiGate == null)
            {
                LogEvent(
                    "start_scene_recenter_alignment_failed",
                    $"generation={generation} reason=start_scene_ui_gate_missing fallback=continue_camera_alignment",
                    warning: true);
                return false;
            }

            try
            {
                _realignmentUiGate.EnterRealignmentGate(generation);
                return true;
            }
            catch (Exception exception)
            {
                LogEvent(
                    "start_scene_recenter_alignment_failed",
                    $"generation={generation} reason=ui_gate_enter_exception exception={exception.GetType().Name} fallback=continue_camera_alignment",
                    warning: true);
                return false;
            }
        }

        private bool SafeRepositionCanvas(int generation)
        {
            if (_realignmentUiGate == null)
            {
                return false;
            }

            try
            {
                _realignmentUiGate.RepositionCanvasAfterAlignment(generation);
                return true;
            }
            catch (Exception exception)
            {
                LogEvent(
                    "start_scene_recenter_alignment_failed",
                    $"generation={generation} reason=canvas_reposition_exception exception={exception.GetType().Name} fallback=release_ui_fail_soft",
                    warning: true);
                return false;
            }
        }

        private void SafeReleaseRealignmentUiGate(string reason, bool alignmentSucceeded)
        {
            if (_realignmentUiGate == null)
            {
                return;
            }

            try
            {
                _realignmentUiGate.ReleaseRealignmentGate(reason ?? string.Empty, alignmentSucceeded);
            }
            catch (Exception exception)
            {
                LogEvent(
                    "start_scene_recenter_alignment_failed",
                    $"reason=ui_gate_release_exception exception={exception.GetType().Name} fallback=ui_component_self_restores_on_disable",
                    warning: true);
            }
        }

        private ExperimentStartSceneCanvasDiagnostics CaptureCanvasDiagnostics()
        {
            if (_realignmentUiGate == null)
            {
                return ExperimentStartSceneCanvasDiagnostics.Missing;
            }

            try
            {
                return _realignmentUiGate.CaptureDiagnostics();
            }
            catch (Exception)
            {
                return ExperimentStartSceneCanvasDiagnostics.Missing;
            }
        }

        private static float SanitizeError(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? -1f : value;
        }

        private static string FormatReason(ExperimentStartSceneAlignmentReason reason)
        {
            if (reason == ExperimentStartSceneAlignmentReason.None)
            {
                return "none";
            }

            var values = new List<string>();
            if (reason.HasFlag(ExperimentStartSceneAlignmentReason.TrackingOriginUpdate))
            {
                values.Add("tracking_origin_update");
            }

            if (reason.HasFlag(ExperimentStartSceneAlignmentReason.ApplicationFocusRegained))
            {
                values.Add("application_focus_regained");
            }

            if (reason.HasFlag(ExperimentStartSceneAlignmentReason.ApplicationResume))
            {
                values.Add("application_resume");
            }

            if (reason.HasFlag(ExperimentStartSceneAlignmentReason.PoseDriftObserved))
            {
                values.Add("pose_drift_observed");
            }

            return string.Join("+", values);
        }

        private void MeasureErrors(out float positionErrorMeters, out float yawErrorDegrees)
        {
            Transform cameraTransform = _xrOrigin != null && _xrOrigin.Camera != null
                ? _xrOrigin.Camera.transform
                : null;
            if (cameraTransform == null || _headPoseAnchor == null)
            {
                positionErrorMeters = float.PositiveInfinity;
                yawErrorDegrees = float.PositiveInfinity;
                return;
            }

            positionErrorMeters = Vector3.Distance(cameraTransform.position, _headPoseAnchor.position);
            yawErrorDegrees = ExperimentStartSceneXrPoseMath.CalculateHorizontalYawErrorDegrees(
                cameraTransform.forward,
                _headPoseAnchor.forward);
        }

        private void CompleteSuccess()
        {
            AlignmentSucceeded = true;
            FailureReason = string.Empty;
            AlignmentState = ExperimentStartSceneAlignmentState.Completed;
            LogEvent(
                "start_scene_xr_alignment_succeeded",
                $"attempts={AlignmentAttemptCount} tracking_origin_updates={TrackingOriginUpdateCount} " +
                $"camera_after={DescribePose(_xrOrigin.Camera.transform)} camera_local_after={DescribeLocalPose(_xrOrigin.Camera.transform)} " +
                $"anchor={DescribePose(_headPoseAnchor)} position_error_m={FinalPositionErrorMeters:F6} " +
                $"yaw_error_deg={FinalYawErrorDegrees:F4}");
            CompleteTerminalState();
        }

        private void CompleteFailure(string reason)
        {
            AlignmentSucceeded = false;
            FailureReason = reason ?? "unknown";
            AlignmentState = ExperimentStartSceneAlignmentState.Failed;
            MeasureErrors(out float positionError, out float yawError);
            FinalPositionErrorMeters = positionError;
            FinalYawErrorDegrees = yawError;
            LogEvent(
                "start_scene_xr_alignment_failed",
                $"reason={FailureReason} attempts={AlignmentAttemptCount} tracking_origin_updates={TrackingOriginUpdateCount} " +
                $"camera_after={(HeadCamera != null ? DescribePose(HeadCamera.transform) : "missing")} " +
                $"camera_local_after={(HeadCamera != null ? DescribeLocalPose(HeadCamera.transform) : "missing")} " +
                $"anchor={(_headPoseAnchor != null ? DescribePose(_headPoseAnchor) : "missing")} " +
                $"position_error_m={FinalPositionErrorMeters:F6} yaw_error_deg={FinalYawErrorDegrees:F4} fallback=release_ui_fail_soft",
                warning: true);
            CompleteTerminalState();
        }

        private void CompleteTerminalState()
        {
            IsAlignmentRunning = false;
            IsAlignmentComplete = true;
            _alignmentCoroutine = null;
            AlignmentFinished?.Invoke(this);

            ExperimentStartSceneRecenterRequestTracker tracker = EnsureRecenterTracker();
            if (tracker.HasScheduledSignals)
            {
                if (!IsCameraOutsideTolerance())
                {
                    LogEvent(
                        "start_scene_recenter_debounced",
                        $"source=initial_alignment_terminal reason=camera_pose_already_within_tolerance generation={tracker.SignalGeneration}");
                    tracker.DiscardScheduledSignals();
                }
                else if (_recenterCoroutine == null)
                {
                    _recenterCoroutine = StartCoroutine(RealignAfterReferenceSpaceChange());
                }
            }
        }

        private void ResetResult()
        {
            IsAlignmentComplete = false;
            AlignmentSucceeded = false;
            FailureReason = string.Empty;
            AlignmentAttemptCount = 0;
            TrackingOriginUpdateCount = 0;
            FinalPositionErrorMeters = float.PositiveInfinity;
            FinalYawErrorDegrees = float.PositiveInfinity;
            _trackingOriginChanged = false;
        }

        private void ResolveReferences()
        {
            _xrOrigin ??= FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
            if (_headPoseAnchor == null)
            {
                GameObject anchor = GameObject.Find("StartSceneHeadPoseAnchor");
                _headPoseAnchor = anchor != null ? anchor.transform : null;
            }
        }

        private static string DescribePose(Transform value)
        {
            return value == null
                ? "missing"
                : $"pos({value.position.x:F5},{value.position.y:F5},{value.position.z:F5})_rot({value.eulerAngles.x:F3},{value.eulerAngles.y:F3},{value.eulerAngles.z:F3})";
        }

        private static string DescribeLocalPose(Transform value)
        {
            return value == null
                ? "missing"
                : $"pos({value.localPosition.x:F5},{value.localPosition.y:F5},{value.localPosition.z:F5})_rot({value.localEulerAngles.x:F3},{value.localEulerAngles.y:F3},{value.localEulerAngles.z:F3})";
        }

        private static void LogEvent(string eventName, string details, bool warning = false)
        {
            string message = $"{LogPrefix} {eventName} | {details}";
            if (warning)
            {
                Debug.LogWarning(message);
            }
            else
            {
                Debug.Log(message);
            }
        }
    }

    public enum ExperimentStartSceneAlignmentState
    {
        Idle,
        WaitingForTracking,
        Aligning,
        WaitingForVerification,
        Completed,
        Failed,
        PendingRealignment
    }

    [Flags]
    public enum ExperimentStartSceneAlignmentReason
    {
        None = 0,
        TrackingOriginUpdate = 1 << 0,
        ApplicationFocusRegained = 1 << 1,
        ApplicationResume = 1 << 2,
        PoseDriftObserved = 1 << 3
    }

    public enum ExperimentStartSceneRecenterSignalDisposition
    {
        Scheduled,
        Coalesced,
        PendingReplayRequested,
        IgnoredWrongScene,
        InternalEventSuppressed
    }

    public readonly struct ExperimentStartSceneRecenterOperation
    {
        public ExperimentStartSceneRecenterOperation(
            int generation,
            ExperimentStartSceneAlignmentReason reasons,
            int groupedSignalCount,
            bool isReplay)
        {
            Generation = generation;
            Reasons = reasons;
            GroupedSignalCount = groupedSignalCount;
            IsReplay = isReplay;
        }

        public int Generation { get; }
        public ExperimentStartSceneAlignmentReason Reasons { get; }
        public int GroupedSignalCount { get; }
        public bool IsReplay { get; }
        public bool IsValid => Generation > 0 && Reasons != ExperimentStartSceneAlignmentReason.None;
    }

    public sealed class ExperimentStartSceneRecenterRequestTracker
    {
        private readonly int _maxPendingReplays;
        private ExperimentStartSceneAlignmentReason _scheduledReasons;
        private ExperimentStartSceneAlignmentReason _pendingReasons;
        private int _scheduledSignalCount;
        private int _pendingSignalCount;
        private bool _operationRunning;
        private bool _pendingReplay;

        public ExperimentStartSceneRecenterRequestTracker(int maxPendingReplays = 1)
        {
            _maxPendingReplays = Math.Max(0, maxPendingReplays);
        }

        public int SignalGeneration { get; private set; }
        public int ActiveGeneration { get; private set; }
        public int AppliedGeneration { get; private set; }
        public int ReplayCount { get; private set; }
        public int GroupedSignalCount => _scheduledSignalCount;
        public bool HasScheduledSignals => _scheduledSignalCount > 0;
        public bool IsOperationRunning => _operationRunning;
        public bool HasPendingReplay => _pendingReplay;

        public ExperimentStartSceneRecenterSignalDisposition RegisterSignal(
            ExperimentStartSceneAlignmentReason reason,
            bool inStartScene,
            bool internalSuppressed)
        {
            if (!inStartScene)
            {
                return ExperimentStartSceneRecenterSignalDisposition.IgnoredWrongScene;
            }

            if (internalSuppressed)
            {
                return ExperimentStartSceneRecenterSignalDisposition.InternalEventSuppressed;
            }

            SignalGeneration++;
            if (_operationRunning)
            {
                _pendingReasons |= reason;
                _pendingSignalCount++;
                bool firstPending = !_pendingReplay;
                _pendingReplay = true;
                return firstPending
                    ? ExperimentStartSceneRecenterSignalDisposition.PendingReplayRequested
                    : ExperimentStartSceneRecenterSignalDisposition.Coalesced;
            }

            bool firstScheduled = _scheduledSignalCount == 0;
            if (firstScheduled)
            {
                ReplayCount = 0;
            }

            _scheduledReasons |= reason;
            _scheduledSignalCount++;
            return firstScheduled
                ? ExperimentStartSceneRecenterSignalDisposition.Scheduled
                : ExperimentStartSceneRecenterSignalDisposition.Coalesced;
        }

        public ExperimentStartSceneRecenterOperation BeginOperation()
        {
            if (_operationRunning || _scheduledSignalCount <= 0)
            {
                return default;
            }

            _operationRunning = true;
            ActiveGeneration = SignalGeneration;
            ExperimentStartSceneRecenterOperation operation = new(
                ActiveGeneration,
                _scheduledReasons,
                _scheduledSignalCount,
                ReplayCount > 0);
            _scheduledReasons = ExperimentStartSceneAlignmentReason.None;
            _scheduledSignalCount = 0;
            return operation;
        }

        public bool CompleteOperation()
        {
            if (!_operationRunning)
            {
                return false;
            }

            _operationRunning = false;
            AppliedGeneration = ActiveGeneration;
            ActiveGeneration = 0;
            bool replay = _pendingReplay &&
                SignalGeneration > AppliedGeneration &&
                ReplayCount < _maxPendingReplays;
            if (replay)
            {
                ReplayCount++;
                _scheduledReasons = _pendingReasons;
                _scheduledSignalCount = Math.Max(1, _pendingSignalCount);
            }

            _pendingReplay = false;
            _pendingReasons = ExperimentStartSceneAlignmentReason.None;
            _pendingSignalCount = 0;
            return replay;
        }

        public void DiscardScheduledSignals()
        {
            _scheduledReasons = ExperimentStartSceneAlignmentReason.None;
            _scheduledSignalCount = 0;
            _pendingReasons = ExperimentStartSceneAlignmentReason.None;
            _pendingSignalCount = 0;
            _pendingReplay = false;
        }

        public void ResetBatchIfIdle()
        {
            if (!_operationRunning && _scheduledSignalCount == 0)
            {
                ReplayCount = 0;
                _pendingReplay = false;
                _pendingReasons = ExperimentStartSceneAlignmentReason.None;
                _pendingSignalCount = 0;
            }
        }
    }

    public readonly struct ExperimentStartSceneCanvasDiagnostics
    {
        public ExperimentStartSceneCanvasDiagnostics(
            string pose,
            bool canvasEnabled,
            bool graphicRaycasterEnabled,
            bool trackedDeviceGraphicRaycasterEnabled,
            bool rayInteractorsActive,
            float positionErrorMeters,
            float yawErrorDegrees)
        {
            Pose = pose ?? string.Empty;
            CanvasEnabled = canvasEnabled;
            GraphicRaycasterEnabled = graphicRaycasterEnabled;
            TrackedDeviceGraphicRaycasterEnabled = trackedDeviceGraphicRaycasterEnabled;
            RayInteractorsActive = rayInteractorsActive;
            PositionErrorMeters = positionErrorMeters;
            YawErrorDegrees = yawErrorDegrees;
        }

        public string Pose { get; }
        public bool CanvasEnabled { get; }
        public bool GraphicRaycasterEnabled { get; }
        public bool TrackedDeviceGraphicRaycasterEnabled { get; }
        public bool RayInteractorsActive { get; }
        public float PositionErrorMeters { get; }
        public float YawErrorDegrees { get; }

        public static ExperimentStartSceneCanvasDiagnostics Missing => new(
            "missing",
            false,
            false,
            false,
            false,
            float.PositiveInfinity,
            float.PositiveInfinity);
    }

    public interface IExperimentStartSceneRealignmentUiGate
    {
        void EnterRealignmentGate(int generation);
        void RepositionCanvasAfterAlignment(int generation);
        void ReleaseRealignmentGate(string reason, bool alignmentSucceeded);
        ExperimentStartSceneCanvasDiagnostics CaptureDiagnostics();
    }

    public static class ExperimentStartSceneXrPoseMath
    {
        public static bool TryGetHorizontalForward(Vector3 forward, out Vector3 horizontalForward)
        {
            horizontalForward = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (horizontalForward.sqrMagnitude <= 0.000001f)
            {
                horizontalForward = Vector3.zero;
                return false;
            }

            horizontalForward.Normalize();
            return true;
        }

        public static float CalculateHorizontalYawErrorDegrees(Vector3 currentForward, Vector3 desiredForward)
        {
            if (!TryGetHorizontalForward(currentForward, out Vector3 current) ||
                !TryGetHorizontalForward(desiredForward, out Vector3 desired))
            {
                return 180f;
            }

            return Mathf.Abs(Vector3.SignedAngle(current, desired, Vector3.up));
        }

        public static bool IsWithinTolerance(
            float positionErrorMeters,
            float yawErrorDegrees,
            float positionToleranceMeters,
            float yawToleranceDegrees)
        {
            return positionErrorMeters <= positionToleranceMeters &&
                yawErrorDegrees <= yawToleranceDegrees;
        }
    }
}
