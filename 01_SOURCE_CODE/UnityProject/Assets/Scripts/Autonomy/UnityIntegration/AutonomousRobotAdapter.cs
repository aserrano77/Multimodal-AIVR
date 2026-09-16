using UnityEngine;
using Autonomy.Core;
using Autonomy.Domain;
using Autonomy.Services;
using Autonomy.BT.Core;
using Autonomy.BT.Composition;
using Autonomy.Integration;
using Autonomy.Provisioning;
using System;
using System.Collections.Generic;
using UnityEngine.Serialization;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Punto de entrada principal y Composition Root local para el módulo de autonomía en Unity.
    /// Se encarga de instanciar las piezas de C# puro, cablearlas y sincronizar el ciclo de vida.
    /// </summary>
    public class AutonomousRobotAdapter : MonoBehaviour
    {
        private enum NavigationMode
        {
            Stub,
            TiagoNavMeshGuided
        }

        private enum ManipulationMode
        {
            Stub,
            TiagoUnityScene
        }

        private sealed class StoppedTaskSnapshot
        {
            public string TaskInstanceId;
            public string RequestId;
            public string Producer;
            public string VoiceInteractionId;
            public TargetDescriptor PickupTarget;
            public TargetDescriptor PlaceTarget;
            public string Phase;
            public bool WasHoldingObject;
            public string HeldObjectId;
            public TaskStatus StatusBeforeStop;
            public RobotMode ModeBeforeStop;
        }

        [Header("Initial Bootstrapping (Integration Test)")]
        [SerializeField] private bool _seedInitialTarget = false;
        [SerializeField] private string _initialTargetId = "box_01";
        [SerializeField] private UnityEngine.Vector3 _initialTargetPosition = UnityEngine.Vector3.forward * 2f;
        [SerializeField] private bool _forceAutonomousOnStart = false;
        [SerializeField] private AutonomousTaskFlow _taskFlow = AutonomousTaskFlow.PickOnly;

        [Header("Autonomous Navigation (NavMesh-First Runtime)")]
        [SerializeField] private NavigationMode _navigationMode = NavigationMode.Stub;
        [SerializeField] private int _navigationTicks = 60;
        [SerializeField] private Transform _navigationReference;
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
        [SerializeField] private float _arrivalDistance = 0.35f;
        [SerializeField] private float _targetSampleRadius = 1.0f;
        [SerializeField] private float _waypointReachDistance = 0.25f;
        [SerializeField] private float _pathLookAheadDistance = 0.8f;
        [SerializeField] private float _slowdownDistance = 1.5f;
        [SerializeField] private float _maxLinearSpeed = 0.7f;
        [SerializeField] private float _maxAngularSpeed = 1.5f;
        [SerializeField] private float _accelerationLimit = 10f;
        [SerializeField] private float _decelerationLimit = 10f;
        [SerializeField] private float _angularGain = 2.5f;
        [SerializeField] private float _rotateInPlaceAngleDeg = 40f;
        [SerializeField] private float _headingOffsetDegrees = 0f;

        [Header("Obstacle Perception Diagnostics (No Reactive Bypass)")]
        [Tooltip("Three-ray perception distance used for diagnostics, emergency stop/stall context, and gizmos. It does not re-enable legacy reactive bypass.")]
        [SerializeField] private float _avoidanceDetectionDistance = 0.9f;
        [Tooltip("Three-ray spread used for obstacle perception diagnostics. The NavMesh-first path remains authoritative.")]
        [SerializeField] private float _avoidanceRayAngleDegrees = 30f;
        [HideInInspector]
        [SerializeField] private float _avoidanceAngularStrength = 1.0f;
        [HideInInspector]
        [SerializeField] private float _avoidanceLinearReductionFactor = 0.35f;
        [Tooltip("Layer mask for obstacle perception diagnostics and safety/stall context.")]
        [SerializeField] private LayerMask _avoidanceLayerMask = ~0;

        [Header("Path Geometry Pipeline (NavMesh -> Centering -> Clearance -> Smoothing -> Lock)")]
        [SerializeField] private bool _enablePathCornerSmoothing = true;
        [SerializeField] private float _cornerSmoothingAngleThresholdDeg = 22f;
        [SerializeField] private float _cornerSmoothingRadius = 0.40f;
        [SerializeField] private int _cornerSmoothingSamplesPerCorner = 8;
        [SerializeField] private float _cornerSmoothingMinSegmentLength = 0.50f;
        [SerializeField] private float _cornerSmoothingNavMeshSampleDistance = 0.20f;
        [SerializeField] private bool _cornerSmoothingValidateSegments = true;
        [SerializeField] private bool _cornerSmoothingClearanceAware = false;
        [SerializeField] private float _cornerSmoothingMinNavMeshEdgeClearance = 0.35f;
        [SerializeField] private float _cornerSmoothingMaxControlPointOffset = 0.75f;
        [SerializeField] private float _cornerSmoothingControlPointOffsetStep = 0.15f;
        [SerializeField] private int _cornerSmoothingMaxOffsetAttempts = 5;
        [SerializeField] private bool _useLastValidSmoothedPathOnSmoothingFailure = false;
        [SerializeField] private float _maxLastValidSmoothedPathAgeSeconds = 1.5f;
        [SerializeField] private float _maxLastValidSmoothedPathStartDistance = 0.75f;
        [SerializeField] private float _maxLastValidSmoothedPathTargetDistance = 0.75f;
        [SerializeField] private bool _lockActivePathDuringTracking = true;
        [SerializeField] private float _replanIfDistanceFromActivePathExceeds = 0.75f;
        [SerializeField] private float _replanIfTargetMovedMoreThan = 0.50f;
        [SerializeField] private float _minSecondsBetweenAutomaticReplans = 1.50f;
        [SerializeField] private bool _allowPeriodicReplanDuringTracking = false;
        [SerializeField] private bool _enablePathClearanceOffset = true;
        [SerializeField] private float _pathClearanceOffsetMinEdgeDistance = 0.28f;
        [SerializeField] private float _pathClearanceOffsetSearchRadius = 0.30f;
        [SerializeField] private float _pathClearanceOffsetStep = 0.05f;
        [SerializeField] private int _pathClearanceOffsetMaxCandidatesPerSide = 4;
        [SerializeField] private float _pathClearanceOffsetNavMeshSampleDistance = 0.15f;
        [SerializeField] private bool _pathClearanceOffsetValidateSegments = true;
        [SerializeField] private bool _pathClearanceOffsetSkipEndpoints = true;
        [SerializeField] private float _pathClearanceOffsetMinPointSpacing = 0.15f;
        [SerializeField] private bool _pathClearanceOffsetOnlyIfBelowMinEdgeDistance = true;
        [SerializeField] private float _pathClearanceOffsetMaxDeviationFromCenteredPath = 0.25f;
        [SerializeField] private float _pathClearanceOffsetMaxLocalHeadingChangeDeg = 25f;

        [Header("User Safety Stop")]
        [SerializeField] private bool _enableUserSafetyStop = true;
        [SerializeField] private bool _userSafetyStopDetectXRRig = true;
        [SerializeField] private bool _userSafetyStopUseIgnoreRaycastLayer = true;
        [SerializeField] private string[] _userSafetyStopRootNameContains =
        {
            "XR Origin",
            "XR Rig",
            "Hands",
            "Controller",
            "Main Camera",
            "Camera Offset"
        };
        [SerializeField] private float _userSafetyStopRadius = 0.75f;
        [SerializeField] private float _userSafetyStopPathLookaheadDistance = 1.00f;
        [SerializeField] private float _userSafetyResumeRadius = 0.95f;
        [SerializeField] private float _userSafetyStopTimeoutSeconds = 8.0f;
        [SerializeField] private bool _userSafetyStopCommandZeroVelocity = true;

        [Header("Autonomy Drive Profile")]
        [Tooltip("Public autonomous drive profile. Realistic maps to Safe policy; Arcade maps to FastDemo policy.")]
        [SerializeField] private TiagoDriveProfile _autonomyDriveProfile = TiagoDriveProfile.Realistic;
        [Tooltip("When enabled, resolves the public autonomous profile into runtime navigation limits and autonomous policy.")]
        [SerializeField] private bool _applyAutonomyProfile = true;

        [Header("Debug & Instrumentation")]
        [SerializeField] private bool _showHeartbeat = false;
        [SerializeField, FormerlySerializedAs("_showObstacleRays")]
        private bool _debugDrawObstacleRays = false;
        [SerializeField] private bool _debugDrawNavMeshPath = true;
        [SerializeField] private bool _debugDrawOnlyActivePath = true;
        [Tooltip("Legacy demo-only state tint. Keep disabled so the robot base retains its original material and gray color.")]
        [SerializeField] private bool _enableBaseStateTint = false;
        [SerializeField] private Renderer _robotVisualRenderer;
        [SerializeField] private int _manipulationTicks = 30;

        [Header("Autonomous Manipulation (Scene MVP)")]
        [SerializeField] private ManipulationMode _manipulationMode = ManipulationMode.TiagoUnityScene;
        [Tooltip("Preferred: assign an empty child under gripper_right_grasping_frame or gripper_left_grasping_frame. Avoid Visuals, Collisions, finger links, base_link, and NavigationReference except as a fallback.")]
        [SerializeField] private Transform _manipulationAnchor;
        [SerializeField] private float _manipulationRange = 1.25f;
        [SerializeField] private Vector3 _placeOffset = new Vector3(0f, 0.55f, 0f);
        [SerializeField] private bool _requirePlaceWithinRange = true;
        [SerializeField] private float _maxExpectedPlaceDistance = 1.25f;
        [SerializeField] private float _placeDistanceSafetyMargin = 0.05f;
        [SerializeField] private float _maxRelaxedPlaceDistance = 1.45f;
        [SerializeField] private PlaceFailureRecoveryMode _placeFailureRecoveryMode = PlaceFailureRecoveryMode.RelaxedRange;
        [SerializeField] private bool _disableXRGrabWhileHeld = true;
        [SerializeField] private bool _disableHeldObjectCollidersWhileHeld = true;
        [SerializeField] private HeldObjectAlignmentMode _heldObjectAlignmentMode = HeldObjectAlignmentMode.BoundsCenterToAnchorOffset;
        [SerializeField] private Vector3 _heldLocalPositionOffset = Vector3.zero;
        [SerializeField] private Vector3 _heldLocalEulerOffset = Vector3.zero;
        [SerializeField] private bool _enforceHeldPoseWhileHolding = true;
        [SerializeField] private float _heldPoseDriftWarningThreshold = 0.03f;
        [SerializeField] private float _heldPoseDriftCorrectionThreshold = 0.01f;
        [SerializeField] private bool _enableDepositedBoxNavMeshObstacle = true;
        [SerializeField] private Vector3 _depositedBoxObstacleSizePadding = new(0.08f, 0.02f, 0.08f);
        [SerializeField] private bool _depositedBoxObstacleCarve = true;
        [SerializeField] private float _depositedBoxObstacleCarveMoveThreshold = 0.05f;

        [Header("Manipulation Runtime Debug (Read Only)")]
        [SerializeField] private GameObject _heldObjectDebug;
        [SerializeField] private string _heldObjectIdDebug = string.Empty;
        [SerializeField] private string _manipulationStateDebug = "Unavailable";
        [SerializeField] private bool _debugDumpLastPlaceContextNow;

        // Referencias al runtime de autonomía (C# puro)
        private RobotBlackboard _blackboard;
        private RobotFSM _fsm;
        private RobotController _robotController;
        private ManualTargetProvisioner _targetSeeder;
        private PlaceTargetProvisioner _placeTargetSeeder;
        private SafetyServiceStub _safetyServiceStub;
        private int _frameCounter = 0;
        private TiagoDifferentialDriveBridge _driveBridge;
        private TiagoNavMeshNavigationService _tiagoNavMeshNavigationService;
        private TiagoUnityManipulationService _tiagoUnityManipulationService;
        private RobotAssistanceRoundCoordinator _assistanceRoundCoordinator;
        private float _lastGizmoPathSourceLogTime = float.NegativeInfinity;
        private string _activeP40TaskInstanceId = string.Empty;
        private string _activeP40RequestId = string.Empty;
        private string _activeP40Producer = "unknown";
        private string _activeP40VoiceInteractionId = string.Empty;
        private string _activeP40TargetId = string.Empty;
        private string _activeP40PlaceTargetId = string.Empty;
        private bool _activeP40TaskResumedFromStop;
        private TaskStatus _lastObservedP40TaskStatus = TaskStatus.None;
        private P40BPendingVoiceOrder _pendingP40BVoiceOrder;
        private bool _pendingP40BVoiceOrderPausedByStop;
        private StoppedTaskSnapshot _stoppedTaskSnapshot;
        private bool _voiceStopLatched;
        private bool _autonomyTickFaultLatched;

        /// <summary>
        /// Acceso controlado al sembrador de objetivos para la capa de demo.
        /// </summary>
        public ManualTargetProvisioner TargetSeeder => _targetSeeder;
        public PlaceTargetProvisioner PlaceTargetSeeder => _placeTargetSeeder;
        public SafetyServiceStub SafetyServiceStub => _safetyServiceStub;
        public IReadOnlyRobotBlackboard Blackboard => _blackboard;
        public RobotMode? AuthoritativeRobotMode => _robotController != null
            ? _robotController.CurrentMode
            : _fsm != null
                ? _fsm.CurrentMode
                : (RobotMode?)null;
        public AutonomousTaskFlow TaskFlow => _taskFlow;
        public Transform NavigationReference => _navigationReference != null ? _navigationReference : transform;
        public GameObject HeldObject => _tiagoUnityManipulationService != null ? _tiagoUnityManipulationService.HeldObject : null;
        public string HeldObjectId => _tiagoUnityManipulationService != null ? _tiagoUnityManipulationService.HeldObjectId : string.Empty;
        public string ManipulationState => _tiagoUnityManipulationService != null ? _tiagoUnityManipulationService.ManipulationState : "Unavailable";
        public string PlaceFailureRecoveryModeName => _placeFailureRecoveryMode.ToString();
        public string ActiveP40TaskInstanceId => _activeP40TaskInstanceId;
        public string ActiveP40RequestId => _activeP40RequestId;
        public string ActiveP40Producer => _activeP40Producer;
        public string ActiveP40TargetId => _activeP40TargetId;
        public string ActiveP40PlaceTargetId => _activeP40PlaceTargetId;
        public bool HasStoppedTaskSnapshotForVoiceResume => _stoppedTaskSnapshot != null;
        public bool HasStructurallyValidStoppedTaskSnapshotForVoiceResume =>
            IsStoppedTaskSnapshotStructurallyValid(_stoppedTaskSnapshot);
        public bool VoiceStopLatched => _voiceStopLatched;
        public bool AutonomyTickFaultLatched => _autonomyTickFaultLatched;

        public void SuspendForExperimentPause(string reason)
        {
            _tiagoNavMeshNavigationService?.SuspendForExperimentPause();
            _driveBridge?.Stop();
            Dictionary<string, object> payload = BuildP44IVoiceStopResumeStatePayload(reason ?? "experiment_pause");
            payload["is_experiment_paused"] = ExperimentRuntimePauseCoordinator.IsExperimentPaused;
            payload["suspension_kind"] = "non_destructive_global_pause";
            TiagoExperimentTelemetry.LogEvent("robot_runtime_suspended_for_experiment_pause", payload);
        }

        public void ResumeFromExperimentPause(string reason)
        {
            Dictionary<string, object> payload = BuildP44IVoiceStopResumeStatePayload(reason ?? "experiment_pause_resume");
            payload["is_experiment_paused"] = ExperimentRuntimePauseCoordinator.IsExperimentPaused;
            payload["resume_result"] = "controller_state_preserved_next_tick_continues";
            TiagoExperimentTelemetry.LogEvent("robot_runtime_resumed_from_experiment_pause", payload);
        }

        public bool TryGetSupervisoryStopRequestRejection(
            MultimodalTaskIntent intent,
            out string rejectionReason,
            out string requestSource)
        {
            requestSource = P40TraceContext.NormalizeProducer(intent?.Source);
            rejectionReason = string.Empty;
            if (!_voiceStopLatched || intent == null || intent.TaskFlow != AutonomousTaskFlow.PickAndPlace)
            {
                return false;
            }

            rejectionReason = string.Equals(requestSource, "voice_command", StringComparison.OrdinalIgnoreCase)
                ? "voice_stop_latched_pick_and_place_rejected"
                : string.Equals(requestSource, "assisted_navmesh_selection", StringComparison.OrdinalIgnoreCase)
                    ? "voice_stop_latched_auto_task_blocked"
                    : "voice_stop_latched_autonomy_request_blocked";
            return true;
        }

        public NodeStatus StepPostPlaceRetreat(UnityEngine.Vector3 targetPosition)
        {
            if (_tiagoNavMeshNavigationService == null)
            {
                return NodeStatus.Failure;
            }

            return _tiagoNavMeshNavigationService.MoveTo(new System.Numerics.Vector3(targetPosition.x, targetPosition.y, targetPosition.z));
        }

        public void StopPostPlaceRetreat()
        {
            _tiagoNavMeshNavigationService?.Stop();
            _driveBridge?.Stop();
        }

        public void PrepareForExperimentalPoseReset()
        {
            ClearExperimentRuntimeState("experimental_pose_reset");
        }

        public void ClearExperimentRuntimeState(string reason)
        {
            _tiagoNavMeshNavigationService?.Stop();
            _driveBridge?.Stop();
            _robotController?.ResetForExperimentTrial();
            _fsm?.TryChangeMode(RobotMode.Idle);
            _blackboard?.Remove(TaskBlackboardKeys.CurrentTarget);
            _blackboard?.Remove(TaskBlackboardKeys.PlaceTarget);
            _blackboard?.Remove(TaskBlackboardKeys.ResumeHeldObjectId);
            _blackboard?.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.None);
            _activeP40TaskInstanceId = string.Empty;
            _activeP40RequestId = string.Empty;
            _activeP40Producer = "unknown";
            _activeP40VoiceInteractionId = string.Empty;
            _activeP40TargetId = string.Empty;
            _activeP40PlaceTargetId = string.Empty;
            _activeP40TaskResumedFromStop = false;
            _lastObservedP40TaskStatus = TaskStatus.None;
            _pendingP40BVoiceOrder = null;
            _pendingP40BVoiceOrderPausedByStop = false;
            InvalidateStoppedTaskSnapshot(reason ?? "experiment_runtime_state_cleared");
            ZeroPhysicsVelocities(NavigationReference);
            if (NavigationReference != transform)
            {
                ZeroPhysicsVelocities(transform);
            }

            TiagoExperimentTelemetry.LogEvent(
                "robot_experiment_runtime_state_cleared",
                new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["held_object_id"] = HeldObjectId,
                    ["navigation_reference"] = NavigationReference != null ? NavigationReference.name : string.Empty
                });

            if (_autonomyTickFaultLatched)
            {
                _autonomyTickFaultLatched = false;
                TiagoExperimentTelemetry.LogEvent(
                    "autonomy_tick_fault_cleared",
                    new Dictionary<string, object>
                    {
                        ["reason"] = reason ?? string.Empty,
                        ["frame_count"] = Time.frameCount,
                        ["time_since_start"] = Time.time
                    });
            }
        }

        public bool ClearHeldObjectForExperimentBoundary(string reason)
        {
            string heldBefore = HeldObjectId;
            bool cleared = false;
            if (_tiagoUnityManipulationService != null)
            {
                cleared = _tiagoUnityManipulationService.AbortHeldObjectForExperimentBoundary(reason);
            }

            _blackboard?.Remove(TaskBlackboardKeys.ResumeHeldObjectId);
            _blackboard?.Remove(TaskBlackboardKeys.CurrentTarget);
            _blackboard?.Remove(TaskBlackboardKeys.PlaceTarget);
            _blackboard?.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.None);
            _activeP40TaskInstanceId = string.Empty;
            _activeP40RequestId = string.Empty;
            _activeP40Producer = "unknown";
            _activeP40VoiceInteractionId = string.Empty;
            _activeP40TargetId = string.Empty;
            _activeP40PlaceTargetId = string.Empty;
            _activeP40TaskResumedFromStop = false;
            _lastObservedP40TaskStatus = TaskStatus.None;
            _pendingP40BVoiceOrder = null;
            _pendingP40BVoiceOrderPausedByStop = false;
            InvalidateStoppedTaskSnapshot(reason ?? "experiment_boundary_held_object_cleared");

            TiagoExperimentTelemetry.LogEvent(
                "robot_experiment_boundary_held_object_cleared",
                new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["held_object_before"] = heldBefore,
                    ["held_object_after"] = HeldObjectId,
                    ["cleared"] = cleared
                });
            return cleared || !string.Equals(heldBefore, HeldObjectId, StringComparison.Ordinal);
        }

        private static void ZeroPhysicsVelocities(Transform root)
        {
            if (root == null)
            {
                return;
            }

            Rigidbody[] rigidbodies = root.GetComponentsInParent<Rigidbody>(true);
            foreach (Rigidbody body in rigidbodies)
            {
                if (body == null)
                {
                    continue;
                }

                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            Rigidbody[] childRigidbodies = root.GetComponentsInChildren<Rigidbody>(true);
            foreach (Rigidbody body in childRigidbodies)
            {
                if (body == null)
                {
                    continue;
                }

                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            ArticulationBody[] articulationBodies = root.GetComponentsInParent<ArticulationBody>(true);
            foreach (ArticulationBody body in articulationBodies)
            {
                if (body == null)
                {
                    continue;
                }

                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            ArticulationBody[] childArticulationBodies = root.GetComponentsInChildren<ArticulationBody>(true);
            foreach (ArticulationBody body in childArticulationBodies)
            {
                if (body == null)
                {
                    continue;
                }

                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }

        public bool TryApplyPostPlaceLocalRetreat(float linearVelocity)
        {
            if (_driveBridge == null || !_driveBridge.IsValid)
            {
                return false;
            }

            _tiagoNavMeshNavigationService?.Stop();
            _driveBridge.ApplyCommand(linearVelocity, 0f);
            return true;
        }

        public bool RegisterDepositedBoxObstacle(string boxId)
        {
            GameObject box = ActiveRoundBoxRegistry.TryResolve(boxId, out GameObject resolvedBox)
                ? resolvedBox
                : null;
            P40NavTraceDiagnostics.LogObstacleRegistrySnapshot(
                "before_deposited_box_obstacle_registered",
                "adapter_register_deposited_box_obstacle",
                this,
                _tiagoNavMeshNavigationService,
                lastDepositedBoxId: boxId);
            P40NavTraceDiagnostics.LogPostPlaceObstacleRegistrationDiagnostic(
                "before_registration",
                "adapter_register_deposited_box_obstacle",
                box,
                obstacleRegistered: false,
                pathsInvalidated: false,
                adapter: this,
                boxId: boxId);

            bool registered = _tiagoUnityManipulationService != null &&
                              _tiagoUnityManipulationService.RegisterDepositedBoxObstacle(box, boxId);

            P40NavTraceDiagnostics.LogPostPlaceObstacleRegistrationDiagnostic(
                "after_registration",
                registered ? "registered" : "registration_failed",
                box,
                registered,
                pathsInvalidated: false,
                adapter: this,
                boxId: boxId);
            P40NavTraceDiagnostics.LogObstacleRegistrySnapshot(
                "after_deposited_box_obstacle_registered",
                registered ? "registered" : "registration_failed",
                this,
                _tiagoNavMeshNavigationService,
                lastDepositedBoxId: boxId);
            return registered;
        }

        public string LastAutonomyRequestRejectionReason { get; private set; } = string.Empty;
        public bool LastAutonomyRequestWasDeferred { get; private set; }
        public bool HasPendingP40BVoiceOrder => _pendingP40BVoiceOrder != null;
        public bool HasPausedPendingP40BVoiceOrder => _pendingP40BVoiceOrder != null && _pendingP40BVoiceOrderPausedByStop;
        public string PendingP40BVoiceOrderTargetId => _pendingP40BVoiceOrder?.TargetId ?? string.Empty;
        public string PendingP40BVoiceOrderDestination => _pendingP40BVoiceOrder?.PlaceTargetId ?? string.Empty;

        public Dictionary<string, object> BuildP44IVoiceStopResumeStatePayload(string reason)
        {
            TargetDescriptor currentTarget = null;
            TargetDescriptor placeTarget = null;
            TaskStatus blackboardTaskStatus = TaskStatus.None;
            bool hasCurrentTarget = _blackboard != null &&
                                    _blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out currentTarget) &&
                                    currentTarget != null;
            bool hasPlaceTarget = _blackboard != null &&
                                  _blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out placeTarget) &&
                                  placeTarget != null;
            bool hasTaskStatus = _blackboard != null &&
                                 _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out blackboardTaskStatus);
            Dictionary<string, object> payload = new()
            {
                ["reason"] = reason ?? string.Empty,
                ["active_task_instance_id"] = _activeP40TaskInstanceId,
                ["active_request_id"] = _activeP40RequestId,
                ["active_producer"] = _activeP40Producer,
                ["active_target_id"] = _activeP40TargetId,
                ["active_place_target_id"] = _activeP40PlaceTargetId,
                ["held_object_id"] = HeldObjectId,
                ["manipulation_state"] = ManipulationState,
                ["has_pending_voice_order"] = _pendingP40BVoiceOrder != null,
                ["has_paused_pending_voice_order"] = _pendingP40BVoiceOrder != null && _pendingP40BVoiceOrderPausedByStop,
                ["pending_target_id"] = _pendingP40BVoiceOrder?.TargetId ?? string.Empty,
                ["pending_destination"] = _pendingP40BVoiceOrder?.PlaceTargetId ?? string.Empty,
                ["paused_pending_target_id"] = _pendingP40BVoiceOrderPausedByStop ? _pendingP40BVoiceOrder?.TargetId ?? string.Empty : string.Empty,
                ["paused_pending_destination"] = _pendingP40BVoiceOrderPausedByStop ? _pendingP40BVoiceOrder?.PlaceTargetId ?? string.Empty : string.Empty,
                ["stop_latch_active"] = _voiceStopLatched,
                ["voice_stop_latched"] = _voiceStopLatched,
                ["stop_latch_before"] = _voiceStopLatched,
                ["stop_latch_after"] = _voiceStopLatched,
                ["already_stopped"] = _voiceStopLatched,
                ["resume_snapshot_available"] = _stoppedTaskSnapshot != null,
                ["stopped_snapshot_valid"] = IsStoppedTaskSnapshotStructurallyValid(_stoppedTaskSnapshot),
                ["stopped_snapshot_phase"] = _stoppedTaskSnapshot?.Phase ?? string.Empty,
                ["stopped_snapshot_target"] = _stoppedTaskSnapshot?.PickupTarget?.Id ?? string.Empty,
                ["stopped_snapshot_place"] = _stoppedTaskSnapshot?.PlaceTarget?.Id ?? string.Empty,
                ["snapshot_task_instance_id"] = _stoppedTaskSnapshot?.TaskInstanceId ?? string.Empty,
                ["snapshot_request_id"] = _stoppedTaskSnapshot?.RequestId ?? string.Empty,
                ["snapshot_producer"] = _stoppedTaskSnapshot?.Producer ?? string.Empty,
                ["snapshot_target_id"] = _stoppedTaskSnapshot?.PickupTarget?.Id ?? string.Empty,
                ["snapshot_place_target_id"] = _stoppedTaskSnapshot?.PlaceTarget?.Id ?? string.Empty,
                ["snapshot_phase"] = _stoppedTaskSnapshot?.Phase ?? string.Empty,
                ["snapshot_was_holding_object"] = _stoppedTaskSnapshot?.WasHoldingObject ?? false,
                ["snapshot_held_object_id"] = _stoppedTaskSnapshot?.HeldObjectId ?? string.Empty,
                ["controller_task_status"] = _robotController != null ? _robotController.LastTaskStatus.ToString() : string.Empty,
                ["controller_mode"] = _robotController != null ? _robotController.CurrentMode.ToString() : string.Empty,
                ["task_status"] = _robotController != null ? _robotController.LastTaskStatus.ToString() : string.Empty,
                ["robot_mode"] = _robotController != null ? _robotController.CurrentMode.ToString() : string.Empty,
                ["request_source"] = string.Empty,
                ["rejection_reason"] = string.Empty,
                ["resume_result"] = string.Empty,
                ["blackboard_task_status"] = hasTaskStatus ? blackboardTaskStatus.ToString() : string.Empty,
                ["blackboard_mode"] = _blackboard != null ? _blackboard.CurrentMode.ToString() : string.Empty,
                ["blackboard_current_target"] = hasCurrentTarget ? currentTarget.Id : string.Empty,
                ["blackboard_place_target"] = hasPlaceTarget ? placeTarget.Id : string.Empty,
                ["phase"] = ResolveP40NavigationPhase()
            };
            AppendPendingVoiceOrderPayload(payload, _pendingP40BVoiceOrder, _pendingP40BVoiceOrderPausedByStop ? "paused" : "active");
            return payload;
        }

        public VoiceStopCommandResult ApplyVoiceStopCommand(string rawTranscript, string normalizedText, string intentKind)
        {
            Dictionary<string, object> payload = BuildVoiceStopPayload(rawTranscript, normalizedText, intentKind, "received", string.Empty);
            TiagoExperimentTelemetry.LogEvent("voice_stop_command_received", payload);

            if (_robotController == null || _blackboard == null)
            {
                Dictionary<string, object> rejected = BuildVoiceStopPayload(rawTranscript, normalizedText, intentKind, "rejected", "adapter_not_ready");
                TiagoExperimentTelemetry.LogEvent("voice_stop_command_rejected", rejected);
                return VoiceStopCommandResult.Reject("adapter_not_ready");
            }

            if (_voiceStopLatched)
            {
                const string reason = "already_stopped_idempotent_noop";
                Dictionary<string, object> alreadyStoppedPayload = BuildVoiceStopPayload(
                    rawTranscript,
                    normalizedText,
                    intentKind,
                    "idempotent_noop",
                    reason);
                alreadyStoppedPayload["stop_latch_before"] = true;
                alreadyStoppedPayload["stop_latch_after"] = true;
                alreadyStoppedPayload["already_stopped"] = true;
                alreadyStoppedPayload["stopped_snapshot_valid"] = IsStoppedTaskSnapshotStructurallyValid(_stoppedTaskSnapshot);
                alreadyStoppedPayload["stopped_snapshot_phase"] = _stoppedTaskSnapshot?.Phase ?? string.Empty;
                alreadyStoppedPayload["stopped_snapshot_target"] = _stoppedTaskSnapshot?.PickupTarget?.Id ?? string.Empty;
                alreadyStoppedPayload["stopped_snapshot_place"] = _stoppedTaskSnapshot?.PlaceTarget?.Id ?? string.Empty;
                alreadyStoppedPayload["request_source"] = "voice_command";
                alreadyStoppedPayload["rejection_reason"] = string.Empty;
                alreadyStoppedPayload["resume_result"] = string.Empty;
                TiagoExperimentTelemetry.LogEvent("voice_stop_already_stopped_idempotent_noop", alreadyStoppedPayload);
                return VoiceStopCommandResult.Accept(reason, "Ya estoy parado.");
            }

            bool hadPendingVoiceOrder = _pendingP40BVoiceOrder != null;
            P40BPendingVoiceOrder pendingAtStop = _pendingP40BVoiceOrder;
            bool holdingObject = !string.IsNullOrWhiteSpace(HeldObjectId);
            string phaseBefore = ResolveP40NavigationPhase();
            TaskStatus statusBefore = _robotController.LastTaskStatus;
            RobotMode modeBefore = _robotController.CurrentMode;
            bool hasActiveTask = statusBefore == TaskStatus.InProgress ||
                                 modeBefore == RobotMode.Autonomous ||
                                 !string.IsNullOrWhiteSpace(_activeP40TaskInstanceId);
            TargetDescriptor currentTargetBefore = null;
            TargetDescriptor placeTargetBefore = null;
            bool hasCurrentTargetBefore = _blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out currentTargetBefore) &&
                                          currentTargetBefore != null;
            bool hasPlaceTargetBefore = _blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out placeTargetBefore) &&
                                        placeTargetBefore != null;

            TiagoExperimentTelemetry.LogEvent(
                "voice_stop_command_accepted",
                BuildVoiceStopPayload(rawTranscript, normalizedText, intentKind, "accepted", hasActiveTask ? "active_task_stop_accepted" : "no_active_task_to_cancel"));

            if (hadPendingVoiceOrder)
            {
                _pendingP40BVoiceOrderPausedByStop = true;
                Dictionary<string, object> pendingPayload = BuildVoiceStopPayload(rawTranscript, normalizedText, intentKind, "pending_voice_order_paused", "voice_stop_command");
                AppendPendingVoiceOrderPayload(pendingPayload, pendingAtStop, "paused");
                TiagoExperimentTelemetry.LogEvent("voice_stop_pending_voice_order_preserved", pendingPayload);
                TiagoExperimentTelemetry.LogEvent("voice_stop_pending_voice_order_paused", pendingPayload);
            }

            if (hasActiveTask)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "voice_stop_active_task_cancellation_requested",
                    BuildVoiceStopPayload(rawTranscript, normalizedText, intentKind, "cancellation_requested", holdingObject ? "safe_stop_with_held_object" : "cancel_active_task"));
            }

            // Stop is a suspension, not a pre-pick replacement. Keep the round assignment
            // owned by the resumable task so the assistance selector cannot recycle it.
            bool assignmentReleased = false;
            string assignmentReleaseReason = hasActiveTask
                ? "preserved_for_supervisory_resume"
                : string.Empty;

            string previousTaskInstanceId = _activeP40TaskInstanceId;
            string previousRequestId = _activeP40RequestId;
            string previousProducer = _activeP40Producer;
            string previousVoiceInteractionId = _activeP40VoiceInteractionId;
            string previousTargetId = _activeP40TargetId;
            string previousPlaceTargetId = _activeP40PlaceTargetId;

            if (hasActiveTask)
            {
                CreateStoppedTaskSnapshot(
                    previousTaskInstanceId,
                    previousRequestId,
                    previousProducer,
                    previousVoiceInteractionId,
                    hasCurrentTargetBefore ? currentTargetBefore : null,
                    hasPlaceTargetBefore ? placeTargetBefore : null,
                    phaseBefore,
                    holdingObject,
                    HeldObjectId,
                    statusBefore,
                    modeBefore,
                    rawTranscript,
                    normalizedText,
                    intentKind);
                if (_stoppedTaskSnapshot != null)
                {
                    ActivateVoiceStopLatch("voice_stop_command", _stoppedTaskSnapshot);
                }
            }
            else
            {
                if (hadPendingVoiceOrder)
                {
                    ActivateVoiceStopLatch("voice_stop_pending_voice_order_paused", null);
                }
                else
                {
                    ActivateVoiceStopLatch("voice_stop_without_active_task", null);
                }
            }

            _tiagoNavMeshNavigationService?.Stop();
            _driveBridge?.Stop();
            _robotController.CancelInProgressTaskForReplacement("voice_stop_command");
            _fsm?.TryChangeMode(RobotMode.Idle);
            _blackboard.SetMode(RobotMode.Idle);
            _blackboard.Remove(TaskBlackboardKeys.CurrentTarget);
            _blackboard.Remove(TaskBlackboardKeys.PlaceTarget);
            _blackboard.Remove(TaskBlackboardKeys.ResumeHeldObjectId);
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.None);
            ClearActiveP40TaskState();

            if (hasActiveTask)
            {
                LogP40TaskLifecycle(
                    holdingObject ? "task_safe_stopped_with_held_object" : "task_cancelled_by_voice_stop",
                    new P40TraceMetadata
                    {
                        RequestId = previousRequestId,
                        Producer = previousProducer,
                        VoiceInteractionId = previousVoiceInteractionId
                    },
                    previousTaskInstanceId,
                    previousTargetId,
                    previousPlaceTargetId,
                    statusBefore.ToString(),
                    TaskStatus.None.ToString(),
                    "voice_stop_command");
            }

            Dictionary<string, object> appliedPayload = BuildVoiceStopPayload(
                rawTranscript,
                normalizedText,
                intentKind,
                "cancellation_applied",
                holdingObject ? "safe_stop_with_held_object" : hasActiveTask ? "active_task_cancelled" : "no_active_task_to_cancel");
            appliedPayload["had_active_task"] = hasActiveTask;
            appliedPayload["had_pending_voice_order"] = hadPendingVoiceOrder;
            appliedPayload["assignment_released"] = assignmentReleased;
            appliedPayload["assignment_release_reason"] = assignmentReleaseReason ?? string.Empty;
            appliedPayload["previous_task_instance_id"] = previousTaskInstanceId ?? string.Empty;
            appliedPayload["previous_target_id"] = previousTargetId ?? string.Empty;
            appliedPayload["previous_place_target_id"] = previousPlaceTargetId ?? string.Empty;
            TiagoExperimentTelemetry.LogEvent("voice_stop_navigation_manipulation_bt_cancellation_applied", appliedPayload);

            string acceptedReason = holdingObject
                ? "safe_stop_with_held_object"
                : hasActiveTask
                    ? "active_task_cancelled_by_voice_stop"
                    : hadPendingVoiceOrder
                        ? "pending_voice_order_paused_by_stop"
                        : "no_active_task_to_cancel";
            string feedback = holdingObject
                ? "Orden recibida: me detengo y mantengo el objeto sujeto."
                : hasActiveTask
                    ? "Orden recibida: detengo la tarea actual."
                    : hadPendingVoiceOrder
                        ? "Orden recibida: detengo y mantengo la orden pendiente."
                        : "No hay tarea activa; permanezco detenido.";
            return VoiceStopCommandResult.Accept(acceptedReason, feedback);
        }

        public bool CancelPendingVoiceOrder(string rawTranscript, string normalizedText, string intentKind, string reason = "explicit_cancel_pending_order")
        {
            P40BPendingVoiceOrder pending = _pendingP40BVoiceOrder;
            if (pending == null)
            {
                Dictionary<string, object> noPendingPayload = BuildVoiceStopPayload(rawTranscript, normalizedText, intentKind, "pending_voice_order_cancel_requested", "no_pending_voice_order");
                TiagoExperimentTelemetry.LogEvent("voice_cancel_pending_voice_order_not_found", noPendingPayload);
                return false;
            }

            Dictionary<string, object> payload = BuildVoiceStopPayload(rawTranscript, normalizedText, intentKind, "pending_voice_order_cleared", reason);
            AppendPendingVoiceOrderPayload(payload, pending, "cleared");
            payload["cleared_pending_target_id"] = pending.TargetId;
            payload["cleared_pending_destination"] = pending.PlaceTargetId;
            _pendingP40BVoiceOrder = null;
            _pendingP40BVoiceOrderPausedByStop = false;
            if (_stoppedTaskSnapshot == null)
            {
                ReleaseVoiceStopLatch("pending_voice_order_cancelled", null);
            }

            TiagoExperimentTelemetry.LogEvent("voice_stop_pending_voice_order_cleared", payload);
            return true;
        }

        public VoiceResumeCommandResult ApplyVoiceResumeCommand(string rawTranscript, string normalizedText, string intentKind)
        {
            Dictionary<string, object> receivedPayload = BuildVoiceResumePayload(rawTranscript, normalizedText, intentKind, "received", string.Empty, _stoppedTaskSnapshot);
            TiagoExperimentTelemetry.LogEvent("voice_resume_command_received", receivedPayload);

            if (_robotController == null || _blackboard == null || _targetSeeder == null || _placeTargetSeeder == null)
            {
                Dictionary<string, object> rejected = BuildVoiceResumePayload(rawTranscript, normalizedText, intentKind, "rejected", "adapter_not_ready", _stoppedTaskSnapshot);
                TiagoExperimentTelemetry.LogEvent("voice_resume_command_rejected", rejected);
                return VoiceResumeCommandResult.Reject("adapter_not_ready", "No puedo reanudar la tarea detenida porque el estado ya no es seguro.");
            }

            if (_stoppedTaskSnapshot == null)
            {
                if (_pendingP40BVoiceOrder != null && _pendingP40BVoiceOrderPausedByStop)
                {
                    return TryResumePausedPendingVoiceOrder(rawTranscript, normalizedText, intentKind);
                }

                if (_voiceStopLatched)
                {
                    const string latchOnlyResumeResult = "stop_latch_released_without_stopped_task";
                    ReleaseVoiceStopLatch("voice_resume_without_stopped_task", null);
                    Dictionary<string, object> accepted = BuildVoiceResumePayload(
                        rawTranscript,
                        normalizedText,
                        intentKind,
                        "accepted",
                        latchOnlyResumeResult,
                        null);
                    accepted["resume_result"] = latchOnlyResumeResult;
                    TiagoExperimentTelemetry.LogEvent("voice_resume_command_accepted", accepted);
                    return VoiceResumeCommandResult.Accept(
                        latchOnlyResumeResult,
                        "Orden recibida: dejo de permanecer detenido.");
                }

                Dictionary<string, object> rejected = BuildVoiceResumePayload(rawTranscript, normalizedText, intentKind, "rejected", "no_stopped_task_to_resume", null);
                TiagoExperimentTelemetry.LogEvent("voice_resume_command_rejected", rejected);
                TiagoExperimentTelemetry.LogEvent("voice_resume_no_resumable_task", rejected);
                return VoiceResumeCommandResult.Reject("no_stopped_task_to_resume", "No hay ninguna tarea detenida que pueda reanudar.");
            }

            StoppedTaskSnapshot snapshot = _stoppedTaskSnapshot;
            string invalidReason = ValidateStoppedTaskSnapshotForResume(snapshot);
            if (!string.IsNullOrWhiteSpace(invalidReason))
            {
                Dictionary<string, object> rejected = BuildVoiceResumePayload(rawTranscript, normalizedText, intentKind, "rejected", invalidReason, snapshot);
                TiagoExperimentTelemetry.LogEvent("voice_resume_command_rejected", rejected);
                InvalidateStoppedTaskSnapshot(invalidReason);
                return VoiceResumeCommandResult.Reject(invalidReason, "No puedo reanudar la tarea detenida porque el estado ya no es seguro.");
            }

            if (!_robotController.TryResetForNewTask(
                    out TaskStatus previousTaskStatus,
                    out RobotMode previousMode,
                    out bool wasTerminal,
                    out string controllerRejectionReason))
            {
                string reason = string.IsNullOrWhiteSpace(controllerRejectionReason)
                    ? "resume_controller_rejected"
                    : $"resume_controller_rejected_{controllerRejectionReason}";
                Dictionary<string, object> rejected = BuildVoiceResumePayload(rawTranscript, normalizedText, intentKind, "rejected", reason, snapshot);
                rejected["previous_task_status"] = previousTaskStatus.ToString();
                rejected["previous_robot_mode"] = previousMode.ToString();
                rejected["bt_terminal"] = wasTerminal;
                TiagoExperimentTelemetry.LogEvent("voice_resume_command_rejected", rejected);
                return VoiceResumeCommandResult.Reject(reason, "No puedo reanudar la tarea detenida porque el estado ya no es seguro.");
            }

            if (_pendingP40BVoiceOrder != null && _pendingP40BVoiceOrderPausedByStop)
            {
                _pendingP40BVoiceOrderPausedByStop = false;
                Dictionary<string, object> restoredPending = BuildVoiceResumePayload(rawTranscript, normalizedText, intentKind, "pending_voice_order_restored", "resume_stopped_task_first", snapshot);
                AppendPendingVoiceOrderPayload(restoredPending, _pendingP40BVoiceOrder, "restored");
                restoredPending["resume_source"] = "stopped_task_snapshot";
                TiagoExperimentTelemetry.LogEvent("voice_resume_pending_voice_order_restored", restoredPending);
            }

            _activeP40TaskInstanceId = string.IsNullOrWhiteSpace(snapshot.TaskInstanceId)
                ? P40TraceContext.NextTaskInstanceId("voice_resume_command")
                : snapshot.TaskInstanceId;
            _activeP40RequestId = snapshot.RequestId ?? string.Empty;
            _activeP40Producer = string.IsNullOrWhiteSpace(snapshot.Producer) ? "voice_resume_command" : snapshot.Producer;
            _activeP40VoiceInteractionId = snapshot.VoiceInteractionId ?? string.Empty;
            _activeP40TargetId = snapshot.PickupTarget.Id;
            _activeP40PlaceTargetId = snapshot.PlaceTarget.Id;
            _activeP40TaskResumedFromStop = true;
            _lastObservedP40TaskStatus = TaskStatus.None;

            _targetSeeder.SeedTarget(snapshot.PickupTarget);
            _placeTargetSeeder.SeedTarget(snapshot.PlaceTarget);
            if (snapshot.WasHoldingObject)
            {
                _blackboard.Set(TaskBlackboardKeys.ResumeHeldObjectId, snapshot.HeldObjectId);
            }
            else
            {
                _blackboard.Remove(TaskBlackboardKeys.ResumeHeldObjectId);
            }

            _tiagoNavMeshNavigationService?.SetP40NavTraceContext(_activeP40TaskInstanceId, _activeP40RequestId, _activeP40TargetId);
            bool modeChanged = _fsm != null && _fsm.TryChangeMode(RobotMode.Autonomous);
            _blackboard.SetMode(RobotMode.Autonomous);
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.InProgress);
            _lastObservedP40TaskStatus = TaskStatus.InProgress;

            // Consume the latch/snapshot only after the stopped operation has been
            // fully restored. Until this point the adapter remains the authoritative gate.
            ReleaseVoiceStopLatch("voice_resume_command", snapshot);
            _stoppedTaskSnapshot = null;
            TiagoExperimentTelemetry.LogEvent(
                "voice_resume_stopped_task_snapshot_invalidated",
                BuildVoiceResumePayload(rawTranscript, normalizedText, intentKind, "snapshot_consumed", "resume_accepted", snapshot));

            Dictionary<string, object> acceptedPayload = BuildVoiceResumePayload(rawTranscript, normalizedText, intentKind, "accepted", "resume_accepted", snapshot);
            acceptedPayload["mode_changed"] = modeChanged;
            acceptedPayload["blackboard_task_status_after_resume"] = TaskStatus.InProgress.ToString();
            acceptedPayload["controller_task_status_after_resume"] = _robotController.LastTaskStatus.ToString();
            TiagoExperimentTelemetry.LogEvent("voice_resume_command_accepted", acceptedPayload);
            TiagoExperimentTelemetry.LogEvent(
                snapshot.WasHoldingObject ? "voice_resume_holding_place_task_continued" : "voice_resume_pre_pick_task_restored",
                acceptedPayload);
            LogP40TaskLifecycle(
                snapshot.WasHoldingObject ? "task_resumed_holding_place" : "task_resumed_pre_pick",
                new P40TraceMetadata
                {
                    RequestId = _activeP40RequestId,
                    Producer = _activeP40Producer,
                    VoiceInteractionId = _activeP40VoiceInteractionId
                },
                _activeP40TaskInstanceId,
                _activeP40TargetId,
                _activeP40PlaceTargetId,
                previousTaskStatus.ToString(),
                TaskStatus.InProgress.ToString(),
                "voice_resume_command");

            return VoiceResumeCommandResult.Accept(
                snapshot.WasHoldingObject ? "holding_place_task_continued" : "pre_pick_task_restored",
                "Orden recibida: reanudo la tarea detenida.");
        }

        private VoiceResumeCommandResult TryResumePausedPendingVoiceOrder(string rawTranscript, string normalizedText, string intentKind)
        {
            P40BPendingVoiceOrder pending = _pendingP40BVoiceOrder;
            string invalidReason = ValidatePendingP40BVoiceOrderForPromotion(
                pending,
                targetId =>
                {
                    RobotAssistanceRoundCoordinator coordinator = ResolveAssistanceRoundCoordinator();
                    return coordinator != null && coordinator.IsP40BTargetAlreadyDepositedOrCompleted(targetId)
                        ? "target_already_deposited"
                        : string.Empty;
                });
            if (!string.IsNullOrWhiteSpace(invalidReason))
            {
                Dictionary<string, object> rejected = BuildVoiceResumePayload(rawTranscript, normalizedText, intentKind, "rejected", invalidReason, null);
                AppendPendingVoiceOrderPayload(rejected, pending, "invalid");
                TiagoExperimentTelemetry.LogEvent("voice_resume_command_rejected", rejected);
                TiagoExperimentTelemetry.LogEvent("voice_resume_no_resumable_task", rejected);
                _pendingP40BVoiceOrder = null;
                _pendingP40BVoiceOrderPausedByStop = false;
                ReleaseVoiceStopLatch(invalidReason, null);
                return VoiceResumeCommandResult.Reject(invalidReason, "No puedo reanudar la orden pendiente porque el estado ya no es seguro.");
            }

            if (!_robotController.TryResetForNewTask(
                    out TaskStatus previousTaskStatus,
                    out RobotMode previousMode,
                    out bool wasTerminal,
                    out string controllerRejectionReason))
            {
                string reason = string.IsNullOrWhiteSpace(controllerRejectionReason)
                    ? "resume_pending_voice_order_controller_rejected"
                    : $"resume_pending_voice_order_controller_rejected_{controllerRejectionReason}";
                Dictionary<string, object> rejected = BuildVoiceResumePayload(rawTranscript, normalizedText, intentKind, "rejected", reason, null);
                AppendPendingVoiceOrderPayload(rejected, pending, "controller_rejected");
                rejected["previous_task_status"] = previousTaskStatus.ToString();
                rejected["previous_robot_mode"] = previousMode.ToString();
                rejected["bt_terminal"] = wasTerminal;
                TiagoExperimentTelemetry.LogEvent("voice_resume_command_rejected", rejected);
                return VoiceResumeCommandResult.Reject(reason, "No puedo reanudar la orden pendiente porque el estado ya no es seguro.");
            }

            _pendingP40BVoiceOrder = null;
            _pendingP40BVoiceOrderPausedByStop = false;
            ReleaseVoiceStopLatch("voice_resume_pending_voice_order", null);

            TargetDescriptor pickupTarget = pending.PickupTarget ?? new TargetDescriptor(pending.TargetId, System.Numerics.Vector3.Zero);
            TargetDescriptor placeTarget = pending.PlaceTarget ?? new TargetDescriptor(pending.PlaceTargetId, System.Numerics.Vector3.Zero);
            _activeP40TaskInstanceId = P40TraceContext.NextTaskInstanceId("voice_resume_pending_voice_order");
            _activeP40RequestId = pending.Trace?.RequestId ?? string.Empty;
            _activeP40Producer = "voice_command";
            _activeP40VoiceInteractionId = pending.Trace?.VoiceInteractionId ?? string.Empty;
            _activeP40TargetId = pickupTarget.Id;
            _activeP40PlaceTargetId = placeTarget.Id;
            _activeP40TaskResumedFromStop = false;
            _lastObservedP40TaskStatus = previousTaskStatus;

            _targetSeeder.SeedTarget(pickupTarget);
            _placeTargetSeeder.SeedTarget(placeTarget);
            _blackboard.Remove(TaskBlackboardKeys.ResumeHeldObjectId);
            _tiagoNavMeshNavigationService?.SetP40NavTraceContext(_activeP40TaskInstanceId, _activeP40RequestId, _activeP40TargetId);
            bool modeChanged = _fsm != null && _fsm.TryChangeMode(RobotMode.Autonomous);
            _blackboard.SetMode(RobotMode.Autonomous);
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.InProgress);
            _lastObservedP40TaskStatus = TaskStatus.InProgress;

            Dictionary<string, object> payload = BuildVoiceResumePayload(rawTranscript, normalizedText, intentKind, "accepted", "pending_voice_order_promoted", null);
            AppendPendingVoiceOrderPayload(payload, pending, "promoted");
            payload["resume_source"] = "paused_pending_voice_order";
            payload["promoted_task_instance_id"] = _activeP40TaskInstanceId;
            payload["mode_changed"] = modeChanged;
            payload["previous_task_status"] = previousTaskStatus.ToString();
            payload["previous_robot_mode"] = previousMode.ToString();
            TiagoExperimentTelemetry.LogEvent("voice_resume_pending_voice_order_restored", payload);
            TiagoExperimentTelemetry.LogEvent("voice_resume_pending_voice_order_promoted", payload);
            TiagoExperimentTelemetry.LogEvent("voice_resume_command_accepted", payload);
            LogP40TaskLifecycle(
                "task_started",
                pending.Trace,
                _activeP40TaskInstanceId,
                _activeP40TargetId,
                _activeP40PlaceTargetId,
                previousTaskStatus.ToString(),
                TaskStatus.InProgress.ToString(),
                "voice_resume_pending_voice_order");

            return VoiceResumeCommandResult.Accept("pending_voice_order_promoted", "Orden recibida: retomo la orden pendiente.");
        }

        public bool TrySubmitAutonomyRequest(MultimodalTaskIntent intent, TargetDescriptor pickupTarget, TargetDescriptor placeTarget, bool activateAutonomousMode)
        {
            string requestedTargetId = pickupTarget != null ? pickupTarget.Id : string.Empty;
            string requestedPlaceId = placeTarget != null ? placeTarget.Id : string.Empty;
            TaskStatus previousTaskStatus = _robotController != null ? _robotController.LastTaskStatus : TaskStatus.None;
            RobotMode previousMode = _robotController != null ? _robotController.CurrentMode : RobotMode.Idle;
            bool wasTerminal = previousTaskStatus == TaskStatus.Succeeded || previousTaskStatus == TaskStatus.Failed;
            P40TraceMetadata trace = P40TraceContext.EnsureForIntent(intent, intent != null ? intent.Source : "unknown");

            LogAutonomyRequestAdapterEvent(
                "autonomy_request_received_by_adapter",
                requestedTargetId,
                requestedPlaceId,
                previousTaskStatus,
                previousMode,
                wasTerminal,
                string.Empty);
            LogP40AdapterGateSnapshot(
                "adapter_received",
                trace,
                requestedTargetId,
                requestedPlaceId,
                previousTaskStatus,
                previousMode,
                wasTerminal,
                string.Empty,
                accepted: false);
            LogP40VoiceLifecycle("adapter_received", trace, intent, "received", "adapter_received_request");
            LastAutonomyRequestRejectionReason = string.Empty;
            LastAutonomyRequestWasDeferred = false;

            if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
            {
                const string reason = ExperimentRuntimePauseCoordinator.AutonomyRequestRejectionReason;
                LastAutonomyRequestRejectionReason = reason;
                SuspendForExperimentPause(reason);
                LogAutonomyRequestAdapterEvent(
                    "autonomy_request_rejected_by_adapter",
                    requestedTargetId,
                    requestedPlaceId,
                    previousTaskStatus,
                    previousMode,
                    wasTerminal,
                    reason);
                LogP40AdapterGateSnapshot("adapter_rejected", trace, requestedTargetId, requestedPlaceId, previousTaskStatus, previousMode, wasTerminal, reason, accepted: false);
                LogP40VoiceLifecycle("adapter_rejected", trace, intent, "rejected", reason);
                Dictionary<string, object> pausedPayload = BuildP44IVoiceStopResumeStatePayload(reason);
                pausedPayload["request_source"] = P40TraceContext.NormalizeProducer(intent?.Source);
                pausedPayload["rejection_reason"] = reason;
                pausedPayload["requested_target_id"] = requestedTargetId;
                pausedPayload["requested_place_target_id"] = requestedPlaceId;
                TiagoExperimentTelemetry.LogEvent("experiment_paused_autonomy_request_rejected", pausedPayload);
                return false;
            }

            if (intent == null)
            {
                LastAutonomyRequestRejectionReason = "intent_null";
                LogAutonomyRequestAdapterEvent(
                    "autonomy_request_rejected_by_adapter",
                    requestedTargetId,
                    requestedPlaceId,
                    previousTaskStatus,
                    previousMode,
                    wasTerminal,
                    "intent_null");
                LogP40AdapterGateSnapshot("adapter_rejected", trace, requestedTargetId, requestedPlaceId, previousTaskStatus, previousMode, wasTerminal, "intent_null", accepted: false);
                LogP40VoiceLifecycle("adapter_rejected", trace, intent, "rejected", "intent_null");
                return false;
            }

            if (pickupTarget == null || placeTarget == null)
            {
                string reason = pickupTarget == null ? "pickup_target_null" : "place_target_null";
                LastAutonomyRequestRejectionReason = reason;
                LogAutonomyRequestAdapterEvent(
                    "autonomy_request_rejected_by_adapter",
                    requestedTargetId,
                    requestedPlaceId,
                    previousTaskStatus,
                    previousMode,
                    wasTerminal,
                    reason);
                LogP40AdapterGateSnapshot("adapter_rejected", trace, requestedTargetId, requestedPlaceId, previousTaskStatus, previousMode, wasTerminal, reason, accepted: false);
                LogP40VoiceLifecycle("adapter_rejected", trace, intent, "rejected", reason);
                return false;
            }

            if (_robotController == null || _targetSeeder == null || _placeTargetSeeder == null)
            {
                LastAutonomyRequestRejectionReason = "adapter_not_ready";
                LogAutonomyRequestAdapterEvent(
                    "autonomy_request_rejected_by_adapter",
                    requestedTargetId,
                    requestedPlaceId,
                    previousTaskStatus,
                    previousMode,
                    wasTerminal,
                    "adapter_not_ready");
                LogP40AdapterGateSnapshot("adapter_rejected", trace, requestedTargetId, requestedPlaceId, previousTaskStatus, previousMode, wasTerminal, "adapter_not_ready", accepted: false);
                LogP40VoiceLifecycle("adapter_rejected", trace, intent, "rejected", "adapter_not_ready");
                return false;
            }

            if (TryGetSupervisoryStopRequestRejection(intent, out string stopRejectionReason, out string requestSource))
            {
                string reason = stopRejectionReason;
                LastAutonomyRequestRejectionReason = reason;
                _tiagoNavMeshNavigationService?.Stop();
                _driveBridge?.Stop();
                LogAutonomyRequestAdapterEvent(
                    "autonomy_request_rejected_by_adapter",
                    requestedTargetId,
                    requestedPlaceId,
                    previousTaskStatus,
                    previousMode,
                    wasTerminal,
                    reason);
                LogP40AdapterGateSnapshot("adapter_rejected", trace, requestedTargetId, requestedPlaceId, previousTaskStatus, previousMode, wasTerminal, reason, accepted: false);
                LogP40VoiceLifecycle("adapter_rejected", trace, intent, "rejected", reason);
                Dictionary<string, object> blockedPayload = BuildVoiceStopLatchPayload(
                    reason,
                    requestedTargetId,
                    requestedPlaceId,
                    trace,
                    intent,
                    _stoppedTaskSnapshot);
                blockedPayload["request_source"] = requestSource;
                blockedPayload["rejection_reason"] = reason;
                TiagoExperimentTelemetry.LogEvent("supervisory_stop_latched_request_rejected", blockedPayload);
                TiagoExperimentTelemetry.LogEvent(reason, blockedPayload);
                TiagoExperimentTelemetry.LogEvent("voice_resume_snapshot_preserved_after_auto_task_block", blockedPayload);
                return false;
            }

            if (_pendingP40BVoiceOrderPausedByStop && _pendingP40BVoiceOrder != null)
            {
                string producer = P40TraceContext.NormalizeProducer(trace?.Producer ?? intent?.Source);
                string reason = string.Equals(producer, "voice_command", StringComparison.OrdinalIgnoreCase)
                    ? "paused_pending_voice_order_already_exists"
                    : "paused_pending_voice_order_blocks_auto_selection";
                LastAutonomyRequestRejectionReason = reason;
                LogAutonomyRequestAdapterEvent(
                    "autonomy_request_rejected_by_adapter",
                    requestedTargetId,
                    requestedPlaceId,
                    previousTaskStatus,
                    previousMode,
                    wasTerminal,
                    reason);
                LogP40AdapterGateSnapshot("adapter_rejected", trace, requestedTargetId, requestedPlaceId, previousTaskStatus, previousMode, wasTerminal, reason, accepted: false);
                LogP40VoiceLifecycle("adapter_rejected", trace, intent, "rejected", reason);
                Dictionary<string, object> blockedPayload = BuildP40BPendingPayload(_pendingP40BVoiceOrder, reason);
                blockedPayload["rejected_target_id"] = requestedTargetId ?? string.Empty;
                blockedPayload["rejected_destination"] = requestedPlaceId ?? string.Empty;
                blockedPayload["requested_producer"] = producer;
                TiagoExperimentTelemetry.LogEvent("paused_pending_voice_order_blocked_new_task", blockedPayload);
                return false;
            }

            if (!_robotController.TryResetForNewTask(
                    out previousTaskStatus,
                    out previousMode,
                    out wasTerminal,
                    out string rejectionReason))
            {
                if (string.Equals(rejectionReason, "task_in_progress", StringComparison.OrdinalIgnoreCase) &&
                    TryHandleP40AVoicePrePickReplacement(
                        trace,
                        intent,
                        requestedTargetId,
                        requestedPlaceId,
                        previousTaskStatus,
                        previousMode,
                        wasTerminal))
                {
                    rejectionReason = string.Empty;
                }
                else if (string.Equals(rejectionReason, "task_in_progress", StringComparison.OrdinalIgnoreCase) &&
                         IsP40BVoiceOrderCandidate(trace, intent) &&
                         TryHandleP40BVoiceOrderDeferral(
                             trace,
                             intent,
                             pickupTarget,
                             placeTarget,
                             requestedTargetId,
                             requestedPlaceId,
                             previousTaskStatus,
                             previousMode))
                {
                    return true;
                }
                else
                {
                LastAutonomyRequestRejectionReason = rejectionReason;
                LogAutonomyRequestAdapterEvent(
                    "autonomy_request_rejected_by_adapter",
                    requestedTargetId,
                    requestedPlaceId,
                    previousTaskStatus,
                    previousMode,
                    wasTerminal,
                    rejectionReason);
                LogP40AdapterGateSnapshot("adapter_rejected", trace, requestedTargetId, requestedPlaceId, previousTaskStatus, previousMode, wasTerminal, rejectionReason, accepted: false);
                LogP40VoiceLifecycle("adapter_rejected", trace, intent, "rejected", rejectionReason);
                return false;
                }
            }

            string taskInstanceId = P40TraceContext.NextTaskInstanceId(trace.Producer);
            _activeP40TaskInstanceId = taskInstanceId;
            _activeP40RequestId = trace.RequestId;
            _activeP40Producer = trace.Producer;
            _activeP40VoiceInteractionId = trace.VoiceInteractionId;
            _activeP40TargetId = requestedTargetId;
            _activeP40PlaceTargetId = requestedPlaceId;
            _activeP40TaskResumedFromStop = false;
            _lastObservedP40TaskStatus = previousTaskStatus;
            _tiagoNavMeshNavigationService?.SetP40NavTraceContext(taskInstanceId, trace.RequestId, requestedTargetId);
            LogP40TaskLifecycle("task_created", trace, taskInstanceId, requestedTargetId, requestedPlaceId, previousTaskStatus.ToString(), TaskStatus.None.ToString(), "adapter_request_passed_gate");

            LogAutonomyRequestAdapterEvent(
                "robot_controller_task_reset_for_new_request",
                requestedTargetId,
                requestedPlaceId,
                previousTaskStatus,
                previousMode,
                wasTerminal,
                string.Empty);

            _targetSeeder.SeedTarget(pickupTarget);
            _placeTargetSeeder.SeedTarget(placeTarget);

            LogAutonomyRequestAdapterEvent(
                "autonomy_request_accepted_by_adapter",
                requestedTargetId,
                requestedPlaceId,
                previousTaskStatus,
                previousMode,
                wasTerminal,
                string.Empty);
            LogP40AdapterGateSnapshot("adapter_accepted", trace, requestedTargetId, requestedPlaceId, previousTaskStatus, previousMode, wasTerminal, string.Empty, accepted: true);
            LogP40VoiceLifecycle("adapter_accepted", trace, intent, "accepted", "adapter_accepted_request");
            LogP40TaskLifecycle("task_started", trace, taskInstanceId, requestedTargetId, requestedPlaceId, previousTaskStatus.ToString(), TaskStatus.None.ToString(), "targets_seeded");
            P40NavTraceDiagnostics.LogObstacleRegistrySnapshot(
                "task_accepted",
                "adapter_accepted",
                this,
                _tiagoNavMeshNavigationService,
                taskInstanceId,
                trace.RequestId,
                requestedTargetId,
                requestedPlaceId);
            _tiagoNavMeshNavigationService?.LogP40NavTraceActivePathOwnershipSnapshot("task_accepted", "task_accepted");

            if (activateAutonomousMode)
            {
                bool modeChanged = _fsm.TryChangeMode(RobotMode.Autonomous);
                TiagoExperimentTelemetry.LogEvent(
                    "robot_controller_task_start_requested",
                    BuildAutonomyRequestAdapterPayload(
                        requestedTargetId,
                        requestedPlaceId,
                        previousTaskStatus,
                        previousMode,
                        wasTerminal,
                        string.Empty,
                        modeChanged));
                Debug.Log($"[Autonomy] robot_controller_task_start_requested | target={requestedTargetId} place={requestedPlaceId} previous_task_status={previousTaskStatus} previous_mode={previousMode} bt_terminal={wasTerminal} mode_changed={modeChanged}", this);
            }

            return true;
        }

        private void ClearActiveP40TaskState()
        {
            _activeP40TaskInstanceId = string.Empty;
            _activeP40RequestId = string.Empty;
            _activeP40Producer = "unknown";
            _activeP40VoiceInteractionId = string.Empty;
            _activeP40TargetId = string.Empty;
            _activeP40PlaceTargetId = string.Empty;
            _activeP40TaskResumedFromStop = false;
            _lastObservedP40TaskStatus = TaskStatus.None;
        }

        private void ActivateVoiceStopLatch(string reason, StoppedTaskSnapshot snapshot)
        {
            bool latchBefore = _voiceStopLatched;
            if (_voiceStopLatched)
            {
                return;
            }

            _voiceStopLatched = true;
            Dictionary<string, object> payload = BuildVoiceStopLatchPayload(reason, string.Empty, string.Empty, null, null, snapshot);
            payload["stop_latch_before"] = latchBefore;
            payload["stop_latch_after"] = _voiceStopLatched;
            TiagoExperimentTelemetry.LogEvent(
                "voice_stop_latch_activated",
                payload);
        }

        private void ReleaseVoiceStopLatch(string reason, StoppedTaskSnapshot snapshot)
        {
            bool latchBefore = _voiceStopLatched;
            if (!_voiceStopLatched)
            {
                return;
            }

            _voiceStopLatched = false;
            Dictionary<string, object> payload = BuildVoiceStopLatchPayload(reason, string.Empty, string.Empty, null, null, snapshot);
            payload["stop_latch_before"] = latchBefore;
            payload["stop_latch_after"] = _voiceStopLatched;
            TiagoExperimentTelemetry.LogEvent(
                "voice_stop_latch_released",
                payload);
        }

        private Dictionary<string, object> BuildVoiceStopLatchPayload(
            string reason,
            string requestedTargetId,
            string requestedPlaceId,
            P40TraceMetadata trace,
            MultimodalTaskIntent intent,
            StoppedTaskSnapshot snapshot)
        {
            string producer = P40TraceContext.NormalizeProducer(trace?.Producer ?? intent?.Source);
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["reason"] = reason ?? string.Empty,
                ["voice_stop_latched"] = _voiceStopLatched,
                ["stop_latch_active"] = _voiceStopLatched,
                ["stop_latch_before"] = _voiceStopLatched,
                ["stop_latch_after"] = _voiceStopLatched,
                ["already_stopped"] = false,
                ["snapshot_available"] = snapshot != null,
                ["stopped_snapshot_valid"] = IsStoppedTaskSnapshotStructurallyValid(snapshot),
                ["stopped_snapshot_phase"] = snapshot?.Phase ?? string.Empty,
                ["stopped_snapshot_target"] = snapshot?.PickupTarget?.Id ?? string.Empty,
                ["stopped_snapshot_place"] = snapshot?.PlaceTarget?.Id ?? string.Empty,
                ["snapshot_task_instance_id"] = snapshot?.TaskInstanceId ?? string.Empty,
                ["snapshot_request_id"] = snapshot?.RequestId ?? string.Empty,
                ["snapshot_producer"] = snapshot?.Producer ?? string.Empty,
                ["snapshot_target_id"] = snapshot?.PickupTarget?.Id ?? string.Empty,
                ["snapshot_place_target_id"] = snapshot?.PlaceTarget?.Id ?? string.Empty,
                ["snapshot_phase"] = snapshot?.Phase ?? string.Empty,
                ["snapshot_was_holding_object"] = snapshot?.WasHoldingObject ?? false,
                ["snapshot_held_object_id"] = snapshot?.HeldObjectId ?? string.Empty,
                ["requested_target_id"] = requestedTargetId ?? string.Empty,
                ["requested_place_target_id"] = requestedPlaceId ?? string.Empty,
                ["requested_producer"] = producer,
                ["request_source"] = producer,
                ["rejection_reason"] = reason ?? string.Empty,
                ["resume_result"] = string.Empty,
                ["intent_source"] = intent?.Source ?? string.Empty,
                ["active_task_instance_id"] = _activeP40TaskInstanceId,
                ["active_target_id"] = _activeP40TargetId,
                ["active_place_target_id"] = _activeP40PlaceTargetId,
                ["held_object_id"] = HeldObjectId,
                ["controller_task_status"] = _robotController != null ? _robotController.LastTaskStatus.ToString() : string.Empty,
                ["controller_mode"] = _robotController != null ? _robotController.CurrentMode.ToString() : string.Empty,
                ["task_status"] = _robotController != null ? _robotController.LastTaskStatus.ToString() : string.Empty,
                ["robot_mode"] = _robotController != null ? _robotController.CurrentMode.ToString() : string.Empty,
                ["blackboard_mode"] = _blackboard != null ? _blackboard.CurrentMode.ToString() : string.Empty,
                ["phase"] = ResolveP40NavigationPhase()
            };
            AppendPendingVoiceOrderPayload(payload, _pendingP40BVoiceOrder, _pendingP40BVoiceOrderPausedByStop ? "paused" : "active");
            return payload;
        }

        private static bool IsP40BVoiceOrderCandidate(P40TraceMetadata trace, MultimodalTaskIntent intent)
        {
            string producer = P40TraceContext.NormalizeProducer(trace?.Producer ?? intent?.Source);
            return string.Equals(producer, "voice_command", StringComparison.OrdinalIgnoreCase) &&
                   intent != null &&
                   intent.TaskFlow == AutonomousTaskFlow.PickAndPlace;
        }

        public bool TryPromoteP40BPendingVoiceOrderAfterPlace(
            Func<MultimodalTaskIntent, bool> submitIntent,
            Func<string, string> validateTarget,
            string previousCompletedTaskInstanceId)
        {
            if (_voiceStopLatched)
            {
                P40BPendingVoiceOrder blockedPending = _pendingP40BVoiceOrder;
                Dictionary<string, object> blockedPayload = BuildVoiceStopLatchPayload(
                    "voice_stop_latched_pending_promotion_blocked",
                    blockedPending?.TargetId,
                    blockedPending?.PlaceTargetId,
                    blockedPending?.Trace,
                    blockedPending?.Intent,
                    _stoppedTaskSnapshot);
                blockedPayload["rejection_reason"] = "voice_stop_latched_pending_promotion_blocked";
                TiagoExperimentTelemetry.LogEvent("voice_stop_latched_pending_promotion_blocked", blockedPayload);
                return false;
            }

            if (_pendingP40BVoiceOrder == null)
            {
                return false;
            }

            P40BPendingVoiceOrder pending = _pendingP40BVoiceOrder;
            string invalidReason = ValidatePendingP40BVoiceOrderForPromotion(pending, validateTarget);
            if (!string.IsNullOrWhiteSpace(invalidReason))
            {
                _pendingP40BVoiceOrder = null;
                _pendingP40BVoiceOrderPausedByStop = false;
                LogP40BDeferredVoiceOrderDiscarded(pending, invalidReason, previousCompletedTaskInstanceId);
                return false;
            }

            _pendingP40BVoiceOrder = null;
            _pendingP40BVoiceOrderPausedByStop = false;
            LogP40BDeferredVoiceOrderPromoted(pending, previousCompletedTaskInstanceId);
            return submitIntent != null && submitIntent(pending.Intent);
        }

        private bool TryHandleP40AVoicePrePickReplacement(
            P40TraceMetadata trace,
            MultimodalTaskIntent intent,
            string requestedTargetId,
            string requestedPlaceId,
            TaskStatus previousTaskStatus,
            RobotMode previousMode,
            bool wasTerminal)
        {
            string notAllowedReason = ResolveP40APrePickReplacementBlockReason(
                trace,
                intent,
                requestedTargetId,
                requestedPlaceId,
                previousTaskStatus,
                previousMode);
            if (!string.IsNullOrWhiteSpace(notAllowedReason))
            {
                LogP40AVoicePrePickReplacementNotAllowed(trace, requestedTargetId, requestedPlaceId, notAllowedReason);
                return false;
            }

            RobotAssistanceRoundCoordinator coordinator = ResolveAssistanceRoundCoordinator();
            if (coordinator == null)
            {
                LogP40AVoicePrePickReplacementNotAllowed(trace, requestedTargetId, requestedPlaceId, "replacement_not_allowed_assignment_release_unavailable");
                return false;
            }

            string oldTaskInstanceId = _activeP40TaskInstanceId;
            string oldRequestId = _activeP40RequestId;
            string oldTargetId = _activeP40TargetId;
            string oldPlaceTargetId = _activeP40PlaceTargetId;
            string oldProducer = _activeP40Producer;
            LogP40AVoicePrePickReplacementRequested(trace, requestedTargetId, requestedPlaceId, oldTaskInstanceId, oldRequestId, oldTargetId, oldProducer);

            if (!coordinator.TryReleaseAssignedBoxForVoicePrePickReplacement(oldTargetId, oldTaskInstanceId, out string releaseReason))
            {
                LogP40AVoicePrePickReplacementNotAllowed(trace, requestedTargetId, requestedPlaceId, releaseReason);
                return false;
            }

            LogP40TaskLifecycle(
                "task_aborted",
                new P40TraceMetadata
                {
                    RequestId = oldRequestId,
                    Producer = oldProducer,
                    VoiceInteractionId = _activeP40VoiceInteractionId
                },
                oldTaskInstanceId,
                oldTargetId,
                oldPlaceTargetId,
                previousTaskStatus.ToString(),
                TaskStatus.None.ToString(),
                "cancelled_by_voice_pre_pick");

            TiagoExperimentTelemetry.LogEvent(
                "p40a_autonomous_task_replaced_before_pick",
                BuildP40AReplacementPayload(trace, requestedTargetId, requestedPlaceId, oldTaskInstanceId, oldRequestId, oldTargetId, oldProducer, "cancelled_by_voice_pre_pick"));

            _robotController.CancelInProgressTaskForReplacement("voice_pre_pick_replacement");
            _activeP40TaskInstanceId = string.Empty;
            _activeP40RequestId = string.Empty;
            _activeP40Producer = "unknown";
            _activeP40VoiceInteractionId = string.Empty;
            _activeP40TargetId = string.Empty;
            _activeP40PlaceTargetId = string.Empty;
            _lastObservedP40TaskStatus = TaskStatus.None;

            TiagoExperimentTelemetry.LogEvent(
                "p40a_voice_pre_pick_replacement_accepted",
                BuildP40AReplacementPayload(trace, requestedTargetId, requestedPlaceId, oldTaskInstanceId, oldRequestId, oldTargetId, oldProducer, "voice_supervision_pre_pick_replacement"));
            return true;
        }

        private bool TryHandleP40BVoiceOrderDeferral(
            P40TraceMetadata trace,
            MultimodalTaskIntent intent,
            TargetDescriptor pickupTarget,
            TargetDescriptor placeTarget,
            string requestedTargetId,
            string requestedPlaceId,
            TaskStatus previousTaskStatus,
            RobotMode previousMode)
        {
            string rejectionReason = ResolveP40BVoiceOrderDeferBlockReason(
                trace,
                intent,
                requestedTargetId,
                requestedPlaceId,
                previousTaskStatus,
                previousMode);
            if (!string.IsNullOrWhiteSpace(rejectionReason))
            {
                LogP40BVoiceOrderDeferRejected(trace, requestedTargetId, requestedPlaceId, rejectionReason);
                return false;
            }

            _pendingP40BVoiceOrder = new P40BPendingVoiceOrder(
                intent,
                trace,
                pickupTarget,
                placeTarget,
                requestedTargetId,
                requestedPlaceId,
                _activeP40TaskInstanceId,
                _activeP40TargetId,
                HeldObjectId);
            LastAutonomyRequestWasDeferred = true;
            LogP40BVoiceOrderDeferred(_pendingP40BVoiceOrder);
            LogP40AdapterGateSnapshot("adapter_deferred", trace, requestedTargetId, requestedPlaceId, previousTaskStatus, previousMode, wasTerminal: false, "deferred_until_current_place_completed", accepted: true);
            LogP40VoiceLifecycle("adapter_deferred", trace, intent, "deferred", "holding_or_toward_place");
            return true;
        }

        private string ResolveP40BVoiceOrderDeferBlockReason(
            P40TraceMetadata trace,
            MultimodalTaskIntent intent,
            string requestedTargetId,
            string requestedPlaceId,
            TaskStatus previousTaskStatus,
            RobotMode previousMode)
        {
            string producer = P40TraceContext.NormalizeProducer(trace?.Producer ?? intent?.Source);
            if (!string.Equals(producer, "voice_command", StringComparison.OrdinalIgnoreCase))
            {
                return "not_voice_command";
            }

            if (intent == null || intent.TaskFlow != AutonomousTaskFlow.PickAndPlace)
            {
                return "not_pick_and_place";
            }

            if (string.IsNullOrWhiteSpace(requestedTargetId))
            {
                return "target_unresolved";
            }

            if (string.IsNullOrWhiteSpace(requestedPlaceId) ||
                string.Equals(requestedPlaceId, "SELF", StringComparison.OrdinalIgnoreCase))
            {
                return "destination_unresolved";
            }

            if (_pendingP40BVoiceOrder != null)
            {
                return "pending_voice_order_already_exists";
            }

            if (string.IsNullOrWhiteSpace(_activeP40TaskInstanceId) ||
                previousTaskStatus != TaskStatus.InProgress ||
                previousMode != RobotMode.Autonomous)
            {
                return "active_task_not_in_progress";
            }

            string phase = ResolveP40NavigationPhase();
            bool holdingOrPlacing = !string.IsNullOrWhiteSpace(HeldObjectId) ||
                                    string.Equals(phase, "toward_place", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(phase, "place", StringComparison.OrdinalIgnoreCase);
            if (!holdingOrPlacing)
            {
                return "not_holding_or_placing";
            }

            RobotAssistanceRoundCoordinator coordinator = ResolveAssistanceRoundCoordinator();
            if (coordinator != null && coordinator.IsP40BTargetAlreadyDepositedOrCompleted(requestedTargetId))
            {
                return "target_already_deposited";
            }

            return string.Empty;
        }

        private string ResolveP40APrePickReplacementBlockReason(
            P40TraceMetadata trace,
            MultimodalTaskIntent intent,
            string requestedTargetId,
            string requestedPlaceId,
            TaskStatus previousTaskStatus,
            RobotMode previousMode)
        {
            string newProducer = P40TraceContext.NormalizeProducer(trace?.Producer ?? intent?.Source);
            if (!string.Equals(newProducer, "voice_command", StringComparison.OrdinalIgnoreCase))
            {
                return "replacement_not_allowed_active_task_not_voice_request";
            }

            if (intent == null || intent.TaskFlow != AutonomousTaskFlow.PickAndPlace)
            {
                return "replacement_not_allowed_not_pick_and_place";
            }

            if (string.IsNullOrWhiteSpace(requestedTargetId))
            {
                return "replacement_not_allowed_missing_target";
            }

            if (string.IsNullOrWhiteSpace(requestedPlaceId) ||
                string.Equals(requestedPlaceId, "SELF", StringComparison.OrdinalIgnoreCase))
            {
                return "replacement_not_allowed_destination_unresolved";
            }

            if (string.IsNullOrWhiteSpace(_activeP40TaskInstanceId) ||
                previousTaskStatus != TaskStatus.InProgress ||
                previousMode != RobotMode.Autonomous)
            {
                return "replacement_not_allowed_phase_unknown";
            }

            string oldProducer = P40TraceContext.NormalizeProducer(_activeP40Producer);
            if (string.Equals(oldProducer, "voice_command", StringComparison.OrdinalIgnoreCase))
            {
                return "replacement_not_allowed_already_voice_task";
            }

            if (!string.Equals(oldProducer, "assisted_navmesh_selection", StringComparison.OrdinalIgnoreCase))
            {
                return "replacement_not_allowed_active_task_not_autonomous";
            }

            if (!string.IsNullOrWhiteSpace(HeldObjectId))
            {
                return "replacement_not_allowed_holding_object";
            }

            string phase = ResolveP40NavigationPhase();
            if (!string.Equals(phase, "toward_pick", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(phase, "toward_place", StringComparison.OrdinalIgnoreCase)
                    ? "replacement_not_allowed_placing"
                    : "replacement_not_allowed_phase_unknown";
            }

            return string.Empty;
        }

        private RobotAssistanceRoundCoordinator ResolveAssistanceRoundCoordinator()
        {
            if (_assistanceRoundCoordinator != null)
            {
                return _assistanceRoundCoordinator;
            }

            _assistanceRoundCoordinator = FindFirstObjectByType<RobotAssistanceRoundCoordinator>();
            return _assistanceRoundCoordinator;
        }

        private void LogP40AVoicePrePickReplacementRequested(
            P40TraceMetadata trace,
            string requestedTargetId,
            string requestedPlaceId,
            string oldTaskInstanceId,
            string oldRequestId,
            string oldTargetId,
            string oldProducer)
        {
            TiagoExperimentTelemetry.LogEvent(
                "p40a_voice_pre_pick_replacement_requested",
                BuildP40AReplacementPayload(trace, requestedTargetId, requestedPlaceId, oldTaskInstanceId, oldRequestId, oldTargetId, oldProducer, "voice_supervision_pre_pick_replacement"));
        }

        private void LogP40AVoicePrePickReplacementNotAllowed(
            P40TraceMetadata trace,
            string requestedTargetId,
            string requestedPlaceId,
            string reason)
        {
            Dictionary<string, object> payload = BuildP40AReplacementPayload(
                trace,
                requestedTargetId,
                requestedPlaceId,
                _activeP40TaskInstanceId,
                _activeP40RequestId,
                _activeP40TargetId,
                _activeP40Producer,
                reason);
            payload["decision"] = "rejected";
            TiagoExperimentTelemetry.LogEvent("p40a_voice_pre_pick_replacement_not_allowed", payload);
        }

        private Dictionary<string, object> BuildP40AReplacementPayload(
            P40TraceMetadata trace,
            string requestedTargetId,
            string requestedPlaceId,
            string oldTaskInstanceId,
            string oldRequestId,
            string oldTargetId,
            string oldProducer,
            string reason)
        {
            return new Dictionary<string, object>
            {
                ["voice_interaction_id"] = trace?.VoiceInteractionId ?? string.Empty,
                ["new_request_id"] = trace?.RequestId ?? string.Empty,
                ["old_task_instance_id"] = oldTaskInstanceId ?? string.Empty,
                ["old_request_id"] = oldRequestId ?? string.Empty,
                ["old_target_id"] = oldTargetId ?? string.Empty,
                ["new_target_id"] = requestedTargetId ?? string.Empty,
                ["new_destination"] = requestedPlaceId ?? string.Empty,
                ["old_producer"] = P40TraceContext.NormalizeProducer(oldProducer),
                ["new_producer"] = P40TraceContext.NormalizeProducer(trace?.Producer),
                ["reason"] = reason ?? string.Empty,
                ["phase"] = ResolveP40NavigationPhase(),
                ["held_object_id"] = HeldObjectId,
                ["active_task_status"] = _robotController != null ? _robotController.LastTaskStatus.ToString() : string.Empty
            };
        }

        private string ValidatePendingP40BVoiceOrderForPromotion(
            P40BPendingVoiceOrder pending,
            Func<string, string> validateTarget)
        {
            if (pending == null)
            {
                return "unknown";
            }

            if (string.IsNullOrWhiteSpace(pending.TargetId))
            {
                return "target_no_longer_available";
            }

            if (string.IsNullOrWhiteSpace(pending.PlaceTargetId) ||
                string.Equals(pending.PlaceTargetId, "SELF", StringComparison.OrdinalIgnoreCase))
            {
                return "destination_unresolved";
            }

            if (!string.IsNullOrWhiteSpace(HeldObjectId) &&
                string.Equals(HeldObjectId, pending.TargetId, StringComparison.OrdinalIgnoreCase))
            {
                return "target_no_longer_available";
            }

            string reason = validateTarget?.Invoke(pending.TargetId);
            return string.IsNullOrWhiteSpace(reason) ? string.Empty : reason;
        }

        private void LogP40BVoiceOrderDeferred(P40BPendingVoiceOrder pending)
        {
            TiagoExperimentTelemetry.LogEvent(
                "p40b_voice_order_deferred_until_current_place_completed",
                BuildP40BPendingPayload(pending, "holding_or_toward_place"));
        }

        private void LogP40BVoiceOrderDeferRejected(
            P40TraceMetadata trace,
            string requestedTargetId,
            string requestedPlaceId,
            string reason)
        {
            bool pendingAlreadyExists = string.Equals(reason, "pending_voice_order_already_exists", StringComparison.OrdinalIgnoreCase) &&
                                        _pendingP40BVoiceOrder != null;
            string existingPendingTargetId = pendingAlreadyExists ? _pendingP40BVoiceOrder.TargetId : string.Empty;
            string existingPendingDestination = pendingAlreadyExists ? _pendingP40BVoiceOrder.PlaceTargetId : string.Empty;
            TiagoExperimentTelemetry.LogEvent(
                "p40b_voice_order_defer_rejected",
                new Dictionary<string, object>
                {
                    ["voice_interaction_id"] = trace?.VoiceInteractionId ?? string.Empty,
                    ["request_id"] = trace?.RequestId ?? string.Empty,
                    ["pending_target_id"] = pendingAlreadyExists ? existingPendingTargetId : requestedTargetId ?? string.Empty,
                    ["pending_destination"] = pendingAlreadyExists ? existingPendingDestination : requestedPlaceId ?? string.Empty,
                    ["existing_pending_target_id"] = existingPendingTargetId,
                    ["existing_pending_destination"] = existingPendingDestination,
                    ["rejected_target_id"] = requestedTargetId ?? string.Empty,
                    ["rejected_destination"] = requestedPlaceId ?? string.Empty,
                    ["current_task_instance_id"] = _activeP40TaskInstanceId,
                    ["current_target_id"] = _activeP40TargetId,
                    ["held_object_id"] = HeldObjectId,
                    ["reason"] = reason ?? string.Empty,
                    ["phase"] = ResolveP40NavigationPhase()
                });
        }

        private void LogP40BDeferredVoiceOrderPromoted(P40BPendingVoiceOrder pending, string previousCompletedTaskInstanceId)
        {
            Dictionary<string, object> payload = BuildP40BPendingPayload(pending, "pending_voice_order_promoted");
            payload["previous_completed_task_instance_id"] = previousCompletedTaskInstanceId ?? pending.CurrentTaskInstanceId;
            payload["held_object_id"] = HeldObjectId;
            payload["previous_held_object_id"] = pending?.HeldObjectIdAtDeferral ?? string.Empty;
            payload["previous_target_id"] = pending?.CurrentTargetId ?? string.Empty;
            payload["completed_target_id"] = pending?.CurrentTargetId ?? string.Empty;
            TiagoExperimentTelemetry.LogEvent("p40b_deferred_voice_order_promoted_after_place", payload);
            LogP40VoiceLifecycle("adapter_promoted_after_place", pending.Trace, pending.Intent, "accepted", "deferred_voice_order_promoted_after_place");
        }

        private void LogP40BDeferredVoiceOrderDiscarded(P40BPendingVoiceOrder pending, string reason, string previousCompletedTaskInstanceId)
        {
            Dictionary<string, object> payload = BuildP40BPendingPayload(pending, reason);
            payload["previous_completed_task_instance_id"] = previousCompletedTaskInstanceId ?? pending.CurrentTaskInstanceId;
            TiagoExperimentTelemetry.LogEvent("p40b_deferred_voice_order_discarded", payload);
            LogP40VoiceLifecycle("adapter_discarded_after_place", pending.Trace, pending.Intent, "discarded", reason);
        }

        private Dictionary<string, object> BuildP40BPendingPayload(P40BPendingVoiceOrder pending, string reason)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["voice_interaction_id"] = pending?.Trace?.VoiceInteractionId ?? string.Empty,
                ["request_id"] = pending?.Trace?.RequestId ?? string.Empty,
                ["pending_target_id"] = pending?.TargetId ?? string.Empty,
                ["pending_destination"] = pending?.PlaceTargetId ?? string.Empty,
                ["current_task_instance_id"] = pending?.CurrentTaskInstanceId ?? string.Empty,
                ["current_target_id"] = pending?.CurrentTargetId ?? string.Empty,
                ["held_object_id"] = pending?.HeldObjectIdAtDeferral ?? HeldObjectId,
                ["reason"] = reason ?? string.Empty,
                ["phase"] = ResolveP40NavigationPhase()
            };
            AppendPendingVoiceOrderPayload(payload, pending, _pendingP40BVoiceOrderPausedByStop ? "paused" : "active");
            return payload;
        }

        private void AppendPendingVoiceOrderPayload(Dictionary<string, object> payload, P40BPendingVoiceOrder pending, string state)
        {
            if (payload == null)
            {
                return;
            }

            bool hasPending = pending != null;
            bool pendingPaused = hasPending && _pendingP40BVoiceOrderPausedByStop;
            string targetId = pending?.TargetId ?? string.Empty;
            string destinationId = pending?.PlaceTargetId ?? string.Empty;
            payload["pending_voice_order_state"] = state ?? string.Empty;
            payload["has_pending_voice_order"] = hasPending;
            payload["pending_voice_order"] = hasPending;
            payload["has_paused_pending_voice_order"] = pendingPaused;
            payload["pending_target_id"] = targetId;
            payload["pending_destination"] = destinationId;
            payload["paused_pending_target_id"] = pendingPaused ? targetId : string.Empty;
            payload["paused_pending_destination"] = pendingPaused ? destinationId : string.Empty;
            payload["target_id"] = targetId;
            payload["target_alias"] = pending?.Intent?.TargetId ?? targetId;
            payload["destination_id"] = destinationId;
            payload["held_object_id"] = HeldObjectId;
            payload["current_task_instance_id"] = pending?.CurrentTaskInstanceId ?? _activeP40TaskInstanceId;
            payload["current_target_id"] = pending?.CurrentTargetId ?? _activeP40TargetId;
            payload["stop_latch_active"] = _voiceStopLatched;
        }

        private Dictionary<string, object> BuildVoiceStopPayload(
            string rawTranscript,
            string normalizedText,
            string intentKind,
            string decision,
            string reason)
        {
            TargetDescriptor currentTarget = null;
            TargetDescriptor placeTarget = null;
            TaskStatus blackboardTaskStatus = TaskStatus.None;
            bool hasCurrentTarget = _blackboard != null &&
                                    _blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out currentTarget) &&
                                    currentTarget != null;
            bool hasPlaceTarget = _blackboard != null &&
                                  _blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out placeTarget) &&
                                  placeTarget != null;
            bool hasTaskStatus = _blackboard != null &&
                                 _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out blackboardTaskStatus);
            TaskStatus controllerTaskStatus = _robotController != null ? _robotController.LastTaskStatus : TaskStatus.None;
            RobotMode controllerMode = _robotController != null ? _robotController.CurrentMode : RobotMode.Idle;
            string pendingTargetId = _pendingP40BVoiceOrder?.TargetId ?? string.Empty;
            string pendingDestination = _pendingP40BVoiceOrder?.PlaceTargetId ?? string.Empty;
            bool hasActiveTask = controllerTaskStatus == TaskStatus.InProgress ||
                                 controllerMode == RobotMode.Autonomous ||
                                 !string.IsNullOrWhiteSpace(_activeP40TaskInstanceId);

            return new Dictionary<string, object>
            {
                ["raw_transcript"] = rawTranscript ?? string.Empty,
                ["normalized_text"] = normalizedText ?? string.Empty,
                ["intent_kind"] = string.IsNullOrWhiteSpace(intentKind) ? VoiceCommandIntentKind.Stop.ToString() : intentKind,
                ["decision"] = decision ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["active_task_instance_id"] = _activeP40TaskInstanceId,
                ["active_request_id"] = _activeP40RequestId,
                ["active_producer"] = _activeP40Producer,
                ["active_target_id"] = _activeP40TargetId,
                ["active_place_target_id"] = _activeP40PlaceTargetId,
                ["blackboard_current_target"] = hasCurrentTarget ? currentTarget.Id : string.Empty,
                ["blackboard_place_target"] = hasPlaceTarget ? placeTarget.Id : string.Empty,
                ["blackboard_task_status"] = hasTaskStatus ? blackboardTaskStatus.ToString() : string.Empty,
                ["controller_task_status"] = controllerTaskStatus.ToString(),
                ["controller_mode"] = controllerMode.ToString(),
                ["blackboard_mode"] = _blackboard != null ? _blackboard.CurrentMode.ToString() : string.Empty,
                ["phase"] = ResolveP40NavigationPhase(),
                ["held_object_id"] = HeldObjectId,
                ["manipulation_state"] = ManipulationState,
                ["voice_stop_latched"] = _voiceStopLatched,
                ["stop_latch_active"] = _voiceStopLatched,
                ["stop_latch_before"] = _voiceStopLatched,
                ["stop_latch_after"] = _voiceStopLatched,
                ["already_stopped"] = _voiceStopLatched,
                ["stopped_snapshot_valid"] = IsStoppedTaskSnapshotStructurallyValid(_stoppedTaskSnapshot),
                ["stopped_snapshot_phase"] = _stoppedTaskSnapshot?.Phase ?? string.Empty,
                ["stopped_snapshot_target"] = _stoppedTaskSnapshot?.PickupTarget?.Id ?? string.Empty,
                ["stopped_snapshot_place"] = _stoppedTaskSnapshot?.PlaceTarget?.Id ?? string.Empty,
                ["request_source"] = "voice_command",
                ["rejection_reason"] = string.Equals(decision, "rejected", StringComparison.OrdinalIgnoreCase) ? reason ?? string.Empty : string.Empty,
                ["resume_result"] = string.Empty,
                ["task_status"] = controllerTaskStatus.ToString(),
                ["robot_mode"] = controllerMode.ToString(),
                ["has_active_task"] = hasActiveTask,
                ["pending_voice_order"] = _pendingP40BVoiceOrder != null,
                ["has_pending_voice_order"] = _pendingP40BVoiceOrder != null,
                ["has_paused_pending_voice_order"] = _pendingP40BVoiceOrder != null && _pendingP40BVoiceOrderPausedByStop,
                ["pending_target_id"] = pendingTargetId,
                ["pending_destination"] = pendingDestination,
                ["paused_pending_target_id"] = _pendingP40BVoiceOrderPausedByStop ? pendingTargetId : string.Empty,
                ["paused_pending_destination"] = _pendingP40BVoiceOrderPausedByStop ? pendingDestination : string.Empty,
                ["target_id"] = pendingTargetId,
                ["target_alias"] = _pendingP40BVoiceOrder?.Intent?.TargetId ?? pendingTargetId,
                ["destination_id"] = pendingDestination,
                ["adapter_ready"] = _robotController != null && _blackboard != null
            };
        }

        private void CreateStoppedTaskSnapshot(
            string taskInstanceId,
            string requestId,
            string producer,
            string voiceInteractionId,
            TargetDescriptor pickupTarget,
            TargetDescriptor placeTarget,
            string phase,
            bool wasHoldingObject,
            string heldObjectId,
            TaskStatus statusBeforeStop,
            RobotMode modeBeforeStop,
            string rawTranscript,
            string normalizedText,
            string intentKind)
        {
            if (pickupTarget == null || string.IsNullOrWhiteSpace(pickupTarget.Id) ||
                placeTarget == null || string.IsNullOrWhiteSpace(placeTarget.Id) ||
                string.Equals(placeTarget.Id, "SELF", StringComparison.OrdinalIgnoreCase))
            {
                InvalidateStoppedTaskSnapshot("stopped_task_snapshot_missing_target_or_destination");
                return;
            }

            _stoppedTaskSnapshot = new StoppedTaskSnapshot
            {
                TaskInstanceId = taskInstanceId ?? string.Empty,
                RequestId = requestId ?? string.Empty,
                Producer = producer ?? string.Empty,
                VoiceInteractionId = voiceInteractionId ?? string.Empty,
                PickupTarget = pickupTarget,
                PlaceTarget = placeTarget,
                Phase = phase ?? string.Empty,
                WasHoldingObject = wasHoldingObject,
                HeldObjectId = heldObjectId ?? string.Empty,
                StatusBeforeStop = statusBeforeStop,
                ModeBeforeStop = modeBeforeStop
            };

            TiagoExperimentTelemetry.LogEvent(
                "voice_resume_stopped_task_snapshot_created",
                BuildVoiceResumePayload(rawTranscript, normalizedText, intentKind, "snapshot_created", "voice_stop_command", _stoppedTaskSnapshot));
        }

        private static bool IsStoppedTaskSnapshotStructurallyValid(StoppedTaskSnapshot snapshot)
        {
            if (snapshot?.PickupTarget == null ||
                string.IsNullOrWhiteSpace(snapshot.PickupTarget.Id) ||
                snapshot.PlaceTarget == null ||
                string.IsNullOrWhiteSpace(snapshot.PlaceTarget.Id) ||
                string.Equals(snapshot.PlaceTarget.Id, "SELF", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !snapshot.WasHoldingObject ||
                   (!string.IsNullOrWhiteSpace(snapshot.HeldObjectId) &&
                    string.Equals(snapshot.PickupTarget.Id, snapshot.HeldObjectId, StringComparison.OrdinalIgnoreCase));
        }

        private string ValidateStoppedTaskSnapshotForResume(StoppedTaskSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "no_stopped_task_to_resume";
            }

            if (snapshot.PickupTarget == null || string.IsNullOrWhiteSpace(snapshot.PickupTarget.Id))
            {
                return "stopped_task_target_missing";
            }

            if (snapshot.PlaceTarget == null ||
                string.IsNullOrWhiteSpace(snapshot.PlaceTarget.Id) ||
                string.Equals(snapshot.PlaceTarget.Id, "SELF", StringComparison.OrdinalIgnoreCase))
            {
                return "stopped_task_destination_invalid";
            }

            RobotAssistanceRoundCoordinator coordinator = ResolveAssistanceRoundCoordinator();
            if (coordinator != null && coordinator.IsP40BTargetAlreadyDepositedOrCompleted(snapshot.PickupTarget.Id))
            {
                return "stopped_task_target_no_longer_available";
            }

            string currentHeldObjectId = HeldObjectId;
            if (snapshot.WasHoldingObject)
            {
                if (string.IsNullOrWhiteSpace(snapshot.HeldObjectId) ||
                    string.IsNullOrWhiteSpace(currentHeldObjectId) ||
                    !string.Equals(currentHeldObjectId, snapshot.HeldObjectId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(snapshot.PickupTarget.Id, snapshot.HeldObjectId, StringComparison.OrdinalIgnoreCase))
                {
                    return "stopped_task_held_object_inconsistent";
                }
            }
            else if (!string.IsNullOrWhiteSpace(currentHeldObjectId))
            {
                return "stopped_task_unexpected_held_object";
            }

            return string.Empty;
        }

        private void InvalidateStoppedTaskSnapshot(string reason)
        {
            if (_stoppedTaskSnapshot == null)
            {
                ReleaseVoiceStopLatch(reason ?? "snapshot_missing", null);
                return;
            }

            StoppedTaskSnapshot snapshot = _stoppedTaskSnapshot;
            ReleaseVoiceStopLatch(reason ?? "snapshot_invalidated", snapshot);
            _stoppedTaskSnapshot = null;
            TiagoExperimentTelemetry.LogEvent(
                "voice_resume_stopped_task_snapshot_invalidated",
                BuildVoiceResumePayload(string.Empty, string.Empty, VoiceCommandIntentKind.Resume.ToString(), "snapshot_invalidated", reason, snapshot));
        }

        private Dictionary<string, object> BuildVoiceResumePayload(
            string rawTranscript,
            string normalizedText,
            string intentKind,
            string decision,
            string reason,
            StoppedTaskSnapshot snapshot)
        {
            TargetDescriptor currentTarget = null;
            TargetDescriptor placeTarget = null;
            TaskStatus blackboardTaskStatus = TaskStatus.None;
            bool hasCurrentTarget = _blackboard != null &&
                                    _blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out currentTarget) &&
                                    currentTarget != null;
            bool hasPlaceTarget = _blackboard != null &&
                                  _blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out placeTarget) &&
                                  placeTarget != null;
            bool hasTaskStatus = _blackboard != null &&
                                 _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out blackboardTaskStatus);
            TaskStatus controllerTaskStatus = _robotController != null ? _robotController.LastTaskStatus : TaskStatus.None;
            RobotMode controllerMode = _robotController != null ? _robotController.CurrentMode : RobotMode.Idle;
            string pendingTargetId = _pendingP40BVoiceOrder?.TargetId ?? string.Empty;
            string pendingDestination = _pendingP40BVoiceOrder?.PlaceTargetId ?? string.Empty;
            bool hasActiveTask = controllerTaskStatus == TaskStatus.InProgress ||
                                 controllerMode == RobotMode.Autonomous ||
                                 !string.IsNullOrWhiteSpace(_activeP40TaskInstanceId);

            return new Dictionary<string, object>
            {
                ["raw_transcript"] = rawTranscript ?? string.Empty,
                ["normalized_text"] = normalizedText ?? string.Empty,
                ["intent_kind"] = string.IsNullOrWhiteSpace(intentKind) ? VoiceCommandIntentKind.Resume.ToString() : intentKind,
                ["decision"] = decision ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["snapshot_available"] = snapshot != null,
                ["voice_stop_latched"] = _voiceStopLatched,
                ["stop_latch_active"] = _voiceStopLatched,
                ["stop_latch_before"] = _voiceStopLatched,
                ["stop_latch_after"] = _voiceStopLatched,
                ["already_stopped"] = false,
                ["stopped_snapshot_valid"] = IsStoppedTaskSnapshotStructurallyValid(snapshot),
                ["stopped_snapshot_phase"] = snapshot?.Phase ?? string.Empty,
                ["stopped_snapshot_target"] = snapshot?.PickupTarget?.Id ?? string.Empty,
                ["stopped_snapshot_place"] = snapshot?.PlaceTarget?.Id ?? string.Empty,
                ["request_source"] = "voice_command",
                ["rejection_reason"] = string.Equals(decision, "rejected", StringComparison.OrdinalIgnoreCase) ? reason ?? string.Empty : string.Empty,
                ["resume_result"] = reason ?? string.Empty,
                ["task_status"] = controllerTaskStatus.ToString(),
                ["robot_mode"] = controllerMode.ToString(),
                ["snapshot_task_instance_id"] = snapshot?.TaskInstanceId ?? string.Empty,
                ["snapshot_request_id"] = snapshot?.RequestId ?? string.Empty,
                ["snapshot_producer"] = snapshot?.Producer ?? string.Empty,
                ["snapshot_target_id"] = snapshot?.PickupTarget?.Id ?? string.Empty,
                ["snapshot_place_target_id"] = snapshot?.PlaceTarget?.Id ?? string.Empty,
                ["snapshot_phase"] = snapshot?.Phase ?? string.Empty,
                ["snapshot_was_holding_object"] = snapshot?.WasHoldingObject ?? false,
                ["snapshot_held_object_id"] = snapshot?.HeldObjectId ?? string.Empty,
                ["snapshot_status_before_stop"] = snapshot != null ? snapshot.StatusBeforeStop.ToString() : string.Empty,
                ["snapshot_mode_before_stop"] = snapshot != null ? snapshot.ModeBeforeStop.ToString() : string.Empty,
                ["active_task_instance_id"] = _activeP40TaskInstanceId,
                ["active_request_id"] = _activeP40RequestId,
                ["active_producer"] = _activeP40Producer,
                ["active_target_id"] = _activeP40TargetId,
                ["active_place_target_id"] = _activeP40PlaceTargetId,
                ["blackboard_current_target"] = hasCurrentTarget ? currentTarget.Id : string.Empty,
                ["blackboard_place_target"] = hasPlaceTarget ? placeTarget.Id : string.Empty,
                ["blackboard_task_status"] = hasTaskStatus ? blackboardTaskStatus.ToString() : string.Empty,
                ["controller_task_status"] = controllerTaskStatus.ToString(),
                ["controller_mode"] = controllerMode.ToString(),
                ["blackboard_mode"] = _blackboard != null ? _blackboard.CurrentMode.ToString() : string.Empty,
                ["phase"] = ResolveP40NavigationPhase(),
                ["held_object_id"] = HeldObjectId,
                ["manipulation_state"] = ManipulationState,
                ["pending_voice_order"] = _pendingP40BVoiceOrder != null,
                ["has_pending_voice_order"] = _pendingP40BVoiceOrder != null,
                ["has_paused_pending_voice_order"] = _pendingP40BVoiceOrder != null && _pendingP40BVoiceOrderPausedByStop,
                ["has_active_task"] = hasActiveTask,
                ["pending_target_id"] = pendingTargetId,
                ["pending_destination"] = pendingDestination,
                ["paused_pending_target_id"] = _pendingP40BVoiceOrderPausedByStop ? pendingTargetId : string.Empty,
                ["paused_pending_destination"] = _pendingP40BVoiceOrderPausedByStop ? pendingDestination : string.Empty,
                ["target_id"] = pendingTargetId,
                ["target_alias"] = _pendingP40BVoiceOrder?.Intent?.TargetId ?? pendingTargetId,
                ["destination_id"] = pendingDestination,
                ["adapter_ready"] = _robotController != null && _blackboard != null
            };
        }

        public bool TryGetDeclaredAutonomyMetadata(out string driveProfile, out string autonomyPolicy)
        {
            TiagoDriveProfile publicDriveProfile = TiagoDriveProfileSettings.NormalizePublicProfile(_autonomyDriveProfile);
            if (_applyAutonomyProfile && !TiagoDriveProfileSettings.IsCustomProfile(publicDriveProfile))
            {
                driveProfile = TiagoDriveProfileSettings.GetDriveProfileName(_autonomyDriveProfile);
                autonomyPolicy = TiagoDriveProfileSettings.GetAutonomousPolicyName(_autonomyDriveProfile);
                return true;
            }

            driveProfile = string.Empty;
            autonomyPolicy = string.Empty;
            return false;
        }

        private void Awake()
        {
            EnforceAndroidExperimentalDriveProfileIfNeeded("awake");
            LogRuntimeInstanceDiagnostics();

            // 1. Infraestructura base
            _blackboard = new RobotBlackboard();
            _fsm = new RobotFSM();

            // 2. Servicios (Stubs decorados visualmente para la demo)
            INavigationService navigationService = CreateNavigationService();
            IManipulationService manipulationService = CreateManipulationService();
            _safetyServiceStub = new SafetyServiceStub();
            ISafetyService safetyService = _safetyServiceStub;

            if (_enableBaseStateTint && _robotVisualRenderer != null)
            {
                navigationService = new VisualNavigationDecorator(navigationService, _robotVisualRenderer);
                manipulationService = new VisualManipulationDecorator(manipulationService, _robotVisualRenderer);
            }

            // 3. Ensamblado del árbol de comportamiento
            var assembler = new PickupTargetTreeAssembler();
            var btRunner = assembler.Assemble(_blackboard, navigationService, manipulationService, _taskFlow);

            // 4. Provisión externa
            _targetSeeder = new ManualTargetProvisioner(_blackboard);
            _placeTargetSeeder = new PlaceTargetProvisioner(_blackboard);

            // 5. Orquestador integrador
            _robotController = new RobotController(
                _fsm,
                _blackboard,
                btRunner,
                navigationService,
                manipulationService,
                safetyService
            );

            TiagoExperimentTelemetry.LogEvent(
                "bt_task_flow_configured",
                new Dictionary<string, object>
                {
                    ["task_flow"] = _taskFlow.ToString()
                });

            Debug.Log($"[Autonomy] System initialized and wired. taskFlow={_taskFlow}");
        }

        private void LogRuntimeInstanceDiagnostics()
        {
            Debug.Log(
                $"[Autonomy][Diag] Adapter Awake -> name='{gameObject.name}', path='{GetHierarchyPath(transform)}', scene='{gameObject.scene.name}', instanceId={GetInstanceID()}",
                this);

            var adapters = FindObjectsByType<AutonomousRobotAdapter>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Debug.Log($"[Autonomy][Diag] AutonomousRobotAdapter count at startup: {adapters.Length}", this);

            foreach (var adapter in adapters)
            {
                if (adapter == null)
                {
                    continue;
                }

                Debug.Log(
                    $"[Autonomy][Diag] Adapter instance -> name='{adapter.gameObject.name}', path='{GetHierarchyPath(adapter.transform)}', scene='{adapter.gameObject.scene.name}', instanceId={adapter.GetInstanceID()}",
                    adapter);
            }
        }

        private void EnforceAndroidExperimentalDriveProfileIfNeeded(string reason)
        {
            if (!ExperimentDataPathResolver.IsAndroidRuntime() ||
                !string.Equals(gameObject.scene.name, "final_scene", StringComparison.Ordinal))
            {
                return;
            }

            TiagoDriveProfile publicProfile = TiagoDriveProfileSettings.NormalizePublicProfile(_autonomyDriveProfile);
            bool invalid = _applyAutonomyProfile &&
                !TiagoDriveProfileSettings.IsCustomProfile(publicProfile) &&
                (publicProfile == TiagoDriveProfile.Arcade ||
                 TiagoDriveProfileSettings.ResolveAutonomousPolicy(publicProfile) == TiagoAutonomousPolicy.FastDemo);
            if (!invalid)
            {
                return;
            }

            var payload = new Dictionary<string, object>
            {
                ["reason"] = reason ?? string.Empty,
                ["scene"] = gameObject.scene.name,
                ["platform"] = Application.platform.ToString(),
                ["serialized_profile_before"] = _autonomyDriveProfile.ToString(),
                ["active_drive_profile_before"] = TiagoDriveProfileSettings.GetDriveProfileName(_autonomyDriveProfile),
                ["active_autonomous_policy_before"] = TiagoDriveProfileSettings.GetAutonomousPolicyName(_autonomyDriveProfile),
                ["auto_corrected_to"] = TiagoDriveProfile.Realistic.ToString()
            };
            TiagoExperimentTelemetry.LogEvent("p45d_profile_invalid_for_experiment", payload);
            Debug.LogWarning(
                $"[Autonomy] p45d_profile_invalid_for_experiment | scene={gameObject.scene.name} platform={Application.platform} profile={_autonomyDriveProfile} policy={TiagoDriveProfileSettings.GetAutonomousPolicyName(_autonomyDriveProfile)} corrected_to=Realistic",
                this);
            _autonomyDriveProfile = TiagoDriveProfile.Realistic;
        }

        private void LogP45DProfileAudit(
            string activeDriveProfile,
            string activeAutonomousPolicy,
            bool fastDemo,
            float maxLinear,
            string reason)
        {
            var payload = new Dictionary<string, object>
            {
                ["active_drive_profile"] = activeDriveProfile ?? string.Empty,
                ["active_autonomous_policy"] = activeAutonomousPolicy ?? string.Empty,
                ["fastDemo"] = fastDemo,
                ["maxLinear"] = maxLinear,
                ["scene"] = gameObject.scene.name,
                ["platform"] = Application.platform.ToString(),
                ["reason"] = reason ?? string.Empty
            };
            TiagoExperimentTelemetry.LogEvent("p45d_robot_drive_profile_audit", payload);
            Debug.Log(
                $"[Autonomy] p45d_robot_drive_profile_audit | active_drive_profile={activeDriveProfile} active_autonomous_policy={activeAutonomousPolicy} fastDemo={fastDemo} maxLinear={maxLinear:F2} scene={gameObject.scene.name} platform={Application.platform}",
                this);

            if (ExperimentDataPathResolver.IsAndroidRuntime() &&
                string.Equals(gameObject.scene.name, "final_scene", StringComparison.Ordinal) &&
                (string.Equals(activeDriveProfile, "Arcade", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(activeAutonomousPolicy, "FastDemo", StringComparison.OrdinalIgnoreCase) ||
                 fastDemo))
            {
                TiagoExperimentTelemetry.LogEvent("p45d_profile_invalid_for_experiment", payload);
                Debug.LogWarning(
                    $"[Autonomy] p45d_profile_invalid_for_experiment | scene={gameObject.scene.name} platform={Application.platform} active_drive_profile={activeDriveProfile} active_autonomous_policy={activeAutonomousPolicy} fastDemo={fastDemo}",
                    this);
            }
        }

        private static string GetHierarchyPath(Transform current)
        {
            if (current == null)
            {
                return string.Empty;
            }

            string path = current.name;

            while (current.parent != null)
            {
                current = current.parent;
                path = $"{current.name}/{path}";
            }

            return path;
        }

        private INavigationService CreateNavigationService()
        {
            if (_navigationMode == NavigationMode.TiagoNavMeshGuided)
            {
                Transform navigationReference = _navigationReference != null ? _navigationReference : transform;
                var driveBridge = new TiagoDifferentialDriveBridge(
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

                if (!driveBridge.IsValid)
                {
                    _driveBridge = null;
                    Debug.LogWarning("[Autonomy] Tiago NavMesh-guided navigation selected but wheel configuration is invalid. Falling back to NavigationServiceStub.", this);
                    return new NavigationServiceStub(_navigationTicks);
                }

                if (navigationReference == null)
                {
                    Debug.LogWarning("[Autonomy] Tiago NavMesh-guided navigation selected but navigation reference is missing. Falling back to NavigationServiceStub.", this);
                    return new NavigationServiceStub(_navigationTicks);
                }

                driveBridge.Initialize();
                _driveBridge = driveBridge;

                float maxLinearSpeed = _maxLinearSpeed;
                float maxAngularSpeed = _maxAngularSpeed;
                float accelerationLimit = _accelerationLimit;
                float decelerationLimit = _decelerationLimit;
                float angularGain = _angularGain;
                float pathLookAheadDistance = _pathLookAheadDistance;
                TiagoDriveProfile publicDriveProfile = TiagoDriveProfileSettings.NormalizePublicProfile(_autonomyDriveProfile);
                bool useCustomProfile = TiagoDriveProfileSettings.IsCustomProfile(publicDriveProfile);
                string activeDriveProfile = _applyAutonomyProfile && !useCustomProfile
                    ? TiagoDriveProfileSettings.GetDriveProfileName(_autonomyDriveProfile)
                    : "Custom";
                string activeAutonomousPolicy = _applyAutonomyProfile && !useCustomProfile
                    ? TiagoDriveProfileSettings.GetAutonomousPolicyName(_autonomyDriveProfile)
                    : "Custom";
                bool fastDemoTrackingEnabled = false;
                float fastMinLookaheadDistance = 0f;
                float fastMaxLookaheadDistance = 0f;
                float fastLookaheadSpeedFactor = 0f;
                float fastCornerBrakeDistance = 0f;
                float fastMediumTurnAngleDeg = 0f;
                float fastSevereTurnAngleDeg = 0f;
                float fastMinCornerSpeed = 0f;
                float fastHeadingSpeedLimitStartAngleDeg = 0f;
                float fastCornerCrawlAngleDeg = 0f;

                if (_applyAutonomyProfile && !useCustomProfile)
                {
                    TiagoDriveProfileSettings settings = TiagoDriveProfileSettings.ResolveAutonomy(_autonomyDriveProfile);
                    maxLinearSpeed = settings.MaxLinearSpeed;
                    maxAngularSpeed = settings.MaxAngularSpeed;
                    accelerationLimit = settings.Acceleration;
                    decelerationLimit = settings.FastDeceleration > 0f ? settings.FastDeceleration : settings.Acceleration;
                    angularGain = settings.AngularGain;
                    pathLookAheadDistance = settings.PathLookAheadDistance;
                    fastDemoTrackingEnabled = settings.FastDemoTracking;
                    fastMinLookaheadDistance = settings.FastMinLookaheadDistance;
                    fastMaxLookaheadDistance = settings.FastMaxLookaheadDistance;
                    fastLookaheadSpeedFactor = settings.FastLookaheadSpeedFactor;
                    fastCornerBrakeDistance = settings.FastCornerBrakeDistance;
                    fastMediumTurnAngleDeg = settings.FastMediumTurnAngleDeg;
                    fastSevereTurnAngleDeg = settings.FastSevereTurnAngleDeg;
                    fastMinCornerSpeed = settings.FastMinCornerSpeed;
                    fastHeadingSpeedLimitStartAngleDeg = settings.FastHeadingSpeedLimitStartAngleDeg;
                    fastCornerCrawlAngleDeg = settings.FastCornerCrawlAngleDeg;
                }

                string profilePayload =
                    $"active_drive_profile={activeDriveProfile} active_autonomous_policy={activeAutonomousPolicy} serializedProfile={_autonomyDriveProfile} maxLinear={maxLinearSpeed:F2} maxAngular={maxAngularSpeed:F2} accel={accelerationLimit:F2} decel={decelerationLimit:F2} angularGain={angularGain:F2} lookAhead={pathLookAheadDistance:F2} fastDemo={fastDemoTrackingEnabled} fastLookahead=({fastMinLookaheadDistance:F2},{fastMaxLookaheadDistance:F2},factor={fastLookaheadSpeedFactor:F2}) fastCornerBrake={fastCornerBrakeDistance:F2} fastTurnAngles=({fastMediumTurnAngleDeg:F1},{fastSevereTurnAngleDeg:F1}) fastMinCornerSpeed={fastMinCornerSpeed:F2} applyProfile={_applyAutonomyProfile}";
                Debug.Log($"[Autonomy] AutonomyProfile | applied {profilePayload}", this);
                TiagoExperimentTelemetry.RecordEvent("autonomy_profile_applied", profilePayload);
                LogP45DProfileAudit(
                    activeDriveProfile,
                    activeAutonomousPolicy,
                    fastDemoTrackingEnabled,
                    maxLinearSpeed,
                    "create_navigation_service");
                Debug.Log("[Autonomy] Navigation service: Tiago NavMesh-guided differential drive");
                _tiagoNavMeshNavigationService = new TiagoNavMeshNavigationService(
                    navigationReference,
                    driveBridge,
                    _arrivalDistance,
                    _targetSampleRadius,
                    _waypointReachDistance,
                    pathLookAheadDistance,
                    _slowdownDistance,
                    maxLinearSpeed,
                    maxAngularSpeed,
                    accelerationLimit,
                    decelerationLimit,
                    angularGain,
                    _rotateInPlaceAngleDeg,
                    _autonomyDriveProfile.ToString(),
                    (int)_autonomyDriveProfile,
                    activeDriveProfile,
                    activeAutonomousPolicy,
                    fastDemoTrackingEnabled,
                    fastMinLookaheadDistance,
                    fastMaxLookaheadDistance,
                    fastLookaheadSpeedFactor,
                    fastCornerBrakeDistance,
                    fastMediumTurnAngleDeg,
                    fastSevereTurnAngleDeg,
                    fastMinCornerSpeed,
                    fastHeadingSpeedLimitStartAngleDeg,
                    fastCornerCrawlAngleDeg,
                    _headingOffsetDegrees,
                    _avoidanceDetectionDistance,
                    _avoidanceRayAngleDegrees,
                    _avoidanceAngularStrength,
                    _avoidanceLinearReductionFactor,
                    _avoidanceLayerMask,
                    enablePathCornerSmoothing: _enablePathCornerSmoothing,
                    cornerSmoothingAngleThresholdDeg: _cornerSmoothingAngleThresholdDeg,
                    cornerSmoothingRadius: _cornerSmoothingRadius,
                    cornerSmoothingSamplesPerCorner: _cornerSmoothingSamplesPerCorner,
                    cornerSmoothingMinSegmentLength: _cornerSmoothingMinSegmentLength,
                    cornerSmoothingNavMeshSampleDistance: _cornerSmoothingNavMeshSampleDistance,
                    cornerSmoothingValidateSegments: _cornerSmoothingValidateSegments,
                    cornerSmoothingClearanceAware: _cornerSmoothingClearanceAware,
                    cornerSmoothingMinNavMeshEdgeClearance: _cornerSmoothingMinNavMeshEdgeClearance,
                    cornerSmoothingMaxControlPointOffset: _cornerSmoothingMaxControlPointOffset,
                    cornerSmoothingControlPointOffsetStep: _cornerSmoothingControlPointOffsetStep,
                    cornerSmoothingMaxOffsetAttempts: _cornerSmoothingMaxOffsetAttempts,
                    useLastValidSmoothedPathOnSmoothingFailure: _useLastValidSmoothedPathOnSmoothingFailure,
                    maxLastValidSmoothedPathAgeSeconds: _maxLastValidSmoothedPathAgeSeconds,
                    maxLastValidSmoothedPathStartDistance: _maxLastValidSmoothedPathStartDistance,
                    maxLastValidSmoothedPathTargetDistance: _maxLastValidSmoothedPathTargetDistance,
                    lockActivePathDuringTracking: _lockActivePathDuringTracking,
                    replanIfDistanceFromActivePathExceeds: _replanIfDistanceFromActivePathExceeds,
                    replanIfTargetMovedMoreThan: _replanIfTargetMovedMoreThan,
                    minSecondsBetweenAutomaticReplans: _minSecondsBetweenAutomaticReplans,
                    allowPeriodicReplanDuringTracking: _allowPeriodicReplanDuringTracking,
                    enablePathClearanceOffset: _enablePathClearanceOffset,
                    pathClearanceOffsetMinEdgeDistance: _pathClearanceOffsetMinEdgeDistance,
                    pathClearanceOffsetSearchRadius: _pathClearanceOffsetSearchRadius,
                    pathClearanceOffsetStep: _pathClearanceOffsetStep,
                    pathClearanceOffsetMaxCandidatesPerSide: _pathClearanceOffsetMaxCandidatesPerSide,
                    pathClearanceOffsetNavMeshSampleDistance: _pathClearanceOffsetNavMeshSampleDistance,
                    pathClearanceOffsetValidateSegments: _pathClearanceOffsetValidateSegments,
                    pathClearanceOffsetSkipEndpoints: _pathClearanceOffsetSkipEndpoints,
                    pathClearanceOffsetMinPointSpacing: _pathClearanceOffsetMinPointSpacing,
                    pathClearanceOffsetOnlyIfBelowMinEdgeDistance: _pathClearanceOffsetOnlyIfBelowMinEdgeDistance,
                    pathClearanceOffsetMaxDeviationFromCenteredPath: _pathClearanceOffsetMaxDeviationFromCenteredPath,
                    pathClearanceOffsetMaxLocalHeadingChangeDeg: _pathClearanceOffsetMaxLocalHeadingChangeDeg,
                    enableUserSafetyStop: _enableUserSafetyStop,
                    userSafetyStopDetectXRRig: _userSafetyStopDetectXRRig,
                    userSafetyStopUseIgnoreRaycastLayer: _userSafetyStopUseIgnoreRaycastLayer,
                    userSafetyStopRootNameContains: _userSafetyStopRootNameContains,
                    userSafetyStopRadius: _userSafetyStopRadius,
                    userSafetyStopPathLookaheadDistance: _userSafetyStopPathLookaheadDistance,
                    userSafetyResumeRadius: _userSafetyResumeRadius,
                    userSafetyStopTimeoutSeconds: _userSafetyStopTimeoutSeconds,
                    userSafetyStopCommandZeroVelocity: _userSafetyStopCommandZeroVelocity);
                return _tiagoNavMeshNavigationService;
            }

            Debug.Log("[Autonomy] Navigation service: Stub");
            _driveBridge = null;
            _tiagoNavMeshNavigationService = null;
            return new NavigationServiceStub(_navigationTicks);
        }

        private IManipulationService CreateManipulationService()
        {
            _tiagoUnityManipulationService = null;

            if (_manipulationMode == ManipulationMode.TiagoUnityScene)
            {
                Transform robotReference = NavigationReference;
                Transform anchor = _manipulationAnchor != null ? _manipulationAnchor : robotReference;
                LogManipulationAnchorConfiguration(anchor, robotReference);

                _tiagoUnityManipulationService = new TiagoUnityManipulationService(
                    robotReference,
                    anchor,
                    _manipulationRange,
                    _placeOffset,
                    _requirePlaceWithinRange,
                    _maxExpectedPlaceDistance,
                    _placeDistanceSafetyMargin,
                    _maxRelaxedPlaceDistance,
                    _placeFailureRecoveryMode,
                    _disableXRGrabWhileHeld,
                    _disableHeldObjectCollidersWhileHeld,
                    _heldLocalPositionOffset,
                    _heldLocalEulerOffset,
                    _heldObjectAlignmentMode,
                    _enforceHeldPoseWhileHolding,
                    _heldPoseDriftWarningThreshold,
                    _heldPoseDriftCorrectionThreshold,
                    _enableDepositedBoxNavMeshObstacle,
                    _depositedBoxObstacleSizePadding,
                    _depositedBoxObstacleCarve,
                    _depositedBoxObstacleCarveMoveThreshold);
                LogManipulationServiceConfig(anchor);
                return _tiagoUnityManipulationService;
            }

            Debug.Log("[Autonomy] Manipulation service: Stub");
            return new ManipulationServiceStub(_manipulationTicks);
        }

        private void LogManipulationAnchorConfiguration(Transform anchor, Transform robotReference)
        {
            string anchorName = anchor != null ? anchor.name : "none";
            Debug.Log($"[Autonomy] Manipulation service: Tiago Unity scene MVP | anchor='{anchorName}' range={_manipulationRange:F2} disableXRGrabWhileHeld={_disableXRGrabWhileHeld} disableHeldObjectCollidersWhileHeld={_disableHeldObjectCollidersWhileHeld} heldObjectAlignmentMode={_heldObjectAlignmentMode} heldLocalPositionOffset={_heldLocalPositionOffset} heldLocalEulerOffset={_heldLocalEulerOffset} enforceHeldPoseWhileHolding={_enforceHeldPoseWhileHolding}", this);

            if (anchor == null || anchor == robotReference)
            {
                Debug.LogWarning("[Autonomy] Manipulation anchor is using NavigationReference fallback. Preferred setup: create an empty child under gripper_right_grasping_frame or gripper_left_grasping_frame and assign that child as _manipulationAnchor.", this);
                return;
            }

            if (IsDiscouragedManipulationAnchor(anchor))
            {
                Debug.LogWarning($"[Autonomy] Manipulation anchor '{anchorName}' is not recommended. Use an empty child under gripper_right_grasping_frame or gripper_left_grasping_frame; avoid Visuals, Collisions, finger links, base_link, and NavigationReference.", this);
            }
        }

        private void LogManipulationServiceConfig(Transform anchor)
        {
            TiagoExperimentTelemetry.LogEvent(
                "manipulation_service_config",
                new Dictionary<string, object>
                {
                    ["manipulation_mode"] = _manipulationMode.ToString(),
                    ["service_type"] = nameof(TiagoUnityManipulationService),
                    ["manipulation_anchor_name"] = anchor != null ? anchor.name : string.Empty,
                    ["manipulation_range"] = _manipulationRange,
                    ["require_place_within_range"] = _requirePlaceWithinRange,
                    ["max_expected_place_distance"] = _maxExpectedPlaceDistance,
                    ["place_distance_safety_margin"] = _placeDistanceSafetyMargin,
                    ["max_relaxed_place_distance"] = _maxRelaxedPlaceDistance,
                    ["place_failure_recovery_mode"] = _placeFailureRecoveryMode.ToString(),
                    ["disable_xr_grab_while_held"] = _disableXRGrabWhileHeld,
                    ["disable_held_object_colliders_while_held"] = _disableHeldObjectCollidersWhileHeld,
                    ["held_object_alignment_mode"] = _heldObjectAlignmentMode.ToString(),
                    ["held_local_position_offset"] = _heldLocalPositionOffset,
                    ["held_local_euler_offset"] = _heldLocalEulerOffset,
                    ["enforce_held_pose_while_holding"] = _enforceHeldPoseWhileHolding,
                    ["held_pose_drift_warning_threshold"] = _heldPoseDriftWarningThreshold,
                    ["held_pose_drift_correction_threshold"] = _heldPoseDriftCorrectionThreshold,
                    ["enable_deposited_box_navmesh_obstacle"] = _enableDepositedBoxNavMeshObstacle,
                    ["deposited_box_obstacle_size_padding"] = _depositedBoxObstacleSizePadding,
                    ["deposited_box_obstacle_carve"] = _depositedBoxObstacleCarve,
                    ["deposited_box_obstacle_carve_move_threshold"] = _depositedBoxObstacleCarveMoveThreshold
                });
        }

        private static bool IsDiscouragedManipulationAnchor(Transform anchor)
        {
            if (anchor == null)
            {
                return true;
            }

            string name = anchor.name.ToLowerInvariant();
            return name == "visuals" ||
                   name == "collisions" ||
                   name.Contains("finger") ||
                   name.Contains("base_link");
        }

        private void Start()
        {
            if (_seedInitialTarget)
            {
                var target = new TargetDescriptor(
                    _initialTargetId, 
                    new System.Numerics.Vector3(_initialTargetPosition.x, _initialTargetPosition.y, _initialTargetPosition.z)
                );
                
                _targetSeeder.SeedTarget(target);
                Debug.Log($"[Autonomy] Initial target seeded: {_initialTargetId}");
            }

            if (_forceAutonomousOnStart)
            {
                TrySetAutonomousMode();
            }
        }

        private void Update()
        {
            if (ExperimentRuntimePauseCoordinator.IsExperimentPaused || _autonomyTickFaultLatched)
            {
                return;
            }

            try
            {
                if (_robotController == null)
                {
                    HandleAutonomyTickFault("robot_controller_missing", null);
                    return;
                }

                _robotController.Tick();
                RefreshManipulationDebugState();
                ObserveP40TaskLifecycle();
            }
            catch (Exception exception)
            {
                HandleAutonomyTickFault("tick_exception", exception);
                return;
            }

            if (_debugDumpLastPlaceContextNow)
            {
                _debugDumpLastPlaceContextNow = false;
                _tiagoUnityManipulationService?.DebugDumpLastPlaceContext();
            }

            if (_showHeartbeat)
            {
                _frameCounter++;
                if (_frameCounter >= 100)
                {
                    Debug.Log("[Autonomy] Heartbeat: Tick active.");
                    _frameCounter = 0;
                }
            }
        }

        private void RefreshManipulationDebugState()
        {
            if (_tiagoUnityManipulationService == null)
            {
                _heldObjectDebug = null;
                _heldObjectIdDebug = string.Empty;
                _manipulationStateDebug = _manipulationMode == ManipulationMode.Stub ? "Stub" : "Unavailable";
                return;
            }

            _heldObjectDebug = _tiagoUnityManipulationService.HeldObject;
            _heldObjectIdDebug = _tiagoUnityManipulationService.HeldObjectId;
            _manipulationStateDebug = _tiagoUnityManipulationService.ManipulationState;
        }

        /// <summary>
        /// Intenta cambiar el modo del robot a Autónomo. 
        /// Expuesto para la capa de demo.
        /// </summary>
        public void TrySetAutonomousMode()
        {
            _fsm.TryChangeMode(RobotMode.Autonomous);
        }

        private void LogAutonomyRequestAdapterEvent(
            string eventType,
            string requestedTargetId,
            string requestedPlaceId,
            TaskStatus previousTaskStatus,
            RobotMode previousMode,
            bool wasTerminal,
            string rejectionReason)
        {
            Dictionary<string, object> payload = BuildAutonomyRequestAdapterPayload(
                requestedTargetId,
                requestedPlaceId,
                previousTaskStatus,
                previousMode,
                wasTerminal,
                rejectionReason,
                modeChanged: false);
            TiagoExperimentTelemetry.LogEvent(eventType, payload);
            Debug.Log($"[Autonomy] {eventType} | target={requestedTargetId} place={requestedPlaceId} previous_task_status={previousTaskStatus} previous_mode={previousMode} bt_terminal={wasTerminal} rejection_reason={rejectionReason}", this);
        }

        private static Dictionary<string, object> BuildAutonomyRequestAdapterPayload(
            string requestedTargetId,
            string requestedPlaceId,
            TaskStatus previousTaskStatus,
            RobotMode previousMode,
            bool wasTerminal,
            string rejectionReason,
            bool modeChanged)
        {
            return new Dictionary<string, object>
            {
                ["requested_target_id"] = requestedTargetId ?? string.Empty,
                ["requested_place_target_id"] = requestedPlaceId ?? string.Empty,
                ["previous_task_status"] = previousTaskStatus.ToString(),
                ["previous_robot_mode"] = previousMode.ToString(),
                ["bt_terminal"] = wasTerminal,
                ["rejection_reason"] = rejectionReason ?? string.Empty,
                ["mode_changed"] = modeChanged
            };
        }

        private void LogP40AdapterGateSnapshot(
            string phase,
            P40TraceMetadata trace,
            string requestedTargetId,
            string requestedPlaceId,
            TaskStatus previousTaskStatus,
            RobotMode previousMode,
            bool wasTerminal,
            string rejectionReason,
            bool accepted)
        {
            Dictionary<string, object> payload = BuildP40AdapterSnapshotPayload(
                trace,
                requestedTargetId,
                requestedPlaceId,
                previousTaskStatus,
                previousMode,
                wasTerminal,
                rejectionReason);
            payload["phase"] = phase ?? string.Empty;
            payload["decision"] = accepted ? "accepted" : string.IsNullOrWhiteSpace(rejectionReason) ? "received" : "rejected";
            payload["accepted"] = accepted;
            TiagoExperimentTelemetry.LogEvent("p40_trace_adapter_gate_snapshot", payload);
        }

        private Dictionary<string, object> BuildP40AdapterSnapshotPayload(
            P40TraceMetadata trace,
            string requestedTargetId,
            string requestedPlaceId,
            TaskStatus previousTaskStatus,
            RobotMode previousMode,
            bool wasTerminal,
            string rejectionReason)
        {
            TargetDescriptor currentTarget = null;
            TargetDescriptor placeTarget = null;
            TaskStatus blackboardTaskStatus = TaskStatus.None;
            bool hasCurrentTarget = _blackboard != null && _blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out currentTarget) && currentTarget != null;
            bool hasPlaceTarget = _blackboard != null && _blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out placeTarget) && placeTarget != null;
            bool hasTaskStatus = _blackboard != null && _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out blackboardTaskStatus);
            bool robotBusy = previousTaskStatus == TaskStatus.InProgress || previousMode == RobotMode.Autonomous;
            Dictionary<string, object> payload = P40TraceContext.ToPayload(trace);
            payload["requested_target_id"] = requestedTargetId ?? string.Empty;
            payload["requested_place_target_id"] = requestedPlaceId ?? string.Empty;
            payload["previous_task_status"] = previousTaskStatus.ToString();
            payload["previous_robot_mode"] = previousMode.ToString();
            payload["bt_terminal"] = wasTerminal;
            payload["rejection_reason"] = rejectionReason ?? string.Empty;
            payload["active_task_instance_id"] = _activeP40TaskInstanceId;
            payload["active_target_id"] = _activeP40TargetId;
            payload["active_place_target_id"] = _activeP40PlaceTargetId;
            payload["held_object_id"] = HeldObjectId;
            payload["manipulation_state"] = ManipulationState;
            payload["robot_busy"] = robotBusy;
            payload["blackboard_current_target"] = hasCurrentTarget ? currentTarget.Id : string.Empty;
            payload["blackboard_place_target"] = hasPlaceTarget ? placeTarget.Id : string.Empty;
            payload["blackboard_task_status"] = hasTaskStatus ? blackboardTaskStatus.ToString() : string.Empty;
            payload["navigation_target_id"] = _activeP40TargetId;
            payload["navigation_phase"] = ResolveP40NavigationPhase();
            payload["frame_count"] = Time.frameCount;
            AddP40NavigationLiveness(payload);
            return payload;
        }

        private void LogP40VoiceLifecycle(
            string phase,
            P40TraceMetadata trace,
            MultimodalTaskIntent intent,
            string decision,
            string reason)
        {
            if (trace == null || string.IsNullOrWhiteSpace(trace.VoiceInteractionId))
            {
                return;
            }

            Dictionary<string, object> payload = P40TraceContext.ToPayload(trace);
            payload["phase"] = phase ?? string.Empty;
            payload["intent"] = trace.IntentKind;
            payload["target_id"] = intent != null ? intent.TargetId : string.Empty;
            payload["object_category"] = intent != null ? intent.ObjectCategory : string.Empty;
            payload["submitted_destination"] = intent != null ? intent.PlaceTargetId : trace.SubmittedDestination;
            payload["destination"] = payload["submitted_destination"];
            payload["decision"] = decision ?? string.Empty;
            payload["reason"] = reason ?? string.Empty;
            payload["active_task_instance_id"] = _activeP40TaskInstanceId;
            TiagoExperimentTelemetry.LogEvent("p40_trace_voice_command_lifecycle", payload);
            trace.LastDecision = decision ?? string.Empty;
            trace.LastReason = reason ?? string.Empty;
            P40TraceContext.RecordLastVoiceCommand(trace);
        }

        private void LogP40TaskLifecycle(
            string phase,
            P40TraceMetadata trace,
            string taskInstanceId,
            string targetId,
            string placeTargetId,
            string previousStatus,
            string newStatus,
            string reason)
        {
            Dictionary<string, object> payload = P40TraceContext.ToPayload(trace);
            payload["phase"] = phase ?? string.Empty;
            payload["task_instance_id"] = taskInstanceId ?? string.Empty;
            payload["request_id"] = trace?.RequestId ?? _activeP40RequestId;
            payload["producer"] = trace?.Producer ?? _activeP40Producer;
            payload["voice_interaction_id"] = trace?.VoiceInteractionId ?? _activeP40VoiceInteractionId;
            payload["target_id"] = targetId ?? string.Empty;
            payload["place_target_id"] = placeTargetId ?? string.Empty;
            payload["previous_status"] = previousStatus ?? string.Empty;
            payload["new_status"] = newStatus ?? string.Empty;
            payload["reason"] = reason ?? string.Empty;
            TiagoExperimentTelemetry.LogEvent("p40_trace_task_lifecycle", payload);
        }

        private void ObserveP40TaskLifecycle()
        {
            if (string.IsNullOrWhiteSpace(_activeP40TaskInstanceId) || _robotController == null)
            {
                return;
            }

            TaskStatus current = _robotController.LastTaskStatus;
            if (current == _lastObservedP40TaskStatus)
            {
                return;
            }

            string phase = current == TaskStatus.InProgress
                ? "task_started"
                : current == TaskStatus.Succeeded
                    ? "task_completed"
                    : current == TaskStatus.Failed
                        ? "task_failed"
                        : "task_state_changed";
            LogP40TaskLifecycle(
                phase,
                new P40TraceMetadata
                {
                    RequestId = _activeP40RequestId,
                    Producer = _activeP40Producer,
                    VoiceInteractionId = _activeP40VoiceInteractionId
                },
                _activeP40TaskInstanceId,
                _activeP40TargetId,
                _activeP40PlaceTargetId,
                _lastObservedP40TaskStatus.ToString(),
                current.ToString(),
                "observed_adapter_task_status_change");
            _lastObservedP40TaskStatus = current;
            if (current == TaskStatus.Succeeded || current == TaskStatus.Failed)
            {
                bool wasResumedFromStop = _activeP40TaskResumedFromStop;
                InvalidateStoppedTaskSnapshot(current == TaskStatus.Succeeded ? "task_completed" : "task_failed");
                _fsm?.TryChangeMode(RobotMode.Idle);
                _blackboard?.SetMode(RobotMode.Idle);
                _blackboard?.Remove(TaskBlackboardKeys.CurrentTarget);
                _blackboard?.Remove(TaskBlackboardKeys.PlaceTarget);
                _blackboard?.Remove(TaskBlackboardKeys.ResumeHeldObjectId);
                if (wasResumedFromStop)
                {
                    TiagoExperimentTelemetry.LogEvent(
                        "voice_resume_post_completion_cleanup_verified",
                        new Dictionary<string, object>
                        {
                            ["task_status"] = current.ToString(),
                            ["task_instance_id"] = _activeP40TaskInstanceId,
                            ["target_id"] = _activeP40TargetId,
                            ["place_target_id"] = _activeP40PlaceTargetId,
                            ["blackboard_mode"] = _blackboard != null ? _blackboard.CurrentMode.ToString() : string.Empty,
                            ["controller_mode"] = _robotController != null ? _robotController.CurrentMode.ToString() : string.Empty,
                            ["resume_held_object_cleared"] = _blackboard == null || !_blackboard.TryGet(TaskBlackboardKeys.ResumeHeldObjectId, out string _),
                            ["current_target_cleared"] = _blackboard == null || !_blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out TargetDescriptor _),
                            ["place_target_cleared"] = _blackboard == null || !_blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out TargetDescriptor _)
                        });
                }
                _activeP40TaskInstanceId = string.Empty;
                _activeP40RequestId = string.Empty;
                _activeP40Producer = "unknown";
                _activeP40VoiceInteractionId = string.Empty;
                _activeP40TargetId = string.Empty;
                _activeP40PlaceTargetId = string.Empty;
                _activeP40TaskResumedFromStop = false;
            }
        }

        private string ResolveP40NavigationPhase()
        {
            if (string.IsNullOrWhiteSpace(_activeP40TaskInstanceId))
            {
                return "idle";
            }

            return string.IsNullOrWhiteSpace(HeldObjectId) ? "toward_pick" : "toward_place";
        }

        private void AddP40NavigationLiveness(Dictionary<string, object> payload)
        {
            TiagoExperimentTelemetry.Snapshot latest = TiagoExperimentTelemetry.Latest;
            payload["path_source"] = _tiagoNavMeshNavigationService != null ? _tiagoNavMeshNavigationService.LastActivePathSource.ToString() : string.Empty;
            payload["path_point_count"] = _tiagoNavMeshNavigationService != null ? _tiagoNavMeshNavigationService.LastActivePathPoints.Count : 0;
            payload["active_segment_index"] = _tiagoNavMeshNavigationService != null ? _tiagoNavMeshNavigationService.LastActiveSegmentIndex : -1;
            payload["remaining_distance"] = latest.RemainingDistance;
            payload["navigation_target_id"] = _activeP40TargetId;
            payload["path_belongs_to_current_task"] = !string.IsNullOrWhiteSpace(_activeP40TaskInstanceId);
        }

        private void OnDestroy()
        {
            _robotController?.Dispose();
        }

        private void OnDrawGizmos()
        {
            if (!isActiveAndEnabled || _tiagoNavMeshNavigationService == null)
            {
                return;
            }

            if (_debugDrawNavMeshPath)
            {
                DrawNavMeshPathGizmos();
            }

            if (!_debugDrawObstacleRays)
            {
                return;
            }

            var rays = _tiagoNavMeshNavigationService.LastObstacleRayDebugStates;
            int rayCount = rays?.Count ?? 0;
            if (rayCount <= 0)
            {
                return;
            }

            for (int i = 0; i < rayCount; i++)
            {
                TiagoNavMeshNavigationService.ObstacleRayDebugState ray = rays[i];
                Vector3 direction = ray.Direction.sqrMagnitude > 0.0001f ? ray.Direction.normalized : Vector3.forward;
                Vector3 end = ray.Origin + direction * Mathf.Max(ray.Length, 0.05f);

                Gizmos.color = new Color(0.1f, 0.45f, 1f, 1f);
                Gizmos.DrawSphere(ray.Origin, 0.055f);

                Gizmos.color = ray.Hit ? Color.red : new Color(0f, 1f, 0.35f, 1f);
                Gizmos.DrawLine(ray.Origin, end);

                if (ray.Hit)
                {
                    Gizmos.color = new Color(1f, 0.25f, 0.1f, 1f);
                    Gizmos.DrawSphere(ray.HitPoint, 0.075f);
                }
            }
        }

        private void HandleAutonomyTickFault(string reason, Exception exception)
        {
            if (_autonomyTickFaultLatched)
            {
                return;
            }

            _autonomyTickFaultLatched = true;
            RobotMode previousMode = AuthoritativeRobotMode ?? RobotMode.Idle;
            TaskStatus previousTaskStatus = _robotController != null
                ? _robotController.LastTaskStatus
                : TaskStatus.None;
            var containmentFailures = new List<string>();

            TryContainAutonomyTickFault("drive_stop", () => _driveBridge?.Stop(), containmentFailures);
            TryContainAutonomyTickFault("navigation_reference_zero", () => ZeroPhysicsVelocities(NavigationReference), containmentFailures);
            if (NavigationReference != transform)
            {
                TryContainAutonomyTickFault("adapter_transform_zero", () => ZeroPhysicsVelocities(transform), containmentFailures);
            }

            TryContainAutonomyTickFault("navigation_stop", () => _tiagoNavMeshNavigationService?.Stop(), containmentFailures);
            TryContainAutonomyTickFault(
                "task_failed",
                () => _blackboard?.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.Failed),
                containmentFailures);
            TryContainAutonomyTickFault(
                "fsm_safety_pause",
                () => _fsm?.ForceSafetyPause(),
                containmentFailures);
            TryContainAutonomyTickFault(
                "blackboard_safety_pause",
                () => _blackboard?.SetMode(RobotMode.SafetyPause),
                containmentFailures);

            TiagoDifferentialDriveBridge.DriveDiagnostics driveDiagnostics = _driveBridge != null
                ? _driveBridge.LastDiagnostics
                : default;
            var payload = new Dictionary<string, object>
            {
                ["reason"] = reason ?? string.Empty,
                ["exception_type"] = exception != null ? exception.GetType().FullName : string.Empty,
                ["exception_message"] = exception?.Message ?? string.Empty,
                ["previous_robot_mode"] = previousMode.ToString(),
                ["robot_mode_after"] = AuthoritativeRobotMode?.ToString() ?? string.Empty,
                ["previous_task_status"] = previousTaskStatus.ToString(),
                ["task_status_after"] = _robotController != null ? _robotController.LastTaskStatus.ToString() : TaskStatus.Failed.ToString(),
                ["left_drive_target_deg_per_sec"] = driveDiagnostics.LeftDriveTargetDegPerSec,
                ["right_drive_target_deg_per_sec"] = driveDiagnostics.RightDriveTargetDegPerSec,
                ["containment_failures"] = string.Join("|", containmentFailures),
                ["frame_count"] = Time.frameCount,
                ["time_since_start"] = Time.time
            };

            try
            {
                TiagoExperimentTelemetry.LogEvent("autonomy_tick_faulted", payload);
            }
            catch (Exception telemetryException)
            {
                Debug.LogException(telemetryException, this);
            }

            if (exception != null)
            {
                Debug.LogException(exception, this);
            }
            else
            {
                Debug.LogError($"[Autonomy] autonomy_tick_faulted | reason={reason}", this);
            }
        }

        private static void TryContainAutonomyTickFault(
            string operation,
            Action action,
            ICollection<string> containmentFailures)
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception containmentException)
            {
                containmentFailures?.Add($"{operation}:{containmentException.GetType().Name}:{containmentException.Message}");
            }
        }

        private void DrawNavMeshPathGizmos()
        {
            var corners = _tiagoNavMeshNavigationService.LastNavMeshPathCorners;
            int cornerCount = corners?.Count ?? 0;
            var centered = _tiagoNavMeshNavigationService.LastCenteredPathPoints;
            int centeredCount = centered?.Count ?? 0;
            var clearanceOffset = _tiagoNavMeshNavigationService.LastClearanceOffsetPathPoints;
            int clearanceOffsetCount = clearanceOffset?.Count ?? 0;
            var smoothed = _tiagoNavMeshNavigationService.LastSmoothedPathPoints;
            int smoothedCount = smoothed?.Count ?? 0;
            var active = _tiagoNavMeshNavigationService.LastActivePathPoints;
            int activeCount = active?.Count ?? 0;
            if (Time.time - _lastGizmoPathSourceLogTime >= 1.0f)
            {
                _lastGizmoPathSourceLogTime = Time.time;
                string lookahead = _tiagoNavMeshNavigationService.HasLastActiveLookahead
                    ? FormatVector(_tiagoNavMeshNavigationService.LastActiveLookaheadPoint)
                    : "none";
                string projected = _tiagoNavMeshNavigationService.HasLastActiveLookahead
                    ? FormatVector(_tiagoNavMeshNavigationService.LastActiveProjectedPoint)
                    : "none";
                Debug.Log($"[Autonomy] [Gizmos] activePathSource={_tiagoNavMeshNavigationService.LastActivePathSource} activePathPointCount={activeCount} lookahead={lookahead} projected={projected}");
            }

            if (_debugDrawOnlyActivePath)
            {
                DrawActivePathOnlyGizmos(active, activeCount);
                return;
            }

            if (cornerCount <= 0 && centeredCount <= 0 && clearanceOffsetCount <= 0 && smoothedCount <= 0 && activeCount <= 0)
            {
                return;
            }

            if (cornerCount > 0)
            {
                Gizmos.color = Color.yellow;
                for (int i = 0; i < cornerCount; i++)
                {
                    Gizmos.DrawSphere(corners[i] + Vector3.up * 0.08f, 0.08f);
                }

                if (cornerCount >= 2)
                {
                    Gizmos.color = new Color(0.0f, 0.7f, 1f, 0.45f);
                    for (int i = 0; i < cornerCount - 1; i++)
                    {
                        Vector3 a = corners[i] + Vector3.up * 0.08f;
                        Vector3 b = corners[i + 1] + Vector3.up * 0.08f;
                        Gizmos.DrawLine(a, b);
                        if (i % 2 == 0)
                        {
                            Vector3 mid = Vector3.Lerp(a, b, 0.5f);
                            Gizmos.DrawLine(a, mid);
                        }
                    }
                }
            }

            if (centeredCount > 0)
            {
                Gizmos.color = Color.magenta;
                for (int i = 0; i < centeredCount; i++)
                {
                    Gizmos.DrawSphere(centered[i] + Vector3.up * 0.16f, 0.045f);
                }

                if (centeredCount >= 2)
                {
                    Gizmos.color = Color.green;
                    for (int i = 0; i < centeredCount - 1; i++)
                    {
                        Gizmos.DrawLine(centered[i] + Vector3.up * 0.16f, centered[i + 1] + Vector3.up * 0.16f);
                    }
                }
            }

            if (clearanceOffsetCount > 0)
            {
                Gizmos.color = new Color(0.25f, 1f, 0.85f, 1f);
                for (int i = 0; i < clearanceOffsetCount; i++)
                {
                    Gizmos.DrawSphere(clearanceOffset[i] + Vector3.up * 0.19f, 0.045f);
                }

                if (clearanceOffsetCount >= 2)
                {
                    Gizmos.color = new Color(0.25f, 1f, 0.85f, 1f);
                    for (int i = 0; i < clearanceOffsetCount - 1; i++)
                    {
                        Gizmos.DrawLine(clearanceOffset[i] + Vector3.up * 0.19f, clearanceOffset[i + 1] + Vector3.up * 0.19f);
                    }
                }
            }

            if (smoothedCount > 0)
            {
                Gizmos.color = new Color(1f, 0.55f, 0f, 1f);
                for (int i = 0; i < smoothedCount; i++)
                {
                    Gizmos.DrawSphere(smoothed[i] + Vector3.up * 0.22f, 0.055f);
                }

                if (smoothedCount >= 2)
                {
                    Gizmos.color = new Color(1f, 0.55f, 0f, 1f);
                    for (int i = 0; i < smoothedCount - 1; i++)
                    {
                        Gizmos.DrawLine(smoothed[i] + Vector3.up * 0.22f, smoothed[i + 1] + Vector3.up * 0.22f);
                    }
                }
            }

            if (activeCount > 0)
            {
                Gizmos.color = Color.white;
                for (int i = 0; i < activeCount; i++)
                {
                    float radius = i == 0 ? 0.12f : 0.085f;
                    Gizmos.DrawSphere(active[i] + Vector3.up * 0.28f, radius);
                }

                if (activeCount >= 2)
                {
                    Gizmos.color = new Color(1f, 1f, 1f, 1f);
                    for (int i = 0; i < activeCount - 1; i++)
                    {
                        Vector3 a = active[i] + Vector3.up * 0.28f;
                        Vector3 b = active[i + 1] + Vector3.up * 0.28f;
                        Gizmos.DrawLine(a, b);
                    }
                }
            }

            if (_tiagoNavMeshNavigationService.HasLastActiveLookahead)
            {
                DrawActiveLookaheadGizmos();
            }
        }

        private void DrawActivePathOnlyGizmos(IReadOnlyList<Vector3> active, int activeCount)
        {
            if (activeCount > 0)
            {
                Gizmos.color = Color.white;
                for (int i = 0; i < activeCount; i++)
                {
                    Gizmos.DrawSphere(active[i] + Vector3.up * 0.22f, i == 0 ? 0.11f : 0.07f);
                }

                if (activeCount >= 2)
                {
                    Gizmos.color = Color.white;
                    for (int i = 0; i < activeCount - 1; i++)
                    {
                        Gizmos.DrawLine(active[i] + Vector3.up * 0.22f, active[i + 1] + Vector3.up * 0.22f);
                    }
                }
            }

            if (_tiagoNavMeshNavigationService.HasLastActiveLookahead)
            {
                DrawActiveLookaheadGizmos();
                Gizmos.color = Color.blue;
                Gizmos.DrawSphere(_tiagoNavMeshNavigationService.LastActiveProjectedPoint + Vector3.up * 0.30f, 0.10f);
            }
        }

        private void DrawActiveLookaheadGizmos()
        {
            Vector3 lookahead = _tiagoNavMeshNavigationService.LastActiveLookaheadPoint + Vector3.up * 0.42f;
            Vector3 robot = NavigationReference.position + Vector3.up * 0.42f;
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(lookahead, 0.18f);
            Gizmos.DrawLine(robot, lookahead);
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F2}, {value.y:F2}, {value.z:F2})";
        }

        private sealed class P40BPendingVoiceOrder
        {
            public P40BPendingVoiceOrder(
                MultimodalTaskIntent intent,
                P40TraceMetadata trace,
                TargetDescriptor pickupTarget,
                TargetDescriptor placeTarget,
                string targetId,
                string placeTargetId,
                string currentTaskInstanceId,
                string currentTargetId,
                string heldObjectIdAtDeferral)
            {
                Intent = intent;
                Trace = trace;
                PickupTarget = pickupTarget;
                PlaceTarget = placeTarget;
                TargetId = targetId ?? string.Empty;
                PlaceTargetId = placeTargetId ?? string.Empty;
                CurrentTaskInstanceId = currentTaskInstanceId ?? string.Empty;
                CurrentTargetId = currentTargetId ?? string.Empty;
                HeldObjectIdAtDeferral = heldObjectIdAtDeferral ?? string.Empty;
            }

            public MultimodalTaskIntent Intent { get; }
            public P40TraceMetadata Trace { get; }
            public TargetDescriptor PickupTarget { get; }
            public TargetDescriptor PlaceTarget { get; }
            public string TargetId { get; }
            public string PlaceTargetId { get; }
            public string CurrentTaskInstanceId { get; }
            public string CurrentTargetId { get; }
            public string HeldObjectIdAtDeferral { get; }
        }
    }
}
