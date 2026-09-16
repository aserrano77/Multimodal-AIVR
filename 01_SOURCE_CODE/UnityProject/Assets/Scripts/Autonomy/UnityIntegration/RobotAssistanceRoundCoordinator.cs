using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Autonomy.BT.Core;
using Autonomy.Core;
using Autonomy.Domain;
using Autonomy.Perception;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class RobotAssistanceRoundCoordinator : MonoBehaviour, IExplicitAutonomyIntentActivity
    {
        private const string LogPrefix = "[RobotAssistanceRoundCoordinator]";
        private const AutonomousSelectionPolicy ActiveAutonomousSelectionPolicy = AutonomousSelectionPolicy.LocalPickCost;

        private enum PostPlaceEgressFailurePolicy
        {
            ContinueWithWarning,
            BlockAssistance,
            MarkRoundIncomplete
        }

        private enum PostPlaceEgressMode
        {
            LocalRetreat,
            ShortNav,
            FullNav
        }

        [Header("References")]
        [SerializeField] private MonoBehaviour _experimentConditionProviderComponent;
        [SerializeField] private MonoBehaviour _roundLifecycleComponent;
        [SerializeField] private MultimodalAutonomyCommandBridge _commandBridge;
        [SerializeField] private AutonomousRobotAdapter _robotAdapter;
        [SerializeField] private UnityScenePerceptionService _perceptionService;
        [SerializeField] private MultimodalPlaceTargetRegistry _placeTargetRegistry;

        [Header("Selection")]
        [SerializeField] private float _selectionIntervalSeconds = 0.75f;
        [SerializeField] private float _completionReevaluationDelaySeconds = 0.5f;
        [SerializeField] private float _navMeshSampleRadius = 0.75f;
        [SerializeField] private string _placeTargetPrefix = "Zone";
        [SerializeField] private bool _autoInitializeRound = true;

        [Header("Pickup Approach")]
        [SerializeField] private bool _useDynamicPickupApproach = true;
        [SerializeField] private bool _useCornerPickupApproachCandidates = false;
        [SerializeField] private float _pickApproachStandoff = 0.45f;
        [SerializeField] private float _maxExpectedPickDistance = 1.20f;
        [SerializeField] private float _pickDistanceSafetyMargin = 0.05f;
        [SerializeField] private int _maxPickApproachRetries = 2;

        [Header("Place Navigation")]
        [SerializeField] private bool _useDynamicPlaceNavigationCandidates = true;
        [SerializeField] private bool _useCornerPlaceNavigationCandidates = false;
        [SerializeField] private float _placeNavigationStandoff = 0.65f;
        [SerializeField] private float _maxExpectedPlaceDistance = 1.25f;
        [SerializeField] private float _maxRelaxedPlaceDistance = 1.45f;
        [SerializeField] private float _placeDistanceSafetyMargin = 0.05f;
        [SerializeField] private float _placeCandidateReachabilityMargin = 0.20f;
        [SerializeField] private float _placeCandidateArrivalTolerance = 0.10f;
        [SerializeField] private bool _useDynamicPlacePose = true;
        [SerializeField] private float _placePoseInset = 0.20f;
        [SerializeField] private float _minPlacePoseClearance = 0.05f;
        [SerializeField] private bool _useDepositZoneSlotAllocator = true;
        [SerializeField] private float _placeSlotInset = 0.30f;
        [SerializeField] private float _placeSlotClearance = 0.10f;
        [SerializeField] private Vector3 _placeSlotOccupancyCheckExtents = new(0.18f, 0.12f, 0.18f);
        [SerializeField] private LayerMask _placeSlotObstacleLayerMask = ~0;
        [SerializeField] private float _placeSlotStackVerticalSpacing = 0.03f;
        [SerializeField] private float _placeSlotDefaultBoxHeight = 0.20f;
        [SerializeField] private float _keepZoneAccessMargin = 0.10f;
        [SerializeField] private bool _allowNonSlotDynamicPlaceFallback = false;
        [SerializeField] private int _maxPlaceApproachRetries = 2;

        [Header("Post Place Egress")]
        [SerializeField] private bool _usePostPlaceEgressValidation = true;
        [SerializeField] private float _postPlaceEgressDistance = 0.70f;
        [SerializeField] private float _postPlaceEgressSampleRadius = 0.75f;
        [SerializeField] private float _postPlaceEgressClearanceRadius = 0.28f;
        [SerializeField] private float _postPlaceEgressSafetyMargin = 0.15f;
        [SerializeField] private bool _requirePostPlaceEgressPath = true;
        [SerializeField] private bool _enablePostPlaceRetreat = true;
        [SerializeField] private PostPlaceEgressMode _postPlaceEgressMode = PostPlaceEgressMode.LocalRetreat;
        [SerializeField] private float _postPlaceEgressShortDistanceThreshold = 0.90f;
        [SerializeField] private float _postPlaceEgressArrivalTolerance = 0.20f;
        [SerializeField] private float _postPlaceEgressMaxHeadingError = 160f;
        [SerializeField] private float _postPlaceRetreatTimeoutSeconds = 6f;
        [SerializeField] private float _postPlaceLocalRetreatSpeed = 0.18f;
        [SerializeField] private float _postPlaceLocalRetreatSafeDistance = 0.85f;
        [SerializeField] private float _postPlaceLocalRetreatMinDistanceGain = 0.12f;
        [SerializeField] private float _postPlaceLocalRetreatProgressLogInterval = 0.35f;
        [SerializeField] private PostPlaceEgressFailurePolicy _postPlaceEgressFailurePolicy = PostPlaceEgressFailurePolicy.ContinueWithWarning;

        [Header("Debug")]
        [SerializeField] private bool _debugDumpAssistanceStateNow;
        [SerializeField] private bool _debugForceReevaluateAssistanceNow;

        private readonly BoxRoundState _roundState = new();
        private readonly AssistedBoxSelectionPolicy _selectionPolicy = new();
        private readonly DepositZoneSlotAllocator _slotAllocator = new();
        private readonly List<BoxRoundItem> _roundBoxes = new();
        private NavMeshRouteCostEstimator _routeCostEstimator;
        private IExperimentalRoundLifecycle _roundLifecycle;
        private float _nextSelectionTime;
        private bool _roundInitialized;
        private bool _roundCompletedLogged;
        private string _assignedBoxId = string.Empty;
        private bool _explicitIntentProcessing;
        private bool _roundLifecycleSubscribed;
        private string _lastSelectionBlockReason = string.Empty;
        private float _lastSelectionTime = float.NegativeInfinity;
        private float _lastDepositTime = float.NegativeInfinity;
        private readonly Dictionary<string, HashSet<string>> _rejectedApproachCandidatesByBox = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _rejectedPlaceCandidatesByBox = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Dictionary<string, object>> _lastSelectionIgnoredCandidates = new();
        private readonly Dictionary<string, int> _pickApproachRetryCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _placeApproachRetryCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _assignedApproachCandidateByBox = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _assignedPlaceCandidateByBox = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _lastPlaceCandidateTelemetryByContext = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _lastDepositSlotTelemetryByBox = new(StringComparer.OrdinalIgnoreCase);
        private BoxRoundItem _preparedImmediateVoicePlacePlanning;
        private string _preparedImmediateVoicePlacePlanningRequestId = string.Empty;
        private bool _assignedTaskFailedOutOfRange;
        private bool _assignedTaskFailedPlaceOutOfRange;
        private bool _pendingPlaceRetry;
        private bool _pendingPostPlaceEgress;
        private bool _postPlaceEgressStarted;
        private string _postPlaceEgressBoxId = string.Empty;
        private string _postPlaceEgressSlotId = string.Empty;
        private Vector3 _postPlaceEgressPoint;
        private float _postPlaceEgressStartedAt;
        private float _postPlaceEgressLastDuration;
        private Vector3 _postPlaceEgressStartPosition;
        private float _postPlaceEgressStartDistanceToTarget;
        private float _postPlaceEgressStartDistanceFromBox;
        private float _postPlaceEgressHeadingErrorStart;
        private float _postPlaceLocalRetreatNextProgressLogTime;
        private bool _postPlaceEgressUsedStartupAlignment;
        private string _postPlaceEgressSuccessCriterion = string.Empty;
        private bool _pendingDepositedObstacleRegistration;
        private string _postPlaceEgressPlaceTargetId = string.Empty;
        private Vector3 _postPlaceSlotPosition;
        private ExperimentRunMode _experimentRunMode = ExperimentRunMode.LegacyDebug;
        private bool _orchestratedTrialActive;
        private bool _orchestratedTrialInvalid;
        private string _orchestratedTrialInvalidReason = string.Empty;

        public bool HasAssignedBox => _roundState.HasAssignedBox || !string.IsNullOrWhiteSpace(_assignedBoxId);
        public string AssignedBoxId => _assignedBoxId ?? string.Empty;
        public int PendingBoxCount => _roundState.PendingCount;
        public int AssignedBoxCount => _roundState.AssignedCount;
        public int CompletedBoxCount => _roundState.CompletedCount;
        public int ExcludedBoxCount => _roundState.ExcludedCount;
        public bool AssistanceRoundCompleted => _roundState.RoundCompleted;
        public int ReservedDepositSlotCount => _slotAllocator.ReservedCount;
        public int OccupiedDepositSlotCount => _slotAllocator.OccupiedCount;
        public int PendingApproachCandidateCacheCount => _rejectedApproachCandidatesByBox.Count + _rejectedPlaceCandidatesByBox.Count;
        public bool ExplicitIntentProcessing => _explicitIntentProcessing;
        public bool RoundInitialized => _roundInitialized;
        public string PostPlaceEgressModeName => _postPlaceEgressMode.ToString();
        public RobotAssistancePlaceRecoveryConfig PlaceRecoveryConfig => new(
            _allowNonSlotDynamicPlaceFallback,
            _useDynamicPlacePose,
            _useDepositZoneSlotAllocator,
            _maxExpectedPlaceDistance,
            _maxRelaxedPlaceDistance,
            _placeCandidateReachabilityMargin,
            _maxPlaceApproachRetries);

        public bool TryReleaseAssignedBoxForVoicePrePickReplacement(
            string boxId,
            string oldTaskInstanceId,
            out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(boxId))
            {
                reason = "missing_old_target";
                return false;
            }

            if (_robotAdapter != null &&
                string.Equals(_robotAdapter.HeldObjectId, boxId, StringComparison.OrdinalIgnoreCase))
            {
                reason = "old_target_held";
                return false;
            }

            BoxRoundStatus status = _roundState.GetStatus(boxId);
            if (status == BoxRoundStatus.Completed)
            {
                reason = "old_target_completed";
                return false;
            }

            if (TryFindBoxMetadata(boxId, out Component metadata) && GetBool(metadata, "isDeposited"))
            {
                reason = "old_target_deposited";
                return false;
            }

            bool released = _roundState.TryMarkPendingFromAssigned(boxId);
            if (!released)
            {
                reason = $"old_target_not_assigned_{status}";
                return false;
            }

            if (string.Equals(_assignedBoxId, boxId, StringComparison.OrdinalIgnoreCase))
            {
                _assignedBoxId = string.Empty;
            }

            reason = "voice_pre_pick_replacement";
            TiagoExperimentTelemetry.LogEvent(
                "p40a_previous_assignment_released",
                new Dictionary<string, object>
                {
                    ["old_target_id"] = boxId,
                    ["old_task_instance_id"] = oldTaskInstanceId ?? string.Empty,
                    ["reason"] = reason,
                    ["box_state_before"] = status.ToString(),
                    ["held_object_id"] = _robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty,
                    ["scene"] = SceneManager.GetActiveScene().name
                });
            return true;
        }

        public bool IsP40BTargetAlreadyDepositedOrCompleted(string boxId)
        {
            if (string.IsNullOrWhiteSpace(boxId))
            {
                return false;
            }

            if (_roundState.GetStatus(boxId) == BoxRoundStatus.Completed)
            {
                return true;
            }

            return TryFindBoxMetadata(boxId, out Component metadata) && GetBool(metadata, "isDeposited");
        }

        public bool IsKnownRoundBox(string boxId)
        {
            if (string.IsNullOrWhiteSpace(boxId))
            {
                return false;
            }

            foreach (BoxRoundItem box in _roundBoxes)
            {
                if (box != null && string.Equals(box.BoxId, boxId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return _roundState.GetStatus(boxId) != BoxRoundStatus.Excluded;
        }

        private void Awake()
        {
            TryResolveReferences();
            _routeCostEstimator = new NavMeshRouteCostEstimator(_navMeshSampleRadius, NavMesh.AllAreas);
        }

        private void OnEnable()
        {
            TryResolveRoundLifecycle();
            SubscribeRoundLifecycle();
            TiagoExperimentTelemetry.StructuredEventLogged += HandleStructuredEvent;
        }

        private void OnDisable()
        {
            UnsubscribeRoundLifecycle();
            TiagoExperimentTelemetry.StructuredEventLogged -= HandleStructuredEvent;
        }

        private void Start()
        {
            if (!IsOrchestrated)
            {
                LogCondition("experiment_condition_applied");
            }
            if (!IsOrchestrated && _autoInitializeRound && (_roundLifecycle == null || _roundLifecycle.CurrentRound.RoundActive))
            {
                InitializeRoundFromScene();
            }
        }

        private void Update()
        {
            if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
            {
                return;
            }

            if (_debugDumpAssistanceStateNow)
            {
                _debugDumpAssistanceStateNow = false;
                DebugDumpAssistanceState("inspector_flag");
            }

            if (_debugForceReevaluateAssistanceNow)
            {
                _debugForceReevaluateAssistanceNow = false;
                TiagoExperimentTelemetry.LogEvent(
                    "assisted_debug_force_reevaluate_requested",
                    BuildAssistanceSnapshotPayload("debug_force_reevaluate"));
                _nextSelectionTime = Time.time;
            }

            if (!IsOrchestrated && !_roundInitialized && _autoInitializeRound)
            {
                InitializeRoundFromScene();
            }

            if (IsSupervisoryStopLatched())
            {
                return;
            }

            if (_pendingPostPlaceEgress && ProcessPostPlaceEgress())
            {
                return;
            }

            RefreshAssignedBoxCompletion();

            if (_pendingPlaceRetry && TrySubmitPendingPlaceRetry())
            {
                return;
            }

            if (IsOrchestrated && (_roundLifecycle == null || !_roundLifecycle.CurrentRound.RoundActive))
            {
                return;
            }

            if (Time.time < _nextSelectionTime)
            {
                return;
            }

            _nextSelectionTime = Time.time + Mathf.Max(0.1f, _selectionIntervalSeconds);
            TryLaunchAssistedSelection();
        }

        public void InitializeRoundFromScene()
        {
            TryResolveReferences();
            TryResolveRoundLifecycle();
            _roundBoxes.Clear();
            _slotAllocator.Clear();

            if (_perceptionService == null || _placeTargetRegistry == null)
            {
                LogAssistanceBlocked("round_state_initialized", "perception_or_place_registry_missing");
                return;
            }

            ExperimentalRoundSnapshot snapshot = _roundLifecycle?.CurrentRound;
            if (_roundLifecycle != null && (snapshot == null || !snapshot.RoundActive))
            {
                ClearAssistanceRound("round_not_active");
                return;
            }

            var query = new PerceptionQuery(
                PerceptionSelectionStrategy.NearestAvailable,
                referencePosition: ResolveRobotPosition(),
                rejectDeposited: true,
                rejectGrabbed: true,
                rejectHeld: true);

            IReadOnlyList<PerceivedObject> candidates = _perceptionService.Scan(query);
            foreach (PerceivedObject candidate in candidates)
            {
                if (candidate == null || !candidate.IsAccepted)
                {
                    continue;
                }

                if (!BelongsToActiveRound(candidate))
                {
                    LogAssistedBoxSkipped(candidate.ObjectId, candidate.Category, string.Empty, "outside_active_round");
                    continue;
                }

                string placeTargetId = ResolvePlaceTargetId(candidate.Category);
                if (!_placeTargetRegistry.TryResolve(placeTargetId, out TargetDescriptor placeTarget, out _, out string placeFailure))
                {
                    LogAssistedBoxSkipped(candidate.ObjectId, candidate.Category, placeTargetId, placeFailure);
                    continue;
                }

                _roundBoxes.Add(new BoxRoundItem(
                    candidate.ObjectId,
                    candidate.Category,
                    placeTargetId,
                    ResolvePickupPosition(candidate),
                    ToUnityVector(placeTarget.Position)));
            }

            _roundState.Initialize(_roundBoxes);
            _roundInitialized = true;
            _roundCompletedLogged = false;
            _assignedBoxId = string.Empty;
            LogRoundInitialized();
        }

        public void NotifyExplicitIntentProcessingStarted(MultimodalTaskIntent intent, string source)
        {
            _explicitIntentProcessing = true;
            TiagoExperimentTelemetry.LogEvent(
                "explicit_voice_intent_processing_started",
                BuildIntentPayload(intent, source));
        }

        public void NotifyExplicitIntentProcessingFinished(MultimodalTaskIntent intent, string source, bool accepted)
        {
            _explicitIntentProcessing = false;
            Dictionary<string, object> payload = BuildIntentPayload(intent, source);
            payload["bridge_accepted"] = accepted;
            TiagoExperimentTelemetry.LogEvent("explicit_voice_intent_processing_finished", payload);
        }

        public void ResetForNewExperimentTrial(string reason)
        {
            _orchestratedTrialActive = false;
            _orchestratedTrialInvalid = !string.IsNullOrWhiteSpace(reason) && reason.Contains("failed", StringComparison.OrdinalIgnoreCase);
            _orchestratedTrialInvalidReason = _orchestratedTrialInvalid ? reason : string.Empty;
            ClearAssistanceRound(string.IsNullOrWhiteSpace(reason) ? "experiment_trial_reset" : reason);
        }

        public void SetExperimentRunMode(ExperimentRunMode runMode)
        {
            _experimentRunMode = runMode;
            if (IsOrchestrated)
            {
                _autoInitializeRound = false;
            }
        }

        private bool IsOrchestrated => _experimentRunMode == ExperimentRunMode.Orchestrated2x2;

        private void TryLaunchAssistedSelection()
        {
            if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
            {
                LogSelectionBlockedDetailed(ExperimentRuntimePauseCoordinator.AutonomyRequestRejectionReason);
                return;
            }

            if (!IsExperimentTrialAllowedForAssistance())
            {
                LogAssistanceBlocked("assistance_blocked_by_inactive_or_invalid_trial", ResolveAssistanceTrialBlockReason());
                LogSelectionBlockedDetailed(ResolveAssistanceTrialBlockReason());
                return;
            }

            if (IsSupervisoryStopLatched())
            {
                LogVoiceStopLatchedAutoSelectionBlockedNeutral("pre_selection");
                LogSelectionBlockedDetailed("voice_stop_latched");
                return;
            }

            if (TryPromotePendingP40BVoiceOrderBeforeAssistedSelection())
            {
                return;
            }

            if (_robotAdapter != null && !string.IsNullOrWhiteSpace(_robotAdapter.HeldObjectId))
            {
                LogSelectionBlockedDetailed("robot_holding_object");
                return;
            }

            if (_roundLifecycle != null && !_roundLifecycle.CurrentRound.RoundActive)
            {
                LogAssistanceBlocked("robot_assistance_blocked_by_condition", "round_not_active");
                LogSelectionBlockedDetailed("round_not_active");
                return;
            }

            ExperimentConditionConfig condition = ResolveCondition();
            bool robotBusy = IsRobotBusy();
            AssistanceGateResult gate = condition.EvaluateRobotAssistance(robotBusy, _explicitIntentProcessing, _roundState.HasAssignedBox);
            if (!gate.Allowed)
            {
                LogRobotAssistanceGate(condition, gate);
                LogSelectionBlockedDetailed(gate.Reason);
                return;
            }

            if (_roundState.RoundCompleted)
            {
                LogRoundCompletedIfNeeded();
                LogNoSelection("round_completed");
                LogSelectionBlockedDetailed("round_completed");
                return;
            }

            AssistedBoxSelectionResult selection = _selectionPolicy.SelectNext(
                BuildApproachAwareRoundBoxes(),
                _roundState,
                ResolveRobotPosition(),
                _routeCostEstimator,
                ActiveAutonomousSelectionPolicy);
            P40NavTraceDiagnostics.LogObstacleRegistrySnapshot(
                "before_assisted_selection",
                "assisted_selector_started",
                _robotAdapter,
                activeTargetId: _assignedBoxId);
            LogCandidateEvaluations(selection.Evaluations, selection.Policy, _lastSelectionIgnoredCandidates);
            LogP40RoundAssignmentSnapshot("before_assisted_selection");

            if (!selection.HasSelection)
            {
                string noSelectionReason = _useDepositZoneSlotAllocator && !_allowNonSlotDynamicPlaceFallback && _roundState.PendingCount > 0
                    ? "no_valid_place_slot_for_pending_boxes"
                    : _roundState.RoundCompleted ? "round_completed" : "no_selectable_pending_boxes";
                LogRoundCompletedIfNeeded();
                LogNoSelection(noSelectionReason);
                LogSelectionBlockedDetailed(noSelectionReason);
                return;
            }

            BoxRoundItem selected = selection.SelectedBox;
            if (!_roundState.TryMarkAssigned(selected.BoxId))
            {
                LogAssistedBoxSkipped(selected.BoxId, selected.Category, selected.PlaceTargetId, "box_not_pending");
                LogSelectionBlockedDetailed("box_not_pending");
                return;
            }

            _assignedBoxId = selected.BoxId;
            _assignedTaskFailedOutOfRange = false;
            if (!string.IsNullOrWhiteSpace(selected.PickupApproachCandidateId))
            {
                _assignedApproachCandidateByBox[selected.BoxId] = selected.PickupApproachCandidateId;
                _commandBridge?.SetPreferredPickupApproach(
                    selected.BoxId,
                    selected.PickupPosition,
                    selected.PickupApproachCandidateId,
                    selected.PickupApproachSide,
                    selected.PickupApproachSource);
            }

            if (!string.IsNullOrWhiteSpace(selected.PlaceNavigationCandidateId))
            {
                _assignedPlaceCandidateByBox[selected.BoxId] = selected.PlaceNavigationCandidateId;
                _commandBridge?.SetPreferredPlaceNavigation(
                    selected.PlaceTargetId,
                    selected.PlacePosition,
                    selected.PlaceNavigationCandidateId,
                    selected.PlaceNavigationSideOrCorner,
                    selected.PlaceNavigationSource);
                _commandBridge?.SetPreferredDynamicPlacePose(
                    selected.PlaceTargetId,
                    selected.DynamicPlacePosePosition,
                    selected.PlaceNavigationCandidateId,
                    selected.UseDynamicPlacePose,
                    selected.PlaceNavigationSource != null && selected.PlaceNavigationSource.Contains("Corner") ? "corner" : "side",
                    selected.PlaceAreaSource,
                    selected.PlacePoseInset,
                    selected.PlacePointFallbackPosition,
                    selected.PlaceSlotId,
                    selected.PlaceSlotQuadrant,
                    selected.PlaceSlotIndex,
                    selected.PlaceStackLevel,
                    selected.UsedPlaceSlotAllocator,
                    selected.PostPlaceEgressPoint);
                if (selected.UsedPlaceSlotAllocator && !string.IsNullOrWhiteSpace(selected.PlaceSlotId))
                {
                    DepositZoneSlot slot = ResolveEvaluatedDepositSlot(selected);
                    _slotAllocator.Reserve(slot, selected.BoxId);
                    TiagoExperimentTelemetry.LogEvent("deposit_slot_reserved", _slotAllocator.ToPayload(slot));
                }
            }

            _lastSelectionTime = Time.time;
            LogAssistedBoxAssigned(selected);
            LogAssistedBoxSelected(selected, selection.Evaluations, selection.Policy);
            LogP40AssistedSelectionDecision(selected, selection.Evaluations);

            MultimodalTaskIntent intent = MultimodalTaskIntent.PickAndPlaceByTargetId(
                selected.BoxId,
                selected.PlaceTargetId,
                "assisted_navmesh_selection");
            P40TraceMetadata trace = new()
            {
                Producer = "assisted_navmesh_selection",
                CausalParentId = P40TraceContext.LastVoiceCommand?.RequestId ?? string.Empty,
                TargetAlias = selected.BoxId,
                ResolvedDestination = selected.PlaceTargetId,
                SubmittedDestination = selected.PlaceTargetId,
                LastDecision = "selected",
                LastReason = "assisted_selector_heuristic"
            };
            P40TraceContext.RegisterIntent(intent, trace);
            bool submitted = _commandBridge != null && _commandBridge.SubmitIntent(intent);
            LogAssistedTaskSubmitted(selected, submitted);
            if (!submitted)
            {
                if (IsSupervisoryStopLatched())
                {
                    bool restoredPending = _roundState.TryMarkPendingFromAssigned(selected.BoxId);
                    ReleaseDepositSlot(selected.BoxId, "voice_stop_latched_auto_selection_blocked_neutral");
                    _assignedBoxId = string.Empty;
                    LogVoiceStopLatchedAutoSelectionBlockedNeutral(restoredPending ? "adapter_rejected_after_assignment_restored_pending" : "adapter_rejected_after_assignment_restore_not_needed");
                    LogSelectionBlockedDetailed("voice_stop_latched");
                    return;
                }

                _roundState.TryMarkExcluded(selected.BoxId);
                ReleaseDepositSlot(selected.BoxId, "bridge_rejected_or_missing");
                _assignedBoxId = string.Empty;
                ScheduleReevaluation("bridge_rejected_or_missing");
                LogSelectionBlockedDetailed("bridge_rejected_or_missing");
            }
        }

        public bool TryPromotePendingP40BVoiceOrderBeforeAssistedSelection(Func<MultimodalTaskIntent, bool> submitIntent = null)
        {
            if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
            {
                LogSelectionBlockedDetailed(ExperimentRuntimePauseCoordinator.AutonomyRequestRejectionReason);
                return false;
            }

            if (_robotAdapter == null || !_robotAdapter.HasPendingP40BVoiceOrder)
            {
                return false;
            }

            if (!IsP40BPendingVoiceOrderPromotionWindowOpen())
            {
                return false;
            }

            bool hadPendingOrder = _robotAdapter.HasPendingP40BVoiceOrder;
            string previousCompletedTaskInstanceId = _robotAdapter.ActiveP40TaskInstanceId;
            Func<MultimodalTaskIntent, bool> effectiveSubmitIntent = submitIntent ?? SubmitP40BPromotedVoiceIntent;
            bool submitted = _robotAdapter.TryPromoteP40BPendingVoiceOrderAfterPlace(
                effectiveSubmitIntent,
                ValidateP40BPendingTargetForPromotion,
                previousCompletedTaskInstanceId);
            if (submitted)
            {
                LogSelectionBlockedDetailed("pending_voice_order_promoted");
                return true;
            }

            if (hadPendingOrder && !_robotAdapter.HasPendingP40BVoiceOrder)
            {
                ScheduleReevaluation("pending_voice_order_discarded");
                LogSelectionBlockedDetailed("pending_voice_order_discarded");
                return true;
            }

            return false;
        }

        private bool SubmitP40BPromotedVoiceIntent(MultimodalTaskIntent intent)
        {
            if (_commandBridge == null || intent == null)
            {
                return false;
            }

            TryPrepareP40BPromotedVoicePlacePlanning(intent);
            return _commandBridge.SubmitIntent(intent);
        }

        private bool TryPrepareP40BPromotedVoicePlacePlanning(MultimodalTaskIntent intent)
        {
            if (intent == null ||
                intent.TaskFlow != AutonomousTaskFlow.PickAndPlace ||
                intent.ObjectSelectionMode != MultimodalObjectSelectionMode.ExplicitTargetId ||
                string.IsNullOrWhiteSpace(intent.TargetId) ||
                string.IsNullOrWhiteSpace(intent.PlaceTargetId) ||
                string.Equals(intent.PlaceTargetId, "SELF", StringComparison.OrdinalIgnoreCase) ||
                _commandBridge == null)
            {
                return false;
            }

            BoxRoundItem planned = null;
            foreach (BoxRoundItem box in BuildApproachAwareRoundBoxes())
            {
                if (box != null &&
                    string.Equals(box.BoxId, intent.TargetId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(box.PlaceTargetId, intent.PlaceTargetId, StringComparison.OrdinalIgnoreCase))
                {
                    planned = box;
                    break;
                }
            }

            if (planned == null)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "p40b_promoted_voice_slot_planning_failed",
                    new Dictionary<string, object>
                    {
                        ["target_id"] = intent.TargetId,
                        ["place_target_id"] = intent.PlaceTargetId,
                        ["reason"] = "round_box_place_plan_not_found",
                        ["use_deposit_zone_slot_allocator"] = _useDepositZoneSlotAllocator,
                        ["allow_non_slot_dynamic_place_fallback"] = _allowNonSlotDynamicPlaceFallback,
                        ["scene"] = SceneManager.GetActiveScene().name
                    });
                return false;
            }

            ApplyPreferredPlacePlanning(planned, "p40b_promoted_voice_order", reserveSlot: true);
            TiagoExperimentTelemetry.LogEvent(
                "p40b_promoted_voice_slot_planning_prepared",
                BuildP40BPromotedVoiceSlotPlanningPayload(planned, "prepared"));
            return planned.UsedPlaceSlotAllocator && !string.IsNullOrWhiteSpace(planned.PlaceSlotId);
        }

        public bool TryPrepareImmediateVoicePlacePlanning(
            MultimodalTaskIntent intent,
            out string failureReason)
        {
            failureReason = string.Empty;
            ClearPreparedImmediateVoicePlacePlanning(null, "new_voice_place_planning_request");

            if (!IsImmediateVoicePlacePlanningCandidate(intent, out failureReason))
            {
                return false;
            }

            P40TraceMetadata trace = P40TraceContext.EnsureForIntent(intent, intent.Source);
            BoxRoundItem planned = null;
            foreach (BoxRoundItem box in BuildApproachAwareRoundBoxes())
            {
                if (box != null &&
                    string.Equals(box.BoxId, intent.TargetId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(box.PlaceTargetId, intent.PlaceTargetId, StringComparison.OrdinalIgnoreCase))
                {
                    planned = box;
                    break;
                }
            }

            if (planned == null)
            {
                failureReason = _useDepositZoneSlotAllocator && !_allowNonSlotDynamicPlaceFallback
                    ? "no_valid_deposit_slot"
                    : string.Empty;
                TiagoExperimentTelemetry.LogEvent(
                    "voice_immediate_slot_planning_failed",
                    BuildImmediateVoiceSlotPlanningPayload(intent, null, "failed", "round_box_place_plan_not_found"));
                return false;
            }

            if (_useDepositZoneSlotAllocator &&
                !_allowNonSlotDynamicPlaceFallback &&
                (!planned.UsedPlaceSlotAllocator || string.IsNullOrWhiteSpace(planned.PlaceSlotId)))
            {
                failureReason = "no_valid_deposit_slot";
                TiagoExperimentTelemetry.LogEvent(
                    "voice_immediate_slot_planning_failed",
                    BuildImmediateVoiceSlotPlanningPayload(intent, planned, "failed", "no_valid_deposit_slot"));
                return false;
            }

            ApplyPreferredPlacePlanning(planned, "voice_immediate_pick_and_place", reserveSlot: false);
            _preparedImmediateVoicePlacePlanning = planned;
            _preparedImmediateVoicePlacePlanningRequestId = trace != null ? trace.RequestId : string.Empty;
            TiagoExperimentTelemetry.LogEvent(
                "voice_immediate_slot_planning_prepared",
                BuildImmediateVoiceSlotPlanningPayload(intent, planned, "prepared", string.Empty));
            return true;
        }

        public void CommitPreparedImmediateVoicePlacePlanning(MultimodalTaskIntent intent, string reason)
        {
            if (!IsPreparedImmediateVoicePlacePlanningMatch(intent, out BoxRoundItem prepared))
            {
                return;
            }

            if (prepared.UsedPlaceSlotAllocator && !string.IsNullOrWhiteSpace(prepared.PlaceSlotId))
            {
                DepositZoneSlot slot = ResolveEvaluatedDepositSlot(prepared);
                _slotAllocator.Reserve(slot, prepared.BoxId);
                Dictionary<string, object> payload = _slotAllocator.ToPayload(slot);
                payload["reason"] = reason ?? "voice_immediate_pick_and_place_accepted";
                TiagoExperimentTelemetry.LogEvent("deposit_slot_reserved", payload);
            }

            TiagoExperimentTelemetry.LogEvent(
                "voice_immediate_slot_planning_committed",
                BuildImmediateVoiceSlotPlanningPayload(intent, prepared, "committed", reason));
            ClearPreparedImmediateVoicePlacePlanning(intent, reason);
        }

        public void ClearPreparedImmediateVoicePlacePlanning(MultimodalTaskIntent intent, string reason)
        {
            if (_preparedImmediateVoicePlacePlanning == null)
            {
                return;
            }

            if (intent == null || IsPreparedImmediateVoicePlacePlanningMatch(intent, out _))
            {
                _preparedImmediateVoicePlacePlanning = null;
                _preparedImmediateVoicePlacePlanningRequestId = string.Empty;
            }
        }

        private void ApplyPreferredPlacePlanning(BoxRoundItem selected, string reason, bool reserveSlot)
        {
            if (selected == null || _commandBridge == null || string.IsNullOrWhiteSpace(selected.PlaceNavigationCandidateId))
            {
                return;
            }

            _assignedPlaceCandidateByBox[selected.BoxId] = selected.PlaceNavigationCandidateId;
            _commandBridge.SetPreferredPlaceNavigation(
                selected.PlaceTargetId,
                selected.PlacePosition,
                selected.PlaceNavigationCandidateId,
                selected.PlaceNavigationSideOrCorner,
                selected.PlaceNavigationSource);
            _commandBridge.SetPreferredDynamicPlacePose(
                selected.PlaceTargetId,
                selected.DynamicPlacePosePosition,
                selected.PlaceNavigationCandidateId,
                selected.UseDynamicPlacePose,
                selected.PlaceNavigationSource != null && selected.PlaceNavigationSource.Contains("Corner") ? "corner" : "side",
                selected.PlaceAreaSource,
                selected.PlacePoseInset,
                selected.PlacePointFallbackPosition,
                selected.PlaceSlotId,
                selected.PlaceSlotQuadrant,
                selected.PlaceSlotIndex,
                selected.PlaceStackLevel,
                selected.UsedPlaceSlotAllocator,
                selected.PostPlaceEgressPoint);

            if (reserveSlot && selected.UsedPlaceSlotAllocator && !string.IsNullOrWhiteSpace(selected.PlaceSlotId))
            {
                DepositZoneSlot slot = ResolveEvaluatedDepositSlot(selected);
                _slotAllocator.Reserve(slot, selected.BoxId);
                Dictionary<string, object> payload = _slotAllocator.ToPayload(slot);
                payload["reason"] = reason ?? string.Empty;
                TiagoExperimentTelemetry.LogEvent("deposit_slot_reserved", payload);
            }
        }

        private DepositZoneSlot ResolveEvaluatedDepositSlot(BoxRoundItem selected)
        {
            if (selected != null &&
                TryResolvePlaceTargetTransforms(selected.PlaceTargetId, out Transform placeTransform, out Transform navigationTransform))
            {
                IReadOnlyList<DepositZoneSlot> slots = _slotAllocator.EvaluateSlots(
                    selected.PlaceTargetId,
                    placeTransform,
                    navigationTransform,
                    selected.PlacePosition,
                    selected.BoxId,
                    BuildDepositZoneSlotSettings());
                foreach (DepositZoneSlot slot in slots)
                {
                    if (slot != null && string.Equals(slot.SlotId, selected.PlaceSlotId, StringComparison.OrdinalIgnoreCase))
                    {
                        slot.State = "reserved";
                        slot.AssignedBoxId = selected.BoxId;
                        slot.OccupancyCheckPassed = true;
                        return slot;
                    }
                }
            }

            return new DepositZoneSlot
            {
                PlaceTargetId = selected != null ? selected.PlaceTargetId : string.Empty,
                SlotId = selected != null ? selected.PlaceSlotId : string.Empty,
                Quadrant = selected != null ? selected.PlaceSlotQuadrant : string.Empty,
                Position = selected != null ? selected.DynamicPlacePosePosition : Vector3.zero,
                Rotation = Quaternion.identity,
                SlotIndex = selected != null ? selected.PlaceSlotIndex : -1,
                StackLevel = selected != null ? selected.PlaceStackLevel : 0,
                State = "reserved",
                AssignedBoxId = selected != null ? selected.BoxId : string.Empty,
                SlotInset = _placeSlotInset,
                SlotClearance = _placeSlotClearance,
                OccupancyCheckPassed = true,
                AreaSource = selected != null ? selected.PlaceAreaSource : string.Empty
            };
        }

        private bool IsImmediateVoicePlacePlanningCandidate(MultimodalTaskIntent intent, out string failureReason)
        {
            failureReason = string.Empty;
            if (intent == null ||
                !string.Equals(P40TraceContext.NormalizeProducer(intent.Source), "voice_command", StringComparison.OrdinalIgnoreCase) ||
                intent.TaskFlow != AutonomousTaskFlow.PickAndPlace ||
                intent.ObjectSelectionMode != MultimodalObjectSelectionMode.ExplicitTargetId ||
                string.IsNullOrWhiteSpace(intent.TargetId) ||
                string.IsNullOrWhiteSpace(intent.PlaceTargetId) ||
                string.Equals(intent.PlaceTargetId, "SELF", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (_commandBridge == null || _robotAdapter == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_robotAdapter.HeldObjectId))
            {
                return false;
            }

            if (!TryFindRoundBox(intent.TargetId, out BoxRoundItem roundBox))
            {
                return false;
            }

            if (!string.Equals(roundBox.PlaceTargetId, intent.PlaceTargetId, StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "voice_place_target_mismatch";
                return false;
            }

            BoxRoundStatus status = _roundState.GetStatus(intent.TargetId);
            if (status == BoxRoundStatus.Completed || status == BoxRoundStatus.Excluded)
            {
                failureReason = $"voice_target_not_selectable_{status}";
                return false;
            }

            if (TryFindBoxMetadata(intent.TargetId, out Component metadata) && GetBool(metadata, "isDeposited"))
            {
                failureReason = "voice_target_already_deposited";
                return false;
            }

            return true;
        }

        private bool IsPreparedImmediateVoicePlacePlanningMatch(MultimodalTaskIntent intent, out BoxRoundItem prepared)
        {
            prepared = _preparedImmediateVoicePlacePlanning;
            if (intent == null || prepared == null)
            {
                return false;
            }

            P40TraceMetadata trace = P40TraceContext.EnsureForIntent(intent, intent.Source);
            bool requestMatches = string.IsNullOrWhiteSpace(_preparedImmediateVoicePlacePlanningRequestId) ||
                                  trace == null ||
                                  string.IsNullOrWhiteSpace(trace.RequestId) ||
                                  string.Equals(_preparedImmediateVoicePlacePlanningRequestId, trace.RequestId, StringComparison.OrdinalIgnoreCase);
            return requestMatches &&
                   string.Equals(prepared.BoxId, intent.TargetId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(prepared.PlaceTargetId, intent.PlaceTargetId, StringComparison.OrdinalIgnoreCase);
        }

        private bool TryFindRoundBox(string boxId, out BoxRoundItem roundBox)
        {
            roundBox = null;
            if (string.IsNullOrWhiteSpace(boxId))
            {
                return false;
            }

            foreach (BoxRoundItem box in _roundBoxes)
            {
                if (box != null && string.Equals(box.BoxId, boxId, StringComparison.OrdinalIgnoreCase))
                {
                    roundBox = box;
                    return true;
                }
            }

            return false;
        }

        private Dictionary<string, object> BuildImmediateVoiceSlotPlanningPayload(
            MultimodalTaskIntent intent,
            BoxRoundItem selected,
            string phase,
            string reason)
        {
            return new Dictionary<string, object>
            {
                ["phase"] = phase ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["request_id"] = P40TraceContext.EnsureForIntent(intent, intent != null ? intent.Source : "unknown")?.RequestId ?? string.Empty,
                ["target_id"] = intent != null ? intent.TargetId : string.Empty,
                ["place_target_id"] = intent != null ? intent.PlaceTargetId : string.Empty,
                ["planned_box_id"] = selected != null ? selected.BoxId : string.Empty,
                ["slot_id"] = selected != null ? selected.PlaceSlotId : string.Empty,
                ["slot_quadrant"] = selected != null ? selected.PlaceSlotQuadrant : string.Empty,
                ["slot_index"] = selected != null ? selected.PlaceSlotIndex : -1,
                ["stack_level"] = selected != null ? selected.PlaceStackLevel : 0,
                ["used_place_slot_allocator"] = selected != null && selected.UsedPlaceSlotAllocator,
                ["post_place_egress_point"] = selected != null ? selected.PostPlaceEgressPoint : Vector3.zero,
                ["use_deposit_zone_slot_allocator"] = _useDepositZoneSlotAllocator,
                ["allow_non_slot_dynamic_place_fallback"] = _allowNonSlotDynamicPlaceFallback,
                ["scene"] = SceneManager.GetActiveScene().name
            };
        }

        private Dictionary<string, object> BuildP40BPromotedVoiceSlotPlanningPayload(BoxRoundItem selected, string phase)
        {
            return new Dictionary<string, object>
            {
                ["phase"] = phase ?? string.Empty,
                ["target_id"] = selected != null ? selected.BoxId : string.Empty,
                ["place_target_id"] = selected != null ? selected.PlaceTargetId : string.Empty,
                ["place_navigation_candidate_id"] = selected != null ? selected.PlaceNavigationCandidateId : string.Empty,
                ["slot_id"] = selected != null ? selected.PlaceSlotId : string.Empty,
                ["slot_quadrant"] = selected != null ? selected.PlaceSlotQuadrant : string.Empty,
                ["slot_index"] = selected != null ? selected.PlaceSlotIndex : -1,
                ["stack_level"] = selected != null ? selected.PlaceStackLevel : 0,
                ["used_place_slot_allocator"] = selected != null && selected.UsedPlaceSlotAllocator,
                ["post_place_egress_point"] = selected != null ? selected.PostPlaceEgressPoint : Vector3.zero,
                ["use_deposit_zone_slot_allocator"] = _useDepositZoneSlotAllocator,
                ["allow_non_slot_dynamic_place_fallback"] = _allowNonSlotDynamicPlaceFallback,
                ["scene"] = SceneManager.GetActiveScene().name
            };
        }

        private bool IsP40BPendingVoiceOrderPromotionWindowOpen()
        {
            if (_robotAdapter == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_robotAdapter.HeldObjectId))
            {
                return false;
            }

            if (IsRobotBusy())
            {
                return false;
            }

            if (_pendingPlaceRetry || _pendingPostPlaceEgress || _postPlaceEgressStarted)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_assignedBoxId) &&
                _roundState.GetStatus(_assignedBoxId) == BoxRoundStatus.Assigned)
            {
                return false;
            }

            return true;
        }

        private string ValidateP40BPendingTargetForPromotion(string targetId)
        {
            if (string.IsNullOrWhiteSpace(targetId))
            {
                return "target_no_longer_available";
            }

            if (_roundLifecycle != null && !_roundLifecycle.CurrentRound.RoundActive)
            {
                return "round_ended";
            }

            if (_roundState.GetStatus(targetId) == BoxRoundStatus.Completed ||
                IsP40BTargetAlreadyDepositedOrCompleted(targetId))
            {
                return "target_already_deposited";
            }

            if (_roundState.GetStatus(targetId) != BoxRoundStatus.Pending)
            {
                return "target_no_longer_available";
            }

            if (_robotAdapter != null &&
                string.Equals(_robotAdapter.HeldObjectId, targetId, StringComparison.OrdinalIgnoreCase))
            {
                return "target_no_longer_available";
            }

            return string.Empty;
        }

        private void RefreshAssignedBoxCompletion()
        {
            if (_roundLifecycle == null)
            {
                foreach (BoxRoundItem box in _roundBoxes)
                {
                    if (box == null ||
                        _roundState.GetStatus(box.BoxId) == BoxRoundStatus.Completed ||
                        _roundState.GetStatus(box.BoxId) == BoxRoundStatus.Excluded)
                    {
                        continue;
                    }

                    if (TryFindBoxMetadata(box.BoxId, out Component metadata) && GetBool(metadata, "isDeposited"))
                    {
                        MarkCompletedBox(box.BoxId, "box_deposited_poll_observed_without_round_lifecycle");
                    }
                }

                LogRoundCompletedIfNeeded();
            }

            if (string.IsNullOrWhiteSpace(_assignedBoxId))
            {
                return;
            }

            if (_robotAdapter != null &&
                _robotAdapter.Blackboard != null &&
                _robotAdapter.Blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out TaskStatus status) &&
                status == TaskStatus.Failed)
            {
                if (IsHoldingAssignedBox())
                {
                    if (_assignedTaskFailedPlaceOutOfRange || _pendingPlaceRetry || TrySchedulePlaceApproachRetry(_assignedBoxId))
                    {
                        _assignedTaskFailedPlaceOutOfRange = false;
                        LogPlaceRetryStateSnapshot("held_assigned_box_after_task_failed", string.Empty);
                        return;
                    }

                    LogSelectionBlockedDetailed("robot_holding_object");
                    return;
                }

                if (_assignedTaskFailedOutOfRange && TrySchedulePickApproachRetry(_assignedBoxId))
                {
                    _assignedBoxId = string.Empty;
                    _assignedTaskFailedOutOfRange = false;
                    return;
                }

                if (_assignedTaskFailedPlaceOutOfRange && TrySchedulePlaceApproachRetry(_assignedBoxId))
                {
                    _assignedTaskFailedPlaceOutOfRange = false;
                    return;
                }

                _roundState.TryMarkExcluded(_assignedBoxId);
                LogAssistedBoxFailedAfterRetries(_assignedBoxId, _assignedTaskFailedOutOfRange ? "object_out_of_range_retries_exhausted" : _assignedTaskFailedPlaceOutOfRange ? "place_out_of_range_retries_exhausted" : "assigned_task_failed");
                LogAssistedBoxSkipped(_assignedBoxId, string.Empty, string.Empty, "assigned_task_failed");
                ReleaseDepositSlot(_assignedBoxId, "assigned_task_failed");
                _assignedBoxId = string.Empty;
                _assignedTaskFailedOutOfRange = false;
                _assignedTaskFailedPlaceOutOfRange = false;
                _pendingPlaceRetry = false;
            }
        }

        private void HandleRoundStarted(ExperimentalRoundSnapshot snapshot)
        {
            ClearAssistanceRound("round_started");
            if (!IsOrchestrated && _autoInitializeRound)
            {
                InitializeRoundFromScene();
            }
        }

        private void HandleCorrectDepositRegistered(Component box, ExperimentalRoundSnapshot snapshot)
        {
            if (box == null)
            {
                return;
            }

            string requestedBoxId = box.gameObject.name;
            string resolvedBoxId = ResolveKnownBoxId(box);
            TiagoExperimentTelemetry.LogEvent(
                "assisted_round_deposit_received",
                new Dictionary<string, object>
                {
                    ["reported_box_id"] = requestedBoxId,
                    ["resolved_box_id"] = resolvedBoxId,
                    ["pending_count_before"] = _roundState.PendingCount,
                    ["assigned_count_before"] = _roundState.AssignedCount,
                    ["scene"] = SceneManager.GetActiveScene().name
                });
            _lastDepositTime = Time.time;
            MarkCompletedFromRoundManager(resolvedBoxId, "round_manager_correct_deposit");
        }

        private void HandleRoundCompleted(ExperimentalRoundSnapshot snapshot)
        {
            LogRoundCompletedIfNeeded();
        }

        private void HandleRoundReset(ExperimentalRoundSnapshot snapshot)
        {
            ClearAssistanceRound("round_reset");
        }

        private void MarkCompletedFromRoundManager(string boxId, string reason)
        {
            if (string.IsNullOrWhiteSpace(boxId))
            {
                return;
            }

            MarkCompletedBox(boxId, reason);
        }

        private void MarkCompletedBox(string boxId, string reason)
        {
            if (string.IsNullOrWhiteSpace(boxId))
            {
                return;
            }

            bool wasAssigned = string.Equals(_assignedBoxId, boxId, StringComparison.OrdinalIgnoreCase);
            if (wasAssigned)
            {
                _assignedBoxId = string.Empty;
            }

            bool markedCompleted = _roundState.TryMarkCompleted(boxId);
            if (markedCompleted && _slotAllocator.MarkOccupied(boxId, out DepositZoneSlot occupiedSlot))
            {
                TiagoExperimentTelemetry.LogEvent("deposit_slot_occupied", _slotAllocator.ToPayload(occupiedSlot));
            }
            TiagoExperimentTelemetry.LogEvent(
                "assisted_box_completed",
                new Dictionary<string, object>
                {
                    ["box_id"] = boxId,
                    ["reason"] = reason,
                    ["was_assigned"] = wasAssigned,
                    ["marked_completed"] = markedCompleted,
                    ["pending_count_remaining"] = _roundState.PendingCount,
                    ["assigned_count_remaining"] = _roundState.AssignedCount,
                    ["completed_count"] = _roundState.CompletedCount,
                    ["scene"] = SceneManager.GetActiveScene().name
                });

            if (markedCompleted || wasAssigned)
            {
                ScheduleReevaluation("box_completed");
            }

            LogRoundCompletedIfNeeded();
        }

        private void ScheduleReevaluation(string reason)
        {
            float delay = Mathf.Max(0.05f, _completionReevaluationDelaySeconds);
            _nextSelectionTime = Time.time + delay;
            TiagoExperimentTelemetry.LogEvent(
                "assisted_selection_reevaluation_scheduled",
                new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["delay_seconds"] = delay,
                    ["pending_count_remaining"] = _roundState.PendingCount,
                    ["assigned_count_remaining"] = _roundState.AssignedCount,
                    ["robot_busy"] = IsRobotBusy(),
                    ["explicit_intent_processing"] = _explicitIntentProcessing,
                    ["scene"] = SceneManager.GetActiveScene().name
                });
        }

        private IReadOnlyList<BoxRoundItem> BuildApproachAwareRoundBoxes()
        {
            _lastSelectionIgnoredCandidates.Clear();
            if (!_useDynamicPickupApproach && !_useDynamicPlaceNavigationCandidates)
            {
                return _roundBoxes;
            }

            var updated = new List<BoxRoundItem>(_roundBoxes.Count);
            Vector3 robotPosition = ResolveRobotPosition();
            PickupApproachSettings pickupSettings = BuildPickupApproachSettings();
            PlaceNavigationSettings placeSettings = BuildPlaceNavigationSettings();

            foreach (BoxRoundItem box in _roundBoxes)
            {
                if (box == null || !_roundState.IsSelectable(box))
                {
                    updated.Add(box);
                    continue;
                }

                if (!TryResolvePerceivedObject(box.BoxId, out PerceivedObject perceived))
                {
                    updated.Add(box);
                    continue;
                }

                IReadOnlyList<PickupApproachCandidate> candidates;
                if (_useDynamicPickupApproach)
                {
                    HashSet<string> rejected = _rejectedApproachCandidatesByBox.TryGetValue(box.BoxId, out HashSet<string> set)
                        ? set
                        : null;
                    candidates = PickupApproachSelector.EvaluateCandidates(
                        perceived,
                        robotPosition,
                        box.PlacePosition,
                        pickupSettings,
                        rejected);
                }
                else
                {
                    candidates = new[]
                    {
                        new PickupApproachCandidate
                        {
                            BoxId = box.BoxId,
                            CandidateId = string.IsNullOrWhiteSpace(box.PickupApproachCandidateId) ? $"{box.BoxId}:Fallback" : box.PickupApproachCandidateId,
                            Side = string.IsNullOrWhiteSpace(box.PickupApproachSide) ? "Fallback" : box.PickupApproachSide,
                            Position = box.PickupPosition,
                            Source = string.IsNullOrWhiteSpace(box.PickupApproachSource) ? "ApproachPointFallback" : box.PickupApproachSource,
                            RobotToCandidateCost = Vector3.Distance(robotPosition, box.PickupPosition),
                            CandidateToPlaceCost = Vector3.Distance(box.PickupPosition, box.PlacePosition),
                            TotalCost = Vector3.Distance(robotPosition, box.PickupPosition) + Vector3.Distance(box.PickupPosition, box.PlacePosition),
                            DistanceToBox = Vector3.Distance(box.PickupPosition, perceived.Transform.position),
                            WithinPickRange = true,
                            NavMeshValid = true,
                            PathComplete = true
                        }
                    };
                }

                LogPickupApproachCandidatesEvaluated(box, candidates);

                foreach (PickupApproachCandidate candidate in candidates)
                {
                    if (candidate != null && !candidate.IsSelectable)
                    {
                        TiagoExperimentTelemetry.LogEvent("pickup_approach_candidate_rejected", PickupApproachSelector.ToPayload(candidate));
                    }
                }

                if (TrySelectBestPickupPlacePair(
                        box,
                        candidates,
                        out PickupApproachCandidate selected,
                        out PlaceNavigationCandidate selectedPlace))
                {
                    TiagoExperimentTelemetry.LogEvent("pickup_approach_candidate_selected", PickupApproachSelector.ToPayload(selected));
                    if (selectedPlace != null)
                    {
                        TiagoExperimentTelemetry.LogEvent("place_navigation_candidate_selected", PlaceNavigationCandidateSelector.ToPayload(selectedPlace));
                    }

                    if (_pickApproachRetryCounts.TryGetValue(box.BoxId, out int retryCount) && retryCount > 0)
                    {
                        Dictionary<string, object> retryPayload = PickupApproachSelector.ToPayload(selected);
                        retryPayload["retry_index"] = retryCount;
                        TiagoExperimentTelemetry.LogEvent("assisted_pick_retry_candidate_selected", retryPayload);
                    }

                    updated.Add(new BoxRoundItem(
                        box.BoxId,
                        box.Category,
                        box.PlaceTargetId,
                        selected.Position,
                        selectedPlace != null ? selectedPlace.Position : box.PlacePosition,
                        selected.CandidateId,
                        selected.Side,
                        selected.Source,
                        selectedPlace != null ? selectedPlace.CandidateId : box.PlaceNavigationCandidateId,
                        selectedPlace != null ? selectedPlace.SideOrCorner : box.PlaceNavigationSideOrCorner,
                        selectedPlace != null ? selectedPlace.Source : box.PlaceNavigationSource,
                        selectedPlace != null ? selectedPlace.DynamicPlacePosePosition : box.DynamicPlacePosePosition,
                        selectedPlace != null && selectedPlace.UsedDynamicPlacePose,
                        selectedPlace != null ? selectedPlace.PlacePointFallbackPosition : box.PlacePointFallbackPosition,
                        selectedPlace != null ? selectedPlace.AreaSource : box.PlaceAreaSource,
                        selectedPlace != null ? selectedPlace.PlacePoseInset : box.PlacePoseInset,
                        selectedPlace != null ? selectedPlace.SlotId : box.PlaceSlotId,
                        selectedPlace != null ? selectedPlace.SlotQuadrant : box.PlaceSlotQuadrant,
                        selectedPlace != null ? selectedPlace.SlotIndex : box.PlaceSlotIndex,
                        selectedPlace != null ? selectedPlace.StackLevel : box.PlaceStackLevel,
                        selectedPlace != null && selectedPlace.UsedPlaceSlotAllocator,
                        selectedPlace != null ? selectedPlace.PostPlaceEgressPoint : box.PostPlaceEgressPoint));
                    continue;
                }

                TiagoExperimentTelemetry.LogEvent(
                    "pickup_approach_fallback_used",
                    new Dictionary<string, object>
                    {
                        ["box_id"] = box.BoxId,
                        ["candidate_id"] = box.PickupApproachCandidateId,
                        ["side"] = box.PickupApproachSide,
                        ["candidate_position"] = box.PickupPosition,
                        ["rejection_reason"] = "no_dynamic_candidate_selected",
                        ["source"] = "ApproachPointFallback"
                    });
                if (_useDepositZoneSlotAllocator && !_allowNonSlotDynamicPlaceFallback)
                {
                    TiagoExperimentTelemetry.LogEvent(
                        "assisted_place_selection_failed",
                        new Dictionary<string, object>
                        {
                            ["box_id"] = box.BoxId,
                            ["place_target_id"] = box.PlaceTargetId,
                            ["reason"] = "no_valid_deposit_slot",
                            ["used_place_slot_allocator"] = true,
                            ["allow_non_slot_dynamic_place_fallback"] = _allowNonSlotDynamicPlaceFallback
                        });
                    _lastSelectionIgnoredCandidates.Add(BuildIgnoredSelectionCandidatePayload(
                        box,
                        "no_valid_place_slot",
                        "pending_box_without_required_valid_deposit_slot"));
                    continue;
                }

                updated.Add(box);
            }

            return updated;
        }

        private bool TrySelectBestPickupPlacePair(
            BoxRoundItem box,
            IReadOnlyList<PickupApproachCandidate> pickupCandidates,
            out PickupApproachCandidate selectedPickup,
            out PlaceNavigationCandidate selectedPlace)
        {
            selectedPickup = null;
            selectedPlace = null;
            if (box == null || pickupCandidates == null)
            {
                return false;
            }

            float bestCost = float.PositiveInfinity;
            foreach (PickupApproachCandidate pickup in pickupCandidates)
            {
                if (pickup == null || !pickup.IsSelectable)
                {
                    continue;
                }

                PlaceNavigationCandidate bestPlaceForPickup = null;
                float pickupToPlaceCost = pickup.CandidateToPlaceCost;
                if (_useDynamicPlaceNavigationCandidates &&
                    TryResolvePlaceTargetTransforms(box.PlaceTargetId, out Transform placeTransform, out Transform navigationTransform))
                {
                    IReadOnlyList<DepositZoneSlot> slots = _useDepositZoneSlotAllocator
                        ? _slotAllocator.EvaluateSlots(box.PlaceTargetId, placeTransform, navigationTransform, box.PlacePosition, box.BoxId, BuildDepositZoneSlotSettings())
                        : Array.Empty<DepositZoneSlot>();
                    if (_useDepositZoneSlotAllocator)
                    {
                        LogDepositZoneSlotsGenerated(box, slots);
                    }

                    bool requireSlotSelection = _useDepositZoneSlotAllocator && !_allowNonSlotDynamicPlaceFallback;
                    IEnumerable<PlaceNavigationCandidate> placeCandidatesToEvaluate = _useDepositZoneSlotAllocator
                        ? EvaluatePlaceCandidatesForSlots(box, pickup, placeTransform, navigationTransform, slots)
                        : PlaceNavigationCandidateSelector.EvaluateCandidates(
                            box.PlaceTargetId,
                            placeTransform,
                            navigationTransform,
                            box.PlacePosition,
                            pickup.Position,
                            BuildPlaceNavigationSettings());
                    var placeCandidates = new List<PlaceNavigationCandidate>(placeCandidatesToEvaluate);
                    LogPlaceNavigationCandidatesEvaluated(box, pickup, placeCandidates);

                    if (PlaceNavigationCandidateSelector.TrySelectBest(placeCandidates, out bestPlaceForPickup))
                    {
                        pickupToPlaceCost = bestPlaceForPickup.PickupToCandidateCost;
                        LogDepositSlotSelectionSummary(box, pickup, slots, placeCandidates, bestPlaceForPickup);
                    }
                    else if (requireSlotSelection)
                    {
                        LogDepositSlotSelectionSummary(box, pickup, slots, placeCandidates, null);
                        TiagoExperimentTelemetry.LogEvent(
                            "deposit_slot_all_candidates_rejected",
                            new Dictionary<string, object>
                            {
                                ["box_id"] = box.BoxId,
                                ["place_target_id"] = box.PlaceTargetId,
                                ["pickup_candidate_id"] = pickup.CandidateId,
                                ["candidate_count"] = placeCandidates.Count,
                                ["slot_count"] = slots.Count,
                                ["reason"] = "no_valid_deposit_slot",
                                ["used_place_slot_allocator"] = true,
                                ["allow_non_slot_dynamic_place_fallback"] = _allowNonSlotDynamicPlaceFallback,
                                ["obstacle_layer_mask"] = _placeSlotObstacleLayerMask.value
                            });
                        continue;
                    }
                    else
                    {
                        LogDepositSlotSelectionSummary(box, pickup, slots, placeCandidates, null);
                        TiagoExperimentTelemetry.LogEvent(
                            "place_navigation_fallback_used",
                            new Dictionary<string, object>
                            {
                                ["place_target_id"] = box.PlaceTargetId,
                                ["candidate_id"] = box.PlaceNavigationCandidateId,
                                ["side_or_corner"] = box.PlaceNavigationSideOrCorner,
                                ["candidate_position"] = box.PlacePosition,
                                ["pickup_candidate_id"] = pickup.CandidateId,
                                ["rejection_reason"] = "no_dynamic_place_candidate_selected",
                                ["source"] = "PlaceNavigationPointFallback",
                                ["used_place_slot_allocator"] = false,
                                ["allow_non_slot_dynamic_place_fallback"] = _allowNonSlotDynamicPlaceFallback
                            });
                    }
                }

                float total = pickup.RobotToCandidateCost + pickupToPlaceCost;
                if (total < bestCost)
                {
                    bestCost = total;
                    pickup.CandidateToPlaceCost = pickupToPlaceCost;
                    pickup.TotalCost = total;
                    selectedPickup = pickup;
                    selectedPlace = bestPlaceForPickup;
                }
            }

            return selectedPickup != null;
        }

        private static void LogDepositSlotSelectionSummary(
            BoxRoundItem box,
            PickupApproachCandidate pickup,
            IReadOnlyList<DepositZoneSlot> slots,
            IReadOnlyList<PlaceNavigationCandidate> candidates,
            PlaceNavigationCandidate selected)
        {
            int strict = 0;
            int relaxed = 0;
            int rejected = 0;
            string bestStrict = string.Empty;
            string bestRelaxed = string.Empty;
            var rejectionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (candidates != null)
            {
                foreach (PlaceNavigationCandidate candidate in candidates)
                {
                    if (candidate == null)
                    {
                        continue;
                    }

                    if (!candidate.IsSelectable)
                    {
                        rejected++;
                        string reason = string.IsNullOrWhiteSpace(candidate.RejectionReason) ? "unknown" : candidate.RejectionReason;
                        rejectionCounts[reason] = rejectionCounts.TryGetValue(reason, out int count) ? count + 1 : 1;
                    }
                    else if (candidate.RequiresRelaxedPlace)
                    {
                        relaxed++;
                        if (string.IsNullOrEmpty(bestRelaxed))
                        {
                            bestRelaxed = candidate.CandidateId;
                        }
                    }
                    else
                    {
                        strict++;
                        if (string.IsNullOrEmpty(bestStrict))
                        {
                            bestStrict = candidate.CandidateId;
                        }
                    }
                }
            }

            TiagoExperimentTelemetry.LogEvent(
                "deposit_slot_selection_summary",
                new Dictionary<string, object>
                {
                    ["box_id"] = box != null ? box.BoxId : string.Empty,
                    ["place_target_id"] = box != null ? box.PlaceTargetId : string.Empty,
                    ["pickup_candidate_id"] = pickup != null ? pickup.CandidateId : string.Empty,
                    ["slot_count"] = slots != null ? slots.Count : 0,
                    ["candidate_count"] = candidates != null ? candidates.Count : 0,
                    ["strict_valid_count"] = strict,
                    ["relaxed_valid_count"] = relaxed,
                    ["rejected_count"] = rejected,
                    ["best_strict_candidate"] = bestStrict,
                    ["best_relaxed_candidate"] = bestRelaxed,
                    ["selected_candidate_id"] = selected != null ? selected.CandidateId : string.Empty,
                    ["selected_slot_id"] = selected != null ? selected.SlotId : string.Empty,
                    ["requires_relaxed_place"] = selected != null && selected.RequiresRelaxedPlace,
                    ["top_rejection_reasons"] = string.Join("|", rejectionCounts.Keys)
                });
        }

        private IEnumerable<PlaceNavigationCandidate> EvaluatePlaceCandidatesForSlots(
            BoxRoundItem box,
            PickupApproachCandidate pickup,
            Transform placeTransform,
            Transform navigationTransform,
            IReadOnlyList<DepositZoneSlot> slots)
        {
            if (slots == null)
            {
                yield break;
            }

            foreach (DepositZoneSlot slot in slots)
            {
                if (slot == null || !slot.IsAvailable)
                {
                    continue;
                }

                var slotSettings = new PlaceNavigationSettings(
                    _useDynamicPlaceNavigationCandidates,
                    _useCornerPlaceNavigationCandidates,
                    _placeNavigationStandoff,
                    _maxExpectedPlaceDistance,
                    _placeDistanceSafetyMargin,
                    _placeCandidateReachabilityMargin,
                    _placeCandidateArrivalTolerance,
                    _useDynamicPlacePose,
                    _placePoseInset,
                    _minPlacePoseClearance,
                    _navMeshSampleRadius,
                    _maxRelaxedPlaceDistance,
                    slot.Position,
                    slot.SlotId,
                    slot.Quadrant,
                    slot.SlotIndex,
                    slot.StackLevel,
                    true,
                    _usePostPlaceEgressValidation,
                    _postPlaceEgressDistance,
                    _postPlaceEgressSampleRadius,
                    _postPlaceEgressClearanceRadius,
                    _postPlaceEgressSafetyMargin,
                    _requirePostPlaceEgressPath,
                    _placeSlotObstacleLayerMask);
                IReadOnlyList<PlaceNavigationCandidate> candidates = PlaceNavigationCandidateSelector.EvaluateCandidates(
                    box.PlaceTargetId,
                    placeTransform,
                    navigationTransform,
                    box.PlacePosition,
                    pickup.Position,
                    slotSettings);
                foreach (PlaceNavigationCandidate candidate in candidates)
                {
                    yield return candidate;
                }
            }
        }

        private PickupApproachSettings BuildPickupApproachSettings()
        {
            return new PickupApproachSettings(
                _useDynamicPickupApproach,
                ResolveEffectiveUseCornerPickupApproachCandidates(),
                _pickApproachStandoff,
                _maxExpectedPickDistance,
                _pickDistanceSafetyMargin,
                _navMeshSampleRadius);
        }

        private bool ResolveEffectiveUseCornerPickupApproachCandidates()
        {
            return _useCornerPickupApproachCandidates ||
                   (_commandBridge != null && _commandBridge.UseCornerPickupApproachCandidates);
        }

        private PlaceNavigationSettings BuildPlaceNavigationSettings()
        {
            return new PlaceNavigationSettings(
                _useDynamicPlaceNavigationCandidates,
                _useCornerPlaceNavigationCandidates,
                _placeNavigationStandoff,
                _maxExpectedPlaceDistance,
                _placeDistanceSafetyMargin,
                _placeCandidateReachabilityMargin,
                _placeCandidateArrivalTolerance,
                _useDynamicPlacePose,
                _placePoseInset,
                _minPlacePoseClearance,
                _navMeshSampleRadius,
                _maxRelaxedPlaceDistance);
        }

        private DepositZoneSlotSettings BuildDepositZoneSlotSettings()
        {
            return new DepositZoneSlotSettings(
                _placeSlotInset,
                _placeSlotClearance,
                _placeSlotOccupancyCheckExtents,
                _placeSlotObstacleLayerMask,
                _placeSlotStackVerticalSpacing,
                _placeSlotDefaultBoxHeight,
                _keepZoneAccessMargin,
                _navMeshSampleRadius);
        }

        private bool TrySchedulePickApproachRetry(string boxId)
        {
            if (string.IsNullOrWhiteSpace(boxId))
            {
                return false;
            }

            int retryCount = _pickApproachRetryCounts.TryGetValue(boxId, out int current) ? current : 0;
            if (retryCount >= Mathf.Max(0, _maxPickApproachRetries))
            {
                return false;
            }

            if (_assignedApproachCandidateByBox.TryGetValue(boxId, out string candidateId) &&
                !string.IsNullOrWhiteSpace(candidateId))
            {
                if (!_rejectedApproachCandidatesByBox.TryGetValue(boxId, out HashSet<string> rejected))
                {
                    rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _rejectedApproachCandidatesByBox[boxId] = rejected;
                }

                rejected.Add(candidateId);
            }

            _pickApproachRetryCounts[boxId] = retryCount + 1;
            if (!_roundState.TryMarkPendingFromAssigned(boxId))
            {
                return false;
            }

            ReleaseDepositSlot(boxId, "pick_out_of_range_retry");

            TiagoExperimentTelemetry.LogEvent(
                "assisted_pick_retry_scheduled",
                new Dictionary<string, object>
                {
                    ["box_id"] = boxId,
                    ["retry_index"] = retryCount + 1,
                    ["max_pick_approach_retries"] = _maxPickApproachRetries,
                    ["failed_candidate_id"] = candidateId ?? string.Empty,
                    ["scene"] = SceneManager.GetActiveScene().name
                });
            ScheduleReevaluation("pick_out_of_range_retry");
            return true;
        }

        private bool TrySchedulePlaceApproachRetry(string boxId)
        {
            if (string.IsNullOrWhiteSpace(boxId))
            {
                return false;
            }

            int retryCount = _placeApproachRetryCounts.TryGetValue(boxId, out int current) ? current : 0;
            if (retryCount >= Mathf.Max(0, _maxPlaceApproachRetries))
            {
                return false;
            }

            if (_assignedPlaceCandidateByBox.TryGetValue(boxId, out string candidateId) &&
                !string.IsNullOrWhiteSpace(candidateId))
            {
                if (!_rejectedPlaceCandidatesByBox.TryGetValue(boxId, out HashSet<string> rejected))
                {
                    rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _rejectedPlaceCandidatesByBox[boxId] = rejected;
                }

                rejected.Add(candidateId);
            }

            _placeApproachRetryCounts[boxId] = retryCount + 1;
            _pendingPlaceRetry = true;
            TiagoExperimentTelemetry.LogEvent(
                "assisted_place_retry_scheduled",
                new Dictionary<string, object>
                {
                    ["box_id"] = boxId,
                    ["held_object_id"] = _robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty,
                    ["retry_index"] = retryCount + 1,
                    ["max_place_approach_retries"] = _maxPlaceApproachRetries,
                    ["failed_candidate_id"] = candidateId ?? string.Empty,
                    ["scene"] = SceneManager.GetActiveScene().name
                });
            LogPlaceRetryStateSnapshot("scheduled", string.Empty);
            ScheduleReevaluation("place_out_of_range_retry");
            return true;
        }

        private bool TrySubmitPendingPlaceRetry()
        {
            if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
            {
                LogPlaceRetryBlocked(ExperimentRuntimePauseCoordinator.AutonomyRequestRejectionReason);
                return false;
            }

            if (IsSupervisoryStopLatched())
            {
                LogVoiceStopLatchedAutoSelectionBlockedNeutral("place_retry_blocked");
                LogPlaceRetryBlocked("voice_stop_latched");
                return false;
            }

            if (!IsExperimentTrialAllowedForAssistance())
            {
                LogAssistanceBlocked("assistance_blocked_by_inactive_or_invalid_trial", ResolveAssistanceTrialBlockReason());
                LogPlaceRetryBlocked(ResolveAssistanceTrialBlockReason());
                return false;
            }

            if (string.IsNullOrWhiteSpace(_assignedBoxId) || _commandBridge == null)
            {
                LogPlaceRetryBlocked("missing_assigned_box_or_bridge");
                return false;
            }

            if (_robotAdapter == null || !string.Equals(_robotAdapter.HeldObjectId, _assignedBoxId, StringComparison.OrdinalIgnoreCase))
            {
                LogPlaceRetryBlocked("held_object_not_assigned_box");
                return false;
            }

            BoxRoundItem box = FindRoundBox(_assignedBoxId);
            if (box == null)
            {
                LogPlaceRetryBlocked("assigned_box_not_found");
                return false;
            }

            if (Time.time < _nextSelectionTime || IsRobotBusy())
            {
                LogPlaceRetryBlocked(Time.time < _nextSelectionTime ? "waiting_retry_delay" : "robot_busy");
                return false;
            }

            TiagoExperimentTelemetry.LogEvent(
                "assisted_place_retry_allowed_while_holding",
                BuildPlaceRetrySnapshotPayload("allowed_while_holding", string.Empty));

            Vector3 heldRetryPickupPosition = ResolveRobotPosition();
            _commandBridge.SetPreferredPickupApproach(
                box.BoxId,
                heldRetryPickupPosition,
                $"{box.BoxId}:HeldObjectCurrentPose",
                "HeldObjectCurrentPose",
                "PlaceRetryHeldObject");

            if (TrySelectPlaceRetryCandidate(box, heldRetryPickupPosition, out PlaceNavigationCandidate selectedPlace))
            {
                _assignedPlaceCandidateByBox[box.BoxId] = selectedPlace.CandidateId;
                _commandBridge.SetPreferredPlaceNavigation(
                    box.PlaceTargetId,
                    selectedPlace.Position,
                    selectedPlace.CandidateId,
                    selectedPlace.SideOrCorner,
                    selectedPlace.Source);
                _commandBridge.SetPreferredDynamicPlacePose(
                    box.PlaceTargetId,
                    selectedPlace.DynamicPlacePosePosition,
                    selectedPlace.CandidateId,
                    selectedPlace.UsedDynamicPlacePose,
                    selectedPlace.Source != null && selectedPlace.Source.Contains("Corner") ? "corner" : "side",
                    selectedPlace.AreaSource,
                    selectedPlace.PlacePoseInset,
                    selectedPlace.PlacePointFallbackPosition,
                    selectedPlace.SlotId,
                    selectedPlace.SlotQuadrant,
                    selectedPlace.SlotIndex,
                    selectedPlace.StackLevel,
                    selectedPlace.UsedPlaceSlotAllocator,
                    selectedPlace.PostPlaceEgressPoint);
                Dictionary<string, object> payload = PlaceNavigationCandidateSelector.ToPayload(selectedPlace);
                payload["box_id"] = box.BoxId;
                payload["retry_index"] = _placeApproachRetryCounts.TryGetValue(box.BoxId, out int retryCount) ? retryCount : 0;
                TiagoExperimentTelemetry.LogEvent("assisted_place_retry_candidate_selected", payload);
                LogPlaceRetryStateSnapshot("candidate_selected", selectedPlace.CandidateId);
            }
            else
            {
                TiagoExperimentTelemetry.LogEvent(
                    "assisted_place_retry_no_valid_place_candidate",
                    BuildPlaceRetrySnapshotPayload("no_valid_place_candidate", "using_fallback_navigation_point"));
                _commandBridge.SetPreferredPlaceNavigation(
                    box.PlaceTargetId,
                    box.PlacePosition,
                    string.IsNullOrWhiteSpace(box.PlaceNavigationCandidateId) ? $"{box.PlaceTargetId}:Fallback" : box.PlaceNavigationCandidateId,
                    "Fallback",
                    "PlaceNavigationPointFallback");
            }

            MultimodalTaskIntent intent = MultimodalTaskIntent.PickAndPlaceByTargetId(
                box.BoxId,
                box.PlaceTargetId,
                "assisted_place_retry");
            bool submitted = _commandBridge.SubmitIntent(intent);
            TiagoExperimentTelemetry.LogEvent(
                "assisted_place_retry_submitted",
                new Dictionary<string, object>
                {
                    ["box_id"] = box.BoxId,
                    ["place_target_id"] = box.PlaceTargetId,
                    ["bridge_accepted"] = submitted,
                    ["held_object_id"] = _robotAdapter.HeldObjectId
                });

            if (submitted)
            {
                _pendingPlaceRetry = false;
            }
            else
            {
                LogPlaceRetryBlocked("bridge_rejected_retry");
            }

            return submitted;
        }

        private bool TrySelectPlaceRetryCandidate(BoxRoundItem box, Vector3 pickupPosition, out PlaceNavigationCandidate selectedPlace)
        {
            selectedPlace = null;
            if (box == null ||
                !TryResolvePlaceTargetTransforms(box.PlaceTargetId, out Transform placeTransform, out Transform navigationTransform))
            {
                return false;
            }

            IReadOnlyList<DepositZoneSlot> slots = _useDepositZoneSlotAllocator
                ? _slotAllocator.EvaluateSlots(box.PlaceTargetId, placeTransform, navigationTransform, box.PlacePosition, box.BoxId, BuildDepositZoneSlotSettings())
                : Array.Empty<DepositZoneSlot>();
            if (_useDepositZoneSlotAllocator)
            {
                LogDepositZoneSlotsGenerated(box, slots);
            }

            IReadOnlyList<PlaceNavigationCandidate> candidates = _useDepositZoneSlotAllocator && slots.Count > 0
                ? new List<PlaceNavigationCandidate>(EvaluatePlaceCandidatesForSlots(
                    box,
                    new PickupApproachCandidate { Position = pickupPosition, CandidateId = $"{box.BoxId}:HeldObjectCurrentPose" },
                    placeTransform,
                    navigationTransform,
                    slots))
                : PlaceNavigationCandidateSelector.EvaluateCandidates(
                    box.PlaceTargetId,
                    placeTransform,
                    navigationTransform,
                    box.PlacePosition,
                    pickupPosition,
                    BuildPlaceNavigationSettings());
            LogPlaceNavigationCandidatesEvaluated(box, null, candidates);
            HashSet<string> rejected = _rejectedPlaceCandidatesByBox.TryGetValue(box.BoxId, out HashSet<string> set)
                ? set
                : null;
            foreach (PlaceNavigationCandidate candidate in candidates)
            {
                if (candidate == null)
                {
                    continue;
                }

                if (rejected != null && rejected.Contains(candidate.CandidateId))
                {
                    candidate.RejectionReason = "candidate_previously_failed";
                }

            }

            return PlaceNavigationCandidateSelector.TrySelectBest(candidates, out selectedPlace);
        }

        private bool IsHoldingAssignedBox()
        {
            return _robotAdapter != null &&
                   !string.IsNullOrWhiteSpace(_assignedBoxId) &&
                   string.Equals(_robotAdapter.HeldObjectId, _assignedBoxId, StringComparison.OrdinalIgnoreCase);
        }

        private void LogPlaceRetryBlocked(string reason)
        {
            TiagoExperimentTelemetry.LogEvent("assisted_place_retry_blocked", BuildPlaceRetrySnapshotPayload("blocked", reason));
        }

        private void LogPlaceRetryStateSnapshot(string phase, string nextCandidateId)
        {
            TiagoExperimentTelemetry.LogEvent("assisted_place_retry_state_snapshot", BuildPlaceRetrySnapshotPayload(phase, string.Empty, nextCandidateId));
        }

        private Dictionary<string, object> BuildPlaceRetrySnapshotPayload(string phase, string reason, string nextCandidateId = "")
        {
            string heldObjectId = _robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty;
            BoxRoundItem box = FindRoundBox(_assignedBoxId);
            TryFindBoxMetadata(_assignedBoxId, out Component metadata);
            bool transformBelongs = metadata == null || _roundLifecycle == null || _roundLifecycle.IsInActiveRound(metadata);
            return new Dictionary<string, object>
            {
                ["phase"] = phase ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["held_object_id"] = heldObjectId,
                ["assigned_box_id"] = _assignedBoxId,
                ["box_state"] = _roundState.GetStatus(_assignedBoxId).ToString(),
                ["place_target_id"] = box != null ? box.PlaceTargetId : string.Empty,
                ["retry_count"] = _placeApproachRetryCounts.TryGetValue(_assignedBoxId ?? string.Empty, out int retryCount) ? retryCount : 0,
                ["max_retries"] = _maxPlaceApproachRetries,
                ["pending_count"] = _roundState.PendingCount,
                ["assigned_count"] = _roundState.AssignedCount,
                ["completed_count"] = _roundState.CompletedCount,
                ["excluded_count"] = _roundState.ExcludedCount,
                ["belongs_to_active_round_logical"] = box != null,
                ["belongs_to_active_round_transform_parent"] = transformBelongs,
                ["last_failed_place_candidate_id"] = _assignedPlaceCandidateByBox.TryGetValue(_assignedBoxId ?? string.Empty, out string failedCandidate) ? failedCandidate : string.Empty,
                ["next_candidate_id"] = nextCandidateId ?? string.Empty,
                ["scene"] = SceneManager.GetActiveScene().name
            };
        }

        private void HandleStructuredEvent(string eventType, Dictionary<string, object> payload, float unityTime)
        {
            if (payload == null)
            {
                return;
            }

            TrackOrchestratedTrialGate(eventType, payload);

            if (string.Equals(eventType, "manipulation_place_succeeded", StringComparison.Ordinal))
            {
                string placedObjectId = TryGetPayloadString(payload, "object_name");
                if (string.IsNullOrWhiteSpace(placedObjectId))
                {
                    placedObjectId = TryGetPayloadString(payload, "held_object_id");
                }

                if (!string.IsNullOrWhiteSpace(_assignedBoxId) &&
                    !string.IsNullOrWhiteSpace(placedObjectId) &&
                    !string.Equals(placedObjectId, _assignedBoxId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                PreparePostPlaceContext(payload, placedObjectId);
                bool hasEgressPoint = TryGetPayloadVector(payload, "post_place_egress_point", out Vector3 egressPoint);
                if (hasEgressPoint)
                {
                    _postPlaceEgressPoint = egressPoint;
                }

                bool usesSlot = TryGetPayloadBool(payload, "used_place_slot_allocator");
                bool egressRequired = _enablePostPlaceRetreat && hasEgressPoint && usesSlot;
                TiagoExperimentTelemetry.LogEvent(
                    "post_place_egress_decision",
                    BuildPostPlaceEgressPayload(
                        "decision",
                        egressRequired ? "egress_required" :
                            !_enablePostPlaceRetreat ? "retreat_disabled" :
                            !usesSlot ? "place_did_not_use_slot_allocator" :
                            "missing_post_place_egress_point"));

                if (_enablePostPlaceRetreat && hasEgressPoint)
                {
                    _pendingPostPlaceEgress = true;
                    _postPlaceEgressStarted = false;
                    _pendingDepositedObstacleRegistration = true;
                }
                else if (_enablePostPlaceRetreat)
                {
                    TiagoExperimentTelemetry.LogEvent(
                        "post_place_egress_not_started",
                        BuildPostPlaceEgressPayload("not_started", "missing_post_place_egress_point"));
                    CompletePostPlaceEgress("not_started_missing_post_place_egress_point");
                }
                else
                {
                    TiagoExperimentTelemetry.LogEvent(
                        "post_place_egress_not_started",
                        BuildPostPlaceEgressPayload("not_started", "retreat_disabled"));
                    CompletePostPlaceEgress("immediate_no_egress_required");
                }

                return;
            }

            if (string.IsNullOrWhiteSpace(_assignedBoxId))
            {
                return;
            }

            if (string.Equals(eventType, "manipulation_place_failed", StringComparison.Ordinal))
            {
                string placeFailureReason = TryGetPayloadString(payload, "reason");
                if (!string.Equals(_robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty, _assignedBoxId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                TiagoExperimentTelemetry.LogEvent(
                    "assisted_place_failed",
                    new Dictionary<string, object>
                    {
                        ["box_id"] = _assignedBoxId,
                        ["reason"] = placeFailureReason,
                        ["held_object_id"] = _robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty,
                        ["place_candidate_id"] = _assignedPlaceCandidateByBox.TryGetValue(_assignedBoxId, out string placeCandidateId) ? placeCandidateId : string.Empty,
                        ["scene"] = SceneManager.GetActiveScene().name
                    });

                if (string.Equals(placeFailureReason, "place_out_of_range", StringComparison.OrdinalIgnoreCase))
                {
                    _assignedTaskFailedPlaceOutOfRange = true;
                }

                return;
            }

            if (!string.Equals(eventType, "manipulation_pick_failed", StringComparison.Ordinal))
            {
                return;
            }

            string objectId = TryGetPayloadString(payload, "object_id");
            string reason = TryGetPayloadString(payload, "reason");
            if (!string.Equals(objectId, _assignedBoxId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            TiagoExperimentTelemetry.LogEvent(
                "assisted_pick_failed",
                new Dictionary<string, object>
                {
                    ["box_id"] = _assignedBoxId,
                    ["reason"] = reason,
                    ["approach_candidate_id"] = _assignedApproachCandidateByBox.TryGetValue(_assignedBoxId, out string candidateId) ? candidateId : string.Empty,
                    ["scene"] = SceneManager.GetActiveScene().name
                });

            if (string.Equals(reason, "object_out_of_range", StringComparison.OrdinalIgnoreCase))
            {
                _assignedTaskFailedOutOfRange = true;
            }
        }

        private void TrackOrchestratedTrialGate(string eventType, Dictionary<string, object> payload)
        {
            if (!IsOrchestrated)
            {
                return;
            }

            if (string.Equals(eventType, "experiment_trial_prepare_started", StringComparison.Ordinal))
            {
                _orchestratedTrialActive = false;
                _orchestratedTrialInvalid = false;
                _orchestratedTrialInvalidReason = string.Empty;
                return;
            }

            if (string.Equals(eventType, "experiment_trial_started_by_orchestrator", StringComparison.Ordinal))
            {
                _orchestratedTrialActive = true;
                _orchestratedTrialInvalid = false;
                _orchestratedTrialInvalidReason = string.Empty;
                return;
            }

            if (string.Equals(eventType, "robot_pose_reset_failed", StringComparison.Ordinal) ||
                string.Equals(eventType, "experiment_prepared_trial_invalid_summary_written", StringComparison.Ordinal))
            {
                _orchestratedTrialActive = false;
                _orchestratedTrialInvalid = true;
                _orchestratedTrialInvalidReason = string.Equals(eventType, "robot_pose_reset_failed", StringComparison.Ordinal)
                    ? "robot_pose_reset_failed"
                    : TryGetPayloadString(payload, "failure_reason");
                return;
            }

            if (string.Equals(eventType, "experiment_trial_completed_by_orchestrator", StringComparison.Ordinal) ||
                string.Equals(eventType, "experiment_trial_completed", StringComparison.Ordinal) ||
                string.Equals(eventType, "experiment_trial_failed", StringComparison.Ordinal) ||
                string.Equals(eventType, "experiment_trial_aborted", StringComparison.Ordinal))
            {
                _orchestratedTrialActive = false;
            }
        }

        private bool IsExperimentTrialAllowedForAssistance()
        {
            return !IsOrchestrated || (_orchestratedTrialActive && !_orchestratedTrialInvalid);
        }

        private string ResolveAssistanceTrialBlockReason()
        {
            if (!IsOrchestrated)
            {
                return string.Empty;
            }

            if (_orchestratedTrialInvalid)
            {
                return string.IsNullOrWhiteSpace(_orchestratedTrialInvalidReason)
                    ? "trial_invalid_before_start"
                    : _orchestratedTrialInvalidReason;
            }

            if (!_orchestratedTrialActive)
            {
                return "trial_not_started_by_orchestrator";
            }

            return string.Empty;
        }

        private bool ProcessPostPlaceEgress()
        {
            if (!_enablePostPlaceRetreat || _robotAdapter == null)
            {
                CompletePostPlaceEgress("disabled_or_missing_adapter");
                return false;
            }

            if (!_postPlaceEgressStarted)
            {
                StartPostPlaceEgress();
            }

            if (_postPlaceEgressMode == PostPlaceEgressMode.LocalRetreat ||
                (_postPlaceEgressMode == PostPlaceEgressMode.ShortNav &&
                 ResolvePlanarDistance(ResolveRobotPosition(), _postPlaceEgressPoint) <= _postPlaceEgressShortDistanceThreshold))
            {
                return ProcessPostPlaceLocalRetreat();
            }

            _postPlaceEgressUsedStartupAlignment = true;
            NodeStatus status = _robotAdapter.StepPostPlaceRetreat(_postPlaceEgressPoint);
            if (status == NodeStatus.Success)
            {
                _postPlaceEgressSuccessCriterion = "full_navigation_arrived";
                TiagoExperimentTelemetry.LogEvent(
                    "post_place_egress_succeeded",
                    BuildPostPlaceEgressPayload("succeeded", string.Empty));
                CompletePostPlaceEgress("succeeded");
                return false;
            }

            if (status == NodeStatus.Failure || Time.time - _postPlaceEgressStartedAt > Mathf.Max(0.5f, _postPlaceRetreatTimeoutSeconds))
            {
                string reason = status == NodeStatus.Failure ? "navigation_failed" : "timeout";
                TiagoExperimentTelemetry.LogEvent(
                    "post_place_egress_failed",
                    BuildPostPlaceEgressPayload("failed", reason));
                CompletePostPlaceEgress(reason);
                return false;
            }

            return true;
        }

        private void StartPostPlaceEgress()
        {
            _postPlaceEgressStarted = true;
            _postPlaceEgressStartedAt = Time.time;
            _postPlaceEgressStartPosition = ResolveRobotPosition();
            _postPlaceEgressStartDistanceToTarget = ResolvePlanarDistance(_postPlaceEgressStartPosition, _postPlaceEgressPoint);
            _postPlaceEgressStartDistanceFromBox = ResolvePlanarDistance(_postPlaceEgressStartPosition, _postPlaceSlotPosition);
            _postPlaceEgressHeadingErrorStart = ResolveHeadingErrorDegrees(_postPlaceEgressStartPosition, _postPlaceEgressPoint);
            _postPlaceLocalRetreatNextProgressLogTime = Time.time;
            _postPlaceEgressSuccessCriterion = string.Empty;
            _postPlaceEgressUsedStartupAlignment = _postPlaceEgressMode == PostPlaceEgressMode.FullNav;

            TiagoExperimentTelemetry.LogEvent(
                "post_place_egress_started",
                BuildPostPlaceEgressPayload("started", string.Empty));

            if (_postPlaceEgressMode == PostPlaceEgressMode.LocalRetreat ||
                (_postPlaceEgressMode == PostPlaceEgressMode.ShortNav &&
                 _postPlaceEgressStartDistanceToTarget <= _postPlaceEgressShortDistanceThreshold))
            {
                TiagoExperimentTelemetry.LogEvent(
                    "post_place_local_retreat_started",
                    BuildPostPlaceEgressPayload("local_retreat_started", string.Empty));
            }
        }

        private bool ProcessPostPlaceLocalRetreat()
        {
            Vector3 currentPosition = ResolveRobotPosition();
            float distanceToTarget = ResolvePlanarDistance(currentPosition, _postPlaceEgressPoint);
            float distanceFromBox = ResolvePlanarDistance(currentPosition, _postPlaceSlotPosition);
            float elapsed = Time.time - _postPlaceEgressStartedAt;

            if (TryResolveLocalRetreatSuccessCriterion(distanceToTarget, distanceFromBox, out string successCriterion))
            {
                _postPlaceEgressSuccessCriterion = successCriterion;
                _robotAdapter.StopPostPlaceRetreat();
                TiagoExperimentTelemetry.LogEvent(
                    "post_place_local_retreat_succeeded",
                    BuildPostPlaceEgressPayload("local_retreat_succeeded", string.Empty));
                TiagoExperimentTelemetry.LogEvent(
                    "post_place_egress_succeeded",
                    BuildPostPlaceEgressPayload("succeeded", string.Empty));
                CompletePostPlaceEgress("succeeded");
                return false;
            }

            if (elapsed > Mathf.Max(0.5f, _postPlaceRetreatTimeoutSeconds))
            {
                _postPlaceEgressSuccessCriterion = "none";
                _robotAdapter.StopPostPlaceRetreat();
                TiagoExperimentTelemetry.LogEvent(
                    "post_place_local_retreat_failed",
                    BuildPostPlaceEgressPayload("local_retreat_failed", "timeout"));
                TiagoExperimentTelemetry.LogEvent(
                    "post_place_egress_failed",
                    BuildPostPlaceEgressPayload("failed", "local_retreat_timeout"));
                CompletePostPlaceEgress("local_retreat_timeout");
                return false;
            }

            if (Time.time >= _postPlaceLocalRetreatNextProgressLogTime)
            {
                _postPlaceLocalRetreatNextProgressLogTime = Time.time + Mathf.Max(0.1f, _postPlaceLocalRetreatProgressLogInterval);
                TiagoExperimentTelemetry.LogEvent(
                    "post_place_local_retreat_progress",
                    BuildPostPlaceEgressPayload("local_retreat_progress", string.Empty));
            }

            if (!TryApplyLocalRetreatCommand(currentPosition))
            {
                _postPlaceEgressSuccessCriterion = "none";
                TiagoExperimentTelemetry.LogEvent(
                    "post_place_local_retreat_failed",
                    BuildPostPlaceEgressPayload("local_retreat_failed", "drive_unavailable"));
                TiagoExperimentTelemetry.LogEvent(
                    "post_place_egress_failed",
                    BuildPostPlaceEgressPayload("failed", "local_retreat_drive_unavailable"));
                CompletePostPlaceEgress("local_retreat_drive_unavailable");
                return false;
            }

            return true;
        }

        private bool TryResolveLocalRetreatSuccessCriterion(float distanceToTarget, float distanceFromBox, out string criterion)
        {
            if (distanceToTarget <= _postPlaceEgressArrivalTolerance)
            {
                criterion = "reached_egress_point";
                return true;
            }

            if (distanceFromBox >= _postPlaceLocalRetreatSafeDistance)
            {
                criterion = "reached_safe_distance_from_deposited_box";
                return true;
            }

            if (distanceFromBox - _postPlaceEgressStartDistanceFromBox >= _postPlaceLocalRetreatMinDistanceGain)
            {
                criterion = "increased_distance_from_deposited_box";
                return true;
            }

            criterion = string.Empty;
            return false;
        }

        private bool TryApplyLocalRetreatCommand(Vector3 currentPosition)
        {
            Vector3 targetDirection = Flatten(_postPlaceEgressPoint - currentPosition);
            if (targetDirection.sqrMagnitude < 0.0001f)
            {
                targetDirection = Flatten(currentPosition - _postPlaceSlotPosition);
            }

            if (targetDirection.sqrMagnitude < 0.0001f)
            {
                targetDirection = -Flatten(_robotAdapter.NavigationReference.forward);
            }

            Vector3 forward = Flatten(_robotAdapter.NavigationReference.forward);
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = Vector3.forward;
            }

            float directionSign = Vector3.Dot(forward.normalized, targetDirection.normalized) >= 0f ? 1f : -1f;
            return _robotAdapter.TryApplyPostPlaceLocalRetreat(directionSign * Mathf.Max(0.01f, _postPlaceLocalRetreatSpeed));
        }

        private void PreparePostPlaceContext(Dictionary<string, object> payload, string placedObjectId)
        {
            _postPlaceEgressBoxId = !string.IsNullOrWhiteSpace(placedObjectId)
                ? placedObjectId
                : _assignedBoxId;
            _postPlaceEgressSlotId = TryGetPayloadString(payload, "slot_id");
            _postPlaceEgressPlaceTargetId = TryGetPayloadString(payload, "place_target_id");
            if (string.IsNullOrWhiteSpace(_postPlaceEgressPlaceTargetId))
            {
                _postPlaceEgressPlaceTargetId = TryGetPayloadString(payload, "object_id");
            }
            if (string.IsNullOrWhiteSpace(_postPlaceEgressPlaceTargetId))
            {
                _postPlaceEgressPlaceTargetId = TryGetPayloadString(payload, "place_pose_name");
            }

            TryGetPayloadVector(payload, "slot_position", out _postPlaceSlotPosition);
            _pendingDepositedObstacleRegistration = !string.IsNullOrWhiteSpace(_postPlaceEgressBoxId);
        }

        private void CompletePostPlaceEgress(string reason)
        {
            _postPlaceEgressLastDuration = _postPlaceEgressStarted
                ? Mathf.Max(0f, Time.time - _postPlaceEgressStartedAt)
                : 0f;
            _pendingPostPlaceEgress = false;
            _postPlaceEgressStarted = false;
            _robotAdapter?.StopPostPlaceRetreat();
            if (!string.Equals(reason, "succeeded", StringComparison.OrdinalIgnoreCase) &&
                _postPlaceEgressFailurePolicy == PostPlaceEgressFailurePolicy.ContinueWithWarning)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "post_place_egress_continue_with_warning",
                    BuildPostPlaceEgressPayload("continue_with_warning", reason));
            }

            if (_pendingDepositedObstacleRegistration)
            {
                bool registered = _robotAdapter != null && _robotAdapter.RegisterDepositedBoxObstacle(_postPlaceEgressBoxId);
                _pendingDepositedObstacleRegistration = false;
                if (!registered)
                {
                    TiagoExperimentTelemetry.LogEvent(
                        "deposited_box_obstacle_registration_failed",
                        BuildPostPlaceEgressPayload("obstacle_registration_failed", "adapter_or_box_missing"));
                }
            }
            else
            {
                TiagoExperimentTelemetry.LogEvent(
                    "deposited_box_obstacle_registration_skipped",
                    BuildPostPlaceEgressPayload("obstacle_registration_skipped", "no_pending_obstacle_registration"));
            }

            TiagoExperimentTelemetry.LogEvent(
                "assistance_paths_invalidated_after_deposit",
                BuildPostPlaceEgressPayload("paths_invalidated", reason));
            ActiveRoundBoxRegistry.TryResolve(_postPlaceEgressBoxId, out GameObject depositedBox);
            P40NavTraceDiagnostics.LogPostPlaceObstacleRegistrationDiagnostic(
                "paths_invalidated",
                reason,
                depositedBox,
                obstacleRegistered: !_pendingDepositedObstacleRegistration,
                pathsInvalidated: true,
                adapter: _robotAdapter,
                boxId: _postPlaceEgressBoxId,
                zoneId: _postPlaceEgressPlaceTargetId,
                egressRequired: _enablePostPlaceRetreat && _postPlaceEgressPoint != default,
                egressResult: reason,
                timeSincePlaceSucceeded: _postPlaceEgressLastDuration);
            P40NavTraceDiagnostics.LogObstacleRegistrySnapshot(
                "assistance_paths_invalidated_after_deposit",
                reason,
                _robotAdapter,
                lastDepositedBoxId: _postPlaceEgressBoxId);
            ScheduleReevaluation("post_place_egress_completed");
        }

        private Dictionary<string, object> BuildPostPlaceEgressPayload(string phase, string reason)
        {
            Vector3 currentPosition = ResolveRobotPosition();
            float distanceToTargetEnd = ResolvePlanarDistance(currentPosition, _postPlaceEgressPoint);
            float distanceFromBoxEnd = ResolvePlanarDistance(currentPosition, _postPlaceSlotPosition);
            return new Dictionary<string, object>
            {
                ["phase"] = phase ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["egress_reason"] = reason ?? string.Empty,
                ["egress_mode"] = _postPlaceEgressMode.ToString(),
                ["box_id"] = _postPlaceEgressBoxId ?? string.Empty,
                ["place_target_id"] = _postPlaceEgressPlaceTargetId ?? string.Empty,
                ["slot_id"] = _postPlaceEgressSlotId ?? string.Empty,
                ["slot_position"] = _postPlaceSlotPosition,
                ["post_place_egress_point"] = _postPlaceEgressPoint,
                ["robot_position_at_place"] = currentPosition,
                ["start_position"] = _postPlaceEgressStartPosition,
                ["target_position"] = _postPlaceEgressPoint,
                ["egress_distance"] = _postPlaceEgressDistance,
                ["clearance_radius"] = _postPlaceEgressClearanceRadius,
                ["distance_to_target_start"] = _postPlaceEgressStartDistanceToTarget,
                ["distance_to_target_end"] = distanceToTargetEnd,
                ["distance_from_deposited_box_start"] = _postPlaceEgressStartDistanceFromBox,
                ["distance_from_deposited_box_end"] = distanceFromBoxEnd,
                ["heading_error_start"] = _postPlaceEgressHeadingErrorStart,
                ["used_startup_alignment"] = _postPlaceEgressUsedStartupAlignment,
                ["success_criterion"] = _postPlaceEgressSuccessCriterion ?? string.Empty,
                ["timeout_seconds"] = _postPlaceRetreatTimeoutSeconds,
                ["short_distance_threshold"] = _postPlaceEgressShortDistanceThreshold,
                ["arrival_tolerance"] = _postPlaceEgressArrivalTolerance,
                ["max_heading_error"] = _postPlaceEgressMaxHeadingError,
                ["egress_duration"] = _postPlaceEgressStarted
                    ? Time.time - _postPlaceEgressStartedAt
                    : _postPlaceEgressLastDuration,
                ["egress_policy"] = _postPlaceEgressFailurePolicy.ToString(),
                ["egress_required"] = _enablePostPlaceRetreat && _postPlaceEgressPoint != default,
                ["obstacle_registration_pending"] = _pendingDepositedObstacleRegistration,
                ["obstacle_registered"] = string.Equals(phase, "paths_invalidated", StringComparison.OrdinalIgnoreCase) && !_pendingDepositedObstacleRegistration,
                ["obstacle_registration_timing"] = reason != null && reason.Contains("immediate")
                    ? "immediate_no_egress_required"
                    : "after_post_place_egress",
                ["paths_invalidated"] = string.Equals(phase, "paths_invalidated", StringComparison.OrdinalIgnoreCase),
                ["scene"] = SceneManager.GetActiveScene().name
            };
        }

        private float ResolveHeadingErrorDegrees(Vector3 origin, Vector3 target)
        {
            if (_robotAdapter == null || _robotAdapter.NavigationReference == null)
            {
                return 0f;
            }

            Vector3 forward = Flatten(_robotAdapter.NavigationReference.forward);
            Vector3 toTarget = Flatten(target - origin);
            if (forward.sqrMagnitude < 0.0001f || toTarget.sqrMagnitude < 0.0001f)
            {
                return 0f;
            }

            return Vector3.Angle(forward, toTarget);
        }

        private static float ResolvePlanarDistance(Vector3 a, Vector3 b)
        {
            return Vector3.Distance(Flatten(a), Flatten(b));
        }

        private static Vector3 Flatten(Vector3 value)
        {
            value.y = 0f;
            return value;
        }

        private static string TryGetPayloadString(Dictionary<string, object> payload, string key)
        {
            return payload != null && payload.TryGetValue(key, out object value) && value != null
                ? value.ToString()
                : string.Empty;
        }

        private static bool TryGetPayloadVector(Dictionary<string, object> payload, string key, out Vector3 value)
        {
            value = Vector3.zero;
            if (payload == null || !payload.TryGetValue(key, out object raw) || raw == null)
            {
                return false;
            }

            if (raw is Vector3 vector)
            {
                value = vector;
                return true;
            }

            return false;
        }

        private static bool TryGetPayloadBool(Dictionary<string, object> payload, string key)
        {
            if (payload == null || !payload.TryGetValue(key, out object raw) || raw == null)
            {
                return false;
            }

            if (raw is bool boolean)
            {
                return boolean;
            }

            return bool.TryParse(raw.ToString(), out bool parsed) && parsed;
        }

        private void ClearAssistanceRound(string reason)
        {
            _roundBoxes.Clear();
            _roundState.Initialize(_roundBoxes);
            _roundInitialized = false;
            _roundCompletedLogged = false;
            _assignedBoxId = string.Empty;
            _assignedTaskFailedOutOfRange = false;
            _assignedTaskFailedPlaceOutOfRange = false;
            _pendingPlaceRetry = false;
            _pendingPostPlaceEgress = false;
            _postPlaceEgressStarted = false;
            _postPlaceEgressSuccessCriterion = string.Empty;
            _postPlaceEgressBoxId = string.Empty;
            _postPlaceEgressSlotId = string.Empty;
            _slotAllocator.Clear();
            _rejectedApproachCandidatesByBox.Clear();
            _rejectedPlaceCandidatesByBox.Clear();
            _pickApproachRetryCounts.Clear();
            _placeApproachRetryCounts.Clear();
            _assignedApproachCandidateByBox.Clear();
            _assignedPlaceCandidateByBox.Clear();
            _lastPlaceCandidateTelemetryByContext.Clear();
            _lastDepositSlotTelemetryByBox.Clear();
            TiagoExperimentTelemetry.LogEvent(
                "assisted_round_state_cleared",
                new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["scene"] = SceneManager.GetActiveScene().name
                });
        }

        private void ReleaseDepositSlot(string boxId, string reason)
        {
            if (_slotAllocator.Release(boxId, out DepositZoneSlot releasedSlot))
            {
                Dictionary<string, object> payload = _slotAllocator.ToPayload(releasedSlot);
                payload["reason"] = reason ?? string.Empty;
                TiagoExperimentTelemetry.LogEvent("deposit_slot_released", payload);
            }
        }

        private bool IsRobotBusy()
        {
            if (_robotAdapter == null)
            {
                return true;
            }

            RobotMode? authoritativeMode = _robotAdapter.AuthoritativeRobotMode;
            if (!authoritativeMode.HasValue || authoritativeMode.Value != RobotMode.Idle)
            {
                return true;
            }

            if (_robotAdapter.Blackboard == null)
            {
                return true;
            }

            if (_robotAdapter.Blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out TaskStatus status) &&
                status == TaskStatus.InProgress)
            {
                return true;
            }

            return false;
        }

        private ExperimentConditionConfig ResolveCondition()
        {
            if (_experimentConditionProviderComponent is IExperimentConditionProvider provider &&
                provider.CurrentCondition != null)
            {
                return provider.CurrentCondition;
            }

            return new ExperimentConditionConfig(true, true, "default_robot_on_voice_on");
        }

        private Vector3 ResolveRobotPosition()
        {
            if (_robotAdapter != null && _robotAdapter.NavigationReference != null)
            {
                return _robotAdapter.NavigationReference.position;
            }

            return transform.position;
        }

        private static Vector3 ResolvePickupPosition(PerceivedObject candidate)
        {
            Transform approach = FindChildRecursive(candidate.Transform, "ApproachPoint");
            if (approach != null)
            {
                return approach.position;
            }

            Vector3 position = candidate.BoundsCenter;
            position.y = 0f;
            return position;
        }

        private string ResolvePlaceTargetId(string category)
        {
            return $"{_placeTargetPrefix}{(category ?? string.Empty).Trim()}";
        }

        private void LogCondition(string eventType)
        {
            ExperimentConditionConfig condition = ResolveCondition();
            Dictionary<string, object> payload = condition.ToPayload();
            payload["scene"] = SceneManager.GetActiveScene().name;
            payload["coordinator"] = name;
            payload["use_dynamic_pickup_approach"] = _useDynamicPickupApproach;
            payload["use_corner_pickup_approach_candidates"] = _useCornerPickupApproachCandidates;
            payload["bridge_use_corner_pickup_approach_candidates"] = _commandBridge != null && _commandBridge.UseCornerPickupApproachCandidates;
            payload["effective_use_corner_pickup_approach_candidates"] = ResolveEffectiveUseCornerPickupApproachCandidates();
            payload["pick_approach_standoff"] = _pickApproachStandoff;
            payload["max_expected_pick_distance"] = _maxExpectedPickDistance;
            payload["pick_distance_safety_margin"] = _pickDistanceSafetyMargin;
            payload["use_dynamic_place_navigation_candidates"] = _useDynamicPlaceNavigationCandidates;
            payload["use_corner_place_navigation_candidates"] = _useCornerPlaceNavigationCandidates;
            payload["place_navigation_standoff"] = _placeNavigationStandoff;
            payload["effective_place_navigation_standoff"] = _placeNavigationStandoff;
            payload["source_component"] = nameof(RobotAssistanceRoundCoordinator);
            payload["inspector_value_coordinator"] = _placeNavigationStandoff;
            payload["inspector_value_bridge"] = _commandBridge != null ? "bridge_has_own_place_navigation_standoff" : string.Empty;
            payload["max_expected_place_distance"] = _maxExpectedPlaceDistance;
            payload["max_relaxed_place_distance"] = _maxRelaxedPlaceDistance;
            payload["place_distance_safety_margin"] = _placeDistanceSafetyMargin;
            payload["place_candidate_reachability_margin"] = _placeCandidateReachabilityMargin;
            payload["place_candidate_arrival_tolerance"] = _placeCandidateArrivalTolerance;
            payload["use_dynamic_place_pose"] = _useDynamicPlacePose;
            payload["place_pose_inset"] = _placePoseInset;
            payload["min_place_pose_clearance"] = _minPlacePoseClearance;
            payload["use_deposit_zone_slot_allocator"] = _useDepositZoneSlotAllocator;
            payload["place_slot_inset"] = _placeSlotInset;
            payload["place_slot_clearance"] = _placeSlotClearance;
            payload["place_slot_grid"] = "2x2";
            payload["place_slot_occupancy_check_extents"] = _placeSlotOccupancyCheckExtents;
            payload["place_slot_stack_vertical_spacing"] = _placeSlotStackVerticalSpacing;
            payload["place_slot_default_box_height"] = _placeSlotDefaultBoxHeight;
            payload["keep_zone_access_margin"] = _keepZoneAccessMargin;
            payload["allow_non_slot_dynamic_place_fallback"] = _allowNonSlotDynamicPlaceFallback;
            payload["use_post_place_egress_validation"] = _usePostPlaceEgressValidation;
            payload["post_place_egress_distance"] = _postPlaceEgressDistance;
            payload["post_place_egress_sample_radius"] = _postPlaceEgressSampleRadius;
            payload["post_place_egress_clearance_radius"] = _postPlaceEgressClearanceRadius;
            payload["post_place_egress_safety_margin"] = _postPlaceEgressSafetyMargin;
            payload["require_post_place_egress_path"] = _requirePostPlaceEgressPath;
            payload["enable_post_place_retreat"] = _enablePostPlaceRetreat;
            payload["post_place_egress_mode"] = _postPlaceEgressMode.ToString();
            payload["post_place_egress_short_distance_threshold"] = _postPlaceEgressShortDistanceThreshold;
            payload["post_place_egress_arrival_tolerance"] = _postPlaceEgressArrivalTolerance;
            payload["post_place_egress_max_heading_error"] = _postPlaceEgressMaxHeadingError;
            payload["post_place_egress_timeout_seconds"] = _postPlaceRetreatTimeoutSeconds;
            payload["post_place_local_retreat_speed"] = _postPlaceLocalRetreatSpeed;
            payload["post_place_local_retreat_safe_distance"] = _postPlaceLocalRetreatSafeDistance;
            payload["post_place_local_retreat_min_distance_gain"] = _postPlaceLocalRetreatMinDistanceGain;
            payload["post_place_egress_failure_policy"] = _postPlaceEgressFailurePolicy.ToString();
            TiagoExperimentTelemetry.LogEvent(eventType, payload);
        }

        private void LogRoundInitialized()
        {
            var candidates = new List<Dictionary<string, object>>();
            foreach (BoxRoundItem box in _roundBoxes)
            {
                candidates.Add(new Dictionary<string, object>
                {
                    ["box_id"] = box.BoxId,
                    ["category"] = box.Category,
                    ["place_target_id"] = box.PlaceTargetId,
                    ["pickup_position"] = box.PickupPosition,
                    ["place_position"] = box.PlacePosition,
                    ["pickup_approach_candidate_id"] = box.PickupApproachCandidateId,
                    ["pickup_approach_side"] = box.PickupApproachSide,
                    ["pickup_approach_source"] = box.PickupApproachSource
                });
            }

            TiagoExperimentTelemetry.LogEvent(
                "round_state_initialized",
                new Dictionary<string, object>
                {
                    ["box_count"] = _roundBoxes.Count,
                    ["active_round_candidates"] = candidates,
                    ["scene"] = SceneManager.GetActiveScene().name
                });
            P40NavTraceDiagnostics.LogObstacleRegistrySnapshot(
                "round_started",
                "round_state_initialized",
                _robotAdapter,
                activeTargetId: _assignedBoxId);
        }

        private void LogRobotAssistanceGate(ExperimentConditionConfig condition, AssistanceGateResult gate)
        {
            Dictionary<string, object> payload = condition.ToPayload();
            payload["allowed"] = gate.Allowed;
            payload["reason"] = gate.Reason;
            payload["robot_busy"] = IsRobotBusy();
            payload["explicit_intent_processing"] = _explicitIntentProcessing;
            payload["has_assigned_box"] = _roundState.HasAssignedBox;
            payload["scene"] = SceneManager.GetActiveScene().name;
            TiagoExperimentTelemetry.LogEvent("robot_assistance_blocked_by_condition", payload);
        }

        private static void LogCandidateEvaluations(
            IReadOnlyList<AssistedBoxCandidateEvaluation> evaluations,
            AutonomousSelectionPolicy policy,
            IReadOnlyList<Dictionary<string, object>> additionalIgnoredCandidates)
        {
            var records = new List<Dictionary<string, object>>();
            int eligibleCount = 0;
            foreach (AssistedBoxCandidateEvaluation evaluation in evaluations)
            {
                if (evaluation.Eligible)
                {
                    eligibleCount++;
                }

                Dictionary<string, object> record = new Dictionary<string, object>
                {
                    ["box_id"] = evaluation.BoxId,
                    ["category"] = evaluation.Category,
                    ["place_target_id"] = evaluation.PlaceTargetId,
                    ["policy"] = evaluation.Policy.ToString(),
                    ["eligible"] = evaluation.Eligible,
                    ["ignored_reason"] = evaluation.IgnoredReason,
                    ["selection_cost"] = evaluation.SelectionCost,
                    ["selection_metric"] = "robot_to_pickup_cost",
                    ["selection_reason"] = evaluation.SelectionReason,
                    ["robot_to_pickup_cost"] = evaluation.RobotToPickupCost,
                    ["pickup_to_place_cost"] = evaluation.PickupToPlaceCost,
                    ["total_cost"] = evaluation.TotalCost,
                    ["benchmark_pick_place_total_cost"] = evaluation.TotalCost,
                    ["benchmark_pick_place_total_active"] = false,
                    ["used_euclidean_fallback"] = evaluation.UsedEuclideanFallback,
                    ["diagnostic_reason"] = evaluation.DiagnosticReason,
                    ["pickup_approach_candidate_id"] = evaluation.PickupApproachCandidateId,
                    ["pickup_approach_side"] = evaluation.PickupApproachSide,
                    ["pickup_approach_source"] = evaluation.PickupApproachSource,
                    ["place_navigation_candidate_id"] = evaluation.PlaceNavigationCandidateId,
                    ["place_navigation_side_or_corner"] = evaluation.PlaceNavigationSideOrCorner,
                    ["place_navigation_source"] = evaluation.PlaceNavigationSource
                };
                records.Add(record);
                TiagoExperimentTelemetry.LogEvent("autonomous_selection_candidate_scored", record);
            }

            if (additionalIgnoredCandidates != null)
            {
                foreach (Dictionary<string, object> ignored in additionalIgnoredCandidates)
                {
                    if (ignored == null)
                    {
                        continue;
                    }

                    records.Add(ignored);
                    TiagoExperimentTelemetry.LogEvent("autonomous_selection_candidate_scored", ignored);
                }
            }

            TiagoExperimentTelemetry.LogEvent(
                "autonomous_selection_policy_evaluated",
                new Dictionary<string, object>
                {
                    ["policy"] = policy.ToString(),
                    ["selection_metric"] = "robot_to_pickup_cost",
                    ["reason"] = "experimental_base_policy_lowest_local_pick_cost",
                    ["candidate_count"] = records.Count,
                    ["eligible_candidate_count"] = eligibleCount,
                    ["benchmark_pick_place_total_active"] = false,
                    ["benchmark_pick_place_total_scope"] = "offline_diagnostic_only",
                    ["candidates"] = records
                });

            TiagoExperimentTelemetry.LogEvent(
                "assisted_selection_candidates_evaluated",
                new Dictionary<string, object>
                {
                    ["policy"] = policy.ToString(),
                    ["selection_metric"] = "robot_to_pickup_cost",
                    ["candidate_count"] = records.Count,
                    ["eligible_candidate_count"] = eligibleCount,
                    ["benchmark_pick_place_total_active"] = false,
                    ["candidates"] = records
                });
        }

        private void LogP40AssistedSelectionDecision(
            BoxRoundItem selected,
            IReadOnlyList<AssistedBoxCandidateEvaluation> evaluations)
        {
            P40TraceMetadata lastVoice = P40TraceContext.LastVoiceCommand;
            var records = BuildP40CandidateEvaluationPayloads(evaluations);
            TiagoExperimentTelemetry.LogEvent(
                "p40_trace_assisted_selection_decision",
                new Dictionary<string, object>
                {
                    ["request_id"] = string.Empty,
                    ["producer"] = "assisted_navmesh_selection",
                    ["policy"] = ActiveAutonomousSelectionPolicy.ToString(),
                    ["selection_metric"] = "robot_to_pickup_cost",
                    ["benchmark_pick_place_total_active"] = false,
                    ["selected_box_id"] = selected != null ? selected.BoxId : string.Empty,
                    ["selected_place_target_id"] = selected != null ? selected.PlaceTargetId : string.Empty,
                    ["candidate_count"] = records.Count,
                    ["candidates"] = records,
                    ["last_voice_interaction_id"] = lastVoice?.VoiceInteractionId ?? string.Empty,
                    ["last_voice_target_id"] = lastVoice?.TargetAlias ?? string.Empty,
                    ["last_voice_decision"] = lastVoice?.LastDecision ?? string.Empty,
                    ["last_voice_rejection_reason"] = lastVoice?.LastReason ?? string.Empty,
                    ["diagnostic_explanation"] = "la orden vocal no estaba pendiente; el selector actuo por heuristica",
                    ["active_task_instance_id"] = _robotAdapter != null ? _robotAdapter.ActiveP40TaskInstanceId : string.Empty,
                    ["held_object_id"] = _robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty,
                    ["robot_busy"] = IsRobotBusy()
                });
        }

        private void LogP40RoundAssignmentSnapshot(string phase)
        {
            TiagoExperimentTelemetry.LogEvent(
                "p40_trace_round_assignment_snapshot",
                new Dictionary<string, object>
                {
                    ["phase"] = phase ?? string.Empty,
                    ["assigned_box_id"] = _assignedBoxId ?? string.Empty,
                    ["held_object_id"] = _robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty,
                    ["robot_busy"] = IsRobotBusy(),
                    ["active_task_instance_id"] = _robotAdapter != null ? _robotAdapter.ActiveP40TaskInstanceId : string.Empty,
                    ["boxes"] = BuildP40RoundBoxPayloads(),
                    ["box_count"] = _roundBoxes.Count
                });
        }

        private static List<Dictionary<string, object>> BuildP40CandidateEvaluationPayloads(
            IReadOnlyList<AssistedBoxCandidateEvaluation> evaluations)
        {
            var records = new List<Dictionary<string, object>>();
            if (evaluations == null)
            {
                return records;
            }

            foreach (AssistedBoxCandidateEvaluation evaluation in evaluations)
            {
                records.Add(new Dictionary<string, object>
                {
                    ["box_id"] = evaluation.BoxId,
                    ["category"] = evaluation.Category,
                    ["place_target_id"] = evaluation.PlaceTargetId,
                    ["policy"] = evaluation.Policy.ToString(),
                    ["eligible"] = evaluation.Eligible,
                    ["ignored_reason"] = evaluation.IgnoredReason,
                    ["selection_cost"] = evaluation.SelectionCost,
                    ["selection_metric"] = "robot_to_pickup_cost",
                    ["selection_reason"] = evaluation.SelectionReason,
                    ["robot_to_pickup_cost"] = evaluation.RobotToPickupCost,
                    ["pickup_to_place_cost"] = evaluation.PickupToPlaceCost,
                    ["total_cost"] = evaluation.TotalCost,
                    ["benchmark_pick_place_total_cost"] = evaluation.TotalCost,
                    ["benchmark_pick_place_total_active"] = false,
                    ["used_euclidean_fallback"] = evaluation.UsedEuclideanFallback,
                    ["diagnostic_reason"] = evaluation.DiagnosticReason,
                    ["pickup_approach_candidate_id"] = evaluation.PickupApproachCandidateId,
                    ["place_navigation_candidate_id"] = evaluation.PlaceNavigationCandidateId
                });
            }

            return records;
        }

        private List<Dictionary<string, object>> BuildP40RoundBoxPayloads()
        {
            var boxes = new List<Dictionary<string, object>>();
            string heldObjectId = _robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty;
            foreach (BoxRoundItem box in _roundBoxes)
            {
                if (box == null)
                {
                    continue;
                }

                BoxRoundStatus status = _roundState.GetStatus(box.BoxId);
                boxes.Add(new Dictionary<string, object>
                {
                    ["box_id"] = box.BoxId,
                    ["alias"] = box.BoxId,
                    ["category"] = box.Category,
                    ["is_assigned"] = status == BoxRoundStatus.Assigned || string.Equals(_assignedBoxId, box.BoxId, StringComparison.OrdinalIgnoreCase),
                    ["is_held"] = string.Equals(heldObjectId, box.BoxId, StringComparison.OrdinalIgnoreCase),
                    ["is_completed"] = status == BoxRoundStatus.Completed,
                    ["assigned_to_task_instance_id"] = string.Equals(_assignedBoxId, box.BoxId, StringComparison.OrdinalIgnoreCase) && _robotAdapter != null
                        ? _robotAdapter.ActiveP40TaskInstanceId
                        : string.Empty,
                    ["assigned_source"] = status == BoxRoundStatus.Assigned ? "assisted_navmesh_selection" : string.Empty
                });
            }

            return boxes;
        }

        private static Dictionary<string, object> BuildIgnoredSelectionCandidatePayload(
            BoxRoundItem box,
            string ignoredReason,
            string diagnosticReason)
        {
            return new Dictionary<string, object>
            {
                ["box_id"] = box != null ? box.BoxId : string.Empty,
                ["category"] = box != null ? box.Category : string.Empty,
                ["place_target_id"] = box != null ? box.PlaceTargetId : string.Empty,
                ["policy"] = ActiveAutonomousSelectionPolicy.ToString(),
                ["eligible"] = false,
                ["ignored_reason"] = ignoredReason ?? string.Empty,
                ["selection_cost"] = -1f,
                ["selection_metric"] = "robot_to_pickup_cost",
                ["selection_reason"] = string.Empty,
                ["robot_to_pickup_cost"] = -1f,
                ["pickup_to_place_cost"] = -1f,
                ["total_cost"] = -1f,
                ["benchmark_pick_place_total_cost"] = -1f,
                ["benchmark_pick_place_total_active"] = false,
                ["used_euclidean_fallback"] = false,
                ["diagnostic_reason"] = diagnosticReason ?? string.Empty,
                ["pickup_approach_candidate_id"] = box != null ? box.PickupApproachCandidateId : string.Empty,
                ["pickup_approach_side"] = box != null ? box.PickupApproachSide : string.Empty,
                ["pickup_approach_source"] = box != null ? box.PickupApproachSource : string.Empty,
                ["place_navigation_candidate_id"] = box != null ? box.PlaceNavigationCandidateId : string.Empty,
                ["place_navigation_side_or_corner"] = box != null ? box.PlaceNavigationSideOrCorner : string.Empty,
                ["place_navigation_source"] = box != null ? box.PlaceNavigationSource : string.Empty
            };
        }

        private static void LogAssistedBoxSelected(
            BoxRoundItem selected,
            IReadOnlyList<AssistedBoxCandidateEvaluation> evaluations,
            AutonomousSelectionPolicy policy)
        {
            AssistedBoxCandidateEvaluation selectedEvaluation = null;
            foreach (AssistedBoxCandidateEvaluation evaluation in evaluations)
            {
                if (string.Equals(evaluation.BoxId, selected.BoxId, StringComparison.OrdinalIgnoreCase))
                {
                    selectedEvaluation = evaluation;
                    break;
                }
            }

            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["box_id"] = selected.BoxId,
                ["category"] = selected.Category,
                ["place_target_id"] = selected.PlaceTargetId,
                ["policy"] = policy.ToString(),
                ["selection_metric"] = "robot_to_pickup_cost",
                ["selection_cost"] = selectedEvaluation != null ? selectedEvaluation.SelectionCost : 0f,
                ["selection_reason"] = selectedEvaluation != null ? selectedEvaluation.SelectionReason : "lowest_robot_to_pickup_cost",
                ["robot_to_pickup_cost"] = selectedEvaluation != null ? selectedEvaluation.RobotToPickupCost : 0f,
                ["pickup_to_place_cost"] = selectedEvaluation != null ? selectedEvaluation.PickupToPlaceCost : 0f,
                ["total_cost"] = selectedEvaluation != null ? selectedEvaluation.TotalCost : 0f,
                ["benchmark_pick_place_total_cost"] = selectedEvaluation != null ? selectedEvaluation.TotalCost : 0f,
                ["benchmark_pick_place_total_active"] = false,
                ["pickup_approach_candidate_id"] = selected.PickupApproachCandidateId,
                ["pickup_approach_side"] = selected.PickupApproachSide,
                ["pickup_approach_source"] = selected.PickupApproachSource,
                ["place_navigation_candidate_id"] = selected.PlaceNavigationCandidateId,
                ["place_navigation_side_or_corner"] = selected.PlaceNavigationSideOrCorner,
                ["place_navigation_source"] = selected.PlaceNavigationSource,
                ["used_euclidean_fallback"] = selectedEvaluation != null && selectedEvaluation.UsedEuclideanFallback
            };

            TiagoExperimentTelemetry.LogEvent("autonomous_selection_candidate_selected", payload);
            TiagoExperimentTelemetry.LogEvent("assisted_box_selected", payload);
        }

        private void LogAssistedBoxAssigned(BoxRoundItem selected)
        {
            TiagoExperimentTelemetry.LogEvent(
                "assisted_box_assigned",
                new Dictionary<string, object>
                {
                    ["box_id"] = selected.BoxId,
                    ["category"] = selected.Category,
                    ["place_target_id"] = selected.PlaceTargetId,
                    ["pickup_approach_candidate_id"] = selected.PickupApproachCandidateId,
                    ["pickup_approach_side"] = selected.PickupApproachSide,
                    ["pickup_approach_source"] = selected.PickupApproachSource,
                    ["place_navigation_candidate_id"] = selected.PlaceNavigationCandidateId,
                    ["place_navigation_side_or_corner"] = selected.PlaceNavigationSideOrCorner,
                    ["place_navigation_source"] = selected.PlaceNavigationSource,
                    ["pending_count_remaining"] = _roundState.PendingCount,
                    ["assigned_count"] = _roundState.AssignedCount,
                    ["scene"] = SceneManager.GetActiveScene().name
                });
        }

        private static void LogAssistedTaskSubmitted(BoxRoundItem selected, bool submitted)
        {
            TiagoExperimentTelemetry.LogEvent(
                "assisted_task_submitted",
                new Dictionary<string, object>
                {
                    ["box_id"] = selected.BoxId,
                    ["place_target_id"] = selected.PlaceTargetId,
                    ["pickup_approach_candidate_id"] = selected.PickupApproachCandidateId,
                    ["pickup_approach_side"] = selected.PickupApproachSide,
                    ["place_navigation_candidate_id"] = selected.PlaceNavigationCandidateId,
                    ["place_navigation_side_or_corner"] = selected.PlaceNavigationSideOrCorner,
                    ["bridge_invoked"] = submitted,
                    ["bridge_accepted"] = submitted
                });
        }

        private static void LogAssistedBoxSkipped(string boxId, string category, string placeTargetId, string reason)
        {
            TiagoExperimentTelemetry.LogEvent(
                "assisted_box_skipped",
                new Dictionary<string, object>
                {
                    ["box_id"] = boxId ?? string.Empty,
                    ["category"] = category ?? string.Empty,
                    ["place_target_id"] = placeTargetId ?? string.Empty,
                    ["reason"] = reason ?? string.Empty
                });
        }

        private void LogRoundCompletedIfNeeded()
        {
            if (_roundCompletedLogged || !_roundState.RoundCompleted)
            {
                return;
            }

            _roundCompletedLogged = true;
            ExperimentalRoundSnapshot snapshot = _roundLifecycle?.CurrentRound;
            bool roundManagerCompleted = snapshot != null && snapshot.RoundFinished;
            bool allBoxesCompleted = _roundBoxes.Count > 0 && _roundState.CompletedCount >= _roundBoxes.Count;
            string eventType = allBoxesCompleted || roundManagerCompleted
                ? "assisted_round_completed"
                : "assisted_round_incomplete";
            TiagoExperimentTelemetry.LogEvent(eventType, BuildRoundTerminalPayload(snapshot, allBoxesCompleted, roundManagerCompleted));
        }

        private bool IsSupervisoryStopLatched()
        {
            return _robotAdapter != null && _robotAdapter.VoiceStopLatched;
        }

        private void LogVoiceStopLatchedAutoSelectionBlockedNeutral(string reason)
        {
            Dictionary<string, object> payload = _robotAdapter != null
                ? _robotAdapter.BuildP44IVoiceStopResumeStatePayload(reason)
                : new Dictionary<string, object>();
            payload["reason"] = reason ?? string.Empty;
            payload["voice_stop_latched"] = _robotAdapter != null && _robotAdapter.VoiceStopLatched;
            payload["snapshot_available"] = _robotAdapter != null && _robotAdapter.HasStoppedTaskSnapshotForVoiceResume;
            payload["stopped_snapshot_valid"] = _robotAdapter != null && _robotAdapter.HasStructurallyValidStoppedTaskSnapshotForVoiceResume;
            payload["stop_latch_before"] = _robotAdapter != null && _robotAdapter.VoiceStopLatched;
            payload["stop_latch_after"] = _robotAdapter != null && _robotAdapter.VoiceStopLatched;
            payload["already_stopped"] = _robotAdapter != null && _robotAdapter.VoiceStopLatched;
            payload["request_source"] = "assisted_navmesh_selection";
            payload["rejection_reason"] = "voice_stop_latched_auto_task_blocked";
            payload["pending_count"] = _roundState.PendingCount;
            payload["assigned_count"] = _roundState.AssignedCount;
            payload["completed_count"] = _roundState.CompletedCount;
            payload["excluded_count"] = _roundState.ExcludedCount;
            payload["round_completed"] = _roundState.RoundCompleted;
            payload["assigned_box_id"] = _assignedBoxId;
            payload["robot_busy"] = IsRobotBusy();
            payload["robot_busy_reason"] = ResolveRobotBusyReason();
            payload["producer"] = "assisted_navmesh_selection";
            payload["box_state_mutation"] = false;
            TiagoExperimentTelemetry.LogEvent("voice_stop_latched_auto_selection_blocked_neutral", payload);
            TiagoExperimentTelemetry.LogEvent("voice_stop_latch_no_box_state_mutation", payload);
        }

        private Dictionary<string, object> BuildRoundTerminalPayload(ExperimentalRoundSnapshot snapshot, bool allBoxesCompleted, bool roundManagerCompleted)
        {
            int total = _roundBoxes.Count;
            return new Dictionary<string, object>
            {
                ["box_count"] = total,
                ["completed_count"] = _roundState.CompletedCount,
                ["failed_count"] = _roundState.ExcludedCount,
                ["excluded_count"] = _roundState.ExcludedCount,
                ["pending_count"] = _roundState.PendingCount,
                ["pending_count_remaining"] = _roundState.PendingCount,
                ["assigned_count"] = _roundState.AssignedCount,
                ["round_manager_deposited_count"] = snapshot != null ? snapshot.DepositedBoxes : -1,
                ["round_manager_total_boxes"] = snapshot != null ? snapshot.TotalBoxes : -1,
                ["all_boxes_completed_by_assistance_state"] = allBoxesCompleted,
                ["round_manager_completed"] = roundManagerCompleted,
                ["scene"] = SceneManager.GetActiveScene().name
            };
        }

        private void LogPickupApproachCandidatesEvaluated(BoxRoundItem box, IReadOnlyList<PickupApproachCandidate> candidates)
        {
            var records = new List<Dictionary<string, object>>();
            if (candidates != null)
            {
                foreach (PickupApproachCandidate candidate in candidates)
                {
                    records.Add(PickupApproachSelector.ToPayload(candidate));
                }
            }

            TiagoExperimentTelemetry.LogEvent(
                "pickup_approach_candidates_evaluated",
                new Dictionary<string, object>
                {
                    ["box_id"] = box != null ? box.BoxId : string.Empty,
                    ["candidate_count"] = candidates != null ? candidates.Count : 0,
                    ["candidates"] = records,
                    ["use_corner_pickup_approach_candidates"] = _useCornerPickupApproachCandidates,
                    ["bridge_use_corner_pickup_approach_candidates"] = _commandBridge != null && _commandBridge.UseCornerPickupApproachCandidates,
                    ["effective_use_corner_pickup_approach_candidates"] = ResolveEffectiveUseCornerPickupApproachCandidates(),
                    ["pick_approach_standoff"] = _pickApproachStandoff,
                    ["max_expected_pick_distance"] = _maxExpectedPickDistance,
                    ["pick_distance_safety_margin"] = _pickDistanceSafetyMargin
                });
        }

        private void LogPlaceNavigationCandidatesEvaluated(
            BoxRoundItem box,
            PickupApproachCandidate pickup,
            IReadOnlyList<PlaceNavigationCandidate> candidates)
        {
            var records = new List<Dictionary<string, object>>();
            if (candidates != null)
            {
                foreach (PlaceNavigationCandidate candidate in candidates)
                {
                    records.Add(PlaceNavigationCandidateSelector.ToPayload(candidate));
                }
            }

            string telemetryContext = (box != null ? box.BoxId : string.Empty) + "|" +
                (pickup != null ? pickup.CandidateId : "held_object_retry");
            string telemetrySignature = BuildTelemetrySignature(records);
            if (_lastPlaceCandidateTelemetryByContext.TryGetValue(telemetryContext, out string previousSignature) &&
                string.Equals(previousSignature, telemetrySignature, StringComparison.Ordinal))
            {
                return;
            }
            _lastPlaceCandidateTelemetryByContext[telemetryContext] = telemetrySignature;

            TryResolvePlaceTargetTransforms(box != null ? box.PlaceTargetId : string.Empty, out Transform placeTransform, out _);
            TiagoExperimentTelemetry.LogEvent(
                "place_navigation_candidates_evaluated",
                new Dictionary<string, object>
                {
                    ["box_id"] = box != null ? box.BoxId : string.Empty,
                    ["place_target_id"] = box != null ? box.PlaceTargetId : string.Empty,
                    ["place_transform_path"] = placeTransform != null ? GetTransformPath(placeTransform) : string.Empty,
                    ["place_transform_position"] = placeTransform != null ? placeTransform.position : Vector3.zero,
                    ["pickup_candidate_id"] = pickup != null ? pickup.CandidateId : string.Empty,
                    ["candidate_count"] = candidates != null ? candidates.Count : 0,
                    ["candidates"] = records,
                    ["use_corner_place_navigation_candidates"] = _useCornerPlaceNavigationCandidates,
                    ["place_navigation_standoff"] = _placeNavigationStandoff,
                    ["use_dynamic_place_pose"] = _useDynamicPlacePose,
                    ["place_pose_inset"] = _placePoseInset,
                    ["min_place_pose_clearance"] = _minPlacePoseClearance
                });
        }

        private void LogDepositZoneSlotsGenerated(BoxRoundItem box, IReadOnlyList<DepositZoneSlot> slots)
        {
            var records = new List<Dictionary<string, object>>();
            if (slots != null)
            {
                foreach (DepositZoneSlot slot in slots)
                {
                    records.Add(_slotAllocator.ToPayload(slot));
                }
            }

            string boxId = box != null ? box.BoxId : string.Empty;
            string telemetrySignature = BuildTelemetrySignature(records);
            if (_lastDepositSlotTelemetryByBox.TryGetValue(boxId, out string previousSignature) &&
                string.Equals(previousSignature, telemetrySignature, StringComparison.Ordinal))
            {
                return;
            }
            _lastDepositSlotTelemetryByBox[boxId] = telemetrySignature;

            TiagoExperimentTelemetry.LogEvent(
                "deposit_zone_slots_generated",
                new Dictionary<string, object>
                {
                    ["box_id"] = box != null ? box.BoxId : string.Empty,
                    ["place_target_id"] = box != null ? box.PlaceTargetId : string.Empty,
                    ["slot_count"] = slots != null ? slots.Count : 0,
                    ["slot_grid"] = "2x2",
                    ["slots"] = records,
                    ["place_slot_inset"] = _placeSlotInset,
                    ["place_slot_clearance"] = _placeSlotClearance,
                    ["place_slot_occupancy_check_extents"] = _placeSlotOccupancyCheckExtents,
                    ["keep_zone_access_margin"] = _keepZoneAccessMargin,
                    ["used_place_slot_allocator"] = _useDepositZoneSlotAllocator
                });
        }

        private static string BuildTelemetrySignature(List<Dictionary<string, object>> records)
        {
            var parts = new List<string>();
            foreach (Dictionary<string, object> record in records)
            {
                foreach (KeyValuePair<string, object> pair in record)
                {
                    parts.Add(pair.Key + "=" + FormatTelemetrySignatureValue(pair.Value));
                }
                parts.Add(";");
            }
            return string.Join("|", parts);
        }

        private static string FormatTelemetrySignatureValue(object value)
        {
            return value switch
            {
                null => string.Empty,
                Vector3 vector => string.Join(",",
                    vector.x.ToString("R", CultureInfo.InvariantCulture),
                    vector.y.ToString("R", CultureInfo.InvariantCulture),
                    vector.z.ToString("R", CultureInfo.InvariantCulture)),
                Quaternion quaternion => string.Join(",",
                    quaternion.x.ToString("R", CultureInfo.InvariantCulture),
                    quaternion.y.ToString("R", CultureInfo.InvariantCulture),
                    quaternion.z.ToString("R", CultureInfo.InvariantCulture),
                    quaternion.w.ToString("R", CultureInfo.InvariantCulture)),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            };
        }

        private void LogAssistedBoxFailedAfterRetries(string boxId, string reason)
        {
            TiagoExperimentTelemetry.LogEvent(
                "assisted_box_failed_after_retries",
                new Dictionary<string, object>
                {
                    ["box_id"] = boxId ?? string.Empty,
                    ["reason"] = reason ?? string.Empty,
                    ["retry_count"] = _pickApproachRetryCounts.TryGetValue(boxId ?? string.Empty, out int count) ? count : 0,
                    ["max_pick_approach_retries"] = _maxPickApproachRetries,
                    ["scene"] = SceneManager.GetActiveScene().name
                });
        }

        private void LogNoSelection(string reason)
        {
            TiagoExperimentTelemetry.LogEvent(
                "assisted_selection_not_started",
                new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["pending_count_remaining"] = _roundState.PendingCount,
                    ["assigned_count"] = _roundState.AssignedCount,
                    ["round_completed"] = _roundState.RoundCompleted,
                    ["robot_busy"] = IsRobotBusy(),
                    ["explicit_intent_processing"] = _explicitIntentProcessing,
                    ["scene"] = SceneManager.GetActiveScene().name
                });
        }

        private void LogSelectionBlockedDetailed(string reason)
        {
            _lastSelectionBlockReason = reason ?? string.Empty;
            Dictionary<string, object> payload = BuildAssistanceSnapshotPayload(reason);
            payload["reason"] = reason ?? string.Empty;
            payload["has_pending_candidates"] = _roundState.PendingCount > 0;
            payload["held_object_id"] = _robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty;
            payload["time_since_last_selection"] = float.IsNegativeInfinity(_lastSelectionTime) ? -1f : Time.time - _lastSelectionTime;
            payload["time_since_last_deposit"] = float.IsNegativeInfinity(_lastDepositTime) ? -1f : Time.time - _lastDepositTime;
            payload["reevaluation_scheduled"] = Time.time < _nextSelectionTime;
            payload["next_selection_time"] = _nextSelectionTime;
            TiagoExperimentTelemetry.LogEvent("assisted_selection_blocked_detailed", payload);
        }

        public void DebugDumpAssistanceState(string reason = "manual")
        {
            Dictionary<string, object> payload = BuildAssistanceSnapshotPayload(reason);
            Debug.Log($"[RobotAssistanceRoundCoordinator] assistance_debug_state_snapshot | pending={_roundState.PendingCount} assigned='{_assignedBoxId}' last_block='{_lastSelectionBlockReason}'", this);
            TiagoExperimentTelemetry.LogEvent("assistance_debug_state_snapshot", payload);
        }

        private Dictionary<string, object> BuildAssistanceSnapshotPayload(string reason)
        {
            ExperimentConditionConfig condition = ResolveCondition();
            ExperimentalRoundSnapshot snapshot = _roundLifecycle?.CurrentRound;
            Transform activeContainer = snapshot != null ? snapshot.ActiveRoundContainer : null;
            var boxes = new List<Dictionary<string, object>>();
            foreach (BoxRoundItem item in _roundBoxes)
            {
                TryFindBoxMetadata(item.BoxId, out Component metadata);
                boxes.Add(new Dictionary<string, object>
                {
                    ["box_id"] = item.BoxId,
                    ["game_object_name"] = metadata != null ? metadata.gameObject.name : string.Empty,
                    ["category"] = item.Category,
                    ["state"] = _roundState.GetStatus(item.BoxId).ToString(),
                    ["is_deposited"] = GetBool(metadata, "isDeposited"),
                    ["belongs_to_active_round"] = metadata == null || _roundLifecycle == null || _roundLifecycle.IsInActiveRound(metadata),
                    ["transform_path"] = metadata != null ? GetTransformPath(metadata.transform) : string.Empty
                });
            }

            bool robotBusy = IsRobotBusy();
            return new Dictionary<string, object>
            {
                ["reason"] = reason ?? string.Empty,
                ["robot_enabled"] = condition.RobotEnabled,
                ["voice_enabled"] = condition.VoiceEnabled,
                ["assistance_mode"] = condition.AssistanceMode.ToString(),
                ["condition_name"] = condition.ConditionName,
                ["round_lifecycle_assigned"] = _roundLifecycle != null,
                ["round_active"] = snapshot != null && snapshot.RoundActive,
                ["active_round_container_name"] = activeContainer != null ? activeContainer.name : string.Empty,
                ["active_round_container_instance_id"] = activeContainer != null ? activeContainer.GetInstanceID() : 0,
                ["assigned_box_id"] = _assignedBoxId,
                ["has_assigned_box"] = _roundState.HasAssignedBox,
                ["explicit_voice_intent_in_progress"] = _explicitIntentProcessing,
                ["robot_busy"] = robotBusy,
                ["robot_busy_reason"] = ResolveRobotBusyReason(),
                ["pending_count"] = _roundState.PendingCount,
                ["assigned_count"] = _roundState.AssignedCount,
                ["completed_count"] = _roundState.CompletedCount,
                ["excluded_count"] = Mathf.Max(0, _roundBoxes.Count - _roundState.PendingCount - _roundState.AssignedCount - _roundState.CompletedCount),
                ["known_boxes"] = boxes,
                ["last_block_reason"] = _lastSelectionBlockReason,
                ["time_since_last_selection"] = float.IsNegativeInfinity(_lastSelectionTime) ? -1f : Time.time - _lastSelectionTime,
                ["time_since_last_deposit"] = float.IsNegativeInfinity(_lastDepositTime) ? -1f : Time.time - _lastDepositTime,
                ["reevaluation_scheduled"] = Time.time < _nextSelectionTime,
                ["frame_count"] = Time.frameCount,
                ["time_since_start"] = Time.time,
                ["scene"] = SceneManager.GetActiveScene().name
            };
        }

        private string ResolveRobotBusyReason()
        {
            if (_robotAdapter == null)
            {
                return "robot_adapter_missing";
            }

            RobotMode? authoritativeMode = _robotAdapter.AuthoritativeRobotMode;
            if (!authoritativeMode.HasValue)
            {
                return "robot_mode_unavailable";
            }

            if (authoritativeMode.Value != RobotMode.Idle)
            {
                return $"mode_{authoritativeMode.Value}";
            }

            if (_robotAdapter.Blackboard == null)
            {
                return "robot_blackboard_missing";
            }

            if (_robotAdapter.Blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out TaskStatus status) &&
                status == TaskStatus.InProgress)
            {
                return "task_in_progress";
            }

            return "not_busy";
        }

        private static Dictionary<string, object> BuildIntentPayload(MultimodalTaskIntent intent, string source)
        {
            return new Dictionary<string, object>
            {
                ["source"] = source ?? string.Empty,
                ["task_flow"] = intent != null ? intent.TaskFlow.ToString() : string.Empty,
                ["object_selection_mode"] = intent != null ? intent.ObjectSelectionMode.ToString() : string.Empty,
                ["target"] = intent != null ? intent.TargetId : string.Empty,
                ["object_category"] = intent != null ? intent.ObjectCategory : string.Empty,
                ["destination"] = intent != null ? intent.PlaceTargetId : string.Empty
            };
        }

        private void LogAssistanceBlocked(string eventType, string reason)
        {
            TiagoExperimentTelemetry.LogEvent(
                eventType,
                new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["scene"] = SceneManager.GetActiveScene().name
                });
        }

        private void TryResolveReferences()
        {
            if (_commandBridge == null)
            {
                _commandBridge = FindFirstObjectByType<MultimodalAutonomyCommandBridge>();
            }

            if (_robotAdapter == null)
            {
                _robotAdapter = FindFirstObjectByType<AutonomousRobotAdapter>();
            }

            if (_perceptionService == null)
            {
                _perceptionService = FindFirstObjectByType<UnityScenePerceptionService>();
            }

            if (_placeTargetRegistry == null)
            {
                _placeTargetRegistry = FindFirstObjectByType<MultimodalPlaceTargetRegistry>();
            }

            TryResolveRoundLifecycle();
        }

        private void TryResolveRoundLifecycle()
        {
            if (_roundLifecycleComponent is IExperimentalRoundLifecycle configuredLifecycle)
            {
                if (!ReferenceEquals(_roundLifecycle, configuredLifecycle))
                {
                    UnsubscribeRoundLifecycle();
                }

                _roundLifecycle = configuredLifecycle;
                SubscribeRoundLifecycle();
                return;
            }

            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour is IExperimentalRoundLifecycle lifecycle)
                {
                    if (!ReferenceEquals(_roundLifecycle, lifecycle))
                    {
                        UnsubscribeRoundLifecycle();
                    }

                    _roundLifecycleComponent = behaviour;
                    _roundLifecycle = lifecycle;
                    SubscribeRoundLifecycle();
                    return;
                }
            }
        }

        private void SubscribeRoundLifecycle()
        {
            if (_roundLifecycle == null)
            {
                return;
            }

            if (_roundLifecycleSubscribed)
            {
                return;
            }

            _roundLifecycle.RoundStarted += HandleRoundStarted;
            _roundLifecycle.CorrectDepositRegistered += HandleCorrectDepositRegistered;
            _roundLifecycle.RoundCompleted += HandleRoundCompleted;
            _roundLifecycle.RoundReset += HandleRoundReset;
            _roundLifecycleSubscribed = true;
        }

        private void UnsubscribeRoundLifecycle()
        {
            if (_roundLifecycle == null)
            {
                return;
            }

            _roundLifecycle.RoundStarted -= HandleRoundStarted;
            _roundLifecycle.CorrectDepositRegistered -= HandleCorrectDepositRegistered;
            _roundLifecycle.RoundCompleted -= HandleRoundCompleted;
            _roundLifecycle.RoundReset -= HandleRoundReset;
            _roundLifecycleSubscribed = false;
        }

        private bool BelongsToActiveRound(PerceivedObject candidate)
        {
            if (_roundLifecycle == null)
            {
                return true;
            }

            return candidate != null && candidate.Transform != null && _roundLifecycle.IsInActiveRound(candidate.Transform);
        }

        private bool TryResolvePerceivedObject(string boxId, out PerceivedObject perceived)
        {
            perceived = null;
            if (_perceptionService == null || string.IsNullOrWhiteSpace(boxId))
            {
                return false;
            }

            var query = new PerceptionQuery(
                PerceptionSelectionStrategy.ExactObjectId,
                objectId: boxId,
                referencePosition: ResolveRobotPosition(),
                rejectDeposited: true,
                rejectGrabbed: true,
                rejectHeld: true);
            return _perceptionService.TrySelectTarget(query, out perceived, out _, out _) && perceived != null;
        }

        private BoxRoundItem FindRoundBox(string boxId)
        {
            foreach (BoxRoundItem item in _roundBoxes)
            {
                if (item != null && string.Equals(item.BoxId, boxId, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }

            return null;
        }

        private bool TryResolvePlaceTargetTransforms(string placeTargetId, out Transform placeTransform, out Transform navigationTransform)
        {
            placeTransform = null;
            navigationTransform = null;
            if (_placeTargetRegistry == null)
            {
                return false;
            }

            return _placeTargetRegistry.TryResolveTransforms(
                placeTargetId,
                out _,
                out placeTransform,
                out navigationTransform,
                out _);
        }

        private static Vector3 ToUnityVector(System.Numerics.Vector3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }

        private static Transform FindChildRecursive(Transform root, string childName)
        {
            if (root == null || string.IsNullOrWhiteSpace(childName))
            {
                return null;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.name == childName)
                {
                    return child;
                }

                Transform nested = FindChildRecursive(child, childName);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private static bool TryFindBoxMetadata(string boxId, out Component metadata)
        {
            metadata = null;
            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null ||
                    !string.Equals(behaviour.GetType().Name, "BoxMetadata", StringComparison.Ordinal) ||
                    !string.Equals(behaviour.gameObject.name, boxId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                metadata = behaviour;
                return true;
            }

            return false;
        }

        private string ResolveKnownBoxId(Component reportedBox)
        {
            if (reportedBox == null)
            {
                return string.Empty;
            }

            string reportedName = reportedBox.gameObject.name;
            foreach (BoxRoundItem item in _roundBoxes)
            {
                if (item == null)
                {
                    continue;
                }

                if (string.Equals(item.BoxId, reportedName, StringComparison.OrdinalIgnoreCase))
                {
                    return item.BoxId;
                }

                if (reportedBox.transform != null && reportedBox.transform.root != null &&
                    string.Equals(item.BoxId, reportedBox.transform.root.gameObject.name, StringComparison.OrdinalIgnoreCase))
                {
                    return item.BoxId;
                }
            }

            return reportedName;
        }

        private static bool GetBool(Component component, string memberName)
        {
            if (component == null || string.IsNullOrWhiteSpace(memberName))
            {
                return false;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo field = component.GetType().GetField(memberName, flags);
            return field != null && field.FieldType == typeof(bool) && (bool)field.GetValue(component);
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            string path = transform.name;
            Transform current = transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        private void OnValidate()
        {
            _selectionIntervalSeconds = Mathf.Max(0.1f, _selectionIntervalSeconds);
            _completionReevaluationDelaySeconds = Mathf.Max(0.05f, _completionReevaluationDelaySeconds);
            _navMeshSampleRadius = Mathf.Max(0.05f, _navMeshSampleRadius);
            _pickApproachStandoff = Mathf.Max(0.05f, _pickApproachStandoff);
            _maxExpectedPickDistance = Mathf.Max(0.05f, _maxExpectedPickDistance);
            _pickDistanceSafetyMargin = Mathf.Max(0f, _pickDistanceSafetyMargin);
            _maxPickApproachRetries = Mathf.Max(0, _maxPickApproachRetries);
            _placeNavigationStandoff = Mathf.Max(0.05f, _placeNavigationStandoff);
            _maxExpectedPlaceDistance = Mathf.Max(0.05f, _maxExpectedPlaceDistance);
            _maxRelaxedPlaceDistance = Mathf.Max(_maxExpectedPlaceDistance, _maxRelaxedPlaceDistance);
            _placeDistanceSafetyMargin = Mathf.Max(0f, _placeDistanceSafetyMargin);
            _placeCandidateReachabilityMargin = Mathf.Max(0f, _placeCandidateReachabilityMargin);
            _placeCandidateArrivalTolerance = Mathf.Max(0f, _placeCandidateArrivalTolerance);
            _placePoseInset = Mathf.Max(0f, _placePoseInset);
            _minPlacePoseClearance = Mathf.Max(0f, _minPlacePoseClearance);
            _placeSlotInset = Mathf.Max(0f, _placeSlotInset);
            _placeSlotClearance = Mathf.Max(0f, _placeSlotClearance);
            _placeSlotOccupancyCheckExtents = new Vector3(
                Mathf.Max(0.01f, _placeSlotOccupancyCheckExtents.x),
                Mathf.Max(0.01f, _placeSlotOccupancyCheckExtents.y),
                Mathf.Max(0.01f, _placeSlotOccupancyCheckExtents.z));
            _placeSlotStackVerticalSpacing = Mathf.Max(0f, _placeSlotStackVerticalSpacing);
            _placeSlotDefaultBoxHeight = Mathf.Max(0.01f, _placeSlotDefaultBoxHeight);
            _keepZoneAccessMargin = Mathf.Max(0f, _keepZoneAccessMargin);
            _postPlaceEgressDistance = Mathf.Max(0.05f, _postPlaceEgressDistance);
            _postPlaceEgressSampleRadius = Mathf.Max(0.05f, _postPlaceEgressSampleRadius);
            _postPlaceEgressClearanceRadius = Mathf.Max(0.01f, _postPlaceEgressClearanceRadius);
            _postPlaceEgressSafetyMargin = Mathf.Max(0f, _postPlaceEgressSafetyMargin);
            _postPlaceEgressShortDistanceThreshold = Mathf.Max(0.05f, _postPlaceEgressShortDistanceThreshold);
            _postPlaceEgressArrivalTolerance = Mathf.Max(0.01f, _postPlaceEgressArrivalTolerance);
            _postPlaceEgressMaxHeadingError = Mathf.Clamp(_postPlaceEgressMaxHeadingError, 0f, 180f);
            _postPlaceRetreatTimeoutSeconds = Mathf.Max(0.5f, _postPlaceRetreatTimeoutSeconds);
            _postPlaceLocalRetreatSpeed = Mathf.Max(0.01f, _postPlaceLocalRetreatSpeed);
            _postPlaceLocalRetreatSafeDistance = Mathf.Max(0.01f, _postPlaceLocalRetreatSafeDistance);
            _postPlaceLocalRetreatMinDistanceGain = Mathf.Max(0.01f, _postPlaceLocalRetreatMinDistanceGain);
            _postPlaceLocalRetreatProgressLogInterval = Mathf.Max(0.1f, _postPlaceLocalRetreatProgressLogInterval);
            _maxPlaceApproachRetries = Mathf.Max(0, _maxPlaceApproachRetries);
        }
    }
}
