using System;
using System.Collections.Generic;
using Autonomy.Core;
using Autonomy.Domain;
using Autonomy.Perception;
using UnityEngine;
using UnityEngine.AI;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class MultimodalAutonomyCommandBridge : MonoBehaviour
    {
        private const string LogPrefix = "[MultimodalAutonomyCommandBridge]";
        private const string ApproachSourceApproachChild = "ApproachChild";
        private const string ApproachSourceBoundsCenterFallback = "BoundsCenterFallback";
        private const string ApproachSourceObjectPosition = "ObjectPosition";

        [Header("References")]
        [SerializeField] private AutonomousRobotAdapter _robotAdapter;
        [SerializeField] private UnityScenePerceptionService _perceptionService;
        [SerializeField] private MultimodalPlaceTargetRegistry _placeTargetRegistry;
        [SerializeField] private MonoBehaviour _roundLifecycleComponent;
        [SerializeField] private Transform _referenceTransform;

        [Header("Pickup Navigation")]
        [SerializeField] private string _approachChildName = "ApproachPoint";
        [SerializeField] private Vector3 _pickupNavigationPositionOffset = Vector3.zero;
        [SerializeField] private bool _useBoundsCenterForPickupNavigation = true;
        [SerializeField] private bool _forcePickupGroundY = true;
        [SerializeField] private float _pickupNavMeshSampleRadius = 0.75f;
        [SerializeField] private bool _useDynamicPickupApproach = true;
        [SerializeField] private bool _useCornerPickupApproachCandidates = false;
        [SerializeField] private float _pickApproachStandoff = 0.45f;
        [SerializeField] private float _maxExpectedPickDistance = 1.20f;
        [SerializeField] private float _pickDistanceSafetyMargin = 0.05f;

        [Header("Place Navigation")]
        [SerializeField] private bool _useDynamicPlaceNavigationCandidates = true;
        [SerializeField] private bool _useCornerPlaceNavigationCandidates = false;
        [SerializeField] private float _placeNavigationStandoff = 0.65f;
        [SerializeField] private float _maxExpectedPlaceDistance = 1.25f;
        [SerializeField] private float _maxRelaxedPlaceDistance = 1.45f;
        [SerializeField] private float _placeDistanceSafetyMargin = 0.05f;
        [SerializeField] private float _placeReachabilityMargin = 0.20f;
        [SerializeField] private float _placeCandidateArrivalTolerance = 0.10f;
        [SerializeField] private bool _useDynamicPlacePose = true;
        [SerializeField] private float _placePoseInset = 0.20f;
        [SerializeField] private float _minPlacePoseClearance = 0.05f;

        [Header("Controlled Demo Target Preference")]
        [SerializeField] private bool _preferConfiguredTargetForCategory = true;
        [SerializeField] private string _preferredCategory = "A";
        [SerializeField] private string _preferredTargetId = "STEP19_TestBox_A_01";

        [Header("Simulated Multimodal Command")]
        [SerializeField] private bool _submitSimulatedIntentOnStart = false;
        [SerializeField] private KeyCode _submitSimulatedIntentKey = KeyCode.I;
        [Tooltip("Category selects an available object by BoxMetadata category/type, usually nearest to the reference. It does not use Simulated Target Id. TargetId resolves the exact scene object id.")]
        [SerializeField] private MultimodalObjectSelectionMode _simulatedSelectionMode = MultimodalObjectSelectionMode.Category;
        [Tooltip("Only used when Simulated Selection Mode is TargetId. Ignored when the active mode is Category.")]
        [SerializeField] private string _simulatedTargetId = "";
        [Tooltip("Only used when Simulated Selection Mode is Category. Category selects an available object of this type, not necessarily the object written in Simulated Target Id.")]
        [SerializeField] private string _simulatedObjectCategory = "A";
        [SerializeField] private string _simulatedPlaceTargetId = "ZoneA";
        [SerializeField] private bool _activateAutonomousModeOnSubmit = true;

        private IExperimentalRoundLifecycle _roundLifecycle;
        private RobotAssistanceRoundCoordinator _assistanceRoundCoordinator;
        private readonly Dictionary<string, PreferredPickupApproach> _preferredPickupApproaches = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PreferredPlaceNavigation> _preferredPlaceNavigations = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PreferredDynamicPlacePose> _preferredDynamicPlacePoses = new(StringComparer.OrdinalIgnoreCase);

        public bool UseCornerPickupApproachCandidates => _useCornerPickupApproachCandidates;
        public AutonomousRobotAdapter RobotAdapter => _robotAdapter;
        public string LastRejectionReason { get; private set; } = string.Empty;

        public void ResetForNewExperimentTrial(string reason)
        {
            _preferredPickupApproaches.Clear();
            _preferredPlaceNavigations.Clear();
            _preferredDynamicPlacePoses.Clear();
            TiagoExperimentTelemetry.LogEvent(
                "multimodal_bridge_runtime_state_cleared",
                new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty
                });
        }

        private void Awake()
        {
            TryResolveReferences();
        }

        private void Start()
        {
            if (_submitSimulatedIntentOnStart)
            {
                SubmitSimulatedIntent();
            }
        }

        private void Update()
        {
            if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
            {
                return;
            }

            if (RuntimeHotkeyInput.GetKeyDown(_submitSimulatedIntentKey))
            {
                SubmitSimulatedIntent();
            }
        }

        public void SubmitSimulatedIntent()
        {
            if (!TryValidateSimulatedConfiguration())
            {
                return;
            }

            MultimodalTaskIntent intent = _simulatedSelectionMode == MultimodalObjectSelectionMode.ExplicitTargetId
                ? MultimodalTaskIntent.PickAndPlaceByTargetId(_simulatedTargetId, _simulatedPlaceTargetId, "simulated_inspector")
                : MultimodalTaskIntent.PickAndPlaceByCategory(_simulatedObjectCategory, _simulatedPlaceTargetId, "simulated_inspector");

            SubmitIntent(intent);
        }

        public void SetPreferredPickupApproach(string objectId, Vector3 position, string candidateId, string side, string source)
        {
            if (string.IsNullOrWhiteSpace(objectId))
            {
                return;
            }

            _preferredPickupApproaches[objectId] = new PreferredPickupApproach(
                position,
                candidateId,
                side,
                source);
        }

        public void SetPreferredPlaceNavigation(string placeTargetId, Vector3 position, string candidateId, string sideOrCorner, string source)
        {
            if (string.IsNullOrWhiteSpace(placeTargetId))
            {
                return;
            }

            _preferredPlaceNavigations[placeTargetId] = new PreferredPlaceNavigation(
                position,
                candidateId,
                sideOrCorner,
                source);
        }

        public void SetPreferredDynamicPlacePose(
            string placeTargetId,
            Vector3 position,
            string candidateId,
            bool usedDynamicPlacePose,
            string candidateKind = "",
            string areaSource = "",
            float placePoseInset = 0f,
            Vector3 placePointFallbackPosition = default,
            string slotId = "",
            string slotQuadrant = "",
            int slotIndex = -1,
            int stackLevel = 0,
            bool usedPlaceSlotAllocator = false,
            Vector3 postPlaceEgressPoint = default)
        {
            if (string.IsNullOrWhiteSpace(placeTargetId))
            {
                return;
            }

            _preferredDynamicPlacePoses[placeTargetId] = new PreferredDynamicPlacePose(
                position,
                candidateId,
                candidateKind,
                areaSource,
                placePoseInset > 0f ? placePoseInset : _placePoseInset,
                placePointFallbackPosition,
                usedDynamicPlacePose,
                slotId,
                slotQuadrant,
                slotIndex,
                stackLevel,
                usedPlaceSlotAllocator,
                postPlaceEgressPoint);
        }

        private bool TryValidateSimulatedConfiguration()
        {
            if (_simulatedSelectionMode == MultimodalObjectSelectionMode.Category &&
                !string.IsNullOrWhiteSpace(_simulatedTargetId))
            {
                string reason = "simulated_target_id_ignored_in_category_mode";
                Debug.LogWarning(
                    $"{LogPrefix} {reason} | simulatedSelectionMode={_simulatedSelectionMode} simulatedTargetId='{_simulatedTargetId}' simulatedObjectCategory='{_simulatedObjectCategory}'",
                    this);
                LogSimulatedConfigWarning(reason, fatal: false);
                return true;
            }

            if (_simulatedSelectionMode == MultimodalObjectSelectionMode.ExplicitTargetId &&
                string.IsNullOrWhiteSpace(_simulatedTargetId))
            {
                string reason = "missing_simulated_target_id_for_target_id_mode";
                Debug.LogWarning(
                    $"{LogPrefix} {reason} | simulatedSelectionMode={_simulatedSelectionMode}. Simulated intent was not submitted.",
                    this);
                LogSimulatedConfigWarning(reason, fatal: true);
                return false;
            }

            return true;
        }

        public bool SubmitIntent(MultimodalTaskIntent intent)
        {
            LastRejectionReason = string.Empty;
            P40TraceMetadata trace = P40TraceContext.EnsureForIntent(
                intent,
                intent != null ? intent.Source : "unknown");
            LogP40VoiceLifecycle("bridge_submitted", intent, trace, "bridge_received_intent", "bridge_submit_intent_entered");
            LogIntentReceived(intent);

            if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
            {
                const string reason = ExperimentRuntimePauseCoordinator.AutonomyRequestRejectionReason;
                Dictionary<string, object> payload = BuildIntentPayload(intent);
                payload["request_source"] = P40TraceContext.NormalizeProducer(intent?.Source);
                payload["rejection_reason"] = reason;
                payload["is_experiment_paused"] = true;
                TiagoExperimentTelemetry.LogEvent("experiment_paused_bridge_request_rejected", payload);
                LogIntentRejected(intent, reason);
                return false;
            }

            if (!TryValidate(intent, out string validationFailure))
            {
                LogIntentRejected(intent, validationFailure);
                return false;
            }

            // Reject before target resolution or place-planning side effects. The adapter
            // remains the authoritative final gate and repeats this check before reset/start.
            if (_robotAdapter.TryGetSupervisoryStopRequestRejection(
                    intent,
                    out string stopRejectionReason,
                    out string requestSource))
            {
                Dictionary<string, object> blockedPayload = _robotAdapter.BuildP44IVoiceStopResumeStatePayload(stopRejectionReason);
                blockedPayload["stop_latch_before"] = true;
                blockedPayload["stop_latch_after"] = true;
                blockedPayload["already_stopped"] = true;
                blockedPayload["request_source"] = requestSource;
                blockedPayload["rejection_reason"] = stopRejectionReason;
                blockedPayload["requested_target_id"] = intent.TargetId ?? string.Empty;
                blockedPayload["requested_place_target_id"] = intent.PlaceTargetId ?? string.Empty;
                blockedPayload["task_status"] = blockedPayload.TryGetValue("controller_task_status", out object taskStatus)
                    ? taskStatus
                    : string.Empty;
                blockedPayload["robot_mode"] = blockedPayload.TryGetValue("controller_mode", out object robotMode)
                    ? robotMode
                    : string.Empty;
                TiagoExperimentTelemetry.LogEvent("supervisory_stop_latched_bridge_request_rejected", blockedPayload);
                LogIntentRejected(intent, stopRejectionReason);
                return false;
            }

            if (!TryResolvePickupTarget(intent, out TargetDescriptor pickupTarget, out PerceivedObject selected, out NavigationTargetResolution pickupNavigation, out string pickupFailure))
            {
                LogIntentRejected(intent, pickupFailure);
                return false;
            }

            if (!_placeTargetRegistry.TryResolve(intent.PlaceTargetId, out TargetDescriptor placeTarget, out Vector3 placePosition, out string placeFailure))
            {
                LogIntentRejected(intent, placeFailure);
                return false;
            }

            RobotAssistanceRoundCoordinator assistanceRoundCoordinator = ResolveAssistanceRoundCoordinator();
            bool preparedImmediateVoicePlacePlanning = false;
            string voiceSlotPlanningFailure = string.Empty;
            if (assistanceRoundCoordinator != null &&
                assistanceRoundCoordinator.TryPrepareImmediateVoicePlacePlanning(intent, out voiceSlotPlanningFailure))
            {
                preparedImmediateVoicePlacePlanning = true;
            }
            else if (!string.IsNullOrWhiteSpace(voiceSlotPlanningFailure))
            {
                LogIntentRejected(intent, voiceSlotPlanningFailure);
                return false;
            }

            PreferredPlaceNavigation preferredPlaceNavigation = default;
            bool hasPreferredPlaceNavigation = _preferredPlaceNavigations.TryGetValue(intent.PlaceTargetId, out preferredPlaceNavigation);
            PreferredDynamicPlacePose preferredDynamicPlacePose = default;
            bool hasPreferredDynamicPlacePose = _preferredDynamicPlacePoses.TryGetValue(intent.PlaceTargetId, out preferredDynamicPlacePose);
            if (hasPreferredPlaceNavigation)
            {
                _preferredPlaceNavigations.Remove(intent.PlaceTargetId);
                if (hasPreferredDynamicPlacePose)
                {
                    _preferredDynamicPlacePoses.Remove(intent.PlaceTargetId);
                }

                placeTarget = new TargetDescriptor(
                    placeTarget.Id,
                    new System.Numerics.Vector3(
                        preferredPlaceNavigation.Position.x,
                        preferredPlaceNavigation.Position.y,
                        preferredPlaceNavigation.Position.z));
            }
            else if (_useDynamicPlaceNavigationCandidates &&
                     _placeTargetRegistry.TryResolveTransforms(intent.PlaceTargetId, out _, out Transform placeTransform, out Transform navigationTransform, out _))
            {
                var placeSettings = new PlaceNavigationSettings(
                    _useDynamicPlaceNavigationCandidates,
                    _useCornerPlaceNavigationCandidates,
                    _placeNavigationStandoff,
                    _maxExpectedPlaceDistance,
                    _placeDistanceSafetyMargin,
                    _placeReachabilityMargin,
                    _placeCandidateArrivalTolerance,
                    _useDynamicPlacePose,
                    _placePoseInset,
                    _minPlacePoseClearance,
                    _pickupNavMeshSampleRadius,
                    _maxRelaxedPlaceDistance);
                IReadOnlyList<PlaceNavigationCandidate> placeCandidates = PlaceNavigationCandidateSelector.EvaluateCandidates(
                    intent.PlaceTargetId,
                    placeTransform,
                    navigationTransform,
                    ToUnityVector(placeTarget.Position),
                    ToUnityVector(pickupTarget.Position),
                    placeSettings);
                LogPlaceNavigationCandidatesEvaluated(intent.PlaceTargetId, pickupTarget.Id, placeCandidates);
                foreach (PlaceNavigationCandidate candidate in placeCandidates)
                {
                    if (candidate != null && !candidate.IsSelectable)
                    {
                        TiagoExperimentTelemetry.LogEvent("place_navigation_candidate_rejected", PlaceNavigationCandidateSelector.ToPayload(candidate));
                    }
                }

                if (PlaceNavigationCandidateSelector.TrySelectBest(placeCandidates, out PlaceNavigationCandidate selectedPlace))
                {
                    TiagoExperimentTelemetry.LogEvent("place_navigation_candidate_selected", PlaceNavigationCandidateSelector.ToPayload(selectedPlace));
                    hasPreferredPlaceNavigation = true;
                    preferredPlaceNavigation = new PreferredPlaceNavigation(
                        selectedPlace.Position,
                        selectedPlace.CandidateId,
                        selectedPlace.SideOrCorner,
                        selectedPlace.Source);
                    preferredDynamicPlacePose = new PreferredDynamicPlacePose(
                        selectedPlace.DynamicPlacePosePosition,
                        selectedPlace.CandidateId,
                        selectedPlace.Source != null && selectedPlace.Source.Contains("Corner") ? "corner" : "side",
                        selectedPlace.AreaSource,
                        selectedPlace.PlacePoseInset,
                        selectedPlace.PlacePointFallbackPosition,
                        selectedPlace.UsedDynamicPlacePose,
                        selectedPlace.SlotId,
                        selectedPlace.SlotQuadrant,
                        selectedPlace.SlotIndex,
                        selectedPlace.StackLevel,
                        selectedPlace.UsedPlaceSlotAllocator,
                        selectedPlace.PostPlaceEgressPoint);
                    hasPreferredDynamicPlacePose = true;
                    placeTarget = new TargetDescriptor(
                        placeTarget.Id,
                        new System.Numerics.Vector3(selectedPlace.Position.x, selectedPlace.Position.y, selectedPlace.Position.z));
                }
                else
                {
                    TiagoExperimentTelemetry.LogEvent(
                        "place_navigation_fallback_used",
                        new Dictionary<string, object>
                        {
                            ["place_target_id"] = intent.PlaceTargetId,
                            ["candidate_position"] = ToUnityVector(placeTarget.Position),
                            ["rejection_reason"] = "no_dynamic_place_candidate_selected",
                            ["source"] = "PlaceNavigationPointFallback"
                        });
                }
            }

            LogPickPlaceDebugContext(intent, selected, pickupTarget, placeTarget, placePosition);
            LogIntentResolved(intent, selected, pickupTarget, pickupNavigation, placeTarget, placePosition, hasPreferredPlaceNavigation, preferredPlaceNavigation);

            if (!_robotAdapter.TrySubmitAutonomyRequest(intent, pickupTarget, placeTarget, _activateAutonomousModeOnSubmit))
            {
                string adapterReason = string.IsNullOrWhiteSpace(_robotAdapter.LastAutonomyRequestRejectionReason)
                    ? "robot_adapter_rejected_request"
                    : _robotAdapter.LastAutonomyRequestRejectionReason;
                if (preparedImmediateVoicePlacePlanning)
                {
                    assistanceRoundCoordinator?.ClearPreparedImmediateVoicePlacePlanning(intent, adapterReason);
                }

                LogIntentRejected(intent, adapterReason);
                return false;
            }

            if (!_robotAdapter.LastAutonomyRequestWasDeferred)
            {
                if (preparedImmediateVoicePlacePlanning)
                {
                    assistanceRoundCoordinator?.CommitPreparedImmediateVoicePlacePlanning(intent, "voice_immediate_pick_and_place_accepted");
                }

                RegisterDynamicPlacePoseOverride(intent, pickupTarget, hasPreferredDynamicPlacePose, preferredDynamicPlacePose);
            }
            else if (preparedImmediateVoicePlacePlanning)
            {
                assistanceRoundCoordinator?.ClearPreparedImmediateVoicePlacePlanning(intent, "voice_request_deferred");
            }

            LogAutonomyRequestSubmitted(intent, pickupTarget, placeTarget);
            return true;
        }

        private static void LogP40VoiceLifecycle(
            string phase,
            MultimodalTaskIntent intent,
            P40TraceMetadata trace,
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
            payload["submitted_destination"] = intent != null ? intent.PlaceTargetId : string.Empty;
            payload["resolved_destination"] = !string.IsNullOrWhiteSpace(trace.ResolvedDestination)
                ? trace.ResolvedDestination
                : intent != null ? intent.PlaceTargetId : string.Empty;
            payload["destination"] = payload["resolved_destination"];
            payload["bridge_submitted"] = true;
            payload["decision"] = decision ?? string.Empty;
            payload["reason"] = reason ?? string.Empty;
            TiagoExperimentTelemetry.LogEvent("p40_trace_voice_command_lifecycle", payload);
        }

        private bool TryValidate(MultimodalTaskIntent intent, out string failureReason)
        {
            failureReason = string.Empty;

            if (intent == null)
            {
                failureReason = "intent_null";
                return false;
            }

            if (intent.TaskFlow != AutonomousTaskFlow.PickAndPlace)
            {
                failureReason = "unsupported_task_flow";
                return false;
            }

            if (_robotAdapter == null || _robotAdapter.TargetSeeder == null || _robotAdapter.PlaceTargetSeeder == null)
            {
                failureReason = "robot_adapter_not_ready";
                return false;
            }

            if (_robotAdapter.TaskFlow != AutonomousTaskFlow.PickAndPlace)
            {
                failureReason = "adapter_task_flow_not_pick_and_place";
                return false;
            }

            if (_perceptionService == null)
            {
                failureReason = "perception_service_missing";
                return false;
            }

            if (_placeTargetRegistry == null)
            {
                failureReason = "place_target_registry_missing";
                return false;
            }

            if (intent.ObjectSelectionMode == MultimodalObjectSelectionMode.ExplicitTargetId && string.IsNullOrWhiteSpace(intent.TargetId))
            {
                failureReason = "missing_target_id";
                return false;
            }

            if (intent.ObjectSelectionMode == MultimodalObjectSelectionMode.Category && string.IsNullOrWhiteSpace(intent.ObjectCategory))
            {
                failureReason = "missing_object_category";
                return false;
            }

            if (string.IsNullOrWhiteSpace(intent.PlaceTargetId))
            {
                failureReason = "missing_place_target_id";
                return false;
            }

            return true;
        }

        private bool TryResolvePickupTarget(
            MultimodalTaskIntent intent,
            out TargetDescriptor target,
            out PerceivedObject selected,
            out NavigationTargetResolution navigationTarget,
            out string failureReason)
        {
            target = null;
            selected = null;
            navigationTarget = default;
            failureReason = string.Empty;

            if (IsAssistedPlaceRetry(intent))
            {
                return TryResolveHeldRetryPickupTarget(intent, out target, out selected, out navigationTarget, out failureReason);
            }

            Vector3 referencePosition = ResolveReferencePosition();
            PerceptionSelectionStrategy strategy = intent.ObjectSelectionMode == MultimodalObjectSelectionMode.ExplicitTargetId
                ? PerceptionSelectionStrategy.ExactObjectId
                : PerceptionSelectionStrategy.NearestAvailable;
            string preferredObjectId = ResolvePreferredObjectId(intent);
            bool hasPreferredPickupApproach = !string.IsNullOrWhiteSpace(intent.TargetId) &&
                _preferredPickupApproaches.ContainsKey(intent.TargetId);
            var query = new PerceptionQuery(
                strategy,
                intent.TargetId,
                intent.ObjectCategory,
                referencePosition,
                rejectDeposited: true,
                rejectGrabbed: true,
                rejectHeld: !hasPreferredPickupApproach,
                preferredObjectId: preferredObjectId);

            if (!_perceptionService.TrySelectTarget(query, out selected, out IReadOnlyList<PerceivedObject> candidates, out failureReason))
            {
                if (string.Equals(failureReason, "requested_object_not_available", StringComparison.OrdinalIgnoreCase) &&
                    TryResolveCurrentAssignedHeldVoiceTarget(intent, out target, out selected, out navigationTarget, out failureReason))
                {
                    return true;
                }

                return false;
            }

            if (IsRoundActive() &&
                !hasPreferredPickupApproach &&
                !TryEnsureSelectedTargetBelongsToActiveRound(intent, candidates, ref selected, out failureReason))
            {
                return false;
            }

            navigationTarget = ResolvePickupNavigationTarget(selected);
            WarnIfNavigationTargetIsOffNavMesh(navigationTarget.Position, selected.ObjectId);
            target = PerceptionTargetSelector.ToTargetDescriptor(selected, navigationTarget.Position);
            return true;
        }

        private bool TryResolveCurrentAssignedHeldVoiceTarget(
            MultimodalTaskIntent intent,
            out TargetDescriptor target,
            out PerceivedObject selected,
            out NavigationTargetResolution navigationTarget,
            out string failureReason)
        {
            target = null;
            selected = null;
            navigationTarget = default;
            failureReason = string.Empty;

            if (!IsCurrentAssignedHeldVoiceTargetCandidate(intent))
            {
                failureReason = "requested_object_not_available";
                return false;
            }

            GameObject currentObject = _robotAdapter.HeldObject != null &&
                                       string.Equals(_robotAdapter.HeldObjectId, intent.TargetId, StringComparison.OrdinalIgnoreCase)
                ? _robotAdapter.HeldObject
                : ActiveRoundBoxRegistry.TryResolve(intent.TargetId, out GameObject resolvedBox)
                    ? resolvedBox
                    : null;
            if (currentObject == null)
            {
                failureReason = "requested_object_not_available";
                return false;
            }

            navigationTarget = ResolvePickupNavigationTargetForHeldRetry(currentObject, intent.TargetId);
            target = new TargetDescriptor(
                intent.TargetId,
                new System.Numerics.Vector3(navigationTarget.Position.x, navigationTarget.Position.y, navigationTarget.Position.z));
            Bounds bounds = ResolveBounds(currentObject);
            selected = new PerceivedObject(
                intent.TargetId,
                currentObject.name,
                ResolveBoxCategory(currentObject),
                currentObject.transform,
                currentObject.transform.position,
                bounds.center,
                bounds.extents,
                BoundsSource.TransformFallback,
                currentObject.activeInHierarchy,
                isManipulable: true,
                isDeposited: false,
                isGrabbed: true,
                isHeld: true,
                distanceToReference: 0f,
                reasonIfRejected: string.Empty);

            TiagoExperimentTelemetry.LogEvent(
                "voice_current_assigned_held_target_allowed_for_p40b_deferral",
                new Dictionary<string, object>
                {
                    ["requested_target_id"] = intent.TargetId,
                    ["requested_place_target_id"] = intent.PlaceTargetId,
                    ["held_object_id"] = _robotAdapter.HeldObjectId,
                    ["active_target_id"] = _robotAdapter.ActiveP40TargetId,
                    ["assigned_box_id"] = ResolveAssignedBoxId(out _, out _, out _),
                    ["reason"] = "current_assigned_or_held_voice_target"
                });
            return true;
        }

        private bool IsCurrentAssignedHeldVoiceTargetCandidate(MultimodalTaskIntent intent)
        {
            if (_robotAdapter == null || intent == null)
            {
                return false;
            }

            if (!string.Equals(P40TraceContext.NormalizeProducer(intent.Source), "voice_command", StringComparison.OrdinalIgnoreCase) ||
                intent.TaskFlow != AutonomousTaskFlow.PickAndPlace ||
                intent.ObjectSelectionMode != MultimodalObjectSelectionMode.ExplicitTargetId ||
                string.IsNullOrWhiteSpace(intent.TargetId) ||
                string.IsNullOrWhiteSpace(intent.PlaceTargetId) ||
                string.Equals(intent.PlaceTargetId, "SELF", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            IReadOnlyRobotBlackboard blackboard = _robotAdapter.Blackboard;
            bool taskInProgress = blackboard != null &&
                                  blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out TaskStatus status) &&
                                  status == TaskStatus.InProgress;
            if (!taskInProgress)
            {
                return false;
            }

            string assignedBoxId = ResolveAssignedBoxId(out _, out _, out _);
            string heldObjectId = _robotAdapter.HeldObjectId;
            return string.Equals(intent.TargetId, heldObjectId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(intent.TargetId, _robotAdapter.ActiveP40TargetId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(intent.TargetId, assignedBoxId, StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveAssignedBoxId(out bool hasAssignedBox, out int assignedCount, out string assignmentSource)
        {
            RobotAssistanceRoundCoordinator coordinator = FindFirstObjectByType<RobotAssistanceRoundCoordinator>();
            if (coordinator == null)
            {
                hasAssignedBox = false;
                assignedCount = 0;
                assignmentSource = "round_coordinator_not_found";
                return string.Empty;
            }

            string assignedBoxId = coordinator.AssignedBoxId;
            hasAssignedBox = coordinator.HasAssignedBox;
            assignedCount = hasAssignedBox ? 1 : 0;
            assignmentSource = "robot_assistance_round_coordinator";
            return assignedBoxId ?? string.Empty;
        }

        private bool TryResolveHeldRetryPickupTarget(
            MultimodalTaskIntent intent,
            out TargetDescriptor target,
            out PerceivedObject selected,
            out NavigationTargetResolution navigationTarget,
            out string failureReason)
        {
            target = null;
            selected = null;
            navigationTarget = default;
            failureReason = string.Empty;

            string heldObjectId = _robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty;
            if (_robotAdapter == null || string.IsNullOrWhiteSpace(heldObjectId))
            {
                failureReason = "retry_no_held_object";
                LogAssistedPlaceRetryRejected(intent, failureReason, "held_object");
                return false;
            }

            if (!string.Equals(intent.TargetId, heldObjectId, StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "retry_target_not_held_object";
                LogAssistedPlaceRetryRejected(intent, failureReason, "held_object");
                return false;
            }

            GameObject heldObject = _robotAdapter.HeldObject;
            if (heldObject == null)
            {
                failureReason = "retry_held_object_missing";
                LogAssistedPlaceRetryRejected(intent, failureReason, "held_object");
                return false;
            }

            navigationTarget = ResolvePickupNavigationTargetForHeldRetry(heldObject, intent.TargetId);
            target = new TargetDescriptor(
                intent.TargetId,
                new System.Numerics.Vector3(navigationTarget.Position.x, navigationTarget.Position.y, navigationTarget.Position.z));
            Bounds bounds = ResolveBounds(heldObject);
            selected = new PerceivedObject(
                intent.TargetId,
                heldObject.name,
                ResolveBoxCategory(heldObject),
                heldObject.transform,
                heldObject.transform.position,
                bounds.center,
                bounds.extents,
                BoundsSource.TransformFallback,
                heldObject.activeInHierarchy,
                isManipulable: true,
                isDeposited: false,
                isGrabbed: true,
                isHeld: true,
                distanceToReference: 0f,
                reasonIfRejected: string.Empty);

            TiagoExperimentTelemetry.LogEvent(
                "assisted_place_retry_held_target_resolved",
                new Dictionary<string, object>
                {
                    ["held_object_id"] = heldObjectId,
                    ["requested_target_id"] = intent.TargetId,
                    ["requested_place_target_id"] = intent.PlaceTargetId,
                    ["target_resolution_source"] = "held_object_current_state",
                    ["pickup_navigation_position"] = navigationTarget.Position
                });
            return true;
        }

        private NavigationTargetResolution ResolvePickupNavigationTargetForHeldRetry(GameObject heldObject, string targetId)
        {
            if (!string.IsNullOrWhiteSpace(targetId) &&
                _preferredPickupApproaches.TryGetValue(targetId, out PreferredPickupApproach preferred))
            {
                _preferredPickupApproaches.Remove(targetId);
                return new NavigationTargetResolution(
                    preferred.Position,
                    "AssistedPlaceRetryHeldObject",
                    string.Empty,
                    preferred.CandidateId,
                    preferred.Side,
                    preferred.Source);
            }

            Vector3 position = ResolveReferencePosition();
            return new NavigationTargetResolution(
                position,
                "AssistedPlaceRetryRobotCurrentPose",
                string.Empty,
                $"{targetId}:HeldObjectCurrentPose",
                "HeldObjectCurrentPose",
                "PlaceRetryHeldObject");
        }

        private bool TryEnsureSelectedTargetBelongsToActiveRound(
            MultimodalTaskIntent intent,
            IReadOnlyList<PerceivedObject> candidates,
            ref PerceivedObject selected,
            out string failureReason)
        {
            failureReason = string.Empty;
            if (selected != null && IsPerceivedObjectInActiveRound(selected))
            {
                return true;
            }

            if (intent.ObjectSelectionMode == MultimodalObjectSelectionMode.ExplicitTargetId)
            {
                failureReason = "explicit_target_outside_active_round";
                return false;
            }

            PerceivedObject best = null;
            float bestDistance = float.PositiveInfinity;
            if (candidates != null)
            {
                foreach (PerceivedObject candidate in candidates)
                {
                    if (candidate == null ||
                        !candidate.IsAccepted ||
                        !IsPerceivedObjectInActiveRound(candidate) ||
                        (!string.IsNullOrWhiteSpace(intent.ObjectCategory) &&
                         !string.Equals(candidate.Category, intent.ObjectCategory, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    float distance = float.IsNaN(candidate.DistanceToReference)
                        ? Vector3.Distance(candidate.WorldPosition, ResolveReferencePosition())
                        : candidate.DistanceToReference;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                    }
                }
            }

            if (best == null)
            {
                failureReason = "no_active_round_candidate_for_category";
                return false;
            }

            selected = best;
            TiagoExperimentTelemetry.LogEvent(
                "multimodal_round_candidate_substituted",
                new Dictionary<string, object>
                {
                    ["selected_object_id"] = selected.ObjectId,
                    ["selected_object_category"] = selected.Category,
                    ["reason"] = "initial_selection_outside_active_round"
                });
            return true;
        }

        private string ResolvePreferredObjectId(MultimodalTaskIntent intent)
        {
            if (!_preferConfiguredTargetForCategory ||
                intent == null ||
                intent.ObjectSelectionMode != MultimodalObjectSelectionMode.Category ||
                IsRoundActive() ||
                string.IsNullOrWhiteSpace(_preferredCategory) ||
                string.IsNullOrWhiteSpace(_preferredTargetId) ||
                !string.Equals(intent.ObjectCategory, _preferredCategory, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return _preferredTargetId;
        }

        private void LogPickPlaceDebugContext(
            MultimodalTaskIntent intent,
            PerceivedObject selected,
            TargetDescriptor pickupTarget,
            TargetDescriptor placeTarget,
            Vector3 placePosition)
        {
            bool roundActive = IsRoundActive();
            bool preferredTargetIgnored = _preferConfiguredTargetForCategory &&
                intent != null &&
                intent.ObjectSelectionMode == MultimodalObjectSelectionMode.Category &&
                roundActive;
            string placeRegistryEntryId = placeTarget != null ? placeTarget.Id : string.Empty;
            string placeTransformName = string.Empty;
            string placeNavigationTransformName = string.Empty;
            if (_placeTargetRegistry != null &&
                intent != null &&
                _placeTargetRegistry.TryResolveTransforms(intent.PlaceTargetId, out string semanticId, out Transform placeTransform, out Transform navigationTransform, out _))
            {
                placeRegistryEntryId = semanticId;
                placeTransformName = placeTransform != null ? placeTransform.name : string.Empty;
                placeNavigationTransformName = navigationTransform != null ? navigationTransform.name : string.Empty;
            }

            TiagoExperimentTelemetry.LogEvent(
                "bridge_pick_place_debug_context",
                new Dictionary<string, object>
                {
                    ["requested_target_id"] = intent != null ? intent.TargetId : string.Empty,
                    ["resolved_target_id"] = pickupTarget != null ? pickupTarget.Id : string.Empty,
                    ["requested_place_target_id"] = intent != null ? intent.PlaceTargetId : string.Empty,
                    ["resolved_place_target_id"] = placeTarget != null ? placeTarget.Id : string.Empty,
                    ["target_category"] = selected != null ? selected.Category : (intent != null ? intent.ObjectCategory : string.Empty),
                    ["place_registry_entry_id"] = placeRegistryEntryId,
                    ["place_transform_name"] = placeTransformName,
                    ["place_navigation_transform_name"] = placeNavigationTransformName,
                    ["place_position"] = placePosition,
                    ["intent_source"] = intent != null ? intent.Source : string.Empty,
                    ["active_round_filter_applied"] = roundActive,
                    ["preferred_target_id_ignored"] = preferredTargetIgnored,
                    ["preferred_target_id"] = _preferredTargetId ?? string.Empty,
                    ["substituted_outside_round_candidate"] = intent != null &&
                        intent.ObjectSelectionMode == MultimodalObjectSelectionMode.Category &&
                        selected != null &&
                        !string.IsNullOrWhiteSpace(intent.ObjectCategory),
                    ["frame_count"] = Time.frameCount,
                    ["time_since_start"] = Time.time
                });
        }

        private bool IsRoundActive()
        {
            TryResolveRoundLifecycle();
            return _roundLifecycle != null && _roundLifecycle.CurrentRound.RoundActive;
        }

        private bool IsPerceivedObjectInActiveRound(PerceivedObject perceivedObject)
        {
            TryResolveRoundLifecycle();
            if (_roundLifecycle == null)
            {
                return true;
            }

            return perceivedObject != null &&
                   perceivedObject.Transform != null &&
                   _roundLifecycle.IsInActiveRound(perceivedObject.Transform);
        }

        private NavigationTargetResolution ResolvePickupNavigationTarget(PerceivedObject selected)
        {
            if (selected != null &&
                _preferredPickupApproaches.TryGetValue(selected.ObjectId, out PreferredPickupApproach preferred))
            {
                _preferredPickupApproaches.Remove(selected.ObjectId);
                return new NavigationTargetResolution(
                    preferred.Position,
                    "AssistedDynamicApproach",
                    string.Empty,
                    preferred.CandidateId,
                    preferred.Side,
                    preferred.Source);
            }

            if (_useDynamicPickupApproach && selected != null)
            {
                var settings = new PickupApproachSettings(
                    _useDynamicPickupApproach,
                    _useCornerPickupApproachCandidates,
                    _pickApproachStandoff,
                    _maxExpectedPickDistance,
                    _pickDistanceSafetyMargin,
                    _pickupNavMeshSampleRadius);
                IReadOnlyList<PickupApproachCandidate> candidates = PickupApproachSelector.EvaluateCandidates(
                    selected,
                    ResolveReferencePosition(),
                    ResolveReferencePosition(),
                    settings);
                if (PickupApproachSelector.TrySelectBest(candidates, out PickupApproachCandidate dynamicCandidate))
                {
                    TiagoExperimentTelemetry.LogEvent("pickup_approach_candidate_selected", PickupApproachSelector.ToPayload(dynamicCandidate));
                    return new NavigationTargetResolution(
                        dynamicCandidate.Position,
                        "DynamicBoundsSide",
                        string.Empty,
                        dynamicCandidate.CandidateId,
                        dynamicCandidate.Side,
                        dynamicCandidate.Source);
                }
            }

            Transform approach = FindChildRecursive(selected.Transform, _approachChildName);
            Vector3 position;
            string source;

            if (approach != null)
            {
                position = approach.position;
                source = ApproachSourceApproachChild;
            }
            else if (_useBoundsCenterForPickupNavigation)
            {
                position = selected.BoundsCenter;
                source = ApproachSourceBoundsCenterFallback;
            }
            else
            {
                position = selected.WorldPosition;
                source = ApproachSourceObjectPosition;
            }

            position += _pickupNavigationPositionOffset;
            if (_forcePickupGroundY)
            {
                position.y = 0f;
            }

            return new NavigationTargetResolution(position, source, approach != null ? approach.name : string.Empty);
        }

        private Vector3 ResolveReferencePosition()
        {
            if (_referenceTransform != null)
            {
                return _referenceTransform.position;
            }

            if (_robotAdapter != null)
            {
                return _robotAdapter.NavigationReference.position;
            }

            return transform.position;
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

        private void WarnIfNavigationTargetIsOffNavMesh(Vector3 unityPosition, string targetId)
        {
            float sampleRadius = Mathf.Max(0.05f, _pickupNavMeshSampleRadius);
            if (NavMesh.SamplePosition(unityPosition, out _, sampleRadius, NavMesh.AllAreas))
            {
                return;
            }

            Debug.LogWarning(
                $"{LogPrefix} pickup_target_not_on_navmesh | id={targetId} position={FormatVector(unityPosition)} sampleRadius={sampleRadius:F2}",
                this);
        }

        private void LogIntentReceived(MultimodalTaskIntent intent)
        {
            TiagoExperimentTelemetry.LogEvent("multimodal_intent_received", BuildIntentPayload(intent));
        }

        private void LogIntentResolved(
            MultimodalTaskIntent intent,
            PerceivedObject selected,
            TargetDescriptor pickupTarget,
            NavigationTargetResolution pickupNavigation,
            TargetDescriptor placeTarget,
            Vector3 placePosition,
            bool hasPreferredPlaceNavigation,
            PreferredPlaceNavigation preferredPlaceNavigation)
        {
            Dictionary<string, object> payload = BuildIntentPayload(intent);
            payload["selected_object_id"] = selected.ObjectId;
            payload["selected_object_category"] = selected.Category;
            payload["pickup_target_id"] = pickupTarget.Id;
            payload["pickup_navigation_position"] = pickupTarget.Position;
            payload["pickup_navigation_source"] = pickupNavigation.Source;
            payload["pickup_approach_child_name"] = pickupNavigation.ApproachChildName;
            payload["pickup_approach_candidate_id"] = pickupNavigation.CandidateId;
            payload["pickup_approach_side"] = pickupNavigation.Side;
            payload["pickup_approach_source_detail"] = pickupNavigation.SourceDetail;
            payload["place_target_descriptor_id"] = placeTarget.Id;
            payload["place_navigation_position"] = placeTarget.Position;
            payload["place_position"] = placePosition;
            payload["place_navigation_candidate_id"] = hasPreferredPlaceNavigation ? preferredPlaceNavigation.CandidateId : string.Empty;
            payload["place_navigation_side_or_corner"] = hasPreferredPlaceNavigation ? preferredPlaceNavigation.SideOrCorner : string.Empty;
            payload["place_navigation_source"] = hasPreferredPlaceNavigation ? preferredPlaceNavigation.Source : string.Empty;
            payload["place_transform_preserved"] = true;
            TiagoExperimentTelemetry.LogEvent("multimodal_intent_resolved", payload);
        }

        private void LogIntentRejected(MultimodalTaskIntent intent, string reason)
        {
            LastRejectionReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
            Dictionary<string, object> payload = BuildIntentPayload(intent);
            payload["reason"] = reason ?? string.Empty;
            TiagoExperimentTelemetry.LogEvent("multimodal_intent_rejected", payload);
            Debug.LogWarning($"{LogPrefix} Intent rejected | reason={reason}", this);
        }

        private void LogAutonomyRequestSubmitted(
            MultimodalTaskIntent intent,
            TargetDescriptor pickupTarget,
            TargetDescriptor placeTarget)
        {
            Dictionary<string, object> payload = BuildIntentPayload(intent);
            payload["pickup_target_id"] = pickupTarget.Id;
            payload["pickup_navigation_position"] = pickupTarget.Position;
            payload["place_target_descriptor_id"] = placeTarget.Id;
            payload["place_navigation_position"] = placeTarget.Position;
            payload["activate_autonomous_mode"] = _activateAutonomousModeOnSubmit;
            TiagoExperimentTelemetry.LogEvent("autonomy_request_submitted", payload);
        }

        private static void LogPlaceNavigationCandidatesEvaluated(
            string placeTargetId,
            string pickupTargetId,
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

            TiagoExperimentTelemetry.LogEvent(
                "place_navigation_candidates_evaluated",
                new Dictionary<string, object>
                {
                    ["place_target_id"] = placeTargetId ?? string.Empty,
                    ["pickup_target_id"] = pickupTargetId ?? string.Empty,
                    ["candidate_count"] = candidates != null ? candidates.Count : 0,
                    ["candidates"] = records
                });
        }

        private void LogSimulatedConfigWarning(string reason, bool fatal)
        {
            TiagoExperimentTelemetry.LogEvent(
                "multimodal_simulated_config_warning",
                new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["fatal"] = fatal,
                    ["simulated_selection_mode"] = _simulatedSelectionMode.ToString(),
                    ["simulated_target_id"] = _simulatedTargetId ?? string.Empty,
                    ["simulated_object_category"] = _simulatedObjectCategory ?? string.Empty,
                    ["simulated_place_target_id"] = _simulatedPlaceTargetId ?? string.Empty
                });
        }

        private static Dictionary<string, object> BuildIntentPayload(MultimodalTaskIntent intent)
        {
            return new Dictionary<string, object>
            {
                ["task_flow"] = intent != null ? intent.TaskFlow.ToString() : string.Empty,
                ["object_selection_mode"] = intent != null ? intent.ObjectSelectionMode.ToString() : string.Empty,
                ["target_id"] = intent != null ? intent.TargetId : string.Empty,
                ["requested_target_id"] = intent != null ? intent.TargetId : string.Empty,
                ["requested_object_category"] = intent != null ? intent.ObjectCategory : string.Empty,
                ["requested_place_target_id"] = intent != null ? intent.PlaceTargetId : string.Empty,
                ["intent_source"] = intent != null ? intent.Source : string.Empty
            };
        }

        private bool IsAssistedPlaceRetry(MultimodalTaskIntent intent)
        {
            return intent != null &&
                   string.Equals(intent.Source, "assisted_place_retry", StringComparison.OrdinalIgnoreCase);
        }

        private void LogAssistedPlaceRetryRejected(MultimodalTaskIntent intent, string reason, string targetResolutionSource)
        {
            TiagoExperimentTelemetry.LogEvent(
                "assisted_place_retry_rejected_by_bridge",
                new Dictionary<string, object>
                {
                    ["held_object_id"] = _robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty,
                    ["requested_target_id"] = intent != null ? intent.TargetId : string.Empty,
                    ["requested_place_target_id"] = intent != null ? intent.PlaceTargetId : string.Empty,
                    ["is_retry_intent"] = IsAssistedPlaceRetry(intent),
                    ["target_resolution_source"] = targetResolutionSource ?? string.Empty,
                    ["rejection_reason"] = reason ?? string.Empty
                });
        }

        private static Bounds ResolveBounds(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return new Bounds(Vector3.zero, Vector3.one * 0.1f);
            }

            Collider collider = gameObject.GetComponentInChildren<Collider>();
            if (collider != null)
            {
                return collider.bounds;
            }

            Renderer renderer = gameObject.GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                return renderer.bounds;
            }

            return new Bounds(gameObject.transform.position, Vector3.one * 0.1f);
        }

        private static string ResolveBoxCategory(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return string.Empty;
            }

            Component[] components = gameObject.GetComponents<Component>();
            foreach (Component component in components)
            {
                if (component == null || !string.Equals(component.GetType().Name, "BoxMetadata", StringComparison.Ordinal))
                {
                    continue;
                }

                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                                                             System.Reflection.BindingFlags.Public |
                                                             System.Reflection.BindingFlags.NonPublic;
                System.Reflection.FieldInfo field = component.GetType().GetField("boxType", flags);
                object value = field != null ? field.GetValue(component) : null;
                return value != null ? value.ToString() : string.Empty;
            }

            return string.Empty;
        }

        private void TryResolveReferences()
        {
            if (_robotAdapter == null)
            {
                _robotAdapter = GetComponent<AutonomousRobotAdapter>();
            }

            if (_robotAdapter == null)
            {
                _robotAdapter = GetComponentInParent<AutonomousRobotAdapter>();
            }

            if (_perceptionService == null)
            {
                _perceptionService = GetComponent<UnityScenePerceptionService>();
            }

            if (_perceptionService == null)
            {
                _perceptionService = GetComponentInParent<UnityScenePerceptionService>();
            }

            if (_placeTargetRegistry == null)
            {
                _placeTargetRegistry = GetComponent<MultimodalPlaceTargetRegistry>();
            }

            if (_placeTargetRegistry == null)
            {
                _placeTargetRegistry = GetComponentInParent<MultimodalPlaceTargetRegistry>();
            }

            if (_referenceTransform == null && _robotAdapter != null)
            {
                _referenceTransform = _robotAdapter.NavigationReference;
            }

            if (_assistanceRoundCoordinator == null)
            {
                _assistanceRoundCoordinator = GetComponent<RobotAssistanceRoundCoordinator>();
            }

            if (_assistanceRoundCoordinator == null)
            {
                _assistanceRoundCoordinator = GetComponentInParent<RobotAssistanceRoundCoordinator>();
            }

            TryResolveRoundLifecycle();
        }

        private RobotAssistanceRoundCoordinator ResolveAssistanceRoundCoordinator()
        {
            if (_assistanceRoundCoordinator == null)
            {
                _assistanceRoundCoordinator = GetComponent<RobotAssistanceRoundCoordinator>();
            }

            if (_assistanceRoundCoordinator == null)
            {
                _assistanceRoundCoordinator = GetComponentInParent<RobotAssistanceRoundCoordinator>();
            }

            if (_assistanceRoundCoordinator == null)
            {
                _assistanceRoundCoordinator = FindFirstObjectByType<RobotAssistanceRoundCoordinator>();
            }

            return _assistanceRoundCoordinator;
        }

        private void TryResolveRoundLifecycle()
        {
            if (_roundLifecycleComponent is IExperimentalRoundLifecycle configuredLifecycle)
            {
                _roundLifecycle = configuredLifecycle;
                return;
            }

            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour is IExperimentalRoundLifecycle lifecycle)
                {
                    _roundLifecycleComponent = behaviour;
                    _roundLifecycle = lifecycle;
                    return;
                }
            }
        }

        private void Reset()
        {
            TryResolveReferences();
        }

        private void OnValidate()
        {
            if (_pickupNavMeshSampleRadius < 0.05f)
            {
                _pickupNavMeshSampleRadius = 0.05f;
            }

            _pickApproachStandoff = Mathf.Max(0.05f, _pickApproachStandoff);
            _maxExpectedPickDistance = Mathf.Max(0.05f, _maxExpectedPickDistance);
            _pickDistanceSafetyMargin = Mathf.Max(0f, _pickDistanceSafetyMargin);
            _placeNavigationStandoff = Mathf.Max(0.05f, _placeNavigationStandoff);
            _maxExpectedPlaceDistance = Mathf.Max(0.05f, _maxExpectedPlaceDistance);
            _placeDistanceSafetyMargin = Mathf.Max(0f, _placeDistanceSafetyMargin);
            _placeReachabilityMargin = Mathf.Max(0f, _placeReachabilityMargin);
            _placeCandidateArrivalTolerance = Mathf.Max(0f, _placeCandidateArrivalTolerance);
            _placePoseInset = Mathf.Max(0f, _placePoseInset);
            _minPlacePoseClearance = Mathf.Max(0f, _minPlacePoseClearance);
        }

        private void RegisterDynamicPlacePoseOverride(
            MultimodalTaskIntent intent,
            TargetDescriptor pickupTarget,
            bool hasPreferredDynamicPlacePose,
            PreferredDynamicPlacePose preferredDynamicPlacePose)
        {
            if (intent == null || pickupTarget == null || !hasPreferredDynamicPlacePose || !preferredDynamicPlacePose.UsedDynamicPlacePose)
            {
                return;
            }

            DynamicPlacePoseOverrideRegistry.Set(
                intent.PlaceTargetId,
                pickupTarget.Id,
                new DynamicPlacePoseOverride(
                    preferredDynamicPlacePose.Position,
                    preferredDynamicPlacePose.CandidateId,
                    preferredDynamicPlacePose.CandidateKind,
                    preferredDynamicPlacePose.AreaSource,
                    preferredDynamicPlacePose.PlacePoseInset,
                    preferredDynamicPlacePose.PlacePointFallbackPosition,
                    preferredDynamicPlacePose.UsedDynamicPlacePose,
                    preferredDynamicPlacePose.SlotId,
                    preferredDynamicPlacePose.SlotQuadrant,
                    preferredDynamicPlacePose.SlotIndex,
                    preferredDynamicPlacePose.StackLevel,
                    preferredDynamicPlacePose.UsedPlaceSlotAllocator,
                    preferredDynamicPlacePose.PostPlaceEgressPoint));

            TiagoExperimentTelemetry.LogEvent(
                "dynamic_place_pose_override_registered",
                new Dictionary<string, object>
                {
                    ["place_target_id"] = intent.PlaceTargetId,
                    ["object_id"] = pickupTarget.Id,
                    ["dynamic_place_pose_position"] = preferredDynamicPlacePose.Position,
                    ["place_point_fallback_position"] = preferredDynamicPlacePose.PlacePointFallbackPosition,
                    ["candidate_id"] = preferredDynamicPlacePose.CandidateId,
                    ["candidate_kind"] = preferredDynamicPlacePose.CandidateKind,
                    ["place_area_source"] = preferredDynamicPlacePose.AreaSource,
                    ["place_pose_inset"] = preferredDynamicPlacePose.PlacePoseInset,
                    ["used_dynamic_place_pose"] = preferredDynamicPlacePose.UsedDynamicPlacePose,
                    ["slot_id"] = preferredDynamicPlacePose.SlotId,
                    ["quadrant"] = preferredDynamicPlacePose.SlotQuadrant,
                    ["slot_index"] = preferredDynamicPlacePose.SlotIndex,
                    ["stack_level"] = preferredDynamicPlacePose.StackLevel,
                    ["slot_position"] = preferredDynamicPlacePose.Position,
                    ["used_place_slot_allocator"] = preferredDynamicPlacePose.UsedPlaceSlotAllocator,
                    ["post_place_egress_point"] = preferredDynamicPlacePose.PostPlaceEgressPoint
                });
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F2},{value.y:F2},{value.z:F2})";
        }

        private static Vector3 ToUnityVector(System.Numerics.Vector3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }

        private readonly struct NavigationTargetResolution
        {
            public NavigationTargetResolution(
                Vector3 position,
                string source,
                string approachChildName,
                string candidateId = "",
                string side = "",
                string sourceDetail = "")
            {
                Position = position;
                Source = source ?? string.Empty;
                ApproachChildName = approachChildName ?? string.Empty;
                CandidateId = candidateId ?? string.Empty;
                Side = side ?? string.Empty;
                SourceDetail = sourceDetail ?? string.Empty;
            }

            public Vector3 Position { get; }
            public string Source { get; }
            public string ApproachChildName { get; }
            public string CandidateId { get; }
            public string Side { get; }
            public string SourceDetail { get; }
        }

        private readonly struct PreferredPickupApproach
        {
            public PreferredPickupApproach(Vector3 position, string candidateId, string side, string source)
            {
                Position = position;
                CandidateId = candidateId ?? string.Empty;
                Side = side ?? string.Empty;
                Source = source ?? string.Empty;
            }

            public Vector3 Position { get; }
            public string CandidateId { get; }
            public string Side { get; }
            public string Source { get; }
        }

        private readonly struct PreferredPlaceNavigation
        {
            public PreferredPlaceNavigation(Vector3 position, string candidateId, string sideOrCorner, string source)
            {
                Position = position;
                CandidateId = candidateId ?? string.Empty;
                SideOrCorner = sideOrCorner ?? string.Empty;
                Source = source ?? string.Empty;
            }

            public Vector3 Position { get; }
            public string CandidateId { get; }
            public string SideOrCorner { get; }
            public string Source { get; }
        }

        private readonly struct PreferredDynamicPlacePose
        {
            public PreferredDynamicPlacePose(
                Vector3 position,
                string candidateId,
                string candidateKind,
                string areaSource,
                float placePoseInset,
                Vector3 placePointFallbackPosition,
                bool usedDynamicPlacePose,
                string slotId = "",
                string slotQuadrant = "",
                int slotIndex = -1,
                int stackLevel = 0,
                bool usedPlaceSlotAllocator = false,
                Vector3 postPlaceEgressPoint = default)
            {
                Position = position;
                CandidateId = candidateId ?? string.Empty;
                CandidateKind = candidateKind ?? string.Empty;
                AreaSource = areaSource ?? string.Empty;
                PlacePoseInset = placePoseInset;
                PlacePointFallbackPosition = placePointFallbackPosition;
                UsedDynamicPlacePose = usedDynamicPlacePose;
                SlotId = slotId ?? string.Empty;
                SlotQuadrant = slotQuadrant ?? string.Empty;
                SlotIndex = slotIndex;
                StackLevel = stackLevel;
                UsedPlaceSlotAllocator = usedPlaceSlotAllocator;
                PostPlaceEgressPoint = postPlaceEgressPoint;
            }

            public Vector3 Position { get; }
            public string CandidateId { get; }
            public string CandidateKind { get; }
            public string AreaSource { get; }
            public float PlacePoseInset { get; }
            public Vector3 PlacePointFallbackPosition { get; }
            public bool UsedDynamicPlacePose { get; }
            public string SlotId { get; }
            public string SlotQuadrant { get; }
            public int SlotIndex { get; }
            public int StackLevel { get; }
            public bool UsedPlaceSlotAllocator { get; }
            public Vector3 PostPlaceEgressPoint { get; }
        }
    }
}
