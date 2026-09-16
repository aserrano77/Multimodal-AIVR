using Autonomy.BT.Core;
using Autonomy.Services;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using DomainVector3 = System.Numerics.Vector3;
using UnityVector3 = UnityEngine.Vector3;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Servicio de navegacion para autonomia:
    /// usa NavMesh como guia espacial y ejecuta el movimiento real mediante locomocion diferencial.
    /// La capa local es un driver explicito y stateful, no un planner por muestreo.
    /// </summary>
    public sealed class TiagoNavMeshNavigationService : INavigationService
    {
        private enum LocomotionMode
        {
            StartupAlignment,
            TrackPath,
            // Legacy local-navigation modes are kept for diagnostics/backward compatibility only.
            // ChangeMode blocks them under the current NavMesh-first controller.
            CornerTurn,
            ObstacleBypass,
            StallRecovery
        }

        private enum BypassSide
        {
            Left = -1,
            Right = 1
        }

        private enum StallRecoveryPhase
        {
            None,
            Stop,
            ReverseArc
        }

        public enum StartupAlignmentExitDecision
        {
            Stay,
            HardExit,
            SoftExitAfterNoProgress
        }

        public enum ActivePathSource
        {
            Original,
            Centered,
            ClearanceOffset,
            ClearanceOffsetSmoothed,
            Smoothed,
            ClearanceOffsetLocked,
            ClearanceOffsetSmoothedLocked,
            CenteredLocked,
            SmoothedLocked,
            ReplannedClearanceOffset,
            ReplannedClearanceOffsetSmoothed,
            ReplannedCentered,
            ReplannedSmoothed,
            SmoothedCached,
            SmoothingFailed,
            CenteringFailed
        }

        private const string LogPrefix = "[TiagoNavMeshNav]";
        private const float CornerEnterAngleDeg = 55f;
        private const float CornerHeadingEnterAngleDeg = 32f;
        private const float CornerExitAngleDeg = 14f;
        private const float CornerApproachMultiplier = 1.25f;
        private const float CornerEnterStableSeconds = 0.12f;
        private const float CornerIdentityTolerance = 0.35f;
        private const float CornerReleaseProgressDistance = 0.25f;
        private const float CornerMinimumPositiveProgress = 0.05f;
        private const float CornerClearanceReleaseDistanceMultiplier = 1.5f;
        private const float CornerFollowThroughLinearFactor = 0.35f;
        private const float CornerFollowThroughBlockedLinearFactor = 0.08f;
        private const float CornerFollowThroughMinLinearSpeed = 0.22f;
        private const float CornerFollowThroughNoProgressSeconds = 1.5f;
        private const float CornerFrontCompromisedOccupancy = 0.25f;
        private const float ObstacleEnterOccupancy = 0.18f;
        private const float ObstacleExitOccupancy = 0.08f;
        private const float EmergencyStopFrontOccupancy = 0.55f;
        private const float EmergencyStopStableSeconds = 0.12f;
        private const float ObstacleEnterStableSeconds = 0.10f;
        private const float ObstacleExitStableSeconds = 0.25f;
        private const float BypassMinDurationSeconds = 0.65f;
        private const float BypassMaxDurationSeconds = 2.2f;
        private const float BypassCooldownSeconds = 0.35f;
        private const float ActiveCornerBypassCooldownSeconds = 0.9f;
        private const float ActiveCornerBypassCancelAngleDeg = 45f;
        private const float MinimumModeDwellSeconds = 0.18f;
        private const float ModeOverstaySeconds = 5.0f;
        private const float ProgressEpsilonMeters = 0.05f;
        private const float AnomalyLogCooldownSeconds = 1.0f;
        private const float ActuationDiagnosticIntervalSeconds = 1.0f;
        private const float DriveAsymmetryDetectSeconds = 0.75f;
        private const float DriveFollowGoodRatio = 0.65f;
        private const float DriveFollowPoorRatio = 0.25f;
        private const float AvoidanceDiagnosticIntervalSeconds = 0.5f;
        private const float AvoidanceTurnIntoObstacleSeconds = 0.2f;
        private const float ThreeRayPerceptionLogCooldownSeconds = 0.5f;
        private const float YawCalibrationAngularThreshold = 0.45f;
        private const float YawCalibrationWindowSeconds = 0.35f;
        private const float LateralAvoidanceOccupancyThreshold = 0.35f;
        private const float LateralAvoidanceDominanceMargin = 0.18f;
        private const float LateralAvoidanceLinearScale = 0.30f;
        private const float LateralAvoidanceMinAngularSpeed = 0.85f;
        private const float LateralAvoidanceWeakResponseSeconds = 0.65f;
        private const float LateralAvoidanceObservedAngularThreshold = 0.12f;
        private const float StallObservedLinearThreshold = 0.035f;
        private const float StallObservedAngularThreshold = 0.25f;
        private const float StallObservedAngularInsufficientThreshold = 0.12f;
        private const float StallCommandLinearThreshold = 0.18f;
        private const float StallCommandAngularThreshold = 0.7f;
        private const float StallCommandAngularSustainThreshold = 0.45f;
        private static bool SuppressVerboseAndroidDiagnostics => !QuestLoggingPolicy.EmitLegacyContinuousDiagnostics;
        private const float StallTurnDemandSustainSeconds = 0.45f;
        private const float StallNoProgressSeconds = 1.0f;
        private const float StallDiagnosticLogCooldownSeconds = 0.75f;
        private const float StallRecoveryStopSeconds = 0.20f;
        private const float StallRecoveryReverseArcSeconds = 1.00f;
        private const float StallRecoveryReverseArcSpeed = -0.25f;
        private const float StallRecoveryReverseArcAngularSpeed = 0.60f;
        private const float StallRecoveryCooldownSeconds = 0.8f;
        private const float StallRecoveryMinProgressMeters = 0.10f;
        private const int StallRecoveryMaxAttempts = 2;
        private const float StallForwardCommandMemorySeconds = 1.5f;
        private const float StallRecoveryForwardRecoveryLinearThreshold = 0.12f;
        private const float NavMeshSafetyMarginMeters = 0.12f;
        private const float NavMeshRecommendedAgentRadiusMeters = 0.45f;
        private const float NavMeshFallbackAgentRadiusMeters = 0.50f;
        private const float NavMeshClearanceProbeRadiusMeters = 1.25f;
        private const float HeadingAlignEnterAngleDeg = 40f;
        private const float HeadingAlignExitAngleDeg = 12f;
        private const float HeadingAlignmentHoldSeconds = 0.25f;
        private const float StartupAlignmentSoftExitToleranceDeg = 5f;
        private const float StartupAlignmentSoftExitNoProgressSeconds = 1.0f;
        private const float TrackingSpeedLimitStartAngleDeg = 15f;
        private const float TrackingCornerCrawlAngleDeg = 70f;
        private const float TrackingHardStopAngleDeg = 85f;
        private const float CornerCrawlSpeed = 0.08f;
        // Regression guardrails: these historical limiters stay compiled for diagnostics only.
        // Do not re-enable without a dedicated navigation-behavior validation pass.
        private static readonly bool EnablePathCurvatureSpeedProfiler = false;
        private static readonly bool EnableDifferentialFeasibilityLimiter = false;
        private static readonly bool FootprintClearanceCheckEnabled = true;
        private const float CurvatureLookaheadHorizonMeters = 1.20f;
        private const float ModerateTurnAngleDeg = 20f;
        private const float SharpTurnAngleDeg = 45f;
        private const float VerySharpTurnAngleDeg = 65f;
        private const float ModerateTurnSpeedCap = 0.35f;
        private const float SharpTurnSpeedCap = 0.18f;
        private const float VerySharpTurnSpeedCap = 0.10f;
        private const float FeasibilityAngularLimitStart = 0.40f;
        private const float FeasibilityAngularMedium = 0.80f;
        private const float FeasibilityAngularSaturated = 1.15f;
        private const float FeasibilityMediumAngularSpeedCap = 0.35f;
        private const float FeasibilitySaturatedAngularSpeedCap = 0.18f;
        private const float FootprintRadiusMeters = 0.50f;
        private const float FootprintClearanceSafetyMargin = 0.08f;
        private const float FootprintSampleSpacingMeters = 0.25f;
        private const float FootprintCheckHorizonMeters = 1.50f;
        private const float HeadingAlignmentSampleLogIntervalSeconds = 0.5f;
        private const float CurvatureCommandLogIntervalSeconds = 0.5f;
        private const float CautiousTrackingAngleDeg = 28f;
        private const float CautiousTrackingMaxAngularFactor = 0.80f;
        private static readonly bool EnablePathCentering = true;
        private const float PathCenteringProbeDistance = 0.75f;
        private const float PathCenteringMinClearance = 0.25f;
        private const float PathCenteringMaxShift = 0.35f;
        private const int PathCenteringSamplesPerSide = 3;
        private const float PathCenteringHeightOffset = 0.20f;
        private const float PathCenteringProbeRadius = 0.12f;
        private const float PathCenteringDensifySpacing = 0.50f;
        private const float PathCenteringSampleRadius = 0.35f;
        private const float PathCornerSmoothingDuplicateDistance = 0.03f;
        private const float PathCornerSmoothingMaxTrimSegmentFraction = 0.35f;
        private const int UserSafetyOverlapBufferSize = 64;
        private const int DiagnosticOverlapBufferSize = 64;
        private const string SpeedReductionStraight = "straight";
        private const string SpeedReductionHeadingError = "heading_error";
        private const string SpeedReductionCornerBraking = "corner_braking";
        private const string SpeedReductionNearGoal = "near_goal";
        private const string SpeedReductionAccelerationLimit = "acceleration_limit";

        private readonly Transform _navigationReference;
        private readonly TiagoDifferentialDriveBridge _driveBridge;
        private readonly string _selectedDriveProfile;
        private readonly int _selectedDriveProfileValue;
        private readonly string _activeDriveProfile;
        private readonly string _activeAutonomousPolicy;
        private readonly float _arrivalDistance;
        private readonly float _targetSampleRadius;
        private readonly float _waypointReachDistance;
        private readonly float _pathLookAheadDistance;
        private readonly float _slowdownDistance;
        private readonly float _maxLinearSpeed;
        private readonly float _maxAngularSpeed;
        private readonly float _accelerationLimit;
        private readonly float _decelerationLimit;
        private readonly float _angularGain;
        private readonly float _rotateInPlaceAngleDeg;
        private readonly bool _fastDemoTrackingEnabled;
        private readonly float _fastMinLookaheadDistance;
        private readonly float _fastMaxLookaheadDistance;
        private readonly float _fastLookaheadSpeedFactor;
        private readonly float _fastCornerBrakeDistance;
        private readonly float _fastMediumTurnAngleDeg;
        private readonly float _fastSevereTurnAngleDeg;
        private readonly float _fastMinCornerSpeed;
        private readonly float _fastHeadingSpeedLimitStartAngleDeg;
        private readonly float _fastCornerCrawlAngleDeg;
        private readonly float _headingOffsetDegrees;
        private readonly float _avoidanceDetectionDistance;
        private readonly float _avoidanceRayAngleDegrees;
        private readonly float _avoidanceAngularStrength;
        private readonly float _avoidanceLinearReductionFactor;
        private readonly LayerMask _avoidanceLayerMask;
        private readonly LayerMask _obstacleClearanceMask;
        private readonly float _navMeshSafetyMarginMeters;
        private readonly bool _enablePathCornerSmoothing;
        private readonly float _cornerSmoothingAngleThresholdDeg;
        private readonly float _cornerSmoothingRadius;
        private readonly int _cornerSmoothingSamplesPerCorner;
        private readonly float _cornerSmoothingMinSegmentLength;
        private readonly float _cornerSmoothingNavMeshSampleDistance;
        private readonly bool _cornerSmoothingValidateSegments;
        private readonly bool _cornerSmoothingClearanceAware;
        private readonly float _cornerSmoothingMinNavMeshEdgeClearance;
        private readonly float _cornerSmoothingMaxControlPointOffset;
        private readonly float _cornerSmoothingControlPointOffsetStep;
        private readonly int _cornerSmoothingMaxOffsetAttempts;
        private readonly bool _useLastValidSmoothedPathOnSmoothingFailure;
        private readonly float _maxLastValidSmoothedPathAgeSeconds;
        private readonly float _maxLastValidSmoothedPathStartDistance;
        private readonly float _maxLastValidSmoothedPathTargetDistance;
        private readonly bool _lockActivePathDuringTracking;
        private readonly float _replanIfDistanceFromActivePathExceeds;
        private readonly float _replanIfTargetMovedMoreThan;
        private readonly float _minSecondsBetweenAutomaticReplans;
        private readonly bool _allowPeriodicReplanDuringTracking;
        private readonly bool _enablePathClearanceOffset;
        private readonly float _pathClearanceOffsetMinEdgeDistance;
        private readonly float _pathClearanceOffsetSearchRadius;
        private readonly float _pathClearanceOffsetStep;
        private readonly int _pathClearanceOffsetMaxCandidatesPerSide;
        private readonly float _pathClearanceOffsetNavMeshSampleDistance;
        private readonly bool _pathClearanceOffsetValidateSegments;
        private readonly bool _pathClearanceOffsetSkipEndpoints;
        private readonly float _pathClearanceOffsetMinPointSpacing;
        private readonly bool _pathClearanceOffsetOnlyIfBelowMinEdgeDistance;
        private readonly float _pathClearanceOffsetMaxDeviationFromCenteredPath;
        private readonly float _pathClearanceOffsetMaxLocalHeadingChangeDeg;
        private readonly bool _enableUserSafetyStop;
        private readonly bool _userSafetyStopDetectXRRig;
        private readonly bool _userSafetyStopUseIgnoreRaycastLayer;
        private readonly string[] _userSafetyStopRootNameContains;
        private readonly float _userSafetyStopRadius;
        private readonly float _userSafetyStopPathLookaheadDistance;
        private readonly float _userSafetyResumeRadius;
        private readonly float _userSafetyStopTimeoutSeconds;
        private readonly bool _userSafetyStopCommandZeroVelocity;
        private readonly NavMeshPath _workingPath = new NavMeshPath();

        private LocomotionMode _mode = LocomotionMode.TrackPath;
        private BypassSide _bypassSide = BypassSide.Left;
        private float _modeEnteredAt = 0f;
        private float _bypassStartedAt = float.NegativeInfinity;
#pragma warning disable CS0414 // Legacy ObstacleBypass state is retained for diagnostics but blocked by ChangeMode.
        private float _bypassCooldownUntil = float.NegativeInfinity;
#pragma warning restore CS0414
        private float _frontBlockedSince = float.PositiveInfinity;
        private float _frontClearSince = float.PositiveInfinity;
        private float _emergencyStopSince = float.PositiveInfinity;
        private bool _emergencyStopActive;
        private float _lastLookaheadLogTime = float.NegativeInfinity;
        private float _headingAlignmentExitStableSince = float.PositiveInfinity;
        private float _lastHeadingAlignmentSampleLogTime = float.NegativeInfinity;
        private float _lastCurvatureCommandLogTime = float.NegativeInfinity;
        private float _lastTrackSpeedLimitedLogTime = float.NegativeInfinity;
        private float _lastTrackHardStopLogTime = float.NegativeInfinity;
        private float _lastAlignmentReentrySuppressedLogTime = float.NegativeInfinity;
        private float _lastPathCurvatureLimitedLogTime = float.NegativeInfinity;
        private float _lastFeasibilityLimitedLogTime = float.NegativeInfinity;
        private float _lastCornerCrawlProfileLogTime = float.NegativeInfinity;
        private float _lastTrackingPipelineLogTime = float.NegativeInfinity;
        private float _lastFootprintClearanceLogTime = float.NegativeInfinity;
        private float _lastAlignmentStallSuppressionLogTime = float.NegativeInfinity;
        private float _lastPathStatusLogTime = float.NegativeInfinity;
        private float _lastPathCenteringLogTime = float.NegativeInfinity;
        private float _lastPathClearanceOffsetLogTime = float.NegativeInfinity;
        private float _lastPathCornerSmoothingLogTime = float.NegativeInfinity;
        private float _lastActivePathReusedLogTime = float.NegativeInfinity;
        private ObstacleRayDebugState[] _lastObstacleRayDebugStates = new ObstacleRayDebugState[0];
        private UnityVector3[] _lastNavMeshPathCorners = new UnityVector3[0];
        private UnityVector3[] _lastCenteredPathPoints = new UnityVector3[0];
        private UnityVector3[] _lastClearanceOffsetPathPoints = new UnityVector3[0];
        private UnityVector3[] _lastSmoothedPathPoints = new UnityVector3[0];
        private UnityVector3[] _lastActivePathPoints = new UnityVector3[0];
        private UnityVector3[] _lastValidSmoothedPathPoints = new UnityVector3[0];
        private UnityVector3 _lastValidSmoothedPathTarget = Vector3.zero;
        private UnityVector3 _lastValidSmoothedPathStart = Vector3.zero;
        private float _lastValidSmoothedPathTimestamp = float.NegativeInfinity;
        private UnityVector3[] _lockedActivePathPoints = new UnityVector3[0];
        private UnityVector3 _lockedPathTarget = Vector3.zero;
        private UnityVector3 _lockedPathStart = Vector3.zero;
        private ActivePathSource _lockedPathSource = ActivePathSource.Original;
        private float _lockedPathCreatedAt = float.NegativeInfinity;
        private float _lastAutomaticReplanAt = float.NegativeInfinity;
        private bool _hasLockedActivePath;
        private int _lockedPathVersion;
        private float _lastDistanceFromActivePath = float.NaN;
        private UnityVector3[] _cachedPathCenteringSourceTail = new UnityVector3[0];
        private UnityVector3[] _cachedPathCenteringResult = new UnityVector3[0];
        private ActivePathSource _lastActivePathSource = ActivePathSource.Original;
        private UnityVector3 _lastActiveLookaheadPoint = Vector3.zero;
        private UnityVector3 _lastActiveProjectedPoint = Vector3.zero;
        private bool _hasLastActiveLookahead;
        private int _lastActiveSegmentIndex = -1;
        private int _lastLoggedLookaheadSegment = -1;
        private ActivePathSource _lastLoggedLookaheadSource = ActivePathSource.Original;
        private UnityVector3 _lastLoggedLookaheadPoint = Vector3.positiveInfinity;
        private float _lastStallRecoveryAppliedCommandLogTime = float.NegativeInfinity;
        private readonly Collider[] _userSafetyOverlapBuffer = new Collider[UserSafetyOverlapBufferSize];
        private readonly Collider[] _diagnosticOverlapBuffer = new Collider[DiagnosticOverlapBufferSize];
        private bool _userSafetyStopped;
        private bool _userSafetyTimeoutLogged;
        private float _userSafetyStopStartedAt = float.NegativeInfinity;
        private float _lastUserSafetyDetectedLogTime = float.NegativeInfinity;
        private float _lastUserSafetyWaitingLogTime = float.NegativeInfinity;
        private float _lastVelocityLimitedCommand = 0f;
        private float _lastVelocityLimitTimestamp = float.NegativeInfinity;
        private string _lastSpeedReductionReason = SpeedReductionStraight;
        private float _lastCommandLookaheadDistance = 0f;
        private float _lastDistanceToNextCorner = float.PositiveInfinity;
        private float _lastNextCornerAngleDeg = 0f;
        private bool _autonomyProfileDiagnosticEmitted;
        private bool _hasAutonomyProfileDiagnosticTarget;
        private UnityVector3 _autonomyProfileDiagnosticTarget = Vector3.zero;
        private string _diagnosticCurrentTaskInstanceId = string.Empty;
        private string _diagnosticCurrentRequestId = string.Empty;
        private string _diagnosticCurrentTargetId = string.Empty;
        private float _diagnosticLastRemainingDistance = float.NaN;
        private float _diagnosticLastAngleErrorDeg = float.NaN;
        private float _diagnosticLastFrontClearance = float.NaN;
        private UnityVector3 _diagnosticLastTargetPosition = Vector3.zero;

        public IReadOnlyList<ObstacleRayDebugState> LastObstacleRayDebugStates => _lastObstacleRayDebugStates;
        public IReadOnlyList<UnityVector3> LastOriginalPathCorners => _lastNavMeshPathCorners;
        public IReadOnlyList<UnityVector3> LastNavMeshPathCorners => _lastNavMeshPathCorners;
        public IReadOnlyList<UnityVector3> LastCenteredPathPoints => _lastCenteredPathPoints;
        public IReadOnlyList<UnityVector3> LastClearanceOffsetPathPoints => _lastClearanceOffsetPathPoints;
        public IReadOnlyList<UnityVector3> LastSmoothedPathPoints => _lastSmoothedPathPoints;
        public IReadOnlyList<UnityVector3> LastActivePathPoints => _lastActivePathPoints;
        public ActivePathSource LastActivePathSource => _lastActivePathSource;
        public UnityVector3 LastActiveLookaheadPoint => _lastActiveLookaheadPoint;
        public UnityVector3 LastActiveProjectedPoint => _lastActiveProjectedPoint;
        public int LastActiveSegmentIndex => _lastActiveSegmentIndex;
        public bool HasLastActiveLookahead => _hasLastActiveLookahead;
        public int DiagnosticActivePathVersion => _lockedPathVersion;
        public string DiagnosticActivePathSource => _lockedPathSource.ToString();
        public float DiagnosticActivePathCreatedAt => _lockedPathCreatedAt;
        public float DiagnosticActivePathAge => float.IsNegativeInfinity(_lockedPathCreatedAt) ? float.NaN : Mathf.Max(0f, Time.time - _lockedPathCreatedAt);
        public UnityVector3 DiagnosticActivePathTargetPosition => _lockedPathTarget != Vector3.zero ? _lockedPathTarget : _diagnosticLastTargetPosition;
        public string DiagnosticActivePathTargetId => _diagnosticCurrentTargetId;
        public string DiagnosticActivePathTaskInstanceId => _diagnosticCurrentTaskInstanceId;
        public string DiagnosticCurrentTaskInstanceId => _diagnosticCurrentTaskInstanceId;
        public string DiagnosticCurrentRequestId => _diagnosticCurrentRequestId;
        public string DiagnosticNavigationPhase => _mode.ToString();
        public bool DiagnosticStartupAlignmentActive => _mode == LocomotionMode.StartupAlignment;
        public float DiagnosticStartupAlignmentTime => _mode == LocomotionMode.StartupAlignment ? Mathf.Max(0f, Time.time - _modeEnteredAt) : 0f;
        public float DiagnosticLastRemainingDistance => _diagnosticLastRemainingDistance;
        public float DiagnosticLastAngleErrorDeg => _diagnosticLastAngleErrorDeg;
        public float DiagnosticLastFrontClearance => _diagnosticLastFrontClearance;
        public bool DiagnosticHasActiveCorner => _hasActiveCorner;
#pragma warning disable CS0414, CS0649 // Legacy CornerTurn state is retained for diagnostics but blocked by ChangeMode.
        private float _cornerCandidateSince = float.PositiveInfinity;
        private bool _hasCornerCandidate;
        private UnityVector3 _cornerCandidatePoint;
        private UnityVector3 _cornerCandidateHeading;
        private float _cornerCandidateAngleDeg;
        private float _cornerCandidateDistance;
        private bool _hasActiveCorner;
        private UnityVector3 _activeCornerPoint;
        private UnityVector3 _activeCornerHeading;
        private float _activeCornerAngleDeg;
        private float _activeCornerActivatedAt = float.NegativeInfinity;
        private bool _activeCornerWasAligned;
#pragma warning restore CS0414, CS0649
        private float _lastCornerFollowThroughLogTime = float.NegativeInfinity;
        private float _bestCornerFollowThroughProgress = float.NegativeInfinity;
        private float _cornerFollowThroughNoProgressSince = float.PositiveInfinity;
        private float _bestRemainingDistanceInMode = float.PositiveInfinity;
        private float _noProgressSince = float.PositiveInfinity;
        private float _lastAnomalyLogTime = float.NegativeInfinity;
        private float _lastActuationDiagnosticLogTime = float.NegativeInfinity;
        private float _driveAsymmetrySince = float.PositiveInfinity;
        private float _driveSignMismatchSince = float.PositiveInfinity;
        private float _lastAvoidanceDiagnosticLogTime = float.NegativeInfinity;
        private float _avoidanceTurnsIntoObstacleSince = float.PositiveInfinity;
        private float _lateralObstacleHighSince = float.PositiveInfinity;
        private float _lastLateralBiasLogTime = float.NegativeInfinity;
        private float _lastThreeRayPerceptionLogTime = float.NegativeInfinity;
        private bool _yawCalibrationActive;
        private float _yawCalibrationStartedAt;
        private float _yawCalibrationStartYawDeg;
        private int _yawCalibrationCommandSign;
        private bool _yawCalibrationPositiveLogged;
        private bool _yawCalibrationNegativeLogged;
        private bool _hasActuationSample;
        private UnityVector3 _lastActuationSamplePosition;
        private UnityVector3 _lastActuationSampleHeading;
        private float _lastActuationSampleTime;
        private float _observedLinearSpeed;
        private float _observedAngularSpeed;
        private float _lastCommandLinear;
        private float _lastCommandAngular;
        private bool _lastCommandApplied;
        private float _lastPositiveAutonomousForwardCommandAt = float.NegativeInfinity;
        private float _lastPositiveAutonomousForwardCommand;
        private float _stallCandidateSince = float.PositiveInfinity;
        private float _stallTurnDemandSince = float.PositiveInfinity;
        private float _stallRecoveryStartedAt = float.NegativeInfinity;
        private float _stallRecoveryCooldownUntil = float.NegativeInfinity;
        private float _stallRecoveryEntryRemainingDistance = float.PositiveInfinity;
        private float _stallRecoveryBestRemainingDistance = float.PositiveInfinity;
        private StallRecoveryPhase _stallRecoveryLoggedPhase = StallRecoveryPhase.None;
        private int _stallRecoveryAttemptCount;
        private int _stallRecoveryReverseArcDirectionSign = -1;
        private string _stallRecoveryEndReason = "time_limit";
        private UnityVector3 _stallRecoveryStartPosition;
        private UnityVector3 _stallRecoveryStartHeading;
        private bool _stallRecoveryAwaitingProgressCheck;
        private float _stallRecoveryProgressCheckStartedAt = float.NegativeInfinity;
        private float _stallRecoveryProgressCheckBeforeRemaining = float.PositiveInfinity;
        private int _stallRecoveryProgressCheckAttempt;
        private bool _stallRecoveryResumeReplanPending;
        private float _stallRecoveryResumeOldRemaining = float.PositiveInfinity;
        private int _stallRecoveryResumeOldCorners;
        private bool _stallRecoveryFailedMaxAttempts;
        private int _stallRecoveryEntriesThisRun;
        private float _stallRecoveryTotalSeconds;
        private float _lastStallDiagnosticLogTime = float.NegativeInfinity;
        private bool _lastStallCandidateLogged;
        private string _lastStallReason = "none";
        private bool _startupAlignmentCompleted;
        private bool _hasStartupAlignmentTarget;
        private UnityVector3 _startupAlignmentTarget;
        private FootprintClearanceSnapshot _lastFootprintClearance = FootprintClearanceSnapshot.Empty;

        private readonly struct StallRecoveryEntryDecision
        {
            public StallRecoveryEntryDecision(
                bool candidate,
                bool shouldEnter,
                bool stableCandidate,
                string reason,
                string gate,
                bool hasRecentForwardCommand,
                bool noProgress,
                bool lowObservedMotion,
                bool emergencyBlocked)
            {
                Candidate = candidate;
                ShouldEnter = shouldEnter;
                StableCandidate = stableCandidate;
                Reason = reason;
                Gate = gate;
                HasRecentForwardCommand = hasRecentForwardCommand;
                NoProgress = noProgress;
                LowObservedMotion = lowObservedMotion;
                EmergencyBlocked = emergencyBlocked;
            }

            public bool Candidate { get; }
            public bool ShouldEnter { get; }
            public bool StableCandidate { get; }
            public string Reason { get; }
            public string Gate { get; }
            public bool HasRecentForwardCommand { get; }
            public bool NoProgress { get; }
            public bool LowObservedMotion { get; }
            public bool EmergencyBlocked { get; }
        }

        private readonly struct StallCheckResult
        {
            public StallCheckResult(
                bool candidate,
                string candidateReason,
                string blockedReason,
                bool localConstraint,
                bool noProgressStable,
                bool commandedForward,
                bool strongTurnDemand,
                bool sustainedTurnDemand,
                bool lowObservedLinear,
                bool insufficientObservedAngular,
                float remainingDistance,
                float bestRemainingDistance,
                float timeWithoutProgress,
                float observedLinear,
                float observedAngular,
                float commandedLinear,
                float commandedAngular,
                float frontOccupancy,
                float leftOccupancy,
                float rightOccupancy)
            {
                Candidate = candidate;
                CandidateReason = candidateReason;
                BlockedReason = blockedReason;
                LocalConstraint = localConstraint;
                NoProgressStable = noProgressStable;
                CommandedForward = commandedForward;
                StrongTurnDemand = strongTurnDemand;
                SustainedTurnDemand = sustainedTurnDemand;
                LowObservedLinear = lowObservedLinear;
                InsufficientObservedAngular = insufficientObservedAngular;
                RemainingDistance = remainingDistance;
                BestRemainingDistance = bestRemainingDistance;
                TimeWithoutProgress = timeWithoutProgress;
                ObservedLinear = observedLinear;
                ObservedAngular = observedAngular;
                CommandedLinear = commandedLinear;
                CommandedAngular = commandedAngular;
                FrontOccupancy = frontOccupancy;
                LeftOccupancy = leftOccupancy;
                RightOccupancy = rightOccupancy;
            }

            public bool Candidate { get; }
            public string CandidateReason { get; }
            public string BlockedReason { get; }
            public bool LocalConstraint { get; }
            public bool NoProgressStable { get; }
            public bool CommandedForward { get; }
            public bool StrongTurnDemand { get; }
            public bool SustainedTurnDemand { get; }
            public bool LowObservedLinear { get; }
            public bool InsufficientObservedAngular { get; }
            public float RemainingDistance { get; }
            public float BestRemainingDistance { get; }
            public float TimeWithoutProgress { get; }
            public float ObservedLinear { get; }
            public float ObservedAngular { get; }
            public float CommandedLinear { get; }
            public float CommandedAngular { get; }
            public float FrontOccupancy { get; }
            public float LeftOccupancy { get; }
            public float RightOccupancy { get; }
        }

        private readonly struct UserSafetyObstacleSnapshot
        {
            public UserSafetyObstacleSnapshot(
                bool hasObstacle,
                Collider collider,
                string colliderPath,
                float distanceToRobot,
                float distanceToPathSegment,
                string layerName)
            {
                HasObstacle = hasObstacle;
                Collider = collider;
                ColliderPath = string.IsNullOrEmpty(colliderPath) ? "None" : colliderPath;
                DistanceToRobot = distanceToRobot;
                DistanceToPathSegment = distanceToPathSegment;
                LayerName = string.IsNullOrEmpty(layerName) ? "Unnamed" : layerName;
            }

            public static readonly UserSafetyObstacleSnapshot None =
                new UserSafetyObstacleSnapshot(false, null, "None", float.PositiveInfinity, float.PositiveInfinity, "None");

            public bool HasObstacle { get; }
            public Collider Collider { get; }
            public string ColliderPath { get; }
            public float DistanceToRobot { get; }
            public float DistanceToPathSegment { get; }
            public string LayerName { get; }
        }

        public static StartupAlignmentExitDecision EvaluateStartupAlignmentExit(
            float angleErrorDeg,
            float exitDeg,
            float hardExitStableSeconds,
            float hardExitRequiredSeconds,
            float softToleranceDeg,
            float noProgressSeconds,
            float softExitRequiredNoProgressSeconds,
            bool hasActivePath,
            bool emergencyBlocked)
        {
            if (float.IsNaN(angleErrorDeg) || float.IsInfinity(angleErrorDeg))
            {
                return StartupAlignmentExitDecision.Stay;
            }

            float absoluteAngleErrorDeg = Mathf.Abs(angleErrorDeg);
            if (absoluteAngleErrorDeg <= exitDeg && hardExitStableSeconds >= hardExitRequiredSeconds)
            {
                return StartupAlignmentExitDecision.HardExit;
            }

            if (!hasActivePath || emergencyBlocked)
            {
                return StartupAlignmentExitDecision.Stay;
            }

            if (absoluteAngleErrorDeg <= exitDeg + Mathf.Max(0f, softToleranceDeg) &&
                noProgressSeconds >= softExitRequiredNoProgressSeconds)
            {
                return StartupAlignmentExitDecision.SoftExitAfterNoProgress;
            }

            return StartupAlignmentExitDecision.Stay;
        }

        public TiagoNavMeshNavigationService(
            Transform navigationReference,
            TiagoDifferentialDriveBridge driveBridge,
            float arrivalDistance,
            float targetSampleRadius,
            float waypointReachDistance,
            float pathLookAheadDistance,
            float slowdownDistance,
            float maxLinearSpeed,
            float maxAngularSpeed,
            float accelerationLimit,
            float decelerationLimit,
            float angularGain,
            float rotateInPlaceAngleDeg,
            string selectedDriveProfile,
            int selectedDriveProfileValue,
            string activeDriveProfile,
            string activeAutonomousPolicy,
            bool fastDemoTrackingEnabled,
            float fastMinLookaheadDistance,
            float fastMaxLookaheadDistance,
            float fastLookaheadSpeedFactor,
            float fastCornerBrakeDistance,
            float fastMediumTurnAngleDeg,
            float fastSevereTurnAngleDeg,
            float fastMinCornerSpeed,
            float fastHeadingSpeedLimitStartAngleDeg,
            float fastCornerCrawlAngleDeg,
            float headingOffsetDegrees,
            float avoidanceDetectionDistance,
            float avoidanceRayAngleDegrees,
            float avoidanceAngularStrength,
            float avoidanceLinearReductionFactor,
            LayerMask avoidanceLayerMask,
            int obstacleClearanceMask = ~0,
            float navMeshSafetyMarginMeters = NavMeshSafetyMarginMeters,
            bool enablePathCornerSmoothing = true,
            float cornerSmoothingAngleThresholdDeg = 30f,
            float cornerSmoothingRadius = 0.45f,
            int cornerSmoothingSamplesPerCorner = 6,
            float cornerSmoothingMinSegmentLength = 0.50f,
            float cornerSmoothingNavMeshSampleDistance = 0.30f,
            bool cornerSmoothingValidateSegments = true,
            bool cornerSmoothingClearanceAware = false,
            float cornerSmoothingMinNavMeshEdgeClearance = 0.35f,
            float cornerSmoothingMaxControlPointOffset = 0.75f,
            float cornerSmoothingControlPointOffsetStep = 0.15f,
            int cornerSmoothingMaxOffsetAttempts = 5,
            bool useLastValidSmoothedPathOnSmoothingFailure = false,
            float maxLastValidSmoothedPathAgeSeconds = 1.5f,
            float maxLastValidSmoothedPathStartDistance = 0.75f,
            float maxLastValidSmoothedPathTargetDistance = 0.75f,
            bool lockActivePathDuringTracking = true,
            float replanIfDistanceFromActivePathExceeds = 0.75f,
            float replanIfTargetMovedMoreThan = 0.50f,
            float minSecondsBetweenAutomaticReplans = 1.50f,
            bool allowPeriodicReplanDuringTracking = false,
            bool enablePathClearanceOffset = true,
            float pathClearanceOffsetMinEdgeDistance = 0.35f,
            float pathClearanceOffsetSearchRadius = 0.50f,
            float pathClearanceOffsetStep = 0.10f,
            int pathClearanceOffsetMaxCandidatesPerSide = 5,
            float pathClearanceOffsetNavMeshSampleDistance = 0.20f,
            bool pathClearanceOffsetValidateSegments = true,
            bool pathClearanceOffsetSkipEndpoints = true,
            float pathClearanceOffsetMinPointSpacing = 0.15f,
            bool pathClearanceOffsetOnlyIfBelowMinEdgeDistance = true,
            float pathClearanceOffsetMaxDeviationFromCenteredPath = 0.25f,
            float pathClearanceOffsetMaxLocalHeadingChangeDeg = 25f,
            bool enableUserSafetyStop = true,
            bool userSafetyStopDetectXRRig = true,
            bool userSafetyStopUseIgnoreRaycastLayer = true,
            string[] userSafetyStopRootNameContains = null,
            float userSafetyStopRadius = 0.75f,
            float userSafetyStopPathLookaheadDistance = 1.00f,
            float userSafetyResumeRadius = 0.95f,
            float userSafetyStopTimeoutSeconds = 8.0f,
            bool userSafetyStopCommandZeroVelocity = true)
        {
            _navigationReference = navigationReference;
            _driveBridge = driveBridge;
            _arrivalDistance = Mathf.Max(0.01f, arrivalDistance);
            _targetSampleRadius = Mathf.Max(_arrivalDistance, targetSampleRadius);
            _waypointReachDistance = Mathf.Max(0.05f, waypointReachDistance);
            _pathLookAheadDistance = Mathf.Max(_waypointReachDistance, pathLookAheadDistance);
            _slowdownDistance = Mathf.Max(_arrivalDistance, slowdownDistance);
            _maxLinearSpeed = Mathf.Max(0.01f, maxLinearSpeed);
            _maxAngularSpeed = Mathf.Max(0.01f, maxAngularSpeed);
            _accelerationLimit = Mathf.Max(0f, accelerationLimit);
            _decelerationLimit = Mathf.Max(_accelerationLimit, decelerationLimit);
            _angularGain = Mathf.Max(0.01f, angularGain);
            _rotateInPlaceAngleDeg = Mathf.Clamp(rotateInPlaceAngleDeg, 1f, 180f);
            _selectedDriveProfile = string.IsNullOrWhiteSpace(selectedDriveProfile) ? "Custom" : selectedDriveProfile;
            _selectedDriveProfileValue = selectedDriveProfileValue;
            _activeDriveProfile = string.IsNullOrWhiteSpace(activeDriveProfile) ? "Custom" : activeDriveProfile;
            _activeAutonomousPolicy = string.IsNullOrWhiteSpace(activeAutonomousPolicy) ? "Custom" : activeAutonomousPolicy;
            _fastDemoTrackingEnabled = fastDemoTrackingEnabled;
            _fastMinLookaheadDistance = Mathf.Max(_waypointReachDistance, fastMinLookaheadDistance);
            _fastMaxLookaheadDistance = Mathf.Max(_fastMinLookaheadDistance, fastMaxLookaheadDistance);
            _fastLookaheadSpeedFactor = Mathf.Max(0f, fastLookaheadSpeedFactor);
            _fastCornerBrakeDistance = Mathf.Max(0.05f, fastCornerBrakeDistance);
            _fastMediumTurnAngleDeg = Mathf.Clamp(fastMediumTurnAngleDeg, 1f, 180f);
            _fastSevereTurnAngleDeg = Mathf.Clamp(Mathf.Max(fastMediumTurnAngleDeg, fastSevereTurnAngleDeg), 1f, 180f);
            _fastMinCornerSpeed = Mathf.Clamp(fastMinCornerSpeed, 0f, _maxLinearSpeed);
            _fastHeadingSpeedLimitStartAngleDeg = Mathf.Clamp(fastHeadingSpeedLimitStartAngleDeg, 1f, 180f);
            _fastCornerCrawlAngleDeg = Mathf.Clamp(Mathf.Max(fastHeadingSpeedLimitStartAngleDeg, fastCornerCrawlAngleDeg), 1f, 180f);
            _headingOffsetDegrees = headingOffsetDegrees;
            _avoidanceDetectionDistance = Mathf.Max(0f, avoidanceDetectionDistance);
            _avoidanceRayAngleDegrees = Mathf.Clamp(avoidanceRayAngleDegrees, 0f, 90f);
            _avoidanceAngularStrength = Mathf.Max(0f, avoidanceAngularStrength);
            _avoidanceLinearReductionFactor = Mathf.Clamp01(avoidanceLinearReductionFactor);
            _avoidanceLayerMask = avoidanceLayerMask;
            _obstacleClearanceMask = obstacleClearanceMask;
            _navMeshSafetyMarginMeters = Mathf.Max(0.01f, navMeshSafetyMarginMeters);
            _enablePathCornerSmoothing = enablePathCornerSmoothing;
            _cornerSmoothingAngleThresholdDeg = Mathf.Clamp(cornerSmoothingAngleThresholdDeg, 0f, 180f);
            _cornerSmoothingRadius = Mathf.Max(0.01f, cornerSmoothingRadius);
            _cornerSmoothingSamplesPerCorner = Mathf.Clamp(cornerSmoothingSamplesPerCorner, 2, 24);
            _cornerSmoothingMinSegmentLength = Mathf.Max(0.01f, cornerSmoothingMinSegmentLength);
            _cornerSmoothingNavMeshSampleDistance = Mathf.Max(0.01f, cornerSmoothingNavMeshSampleDistance);
            _cornerSmoothingValidateSegments = cornerSmoothingValidateSegments;
            _cornerSmoothingClearanceAware = cornerSmoothingClearanceAware;
            _cornerSmoothingMinNavMeshEdgeClearance = Mathf.Max(0f, cornerSmoothingMinNavMeshEdgeClearance);
            _cornerSmoothingMaxControlPointOffset = Mathf.Max(0f, cornerSmoothingMaxControlPointOffset);
            _cornerSmoothingControlPointOffsetStep = Mathf.Max(0.01f, cornerSmoothingControlPointOffsetStep);
            _cornerSmoothingMaxOffsetAttempts = Mathf.Clamp(cornerSmoothingMaxOffsetAttempts, 0, 16);
            _useLastValidSmoothedPathOnSmoothingFailure = useLastValidSmoothedPathOnSmoothingFailure;
            _maxLastValidSmoothedPathAgeSeconds = Mathf.Max(0f, maxLastValidSmoothedPathAgeSeconds);
            _maxLastValidSmoothedPathStartDistance = Mathf.Max(0f, maxLastValidSmoothedPathStartDistance);
            _maxLastValidSmoothedPathTargetDistance = Mathf.Max(0f, maxLastValidSmoothedPathTargetDistance);
            _lockActivePathDuringTracking = lockActivePathDuringTracking;
            _replanIfDistanceFromActivePathExceeds = Mathf.Max(0.01f, replanIfDistanceFromActivePathExceeds);
            _replanIfTargetMovedMoreThan = Mathf.Max(0.01f, replanIfTargetMovedMoreThan);
            _minSecondsBetweenAutomaticReplans = Mathf.Max(0f, minSecondsBetweenAutomaticReplans);
            _allowPeriodicReplanDuringTracking = allowPeriodicReplanDuringTracking;
            _enablePathClearanceOffset = enablePathClearanceOffset;
            _pathClearanceOffsetMinEdgeDistance = Mathf.Max(0f, pathClearanceOffsetMinEdgeDistance);
            _pathClearanceOffsetSearchRadius = Mathf.Max(0f, pathClearanceOffsetSearchRadius);
            _pathClearanceOffsetStep = Mathf.Max(0.01f, pathClearanceOffsetStep);
            _pathClearanceOffsetMaxCandidatesPerSide = Mathf.Clamp(pathClearanceOffsetMaxCandidatesPerSide, 0, 32);
            _pathClearanceOffsetNavMeshSampleDistance = Mathf.Max(0.01f, pathClearanceOffsetNavMeshSampleDistance);
            _pathClearanceOffsetValidateSegments = pathClearanceOffsetValidateSegments;
            _pathClearanceOffsetSkipEndpoints = pathClearanceOffsetSkipEndpoints;
            _pathClearanceOffsetMinPointSpacing = Mathf.Max(0.01f, pathClearanceOffsetMinPointSpacing);
            _pathClearanceOffsetOnlyIfBelowMinEdgeDistance = pathClearanceOffsetOnlyIfBelowMinEdgeDistance;
            _pathClearanceOffsetMaxDeviationFromCenteredPath = Mathf.Max(0f, pathClearanceOffsetMaxDeviationFromCenteredPath);
            _pathClearanceOffsetMaxLocalHeadingChangeDeg = Mathf.Clamp(pathClearanceOffsetMaxLocalHeadingChangeDeg, 0f, 180f);
            _enableUserSafetyStop = enableUserSafetyStop;
            _userSafetyStopDetectXRRig = userSafetyStopDetectXRRig;
            _userSafetyStopUseIgnoreRaycastLayer = userSafetyStopUseIgnoreRaycastLayer;
            _userSafetyStopRootNameContains = NormalizeUserSafetyNameTokens(userSafetyStopRootNameContains);
            _userSafetyStopRadius = Mathf.Max(0.05f, userSafetyStopRadius);
            _userSafetyStopPathLookaheadDistance = Mathf.Max(0f, userSafetyStopPathLookaheadDistance);
            _userSafetyResumeRadius = Mathf.Max(_userSafetyStopRadius, userSafetyResumeRadius);
            _userSafetyStopTimeoutSeconds = Mathf.Max(0f, userSafetyStopTimeoutSeconds);
            _userSafetyStopCommandZeroVelocity = userSafetyStopCommandZeroVelocity;
            LogNavigationBuildStamp();
            Debug.Log($"{LogPrefix} Obstacle perception mode: three_ray_front_left_right");
            TiagoExperimentTelemetry.RecordEvent("obstacle_perception_mode", "mode=three_ray_front_left_right rays=front,left,right");
            LogNavMeshClearancePolicy(_navMeshSafetyMarginMeters);
            LogStallRecoveryConfig("service_start");
            TiagoExperimentTelemetry.RecordEvent("curvature_speed_profiler_disabled", $"enabled={EnablePathCurvatureSpeedProfiler} reason=regression_recovery diagnosticOnly=true");
            TiagoExperimentTelemetry.RecordEvent("differential_feasibility_limiter_disabled", $"enabled={EnableDifferentialFeasibilityLimiter} reason=regression_recovery diagnosticOnly=true");
        }

        public void SetP40NavTraceContext(string taskInstanceId, string requestId, string targetId)
        {
            _diagnosticCurrentTaskInstanceId = taskInstanceId ?? string.Empty;
            _diagnosticCurrentRequestId = requestId ?? string.Empty;
            _diagnosticCurrentTargetId = targetId ?? string.Empty;
        }

        public void LogP40NavTraceActivePathOwnershipSnapshot(string reason, string status = "")
        {
            TiagoExperimentTelemetry.LogEvent(
                "active_path_ownership_snapshot",
                BuildP40NavTracePathOwnershipPayload(reason, status));
        }

        public void LogP40NavTraceObstacleRegistrySnapshot(string phase, string reason)
        {
            P40NavTraceDiagnostics.LogObstacleRegistrySnapshot(
                phase,
                reason,
                navigation: this,
                taskInstanceId: _diagnosticCurrentTaskInstanceId,
                requestId: _diagnosticCurrentRequestId,
                activeTargetId: _diagnosticCurrentTargetId,
                robotPosition: _navigationReference != null ? _navigationReference.position : Vector3.zero,
                activePathEnd: DiagnosticActivePathTargetPosition);
        }

        private Dictionary<string, object> BuildP40NavTracePathOwnershipPayload(string reason, string status)
        {
            UnityVector3 robotPosition = _navigationReference != null ? _navigationReference.position : Vector3.zero;
            bool hasCurrentTask = !string.IsNullOrWhiteSpace(_diagnosticCurrentTaskInstanceId);
            return new Dictionary<string, object>
            {
                ["reason"] = reason ?? string.Empty,
                ["path_version"] = _lockedPathVersion,
                ["path_source"] = _lockedPathSource.ToString(),
                ["path_created_at"] = _lockedPathCreatedAt,
                ["path_age"] = DiagnosticActivePathAge,
                ["path_target_position"] = DiagnosticActivePathTargetPosition,
                ["path_target_id"] = _diagnosticCurrentTargetId,
                ["path_task_instance_id"] = _diagnosticCurrentTaskInstanceId,
                ["request_id"] = _diagnosticCurrentRequestId,
                ["current_task_instance_id"] = _diagnosticCurrentTaskInstanceId,
                ["path_matches_current_task"] = hasCurrentTask,
                ["segment_index"] = _lastActiveSegmentIndex,
                ["remaining_distance"] = _diagnosticLastRemainingDistance,
                ["status"] = status ?? string.Empty,
                ["robot_position"] = robotPosition,
                ["navigation_phase"] = _mode.ToString(),
                ["startup_alignment_active"] = _mode == LocomotionMode.StartupAlignment,
                ["startup_alignment_time"] = DiagnosticStartupAlignmentTime,
                ["angle_error"] = _diagnosticLastAngleErrorDeg,
                ["front_clearance"] = _diagnosticLastFrontClearance,
                ["has_active_corner"] = _hasActiveCorner,
                ["frame_count"] = Time.frameCount
            };
        }

        private void LogP40NavTracePathBlockerDiagnostic(
            Collider collider,
            UnityVector3 samplePoint,
            float minClearance,
            float horizonMeters,
            UnityVector3 robotPosition)
        {
            Dictionary<string, object> payload = P40NavTraceDiagnostics.BuildBlockingColliderPayload(
                collider,
                samplePoint,
                minClearance,
                horizonMeters,
                robotPosition,
                this);
            TiagoExperimentTelemetry.LogEvent("path_blocker_diagnostic", payload);
        }

        private void LogP40NavTraceStartupAlignmentBlockDiagnostic(
            UnityVector3 currentPosition,
            UnityVector3 desiredTarget,
            PathFrame frame,
            ObstacleFrame obstacle,
            float angleErrorDeg,
            float remainingDistance)
        {
            Dictionary<string, object> payload = BuildP40NavTracePathOwnershipPayload("startup_alignment_no_progress", "no_progress");
            payload["mode"] = _mode.ToString();
            payload["time_in_mode"] = Mathf.Max(0f, Time.time - _modeEnteredAt);
            payload["no_progress_time"] = StableDuration(_noProgressSince);
            payload["remaining_distance"] = remainingDistance;
            payload["best_remaining_distance"] = _bestRemainingDistanceInMode;
            payload["angle_error"] = angleErrorDeg;
            payload["front_clearance"] = 1f - obstacle.FrontOccupancy;
            payload["active_corner"] = frame.UsesActiveCorner ? frame.CornerPoint : Vector3.zero;
            payload["robot_position"] = currentPosition;
            payload["target_position"] = desiredTarget;
            payload["active_path_version"] = _lockedPathVersion;
            payload["active_path_source"] = _lockedPathSource.ToString();
            payload["nearest_blocking_obstacle"] = string.Empty;
            payload["nearest_blocking_obstacle_kind"] = "unknown";
            payload["nearest_blocking_obstacle_distance"] = float.NaN;
            payload["current_task_instance_id"] = _diagnosticCurrentTaskInstanceId;
            payload["current_target_id"] = _diagnosticCurrentTargetId;
            payload["held_object_id"] = string.Empty;
            payload["navigation_phase"] = _mode.ToString();
            TiagoExperimentTelemetry.LogEvent("startup_alignment_block_diagnostic", payload);
        }

        public NodeStatus MoveTo(DomainVector3 targetPosition)
        {
            if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
            {
                SuspendForExperimentPause();
                return NodeStatus.Running;
            }

            if (_navigationReference == null || _driveBridge == null || !_driveBridge.IsValid)
            {
                LogFailure("invalid_navigation_setup");
                return NodeStatus.Failure;
            }

            if (_stallRecoveryFailedMaxAttempts)
            {
                TiagoLocomotionControlGate.TryStopAutonomous(_driveBridge);
                LogFailure("stall_recovery_failed_max_attempts");
                return NodeStatus.Failure;
            }

            UnityVector3 currentPosition = Flatten(_navigationReference.position);
            UnityVector3 desiredTarget = Flatten(new UnityVector3(targetPosition.X, targetPosition.Y, targetPosition.Z));
            EnsureStartupAlignmentState(desiredTarget);
            EmitAutonomyProfileDiagnosticOnce(desiredTarget);

            if (Vector3.Distance(currentPosition, desiredTarget) <= _arrivalDistance)
            {
                bool stopped = TiagoLocomotionControlGate.TryStopAutonomous(_driveBridge);

                ReleaseActiveCorner("target_reached", currentPosition, correctedHeading: Vector3.zero);
                ChangeMode(LocomotionMode.TrackPath, "target_reached");
                LogSuccess(currentPosition, desiredTarget);
                TiagoExperimentTelemetry.RecordEvent(
                    "stall_recovery_run_summary",
                    $"result=success count={_stallRecoveryEntriesThisRun} totalSeconds={_stallRecoveryTotalSeconds:F2}");
                ResetStallRecoveryNavigationState();
                ResetUserSafetyStop("target_reached");
                ClearPathCenteringCache();
                ClearActivePathLock("target_reached");
                PublishExperimentTelemetry(
                    desiredTarget,
                    0f,
                    new DriveCommand(0f, 0f, 0f),
                    _driveBridge.LastDiagnostics,
                    "Arrived",
                    activeCorner: false,
                    obstacleFront: 0f,
                    commandApplied: stopped);
                return NodeStatus.Success;
            }

            if (!TryBuildPath(
                currentPosition,
                desiredTarget,
                out NavMeshPath path,
                out UnityVector3 sampledTarget,
                out UnityVector3[] activePathPoints,
                out ActivePathSource activePathSource))
            {
                _driveBridge.Stop();
                return NodeStatus.Failure;
            }

            PathTrackingFrame trackingFrame = BuildLookaheadFrame(activePathPoints, activePathSource, currentPosition, sampledTarget);
            if (!trackingFrame.IsValid)
            {
                LogFailure($"invalid_lookahead_frame corners={path.corners.Length}");
                _driveBridge.Stop();
                return NodeStatus.Failure;
            }

            LogPathStatus(path, trackingFrame);

            float directRemainingDistance = Vector3.Distance(currentPosition, desiredTarget);
            if (IsArrivalSatisfied(trackingFrame.RemainingPathDistance, directRemainingDistance))
            {
                bool stopped = TiagoLocomotionControlGate.TryStopAutonomous(_driveBridge);
                ChangeMode(LocomotionMode.TrackPath, "target_reached_path");
                LogSuccess(currentPosition, desiredTarget);
                TiagoExperimentTelemetry.RecordEvent(
                    "stall_recovery_run_summary",
                    $"result=success count={_stallRecoveryEntriesThisRun} totalSeconds={_stallRecoveryTotalSeconds:F2}");
                ResetStallRecoveryNavigationState();
                ResetUserSafetyStop("target_reached_path");
                ClearPathCenteringCache();
                ClearActivePathLock("target_reached_path");
                PublishExperimentTelemetry(
                    desiredTarget,
                    trackingFrame.RemainingPathDistance,
                    new DriveCommand(0f, 0f, 0f),
                    _driveBridge.LastDiagnostics,
                    "Arrived",
                    activeCorner: false,
                    obstacleFront: 0f,
                    commandApplied: stopped);
                return NodeStatus.Success;
            }

            PathFrame rawFrame = PathFrame.FromLookahead(trackingFrame);
            UnityVector3 baseHeading = Flatten(_navigationReference.forward);
            UnityVector3 correctedHeading = ApplyHeadingOffset(baseHeading, _headingOffsetDegrees);
            ObstacleFrame obstacle = SenseObstacles(currentPosition, correctedHeading);
            float remainingDistance = trackingFrame.RemainingPathDistance;
            _diagnosticLastRemainingDistance = remainingDistance;
            _diagnosticLastTargetPosition = desiredTarget;
            _diagnosticLastFrontClearance = 1f - obstacle.FrontOccupancy;

            if (TryHandleUserSafetyStop(
                currentPosition,
                desiredTarget,
                trackingFrame,
                rawFrame,
                obstacle,
                remainingDistance,
                out NodeStatus userSafetyStatus))
            {
                return userSafetyStatus;
            }

            UpdateMotionObservation(currentPosition, correctedHeading);
            UpdateProgressTracking(remainingDistance);

            UpdateTemporalFilters(rawFrame, correctedHeading, obstacle);
            UpdateLocomotionMode(rawFrame, rawFrame, currentPosition, desiredTarget, correctedHeading, obstacle, remainingDistance);
            if (_stallRecoveryFailedMaxAttempts)
            {
                TiagoLocomotionControlGate.TryStopAutonomous(_driveBridge);
                LogFailure("stall_recovery_failed_max_attempts");
                return NodeStatus.Failure;
            }

            if (_stallRecoveryResumeReplanPending &&
                !TryRunStallRecoveryResumeReplan(
                    currentPosition,
                    desiredTarget,
                    ref path,
                    ref sampledTarget,
                    ref activePathPoints,
                    ref trackingFrame,
                    ref rawFrame,
                    ref remainingDistance))
            {
                TiagoLocomotionControlGate.TryStopAutonomous(_driveBridge);
                return NodeStatus.Failure;
            }

            DriveCommand command = ComputeCommandForMode(trackingFrame, rawFrame, currentPosition, correctedHeading, obstacle, remainingDistance);
            _diagnosticLastAngleErrorDeg = Mathf.Abs(command.AngleErrorDeg);

            bool commandApplied = TiagoLocomotionControlGate.TryApplyAutonomousCommand(_driveBridge, command.Linear, command.Angular);
            _lastCommandLinear = command.Linear;
            _lastCommandAngular = command.Angular;
            _lastCommandApplied = commandApplied;
            UpdatePositiveForwardCommandMemory(command, commandApplied);

            PublishExperimentTelemetry(
                desiredTarget,
                remainingDistance,
                command,
                _driveBridge.LastDiagnostics,
                GetLocomotionModeTelemetryLabel(),
                rawFrame.UsesActiveCorner,
                obstacle.FrontOccupancy,
                commandApplied);

            if (commandApplied)
            {
                UpdateYawCalibration(command.Angular);

                if (_mode == LocomotionMode.StallRecovery)
                {
                    LogStallRecoveryAppliedCommand(command);
                    LogRecoveryPhase(command, obstacle, correctedHeading, commandApplied);
                }

                LogActuationDiagnostics(currentPosition, correctedHeading, rawFrame, obstacle, command);
                LogAnomalies(currentPosition, desiredTarget, rawFrame, obstacle, command, remainingDistance);
            }

            return NodeStatus.Running;
        }

        public void SuspendForExperimentPause()
        {
            // Do not call Stop(): Stop clears the active path lock and recovery/navigation
            // state. A menu pause only zeroes actuation so the same MoveTo resumes.
            _driveBridge?.Stop();
        }

        public void Stop()
        {
            if (!TiagoLocomotionControlGate.ManualOverrideActive)
            {
                TiagoLocomotionControlGate.TryStopAutonomous(_driveBridge);
            }

            ReleaseActiveCorner("stop_requested", _navigationReference != null ? Flatten(_navigationReference.position) : Vector3.zero, correctedHeading: Vector3.zero);
            ChangeMode(LocomotionMode.TrackPath, "stop_requested");
            ResetStallRecoveryNavigationState();
            ResetUserSafetyStop("stop_requested");
            ClearPathCenteringCache();
            ClearActivePathLock("stop_requested");
            _lastVelocityLimitedCommand = 0f;
            _lastVelocityLimitTimestamp = float.NegativeInfinity;
            _lastSpeedReductionReason = SpeedReductionStraight;
        }

        private bool TryBuildPath(
            UnityVector3 currentPosition,
            UnityVector3 desiredTarget,
            out NavMeshPath path,
            out UnityVector3 sampledTarget,
            out UnityVector3[] activePathPoints,
            out ActivePathSource activePathSource)
        {
            path = _workingPath;
            sampledTarget = desiredTarget;
            activePathPoints = Array.Empty<UnityVector3>();
            activePathSource = ActivePathSource.Original;

            if (!NavMesh.SamplePosition(desiredTarget, out NavMeshHit sampledHit, _targetSampleRadius, NavMesh.AllAreas))
            {
                LogFailure($"sample_target_failed target={FormatVector(desiredTarget)} sampleRadius={_targetSampleRadius:F2}");
                return false;
            }

            sampledTarget = Flatten(sampledHit.position);
            if (TryUseLockedActivePath(currentPosition, sampledTarget, out activePathPoints, out activePathSource))
            {
                UpdateLastActivePathPoints(activePathPoints, activePathSource);
                return true;
            }

            if (!NavMesh.CalculatePath(currentPosition, sampledTarget, NavMesh.AllAreas, path))
            {
                LogFailure($"calculate_path_failed current={FormatVector(currentPosition)} target={FormatVector(sampledTarget)}");
                return false;
            }

            if (path.status != NavMeshPathStatus.PathComplete || path.corners.Length == 0)
            {
                LogFailure($"invalid_path status={path.status} corners={path.corners.Length}");
                return false;
            }

            UnityVector3[] originalPoints = CopyFlattenedCorners(path);
            PathCenteringResult centering = EnablePathCentering
                ? BuildCenteredPathPoints(originalPoints)
                : PathCenteringResult.Failed("disabled", 0, originalPoints.Length);
            UnityVector3[] centeredPoints = centering.Points;
            UnityVector3[] clearanceOffsetPoints = new UnityVector3[0];
            UnityVector3[] smoothedPoints = new UnityVector3[0];
            PathClearanceOffsetResult clearanceOffset = PathClearanceOffsetResult.Failed("disabled", 0, 0);
            PathCornerSmoothingResult smoothing = PathCornerSmoothingResult.Failed("disabled", 0, 0, originalPoints.Length, centeredPoints.Length);

            if (EnablePathCentering && centering.IsValid)
            {
                clearanceOffset = _enablePathClearanceOffset
                    ? BuildClearanceOffsetPathPoints(centeredPoints)
                    : PathClearanceOffsetResult.Failed("disabled", centeredPoints.Length, centeredPoints.Length);
                clearanceOffsetPoints = clearanceOffset.Points;
                UnityVector3[] smoothingInputPoints = clearanceOffset.IsValid ? clearanceOffsetPoints : centeredPoints;
                smoothing = _enablePathCornerSmoothing
                    ? BuildSmoothedPathPoints(smoothingInputPoints, originalPoints.Length)
                    : PathCornerSmoothingResult.Failed("disabled", 0, 0, originalPoints.Length, smoothingInputPoints.Length);
                smoothedPoints = smoothing.Points;

                if (_enablePathCornerSmoothing && smoothing.IsValid)
                {
                    activePathPoints = smoothedPoints;
                    activePathSource = clearanceOffset.IsValid ? ActivePathSource.ClearanceOffsetSmoothed : ActivePathSource.Smoothed;
                    if (_useLastValidSmoothedPathOnSmoothingFailure)
                    {
                        StoreLastValidSmoothedPath(smoothedPoints, currentPosition, sampledTarget);
                    }
                }
                else if (clearanceOffset.IsValid)
                {
                    activePathPoints = clearanceOffsetPoints;
                    activePathSource = ActivePathSource.ClearanceOffset;
                }
                else
                {
                    activePathPoints = centeredPoints;
                    activePathSource = ActivePathSource.Centered;
                }
            }
            else if (EnablePathCentering)
            {
                activePathPoints = originalPoints;
                activePathSource = ActivePathSource.CenteringFailed;
            }
            else
            {
                activePathPoints = originalPoints;
                activePathSource = ActivePathSource.Original;
            }

            UpdateLastOriginalPathCorners(originalPoints);
            UpdateLastCenteredPathPoints(centeredPoints);
            UpdateLastClearanceOffsetPathPoints(clearanceOffsetPoints);
            UpdateLastSmoothedPathPoints(smoothedPoints);
            if (ShouldLockActivePath(activePathSource, activePathPoints))
            {
                ActivePathSource unlockedSource = activePathSource;
                activePathSource = ToLockedPathSource(activePathSource, _lockedPathVersion > 0);
                StoreLockedActivePath(activePathPoints, currentPosition, sampledTarget, activePathSource, unlockedSource);
                activePathPoints = _lockedActivePathPoints;
            }

            UpdateLastActivePathPoints(activePathPoints, activePathSource);
            LogPathSelection(activePathSource, originalPoints, centeredPoints, smoothedPoints, activePathPoints);
            return true;
        }

        private void EmitAutonomyProfileDiagnosticOnce(UnityVector3 desiredTarget)
        {
            UnityVector3 flattenedTarget = Flatten(desiredTarget);
            if (_hasAutonomyProfileDiagnosticTarget &&
                Vector3.Distance(_autonomyProfileDiagnosticTarget, flattenedTarget) > Mathf.Max(_arrivalDistance, 0.05f))
            {
                _autonomyProfileDiagnosticEmitted = false;
            }

            _hasAutonomyProfileDiagnosticTarget = true;
            _autonomyProfileDiagnosticTarget = flattenedTarget;

            if (_autonomyProfileDiagnosticEmitted)
            {
                return;
            }

            bool logged = TiagoExperimentTelemetry.LogEvent(
                "autonomy_profile_diagnostic",
                new Dictionary<string, object>
                {
                    ["selected_drive_profile"] = _selectedDriveProfile,
                    ["selected_drive_profile_value"] = _selectedDriveProfileValue,
                    ["normalized_drive_profile"] = _activeDriveProfile,
                    ["active_drive_profile"] = _activeDriveProfile,
                    ["active_autonomous_policy"] = _activeAutonomousPolicy,
                    ["target"] = flattenedTarget,
                    ["maxLinearSpeed"] = _maxLinearSpeed,
                    ["maxAngularSpeed"] = _maxAngularSpeed,
                    ["initialLookaheadDistance"] = _pathLookAheadDistance,
                    ["accelerationLimit"] = _accelerationLimit,
                    ["decelerationLimit"] = _decelerationLimit,
                    ["fastDemoTrackingEnabled"] = _fastDemoTrackingEnabled,
                    ["fastMinLookaheadDistance"] = _fastMinLookaheadDistance,
                    ["fastMaxLookaheadDistance"] = _fastMaxLookaheadDistance,
                    ["fastLookaheadSpeedFactor"] = _fastLookaheadSpeedFactor,
                    ["fastCornerBrakeDistance"] = _fastCornerBrakeDistance,
                    ["fastMediumTurnAngleDeg"] = _fastMediumTurnAngleDeg,
                    ["fastSevereTurnAngleDeg"] = _fastSevereTurnAngleDeg,
                    ["fastMinCornerSpeed"] = _fastMinCornerSpeed
                });

            if (logged)
            {
                _autonomyProfileDiagnosticEmitted = true;
            }
        }

        private bool TryUseLockedActivePath(
            UnityVector3 currentPosition,
            UnityVector3 sampledTarget,
            out UnityVector3[] activePathPoints,
            out ActivePathSource activePathSource)
        {
            activePathPoints = Array.Empty<UnityVector3>();
            activePathSource = ActivePathSource.Original;

            if (!_lockActivePathDuringTracking || _allowPeriodicReplanDuringTracking || !_hasLockedActivePath)
            {
                return false;
            }

            float targetDelta = Vector3.Distance(Flatten(sampledTarget), _lockedPathTarget);
            if (targetDelta > _replanIfTargetMovedMoreThan)
            {
                RequestActivePathReplan("target_changed", currentPosition, targetDelta);
                return false;
            }

            if (!ValidateLockedActivePath())
            {
                RequestActivePathReplan("locked_path_invalid", currentPosition, targetDelta);
                return false;
            }

            float distanceFromPath = CalculateDistanceToPath(_lockedActivePathPoints, currentPosition);
            _lastDistanceFromActivePath = distanceFromPath;
            if (distanceFromPath > _replanIfDistanceFromActivePathExceeds)
            {
                float secondsSinceLastReplan = Time.time - _lastAutomaticReplanAt;
                if (secondsSinceLastReplan >= _minSecondsBetweenAutomaticReplans)
                {
                    RequestActivePathReplan("robot_far_from_active_path", currentPosition, targetDelta, distanceFromPath);
                    return false;
                }
            }

            activePathPoints = _lockedActivePathPoints;
            activePathSource = _lockedPathSource;
            LogActivePathReused(activePathSource, activePathPoints.Length, distanceFromPath);
            return true;
        }

        private bool ShouldLockActivePath(ActivePathSource source, IReadOnlyList<UnityVector3> points)
        {
            return _lockActivePathDuringTracking &&
                !_allowPeriodicReplanDuringTracking &&
                points != null &&
                points.Count >= 2 &&
                (source == ActivePathSource.Smoothed ||
                 source == ActivePathSource.Centered ||
                 source == ActivePathSource.ClearanceOffset ||
                 source == ActivePathSource.ClearanceOffsetSmoothed);
        }

        private void StoreLockedActivePath(
            IReadOnlyList<UnityVector3> activePathPoints,
            UnityVector3 currentPosition,
            UnityVector3 sampledTarget,
            ActivePathSource lockedSource,
            ActivePathSource unlockedSource)
        {
            _lockedActivePathPoints = CopyPoints(activePathPoints);
            _lockedPathStart = Flatten(currentPosition);
            _lockedPathTarget = Flatten(sampledTarget);
            _lockedPathSource = lockedSource;
            _lockedPathCreatedAt = Time.time;
            _lastAutomaticReplanAt = Time.time;
            _hasLockedActivePath = true;
            _lockedPathVersion++;
            string eventName = lockedSource == ActivePathSource.ReplannedSmoothed || lockedSource == ActivePathSource.ReplannedCentered
                || lockedSource == ActivePathSource.ReplannedClearanceOffset || lockedSource == ActivePathSource.ReplannedClearanceOffsetSmoothed
                ? "active_path_replanned"
                : "active_path_locked";
            string details =
                $"source={lockedSource} pointCount={_lockedActivePathPoints.Length} target={FormatVector(_lockedPathTarget)} start={FormatVector(_lockedPathStart)} createdAt={_lockedPathCreatedAt:F2} version={_lockedPathVersion}";
            Debug.Log($"{LogPrefix} [PathLock] {eventName} | {details}");
            TiagoExperimentTelemetry.RecordEvent(eventName, details);
            LogP40NavTraceActivePathOwnershipSnapshot(eventName, "created");
        }

        private void RequestActivePathReplan(string reason, UnityVector3 currentPosition, float targetDelta, float distanceFromPath = float.NaN)
        {
            float secondsSinceLastReplan = Time.time - _lastAutomaticReplanAt;
            string details =
                $"reason={reason} distanceFromPath={FormatFloat(distanceFromPath)} targetDelta={targetDelta:F2} secondsSinceLastReplan={secondsSinceLastReplan:F2} pointCount={(_lockedActivePathPoints?.Length ?? 0)} version={_lockedPathVersion}";
            Debug.Log($"{LogPrefix} [PathLock] active_path_replan_requested | {details}");
            TiagoExperimentTelemetry.RecordEvent("active_path_replan_requested", details);
            LogP40NavTraceActivePathOwnershipSnapshot($"replan_requested_{reason}", "replan_requested");
            ClearActivePathLock($"replan_requested_{reason}");
            ClearPathCenteringCache();
        }

        private void LogActivePathReused(ActivePathSource source, int pointCount, float distanceFromPath)
        {
            if (Time.time - _lastActivePathReusedLogTime < ActuationDiagnosticIntervalSeconds)
            {
                return;
            }

            _lastActivePathReusedLogTime = Time.time;
            string details =
                $"source={source} pointCount={pointCount} distanceFromPath={distanceFromPath:F2} age={(Time.time - _lockedPathCreatedAt):F2} version={_lockedPathVersion}";
            Debug.Log($"{LogPrefix} [PathLock] active_path_reused | {details}");
            TiagoExperimentTelemetry.RecordEvent("active_path_reused", details);
            LogP40NavTraceActivePathOwnershipSnapshot("active_path_reused", "reused");
        }

        private void ClearActivePathLock(string reason)
        {
            if (!_hasLockedActivePath && (_lockedActivePathPoints == null || _lockedActivePathPoints.Length == 0))
            {
                return;
            }

            string details =
                $"reason={reason} source={_lockedPathSource} pointCount={(_lockedActivePathPoints?.Length ?? 0)} age={(Time.time - _lockedPathCreatedAt):F2} version={_lockedPathVersion}";
            Debug.Log($"{LogPrefix} [PathLock] active_path_lock_cleared | {details}");
            TiagoExperimentTelemetry.RecordEvent("active_path_lock_cleared", details);
            _lockedActivePathPoints = Array.Empty<UnityVector3>();
            _lockedPathTarget = Vector3.zero;
            _lockedPathStart = Vector3.zero;
            _lockedPathSource = ActivePathSource.Original;
            _lockedPathCreatedAt = float.NegativeInfinity;
            _hasLockedActivePath = false;
            _lastDistanceFromActivePath = float.NaN;
        }

        private bool ValidateLockedActivePath()
        {
            if (!_hasLockedActivePath || _lockedActivePathPoints == null || _lockedActivePathPoints.Length < 2)
            {
                return false;
            }

            for (int i = 0; i < _lockedActivePathPoints.Length; i++)
            {
                UnityVector3 point = Flatten(_lockedActivePathPoints[i]);
                if (!NavMesh.SamplePosition(point, out NavMeshHit sampleHit, _cornerSmoothingNavMeshSampleDistance, NavMesh.AllAreas))
                {
                    return false;
                }

                if (Vector3.Distance(point, Flatten(sampleHit.position)) > _cornerSmoothingNavMeshSampleDistance + 0.001f)
                {
                    return false;
                }
            }

            return true;
        }

        private float CalculateDistanceToPath(IReadOnlyList<UnityVector3> pathPoints, UnityVector3 currentPosition)
        {
            if (pathPoints == null || pathPoints.Count == 0)
            {
                return float.PositiveInfinity;
            }

            UnityVector3 projected = ProjectOntoPath(pathPoints, Flatten(currentPosition), out _, out _);
            return Vector3.Distance(Flatten(currentPosition), projected);
        }

        private static ActivePathSource ToLockedPathSource(ActivePathSource source, bool isReplan)
        {
            if (source == ActivePathSource.Smoothed)
            {
                return isReplan ? ActivePathSource.ReplannedSmoothed : ActivePathSource.SmoothedLocked;
            }

            if (source == ActivePathSource.ClearanceOffsetSmoothed)
            {
                return isReplan ? ActivePathSource.ReplannedClearanceOffsetSmoothed : ActivePathSource.ClearanceOffsetSmoothedLocked;
            }

            if (source == ActivePathSource.ClearanceOffset)
            {
                return isReplan ? ActivePathSource.ReplannedClearanceOffset : ActivePathSource.ClearanceOffsetLocked;
            }

            if (source == ActivePathSource.Centered)
            {
                return isReplan ? ActivePathSource.ReplannedCentered : ActivePathSource.CenteredLocked;
            }

            return source;
        }

        private void UpdateLastNavMeshPathCorners(NavMeshPath path)
        {
            UpdateLastOriginalPathCorners(CopyFlattenedCorners(path));
        }

        private void UpdateLastOriginalPathCorners(IReadOnlyList<UnityVector3> originalPoints)
        {
            if (originalPoints == null || originalPoints.Count == 0)
            {
                _lastNavMeshPathCorners = new UnityVector3[0];
                return;
            }

            _lastNavMeshPathCorners = CopyPoints(originalPoints);
        }

        private void UpdateLastCenteredPathPoints(IReadOnlyList<UnityVector3> centeredPathPoints)
        {
            _lastCenteredPathPoints = CopyPoints(centeredPathPoints);
        }

        private void UpdateLastClearanceOffsetPathPoints(IReadOnlyList<UnityVector3> clearanceOffsetPathPoints)
        {
            _lastClearanceOffsetPathPoints = CopyPoints(clearanceOffsetPathPoints);
        }

        private void UpdateLastSmoothedPathPoints(IReadOnlyList<UnityVector3> smoothedPathPoints)
        {
            _lastSmoothedPathPoints = CopyPoints(smoothedPathPoints);
        }

        private void UpdateLastActivePathPoints(IReadOnlyList<UnityVector3> activePathPoints, ActivePathSource source)
        {
            _lastActivePathPoints = ReferenceEquals(activePathPoints, _lockedActivePathPoints)
                ? _lockedActivePathPoints
                : CopyPoints(activePathPoints);
            _lastActivePathSource = source;
        }

        private void StoreLastValidSmoothedPath(IReadOnlyList<UnityVector3> smoothedPathPoints, UnityVector3 currentPosition, UnityVector3 sampledTarget)
        {
            if (smoothedPathPoints == null || smoothedPathPoints.Count < 2)
            {
                return;
            }

            _lastValidSmoothedPathPoints = CopyPoints(smoothedPathPoints);
            _lastValidSmoothedPathStart = Flatten(currentPosition);
            _lastValidSmoothedPathTarget = Flatten(sampledTarget);
            _lastValidSmoothedPathTimestamp = Time.time;
        }

        private bool TryGetLastValidSmoothedPath(
            UnityVector3 currentPosition,
            UnityVector3 sampledTarget,
            out UnityVector3[] cachedSmoothedPath,
            out string reason)
        {
            cachedSmoothedPath = null;
            reason = "cache_disabled";
            if (!_useLastValidSmoothedPathOnSmoothingFailure)
            {
                return false;
            }

            if (_lastValidSmoothedPathPoints == null || _lastValidSmoothedPathPoints.Length < 2)
            {
                reason = "cache_empty";
                return false;
            }

            float age = Time.time - _lastValidSmoothedPathTimestamp;
            if (age < 0f || age > _maxLastValidSmoothedPathAgeSeconds)
            {
                reason = $"cache_stale age={age:F2}";
                return false;
            }

            float targetDistance = Vector3.Distance(Flatten(sampledTarget), _lastValidSmoothedPathTarget);
            if (targetDistance > _maxLastValidSmoothedPathTargetDistance)
            {
                reason = $"target_changed distance={targetDistance:F2}";
                return false;
            }

            UnityVector3 projected = ProjectOntoPath(_lastValidSmoothedPathPoints, Flatten(currentPosition), out _, out _);
            float distanceToCachedPath = Vector3.Distance(Flatten(currentPosition), projected);
            float distanceToCachedStart = Vector3.Distance(Flatten(currentPosition), _lastValidSmoothedPathStart);
            float allowedStartDistance = Mathf.Max(_maxLastValidSmoothedPathStartDistance, _pathLookAheadDistance);
            if (distanceToCachedPath > _maxLastValidSmoothedPathStartDistance && distanceToCachedStart > allowedStartDistance)
            {
                reason = $"robot_far_from_cache pathDistance={distanceToCachedPath:F2} startDistance={distanceToCachedStart:F2}";
                return false;
            }

            if (!ValidateCachedSmoothedPath(_lastValidSmoothedPathPoints))
            {
                reason = "cache_navmesh_invalid";
                return false;
            }

            cachedSmoothedPath = CopyPoints(_lastValidSmoothedPathPoints);
            reason = $"cache_valid age={age:F2} pathDistance={distanceToCachedPath:F2} targetDistance={targetDistance:F2}";
            return true;
        }

        private bool ValidateCachedSmoothedPath(IReadOnlyList<UnityVector3> cachedPath)
        {
            if (cachedPath == null || cachedPath.Count < 2)
            {
                return false;
            }

            for (int i = 0; i < cachedPath.Count; i++)
            {
                UnityVector3 point = Flatten(cachedPath[i]);
                if (!NavMesh.SamplePosition(point, out NavMeshHit sampleHit, _cornerSmoothingNavMeshSampleDistance, NavMesh.AllAreas))
                {
                    return false;
                }

                if (Vector3.Distance(point, Flatten(sampleHit.position)) > _cornerSmoothingNavMeshSampleDistance + 0.001f)
                {
                    return false;
                }
            }

            return true;
        }

        private PathTrackingFrame BuildLookaheadFrame(
            IReadOnlyList<UnityVector3> pathPoints,
            ActivePathSource source,
            UnityVector3 currentPosition,
            UnityVector3 finalTarget)
        {
            if (pathPoints == null || pathPoints.Count == 0)
            {
                return PathTrackingFrame.Invalid;
            }

            UnityVector3 projectedPoint = ProjectOntoPath(pathPoints, currentPosition, out int segmentIndex, out float alongPathDistance);
            float pathLength = CalculatePathLength(pathPoints, projectedPoint, segmentIndex, alongPathDistance);
            float remainingPathDistance = Mathf.Max(0f, pathLength - alongPathDistance);
            CornerInfo nextCorner = FindNextRelevantCorner(pathPoints, segmentIndex, alongPathDistance);
            float activeLookaheadDistance = ComputeActiveLookaheadDistance(nextCorner);
            float targetDistance = Mathf.Min(pathLength, alongPathDistance + activeLookaheadDistance);
            UnityVector3 lookaheadPoint = SamplePathAtDistance(pathPoints, targetDistance);
            UnityVector3 fallbackHeading = SafeDirection(finalTarget - currentPosition, Vector3.forward);
            UnityVector3 lookaheadHeading = SafeDirection(lookaheadPoint - currentPosition, fallbackHeading);
            float distanceToPath = Vector3.Distance(currentPosition, projectedPoint);
            _lastActiveLookaheadPoint = lookaheadPoint;
            _lastActiveProjectedPoint = projectedPoint;
            _lastActiveSegmentIndex = segmentIndex;
            _hasLastActiveLookahead = true;
            _lastCommandLookaheadDistance = activeLookaheadDistance;
            _lastDistanceToNextCorner = nextCorner.DistanceFromProjection;
            _lastNextCornerAngleDeg = nextCorner.AngleDeg;
            LogLookaheadTarget(source, segmentIndex, lookaheadPoint, distanceToPath, remainingPathDistance);

            return new PathTrackingFrame(
                lookaheadPoint,
                lookaheadHeading,
                alongPathDistance,
                remainingPathDistance,
                segmentIndex,
                projectedPoint,
                distanceToPath,
                source,
                activeLookaheadDistance,
                nextCorner.DistanceFromProjection,
                nextCorner.AngleDeg,
                true);
        }

        private float ComputeActiveLookaheadDistance(CornerInfo nextCorner)
        {
            if (!_fastDemoTrackingEnabled)
            {
                return _pathLookAheadDistance;
            }

            float speedBasedLookahead = _fastMinLookaheadDistance + Mathf.Max(0f, _lastVelocityLimitedCommand) * _fastLookaheadSpeedFactor;
            float lookahead = Mathf.Clamp(speedBasedLookahead, _fastMinLookaheadDistance, _fastMaxLookaheadDistance);
            if (nextCorner.IsValid && nextCorner.AngleDeg >= _fastMediumTurnAngleDeg && nextCorner.DistanceFromProjection <= _fastCornerBrakeDistance)
            {
                float severity = Mathf.InverseLerp(_fastMediumTurnAngleDeg, _fastSevereTurnAngleDeg, nextCorner.AngleDeg);
                float distance = Mathf.Clamp01(nextCorner.DistanceFromProjection / _fastCornerBrakeDistance);
                float curveCap = Mathf.Lerp(_fastMinLookaheadDistance, _fastMaxLookaheadDistance, distance * (1f - severity));
                lookahead = Mathf.Min(lookahead, curveCap);
            }

            return Mathf.Max(_waypointReachDistance, lookahead);
        }

        private UnityVector3 ProjectOntoPath(IReadOnlyList<UnityVector3> pathPoints, UnityVector3 currentPosition, out int segmentIndex, out float distanceAlongPath)
        {
            segmentIndex = 0;
            distanceAlongPath = 0f;

            if (pathPoints == null || pathPoints.Count == 0)
            {
                return currentPosition;
            }

            if (pathPoints.Count == 1)
            {
                return Flatten(pathPoints[0]);
            }

            UnityVector3 bestPoint = Flatten(pathPoints[0]);
            float bestSqrDistance = float.PositiveInfinity;
            float accumulatedDistance = 0f;
            float bestDistanceAlongPath = 0f;

            for (int i = 0; i < pathPoints.Count - 1; i++)
            {
                UnityVector3 a = Flatten(pathPoints[i]);
                UnityVector3 b = Flatten(pathPoints[i + 1]);
                UnityVector3 segment = b - a;
                float segmentLength = segment.magnitude;
                if (segmentLength <= 0.001f)
                {
                    continue;
                }

                float t = Mathf.Clamp01(Vector3.Dot(currentPosition - a, segment) / (segmentLength * segmentLength));
                UnityVector3 candidate = a + segment * t;
                float sqrDistance = (currentPosition - candidate).sqrMagnitude;
                if (sqrDistance < bestSqrDistance)
                {
                    bestSqrDistance = sqrDistance;
                    bestPoint = candidate;
                    segmentIndex = i;
                    bestDistanceAlongPath = accumulatedDistance + segmentLength * t;
                }

                accumulatedDistance += segmentLength;
            }

            distanceAlongPath = bestDistanceAlongPath;
            return bestPoint;
        }

        private UnityVector3 SamplePathAtDistance(IReadOnlyList<UnityVector3> pathPoints, float distanceAlongPath)
        {
            if (pathPoints == null || pathPoints.Count == 0)
            {
                return Vector3.zero;
            }

            if (pathPoints.Count == 1)
            {
                return Flatten(pathPoints[0]);
            }

            float accumulatedDistance = 0f;
            for (int i = 0; i < pathPoints.Count - 1; i++)
            {
                UnityVector3 a = Flatten(pathPoints[i]);
                UnityVector3 b = Flatten(pathPoints[i + 1]);
                float segmentLength = Vector3.Distance(a, b);
                if (segmentLength <= 0.001f)
                {
                    continue;
                }

                if (accumulatedDistance + segmentLength >= distanceAlongPath)
                {
                    float t = (distanceAlongPath - accumulatedDistance) / segmentLength;
                    return Vector3.Lerp(a, b, Mathf.Clamp01(t));
                }

                accumulatedDistance += segmentLength;
            }

            return Flatten(pathPoints[pathPoints.Count - 1]);
        }

        private bool TryHandleUserSafetyStop(
            UnityVector3 currentPosition,
            UnityVector3 desiredTarget,
            PathTrackingFrame trackingFrame,
            PathFrame rawFrame,
            ObstacleFrame obstacle,
            float remainingDistance,
            out NodeStatus status)
        {
            status = NodeStatus.Running;
            if (!_enableUserSafetyStop)
            {
                if (_userSafetyStopped)
                {
                    ResetUserSafetyStop("disabled");
                }

                return false;
            }

            bool userBlocking = TryFindUserSafetyObstacle(currentPosition, trackingFrame, out UserSafetyObstacleSnapshot nearestUser);
            if (!userBlocking)
            {
                if (_userSafetyStopped)
                {
                    float elapsed = Time.time - _userSafetyStopStartedAt;
                    LogUserSafetyStopExited(elapsed);
                    ResetUserSafetyStop("user_cleared");
                }

                return false;
            }

            if (!_userSafetyStopped)
            {
                _userSafetyStopped = true;
                _userSafetyTimeoutLogged = false;
                _userSafetyStopStartedAt = Time.time;
                _lastUserSafetyWaitingLogTime = float.NegativeInfinity;
                LogUserSafetyStopEntered(nearestUser);
            }

            float stopElapsed = Mathf.Max(0f, Time.time - _userSafetyStopStartedAt);
            LogUserSafetyStopWaiting(stopElapsed, nearestUser);

            DriveCommand zeroCommand = new DriveCommand(0f, 0f, 0f);
            bool commandApplied = _userSafetyStopCommandZeroVelocity
                ? TiagoLocomotionControlGate.TryApplyAutonomousCommand(_driveBridge, 0f, 0f)
                : TiagoLocomotionControlGate.TryStopAutonomous(_driveBridge);
            _lastCommandLinear = 0f;
            _lastCommandAngular = 0f;
            _lastCommandApplied = commandApplied;
            _frontBlockedSince = float.PositiveInfinity;
            _noProgressSince = float.PositiveInfinity;
            _stallTurnDemandSince = float.PositiveInfinity;
            _stallRecoveryResumeReplanPending = false;

            bool timedOut = _userSafetyStopTimeoutSeconds > 0f && stopElapsed >= _userSafetyStopTimeoutSeconds;
            PublishExperimentTelemetry(
                desiredTarget,
                remainingDistance,
                zeroCommand,
                _driveBridge.LastDiagnostics,
                timedOut ? "UserSafetyBlocked" : "UserSafetyStopped",
                rawFrame.UsesActiveCorner,
                obstacle.FrontOccupancy,
                commandApplied);

            if (timedOut)
            {
                LogUserSafetyStopTimeout(stopElapsed, nearestUser);
                status = NodeStatus.Failure;
                return true;
            }

            status = NodeStatus.Running;
            return true;
        }

        private bool TryFindUserSafetyObstacle(
            UnityVector3 currentPosition,
            PathTrackingFrame trackingFrame,
            out UserSafetyObstacleSnapshot nearestUser)
        {
            nearestUser = UserSafetyObstacleSnapshot.None;
            float radius = _userSafetyStopped ? _userSafetyResumeRadius : _userSafetyStopRadius;
            UnityVector3 segmentStart = Flatten(currentPosition);
            UnityVector3 segmentEnd = trackingFrame.IsValid && _lastActivePathPoints != null && _lastActivePathPoints.Length > 0
                ? SamplePathAtDistance(_lastActivePathPoints, trackingFrame.AlongPathDistance + _userSafetyStopPathLookaheadDistance)
                : trackingFrame.LookaheadPoint;

            if (Vector3.Distance(segmentStart, segmentEnd) < 0.05f)
            {
                segmentEnd = segmentStart + Flatten(_navigationReference.forward).normalized * Mathf.Max(0.05f, _userSafetyStopPathLookaheadDistance);
            }

            UnityVector3 capsuleA = segmentStart + Vector3.up * 0.45f;
            UnityVector3 capsuleB = segmentEnd + Vector3.up * 0.45f;
            int count = Physics.OverlapCapsuleNonAlloc(
                capsuleA,
                capsuleB,
                radius,
                _userSafetyOverlapBuffer,
                ~0,
                QueryTriggerInteraction.Collide);

            bool found = false;
            float bestMetric = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                Collider candidate = _userSafetyOverlapBuffer[i];
                if (candidate == null || IsNavigationReferenceCollider(candidate) || !IsUserOrXrRigCollider(candidate))
                {
                    continue;
                }

                UnityVector3 segmentMid = (segmentStart + segmentEnd) * 0.5f;
                UnityVector3 closestToRobot = Flatten(candidate.ClosestPoint(segmentStart));
                UnityVector3 closestToPathMid = Flatten(candidate.ClosestPoint(segmentMid));
                UnityVector3 closestToPathEnd = Flatten(candidate.ClosestPoint(segmentEnd));
                float distanceToRobot = Vector3.Distance(segmentStart, closestToRobot);
                float distanceToPathSegment = Mathf.Min(
                    DistancePointToSegmentXZ(closestToRobot, segmentStart, segmentEnd),
                    DistancePointToSegmentXZ(closestToPathMid, segmentStart, segmentEnd),
                    DistancePointToSegmentXZ(closestToPathEnd, segmentStart, segmentEnd));
                float metric = Mathf.Min(distanceToRobot, distanceToPathSegment);

                if (metric < bestMetric)
                {
                    bestMetric = metric;
                    nearestUser = new UserSafetyObstacleSnapshot(
                        true,
                        candidate,
                        BuildColliderPath(candidate.transform),
                        distanceToRobot,
                        distanceToPathSegment,
                        LayerMask.LayerToName(candidate.gameObject.layer));
                    found = true;
                }
            }

            if (found)
            {
                LogUserSafetyObstacleDetected(nearestUser);
            }

            return found;
        }

        private bool IsUserOrXrRigCollider(Collider collider)
        {
            if (collider == null || collider.gameObject == null || collider.transform == null)
            {
                return false;
            }

            if (IsNavigationReferenceCollider(collider))
            {
                return false;
            }

            if (_userSafetyStopDetectXRRig)
            {
                string colliderPath = BuildColliderPath(collider.transform);
                string combinedName = $"{collider.name} {collider.gameObject.name} {collider.transform.root?.name} {colliderPath}";
                for (int i = 0; i < _userSafetyStopRootNameContains.Length; i++)
                {
                    string token = _userSafetyStopRootNameContains[i];
                    if (!string.IsNullOrWhiteSpace(token) &&
                        combinedName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }

            string layerName = LayerMask.LayerToName(collider.gameObject.layer);
            if (!string.IsNullOrEmpty(layerName) &&
                (layerName.IndexOf("XR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 layerName.IndexOf("User", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 layerName.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            if (_userSafetyStopUseIgnoreRaycastLayer &&
                collider.gameObject.layer == LayerMask.NameToLayer("Ignore Raycast") &&
                !IsIgnoredClearanceDiagnosticCollider(collider))
            {
                return true;
            }

            return false;
        }

        private static string[] NormalizeUserSafetyNameTokens(string[] configuredTokens)
        {
            if (configuredTokens == null || configuredTokens.Length == 0)
            {
                return new[]
                {
                    "XR Origin",
                    "XR Rig",
                    "Hands",
                    "Hand",
                    "Controller",
                    "Main Camera",
                    "Camera Offset"
                };
            }

            List<string> tokens = new List<string>(configuredTokens.Length + 1);
            for (int i = 0; i < configuredTokens.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(configuredTokens[i]))
                {
                    tokens.Add(configuredTokens[i]);
                }
            }

            if (!tokens.Exists(token => string.Equals(token, "Hand", StringComparison.OrdinalIgnoreCase)))
            {
                tokens.Add("Hand");
            }

            return tokens.Count > 0 ? tokens.ToArray() : NormalizeUserSafetyNameTokens(null);
        }

        private static float DistancePointToSegmentXZ(UnityVector3 point, UnityVector3 segmentStart, UnityVector3 segmentEnd)
        {
            UnityVector3 a = Flatten(segmentStart);
            UnityVector3 b = Flatten(segmentEnd);
            UnityVector3 p = Flatten(point);
            UnityVector3 ab = b - a;
            float sqrLength = ab.sqrMagnitude;
            if (sqrLength <= 0.0001f)
            {
                return Vector3.Distance(p, a);
            }

            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / sqrLength);
            UnityVector3 projected = a + ab * t;
            return Vector3.Distance(p, projected);
        }

        private void ResetUserSafetyStop(string reason)
        {
            if (_userSafetyStopped)
            {
                TiagoExperimentTelemetry.RecordEvent("user_safety_stop_reset", $"reason={reason}");
            }

            _userSafetyStopped = false;
            _userSafetyTimeoutLogged = false;
            _userSafetyStopStartedAt = float.NegativeInfinity;
            _lastUserSafetyWaitingLogTime = float.NegativeInfinity;
        }

        private void LogUserSafetyObstacleDetected(UserSafetyObstacleSnapshot obstacle)
        {
            if (SuppressVerboseAndroidDiagnostics ||
                !obstacle.HasObstacle ||
                Time.time - _lastUserSafetyDetectedLogTime < ActuationDiagnosticIntervalSeconds)
            {
                return;
            }

            _lastUserSafetyDetectedLogTime = Time.time;
            string details =
                $"colliderPath={obstacle.ColliderPath} distanceToRobot={obstacle.DistanceToRobot:F2} " +
                $"distanceToPathSegment={obstacle.DistanceToPathSegment:F2} layer={obstacle.LayerName}";
            Debug.Log($"{LogPrefix} [UserSafetyStop] obstacle_detected | {details}");
            TiagoExperimentTelemetry.RecordEvent("user_safety_obstacle_detected", details);
        }

        private void LogUserSafetyStopEntered(UserSafetyObstacleSnapshot obstacle)
        {
            string details =
                $"colliderPath={obstacle.ColliderPath} distanceToRobot={obstacle.DistanceToRobot:F2} " +
                $"distanceToPathSegment={obstacle.DistanceToPathSegment:F2} layer={obstacle.LayerName} " +
                $"stopRadius={_userSafetyStopRadius:F2} resumeRadius={_userSafetyResumeRadius:F2} pathLookahead={_userSafetyStopPathLookaheadDistance:F2}";
            Debug.LogWarning($"{LogPrefix} [UserSafetyStop] entered | {details}");
            TiagoExperimentTelemetry.RecordEvent("user_safety_stop_entered", details);
        }

        private void LogUserSafetyStopWaiting(float elapsed, UserSafetyObstacleSnapshot obstacle)
        {
            if (SuppressVerboseAndroidDiagnostics ||
                Time.time - _lastUserSafetyWaitingLogTime < ActuationDiagnosticIntervalSeconds)
            {
                return;
            }

            _lastUserSafetyWaitingLogTime = Time.time;
            string details =
                $"elapsed={elapsed:F2} colliderPath={obstacle.ColliderPath} distanceToRobot={obstacle.DistanceToRobot:F2} " +
                $"distanceToPathSegment={obstacle.DistanceToPathSegment:F2} timeout={_userSafetyStopTimeoutSeconds:F2}";
            Debug.Log($"{LogPrefix} [UserSafetyStop] waiting | {details}");
            TiagoExperimentTelemetry.RecordEvent("user_safety_stop_waiting", details);
        }

        private void LogUserSafetyStopExited(float elapsed)
        {
            string details = $"elapsed={elapsed:F2} resumeRadius={_userSafetyResumeRadius:F2} pathLockActive={_hasLockedActivePath}";
            Debug.Log($"{LogPrefix} [UserSafetyStop] exited | {details}");
            TiagoExperimentTelemetry.RecordEvent("user_safety_stop_exited", details);
        }

        private void LogUserSafetyStopTimeout(float elapsed, UserSafetyObstacleSnapshot obstacle)
        {
            string details =
                $"elapsed={elapsed:F2} colliderPath={obstacle.ColliderPath} distanceToRobot={obstacle.DistanceToRobot:F2} " +
                $"distanceToPathSegment={obstacle.DistanceToPathSegment:F2} pathLockActive={_hasLockedActivePath}";
            if (_userSafetyTimeoutLogged)
            {
                return;
            }

            _userSafetyTimeoutLogged = true;
            Debug.LogWarning($"{LogPrefix} [UserSafetyStop] timeout | {details}");
            TiagoExperimentTelemetry.RecordEvent("user_safety_stop_timeout", details);
            TiagoExperimentTelemetry.RecordEvent("navigation_blocked_by_user", details);
        }

        private static float CalculatePathLength(IReadOnlyList<UnityVector3> pathPoints, UnityVector3 projectedPoint, int segmentIndex, float distanceAlongPath)
        {
            if (pathPoints == null || pathPoints.Count <= 1)
            {
                return 0f;
            }

            float length = 0f;
            for (int i = 0; i < pathPoints.Count - 1; i++)
            {
                length += Vector3.Distance(Flatten(pathPoints[i]), Flatten(pathPoints[i + 1]));
            }

            return Mathf.Max(length, distanceAlongPath);
        }

        private PathClearanceOffsetResult BuildClearanceOffsetPathPoints(IReadOnlyList<UnityVector3> inputPoints)
        {
            int inputCount = inputPoints?.Count ?? 0;
            if (inputCount == 0)
            {
                return PathClearanceOffsetResult.Failed("no_data", 0, 0);
            }

            bool shouldLog = !SuppressVerboseAndroidDiagnostics &&
                Time.time - _lastPathClearanceOffsetLogTime >= ActuationDiagnosticIntervalSeconds;
            if (shouldLog)
            {
                _lastPathClearanceOffsetLogTime = Time.time;
                Debug.Log($"{LogPrefix} [PathClearanceOffset] start | inputPoints={inputCount}");
                TiagoExperimentTelemetry.RecordEvent("path_clearance_offset_start", $"inputPoints={inputCount}");
            }

            UnityVector3[] result = CopyPoints(inputPoints);
            if (inputCount <= 2)
            {
                LogPathClearanceOffsetComplete(shouldLog, inputCount, result.Length, 0, 0, 0, inputCount, 0, 0, 0, 0, float.NaN, float.NaN, float.NaN, float.NaN, 0f, "too_few_points");
                return new PathClearanceOffsetResult(result, false, 0, inputCount, 0, 0, 0, 0, 0, 0, float.NaN, float.NaN, float.NaN, float.NaN, 0f, "too_few_points");
            }

            MeasurePathEdgeClearance(inputPoints, out float originalMinClearance, out float originalMeanClearance);
            int adjusted = 0;
            int kept = 0;
            int eligiblePoints = 0;
            int skippedBySufficientClearance = 0;
            int rejectedBySample = 0;
            int rejectedByRaycast = 0;
            int revertedByMaxDeviation = 0;
            int revertedByArtificialCurvature = 0;
            float maxDeviationApplied = 0f;
            bool[] adjustedMask = new bool[inputCount];
            float previousSignedOffset = 0f;
            int startIndex = _pathClearanceOffsetSkipEndpoints ? 1 : 0;
            int endIndex = _pathClearanceOffsetSkipEndpoints ? inputCount - 2 : inputCount - 1;

            for (int i = startIndex; i <= endIndex; i++)
            {
                UnityVector3 previous = i > 0 ? Flatten(result[i - 1]) : Flatten(inputPoints[i]);
                UnityVector3 current = Flatten(inputPoints[i]);
                UnityVector3 next = i < inputCount - 1 ? Flatten(inputPoints[i + 1]) : current;
                if (Vector3.Distance(previous, current) < _pathClearanceOffsetMinPointSpacing ||
                    Vector3.Distance(current, next) < _pathClearanceOffsetMinPointSpacing)
                {
                    kept++;
                    continue;
                }

                UnityVector3 tangent = SafeDirection(next - previous, Vector3.zero);
                if (tangent.sqrMagnitude <= 0.0001f)
                {
                    kept++;
                    continue;
                }

                UnityVector3 normal = Vector3.Cross(Vector3.up, tangent).normalized;
                ClearanceOffsetCandidate best = EvaluateClearanceOffsetCandidate(previous, current, next, current, 0f, 0f, previousSignedOffset);
                if (!best.SampleValid)
                {
                    kept++;
                    rejectedBySample++;
                    continue;
                }

                float originalClearance = best.Clearance;
                if (_pathClearanceOffsetOnlyIfBelowMinEdgeDistance && originalClearance >= _pathClearanceOffsetMinEdgeDistance)
                {
                    skippedBySufficientClearance++;
                    kept++;
                    previousSignedOffset *= 0.5f;
                    continue;
                }

                eligiblePoints++;
                int maxSteps = Mathf.Min(
                    _pathClearanceOffsetMaxCandidatesPerSide,
                    Mathf.FloorToInt(_pathClearanceOffsetSearchRadius / _pathClearanceOffsetStep));

                for (int step = 1; step <= maxSteps; step++)
                {
                    float distance = step * _pathClearanceOffsetStep;
                    if (distance > _pathClearanceOffsetSearchRadius + 0.001f)
                    {
                        break;
                    }

                    TrySelectClearanceOffsetCandidate(previous, current, next, normal, distance, previousSignedOffset, ref best, ref rejectedBySample, ref rejectedByRaycast);
                    TrySelectClearanceOffsetCandidate(previous, current, next, normal, -distance, previousSignedOffset, ref best, ref rejectedBySample, ref rejectedByRaycast);
                }

                bool improves = best.SampleValid && best.SegmentValid && best.Clearance > originalClearance + 0.025f;
                bool reachesMinimumOrMeaningful = best.Clearance >= _pathClearanceOffsetMinEdgeDistance || best.Clearance > originalClearance + 0.06f;
                if (improves && reachesMinimumOrMeaningful && best.OffsetDistance > 0.001f)
                {
                    result[i] = best.Point;
                    previousSignedOffset = best.SignedOffset;
                    adjustedMask[i] = true;
                    maxDeviationApplied = Mathf.Max(maxDeviationApplied, best.OffsetDistance);
                    adjusted++;
                    LogPathClearanceOffsetPointAdjusted(false, i, originalClearance, best.Clearance, best.OffsetDistance, best.SignedOffset);
                }
                else
                {
                    kept++;
                    previousSignedOffset *= 0.5f;
                }
            }

            ApplyClearanceOffsetPostFilter(inputPoints, result, adjustedMask, ref adjusted, ref kept, ref revertedByMaxDeviation, ref revertedByArtificialCurvature);
            for (int i = 0; i < adjustedMask.Length; i++)
            {
                if (adjustedMask[i])
                {
                    maxDeviationApplied = Mathf.Max(maxDeviationApplied, Vector3.Distance(Flatten(inputPoints[i]), Flatten(result[i])));
                }
            }

            MeasurePathEdgeClearance(result, out float finalMinClearance, out float finalMeanClearance);
            LogPathClearanceOffsetComplete(
                shouldLog,
                inputCount,
                result.Length,
                eligiblePoints,
                skippedBySufficientClearance,
                adjusted,
                kept,
                rejectedBySample,
                rejectedByRaycast,
                revertedByMaxDeviation,
                revertedByArtificialCurvature,
                originalMinClearance,
                finalMinClearance,
                originalMeanClearance,
                finalMeanClearance,
                maxDeviationApplied,
                adjusted > 0 ? "none" : "no_safe_improvement");
            return new PathClearanceOffsetResult(
                result,
                adjusted > 0,
                adjusted,
                kept,
                eligiblePoints,
                skippedBySufficientClearance,
                rejectedBySample,
                rejectedByRaycast,
                revertedByMaxDeviation,
                revertedByArtificialCurvature,
                originalMinClearance,
                finalMinClearance,
                originalMeanClearance,
                finalMeanClearance,
                maxDeviationApplied,
                adjusted > 0 ? "none" : "no_safe_improvement");
        }

        private void TrySelectClearanceOffsetCandidate(
            UnityVector3 previous,
            UnityVector3 current,
            UnityVector3 next,
            UnityVector3 normal,
            float signedOffset,
            float previousSignedOffset,
            ref ClearanceOffsetCandidate best,
            ref int rejectedBySample,
            ref int rejectedByRaycast)
        {
            UnityVector3 candidatePoint = current + normal * signedOffset;
            ClearanceOffsetCandidate candidate = EvaluateClearanceOffsetCandidate(previous, current, next, candidatePoint, signedOffset, Mathf.Abs(signedOffset), previousSignedOffset);
            if (!candidate.SampleValid)
            {
                rejectedBySample++;
                return;
            }

            if (!candidate.SegmentValid)
            {
                rejectedByRaycast++;
                return;
            }

            if (candidate.OffsetDistance > _pathClearanceOffsetMaxDeviationFromCenteredPath + 0.001f)
            {
                return;
            }

            if (!best.SampleValid || !best.SegmentValid || IsBetterClearanceOffsetCandidate(candidate, best))
            {
                best = candidate;
            }
        }

        private bool IsBetterClearanceOffsetCandidate(ClearanceOffsetCandidate candidate, ClearanceOffsetCandidate best)
        {
            bool candidateMeetsMinimum = candidate.Clearance >= _pathClearanceOffsetMinEdgeDistance;
            bool bestMeetsMinimum = best.Clearance >= _pathClearanceOffsetMinEdgeDistance;
            if (candidateMeetsMinimum && !bestMeetsMinimum)
            {
                return true;
            }

            if (candidateMeetsMinimum && bestMeetsMinimum)
            {
                if (candidate.OffsetDistance < best.OffsetDistance - 0.001f)
                {
                    return true;
                }

                return Mathf.Abs(candidate.OffsetDistance - best.OffsetDistance) <= 0.001f &&
                    candidate.Score > best.Score + 0.001f;
            }

            return candidate.Score > best.Score + 0.001f;
        }

        private ClearanceOffsetCandidate EvaluateClearanceOffsetCandidate(
            UnityVector3 previous,
            UnityVector3 original,
            UnityVector3 next,
            UnityVector3 candidatePoint,
            float signedOffset,
            float offsetDistance,
            float previousSignedOffset)
        {
            if (!NavMesh.SamplePosition(candidatePoint, out NavMeshHit sampleHit, _pathClearanceOffsetNavMeshSampleDistance, NavMesh.AllAreas))
            {
                return ClearanceOffsetCandidate.RejectedSample;
            }

            if (Vector3.Distance(candidatePoint, original) > _pathClearanceOffsetMaxDeviationFromCenteredPath + 0.001f)
            {
                return ClearanceOffsetCandidate.RejectedSample;
            }

            UnityVector3 sampledPoint = Flatten(sampleHit.position);
            if (Vector3.Distance(candidatePoint, sampledPoint) > _pathClearanceOffsetNavMeshSampleDistance + 0.001f)
            {
                return ClearanceOffsetCandidate.RejectedSample;
            }

            if (!NavMesh.FindClosestEdge(sampledPoint, out NavMeshHit edgeHit, NavMesh.AllAreas))
            {
                return ClearanceOffsetCandidate.RejectedSample;
            }

            bool segmentValid = true;
            if (_pathClearanceOffsetValidateSegments)
            {
                segmentValid =
                    !NavMesh.Raycast(previous, sampledPoint, out _, NavMesh.AllAreas) &&
                    !NavMesh.Raycast(sampledPoint, next, out _, NavMesh.AllAreas);
            }

            float continuityPenalty = Mathf.Abs(signedOffset - previousSignedOffset) * 0.20f;
            float distancePenalty = offsetDistance * 0.05f;
            float score = edgeHit.distance - continuityPenalty - distancePenalty;
            return new ClearanceOffsetCandidate(sampledPoint, signedOffset, offsetDistance, edgeHit.distance, score, true, segmentValid);
        }

        private void ApplyClearanceOffsetPostFilter(
            IReadOnlyList<UnityVector3> originalPoints,
            UnityVector3[] adjustedPoints,
            bool[] adjustedMask,
            ref int adjusted,
            ref int kept,
            ref int revertedByMaxDeviation,
            ref int revertedByArtificialCurvature)
        {
            if (originalPoints == null || adjustedPoints == null || adjustedMask == null)
            {
                return;
            }

            int count = Mathf.Min(originalPoints.Count, adjustedPoints.Length, adjustedMask.Length);
            for (int i = 1; i < count - 1; i++)
            {
                if (!adjustedMask[i])
                {
                    continue;
                }

                UnityVector3 original = Flatten(originalPoints[i]);
                float deviation = Vector3.Distance(original, Flatten(adjustedPoints[i]));
                if (deviation > _pathClearanceOffsetMaxDeviationFromCenteredPath + 0.001f)
                {
                    RevertClearanceOffsetPoint(originalPoints, adjustedPoints, adjustedMask, i, ref adjusted, ref kept);
                    revertedByMaxDeviation++;
                    continue;
                }

                float originalAngle = ComputeLocalPathAngle(originalPoints, i);
                float adjustedAngle = ComputeLocalPathAngle(adjustedPoints, i);
                if (originalAngle < 8f && adjustedAngle > _pathClearanceOffsetMaxLocalHeadingChangeDeg)
                {
                    RevertClearanceOffsetPoint(originalPoints, adjustedPoints, adjustedMask, i, ref adjusted, ref kept);
                    revertedByArtificialCurvature++;
                }
            }
        }

        private static void RevertClearanceOffsetPoint(
            IReadOnlyList<UnityVector3> originalPoints,
            UnityVector3[] adjustedPoints,
            bool[] adjustedMask,
            int index,
            ref int adjusted,
            ref int kept)
        {
            adjustedPoints[index] = Flatten(originalPoints[index]);
            adjustedMask[index] = false;
            adjusted = Mathf.Max(0, adjusted - 1);
            kept++;
        }

        private static float ComputeLocalPathAngle(IReadOnlyList<UnityVector3> points, int index)
        {
            if (points == null || index <= 0 || index >= points.Count - 1)
            {
                return 0f;
            }

            UnityVector3 previous = Flatten(points[index - 1]);
            UnityVector3 current = Flatten(points[index]);
            UnityVector3 next = Flatten(points[index + 1]);
            UnityVector3 inDirection = SafeDirection(current - previous, Vector3.zero);
            UnityVector3 outDirection = SafeDirection(next - current, Vector3.zero);
            if (inDirection.sqrMagnitude <= 0.0001f || outDirection.sqrMagnitude <= 0.0001f)
            {
                return 0f;
            }

            return Vector3.Angle(inDirection, outDirection);
        }

        private void MeasurePathEdgeClearance(IReadOnlyList<UnityVector3> points, out float minClearance, out float meanClearance)
        {
            minClearance = float.PositiveInfinity;
            meanClearance = float.NaN;
            if (points == null || points.Count == 0)
            {
                minClearance = float.NaN;
                return;
            }

            float sum = 0f;
            int samples = 0;
            for (int i = 0; i < points.Count; i++)
            {
                UnityVector3 point = Flatten(points[i]);
                if (NavMesh.FindClosestEdge(point, out NavMeshHit edgeHit, NavMesh.AllAreas))
                {
                    float clearance = Mathf.Max(0f, edgeHit.distance);
                    minClearance = Mathf.Min(minClearance, clearance);
                    sum += clearance;
                    samples++;
                }
            }

            if (samples > 0)
            {
                meanClearance = sum / samples;
            }
            else
            {
                minClearance = float.NaN;
            }
        }

        private PathCornerSmoothingResult BuildSmoothedPathPoints(IReadOnlyList<UnityVector3> centeredPoints, int originalPointCount)
        {
            int centeredCount = centeredPoints?.Count ?? 0;
            if (centeredCount == 0)
            {
                return PathCornerSmoothingResult.Failed("no_data", 0, 0, originalPointCount, centeredCount);
            }

            bool shouldLog = !SuppressVerboseAndroidDiagnostics &&
                Time.time - _lastPathCornerSmoothingLogTime >= ActuationDiagnosticIntervalSeconds;
            if (shouldLog)
            {
                _lastPathCornerSmoothingLogTime = Time.time;
                Debug.Log($"{LogPrefix} [PathCornerSmoothing] start | originalPoints={originalPointCount} | centeredPoints={centeredCount}");
                TiagoExperimentTelemetry.RecordEvent(
                    "path_corner_smoothing_start",
                    $"originalPoints={originalPointCount} centeredPoints={centeredCount}");
            }

            if (centeredCount <= 2)
            {
                UnityVector3[] copied = CopyPoints(centeredPoints);
                LogPathCornerSmoothingComplete(shouldLog, originalPointCount, centeredCount, copied.Length, 0, 0, 0, 0, 0, 0, "too_few_points");
                return new PathCornerSmoothingResult(copied, false, 0, 0, 0, 0, 0, 0, originalPointCount, centeredCount, "too_few_points");
            }

            var result = new List<UnityVector3>(centeredCount);
            AddPathPoint(result, Flatten(centeredPoints[0]));
            int cornersConsidered = 0;
            int smoothedVertices = 0;
            int keptByAngle = 0;
            int keptByShortSegment = 0;
            int rejectedByNavMesh = 0;
            int rejectedBySegmentRaycast = 0;

            for (int i = 1; i < centeredCount - 1; i++)
            {
                UnityVector3 previous = Flatten(centeredPoints[i - 1]);
                UnityVector3 corner = Flatten(centeredPoints[i]);
                UnityVector3 next = Flatten(centeredPoints[i + 1]);
                cornersConsidered++;

                if (TrySmoothCorner(previous, corner, next, out List<UnityVector3> cornerPoints, out CornerSmoothingVertexReport report))
                {
                    for (int j = 0; j < cornerPoints.Count; j++)
                    {
                        AddPathPoint(result, cornerPoints[j]);
                    }

                    smoothedVertices++;
                    LogPathCornerSmoothingVertex(shouldLog, i, "smoothed", report, cornerPoints.Count);
                }
                else
                {
                    AddPathPoint(result, corner);
                    if (report.RejectReason == "below_angle_threshold")
                    {
                        keptByAngle++;
                    }
                    else if (report.RejectReason == "short_segment")
                    {
                        keptByShortSegment++;
                    }
                    else if (report.RejectReason == "navmesh_sample_failed")
                    {
                        rejectedByNavMesh++;
                    }
                    else if (report.RejectReason == "navmesh_segment_failed")
                    {
                        rejectedBySegmentRaycast++;
                    }

                    LogPathCornerSmoothingVertex(shouldLog, i, "kept", report, 1);
                }
            }

            AddPathPoint(result, Flatten(centeredPoints[centeredCount - 1]));
            UnityVector3[] smoothed = result.ToArray();
            string summaryReason = smoothedVertices > 0 ? "none" : "no_corners_smoothed";
            LogPathCornerSmoothingComplete(
                shouldLog,
                originalPointCount,
                centeredCount,
                smoothed.Length,
                cornersConsidered,
                smoothedVertices,
                keptByAngle,
                keptByShortSegment,
                rejectedByNavMesh,
                rejectedBySegmentRaycast,
                summaryReason);
            return new PathCornerSmoothingResult(
                smoothed,
                smoothedVertices > 0,
                smoothedVertices,
                cornersConsidered,
                keptByAngle,
                keptByShortSegment,
                rejectedByNavMesh,
                rejectedBySegmentRaycast,
                originalPointCount,
                centeredCount,
                summaryReason);
        }

        private bool TrySmoothCorner(
            UnityVector3 previous,
            UnityVector3 corner,
            UnityVector3 next,
            out List<UnityVector3> smoothedPoints,
            out CornerSmoothingVertexReport report)
        {
            smoothedPoints = null;
            report = CornerSmoothingVertexReport.Kept("none", 0f, 0f, 0f, 0f, 0f, 0);

            float previousLength = Vector3.Distance(previous, corner);
            float nextLength = Vector3.Distance(corner, next);
            if (previousLength < _cornerSmoothingMinSegmentLength || nextLength < _cornerSmoothingMinSegmentLength)
            {
                report = CornerSmoothingVertexReport.Kept("short_segment", 0f, previousLength, nextLength, 0f, float.NaN, 0);
                return false;
            }

            UnityVector3 incoming = SafeDirection(corner - previous, Vector3.zero);
            UnityVector3 outgoing = SafeDirection(next - corner, Vector3.zero);
            if (incoming.sqrMagnitude <= 0.0001f || outgoing.sqrMagnitude <= 0.0001f)
            {
                report = CornerSmoothingVertexReport.Kept("no_direction", 0f, previousLength, nextLength, 0f, float.NaN, 0);
                return false;
            }

            float turnAngle = Vector3.Angle(incoming, outgoing);
            if (turnAngle < _cornerSmoothingAngleThresholdDeg)
            {
                report = CornerSmoothingVertexReport.Kept("below_angle_threshold", turnAngle, previousLength, nextLength, 0f, float.NaN, 0);
                return false;
            }

            float trimDistance = Mathf.Min(
                _cornerSmoothingRadius,
                previousLength * PathCornerSmoothingMaxTrimSegmentFraction,
                nextLength * PathCornerSmoothingMaxTrimSegmentFraction);
            if (trimDistance < PathCornerSmoothingDuplicateDistance * 2f)
            {
                report = CornerSmoothingVertexReport.Kept("trim_too_small", turnAngle, previousLength, nextLength, 0f, float.NaN, 0);
                return false;
            }

            UnityVector3 entry = corner - incoming * trimDistance;
            UnityVector3 exit = corner + outgoing * trimDistance;
            if (!BuildFilletCandidate(previous, entry, corner, exit, next, out List<UnityVector3> candidates, out string rejectReason))
            {
                report = CornerSmoothingVertexReport.Kept(rejectReason, turnAngle, previousLength, nextLength, 0f, trimDistance, 0);
                return false;
            }

            smoothedPoints = candidates;
            report = CornerSmoothingVertexReport.CreateSmoothed(
                turnAngle,
                previousLength,
                nextLength,
                0f,
                trimDistance,
                float.NaN,
                0);
            return true;
        }

        private bool BuildFilletCandidate(
            UnityVector3 previous,
            UnityVector3 entry,
            UnityVector3 control,
            UnityVector3 exit,
            UnityVector3 next,
            out List<UnityVector3> candidates,
            out string rejectReason)
        {
            candidates = new List<UnityVector3>(_cornerSmoothingSamplesPerCorner + 1);
            rejectReason = "none";
            int sampleCount = Mathf.Max(2, _cornerSmoothingSamplesPerCorner);
            for (int sample = 0; sample <= sampleCount; sample++)
            {
                float t = sample / (float)sampleCount;
                UnityVector3 candidate = EvaluateQuadraticBezier(entry, control, exit, t);
                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, _cornerSmoothingNavMeshSampleDistance, NavMesh.AllAreas))
                {
                    rejectReason = "navmesh_sample_failed";
                    return false;
                }

                UnityVector3 sampled = Flatten(hit.position);
                if (Vector3.Distance(candidate, sampled) > _cornerSmoothingNavMeshSampleDistance + 0.001f)
                {
                    rejectReason = "navmesh_sample_failed";
                    return false;
                }

                AddPathPoint(candidates, sampled);
            }

            if (_cornerSmoothingValidateSegments && !ValidateSmoothedCornerSegments(previous, candidates, next))
            {
                rejectReason = "navmesh_segment_failed";
                return false;
            }

            return true;
        }

        private bool ValidateSmoothedCornerSegments(UnityVector3 previous, IReadOnlyList<UnityVector3> candidates, UnityVector3 next)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return false;
            }

            UnityVector3 segmentStart = previous;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (NavMesh.Raycast(segmentStart, candidates[i], out _, NavMesh.AllAreas))
                {
                    return false;
                }

                segmentStart = candidates[i];
            }

            return !NavMesh.Raycast(segmentStart, next, out _, NavMesh.AllAreas);
        }

        private static UnityVector3 EvaluateQuadraticBezier(UnityVector3 a, UnityVector3 control, UnityVector3 b, float t)
        {
            float clampedT = Mathf.Clamp01(t);
            float oneMinusT = 1f - clampedT;
            return oneMinusT * oneMinusT * a + 2f * oneMinusT * clampedT * control + clampedT * clampedT * b;
        }

        private static void AddPathPoint(List<UnityVector3> points, UnityVector3 point)
        {
            if (points == null)
            {
                return;
            }

            UnityVector3 flattened = Flatten(point);
            if (points.Count > 0 && Vector3.Distance(points[points.Count - 1], flattened) < PathCornerSmoothingDuplicateDistance)
            {
                return;
            }

            points.Add(flattened);
        }

        private PathCenteringResult BuildCenteredPathPoints(IReadOnlyList<UnityVector3> originalPoints)
        {
            if (originalPoints == null || originalPoints.Count == 0)
            {
                return PathCenteringResult.Failed("no_data", 0, 0);
            }

            if (TryGetCachedCenteredPath(originalPoints, out UnityVector3[] cachedPath))
            {
                return new PathCenteringResult(cachedPath, true, adjusted: 1, kept: Mathf.Max(0, cachedPath.Length - 1), averageShift: 0f, maxShift: 0f, failureReason: "cached");
            }

            UnityVector3[] densifiedPoints = DensifyPathPoints(originalPoints, PathCenteringDensifySpacing);
            bool shouldLog = !SuppressVerboseAndroidDiagnostics &&
                Time.time - _lastPathCenteringLogTime >= ActuationDiagnosticIntervalSeconds;
            if (shouldLog)
            {
                _lastPathCenteringLogTime = Time.time;
                Debug.Log($"{LogPrefix} [PathCentering] start | originalPoints={originalPoints.Count} | densifiedPoints={densifiedPoints.Length}");
                TiagoExperimentTelemetry.RecordEvent(
                    "path_centering_start",
                    $"originalPoints={originalPoints.Count} densifiedPoints={densifiedPoints.Length}");
            }

            if (densifiedPoints.Length <= 2)
            {
                LogPathCenteringComplete(shouldLog, adjusted: 0, kept: densifiedPoints.Length, centeredPoints: densifiedPoints.Length);
                LogPathCenteringDisplacementSummary(shouldLog, 0f, 0f, 0, densifiedPoints.Length, "too_few_points");
                return new PathCenteringResult(densifiedPoints, false, 0, densifiedPoints.Length, 0f, 0f, "too_few_points");
            }

            UnityVector3[] centeredPoints = CopyPoints(densifiedPoints);
            int adjusted = 0;
            int kept = 0;
            float totalShift = 0f;
            float maxShift = 0f;
            var keepReasons = new Dictionary<string, int>();

            for (int i = 1; i < densifiedPoints.Length - 1; i++)
            {
                UnityVector3 waypoint = Flatten(densifiedPoints[i]);
                UnityVector3 localDirection = SafeDirection(densifiedPoints[i + 1] - densifiedPoints[i - 1], Vector3.zero);
                if (localDirection.sqrMagnitude <= 0.0001f)
                {
                    kept++;
                    CountKeepReason(keepReasons, "no_direction");
                    LogPathCenteringWaypointKept(shouldLog, i, "no_direction");
                    continue;
                }

                UnityVector3 lateralRight = Vector3.Cross(Vector3.up, localDirection).normalized;
                bool hasLeft = TryMeasureLateralClearance(waypoint, localDirection, -lateralRight, out float leftClearance);
                bool hasRight = TryMeasureLateralClearance(waypoint, localDirection, lateralRight, out float rightClearance);
                if (!hasLeft && !hasRight)
                {
                    kept++;
                    CountKeepReason(keepReasons, "no_data");
                    LogPathCenteringWaypointKept(shouldLog, i, "no_data");
                    continue;
                }

                float effectiveLeft = hasLeft ? leftClearance : PathCenteringProbeDistance;
                float effectiveRight = hasRight ? rightClearance : PathCenteringProbeDistance;
                float desiredShift = Mathf.Clamp((effectiveRight - effectiveLeft) * 0.5f, -PathCenteringMaxShift, PathCenteringMaxShift);
                if (Mathf.Abs(desiredShift) <= 0.001f)
                {
                    kept++;
                    CountKeepReason(keepReasons, "balanced");
                    LogPathCenteringWaypointKept(shouldLog, i, "balanced");
                    continue;
                }

                float projectedLeftClearance = effectiveLeft + desiredShift;
                float projectedRightClearance = effectiveRight - desiredShift;
                if (Mathf.Min(projectedLeftClearance, projectedRightClearance) < PathCenteringMinClearance)
                {
                    kept++;
                    CountKeepReason(keepReasons, "no_clearance");
                    LogPathCenteringWaypointKept(shouldLog, i, "no_clearance");
                    continue;
                }

                UnityVector3 shiftedWaypoint = waypoint + lateralRight * desiredShift;
                if (!NavMesh.SamplePosition(shiftedWaypoint, out NavMeshHit sampledHit, PathCenteringSampleRadius, NavMesh.AllAreas))
                {
                    kept++;
                    CountKeepReason(keepReasons, "sample_not_on_navmesh");
                    LogPathCenteringWaypointKept(shouldLog, i, "sample_not_on_navmesh");
                    continue;
                }

                UnityVector3 sampledPoint = Flatten(sampledHit.position);
                if (Vector3.Distance(waypoint, sampledPoint) > PathCenteringMaxShift + 0.05f)
                {
                    kept++;
                    CountKeepReason(keepReasons, "invalid_sample");
                    LogPathCenteringWaypointKept(shouldLog, i, "invalid_sample");
                    continue;
                }

                centeredPoints[i] = sampledPoint;
                adjusted++;
                float actualShift = Vector3.Distance(waypoint, sampledPoint);
                totalShift += actualShift;
                maxShift = Mathf.Max(maxShift, actualShift);
                LogPathCenteringWaypointAdjusted(shouldLog, i, effectiveLeft, effectiveRight, desiredShift);
            }

            kept += 2;
            LogPathCenteringComplete(shouldLog, adjusted, kept, centeredPoints.Length);
            float avgShift = adjusted > 0 ? totalShift / adjusted : 0f;
            string failureReason = adjusted > 0 && maxShift > 0.01f ? "none" : ResolvePathCenteringFailureReason(keepReasons);
            LogPathCenteringDisplacementSummary(shouldLog, avgShift, maxShift, adjusted, kept, failureReason);
            bool isValid = adjusted > 0 && maxShift > 0.01f;
            if (isValid)
            {
                StoreCenteredPathCache(originalPoints, centeredPoints);
            }

            return new PathCenteringResult(centeredPoints, isValid, adjusted, kept, avgShift, maxShift, failureReason);
        }

        private bool TryGetCachedCenteredPath(IReadOnlyList<UnityVector3> originalPoints, out UnityVector3[] cachedPath)
        {
            cachedPath = null;
            if (_cachedPathCenteringResult == null ||
                _cachedPathCenteringResult.Length == 0 ||
                !PathMatchesCache(originalPoints))
            {
                return false;
            }

            cachedPath = CopyPoints(_cachedPathCenteringResult);
            return true;
        }

        private bool PathMatchesCache(IReadOnlyList<UnityVector3> originalPoints)
        {
            if (originalPoints == null || originalPoints.Count == 0)
            {
                return false;
            }

            if (_cachedPathCenteringSourceTail == null || _cachedPathCenteringSourceTail.Length != originalPoints.Count)
            {
                return false;
            }

            for (int i = 0; i < originalPoints.Count; i++)
            {
                if (Vector3.Distance(Flatten(originalPoints[i]), _cachedPathCenteringSourceTail[i]) > 0.05f)
                {
                    return false;
                }
            }

            return true;
        }

        private void StoreCenteredPathCache(IReadOnlyList<UnityVector3> originalPoints, IReadOnlyList<UnityVector3> centeredPoints)
        {
            _cachedPathCenteringSourceTail = new UnityVector3[originalPoints.Count];
            for (int i = 0; i < originalPoints.Count; i++)
            {
                _cachedPathCenteringSourceTail[i] = Flatten(originalPoints[i]);
            }

            _cachedPathCenteringResult = CopyPoints(centeredPoints);
        }

        private void ClearPathCenteringCache()
        {
            _cachedPathCenteringSourceTail = new UnityVector3[0];
            _cachedPathCenteringResult = new UnityVector3[0];
        }

        private UnityVector3[] DensifyPathPoints(IReadOnlyList<UnityVector3> points, float spacing)
        {
            if (points == null || points.Count == 0)
            {
                return new UnityVector3[0];
            }

            var result = new List<UnityVector3>(points.Count);
            result.Add(Flatten(points[0]));

            for (int i = 0; i < points.Count - 1; i++)
            {
                UnityVector3 a = Flatten(points[i]);
                UnityVector3 b = Flatten(points[i + 1]);
                float segmentLength = Vector3.Distance(a, b);
                if (segmentLength <= 0.001f)
                {
                    continue;
                }

                int insertedPoints = Mathf.Max(0, Mathf.FloorToInt(segmentLength / Mathf.Max(0.05f, spacing)));
                for (int j = 1; j <= insertedPoints; j++)
                {
                    float t = Mathf.Clamp01((spacing * j) / segmentLength);
                    if (t < 0.999f)
                    {
                        result.Add(Vector3.Lerp(a, b, t));
                    }
                }

                result.Add(b);
            }

            return result.ToArray();
        }

        private bool TryMeasureLateralClearance(
            UnityVector3 waypoint,
            UnityVector3 localDirection,
            UnityVector3 lateralDirection,
            out float clearance)
        {
            clearance = PathCenteringProbeDistance;
            bool hitAny = false;
            float minHitDistance = PathCenteringProbeDistance;
            int sampleCount = Mathf.Max(1, PathCenteringSamplesPerSide);
            float sampleSpan = PathCenteringProbeRadius * 2f;

            for (int i = 0; i < sampleCount; i++)
            {
                float sampleT = sampleCount == 1 ? 0.5f : i / (float)(sampleCount - 1);
                float longitudinalOffset = Mathf.Lerp(-sampleSpan, sampleSpan, sampleT);
                UnityVector3 origin = waypoint + localDirection * longitudinalOffset + Vector3.up * PathCenteringHeightOffset;
                if (Physics.SphereCast(
                    origin,
                    PathCenteringProbeRadius,
                    lateralDirection,
                    out RaycastHit hit,
                    PathCenteringProbeDistance,
                    _obstacleClearanceMask,
                    QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider != null &&
                        (IsNavigationReferenceCollider(hit.collider) || IsUserOrXrRigCollider(hit.collider)))
                    {
                        continue;
                    }

                    hitAny = true;
                    minHitDistance = Mathf.Min(minHitDistance, hit.distance);
                }
            }

            clearance = minHitDistance;
            return hitAny;
        }

        private void LogPathCenteringWaypointAdjusted(bool shouldLog, int index, float leftClearance, float rightClearance, float shift)
        {
            if (!shouldLog || SuppressVerboseAndroidDiagnostics)
            {
                return;
            }

            Debug.Log($"{LogPrefix} [PathCentering] waypoint_adjusted | index={index} | leftClearance={leftClearance:F2} | rightClearance={rightClearance:F2} | shift={shift:F2}");
            TiagoExperimentTelemetry.RecordEvent(
                "path_centering_waypoint_adjusted",
                $"index={index} leftClearance={leftClearance:F2} rightClearance={rightClearance:F2} shift={shift:F2}");
        }

        private void LogPathCenteringWaypointKept(bool shouldLog, int index, string reason)
        {
            if (!shouldLog || SuppressVerboseAndroidDiagnostics)
            {
                return;
            }

            Debug.Log($"{LogPrefix} [PathCentering] waypoint_kept | index={index} | reason={reason}");
            TiagoExperimentTelemetry.RecordEvent(
                "path_centering_waypoint_kept",
                $"index={index} reason={reason}");
        }

        private static void CountKeepReason(Dictionary<string, int> keepReasons, string reason)
        {
            if (keepReasons == null)
            {
                return;
            }

            keepReasons.TryGetValue(reason, out int count);
            keepReasons[reason] = count + 1;
        }

        private static string ResolvePathCenteringFailureReason(Dictionary<string, int> keepReasons)
        {
            if (keepReasons == null || keepReasons.Count == 0)
            {
                return "no_data";
            }

            string bestReason = "no_data";
            int bestCount = -1;
            foreach (KeyValuePair<string, int> pair in keepReasons)
            {
                if (pair.Value > bestCount)
                {
                    bestReason = pair.Key;
                    bestCount = pair.Value;
                }
            }

            return bestReason;
        }

        private void LogPathCenteringComplete(bool shouldLog, int adjusted, int kept, int centeredPoints)
        {
            if (!shouldLog || SuppressVerboseAndroidDiagnostics)
            {
                return;
            }

            Debug.Log($"{LogPrefix} [PathCentering] complete | adjusted={adjusted} | kept={kept} | centeredPoints={centeredPoints}");
            TiagoExperimentTelemetry.RecordEvent(
                "path_centering_complete",
                $"adjusted={adjusted} kept={kept} centeredPoints={centeredPoints}");
        }

        private void LogPathCenteringDisplacementSummary(
            bool shouldLog,
            float averageShift,
            float maxShift,
            int adjusted,
            int kept,
            string dominantReason)
        {
            if (!shouldLog || SuppressVerboseAndroidDiagnostics)
            {
                return;
            }

            Debug.Log($"{LogPrefix} [PathCentering] displacement_summary | avgShift={averageShift:F3} | maxShift={maxShift:F3} | adjusted={adjusted} | kept={kept} | dominantKeptReason={dominantReason}");
            TiagoExperimentTelemetry.RecordEvent(
                "path_centering_displacement_summary",
                $"avgShift={averageShift:F3} maxShift={maxShift:F3} adjusted={adjusted} kept={kept} dominantKeptReason={dominantReason}");
        }

        private void LogPathSelection(
            ActivePathSource source,
            IReadOnlyList<UnityVector3> originalPoints,
            IReadOnlyList<UnityVector3> centeredPoints,
            IReadOnlyList<UnityVector3> smoothedPoints,
            IReadOnlyList<UnityVector3> activePoints)
        {
            if (SuppressVerboseAndroidDiagnostics)
            {
                return;
            }

            UnityVector3 firstActive = activePoints != null && activePoints.Count > 0 ? activePoints[0] : Vector3.zero;
            UnityVector3 nextActive = activePoints != null && activePoints.Count > 1 ? activePoints[1] : firstActive;
            string details =
                $"source={source} | originalPoints={(originalPoints?.Count ?? 0)} | centeredPoints={(centeredPoints?.Count ?? 0)} | smoothedPoints={(smoothedPoints?.Count ?? 0)} | activePoints={(activePoints?.Count ?? 0)} | firstActive={FormatVector(firstActive)} | nextActive={FormatVector(nextActive)}";
            Debug.Log($"{LogPrefix} [PathSelection] active_path | {details}");
            TiagoExperimentTelemetry.RecordEvent("path_selection_active_path", details);
        }

        private void LogPathCornerSmoothingVertex(bool shouldLog, int index, string action, CornerSmoothingVertexReport report, int generatedPoints)
        {
            if (!shouldLog || SuppressVerboseAndroidDiagnostics)
            {
                return;
            }

            string details =
                $"index={index} action={action} angleDeg={report.AngleDeg:F1} previousLength={report.PreviousLength:F2} nextLength={report.NextLength:F2} radiusUsed={report.RadiusUsed:F2} reason={report.RejectReason} generatedPoints={generatedPoints}";
            Debug.Log($"{LogPrefix} [PathCornerSmoothing] vertex | {details}");
            TiagoExperimentTelemetry.RecordEvent("path_corner_smoothing_vertex", details);
        }

        private void LogPathClearanceOffsetComplete(
            bool shouldLog,
            int inputPoints,
            int outputPoints,
            int eligiblePoints,
            int skippedBySufficientClearance,
            int adjustedPoints,
            int keptPoints,
            int rejectedBySample,
            int rejectedByRaycast,
            int revertedByMaxDeviation,
            int revertedByArtificialCurvature,
            float originalMinEdgeDistance,
            float finalMinEdgeDistance,
            float originalMeanEdgeDistance,
            float finalMeanEdgeDistance,
            float maxDeviationApplied,
            string reason)
        {
            if (!shouldLog || SuppressVerboseAndroidDiagnostics)
            {
                return;
            }

            string details =
                $"inputPoints={inputPoints} outputPoints={outputPoints} eligiblePoints={eligiblePoints} skippedBySufficientClearance={skippedBySufficientClearance} adjustedPoints={adjustedPoints} keptPoints={keptPoints} rejectedBySample={rejectedBySample} rejectedByRaycast={rejectedByRaycast} revertedByMaxDeviation={revertedByMaxDeviation} revertedByArtificialCurvature={revertedByArtificialCurvature} originalMinEdgeDistance={FormatFloat(originalMinEdgeDistance)} finalMinEdgeDistance={FormatFloat(finalMinEdgeDistance)} originalMeanEdgeDistance={FormatFloat(originalMeanEdgeDistance)} finalMeanEdgeDistance={FormatFloat(finalMeanEdgeDistance)} maxDeviationApplied={maxDeviationApplied:F3} reason={reason}";
            Debug.Log($"{LogPrefix} [PathClearanceOffset] complete | {details}");
            TiagoExperimentTelemetry.RecordEvent("path_clearance_offset_complete", details);
        }

        private void LogPathClearanceOffsetPointAdjusted(bool shouldLog, int index, float originalClearance, float selectedClearance, float offsetDistance, float signedOffset)
        {
            if (!shouldLog || SuppressVerboseAndroidDiagnostics)
            {
                return;
            }

            string side = signedOffset >= 0f ? "positive" : "negative";
            string details =
                $"index={index} originalClearance={originalClearance:F3} selectedClearance={selectedClearance:F3} offsetDistance={offsetDistance:F2} side={side}";
            Debug.Log($"{LogPrefix} [PathClearanceOffset] point_adjusted | {details}");
            TiagoExperimentTelemetry.RecordEvent("path_clearance_offset_point_adjusted", details);
        }

        private void LogPathCornerSmoothingComplete(
            bool shouldLog,
            int originalPoints,
            int centeredPoints,
            int finalPoints,
            int cornersConsidered,
            int smoothedVertices,
            int keptByAngle,
            int keptByShortSegment,
            int rejectedByNavMesh,
            int rejectedBySegmentRaycast,
            string reason)
        {
            if (!shouldLog || SuppressVerboseAndroidDiagnostics)
            {
                return;
            }

            string details =
                $"centeredPoints={centeredPoints} finalPoints={finalPoints} cornersConsidered={cornersConsidered} cornersSmoothed={smoothedVertices} cornersKeptByAngle={keptByAngle} cornersKeptByShortSegment={keptByShortSegment} cornersRejectedByNavMesh={rejectedByNavMesh} cornersRejectedBySegmentRaycast={rejectedBySegmentRaycast} activePathSource={(smoothedVertices > 0 ? ActivePathSource.Smoothed : ActivePathSource.Centered)} minSegmentLength={_cornerSmoothingMinSegmentLength:F2} radiusUsed={_cornerSmoothingRadius:F2} samplesPerCorner={_cornerSmoothingSamplesPerCorner} originalPoints={originalPoints} reason={reason}";
            Debug.Log($"{LogPrefix} [PathCornerSmoothing] complete | {details}");
            TiagoExperimentTelemetry.RecordEvent("path_corner_smoothing_complete", details);
        }

        private void LogPathCornerSmoothingCacheUse(string reason, UnityVector3 currentPosition, UnityVector3 sampledTarget, int cachedPoints)
        {
            if (SuppressVerboseAndroidDiagnostics ||
                Time.time - _lastPathCornerSmoothingLogTime < ActuationDiagnosticIntervalSeconds)
            {
                return;
            }

            _lastPathCornerSmoothingLogTime = Time.time;
            string details =
                $"activePathSource={ActivePathSource.SmoothedCached} cachedPoints={cachedPoints} reason={reason} current={FormatVector(currentPosition)} target={FormatVector(sampledTarget)}";
            Debug.Log($"{LogPrefix} [PathCornerSmoothing] cache_used | {details}");
            TiagoExperimentTelemetry.RecordEvent("path_corner_smoothing_cache_used", details);
        }

        private static void LogNavigationBuildStamp()
        {
            string details = $"version=StartupAlignmentWithFootprintClearanceDiagnostics timestamp={DateTime.Now:O} recoveryMode=MinimalReverseArc startupAlignEnter={HeadingAlignEnterAngleDeg:F1} startupAlignExit={HeadingAlignExitAngleDeg:F1} hold={HeadingAlignmentHoldSeconds:F2} curvatureProfilerEnabled={EnablePathCurvatureSpeedProfiler} feasibilityLimiterEnabled={EnableDifferentialFeasibilityLimiter} footprintEnabled={FootprintClearanceCheckEnabled} footprintRadius={FootprintRadiusMeters:F2} footprintHorizon={FootprintCheckHorizonMeters:F2}";
            Debug.Log($"{LogPrefix} [NavigationBuildStamp] {details}");
            TiagoExperimentTelemetry.RecordEvent("navigation_build_stamp", details);
        }

        private void LogLookaheadTarget(
            ActivePathSource source,
            int segmentIndex,
            UnityVector3 lookahead,
            float distanceToPath,
            float remaining)
        {
            if (SuppressVerboseAndroidDiagnostics)
            {
                return;
            }

            bool sourceChanged = source != _lastLoggedLookaheadSource;
            bool segmentChanged = segmentIndex != _lastLoggedLookaheadSegment;
            bool targetMoved = float.IsInfinity(_lastLoggedLookaheadPoint.x) ||
                Vector3.Distance(lookahead, _lastLoggedLookaheadPoint) >= 0.20f;
            if (!sourceChanged && !segmentChanged && !targetMoved)
            {
                return;
            }

            _lastLoggedLookaheadSource = source;
            _lastLoggedLookaheadSegment = segmentIndex;
            _lastLoggedLookaheadPoint = lookahead;
            string details =
                $"source={source} | segmentIndex={segmentIndex} | lookahead={FormatVector(lookahead)} | distanceToPath={distanceToPath:F2} | remaining={remaining:F2}";
            Debug.Log($"{LogPrefix} [PathTracking] lookahead_target | {details}");
            TiagoExperimentTelemetry.RecordEvent("path_tracking_lookahead_target", details);
        }

        private static UnityVector3[] CopyFlattenedCorners(NavMeshPath path)
        {
            UnityVector3[] corners = path != null ? path.corners : Array.Empty<UnityVector3>();
            if (corners.Length == 0)
            {
                return Array.Empty<UnityVector3>();
            }

            UnityVector3[] points = new UnityVector3[corners.Length];
            for (int i = 0; i < corners.Length; i++)
            {
                points[i] = Flatten(corners[i]);
            }

            return points;
        }

        private static UnityVector3[] CopyPoints(IReadOnlyList<UnityVector3> points)
        {
            if (points == null || points.Count == 0)
            {
                return Array.Empty<UnityVector3>();
            }

            UnityVector3[] copy = new UnityVector3[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                copy[i] = Flatten(points[i]);
            }

            return copy;
        }

        private bool IsArrivalSatisfied(float remainingPathDistance, float directRemainingDistance)
        {
            return directRemainingDistance <= _arrivalDistance ||
                (remainingPathDistance <= _arrivalDistance && directRemainingDistance <= _arrivalDistance * 1.5f);
        }

        private PathFrame ApplyActiveCornerContext(PathFrame rawFrame, UnityVector3 currentPosition)
        {
            if (!_hasActiveCorner)
            {
                return rawFrame;
            }

            float distanceToCorner = Vector3.Distance(currentPosition, _activeCornerPoint);
            return new PathFrame(
                rawFrame.GuidePoint,
                _activeCornerHeading,
                _activeCornerHeading,
                _activeCornerPoint,
                true,
                _activeCornerAngleDeg,
                distanceToCorner,
                true);
        }

        private void UpdateLocomotionMode(
            PathFrame rawFrame,
            PathFrame frame,
            UnityVector3 currentPosition,
            UnityVector3 desiredTarget,
            UnityVector3 correctedHeading,
            ObstacleFrame obstacle,
            float remainingDistance)
        {
            float timeInMode = Time.time - _modeEnteredAt;
            bool modeDwellElapsed = timeInMode >= MinimumModeDwellSeconds;
            bool stallRecoveryAllowed = Time.time >= _stallRecoveryCooldownUntil || _mode == LocomotionMode.StallRecovery;
            float angleErrorDeg = Mathf.Abs(ComputeSignedAngleDegrees(correctedHeading, frame.FollowHeading));

            if (_mode == LocomotionMode.StallRecovery)
            {
                float elapsed = Time.time - _stallRecoveryStartedAt;
                StallRecoveryPhase phase = GetStallRecoveryPhase(elapsed, currentPosition, correctedHeading, out string endReason);

                if (phase == StallRecoveryPhase.None)
                {
                    _stallRecoveryEndReason = endReason;
                    _stallRecoveryCooldownUntil = Time.time + StallRecoveryCooldownSeconds;
                    _stallRecoveryAwaitingProgressCheck = true;
                    _stallRecoveryProgressCheckStartedAt = Time.time;
                    _stallRecoveryProgressCheckBeforeRemaining = _stallRecoveryEntryRemainingDistance;
                    _stallRecoveryProgressCheckAttempt = _stallRecoveryAttemptCount;
                    _stallRecoveryResumeReplanPending = true;
                    _stallRecoveryResumeOldRemaining = remainingDistance;
                    _stallRecoveryResumeOldCorners = _lastNavMeshPathCorners?.Length ?? 0;
                    Debug.Log($"{LogPrefix} [StallRecovery] exit_resume_follow_path | attempt={_stallRecoveryAttemptCount} | endReason={_stallRecoveryEndReason}");
                    TiagoExperimentTelemetry.RecordEvent(
                        "stall_recovery_exit_resume_follow_path",
                        $"attempt={_stallRecoveryAttemptCount} elapsed={elapsed:F2}s before={_stallRecoveryEntryRemainingDistance:F2} remaining={remainingDistance:F2} endReason={_stallRecoveryEndReason}");
                    ChangeMode(
                        LocomotionMode.TrackPath,
                        $"stall_recovery_sequence_complete attempt={_stallRecoveryAttemptCount} elapsed={elapsed:F2}s endReason={_stallRecoveryEndReason}",
                        frame,
                        obstacle,
                        angleErrorDeg);
                }

                return;
            }

            // Legacy modes are frozen under the NavMesh-first strategy: path geometry owns corners and obstacle routing.
            if (_mode == LocomotionMode.ObstacleBypass)
            {
                ChangeMode(LocomotionMode.TrackPath, "legacy_bypass_disabled_navmesh_first", frame, obstacle, angleErrorDeg);
                return;
            }

            if (_mode == LocomotionMode.CornerTurn)
            {
                ReleaseActiveCorner("legacy_corner_disabled_navmesh_first", currentPosition, correctedHeading);
                ChangeMode(LocomotionMode.TrackPath, "legacy_corner_disabled_navmesh_first", frame, obstacle, angleErrorDeg);
                return;
            }

            if (UpdateStallRecoveryProgressCheck(remainingDistance, currentPosition, correctedHeading))
            {
                return;
            }

            if (_mode == LocomotionMode.StartupAlignment)
            {
                UpdateSince(ref _headingAlignmentExitStableSince, angleErrorDeg <= HeadingAlignExitAngleDeg);
                LogHeadingAlignmentSample(frame, remainingDistance, angleErrorDeg);
                float noProgressSeconds = StableDuration(_noProgressSince);
                if (IsStableSince(_noProgressSince, StallNoProgressSeconds))
                {
                    LogStallCandidateSuppressedDuringAlignment(remainingDistance, angleErrorDeg);
                }

                bool emergencyBlocked = ShouldEmergencyStop(obstacle);
                bool hasActivePath = HasStartupAlignmentSoftExitPath(frame);
                StartupAlignmentExitDecision alignmentExitDecision = EvaluateStartupAlignmentExit(
                    angleErrorDeg,
                    HeadingAlignExitAngleDeg,
                    StableDuration(_headingAlignmentExitStableSince),
                    HeadingAlignmentHoldSeconds,
                    StartupAlignmentSoftExitToleranceDeg,
                    noProgressSeconds,
                    StartupAlignmentSoftExitNoProgressSeconds,
                    hasActivePath,
                    emergencyBlocked);

                if (alignmentExitDecision == StartupAlignmentExitDecision.HardExit)
                {
                    _startupAlignmentCompleted = true;
                    ChangeMode(
                        LocomotionMode.TrackPath,
                        $"heading_aligned angleErr={angleErrorDeg:F1}deg stable={StableDuration(_headingAlignmentExitStableSince):F2}s",
                        frame,
                        obstacle,
                        angleErrorDeg);
                }
                else if (alignmentExitDecision == StartupAlignmentExitDecision.SoftExitAfterNoProgress)
                {
                    _startupAlignmentCompleted = true;
                    LogStartupAlignmentSoftExitAfterNoProgress(frame, remainingDistance, angleErrorDeg, noProgressSeconds);
                    ChangeMode(
                        LocomotionMode.TrackPath,
                        $"soft_exit_after_no_progress angleErr={angleErrorDeg:F1}deg exit={HeadingAlignExitAngleDeg:F1}deg tolerance={StartupAlignmentSoftExitToleranceDeg:F1}deg noProgress={noProgressSeconds:F2}s",
                        frame,
                        obstacle,
                        angleErrorDeg);
                }

                return;
            }

            if (!_startupAlignmentCompleted && angleErrorDeg > HeadingAlignEnterAngleDeg)
            {
                ChangeMode(
                    LocomotionMode.StartupAlignment,
                    $"startup_heading_error_high angleErr={angleErrorDeg:F1}deg enter={HeadingAlignEnterAngleDeg:F1}deg",
                    frame,
                    obstacle,
                    angleErrorDeg);
                LogHeadingAlignmentSample(frame, remainingDistance, angleErrorDeg);
                return;
            }

            if (!_startupAlignmentCompleted)
            {
                _startupAlignmentCompleted = true;
                TiagoExperimentTelemetry.RecordEvent(
                    "startup_alignment_exit",
                    $"reason=already_aligned angleErr={angleErrorDeg:F1}deg exit={HeadingAlignExitAngleDeg:F1}deg");
            }

            if (angleErrorDeg > HeadingAlignEnterAngleDeg)
            {
                LogAlignmentReentrySuppressedDuringTracking(frame, remainingDistance, angleErrorDeg);
            }

            StallRecoveryEntryDecision stallRecoveryEntry = EvaluateStallRecoveryEntry(obstacle, remainingDistance, stallRecoveryAllowed, modeDwellElapsed, angleErrorDeg);
            if (stallRecoveryEntry.ShouldEnter)
            {
                if (_stallRecoveryAttemptCount >= StallRecoveryMaxAttempts)
                {
                    FailStallRecoveryMaxAttempts();
                    return;
                }

                EnterStallRecovery(stallRecoveryEntry.Reason, remainingDistance, currentPosition, correctedHeading, frame, obstacle, angleErrorDeg);
            }
            else if (stallRecoveryEntry.Candidate)
            {
                LogStallCandidateIgnored(stallRecoveryEntry, remainingDistance);
            }
        }

        private void UpdateTemporalFilters(PathFrame frame, UnityVector3 correctedHeading, ObstacleFrame obstacle)
        {
            UpdateSince(ref _frontBlockedSince, obstacle.FrontOccupancy >= ObstacleEnterOccupancy);
            UpdateSince(ref _frontClearSince, obstacle.FrontOccupancy <= ObstacleExitOccupancy);
            UpdateSince(ref _emergencyStopSince, obstacle.Front.HasHit && obstacle.FrontOccupancy >= EmergencyStopFrontOccupancy);

            // Corner ownership is disabled in the NavMesh-first controller. The lookahead point on
            // NavMeshPath carries corner geometry, so this filter only maintains safety-envelope state.
            ClearCornerCandidate();
        }

        private void EnsureStartupAlignmentState(UnityVector3 desiredTarget)
        {
            if (_hasStartupAlignmentTarget &&
                Vector3.Distance(Flatten(_startupAlignmentTarget), Flatten(desiredTarget)) <= Mathf.Max(_arrivalDistance, 0.05f))
            {
                return;
            }

            _hasStartupAlignmentTarget = true;
            _startupAlignmentTarget = desiredTarget;
            _startupAlignmentCompleted = false;
            _headingAlignmentExitStableSince = float.PositiveInfinity;
            _lastAlignmentReentrySuppressedLogTime = float.NegativeInfinity;
            _lastCurvatureCommandLogTime = float.NegativeInfinity;
            TiagoExperimentTelemetry.RecordEvent(
                "startup_alignment_armed",
                $"target={FormatVector(desiredTarget)} enter={HeadingAlignEnterAngleDeg:F1}deg exit={HeadingAlignExitAngleDeg:F1}deg hold={HeadingAlignmentHoldSeconds:F2}s");
        }

        private void ActivateCornerContext()
        {
            if (!_hasCornerCandidate)
            {
                return;
            }

            _hasActiveCorner = true;
            _activeCornerPoint = _cornerCandidatePoint;
            _activeCornerHeading = _cornerCandidateHeading;
            _activeCornerAngleDeg = _cornerCandidateAngleDeg;
            _activeCornerActivatedAt = Time.time;
            _activeCornerWasAligned = false;
            _bestCornerFollowThroughProgress = float.NegativeInfinity;
            _cornerFollowThroughNoProgressSince = float.PositiveInfinity;

            LogCornerEvent(
                "activated",
                $"point={FormatVector(_activeCornerPoint)} heading={FormatVector(_activeCornerHeading)} turn={_activeCornerAngleDeg:F1}deg dist={_cornerCandidateDistance:F2}m stable={StableDuration(_cornerCandidateSince):F2}s");
        }

        private void TryReleaseActiveCorner(UnityVector3 currentPosition, UnityVector3 correctedHeading)
        {
            if (!_hasActiveCorner)
            {
                return;
            }

            CornerResolution resolution = EvaluateCornerResolution(currentPosition, correctedHeading, ObstacleFrame.None);
            if (resolution.IsAligned)
            {
                _activeCornerWasAligned = true;
            }

            if (resolution.HasProgressed)
            {
                ReleaseActiveCorner(
                    $"aligned_and_progressed error={resolution.ErrorDeg:F1}deg progress={resolution.OutgoingProgress:F2}m",
                    currentPosition,
                    correctedHeading);
            }
        }

        private CornerResolution EvaluateCornerResolution(UnityVector3 currentPosition, UnityVector3 correctedHeading, ObstacleFrame obstacle)
        {
            if (!_hasActiveCorner)
            {
                return CornerResolution.None;
            }

            float errorDeg = Mathf.Abs(ComputeSignedAngleDegrees(correctedHeading, _activeCornerHeading));
            float outgoingProgress = Vector3.Dot(currentPosition - _activeCornerPoint, _activeCornerHeading.normalized);
            float distanceToCorner = Vector3.Distance(currentPosition, _activeCornerPoint);
            float requiredProgress = Mathf.Max(_waypointReachDistance, CornerReleaseProgressDistance);
            float requiredClearanceDistance = requiredProgress * CornerClearanceReleaseDistanceMultiplier;
            bool aligned = errorDeg <= CornerExitAngleDeg;
            bool progressed = outgoingProgress >= requiredProgress;
            bool positiveProgress = outgoingProgress >= CornerMinimumPositiveProgress;
            bool frontClear = obstacle.FrontOccupancy <= ObstacleExitOccupancy;
            bool separatedAndClear = positiveProgress && frontClear && distanceToCorner >= requiredClearanceDistance;
            bool channeled = aligned && (progressed || separatedAndClear);
            string channelReason = progressed
                ? "progress"
                : separatedAndClear
                    ? "clearance"
                    : "none";

            return new CornerResolution(
                aligned,
                progressed,
                channeled,
                errorDeg,
                outgoingProgress,
                distanceToCorner,
                channelReason);
        }

        private void ReleaseActiveCorner(string reason, UnityVector3 currentPosition, UnityVector3 correctedHeading)
        {
            if (!_hasActiveCorner)
            {
                ClearCornerCandidate();
                return;
            }

            float errorDeg = correctedHeading.sqrMagnitude <= 0.0001f
                ? 0f
                : Mathf.Abs(ComputeSignedAngleDegrees(correctedHeading, _activeCornerHeading));
            float outgoingProgress = Vector3.Dot(currentPosition - _activeCornerPoint, _activeCornerHeading.normalized);

            LogCornerEvent(
                "released",
                $"reason={reason} point={FormatVector(_activeCornerPoint)} duration={Time.time - _activeCornerActivatedAt:F2}s error={errorDeg:F1}deg progress={outgoingProgress:F2}m");

            _hasActiveCorner = false;
            _activeCornerWasAligned = false;
            _activeCornerActivatedAt = float.NegativeInfinity;
            _bestCornerFollowThroughProgress = float.NegativeInfinity;
            _cornerFollowThroughNoProgressSince = float.PositiveInfinity;
            ClearCornerCandidate();
        }

        private void ClearCornerCandidate()
        {
            _hasCornerCandidate = false;
            _cornerCandidateSince = float.PositiveInfinity;
        }

        private static void UpdateSince(ref float since, bool condition)
        {
            if (condition)
            {
                if (float.IsPositiveInfinity(since))
                {
                    since = Time.time;
                }

                return;
            }

            since = float.PositiveInfinity;
        }

        private static bool IsStableSince(float since, float requiredSeconds)
        {
            return !float.IsPositiveInfinity(since) && Time.time - since >= requiredSeconds;
        }

        private static float StableDuration(float since)
        {
            return float.IsPositiveInfinity(since) ? 0f : Time.time - since;
        }

        private void ResetBypassExitFilter()
        {
            _frontClearSince = float.PositiveInfinity;
        }

        private void UpdateMotionObservation(UnityVector3 currentPosition, UnityVector3 correctedHeading)
        {
            if (!_hasActuationSample)
            {
                _lastActuationSamplePosition = currentPosition;
                _lastActuationSampleHeading = correctedHeading;
                _lastActuationSampleTime = Time.time;
                _hasActuationSample = true;
                _observedLinearSpeed = 0f;
                _observedAngularSpeed = 0f;
                return;
            }

            float dt = Mathf.Max(0.0001f, Time.time - _lastActuationSampleTime);
            UnityVector3 delta = currentPosition - _lastActuationSamplePosition;
            UnityVector3 motionReference = correctedHeading.sqrMagnitude > 0.0001f ? correctedHeading.normalized : _lastActuationSampleHeading.normalized;
            _observedLinearSpeed = Vector3.Dot(delta, motionReference) / dt;
            _observedAngularSpeed =
                _lastActuationSampleHeading.sqrMagnitude > 0.0001f && correctedHeading.sqrMagnitude > 0.0001f
                    ? Vector3.SignedAngle(_lastActuationSampleHeading.normalized, correctedHeading.normalized, Vector3.up) * Mathf.Deg2Rad / dt
                    : 0f;

            _lastActuationSamplePosition = currentPosition;
            _lastActuationSampleHeading = correctedHeading;
            _lastActuationSampleTime = Time.time;
        }

        private void UpdateYawCalibration(float angularCommand)
        {
            if (_navigationReference == null || Mathf.Abs(angularCommand) < YawCalibrationAngularThreshold)
            {
                _yawCalibrationActive = false;
                return;
            }

            int commandSign = angularCommand > 0f ? 1 : -1;
            bool alreadyLogged = commandSign > 0 ? _yawCalibrationPositiveLogged : _yawCalibrationNegativeLogged;
            if (alreadyLogged)
            {
                return;
            }

            float currentYawDeg = _navigationReference.eulerAngles.y;
            if (!_yawCalibrationActive || _yawCalibrationCommandSign != commandSign)
            {
                _yawCalibrationActive = true;
                _yawCalibrationCommandSign = commandSign;
                _yawCalibrationStartedAt = Time.time;
                _yawCalibrationStartYawDeg = currentYawDeg;
                return;
            }

            if (Time.time - _yawCalibrationStartedAt < YawCalibrationWindowSeconds)
            {
                return;
            }

            float yawDeltaDeg = Mathf.DeltaAngle(_yawCalibrationStartYawDeg, currentYawDeg);
            string interpretedTurn = InterpretTurnFromYawDelta(yawDeltaDeg);
            string expectedTurn = commandSign > 0 ? "Left" : "Right";
            string details =
                $"wSign={(commandSign > 0 ? "+" : "-")} observedYawDelta={yawDeltaDeg:F1} interpretedTurn={interpretedTurn} expectedTurn={expectedTurn} window={YawCalibrationWindowSeconds:F2}s";
            Debug.Log($"{LogPrefix} YawCalibration | {details}");
            TiagoExperimentTelemetry.RecordEvent("yaw_calibration", details);

            if (interpretedTurn != "None" && interpretedTurn != expectedTurn)
            {
                LogAnomaly("yaw_calibration_sign_mismatch", details);
            }

            if (commandSign > 0)
            {
                _yawCalibrationPositiveLogged = true;
            }
            else
            {
                _yawCalibrationNegativeLogged = true;
            }

            _yawCalibrationActive = false;
        }

        private StallCheckResult EvaluateFollowPathStall(ObstacleFrame obstacle, float remainingDistance)
        {
            if (_mode != LocomotionMode.TrackPath || TiagoLocomotionControlGate.ManualOverrideActive || !_lastCommandApplied)
            {
                return new StallCheckResult(
                    false,
                    string.Empty,
                    !_lastCommandApplied ? "command_not_applied" : "mode_not_track_path",
                    obstacle.LeftOccupancy > 0.05f || obstacle.RightOccupancy > 0.05f || obstacle.FrontOccupancy > 0.02f,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    remainingDistance,
                    _bestRemainingDistanceInMode,
                    StableDuration(_noProgressSince),
                    _observedLinearSpeed,
                    _observedAngularSpeed,
                    _lastCommandLinear,
                    _lastCommandAngular,
                    obstacle.FrontOccupancy,
                    obstacle.LeftOccupancy,
                    obstacle.RightOccupancy);
            }

            bool lowObservedLinear = Mathf.Abs(_observedLinearSpeed) <= StallObservedLinearThreshold;
            bool highAngularCommand = Mathf.Abs(_lastCommandAngular) >= StallCommandAngularThreshold;
            bool turnDemandActive = Mathf.Abs(_lastCommandAngular) >= StallCommandAngularSustainThreshold;
            UpdateSince(ref _stallTurnDemandSince, turnDemandActive);
            bool sustainedTurnDemand = IsStableSince(_stallTurnDemandSince, StallTurnDemandSustainSeconds);
            bool strongTurnDemand = highAngularCommand || sustainedTurnDemand || Mathf.Abs(_observedAngularSpeed) >= StallObservedAngularThreshold;
            bool commandedForward = _lastCommandLinear >= StallCommandLinearThreshold;
            bool noProgressStable = IsStableSince(_noProgressSince, StallNoProgressSeconds);
            bool insufficientObservedAngular = Mathf.Abs(_observedAngularSpeed) <= StallObservedAngularInsufficientThreshold;
            bool localConstraint = obstacle.LeftOccupancy > 0.05f || obstacle.RightOccupancy > 0.05f || obstacle.FrontOccupancy > 0.02f;
            bool constrainedStall = lowObservedLinear && strongTurnDemand && commandedForward && noProgressStable && localConstraint;
            bool kinematicStall = lowObservedLinear && strongTurnDemand && commandedForward && noProgressStable && insufficientObservedAngular;
            string candidateReason = constrainedStall
                ? "constrained_stall_no_progress"
                : (kinematicStall ? "kinematic_stall_no_progress" : string.Empty);
            string blockedReason = constrainedStall || kinematicStall
                ? string.Empty
                : ResolveStallBlockedReason(commandedForward, strongTurnDemand, lowObservedLinear, insufficientObservedAngular, noProgressStable, localConstraint);

            return new StallCheckResult(
                constrainedStall || kinematicStall,
                candidateReason,
                blockedReason,
                localConstraint,
                noProgressStable,
                commandedForward,
                strongTurnDemand,
                sustainedTurnDemand,
                lowObservedLinear,
                insufficientObservedAngular,
                remainingDistance,
                _bestRemainingDistanceInMode,
                StableDuration(_noProgressSince),
                _observedLinearSpeed,
                _observedAngularSpeed,
                _lastCommandLinear,
                _lastCommandAngular,
                obstacle.FrontOccupancy,
                obstacle.LeftOccupancy,
                obstacle.RightOccupancy);
        }

        private static string ResolveStallBlockedReason(
            bool commandedForward,
            bool strongTurnDemand,
            bool lowObservedLinear,
            bool insufficientObservedAngular,
            bool noProgressStable,
            bool localConstraint)
        {
            if (!commandedForward)
            {
                return "cmd_linear_below_threshold";
            }

            if (!strongTurnDemand)
            {
                return "cmd_angular_not_high_or_sustained";
            }

            if (!lowObservedLinear)
            {
                return "observed_linear_not_low";
            }

            if (!noProgressStable)
            {
                return "remaining_distance_still_improving";
            }

            if (!localConstraint && !insufficientObservedAngular)
            {
                return "no_local_constraint_and_rotation_still_observed";
            }

            return "stall_conditions_not_met";
        }

        private void LogStallCheck(StallCheckResult stallCheck, bool stallRecoveryAllowed, bool modeDwellElapsed)
        {
            if (_mode != LocomotionMode.TrackPath)
            {
                return;
            }

            if (!stallCheck.Candidate)
            {
                _lastStallCandidateLogged = false;
                return;
            }

            bool stableCandidate = IsStableSince(_stallCandidateSince, StallNoProgressSeconds);
            bool canEnter = stallRecoveryAllowed && modeDwellElapsed && stableCandidate;
            if (SuppressVerboseAndroidDiagnostics && !canEnter)
            {
                return;
            }

            bool shouldLog = !_lastStallCandidateLogged || Time.time - _lastStallDiagnosticLogTime >= StallDiagnosticLogCooldownSeconds;
            if (!shouldLog)
            {
                return;
            }

            _lastStallCandidateLogged = true;
            _lastStallDiagnosticLogTime = Time.time;
            string blockedReason = canEnter
                ? "none"
                : (!stallRecoveryAllowed
                    ? "stall_recovery_cooldown_active"
                    : (!modeDwellElapsed ? "mode_dwell_not_elapsed" : "candidate_not_stable_yet"));

            string details =
                $"reason={(canEnter ? stallCheck.CandidateReason : blockedReason)} candidateReason={stallCheck.CandidateReason} " +
                $"timeWithoutProgress={stallCheck.TimeWithoutProgress:F2}s observedV={stallCheck.ObservedLinear:F3} observedW={stallCheck.ObservedAngular:F3} " +
                $"vCmd={stallCheck.CommandedLinear:F2} wCmd={stallCheck.CommandedAngular:F2} remainingDistance={stallCheck.RemainingDistance:F2} bestRemainingDistance={stallCheck.BestRemainingDistance:F2} " +
                $"front={stallCheck.FrontOccupancy:F2} left={stallCheck.LeftOccupancy:F2} right={stallCheck.RightOccupancy:F2} " +
                $"lowObservedLinear={stallCheck.LowObservedLinear} strongTurnDemand={stallCheck.StrongTurnDemand} sustainedTurnDemand={stallCheck.SustainedTurnDemand} " +
                $"insufficientObservedAngular={stallCheck.InsufficientObservedAngular} localConstraint={stallCheck.LocalConstraint}";

            if (canEnter)
            {
                Debug.Log($"{LogPrefix} StallCheck | candidate | {details}");
                TiagoExperimentTelemetry.RecordEvent("stall_check_candidate", details);
                return;
            }

            Debug.Log($"{LogPrefix} StallCheck | waiting | {details}");
            TiagoExperimentTelemetry.RecordEvent("stall_check_waiting", details);
        }

        private void BeginStallRecoveryAttempt(
            float remainingDistance,
            string reason,
            UnityVector3 currentPosition,
            UnityVector3 correctedHeading)
        {
            _stallRecoveryAttemptCount++;
            _stallRecoveryStartedAt = Time.time;
            _stallRecoveryEntryRemainingDistance = remainingDistance;
            _stallRecoveryBestRemainingDistance = remainingDistance;
            _stallRecoveryLoggedPhase = StallRecoveryPhase.None;
            _stallRecoveryAwaitingProgressCheck = false;
            _stallRecoveryProgressCheckStartedAt = float.NegativeInfinity;
            _stallRecoveryProgressCheckBeforeRemaining = float.PositiveInfinity;
            _stallRecoveryProgressCheckAttempt = _stallRecoveryAttemptCount;
            _stallRecoveryReverseArcDirectionSign = ResolveStallRecoveryReverseArcDirection();
            _stallRecoveryEndReason = "time_limit";
            _stallRecoveryStartPosition = currentPosition;
            _stallRecoveryStartHeading = correctedHeading.sqrMagnitude > 0.0001f ? correctedHeading.normalized : Vector3.forward;

            LogStallRecoveryConfig($"enter attempt={_stallRecoveryAttemptCount}");
            Debug.Log($"{LogPrefix} [StallRecovery] enter | attempt={_stallRecoveryAttemptCount} | remaining={remainingDistance:F2} | reason={reason} | reverseArcDirection={FormatRotateDirection(_stallRecoveryReverseArcDirectionSign)}");
            TiagoExperimentTelemetry.RecordEvent(
                "stall_recovery_enter",
                $"attempt={_stallRecoveryAttemptCount} remaining={remainingDistance:F2} reason={reason} reverseArcDirection={FormatRotateDirection(_stallRecoveryReverseArcDirectionSign)}");
        }

        private void EnterStallRecovery(
            string reason,
            float remainingDistance,
            UnityVector3 currentPosition,
            UnityVector3 correctedHeading,
            PathFrame frame,
            ObstacleFrame obstacle,
            float angleErrorDeg)
        {
            BeginStallRecoveryAttempt(remainingDistance, reason, currentPosition, correctedHeading);
            string footprintDetails = _lastFootprintClearance.HasSample
                ? $" nearestFootprintObstacle={_lastFootprintClearance.ColliderPath} minFootprintClearance={_lastFootprintClearance.MinClearance:F2} footprintPass={_lastFootprintClearance.Pass} footprintSampleIndex={_lastFootprintClearance.SampleIndex}"
                : " nearestFootprintObstacle=None minFootprintClearance=NaN footprintPass=Unknown footprintSampleIndex=-1";
            ChangeMode(
                LocomotionMode.StallRecovery,
                $"stall_recovery_enter attempt={_stallRecoveryAttemptCount} reason={reason} noProgress={StableDuration(_noProgressSince):F2}s observedV={_observedLinearSpeed:F3} vFinal={_lastCommandLinear:F2} angleErrorDeg={angleErrorDeg:F1} activeLookahead={FormatVector(_lastActiveLookaheadPoint)} recentForwardV={_lastPositiveAutonomousForwardCommand:F2} remainingPathDistance={remainingDistance:F2} obstacleFront={obstacle.FrontOccupancy:F2}{footprintDetails}",
                frame,
                obstacle,
                angleErrorDeg);
        }

        private bool TryRunStallRecoveryResumeReplan(
            UnityVector3 currentPosition,
            UnityVector3 desiredTarget,
            ref NavMeshPath path,
            ref UnityVector3 sampledTarget,
            ref UnityVector3[] activePathPoints,
            ref PathTrackingFrame trackingFrame,
            ref PathFrame rawFrame,
            ref float remainingDistance)
        {
            float oldRemaining = _stallRecoveryResumeOldRemaining;
            int oldCorners = _stallRecoveryResumeOldCorners;
            _stallRecoveryResumeReplanPending = false;
            ClearPathCenteringCache();
            ClearActivePathLock("stall_recovery_resume_replan");
            _startupAlignmentCompleted = false;
            _headingAlignmentExitStableSince = float.PositiveInfinity;
            TiagoExperimentTelemetry.RecordEvent(
                "startup_alignment_armed",
                $"reason=stall_recovery_resume_replan target={FormatVector(desiredTarget)} enter={HeadingAlignEnterAngleDeg:F1}deg exit={HeadingAlignExitAngleDeg:F1}deg hold={HeadingAlignmentHoldSeconds:F2}s");

            if (!TryBuildPath(
                currentPosition,
                desiredTarget,
                out NavMeshPath replannedPath,
                out UnityVector3 replannedSampledTarget,
                out UnityVector3[] replannedActivePathPoints,
                out ActivePathSource replannedActivePathSource))
            {
                Debug.LogWarning($"{LogPrefix} [StallRecovery] resume_replan | oldRemaining={oldRemaining:F2} | newRemaining=NaN | oldCorners={oldCorners} | newCorners=0 | status=failed");
                TiagoExperimentTelemetry.RecordEvent(
                    "stall_recovery_resume_replan",
                    $"oldRemaining={oldRemaining:F2} newRemaining=NaN oldCorners={oldCorners} newCorners=0 status=failed");
                return false;
            }

            PathTrackingFrame replannedFrame = BuildLookaheadFrame(replannedActivePathPoints, replannedActivePathSource, currentPosition, replannedSampledTarget);
            if (!replannedFrame.IsValid)
            {
                Debug.LogWarning($"{LogPrefix} [StallRecovery] resume_replan | oldRemaining={oldRemaining:F2} | newRemaining=NaN | oldCorners={oldCorners} | newCorners={replannedPath.corners.Length} | status=invalid_lookahead");
                TiagoExperimentTelemetry.RecordEvent(
                    "stall_recovery_resume_replan",
                    $"oldRemaining={oldRemaining:F2} newRemaining=NaN oldCorners={oldCorners} newCorners={replannedPath.corners.Length} status=invalid_lookahead");
                return false;
            }

            path = replannedPath;
            sampledTarget = replannedSampledTarget;
            activePathPoints = replannedActivePathPoints;
            trackingFrame = replannedFrame;
            rawFrame = PathFrame.FromLookahead(replannedFrame);
            remainingDistance = replannedFrame.RemainingPathDistance;
            _bestRemainingDistanceInMode = remainingDistance;
            _noProgressSince = float.PositiveInfinity;

            Debug.Log($"{LogPrefix} [StallRecovery] resume_replan | oldRemaining={oldRemaining:F2} | newRemaining={remainingDistance:F2} | oldCorners={oldCorners} | newCorners={replannedPath.corners.Length} | status={replannedPath.status}");
            TiagoExperimentTelemetry.RecordEvent(
                "stall_recovery_resume_replan",
                $"oldRemaining={oldRemaining:F2} newRemaining={remainingDistance:F2} oldCorners={oldCorners} newCorners={replannedPath.corners.Length} status={replannedPath.status}");
            return true;
        }

        private bool UpdateStallRecoveryProgressCheck(
            float remainingDistance,
            UnityVector3 currentPosition,
            UnityVector3 correctedHeading)
        {
            if (!_stallRecoveryAwaitingProgressCheck)
            {
                return false;
            }

            _stallRecoveryBestRemainingDistance = Mathf.Min(_stallRecoveryBestRemainingDistance, remainingDistance);
            if (Time.time - _stallRecoveryProgressCheckStartedAt < StallNoProgressSeconds)
            {
                return false;
            }

            float after = _stallRecoveryBestRemainingDistance;
            float progressMeters = _stallRecoveryProgressCheckBeforeRemaining - after;
            float displacementDuringRecovery = Vector3.Distance(currentPosition, _stallRecoveryStartPosition);
            float yawDeltaDuringRecovery = _stallRecoveryStartHeading.sqrMagnitude > 0.0001f && correctedHeading.sqrMagnitude > 0.0001f
                ? Vector3.SignedAngle(_stallRecoveryStartHeading.normalized, correctedHeading.normalized, Vector3.up)
                : 0f;
            bool improved = progressMeters >= StallRecoveryMinProgressMeters;
            bool recoveredForwardMotion = _lastCommandApplied &&
                _lastCommandLinear >= StallCommandLinearThreshold &&
                _observedLinearSpeed >= StallRecoveryForwardRecoveryLinearThreshold &&
                progressMeters >= ProgressEpsilonMeters;
            Debug.Log($"{LogPrefix} [StallRecovery] progress_check | before={_stallRecoveryProgressCheckBeforeRemaining:F2} | after={after:F2} | progress={progressMeters:F2} | improved={improved} | forwardMotion={recoveredForwardMotion} | observedV={_observedLinearSpeed:F3} | observedW={_observedAngularSpeed:F3} | yawDeltaDuringRecovery={yawDeltaDuringRecovery:F1} | displacementDuringRecovery={displacementDuringRecovery:F2} | endReason={_stallRecoveryEndReason}");
            TiagoExperimentTelemetry.RecordEvent(
                "stall_recovery_progress_check",
                $"attempt={_stallRecoveryProgressCheckAttempt} before={_stallRecoveryProgressCheckBeforeRemaining:F2} after={after:F2} progress={progressMeters:F2} improved={improved} forwardMotion={recoveredForwardMotion} observedV={_observedLinearSpeed:F3} observedW={_observedAngularSpeed:F3} yawDeltaDuringRecovery={yawDeltaDuringRecovery:F1} displacementDuringRecovery={displacementDuringRecovery:F2} endReason={_stallRecoveryEndReason} forwardThreshold={StallRecoveryForwardRecoveryLinearThreshold:F2}");
            if (displacementDuringRecovery < 0.05f)
            {
                Debug.LogWarning($"{LogPrefix} [StallRecovery] ineffective_motion | displacementDuringRecovery={displacementDuringRecovery:F2} | yawDeltaDuringRecovery={yawDeltaDuringRecovery:F1} | recommendation=check_physics_collision_or_drive_bridge");
                TiagoExperimentTelemetry.RecordEvent(
                    "stall_recovery_ineffective_motion",
                    $"displacementDuringRecovery={displacementDuringRecovery:F2} yawDeltaDuringRecovery={yawDeltaDuringRecovery:F1} recommendation=check_physics_collision_or_drive_bridge");
            }

            _stallRecoveryAwaitingProgressCheck = false;
            if (improved || recoveredForwardMotion)
            {
                _stallRecoveryAttemptCount = 0;
                _stallCandidateSince = float.PositiveInfinity;
                return false;
            }

            if (_stallRecoveryAttemptCount >= StallRecoveryMaxAttempts)
            {
                FailStallRecoveryMaxAttempts();
                return true;
            }

            _stallCandidateSince = float.PositiveInfinity;
            return false;
        }

        private void FailStallRecoveryMaxAttempts()
        {
            _stallRecoveryFailedMaxAttempts = true;
            Debug.LogWarning($"{LogPrefix} [StallRecovery] failed_max_attempts | recommendation=increase_navmesh_clearance_or_obstacle_carving_margin");
            TiagoExperimentTelemetry.RecordEvent(
                "stall_recovery_failed_max_attempts",
                $"attempts={_stallRecoveryAttemptCount} recommendation=increase_navmesh_clearance_or_obstacle_carving_margin");
            TiagoLocomotionControlGate.TryStopAutonomous(_driveBridge);
        }

        private void ResetStallRecoveryNavigationState()
        {
            _stallRecoveryAttemptCount = 0;
            _stallRecoveryAwaitingProgressCheck = false;
            _stallRecoveryProgressCheckStartedAt = float.NegativeInfinity;
            _stallRecoveryProgressCheckBeforeRemaining = float.PositiveInfinity;
            _stallRecoveryProgressCheckAttempt = 0;
            _stallRecoveryResumeReplanPending = false;
            _stallRecoveryResumeOldRemaining = float.PositiveInfinity;
            _stallRecoveryResumeOldCorners = 0;
            _stallRecoveryFailedMaxAttempts = false;
            _stallRecoveryEndReason = "time_limit";
            _stallRecoveryStartPosition = Vector3.zero;
            _stallRecoveryStartHeading = Vector3.forward;
            _lastPositiveAutonomousForwardCommandAt = float.NegativeInfinity;
            _lastPositiveAutonomousForwardCommand = 0f;
            _stallCandidateSince = float.PositiveInfinity;
            _lastStallReason = "none";
        }

        private void UpdatePositiveForwardCommandMemory(DriveCommand command, bool commandApplied)
        {
            if (_mode == LocomotionMode.StallRecovery || !commandApplied || command.Linear < StallCommandLinearThreshold)
            {
                return;
            }

            _lastPositiveAutonomousForwardCommandAt = Time.time;
            _lastPositiveAutonomousForwardCommand = command.Linear;
        }

        private int ResolveStallRecoveryReverseArcDirection()
        {
            return _stallRecoveryAttemptCount == 1 ? 1 : -1;
        }

        private StallRecoveryPhase GetStallRecoveryPhase(float elapsed)
        {
            if (elapsed < StallRecoveryStopSeconds)
            {
                return StallRecoveryPhase.Stop;
            }

            if (elapsed < StallRecoveryStopSeconds + StallRecoveryReverseArcSeconds)
            {
                return StallRecoveryPhase.ReverseArc;
            }

            return StallRecoveryPhase.None;
        }

        private StallRecoveryPhase GetStallRecoveryPhase(
            float elapsed,
            UnityVector3 currentPosition,
            UnityVector3 correctedHeading,
            out string endReason)
        {
            endReason = "time_limit";
            if (elapsed < StallRecoveryStopSeconds)
            {
                return StallRecoveryPhase.Stop;
            }

            if (elapsed < StallRecoveryStopSeconds + StallRecoveryReverseArcSeconds)
            {
                return StallRecoveryPhase.ReverseArc;
            }

            return StallRecoveryPhase.None;
        }

        private void LogRecoveryPhase(DriveCommand command, ObstacleFrame obstacle, UnityVector3 correctedHeading, bool commandApplied)
        {
            float elapsed = Time.time - _stallRecoveryStartedAt;
            StallRecoveryPhase phase = GetStallRecoveryPhase(elapsed);
            if (phase == StallRecoveryPhase.None || _stallRecoveryLoggedPhase == phase)
            {
                return;
            }

            _stallRecoveryLoggedPhase = phase;
            string details = phase == StallRecoveryPhase.ReverseArc
                ? $"phase=ReverseArc | reverseArcDirection={FormatRotateDirection(_stallRecoveryReverseArcDirectionSign)} | v={command.Linear:F2} | w={command.Angular:F2} | elapsed={elapsed:F2}"
                : $"phase={phase} | v={command.Linear:F2} | w={command.Angular:F2}";
            Debug.Log($"{LogPrefix} [StallRecovery] {details}");
            TiagoExperimentTelemetry.RecordEvent(
                "stall_recovery_phase",
                $"attempt={_stallRecoveryAttemptCount} phase={phase} reverseArcDirection={FormatRotateDirection(_stallRecoveryReverseArcDirectionSign)} v={command.Linear:F2} w={command.Angular:F2} commandApplied={commandApplied} elapsed={elapsed:F2}s front={obstacle.FrontOccupancy:F2}");
        }

        private void LogStallRecoveryAppliedCommand(DriveCommand command)
        {
            StallRecoveryPhase phase = GetStallRecoveryPhase(Time.time - _stallRecoveryStartedAt);
            if (phase != StallRecoveryPhase.ReverseArc ||
                Time.time - _lastStallRecoveryAppliedCommandLogTime < ActuationDiagnosticIntervalSeconds)
            {
                return;
            }

            _lastStallRecoveryAppliedCommandLogTime = Time.time;
            string details = $"phase=ReverseArc | v={command.Linear:F2} | w={command.Angular:F2}";
            Debug.Log($"{LogPrefix} [StallRecovery] applied_command | {details}");
            TiagoExperimentTelemetry.RecordEvent("stall_recovery_applied_command", details);
        }

        private static void LogStallRecoveryConfig(string reason)
        {
            string details =
                $"sequence=Stop->ReverseArc->Replan->Resume | maxAttempts={StallRecoveryMaxAttempts} | stopSeconds={StallRecoveryStopSeconds:F2} | reverseArcSeconds={StallRecoveryReverseArcSeconds:F2} | reverseArcSpeed={StallRecoveryReverseArcSpeed:F2} | reverseArcAngularSpeed={StallRecoveryReverseArcAngularSpeed:F2} | directionPolicy=fixed_attempt_1_left_attempt_2_right | reason={reason}";
            Debug.Log($"{LogPrefix} [StallRecovery] config | {details}");
            TiagoExperimentTelemetry.RecordEvent("stall_recovery_config", details);
        }

        private DriveCommand ComputeCommandForMode(
            PathTrackingFrame trackingFrame,
            PathFrame frame,
            UnityVector3 currentPosition,
            UnityVector3 correctedHeading,
            ObstacleFrame obstacle,
            float remainingDistance)
        {
            switch (_mode)
            {
                case LocomotionMode.StartupAlignment:
                    return ComputeHeadingAlignmentCommand(trackingFrame, correctedHeading, obstacle);

                case LocomotionMode.StallRecovery:
                    return ComputeStallRecoveryCommand(currentPosition, correctedHeading);

                case LocomotionMode.CornerTurn:
                    return ComputeLookaheadFollowCommand(trackingFrame, correctedHeading);

                case LocomotionMode.ObstacleBypass:
                    return ComputeLookaheadFollowCommand(trackingFrame, correctedHeading);

                default:
                    return ComputeFollowPathCommand(trackingFrame, correctedHeading, obstacle);
            }
        }

        private DriveCommand ComputeHeadingAlignmentCommand(
            PathTrackingFrame trackingFrame,
            UnityVector3 correctedHeading,
            ObstacleFrame obstacle)
        {
            if (!trackingFrame.IsValid || ShouldEmergencyStop(obstacle))
            {
                return new DriveCommand(0f, 0f, 0f);
            }

            float angleErrorDeg = ComputeSignedAngleDegrees(correctedHeading, trackingFrame.LookaheadHeading);
            float angularCommand = ComputeHeadingAngularCommand(angleErrorDeg, _maxAngularSpeed);
            LogLookaheadCommand(trackingFrame, angleErrorDeg, 0f, angularCommand, "align_to_path");
            return new DriveCommand(0f, angularCommand, angleErrorDeg);
        }

        private DriveCommand ComputeFollowPathCommand(
            PathTrackingFrame trackingFrame,
            UnityVector3 correctedHeading,
            ObstacleFrame obstacle)
        {
            if (ShouldEmergencyStop(obstacle))
            {
                return new DriveCommand(0f, 0f, 0f);
            }

            return ComputeLookaheadFollowCommand(trackingFrame, correctedHeading);
        }

        private DriveCommand ComputeLookaheadFollowCommand(PathTrackingFrame trackingFrame, UnityVector3 correctedHeading)
        {
            if (!trackingFrame.IsValid)
            {
                return new DriveCommand(0f, 0f, 0f);
            }

            float angleErrorDeg = ComputeSignedAngleDegrees(correctedHeading, trackingFrame.LookaheadHeading);
            float absoluteAngleDeg = Mathf.Abs(angleErrorDeg);
            bool cautiousTracking = absoluteAngleDeg >= CautiousTrackingAngleDeg;
            float angularLimit = cautiousTracking
                ? _maxAngularSpeed * CautiousTrackingMaxAngularFactor
                : _maxAngularSpeed;

            // TiagoDifferentialDriveBridge uses positive angular velocity for a left turn.
            // In this scene setup, Vector3.SignedAngle around +Y is negative for the observed left yaw,
            // so the controller negates the signed angle to command the intended bridge turn direction.
            float angularCommand = ComputeHeadingAngularCommand(angleErrorDeg, angularLimit);

            if (absoluteAngleDeg > TrackingHardStopAngleDeg)
            {
                _lastSpeedReductionReason = SpeedReductionHeadingError;
                _lastVelocityLimitedCommand = 0f;
                _lastVelocityLimitTimestamp = Time.time;
                LogTrackHeadingHardStop(trackingFrame, angleErrorDeg, angularCommand);
                LogLookaheadCommand(trackingFrame, angleErrorDeg, 0f, angularCommand, "track_heading_hard_stop");
                return new DriveCommand(0f, angularCommand, angleErrorDeg);
            }

            float distanceFactor = Mathf.Clamp01(trackingFrame.RemainingPathDistance / _slowdownDistance);
            float speedScale = ComputeTrackingSpeedScale(absoluteAngleDeg);
            float vBase = _maxLinearSpeed * distanceFactor;
            string speedReductionReason = distanceFactor < 0.99f ? SpeedReductionNearGoal : SpeedReductionStraight;
            float vAfterHeading = vBase * speedScale;
            bool headingSpeedLimited = speedScale < 0.99f;
            if (headingSpeedLimited && distanceFactor > 0.05f)
            {
                float crawlSpeed = _fastDemoTrackingEnabled ? _fastMinCornerSpeed : CornerCrawlSpeed;
                vAfterHeading = Mathf.Max(Mathf.Min(crawlSpeed, _maxLinearSpeed), vAfterHeading);
                vAfterHeading = Mathf.Min(vAfterHeading, vBase);
                speedReductionReason = SpeedReductionHeadingError;
            }

            float turnSeverityDeg = ComputeUpcomingTurnSeverity(_lastActivePathPoints, trackingFrame);
            float curvatureSpeedCap = ComputePathCurvatureSpeedLimit(turnSeverityDeg, out string curvatureReason);
            float vAfterCurvature = EnablePathCurvatureSpeedProfiler
                ? Mathf.Min(vAfterHeading, curvatureSpeedCap)
                : vAfterHeading;
            bool curvatureLimited = vAfterCurvature < vAfterHeading - 0.001f;
            if (_fastDemoTrackingEnabled)
            {
                float vBeforeCornerBrake = vAfterCurvature;
                vAfterCurvature = ApplyFastCornerBrake(vAfterCurvature, trackingFrame, out string cornerBrakeReason);
                if (vAfterCurvature < vBeforeCornerBrake - 0.001f)
                {
                    curvatureLimited = true;
                    curvatureReason = cornerBrakeReason;
                    speedReductionReason = SpeedReductionCornerBraking;
                }
            }

            string feasibilityReason = "disabled";
            float feasibilityCap = float.PositiveInfinity;
            float vFinal = vAfterCurvature;
            if (EnableDifferentialFeasibilityLimiter)
            {
                vFinal = ApplyDifferentialCommandFeasibilityLimiter(vAfterCurvature, angularCommand, out feasibilityReason, out feasibilityCap);
            }

            bool feasibilityLimited = vFinal < vAfterCurvature - 0.001f;
            float vBeforeAccelerationLimit = vFinal;
            vFinal = ApplyVelocityAccelerationLimit(vFinal, out bool accelerationLimited);
            if (accelerationLimited && vFinal < vBeforeAccelerationLimit - 0.001f)
            {
                speedReductionReason = SpeedReductionAccelerationLimit;
            }
            else if (accelerationLimited && speedReductionReason == SpeedReductionStraight)
            {
                speedReductionReason = SpeedReductionAccelerationLimit;
            }
            _lastSpeedReductionReason = speedReductionReason;

            float cornerCrawlAngle = _fastDemoTrackingEnabled ? _fastCornerCrawlAngleDeg : TrackingCornerCrawlAngleDeg;
            bool cornerCrawlProfile = absoluteAngleDeg >= cornerCrawlAngle || (EnablePathCurvatureSpeedProfiler && (turnSeverityDeg >= SharpTurnAngleDeg || vFinal <= VerySharpTurnSpeedCap + 0.005f));
            string trackingMode = cornerCrawlProfile
                ? "CornerCrawl"
                : (headingSpeedLimited || curvatureLimited || feasibilityLimited ? "TrackPathSpeedLimited" : "TrackPath");

            if (curvatureLimited)
            {
                if (_fastDemoTrackingEnabled && curvatureReason.StartsWith("fast_"))
                {
                    LogFastCornerBraking(trackingFrame, vAfterHeading, vAfterCurvature, curvatureReason);
                }
                else
                {
                    LogPathCurvatureSpeedLimited(turnSeverityDeg, curvatureSpeedCap, vAfterHeading, vAfterCurvature, curvatureReason);
                }
            }

            if (feasibilityLimited)
            {
                LogDifferentialCommandFeasibilityLimited(vAfterCurvature, angularCommand, vFinal, feasibilityCap, feasibilityReason);
            }

            if (cornerCrawlProfile)
            {
                LogCornerCrawlSpeedProfileActive(turnSeverityDeg, angleErrorDeg, vFinal, angularCommand, curvatureReason);
            }

            LogTrackingCommandPipeline(angleErrorDeg, turnSeverityDeg, vBase, vAfterHeading, vAfterCurvature, vFinal, angularCommand, trackingMode);
            LogCurvatureTrackingCommand(trackingFrame, angleErrorDeg, vFinal, angularCommand, speedScale, trackingMode);
            if (headingSpeedLimited)
            {
                LogTrackHeadingSpeedLimited(trackingFrame, angleErrorDeg, vFinal, angularCommand, speedScale, trackingMode);
            }

            LogLookaheadCommand(trackingFrame, angleErrorDeg, vFinal, angularCommand, trackingMode);
            return new DriveCommand(vFinal, angularCommand, angleErrorDeg);
        }

        private float ComputeTrackingSpeedScale(float absoluteAngleDeg)
        {
            float startAngle = _fastDemoTrackingEnabled ? _fastHeadingSpeedLimitStartAngleDeg : TrackingSpeedLimitStartAngleDeg;
            float crawlAngle = _fastDemoTrackingEnabled ? _fastCornerCrawlAngleDeg : TrackingCornerCrawlAngleDeg;
            if (absoluteAngleDeg <= startAngle)
            {
                return 1f;
            }

            float crawlScale = _fastDemoTrackingEnabled ? Mathf.Clamp01(_fastMinCornerSpeed / Mathf.Max(0.01f, _maxLinearSpeed)) : 0.22f;
            float t = Mathf.InverseLerp(startAngle, crawlAngle, absoluteAngleDeg);
            return Mathf.Lerp(1f, crawlScale, t);
        }

        private float ApplyFastCornerBrake(float linearCommand, PathTrackingFrame trackingFrame, out string reason)
        {
            reason = "fast_corner_not_relevant";
            if (!trackingFrame.IsValid ||
                float.IsInfinity(trackingFrame.DistanceToNextCorner) ||
                trackingFrame.NextCornerAngleDeg < _fastMediumTurnAngleDeg ||
                trackingFrame.DistanceToNextCorner > _fastCornerBrakeDistance)
            {
                return linearCommand;
            }

            float severity = Mathf.InverseLerp(_fastMediumTurnAngleDeg, _fastSevereTurnAngleDeg, trackingFrame.NextCornerAngleDeg);
            float proximity = 1f - Mathf.Clamp01(trackingFrame.DistanceToNextCorner / _fastCornerBrakeDistance);
            float targetCornerSpeed = Mathf.Lerp(_maxLinearSpeed * 0.45f, _fastMinCornerSpeed, severity);
            float brakeBlend = Mathf.Clamp01(proximity * Mathf.Lerp(0.55f, 1f, severity));
            float speedCap = Mathf.Lerp(_maxLinearSpeed, targetCornerSpeed, brakeBlend);
            reason = trackingFrame.NextCornerAngleDeg >= _fastSevereTurnAngleDeg ? "fast_severe_corner_braking" : "fast_medium_corner_braking";
            return Mathf.Min(linearCommand, Mathf.Max(_fastMinCornerSpeed, speedCap));
        }

        private float ApplyVelocityAccelerationLimit(float desiredLinear, out bool limited)
        {
            limited = false;
            if (!_fastDemoTrackingEnabled || _lastVelocityLimitTimestamp < 0f)
            {
                _lastVelocityLimitedCommand = desiredLinear;
                _lastVelocityLimitTimestamp = Time.time;
                return desiredLinear;
            }

            float dt = Mathf.Max(Time.deltaTime, Time.time - _lastVelocityLimitTimestamp);
            float limit = desiredLinear >= _lastVelocityLimitedCommand ? _accelerationLimit : _decelerationLimit;
            if (limit <= 0f || dt <= 0f)
            {
                _lastVelocityLimitedCommand = desiredLinear;
                _lastVelocityLimitTimestamp = Time.time;
                return desiredLinear;
            }

            float maxDelta = limit * dt;
            float limitedLinear = Mathf.MoveTowards(_lastVelocityLimitedCommand, desiredLinear, maxDelta);
            limited = Mathf.Abs(limitedLinear - desiredLinear) > 0.001f;
            _lastVelocityLimitedCommand = limitedLinear;
            _lastVelocityLimitTimestamp = Time.time;
            return limitedLinear;
        }

        private float ComputeUpcomingTurnSeverity(IReadOnlyList<UnityVector3> pathPoints, PathTrackingFrame trackingFrame)
        {
            if (pathPoints == null || pathPoints.Count < 3 || !trackingFrame.IsValid)
            {
                return 0f;
            }

            float start = trackingFrame.AlongPathDistance;
            float end = Mathf.Min(start + CurvatureLookaheadHorizonMeters, start + trackingFrame.RemainingPathDistance);
            float mid = Mathf.Lerp(start, end, 0.5f);
            UnityVector3 p0 = trackingFrame.ProjectedPoint;
            UnityVector3 p1 = SamplePathAtDistance(pathPoints, mid);
            UnityVector3 p2 = SamplePathAtDistance(pathPoints, end);
            UnityVector3 d0 = SafeDirection(p1 - p0, trackingFrame.LookaheadHeading);
            UnityVector3 d1 = SafeDirection(p2 - p1, d0);
            float severity = Mathf.Abs(Vector3.SignedAngle(d0, d1, Vector3.up));

            float accumulated = 0f;
            for (int i = 0; i < pathPoints.Count - 2; i++)
            {
                UnityVector3 a = Flatten(pathPoints[i]);
                UnityVector3 b = Flatten(pathPoints[i + 1]);
                UnityVector3 c = Flatten(pathPoints[i + 2]);
                float segmentLength = Vector3.Distance(a, b);
                float nextAccumulated = accumulated + segmentLength;
                if (nextAccumulated < start)
                {
                    accumulated = nextAccumulated;
                    continue;
                }

                if (accumulated > end)
                {
                    break;
                }

                UnityVector3 first = SafeDirection(b - a, Vector3.zero);
                UnityVector3 second = SafeDirection(c - b, first);
                if (first.sqrMagnitude > 0.0001f && second.sqrMagnitude > 0.0001f)
                {
                    severity = Mathf.Max(severity, Mathf.Abs(Vector3.SignedAngle(first, second, Vector3.up)));
                }

                accumulated = nextAccumulated;
            }

            return severity;
        }

        private CornerInfo FindNextRelevantCorner(IReadOnlyList<UnityVector3> pathPoints, int segmentIndex, float alongPathDistance)
        {
            if (pathPoints == null || pathPoints.Count < 3)
            {
                return CornerInfo.None;
            }

            float accumulated = 0f;
            for (int i = 0; i < pathPoints.Count - 2; i++)
            {
                UnityVector3 a = Flatten(pathPoints[i]);
                UnityVector3 b = Flatten(pathPoints[i + 1]);
                UnityVector3 c = Flatten(pathPoints[i + 2]);
                float firstSegmentLength = Vector3.Distance(a, b);
                float cornerDistanceAlongPath = accumulated + firstSegmentLength;
                accumulated = cornerDistanceAlongPath;

                if (i < segmentIndex - 1 || cornerDistanceAlongPath < alongPathDistance)
                {
                    continue;
                }

                UnityVector3 incoming = SafeDirection(b - a, Vector3.zero);
                UnityVector3 outgoing = SafeDirection(c - b, incoming);
                if (incoming.sqrMagnitude <= 0.0001f || outgoing.sqrMagnitude <= 0.0001f)
                {
                    continue;
                }

                float angleDeg = Mathf.Abs(Vector3.SignedAngle(incoming, outgoing, Vector3.up));
                if (angleDeg < 1f)
                {
                    continue;
                }

                return new CornerInfo(true, b, Mathf.Max(0f, cornerDistanceAlongPath - alongPathDistance), angleDeg);
            }

            return CornerInfo.None;
        }

        private static float ComputePathCurvatureSpeedLimit(float turnSeverityDeg, out string reason)
        {
            if (turnSeverityDeg >= VerySharpTurnAngleDeg)
            {
                reason = "very_sharp_upcoming_turn";
                return VerySharpTurnSpeedCap;
            }

            if (turnSeverityDeg >= SharpTurnAngleDeg)
            {
                reason = "sharp_upcoming_turn";
                return SharpTurnSpeedCap;
            }

            if (turnSeverityDeg >= ModerateTurnAngleDeg)
            {
                reason = "moderate_upcoming_turn";
                return ModerateTurnSpeedCap;
            }

            reason = "path_nearly_straight";
            return float.PositiveInfinity;
        }

        private float ApplyDifferentialCommandFeasibilityLimiter(float linearCommand, float angularCommand, out string reason, out float speedCap)
        {
            float absW = Mathf.Abs(angularCommand);
            if (absW < FeasibilityAngularLimitStart)
            {
                reason = "angular_low";
                speedCap = float.PositiveInfinity;
                return linearCommand;
            }

            if (absW < FeasibilityAngularMedium)
            {
                float tLow = Mathf.InverseLerp(FeasibilityAngularLimitStart, FeasibilityAngularMedium, absW);
                speedCap = Mathf.Lerp(_maxLinearSpeed, FeasibilityMediumAngularSpeedCap, tLow);
                reason = "angular_medium";
                return Mathf.Min(linearCommand, speedCap);
            }

            float tHigh = Mathf.InverseLerp(FeasibilityAngularMedium, FeasibilityAngularSaturated, absW);
            speedCap = Mathf.Lerp(FeasibilityMediumAngularSpeedCap, FeasibilitySaturatedAngularSpeedCap, tHigh);
            reason = absW >= FeasibilityAngularSaturated ? "angular_near_saturation" : "angular_high";
            return Mathf.Min(linearCommand, speedCap);
        }

        private float ComputeHeadingAngularCommand(float angleErrorDeg, float angularLimit)
        {
            // TiagoDifferentialDriveBridge uses positive angular velocity for a left turn.
            // In this scene setup, Vector3.SignedAngle around +Y is negative for the observed left yaw,
            // so the controller negates the signed angle to command the intended bridge turn direction.
            return -Mathf.Clamp(angleErrorDeg * Mathf.Deg2Rad * _angularGain, -angularLimit, angularLimit);
        }

        private bool ShouldEmergencyStop(ObstacleFrame obstacle)
        {
            bool stop = obstacle.Front.HasHit &&
                obstacle.FrontOccupancy >= EmergencyStopFrontOccupancy &&
                IsStableSince(_emergencyStopSince, EmergencyStopStableSeconds);

            if (stop != _emergencyStopActive)
            {
                _emergencyStopActive = stop;
                string state = stop ? "begin" : "end";
                string details =
                    $"state={state} frontOcc={obstacle.FrontOccupancy:F2} hitDistance={obstacle.Front.HitDistance:F2} stable={StableDuration(_emergencyStopSince):F2}s";
                Debug.Log($"{LogPrefix} EmergencyStop | {details}");
                TiagoExperimentTelemetry.RecordEvent($"emergency_stop_{state}", details);
            }

            return stop;
        }

        private StallRecoveryEntryDecision EvaluateStallRecoveryEntry(
            ObstacleFrame obstacle,
            float remainingPathDistance,
            bool stallRecoveryAllowed,
            bool modeDwellElapsed,
            float angleErrorDeg)
        {
            bool emergencyBlocked = ShouldEmergencyStop(obstacle);
            float requiredNoProgressSeconds = _lastCommandLinear < ModerateTurnSpeedCap
                ? StallNoProgressSeconds * 2.0f
                : StallNoProgressSeconds;
            bool noProgress = IsStableSince(_noProgressSince, requiredNoProgressSeconds);
            bool lowObservedMotion = Mathf.Abs(_observedLinearSpeed) <= StallObservedLinearThreshold;
            bool commandedForwardNow = _lastCommandApplied && _lastCommandLinear >= StallCommandLinearThreshold;
            bool headingInsideTrackingRegime = Mathf.Abs(angleErrorDeg) <= TrackingHardStopAngleDeg;
            bool persistentFrontBlocked = emergencyBlocked && IsStableSince(_emergencyStopSince, EmergencyStopStableSeconds);
            bool candidate = _mode == LocomotionMode.TrackPath &&
                commandedForwardNow &&
                headingInsideTrackingRegime &&
                !persistentFrontBlocked &&
                noProgress &&
                lowObservedMotion;
            UpdateSince(ref _stallCandidateSince, candidate);

            if (candidate && !_lastStallCandidateLogged)
            {
                _lastStallCandidateLogged = true;
                _lastStallReason = "physical_stall";
                string details =
                    $"reason={_lastStallReason} remainingPathDistance={remainingPathDistance:F2} noProgress={StableDuration(_noProgressSince):F2}s requiredNoProgress={requiredNoProgressSeconds:F2}s observedV={_observedLinearSpeed:F3} lastV={_lastCommandLinear:F2} angleErr={angleErrorDeg:F1} frontOcc={obstacle.FrontOccupancy:F2} emergencyBlocked={persistentFrontBlocked}";
                Debug.Log($"{LogPrefix} StallCheck | candidate | {details}");
                TiagoExperimentTelemetry.RecordEvent("stall_candidate", details);
            }
            else if (!candidate)
            {
                _lastStallCandidateLogged = false;
                _lastStallReason = "none";
            }

            bool stableCandidate = candidate;
            string gate = ResolveStallRecoveryEntryGate(
                candidate,
                stableCandidate,
                stallRecoveryAllowed,
                modeDwellElapsed,
                commandedForwardNow,
                noProgress,
                lowObservedMotion,
                headingInsideTrackingRegime,
                persistentFrontBlocked);

            return new StallRecoveryEntryDecision(
                candidate,
                stableCandidate && stallRecoveryAllowed && modeDwellElapsed,
                stableCandidate,
                _lastStallReason,
                gate,
                commandedForwardNow,
                noProgress,
                lowObservedMotion,
                persistentFrontBlocked);
        }

        private static string ResolveStallRecoveryEntryGate(
            bool candidate,
            bool stableCandidate,
            bool stallRecoveryAllowed,
            bool modeDwellElapsed,
            bool commandedForwardNow,
            bool noProgress,
            bool lowObservedMotion,
            bool headingInsideTrackingRegime,
            bool persistentFrontBlocked)
        {
            if (!candidate)
            {
                if (persistentFrontBlocked)
                {
                    return "emergency_stop_active";
                }

                if (!headingInsideTrackingRegime)
                {
                    return "heading_outside_tracking_regime";
                }

                if (!commandedForwardNow)
                {
                    return "cmd_linear_below_threshold";
                }

                if (!noProgress)
                {
                    return "remaining_distance_still_progressing";
                }

                if (!lowObservedMotion)
                {
                    return "observed_motion_not_low";
                }

                return "candidate_false";
            }

            if (!stableCandidate)
            {
                return "candidate_window_not_elapsed";
            }

            if (!stallRecoveryAllowed)
            {
                return "cooldown_active";
            }

            if (!modeDwellElapsed)
            {
                return "mode_dwell_not_elapsed";
            }

            return "enter";
        }

        private void LogStallCandidateIgnored(StallRecoveryEntryDecision decision, float remainingPathDistance)
        {
            if (_mode == LocomotionMode.StartupAlignment && decision.Candidate)
            {
                LogStallCandidateSuppressedDuringAlignment(decision, remainingPathDistance);
                return;
            }

            if (decision.Gate == "enter" || Time.time - _lastStallDiagnosticLogTime < StallDiagnosticLogCooldownSeconds)
            {
                return;
            }

            _lastStallDiagnosticLogTime = Time.time;
            string details =
                $"reason={decision.Reason} gate={decision.Gate} attempts={_stallRecoveryAttemptCount} mode={_mode} command=({_lastCommandLinear:F2},{_lastCommandAngular:F2}) " +
                $"remainingPathDistance={remainingPathDistance:F2} noProgress={StableDuration(_noProgressSince):F2}s observedV={_observedLinearSpeed:F3} " +
                $"commandedForward={decision.HasRecentForwardCommand} " +
                $"candidateStable={decision.StableCandidate} emergencyBlocked={decision.EmergencyBlocked}";
            Debug.Log($"{LogPrefix} stall_candidate_ignored | {details}");
            TiagoExperimentTelemetry.RecordEvent("stall_candidate_ignored", details);
        }

        private void LogStallCandidateSuppressedDuringAlignment(StallRecoveryEntryDecision decision, float remainingPathDistance)
        {
            if (Time.time - _lastAlignmentStallSuppressionLogTime < StallDiagnosticLogCooldownSeconds)
            {
                return;
            }

            _lastAlignmentStallSuppressionLogTime = Time.time;
            string details =
                $"reason={decision.Reason} mode={_mode} command=({_lastCommandLinear:F2},{_lastCommandAngular:F2}) remainingPathDistance={remainingPathDistance:F2} noProgress={StableDuration(_noProgressSince):F2}s observedV={_observedLinearSpeed:F3}";
            Debug.Log($"{LogPrefix} stall_candidate_suppressed_during_alignment | {details}");
            TiagoExperimentTelemetry.RecordEvent("stall_candidate_suppressed_during_alignment", details);
        }

        private void LogStallCandidateSuppressedDuringAlignment(float remainingPathDistance, float angleErrorDeg)
        {
            if (Time.time - _lastAlignmentStallSuppressionLogTime < StallDiagnosticLogCooldownSeconds)
            {
                return;
            }

            _lastAlignmentStallSuppressionLogTime = Time.time;
            string details =
                $"reason=heading_alignment mode={_mode} angleErr={angleErrorDeg:F1}deg command=({_lastCommandLinear:F2},{_lastCommandAngular:F2}) remainingPathDistance={remainingPathDistance:F2} noProgress={StableDuration(_noProgressSince):F2}s observedV={_observedLinearSpeed:F3} observedW={_observedAngularSpeed:F3}";
            Debug.Log($"{LogPrefix} stall_candidate_suppressed_during_alignment | {details}");
            TiagoExperimentTelemetry.RecordEvent("stall_candidate_suppressed_during_alignment", details);
        }

        private bool HasStartupAlignmentSoftExitPath(PathFrame frame)
        {
            bool hasFrameHeading = Flatten(frame.FollowHeading).sqrMagnitude > 0.0001f;
            bool hasActivePath = (_lastActivePathPoints != null && _lastActivePathPoints.Length >= 2) ||
                (_lockedActivePathPoints != null && _lockedActivePathPoints.Length >= 2) ||
                (_lastNavMeshPathCorners != null && _lastNavMeshPathCorners.Length >= 2) ||
                _hasLockedActivePath ||
                _hasLastActiveLookahead;
            return hasFrameHeading && hasActivePath;
        }

        private void LogStartupAlignmentSoftExitAfterNoProgress(PathFrame frame, float remainingDistance, float angleErrorDeg, float noProgressSeconds)
        {
            var payload = new Dictionary<string, object>
            {
                ["angleErrorDeg"] = angleErrorDeg,
                ["exitDeg"] = HeadingAlignExitAngleDeg,
                ["softToleranceDeg"] = StartupAlignmentSoftExitToleranceDeg,
                ["noProgressSeconds"] = noProgressSeconds,
                ["requiredNoProgressSeconds"] = StartupAlignmentSoftExitNoProgressSeconds,
                ["remainingDistance"] = remainingDistance,
                ["pathSource"] = _lastActivePathSource.ToString(),
                ["targetId"] = _diagnosticCurrentTargetId,
                ["targetPosition"] = DiagnosticActivePathTargetPosition,
                ["pathVersion"] = _lockedPathVersion,
                ["segmentIndex"] = _lastActiveSegmentIndex,
                ["reason"] = "near_exit_no_progress",
                ["guidePoint"] = frame.GuidePoint
            };
            Debug.Log(
                $"{LogPrefix} startup_alignment_soft_exit_after_no_progress | angleErr={angleErrorDeg:F1}deg exit={HeadingAlignExitAngleDeg:F1}deg tolerance={StartupAlignmentSoftExitToleranceDeg:F1}deg noProgress={noProgressSeconds:F2}s remaining={remainingDistance:F2} pathSource={_lastActivePathSource} targetId={_diagnosticCurrentTargetId}");
            TiagoExperimentTelemetry.LogEvent("startup_alignment_soft_exit_after_no_progress", payload);
        }

        private void LogHeadingAlignmentSample(PathFrame frame, float remainingDistance, float angleErrorDeg)
        {
            if (SuppressVerboseAndroidDiagnostics ||
                Time.time - _lastHeadingAlignmentSampleLogTime < HeadingAlignmentSampleLogIntervalSeconds)
            {
                return;
            }

            _lastHeadingAlignmentSampleLogTime = Time.time;
            string details =
                $"angleErr={angleErrorDeg:F1}deg exit={HeadingAlignExitAngleDeg:F1}deg stable={StableDuration(_headingAlignmentExitStableSince):F2}s remainingPathDistance={remainingDistance:F2} guide={FormatVector(frame.GuidePoint)}";
            Debug.Log($"{LogPrefix} HeadingAlignment | sample | {details}");
            TiagoExperimentTelemetry.RecordEvent("heading_alignment_sample", details);
        }

        private void LogCurvatureTrackingCommand(
            PathTrackingFrame trackingFrame,
            float angleErrorDeg,
            float linearCommand,
            float angularCommand,
            float speedScale,
            string mode)
        {
            if (SuppressVerboseAndroidDiagnostics ||
                Time.time - _lastCurvatureCommandLogTime < CurvatureCommandLogIntervalSeconds)
            {
                return;
            }

            _lastCurvatureCommandLogTime = Time.time;
            string details =
                $"angleErrorDeg={angleErrorDeg:F1} lookaheadDistance={_pathLookAheadDistance:F2} v_cmd={linearCommand:F2} w_cmd={angularCommand:F2} speedScale={speedScale:F2} mode={mode} segmentIndex={trackingFrame.SegmentIndex} remainingPathDistance={trackingFrame.RemainingPathDistance:F2}";
            Debug.Log($"{LogPrefix} path_tracking_curvature_command | {details}");
            TiagoExperimentTelemetry.RecordEvent("path_tracking_curvature_command", details);
        }

        private void LogPathCurvatureSpeedLimited(float turnSeverityDeg, float speedCap, float vBefore, float vAfter, string reason)
        {
            if (SuppressVerboseAndroidDiagnostics ||
                Time.time - _lastPathCurvatureLimitedLogTime < ActuationDiagnosticIntervalSeconds)
            {
                return;
            }

            _lastPathCurvatureLimitedLogTime = Time.time;
            string details =
                $"turnSeverityDeg={turnSeverityDeg:F1} horizonMeters={CurvatureLookaheadHorizonMeters:F2} speedCap={speedCap:F2} vBefore={vBefore:F2} vAfter={vAfter:F2} reason={reason}";
            Debug.Log($"{LogPrefix} path_curvature_speed_limited | {details}");
            TiagoExperimentTelemetry.RecordEvent("path_curvature_speed_limited", details);
        }

        private void LogDifferentialCommandFeasibilityLimited(float vBefore, float angularCommand, float vAfter, float speedCap, string reason)
        {
            if (SuppressVerboseAndroidDiagnostics ||
                Time.time - _lastFeasibilityLimitedLogTime < ActuationDiagnosticIntervalSeconds)
            {
                return;
            }

            _lastFeasibilityLimitedLogTime = Time.time;
            string details =
                $"vBefore={vBefore:F2} w={angularCommand:F2} absW={Mathf.Abs(angularCommand):F2} speedCap={speedCap:F2} vAfter={vAfter:F2} reason={reason}";
            Debug.Log($"{LogPrefix} differential_command_feasibility_limited | {details}");
            TiagoExperimentTelemetry.RecordEvent("differential_command_feasibility_limited", details);
        }

        private void LogFastCornerBraking(PathTrackingFrame trackingFrame, float vBefore, float vAfter, string reason)
        {
            if (SuppressVerboseAndroidDiagnostics ||
                Time.time - _lastPathCurvatureLimitedLogTime < ActuationDiagnosticIntervalSeconds)
            {
                return;
            }

            _lastPathCurvatureLimitedLogTime = Time.time;
            string details =
                $"active_drive_profile={_activeDriveProfile} active_autonomous_policy={_activeAutonomousPolicy} reason={reason} distanceToNextCorner={trackingFrame.DistanceToNextCorner:F2} nextCornerAngleDeg={trackingFrame.NextCornerAngleDeg:F1} brakeDistance={_fastCornerBrakeDistance:F2} minCornerSpeed={_fastMinCornerSpeed:F2} vBefore={vBefore:F2} vAfter={vAfter:F2}";
            Debug.Log($"{LogPrefix} fast_corner_braking | {details}");
            TiagoExperimentTelemetry.RecordEvent("fast_corner_braking", details);
        }

        private void LogCornerCrawlSpeedProfileActive(float turnSeverityDeg, float angleErrorDeg, float linearCommand, float angularCommand, string reason)
        {
            if (SuppressVerboseAndroidDiagnostics ||
                Time.time - _lastCornerCrawlProfileLogTime < ActuationDiagnosticIntervalSeconds)
            {
                return;
            }

            _lastCornerCrawlProfileLogTime = Time.time;
            string details =
                $"turnSeverityDeg={turnSeverityDeg:F1} angleErrorDeg={angleErrorDeg:F1} v_cmd={linearCommand:F2} w_cmd={angularCommand:F2} reason={reason}";
            Debug.Log($"{LogPrefix} corner_crawl_speed_profile_active | {details}");
            TiagoExperimentTelemetry.RecordEvent("corner_crawl_speed_profile_active", details);
        }

        private void LogTrackingCommandPipeline(
            float angleErrorDeg,
            float turnSeverityDeg,
            float vBase,
            float vAfterHeading,
            float vAfterCurvature,
            float vFinal,
            float wFinal,
            string mode)
        {
            if (SuppressVerboseAndroidDiagnostics ||
                Time.time - _lastTrackingPipelineLogTime < CurvatureCommandLogIntervalSeconds)
            {
                return;
            }

            _lastTrackingPipelineLogTime = Time.time;
            string details =
                $"active_drive_profile={_activeDriveProfile} active_autonomous_policy={_activeAutonomousPolicy} reason={_lastSpeedReductionReason} angleErrorDeg={angleErrorDeg:F1} turnSeverityDeg={turnSeverityDeg:F1} distanceToNextCorner={_lastDistanceToNextCorner:F2} nextCornerAngleDeg={_lastNextCornerAngleDeg:F1} activeLookaheadDistance={_lastCommandLookaheadDistance:F2} vBase={vBase:F2} vAfterHeading={vAfterHeading:F2} vAfterCurvature={vAfterCurvature:F2} vFinal={vFinal:F2} wFinal={wFinal:F2} mode={mode} curvatureProfilerEnabled={EnablePathCurvatureSpeedProfiler} feasibilityLimiterEnabled={EnableDifferentialFeasibilityLimiter}";
            Debug.Log($"{LogPrefix} tracking_command_pipeline | {details}");
            TiagoExperimentTelemetry.RecordEvent("tracking_command_pipeline", details);
        }

        private void LogTrackHeadingSpeedLimited(
            PathTrackingFrame trackingFrame,
            float angleErrorDeg,
            float linearCommand,
            float angularCommand,
            float speedScale,
            string mode)
        {
            if (SuppressVerboseAndroidDiagnostics ||
                Time.time - _lastTrackSpeedLimitedLogTime < ActuationDiagnosticIntervalSeconds)
            {
                return;
            }

            _lastTrackSpeedLimitedLogTime = Time.time;
            string details =
                $"active_drive_profile={_activeDriveProfile} active_autonomous_policy={_activeAutonomousPolicy} angleErrorDeg={angleErrorDeg:F1} start={(_fastDemoTrackingEnabled ? _fastHeadingSpeedLimitStartAngleDeg : TrackingSpeedLimitStartAngleDeg):F1} crawlAngle={(_fastDemoTrackingEnabled ? _fastCornerCrawlAngleDeg : TrackingCornerCrawlAngleDeg):F1} v_cmd={linearCommand:F2} w_cmd={angularCommand:F2} speedScale={speedScale:F2} mode={mode} lookaheadDistance={trackingFrame.ActiveLookaheadDistance:F2} lookahead={FormatVector(trackingFrame.LookaheadPoint)} segmentIndex={trackingFrame.SegmentIndex}";
            Debug.Log($"{LogPrefix} track_heading_speed_limited | {details}");
            TiagoExperimentTelemetry.RecordEvent("track_heading_speed_limited", details);
        }

        private void LogTrackHeadingHardStop(PathTrackingFrame trackingFrame, float angleErrorDeg, float angularCommand)
        {
            if (SuppressVerboseAndroidDiagnostics ||
                Time.time - _lastTrackHardStopLogTime < ActuationDiagnosticIntervalSeconds)
            {
                return;
            }

            _lastTrackHardStopLogTime = Time.time;
            string details =
                $"angleErrorDeg={angleErrorDeg:F1} threshold={TrackingHardStopAngleDeg:F1} v_cmd=0.00 w_cmd={angularCommand:F2} mode=TrackPathHardStop lookahead={FormatVector(trackingFrame.LookaheadPoint)} segmentIndex={trackingFrame.SegmentIndex}";
            Debug.Log($"{LogPrefix} track_heading_hard_stop | {details}");
            TiagoExperimentTelemetry.RecordEvent("track_heading_hard_stop", details);
        }

        private void LogAlignmentReentrySuppressedDuringTracking(PathFrame frame, float remainingDistance, float angleErrorDeg)
        {
            if (SuppressVerboseAndroidDiagnostics ||
                Time.time - _lastAlignmentReentrySuppressedLogTime < ActuationDiagnosticIntervalSeconds)
            {
                return;
            }

            _lastAlignmentReentrySuppressedLogTime = Time.time;
            string details =
                $"angleErrorDeg={angleErrorDeg:F1} previousEnter={HeadingAlignEnterAngleDeg:F1} mode=TrackPath action=curvature_tracking remainingPathDistance={remainingDistance:F2} guide={FormatVector(frame.GuidePoint)}";
            Debug.Log($"{LogPrefix} alignment_reentry_suppressed_during_tracking | {details}");
            TiagoExperimentTelemetry.RecordEvent("alignment_reentry_suppressed_during_tracking", details);
        }

        private void LogLookaheadCommand(PathTrackingFrame trackingFrame, float angleErrorDeg, float linearCommand, float angularCommand, string mode)
        {
            if (SuppressVerboseAndroidDiagnostics ||
                Time.time - _lastLookaheadLogTime < ActuationDiagnosticIntervalSeconds)
            {
                return;
            }

            _lastLookaheadLogTime = Time.time;
            string details =
                $"active_drive_profile={_activeDriveProfile} active_autonomous_policy={_activeAutonomousPolicy} mode={mode} reduction={_lastSpeedReductionReason} lookahead={FormatVector(trackingFrame.LookaheadPoint)} lookaheadDistance={trackingFrame.ActiveLookaheadDistance:F2} segmentIndex={trackingFrame.SegmentIndex} " +
                $"source={trackingFrame.Source} alongPath={trackingFrame.AlongPathDistance:F2} distanceToPath={trackingFrame.DistanceToPath:F2} remainingPathDistance={trackingFrame.RemainingPathDistance:F2} " +
                $"distanceToNextCorner={trackingFrame.DistanceToNextCorner:F2} nextCornerAngleDeg={trackingFrame.NextCornerAngleDeg:F1} angleError={angleErrorDeg:F1}deg v={linearCommand:F2} w={angularCommand:F2}";
            Debug.Log($"{LogPrefix} Lookahead | {details}");
            TiagoExperimentTelemetry.RecordEvent("lookahead_follow", details);
        }

        private void LogPathStatus(NavMeshPath path, PathTrackingFrame trackingFrame)
        {
            if (Time.time - _lastPathStatusLogTime < ActuationDiagnosticIntervalSeconds)
            {
                return;
            }

            _lastPathStatusLogTime = Time.time;
            if (!SuppressVerboseAndroidDiagnostics)
            {
                string details =
                    $"status={path.status} corners={path.corners.Length} segmentIndex={trackingFrame.SegmentIndex} remainingPathDistance={trackingFrame.RemainingPathDistance:F2}";
                Debug.Log($"{LogPrefix} Path | {details}");
                TiagoExperimentTelemetry.RecordEvent("navmesh_path_status", details);
            }

            if (path.status == NavMeshPathStatus.PathInvalid)
            {
                LogP40NavTraceActivePathOwnershipSnapshot("PathInvalid", path.status.ToString());
                LogP40NavTraceObstacleRegistrySnapshot("path_invalid", "PathInvalid");
            }

            LogPathClearanceDiagnostics(path);
            LogPathFootprintClearanceDiagnostics(trackingFrame);
        }

        private static void LogNavMeshClearancePolicy(float safetyMarginMeters)
        {
            string recommendation =
                $"Agent Radius={NavMeshRecommendedAgentRadiusMeters:F2}; if_still_failing={NavMeshFallbackAgentRadiusMeters:F2}; expand_NavMeshObstacle_carving_bounds_XZ=+0.10_to_0.15m; rebake_NavMesh";
            Debug.Log($"{LogPrefix} [NavMeshConfig] clearance_policy | safetyMargin={safetyMarginMeters:F2} | recommendedAgentRadius={NavMeshRecommendedAgentRadiusMeters:F2} | recommendation={recommendation}");
            TiagoExperimentTelemetry.RecordEvent(
                "navmesh_clearance_policy",
                $"safetyMargin={safetyMarginMeters:F2} recommendedAgentRadius={NavMeshRecommendedAgentRadiusMeters:F2} recommendation={recommendation}");
        }

        private void LogPathClearanceDiagnostics(NavMeshPath path)
        {
            UnityVector3[] corners = path != null ? path.corners : Array.Empty<UnityVector3>();
            if (corners.Length == 0)
            {
                return;
            }

            float minClearance = float.PositiveInfinity;
            int minCornerIndex = -1;
            Collider minCollider = null;

            for (int i = 0; i < corners.Length; i++)
            {
                UnityVector3 corner = Flatten(corners[i]);
                int nearbyColliderCount = GetDiagnosticOverlaps(
                    corner,
                    NavMeshClearanceProbeRadiusMeters,
                    out Collider[] nearbyColliders);

                for (int j = 0; j < nearbyColliderCount; j++)
                {
                    Collider candidate = nearbyColliders[j];
                    if (candidate == null)
                    {
                        continue;
                    }

                    if (IsNavigationReferenceCollider(candidate) || IsUserOrXrRigCollider(candidate))
                    {
                        continue;
                    }

                    if (IsIgnoredClearanceDiagnosticCollider(candidate))
                    {
                        continue;
                    }

                    UnityVector3 closestPoint = Flatten(GetDiagnosticClosestPoint(candidate, corner, out _));
                    float clearance = Vector3.Distance(corner, closestPoint);
                    if (clearance < minClearance)
                    {
                        minClearance = clearance;
                        minCornerIndex = i;
                        minCollider = candidate;
                    }
                }
            }

            if (minCollider == null)
            {
                if (SuppressVerboseAndroidDiagnostics)
                {
                    return;
                }

                Debug.Log($"{LogPrefix} [NavMeshPath] clearance_check | minClearance=NaN | cornerIndex=-1 | object=None");
                TiagoExperimentTelemetry.RecordEvent(
                    "navmesh_path_clearance_check",
                    "minClearance=NaN cornerIndex=-1 object=None");
                return;
            }

            bool tooClose = minClearance < _navMeshSafetyMarginMeters;
            if (SuppressVerboseAndroidDiagnostics && !tooClose)
            {
                return;
            }

            string objectName = minCollider.gameObject != null ? minCollider.gameObject.name : minCollider.name;
            string colliderDetails = FormatColliderDiagnostic(minCollider, referencePoint: _navigationReference != null ? Flatten(_navigationReference.position) : Vector3.zero, pathPoint: Flatten(corners[minCornerIndex]), sampleIndex: minCornerIndex, considered: true);
            if (!SuppressVerboseAndroidDiagnostics)
            {
                Debug.Log($"{LogPrefix} [NavMeshPath] clearance_check | minClearance={minClearance:F2} | cornerIndex={minCornerIndex} | object={objectName} | {colliderDetails}");
                TiagoExperimentTelemetry.RecordEvent(
                    "navmesh_path_clearance_check",
                    $"minClearance={minClearance:F2} cornerIndex={minCornerIndex} object={objectName} {colliderDetails}");
            }

            if (tooClose)
            {
                Debug.LogWarning($"{LogPrefix} [NavMeshPath] path_too_close_to_obstacle | minClearance={minClearance:F2} | safetyMargin={_navMeshSafetyMarginMeters:F2} | cornerIndex={minCornerIndex} | object={objectName} | {colliderDetails} | recommendation=increase_agent_radius_or_carving_bounds");
                TiagoExperimentTelemetry.RecordEvent(
                    "navmesh_path_too_close_to_obstacle",
                    $"minClearance={minClearance:F2} safetyMargin={_navMeshSafetyMarginMeters:F2} cornerIndex={minCornerIndex} object={objectName} {colliderDetails} recommendation=increase_agent_radius_or_carving_bounds");
            }
        }

        private void LogPathFootprintClearanceDiagnostics(PathTrackingFrame trackingFrame)
        {
            if (!FootprintClearanceCheckEnabled || !trackingFrame.IsValid || _lastActivePathPoints == null || _lastActivePathPoints.Length == 0)
            {
                return;
            }

            if (Time.time - _lastFootprintClearanceLogTime < ActuationDiagnosticIntervalSeconds)
            {
                return;
            }

            _lastFootprintClearanceLogTime = Time.time;
            UnityVector3 robotPosition = _navigationReference != null ? Flatten(_navigationReference.position) : trackingFrame.ProjectedPoint;
            float start = trackingFrame.AlongPathDistance;
            float end = Mathf.Min(start + FootprintCheckHorizonMeters, start + trackingFrame.RemainingPathDistance);
            float minClearance = float.PositiveInfinity;
            int minSampleIndex = -1;
            UnityVector3 minSamplePoint = Vector3.zero;
            Collider minCollider = null;
            int sampleIndex = 0;

            for (float distance = start; distance <= end + 0.001f; distance += FootprintSampleSpacingMeters)
            {
                UnityVector3 samplePoint = SamplePathAtDistance(_lastActivePathPoints, distance);
                int nearbyColliderCount = GetDiagnosticOverlaps(
                    samplePoint,
                    FootprintRadiusMeters + FootprintClearanceSafetyMargin,
                    out Collider[] nearbyColliders);

                for (int i = 0; i < nearbyColliderCount; i++)
                {
                    Collider candidate = nearbyColliders[i];
                    if (candidate == null ||
                        IsNavigationReferenceCollider(candidate) ||
                        IsUserOrXrRigCollider(candidate) ||
                        IsIgnoredClearanceDiagnosticCollider(candidate))
                    {
                        continue;
                    }

                    UnityVector3 closestPoint = Flatten(GetDiagnosticClosestPoint(candidate, samplePoint, out _));
                    float centerDistance = Vector3.Distance(samplePoint, closestPoint);
                    float footprintClearance = centerDistance - FootprintRadiusMeters;
                    if (footprintClearance < minClearance)
                    {
                        minClearance = footprintClearance;
                        minSampleIndex = sampleIndex;
                        minSamplePoint = samplePoint;
                        minCollider = candidate;
                    }
                }

                sampleIndex++;
            }

            bool pass = minCollider == null || minClearance >= FootprintClearanceSafetyMargin;
            _lastFootprintClearance = new FootprintClearanceSnapshot(
                pass,
                minCollider != null,
                minClearance,
                minSampleIndex,
                minSamplePoint,
                minCollider != null ? BuildColliderPath(minCollider.transform) : "None");

            if (SuppressVerboseAndroidDiagnostics && pass)
            {
                return;
            }

            string summaryDetails = minCollider == null
                ? $"pass=True horizonMeters={FootprintCheckHorizonMeters:F2} radius={FootprintRadiusMeters:F2} safetyMargin={FootprintClearanceSafetyMargin:F2} minClearance=NaN obstacle=None"
                : $"pass={pass} horizonMeters={FootprintCheckHorizonMeters:F2} radius={FootprintRadiusMeters:F2} safetyMargin={FootprintClearanceSafetyMargin:F2} minClearance={minClearance:F2} sampleIndex={minSampleIndex} samplePoint={FormatVector(minSamplePoint)} obstacle={BuildColliderPath(minCollider.transform)} {FormatColliderDiagnostic(minCollider, robotPosition, minSamplePoint, minSampleIndex, true)}";

            if (!SuppressVerboseAndroidDiagnostics)
            {
                Debug.Log($"{LogPrefix} path_footprint_clearance_summary | {summaryDetails}");
                TiagoExperimentTelemetry.RecordEvent("path_footprint_clearance_summary", summaryDetails);

                if (minCollider != null)
                {
                    TiagoExperimentTelemetry.RecordEvent("path_footprint_clearance_sample", summaryDetails);
                }
            }

            if (!pass)
            {
                Debug.LogWarning($"{LogPrefix} path_footprint_clearance_failed | {summaryDetails}");
                TiagoExperimentTelemetry.RecordEvent("path_footprint_clearance_failed", summaryDetails);
                LogP40NavTracePathBlockerDiagnostic(minCollider, minSamplePoint, minClearance, FootprintCheckHorizonMeters, robotPosition);
                LogP40NavTraceObstacleRegistrySnapshot("path_footprint_clearance_failed", "footprint_clearance_failed");
                LogP40NavTraceActivePathOwnershipSnapshot("path_footprint_clearance_failed", "footprint_clearance_failed");
            }
        }

        private int GetDiagnosticOverlaps(UnityVector3 center, float radius, out Collider[] colliders)
        {
            int count = Physics.OverlapSphereNonAlloc(
                center,
                radius,
                _diagnosticOverlapBuffer,
                _obstacleClearanceMask,
                QueryTriggerInteraction.Ignore);
            if (count < _diagnosticOverlapBuffer.Length)
            {
                colliders = _diagnosticOverlapBuffer;
                return count;
            }

            colliders = Physics.OverlapSphere(
                center,
                radius,
                _obstacleClearanceMask,
                QueryTriggerInteraction.Ignore);
            return colliders.Length;
        }

        private bool IsNavigationReferenceCollider(Collider candidate)
        {
            return _navigationReference != null &&
                candidate.transform != null &&
                candidate.transform.root == _navigationReference.root;
        }

        private static bool IsIgnoredClearanceDiagnosticCollider(Collider candidate)
        {
            if (candidate == null)
            {
                return true;
            }

            string name = candidate.gameObject != null ? candidate.gameObject.name : candidate.name;
            string rootName = candidate.transform != null && candidate.transform.root != null
                ? candidate.transform.root.name
                : string.Empty;
            string combined = $"{name} {rootName}".ToLowerInvariant();
            return combined.Contains("casterflooroverlay") ||
                combined.Contains("floor") ||
                combined.Contains("ground");
        }

        private static string FormatColliderDiagnostic(Collider collider, UnityVector3 referencePoint, UnityVector3 pathPoint, int sampleIndex, bool considered)
        {
            if (collider == null)
            {
                return "collider=None";
            }

            Bounds bounds = collider.bounds;
            string layerName = LayerMask.LayerToName(collider.gameObject.layer);
            UnityVector3 robotClosestPoint = Flatten(GetDiagnosticClosestPoint(collider, referencePoint, out string robotClosestPointReason));
            UnityVector3 pathClosestPoint = Flatten(GetDiagnosticClosestPoint(collider, pathPoint, out string pathClosestPointReason));
            float robotDistance = Vector3.Distance(referencePoint, robotClosestPoint);
            float pathDistance = Vector3.Distance(pathPoint, pathClosestPoint);
            return
                $"colliderPath={BuildColliderPath(collider.transform)} layer={collider.gameObject.layer}:{layerName} tag={collider.tag} isTrigger={collider.isTrigger} " +
                $"boundsCenter={FormatVector(bounds.center)} boundsSize={FormatVector(bounds.size)} distanceToRobot={robotDistance:F2} distanceToPathPoint={pathDistance:F2} " +
                $"closestPointRobot={robotClosestPointReason} closestPointPath={pathClosestPointReason} sampleIndex={sampleIndex} considered={considered}";
        }

        private static UnityVector3 GetDiagnosticClosestPoint(Collider collider, UnityVector3 point, out string reason)
        {
            if (collider == null)
            {
                reason = "collider_null";
                return point;
            }

            if (SupportsClosestPointDiagnostic(collider))
            {
                reason = "collider_closest_point";
                return collider.ClosestPoint(point);
            }

            reason = "unsupported_collider_for_closest_point_bounds_fallback";
            return collider.bounds.ClosestPoint(point);
        }

        private static bool SupportsClosestPointDiagnostic(Collider collider)
        {
            return collider is BoxCollider ||
                collider is SphereCollider ||
                collider is CapsuleCollider ||
                (collider is MeshCollider meshCollider && meshCollider.convex);
        }

        private static string BuildColliderPath(Transform transform)
        {
            if (transform == null)
            {
                return "<null>";
            }

            string path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = $"{transform.name}/{path}";
            }

            return path;
        }

        private DriveCommand ApplyLateralAvoidanceBias(DriveCommand command, ObstacleFrame obstacle)
        {
            bool hasDominantLateral = TryGetDominantLateralObstacle(obstacle, out string dominantSide, out BypassSide escapeSide, out float dominantOccupancy, out float oppositeOccupancy);
            if (!hasDominantLateral)
            {
                _lateralObstacleHighSince = float.PositiveInfinity;
                if (obstacle.HasAnyObstacle)
                {
                    LogThreeRayPerceptionSample(obstacle, "None", command.Angular);
                }

                return command;
            }

            float occupancyStrength = Mathf.Clamp01((dominantOccupancy - LateralAvoidanceOccupancyThreshold) / (1f - LateralAvoidanceOccupancyThreshold));
            float targetAngularMagnitude = Mathf.Min(_maxAngularSpeed, Mathf.Lerp(LateralAvoidanceMinAngularSpeed, _maxAngularSpeed, occupancyStrength));
            float lateralAngular = TurnSideToAngularCommand(escapeSide, targetAngularMagnitude);
            float angularCommand = Mathf.Sign(command.Angular) == Mathf.Sign(lateralAngular)
                ? Mathf.Sign(lateralAngular) * Mathf.Max(Mathf.Abs(command.Angular), Mathf.Abs(lateralAngular))
                : lateralAngular;

            float linearScale = Mathf.Lerp(0.45f, LateralAvoidanceLinearScale, occupancyStrength);
            float linearCap = _maxLinearSpeed * linearScale;
            float linearCommand = Mathf.Min(command.Linear, linearCap);

            UpdateSince(ref _lateralObstacleHighSince, true);
            LogLateralAvoidanceBias(dominantSide, escapeSide, dominantOccupancy, oppositeOccupancy, command, linearCommand, angularCommand);
            LogThreeRayPerceptionSample(obstacle, FormatSide(escapeSide), angularCommand);
            bool weakResponse = IsStableSince(_lateralObstacleHighSince, LateralAvoidanceWeakResponseSeconds) &&
                IsStableSince(_noProgressSince, LateralAvoidanceWeakResponseSeconds) &&
                Mathf.Abs(_observedAngularSpeed) < LateralAvoidanceObservedAngularThreshold;
            if (weakResponse)
            {
                LogAnomaly(
                    "lateral_obstacle_response_too_weak",
                    $"dominant={dominantSide} turn={FormatSide(escapeSide)} dominantOcc={dominantOccupancy:F2} oppositeOcc={oppositeOccupancy:F2} observedW={_observedAngularSpeed:F3} vCmd={linearCommand:F2} wCmd={angularCommand:F2} noProgress={StableDuration(_noProgressSince):F2}s");
            }

            return new DriveCommand(linearCommand, angularCommand, command.AngleErrorDeg);
        }

        private DriveCommand ComputeStallRecoveryCommand(UnityVector3 currentPosition, UnityVector3 correctedHeading)
        {
            float elapsed = Time.time - _stallRecoveryStartedAt;
            StallRecoveryPhase phase = GetStallRecoveryPhase(elapsed);

            switch (phase)
            {
                case StallRecoveryPhase.Stop:
                    return new DriveCommand(0f, 0f, 0f);

                case StallRecoveryPhase.ReverseArc:
                    return new DriveCommand(
                        StallRecoveryReverseArcSpeed,
                        Mathf.Clamp(_stallRecoveryReverseArcDirectionSign * StallRecoveryReverseArcAngularSpeed, -_maxAngularSpeed, _maxAngularSpeed),
                        180f);

                default:
                    return new DriveCommand(0f, 0f, 0f);
            }
        }

        private DriveCommand ComputeHeadingCommand(UnityVector3 targetHeading, UnityVector3 correctedHeading, float remainingDistance, bool forcePivot)
        {
            float angleErrorDeg = ComputeSignedAngleDegrees(correctedHeading, targetHeading);
            float absoluteAngleDeg = Mathf.Abs(angleErrorDeg);
            float angularCommand = -Mathf.Clamp(angleErrorDeg * Mathf.Deg2Rad * _angularGain, -_maxAngularSpeed, _maxAngularSpeed);

            if (forcePivot || absoluteAngleDeg > _rotateInPlaceAngleDeg)
            {
                return new DriveCommand(0f, angularCommand, angleErrorDeg);
            }

            float distanceFactor = Mathf.Clamp01(remainingDistance / _slowdownDistance);
            float headingFactor = Mathf.Clamp01(Mathf.Cos(absoluteAngleDeg * Mathf.Deg2Rad));
            float linearCommand = _maxLinearSpeed * distanceFactor * headingFactor;
            return new DriveCommand(linearCommand, angularCommand, angleErrorDeg);
        }

        private DriveCommand ComputeCornerTurnCommand(
            PathFrame frame,
            UnityVector3 currentPosition,
            UnityVector3 correctedHeading,
            ObstacleFrame obstacle,
            float remainingDistance)
        {
            CornerResolution resolution = EvaluateCornerResolution(currentPosition, correctedHeading, obstacle);
            if (!_hasActiveCorner || !resolution.IsAligned)
            {
                return ComputeHeadingCommand(frame.CornerHeading, correctedHeading, remainingDistance, true);
            }

            float angleErrorDeg = ComputeSignedAngleDegrees(correctedHeading, frame.CornerHeading);
            float angularCommand = -Mathf.Clamp(angleErrorDeg * Mathf.Deg2Rad * _angularGain, -_maxAngularSpeed, _maxAngularSpeed);
            bool frontCompromised = obstacle.FrontOccupancy >= CornerFrontCompromisedOccupancy;
            float linearFactor = frontCompromised
                ? CornerFollowThroughBlockedLinearFactor
                : CornerFollowThroughLinearFactor;
            float linearCommand = _maxLinearSpeed * linearFactor;

            if (!frontCompromised)
            {
                linearCommand = Mathf.Max(linearCommand, CornerFollowThroughMinLinearSpeed);
            }

            TrackCornerFollowThroughProgress(resolution, obstacle, linearCommand, angularCommand);

            return new DriveCommand(linearCommand, angularCommand, angleErrorDeg);
        }

        private void TrackCornerFollowThroughProgress(
            CornerResolution resolution,
            ObstacleFrame obstacle,
            float linearCommand,
            float angularCommand)
        {
            if (!_hasActiveCorner || !resolution.IsAligned || resolution.IsChanneled)
            {
                _cornerFollowThroughNoProgressSince = float.PositiveInfinity;
                return;
            }

            if (resolution.OutgoingProgress > _bestCornerFollowThroughProgress + ProgressEpsilonMeters)
            {
                _bestCornerFollowThroughProgress = resolution.OutgoingProgress;
                _cornerFollowThroughNoProgressSince = float.PositiveInfinity;
            }
            else if (float.IsNegativeInfinity(_bestCornerFollowThroughProgress))
            {
                _bestCornerFollowThroughProgress = resolution.OutgoingProgress;
                _cornerFollowThroughNoProgressSince = Time.time;
            }
            else if (float.IsPositiveInfinity(_cornerFollowThroughNoProgressSince))
            {
                _cornerFollowThroughNoProgressSince = Time.time;
            }

            LogCornerEventThrottled(
                "follow_through",
                $"command_boosted error={resolution.ErrorDeg:F1}deg progress={resolution.OutgoingProgress:F2}m bestProgress={_bestCornerFollowThroughProgress:F2}m front={obstacle.FrontOccupancy:F2} vCmd={linearCommand:F2} wCmd={angularCommand:F2}");

            bool frontFree = obstacle.FrontOccupancy < CornerFrontCompromisedOccupancy;
            bool noProgressStable = IsStableSince(_cornerFollowThroughNoProgressSince, CornerFollowThroughNoProgressSeconds);
            if (frontFree && linearCommand >= CornerFollowThroughMinLinearSpeed && noProgressStable)
            {
                LogAnomaly(
                    "corner_follow_through_commanded_no_progress",
                    $"duration={StableDuration(_cornerFollowThroughNoProgressSince):F2}s progress={resolution.OutgoingProgress:F2}m bestProgress={_bestCornerFollowThroughProgress:F2}m front={obstacle.FrontOccupancy:F2} vCmd={linearCommand:F2} wCmd={angularCommand:F2}");
            }
        }

        private DriveCommand ComputeBypassCommand(ObstacleFrame obstacle)
        {
            float elapsed = Time.time - _bypassStartedAt;
            float occupancy = Mathf.Max(obstacle.FrontOccupancy, obstacle.LeftOccupancy, obstacle.RightOccupancy);
            float turnStrength = Mathf.Lerp(0.65f, 1f, Mathf.Clamp01(occupancy));
            float angularCommand = Mathf.Clamp(
                TurnSideToAngularCommand(_bypassSide, _maxAngularSpeed * Mathf.Max(_avoidanceAngularStrength, 0.25f) * turnStrength),
                -_maxAngularSpeed,
                _maxAngularSpeed);

            bool veryClose = obstacle.FrontOccupancy > 0.55f;
            float linearScale = veryClose ? 0f : Mathf.Max(0.12f, _avoidanceLinearReductionFactor);
            float linearCommand = _maxLinearSpeed * linearScale;

            if (elapsed > BypassMinDurationSeconds && obstacle.FrontOccupancy <= ObstacleExitOccupancy)
            {
                linearCommand = _maxLinearSpeed * Mathf.Max(0.25f, _avoidanceLinearReductionFactor);
            }

            EvaluateAvoidanceTurnDirection(obstacle, angularCommand);
            LogAvoidanceDecision("bypass_active", obstacle, _navigationReference != null ? ApplyHeadingOffset(Flatten(_navigationReference.forward), _headingOffsetDegrees) : Vector3.forward, _bypassSide, linearCommand, angularCommand);
            return new DriveCommand(linearCommand, angularCommand, ExpectedYawDeltaSignForTurnSide(_bypassSide) * 90f);
        }

        private ObstacleFrame SenseObstacles(UnityVector3 currentPosition, UnityVector3 correctedHeading)
        {
            if (_avoidanceDetectionDistance <= 0f || correctedHeading.sqrMagnitude <= 0.0001f)
            {
                return ObstacleFrame.None;
            }

            UnityVector3 origin = currentPosition + Vector3.up * 0.25f;
            UnityVector3 forward = correctedHeading.normalized;
            UnityVector3 left = Quaternion.AngleAxis(-_avoidanceRayAngleDegrees, Vector3.up) * forward;
            UnityVector3 right = Quaternion.AngleAxis(_avoidanceRayAngleDegrees, Vector3.up) * forward;

            ObstacleRaySample frontSample = CastObstacleRay(origin, forward, "front", 0f);
            ObstacleRaySample leftSample = CastObstacleRay(origin, left, "left", -_avoidanceRayAngleDegrees);
            ObstacleRaySample rightSample = CastObstacleRay(origin, right, "right", _avoidanceRayAngleDegrees);
            _lastObstacleRayDebugStates = new[]
            {
                BuildObstacleRayDebugState(frontSample),
                BuildObstacleRayDebugState(leftSample),
                BuildObstacleRayDebugState(rightSample)
            };
            return new ObstacleFrame(frontSample, leftSample, rightSample);
        }

        private static ObstacleRayDebugState BuildObstacleRayDebugState(ObstacleRaySample sample)
        {
            return new ObstacleRayDebugState(
                sample.Label,
                sample.Origin,
                sample.RayDirection,
                sample.HitDistance,
                sample.HasHit,
                sample.HitPoint);
        }

        private ObstacleRaySample CastObstacleRay(UnityVector3 origin, UnityVector3 direction, string label, float angleDeg)
        {
            UnityVector3 normalizedDirection = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
            if (Physics.Raycast(origin, direction, out RaycastHit hit, _avoidanceDetectionDistance, _avoidanceLayerMask, QueryTriggerInteraction.Ignore))
            {
                return new ObstacleRaySample(
                    label,
                    angleDeg,
                    1f - Mathf.Clamp01(hit.distance / _avoidanceDetectionDistance),
                    Mathf.Clamp01(hit.distance / _avoidanceDetectionDistance),
                    hit.distance,
                    origin,
                    normalizedDirection,
                    true,
                    hit.point,
                    SafeDirection(hit.normal, -direction));
            }

            return new ObstacleRaySample(
                label,
                angleDeg,
                0f,
                1f,
                _avoidanceDetectionDistance,
                origin,
                normalizedDirection,
                false,
                origin + normalizedDirection * _avoidanceDetectionDistance,
                Vector3.zero);
        }

        private BypassSide ChooseBypassSide(ObstacleFrame obstacle, UnityVector3 correctedHeading)
        {
            if (TryBuildAvoidanceDirection(obstacle, out UnityVector3 avoidanceDirection))
            {
                float avoidanceAngleDeg = ComputeSignedAngleDegrees(correctedHeading, avoidanceDirection);
                if (Mathf.Abs(avoidanceAngleDeg) > 5f)
                {
                    return avoidanceAngleDeg < 0f ? BypassSide.Left : BypassSide.Right;
                }
            }

            if (Mathf.Abs(obstacle.LeftOccupancy - obstacle.RightOccupancy) < 0.05f)
            {
                return _bypassSide;
            }

            return obstacle.LeftOccupancy <= obstacle.RightOccupancy ? BypassSide.Left : BypassSide.Right;
        }

        private bool TryBuildAvoidanceDirection(ObstacleFrame obstacle, out UnityVector3 avoidanceDirection)
        {
            UnityVector3 repulsion =
                obstacle.Front.SampleNormalWeighted +
                obstacle.Left.SampleNormalWeighted +
                obstacle.Right.SampleNormalWeighted;

            repulsion = Flatten(repulsion);
            if (repulsion.sqrMagnitude <= 0.0001f)
            {
                avoidanceDirection = Vector3.zero;
                return false;
            }

            avoidanceDirection = repulsion.normalized;
            return true;
        }

        private void LogAvoidanceDecision(
            string eventName,
            ObstacleFrame obstacle,
            UnityVector3 correctedHeading,
            BypassSide bypassSide,
            float linearCommand,
            float angularCommand)
        {
            if (SuppressVerboseAndroidDiagnostics)
            {
                return;
            }

            bool relevantObstacle =
                obstacle.Front.HasHit ||
                obstacle.Left.HasHit ||
                obstacle.Right.HasHit;
            if (!relevantObstacle)
            {
                return;
            }

            if (Time.time - _lastAvoidanceDiagnosticLogTime < AvoidanceDiagnosticIntervalSeconds)
            {
                return;
            }

            _lastAvoidanceDiagnosticLogTime = Time.time;
            string strongestSide = GetStrongestObstacleSide(obstacle);
            string avoidanceSummary = TryBuildAvoidanceDirection(obstacle, out UnityVector3 avoidanceDirection)
                ? $"avoidDir={FormatVector(avoidanceDirection)} angleErr={ComputeSignedAngleDegrees(correctedHeading, avoidanceDirection):F1}deg"
                : "avoidDir=<none>";

            Debug.Log(
                $"{LogPrefix} Avoidance | event={eventName} strongest={strongestSide} bypassSide={bypassSide} " +
                $"front(ray={FormatVector(obstacle.Front.RayDirection)} normal={FormatVector(obstacle.Front.HitNormal)} occ={obstacle.Front.Occupancy:F2}) " +
                $"left(ray={FormatVector(obstacle.Left.RayDirection)} normal={FormatVector(obstacle.Left.HitNormal)} occ={obstacle.Left.Occupancy:F2}) " +
                $"right(ray={FormatVector(obstacle.Right.RayDirection)} normal={FormatVector(obstacle.Right.HitNormal)} occ={obstacle.Right.Occupancy:F2}) " +
                $"{avoidanceSummary} vCmd={linearCommand:F2} wCmd={angularCommand:F2}");
        }

        private void LogLateralAvoidanceBias(
            string dominantSide,
            BypassSide escapeSide,
            float dominantOccupancy,
            float oppositeOccupancy,
            DriveCommand baseCommand,
            float linearCommand,
            float angularCommand)
        {
            if (SuppressVerboseAndroidDiagnostics ||
                Time.time - _lastLateralBiasLogTime < AvoidanceDiagnosticIntervalSeconds)
            {
                return;
            }

            _lastLateralBiasLogTime = Time.time;
            string details =
                $"dominant={dominantSide} turn={FormatSide(escapeSide)} dominantOcc={dominantOccupancy:F2} oppositeOcc={oppositeOccupancy:F2} " +
                $"vBefore={baseCommand.Linear:F2} vAfter={linearCommand:F2} wBefore={baseCommand.Angular:F2} wAfter={angularCommand:F2} " +
                $"observedV={_observedLinearSpeed:F3} observedW={_observedAngularSpeed:F3}";
            Debug.Log($"{LogPrefix} Avoidance | lateral_bias | {details}");
            TiagoExperimentTelemetry.RecordEvent("avoidance_lateral_bias", details);
        }

        private void LogThreeRayPerceptionSample(ObstacleFrame obstacle, string intendedTurnSide, float angularCommand)
        {
            if (SuppressVerboseAndroidDiagnostics ||
                Time.time - _lastThreeRayPerceptionLogTime < ThreeRayPerceptionLogCooldownSeconds)
            {
                return;
            }

            _lastThreeRayPerceptionLogTime = Time.time;
            string dominantSide = GetStrongestObstacleSide(obstacle);
            string details =
                $"mode=three_ray_front_left_right dominant={dominantSide} intendedTurn={intendedTurnSide} resultingW={angularCommand:F2} " +
                FormatRayForLog("Front", obstacle.Front) + " " +
                FormatRayForLog("Left", obstacle.Left) + " " +
                FormatRayForLog("Right", obstacle.Right);
            Debug.Log($"{LogPrefix} Perception | three_ray_sample | {details}");
            TiagoExperimentTelemetry.RecordEvent("perception_three_ray_sample", details);
        }

        private static string FormatRayForLog(string name, ObstacleRaySample sample)
        {
            return $"{name}(angle={sample.AngleDeg:F1} hit={sample.HasHit} dist={sample.HitDistance:F2} occ={sample.Occupancy:F2} normal={FormatVector(sample.HitNormal)} dir={FormatVector(sample.RayDirection)})";
        }

        private void EvaluateAvoidanceTurnDirection(ObstacleFrame obstacle, float angularCommand)
        {
            string strongestSide = GetStrongestObstacleSide(obstacle);
            bool turnsIntoObstacle =
                (strongestSide == "left" && IsAngularCommandForTurnSide(angularCommand, BypassSide.Left)) ||
                (strongestSide == "right" && IsAngularCommandForTurnSide(angularCommand, BypassSide.Right));

            UpdateSince(ref _avoidanceTurnsIntoObstacleSince, turnsIntoObstacle);
            if (IsStableSince(_avoidanceTurnsIntoObstacleSince, AvoidanceTurnIntoObstacleSeconds))
            {
                LogAnomaly(
                    "obstacle_avoidance_turns_into_obstacle",
                    $"strongest={strongestSide} wCmd={angularCommand:F2} front={obstacle.Front.Occupancy:F2} left={obstacle.Left.Occupancy:F2} right={obstacle.Right.Occupancy:F2} " +
                    $"leftNormal={FormatVector(obstacle.Left.HitNormal)} rightNormal={FormatVector(obstacle.Right.HitNormal)}");
            }
        }

        private static bool TryGetDominantLateralObstacle(
            ObstacleFrame obstacle,
            out string dominantSide,
            out BypassSide escapeSide,
            out float dominantOccupancy,
            out float oppositeOccupancy)
        {
            bool rightDominant = obstacle.RightOccupancy >= LateralAvoidanceOccupancyThreshold &&
                obstacle.RightOccupancy >= obstacle.LeftOccupancy + LateralAvoidanceDominanceMargin;
            bool leftDominant = obstacle.LeftOccupancy >= LateralAvoidanceOccupancyThreshold &&
                obstacle.LeftOccupancy >= obstacle.RightOccupancy + LateralAvoidanceDominanceMargin;

            if (rightDominant)
            {
                dominantSide = "Right";
                escapeSide = BypassSide.Left;
                dominantOccupancy = obstacle.RightOccupancy;
                oppositeOccupancy = obstacle.LeftOccupancy;
                return true;
            }

            if (leftDominant)
            {
                dominantSide = "Left";
                escapeSide = BypassSide.Right;
                dominantOccupancy = obstacle.LeftOccupancy;
                oppositeOccupancy = obstacle.RightOccupancy;
                return true;
            }

            dominantSide = "None";
            escapeSide = BypassSide.Left;
            dominantOccupancy = 0f;
            oppositeOccupancy = 0f;
            return false;
        }

        private static BypassSide OppositeSide(BypassSide side)
        {
            return side == BypassSide.Left ? BypassSide.Right : BypassSide.Left;
        }

        private static float TurnSideToAngularCommand(BypassSide side, float magnitude)
        {
            // Positive differential angular command produces a left turn on the TIAGo bridge;
            // Unity yaw for that turn is expected to decrease, so yaw delta is negative.
            return (side == BypassSide.Left ? 1f : -1f) * Mathf.Abs(magnitude);
        }

        private static bool IsAngularCommandForTurnSide(float angularCommand, BypassSide side)
        {
            return side == BypassSide.Left ? angularCommand > 0.05f : angularCommand < -0.05f;
        }

        private static float ExpectedYawDeltaSignForTurnSide(BypassSide side)
        {
            return side == BypassSide.Left ? -1f : 1f;
        }

        private static string InterpretTurnFromYawDelta(float yawDeltaDeg)
        {
            if (Mathf.Abs(yawDeltaDeg) < 0.5f)
            {
                return "None";
            }

            return yawDeltaDeg < 0f ? "Left" : "Right";
        }

        private static string FormatSide(BypassSide side)
        {
            return side == BypassSide.Left ? "Left" : "Right";
        }

        private static string FormatRotateDirection(int directionSign)
        {
            return directionSign >= 0 ? "Left" : "Right";
        }

        private string GetLocomotionModeTelemetryLabel()
        {
            if (_mode != LocomotionMode.StallRecovery)
            {
                return _mode.ToString();
            }

            StallRecoveryPhase phase = GetStallRecoveryPhase(Time.time - _stallRecoveryStartedAt);
            return phase == StallRecoveryPhase.None ? "StallRecovery" : $"StallRecovery:{phase}";
        }

        private static string GetStrongestObstacleSide(ObstacleFrame obstacle)
        {
            if (obstacle.LeftOccupancy >= obstacle.RightOccupancy && obstacle.LeftOccupancy >= obstacle.FrontOccupancy && obstacle.LeftOccupancy > 0f)
            {
                return "left";
            }

            if (obstacle.RightOccupancy >= obstacle.LeftOccupancy && obstacle.RightOccupancy >= obstacle.FrontOccupancy && obstacle.RightOccupancy > 0f)
            {
                return "right";
            }

            return obstacle.FrontOccupancy > 0f ? "front" : "none";
        }

        private void PublishExperimentTelemetry(
            UnityVector3 desiredTarget,
            float remainingDistance,
            DriveCommand command,
            TiagoDifferentialDriveBridge.DriveDiagnostics drive,
            string locomotionMode,
            bool activeCorner,
            float obstacleFront,
            bool commandApplied)
        {
            TiagoExperimentTelemetry.PublishCommand(
                "Autonomous",
                commandApplied,
                desiredTarget,
                true,
                remainingDistance,
                command.Linear,
                command.Angular,
                drive,
                locomotionMode,
                activeCorner,
                obstacleFront,
                _lastActivePathSource.ToString(),
                _lastActiveLookaheadPoint,
                _lastActiveProjectedPoint,
                _lastActiveSegmentIndex,
                command.AngleErrorDeg,
                _activeDriveProfile,
                _activeAutonomousPolicy,
                _lastCommandLookaheadDistance,
                _lastDistanceToNextCorner,
                _lastNextCornerAngleDeg,
                _lastSpeedReductionReason);
        }

        private void ChangeMode(LocomotionMode nextMode, string reason)
        {
            ChangeMode(nextMode, reason, null, null, null);
        }

        private void ChangeMode(LocomotionMode nextMode, string reason, PathFrame? frame, ObstacleFrame? obstacle, float? angleErrorDeg)
        {
            if (nextMode == LocomotionMode.ObstacleBypass || nextMode == LocomotionMode.CornerTurn)
            {
                LogAnomaly(
                    "legacy_mode_blocked",
                    $"requested={nextMode} reason={reason}");
                nextMode = LocomotionMode.TrackPath;
                reason = $"legacy_mode_blocked original={reason}";
            }

            if (_mode == nextMode)
            {
                return;
            }

            LocomotionMode previousMode = _mode;
            float timeInPreviousMode = Time.time - _modeEnteredAt;
            LogTransition(previousMode, nextMode, reason, timeInPreviousMode, frame, obstacle, angleErrorDeg);
            UpdateStallRecoveryRunMetrics(previousMode, nextMode, timeInPreviousMode);
            _mode = nextMode;
            _modeEnteredAt = Time.time;
            _bestRemainingDistanceInMode = float.PositiveInfinity;
            _noProgressSince = float.PositiveInfinity;
            _stallTurnDemandSince = float.PositiveInfinity;
            _lastStallCandidateLogged = false;

            if (nextMode == LocomotionMode.StartupAlignment)
            {
                _headingAlignmentExitStableSince = float.PositiveInfinity;
                TiagoExperimentTelemetry.RecordEvent(
                    "startup_alignment_enter",
                    $"reason={reason} angleErr={(angleErrorDeg.HasValue ? angleErrorDeg.Value.ToString("F1") : "NaN")}deg enter={HeadingAlignEnterAngleDeg:F1}deg exit={HeadingAlignExitAngleDeg:F1}deg hold={HeadingAlignmentHoldSeconds:F2}s");
                LogP40NavTraceActivePathOwnershipSnapshot("startup_alignment_enter", "startup_alignment");
            }

            if (timeInPreviousMode >= 0f && previousMode == LocomotionMode.StartupAlignment && nextMode == LocomotionMode.TrackPath)
            {
                TiagoExperimentTelemetry.RecordEvent(
                    "startup_alignment_exit",
                    $"reason={reason} duration={timeInPreviousMode:F2}s angleErr={(angleErrorDeg.HasValue ? angleErrorDeg.Value.ToString("F1") : "NaN")}deg");
            }
        }

        private void UpdateStallRecoveryRunMetrics(LocomotionMode previousMode, LocomotionMode nextMode, float previousDuration)
        {
            if (nextMode == LocomotionMode.StallRecovery)
            {
                _stallRecoveryEntriesThisRun++;
                TiagoExperimentTelemetry.RecordEvent(
                    "stall_recovery_metrics",
                    $"event=entered count={_stallRecoveryEntriesThisRun} totalSeconds={_stallRecoveryTotalSeconds:F2}");
                return;
            }

            if (previousMode == LocomotionMode.StallRecovery)
            {
                _stallRecoveryTotalSeconds += Mathf.Max(0f, previousDuration);
                TiagoExperimentTelemetry.RecordEvent(
                    "stall_recovery_metrics",
                    $"event=exited count={_stallRecoveryEntriesThisRun} totalSeconds={_stallRecoveryTotalSeconds:F2} lastDuration={previousDuration:F2}");
            }
        }

        private void LogTransition(
            LocomotionMode previousMode,
            LocomotionMode nextMode,
            string reason,
            float previousDuration,
            PathFrame? frame,
            ObstacleFrame? obstacle,
            float? angleErrorDeg)
        {
            string context = string.Empty;

            if (frame.HasValue)
            {
                PathFrame value = frame.Value;
                context += $" corner={value.HasCorner} activeCorner={value.UsesActiveCorner} cornerPoint={FormatVector(value.CornerPoint)} cornerDist={value.DistanceToCorner:F2}m";
            }

            if (obstacle.HasValue)
            {
                ObstacleFrame value = obstacle.Value;
                context += $" obstacleFront={value.FrontOccupancy:F2} obstacleLeft={value.LeftOccupancy:F2} obstacleRight={value.RightOccupancy:F2}";
            }

            if (angleErrorDeg.HasValue)
            {
                context += $" angleErr={angleErrorDeg.Value:F1}deg";
            }

            Debug.Log($"{LogPrefix} Transition | {previousMode} -> {nextMode} | previousDuration={previousDuration:F2}s reason={reason}{context}");
            TiagoExperimentTelemetry.RecordEvent(
                "locomotion_mode_changed",
                $"previous={previousMode} current={nextMode} previousDuration={previousDuration:F2}s reason={reason}{context}");
        }

        private static void LogCornerEvent(string eventName, string details)
        {
            Debug.Log($"{LogPrefix} Corner | {eventName} | {details}");
            TiagoExperimentTelemetry.RecordEvent($"corner_{eventName}", details);
        }

        private void LogCornerEventThrottled(string eventName, string details)
        {
            if (Time.time - _lastCornerFollowThroughLogTime < AnomalyLogCooldownSeconds)
            {
                return;
            }

            _lastCornerFollowThroughLogTime = Time.time;
            LogCornerEvent(eventName, details);
        }

        private void LogActuationDiagnostics(
            UnityVector3 currentPosition,
            UnityVector3 correctedHeading,
            PathFrame frame,
            ObstacleFrame obstacle,
            DriveCommand command)
        {
            float now = Time.time;

            if (_mode != LocomotionMode.CornerTurn || !_hasActiveCorner)
            {
                return;
            }

            CornerResolution resolution = EvaluateCornerResolution(currentPosition, correctedHeading, obstacle);
            bool followThrough = resolution.IsAligned && !resolution.IsChanneled;
            bool noProgressStable = IsStableSince(_cornerFollowThroughNoProgressSince, CornerFollowThroughNoProgressSeconds);

            if (!followThrough && !noProgressStable)
            {
                return;
            }

            if (now - _lastActuationDiagnosticLogTime < ActuationDiagnosticIntervalSeconds && !noProgressStable)
            {
                return;
            }

            _lastActuationDiagnosticLogTime = now;
            TiagoDifferentialDriveBridge.DriveDiagnostics drive = _driveBridge.LastDiagnostics;
            float observedLinear = _observedLinearSpeed;
            float observedAngular = _observedAngularSpeed;

            if (!SuppressVerboseAndroidDiagnostics)
            {
                LogCornerEvent(
                    "actuation",
                    $"follow_through={followThrough} noProgress={StableDuration(_cornerFollowThroughNoProgressSince):F2}s progress={resolution.OutgoingProgress:F2}m front={obstacle.FrontOccupancy:F2} vCmd={command.Linear:F2} wCmd={command.Angular:F2} left='{drive.LeftName}' right='{drive.RightName}' jointTypes=({drive.LeftJointType},{drive.RightJointType}) forceLimits=({drive.LeftForceLimit:F0},{drive.RightForceLimit:F0}) wheelLTarget={drive.LeftTargetDegPerSec:F1}deg/s wheelRTarget={drive.RightTargetDegPerSec:F1}deg/s wheelLDrive={drive.LeftDriveTargetDegPerSec:F1}deg/s wheelRDrive={drive.RightDriveTargetDegPerSec:F1}deg/s wheelLJointRaw={drive.LeftJointVelocityDegPerSec:F1}deg/s wheelRJointRaw={drive.RightJointVelocityDegPerSec:F1}deg/s wheelLJointLogical={drive.LeftLogicalJointVelocityDegPerSec:F1}deg/s wheelRJointLogical={drive.RightLogicalJointVelocityDegPerSec:F1}deg/s followRatio=({drive.LeftAbsFollowRatio:F2},{drive.RightAbsFollowRatio:F2}) signMatch=({drive.LeftSignedDirectionMatchesTarget},{drive.RightSignedDirectionMatchesTarget}) wheelLUnsigned={drive.LeftUnsignedRadPerSec:F2}rad/s wheelRUnsigned={drive.RightUnsignedRadPerSec:F2}rad/s theoryV={drive.TheoreticalLinear:F2} theoryW={drive.TheoreticalAngular:F2} observedV={observedLinear:F3} observedW={observedAngular:F3} clamped=False signs=({drive.LeftSign},{drive.RightSign})");
            }

            UpdateDriveHealthDiagnostics(drive);

            if (noProgressStable && Mathf.Abs(drive.TheoreticalLinear) > 0.05f && Mathf.Abs(observedLinear) < 0.01f)
            {
                LogAnomaly(
                    "actuation_command_without_motion",
                    $"theoryV={drive.TheoreticalLinear:F2} observedV={observedLinear:F3} vCmd={command.Linear:F2} wheelLDrive={drive.LeftDriveTargetDegPerSec:F1}deg/s wheelRDrive={drive.RightDriveTargetDegPerSec:F1}deg/s wheelLJoint={drive.LeftJointVelocityDegPerSec:F1}deg/s wheelRJoint={drive.RightJointVelocityDegPerSec:F1}deg/s progress={resolution.OutgoingProgress:F2}m front={obstacle.FrontOccupancy:F2}");
            }
        }

        private void UpdateDriveHealthDiagnostics(TiagoDifferentialDriveBridge.DriveDiagnostics drive)
        {
            bool leftGood = drive.LeftAbsFollowRatio >= DriveFollowGoodRatio;
            bool rightGood = drive.RightAbsFollowRatio >= DriveFollowGoodRatio;
            bool leftPoor = drive.LeftAbsFollowRatio <= DriveFollowPoorRatio;
            bool rightPoor = drive.RightAbsFollowRatio <= DriveFollowPoorRatio;
            bool asymmetric = (leftGood && rightPoor) || (rightGood && leftPoor);

            UpdateSince(ref _driveAsymmetrySince, asymmetric);
            if (IsStableSince(_driveAsymmetrySince, DriveAsymmetryDetectSeconds))
            {
                string weakSide = leftPoor ? "left" : "right";
                LogAnomaly(
                    "drive_asymmetry_detected",
                    $"weak={weakSide} left='{drive.LeftName}' right='{drive.RightName}' followRatio=({drive.LeftAbsFollowRatio:F2},{drive.RightAbsFollowRatio:F2}) targets=({drive.LeftDriveTargetDegPerSec:F1},{drive.RightDriveTargetDegPerSec:F1}) jointsRaw=({drive.LeftJointVelocityDegPerSec:F1},{drive.RightJointVelocityDegPerSec:F1}) jointsLogical=({drive.LeftLogicalJointVelocityDegPerSec:F1},{drive.RightLogicalJointVelocityDegPerSec:F1}) signMatch=({drive.LeftSignedDirectionMatchesTarget},{drive.RightSignedDirectionMatchesTarget}) jointTypes=({drive.LeftJointType},{drive.RightJointType}) forceLimits=({drive.LeftForceLimit:F0},{drive.RightForceLimit:F0}) signs=({drive.LeftSign},{drive.RightSign})");
            }

            bool signMismatch = (leftGood && !drive.LeftSignedDirectionMatchesTarget) ||
                (rightGood && !drive.RightSignedDirectionMatchesTarget);
            UpdateSince(ref _driveSignMismatchSince, signMismatch);
            if (IsStableSince(_driveSignMismatchSince, DriveAsymmetryDetectSeconds))
            {
                LogAnomaly(
                    "drive_sign_mismatch_detected",
                    $"left='{drive.LeftName}' right='{drive.RightName}' targets=({drive.LeftDriveTargetDegPerSec:F1},{drive.RightDriveTargetDegPerSec:F1}) jointsRaw=({drive.LeftJointVelocityDegPerSec:F1},{drive.RightJointVelocityDegPerSec:F1}) followRatio=({drive.LeftAbsFollowRatio:F2},{drive.RightAbsFollowRatio:F2}) signMatch=({drive.LeftSignedDirectionMatchesTarget},{drive.RightSignedDirectionMatchesTarget}) signs=({drive.LeftSign},{drive.RightSign})");
            }
        }

        private void LogAnomalies(
            UnityVector3 currentPosition,
            UnityVector3 desiredTarget,
            PathFrame frame,
            ObstacleFrame obstacle,
            DriveCommand command,
            float remainingDistance)
        {
            float angleErrorDeg = Mathf.Abs(command.AngleErrorDeg);
            float timeInMode = Time.time - _modeEnteredAt;
            if (timeInMode >= ModeOverstaySeconds && IsStableSince(_noProgressSince, ModeOverstaySeconds))
            {
                LogAnomaly(
                    "mode_overstay_no_progress",
                    $"mode={_mode} timeInMode={timeInMode:F2}s noProgress={StableDuration(_noProgressSince):F2}s remaining={remainingDistance:F2}m best={_bestRemainingDistanceInMode:F2}m angleErr={angleErrorDeg:F1}deg activeCorner={frame.UsesActiveCorner} front={obstacle.FrontOccupancy:F2} current={FormatVector(currentPosition)} target={FormatVector(desiredTarget)}");
                LogP40NavTraceActivePathOwnershipSnapshot("anomaly_mode_overstay_no_progress", "no_progress");
                LogP40NavTraceObstacleRegistrySnapshot("anomaly_mode_overstay_no_progress", "no_progress");
                if (_mode == LocomotionMode.StartupAlignment)
                {
                    LogP40NavTraceStartupAlignmentBlockDiagnostic(
                        currentPosition,
                        desiredTarget,
                        frame,
                        obstacle,
                        angleErrorDeg,
                        remainingDistance);
                }
            }
        }

        private void UpdateProgressTracking(float remainingDistance)
        {
            if (remainingDistance < _bestRemainingDistanceInMode - ProgressEpsilonMeters)
            {
                _bestRemainingDistanceInMode = remainingDistance;
                _noProgressSince = float.PositiveInfinity;
                return;
            }

            if (float.IsPositiveInfinity(_bestRemainingDistanceInMode))
            {
                _bestRemainingDistanceInMode = remainingDistance;
                _noProgressSince = float.PositiveInfinity;
                return;
            }

            if (float.IsPositiveInfinity(_noProgressSince))
            {
                _noProgressSince = Time.time;
            }
        }

        private void LogAnomaly(string anomaly, string details)
        {
            if (Time.time - _lastAnomalyLogTime < AnomalyLogCooldownSeconds)
            {
                return;
            }

            _lastAnomalyLogTime = Time.time;
            Debug.LogWarning($"{LogPrefix} Anomaly | {anomaly} | {details}");
            TiagoExperimentTelemetry.RecordEvent($"anomaly_{anomaly}", details);
        }

        private UnityVector3 GetActiveHeading(PathFrame frame)
        {
            return _mode == LocomotionMode.CornerTurn ? frame.CornerHeading : frame.FollowHeading;
        }

        private static void LogSuccess(UnityVector3 currentPosition, UnityVector3 desiredTarget)
        {
            Debug.Log($"{LogPrefix} Success | current={FormatVector(currentPosition)} target={FormatVector(desiredTarget)}");
            TiagoExperimentTelemetry.RecordEvent("navigation_success", $"current={FormatVector(currentPosition)} target={FormatVector(desiredTarget)}");
        }

        private static void LogFailure(string reason)
        {
            Debug.LogWarning($"{LogPrefix} Failure | reason={reason}");
            TiagoExperimentTelemetry.RecordEvent("navigation_failure", $"reason={reason}");
        }

        private static float ComputeSignedAngleDegrees(UnityVector3 forward, UnityVector3 targetHeading)
        {
            UnityVector3 flatForward = Flatten(forward);
            UnityVector3 flatTarget = Flatten(targetHeading);

            if (flatForward.sqrMagnitude <= 0.0001f || flatTarget.sqrMagnitude <= 0.0001f)
            {
                return 0f;
            }

            return Vector3.SignedAngle(flatForward.normalized, flatTarget.normalized, Vector3.up);
        }

        private static UnityVector3 SafeDirection(UnityVector3 value, UnityVector3 fallback)
        {
            value = Flatten(value);
            if (value.sqrMagnitude <= 0.0001f)
            {
                return Flatten(fallback);
            }

            return value.normalized;
        }

        private static UnityVector3 Flatten(UnityVector3 value)
        {
            value.y = 0f;
            return value;
        }

        private static UnityVector3 ApplyHeadingOffset(UnityVector3 baseHeading, float headingOffsetDegrees)
        {
            if (baseHeading.sqrMagnitude <= 0.0001f)
            {
                return baseHeading;
            }

            return Quaternion.AngleAxis(headingOffsetDegrees, Vector3.up) * baseHeading.normalized;
        }

        private static string FormatVector(UnityVector3 value)
        {
            return $"({value.x:F2}, {value.y:F2}, {value.z:F2})";
        }

        private static string FormatFloat(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? "n/a"
                : value.ToString("F3");
        }

        private readonly struct PathTrackingFrame
        {
            public static readonly PathTrackingFrame Invalid = new(
                Vector3.zero,
                Vector3.forward,
                0f,
                0f,
                0,
                Vector3.zero,
                0f,
                ActivePathSource.Original,
                0f,
                float.PositiveInfinity,
                0f,
                false);

            public UnityVector3 LookaheadPoint { get; }
            public UnityVector3 LookaheadHeading { get; }
            public float AlongPathDistance { get; }
            public float RemainingPathDistance { get; }
            public int SegmentIndex { get; }
            public UnityVector3 ProjectedPoint { get; }
            public float DistanceToPath { get; }
            public ActivePathSource Source { get; }
            public float ActiveLookaheadDistance { get; }
            public float DistanceToNextCorner { get; }
            public float NextCornerAngleDeg { get; }
            public bool IsValid { get; }

            public PathTrackingFrame(
                UnityVector3 lookaheadPoint,
                UnityVector3 lookaheadHeading,
                float alongPathDistance,
                float remainingPathDistance,
                int segmentIndex,
                UnityVector3 projectedPoint,
                float distanceToPath,
                ActivePathSource source,
                float activeLookaheadDistance,
                float distanceToNextCorner,
                float nextCornerAngleDeg,
                bool isValid)
            {
                LookaheadPoint = lookaheadPoint;
                LookaheadHeading = lookaheadHeading;
                AlongPathDistance = alongPathDistance;
                RemainingPathDistance = remainingPathDistance;
                SegmentIndex = segmentIndex;
                ProjectedPoint = projectedPoint;
                DistanceToPath = distanceToPath;
                Source = source;
                ActiveLookaheadDistance = activeLookaheadDistance;
                DistanceToNextCorner = distanceToNextCorner;
                NextCornerAngleDeg = nextCornerAngleDeg;
                IsValid = isValid;
            }
        }

        private readonly struct CornerInfo
        {
            public static readonly CornerInfo None = new(false, Vector3.zero, float.PositiveInfinity, 0f);

            public CornerInfo(bool isValid, UnityVector3 point, float distanceFromProjection, float angleDeg)
            {
                IsValid = isValid;
                Point = point;
                DistanceFromProjection = distanceFromProjection;
                AngleDeg = angleDeg;
            }

            public bool IsValid { get; }
            public UnityVector3 Point { get; }
            public float DistanceFromProjection { get; }
            public float AngleDeg { get; }
        }

        private readonly struct FootprintClearanceSnapshot
        {
            public static readonly FootprintClearanceSnapshot Empty = new(false, false, float.NaN, -1, Vector3.zero, "None");

            public FootprintClearanceSnapshot(
                bool pass,
                bool hasSample,
                float minClearance,
                int sampleIndex,
                UnityVector3 samplePoint,
                string colliderPath)
            {
                Pass = pass;
                HasSample = hasSample;
                MinClearance = minClearance;
                SampleIndex = sampleIndex;
                SamplePoint = samplePoint;
                ColliderPath = colliderPath;
            }

            public bool Pass { get; }
            public bool HasSample { get; }
            public float MinClearance { get; }
            public int SampleIndex { get; }
            public UnityVector3 SamplePoint { get; }
            public string ColliderPath { get; }
        }

        private readonly struct PathCenteringResult
        {
            public PathCenteringResult(
                UnityVector3[] points,
                bool isValid,
                int adjusted,
                int kept,
                float averageShift,
                float maxShift,
                string failureReason)
            {
                Points = points ?? new UnityVector3[0];
                IsValid = isValid;
                Adjusted = adjusted;
                Kept = kept;
                AverageShift = averageShift;
                MaxShift = maxShift;
                FailureReason = failureReason;
            }

            public UnityVector3[] Points { get; }
            public bool IsValid { get; }
            public int Adjusted { get; }
            public int Kept { get; }
            public float AverageShift { get; }
            public float MaxShift { get; }
            public string FailureReason { get; }

            public static PathCenteringResult Failed(string reason, int centeredPointCount, int kept)
            {
                return new PathCenteringResult(new UnityVector3[centeredPointCount], false, 0, kept, 0f, 0f, reason);
            }
        }

        private readonly struct PathCornerSmoothingResult
        {
            public PathCornerSmoothingResult(
                UnityVector3[] points,
                bool isValid,
                int smoothedVertices,
                int cornersConsidered,
                int keptByAngle,
                int keptByShortSegment,
                int rejectedByNavMesh,
                int rejectedBySegmentRaycast,
                int originalPointCount,
                int centeredPointCount,
                string failureReason)
            {
                Points = points ?? new UnityVector3[0];
                IsValid = isValid;
                SmoothedVertices = smoothedVertices;
                CornersConsidered = cornersConsidered;
                KeptByAngle = keptByAngle;
                KeptByShortSegment = keptByShortSegment;
                RejectedByNavMesh = rejectedByNavMesh;
                RejectedBySegmentRaycast = rejectedBySegmentRaycast;
                OriginalPointCount = originalPointCount;
                CenteredPointCount = centeredPointCount;
                FailureReason = failureReason;
            }

            public UnityVector3[] Points { get; }
            public bool IsValid { get; }
            public int SmoothedVertices { get; }
            public int CornersConsidered { get; }
            public int KeptByAngle { get; }
            public int KeptByShortSegment { get; }
            public int RejectedByNavMesh { get; }
            public int RejectedBySegmentRaycast { get; }
            public int OriginalPointCount { get; }
            public int CenteredPointCount { get; }
            public string FailureReason { get; }

            public static PathCornerSmoothingResult Failed(
                string reason,
                int smoothedPointCount,
                int rejectedByNavMesh,
                int originalPointCount,
                int centeredPointCount)
            {
                return new PathCornerSmoothingResult(
                    new UnityVector3[smoothedPointCount],
                    false,
                    0,
                    rejectedByNavMesh,
                    0,
                    0,
                    0,
                    0,
                    originalPointCount,
                    centeredPointCount,
                    reason);
            }
        }

        private readonly struct PathClearanceOffsetResult
        {
            public PathClearanceOffsetResult(
                UnityVector3[] points,
                bool isValid,
                int adjustedPoints,
                int keptPoints,
                int eligiblePoints,
                int skippedBySufficientClearance,
                int rejectedBySample,
                int rejectedByRaycast,
                int revertedByMaxDeviation,
                int revertedByArtificialCurvature,
                float originalMinEdgeDistance,
                float finalMinEdgeDistance,
                float originalMeanEdgeDistance,
                float finalMeanEdgeDistance,
                float maxDeviationApplied,
                string failureReason)
            {
                Points = points ?? new UnityVector3[0];
                IsValid = isValid;
                AdjustedPoints = adjustedPoints;
                KeptPoints = keptPoints;
                EligiblePoints = eligiblePoints;
                SkippedBySufficientClearance = skippedBySufficientClearance;
                RejectedBySample = rejectedBySample;
                RejectedByRaycast = rejectedByRaycast;
                RevertedByMaxDeviation = revertedByMaxDeviation;
                RevertedByArtificialCurvature = revertedByArtificialCurvature;
                OriginalMinEdgeDistance = originalMinEdgeDistance;
                FinalMinEdgeDistance = finalMinEdgeDistance;
                OriginalMeanEdgeDistance = originalMeanEdgeDistance;
                FinalMeanEdgeDistance = finalMeanEdgeDistance;
                MaxDeviationApplied = maxDeviationApplied;
                FailureReason = failureReason;
            }

            public UnityVector3[] Points { get; }
            public bool IsValid { get; }
            public int AdjustedPoints { get; }
            public int KeptPoints { get; }
            public int EligiblePoints { get; }
            public int SkippedBySufficientClearance { get; }
            public int RejectedBySample { get; }
            public int RejectedByRaycast { get; }
            public int RevertedByMaxDeviation { get; }
            public int RevertedByArtificialCurvature { get; }
            public float OriginalMinEdgeDistance { get; }
            public float FinalMinEdgeDistance { get; }
            public float OriginalMeanEdgeDistance { get; }
            public float FinalMeanEdgeDistance { get; }
            public float MaxDeviationApplied { get; }
            public string FailureReason { get; }

            public static PathClearanceOffsetResult Failed(string reason, int outputPointCount, int keptPoints)
            {
                return new PathClearanceOffsetResult(new UnityVector3[outputPointCount], false, 0, keptPoints, 0, 0, 0, 0, 0, 0, float.NaN, float.NaN, float.NaN, float.NaN, 0f, reason);
            }
        }

        private readonly struct ClearanceOffsetCandidate
        {
            public static readonly ClearanceOffsetCandidate RejectedSample = new(Vector3.zero, 0f, 0f, 0f, float.NegativeInfinity, false, false);

            public ClearanceOffsetCandidate(
                UnityVector3 point,
                float signedOffset,
                float offsetDistance,
                float clearance,
                float score,
                bool sampleValid,
                bool segmentValid)
            {
                Point = point;
                SignedOffset = signedOffset;
                OffsetDistance = offsetDistance;
                Clearance = clearance;
                Score = score;
                SampleValid = sampleValid;
                SegmentValid = segmentValid;
            }

            public UnityVector3 Point { get; }
            public float SignedOffset { get; }
            public float OffsetDistance { get; }
            public float Clearance { get; }
            public float Score { get; }
            public bool SampleValid { get; }
            public bool SegmentValid { get; }
        }

        private readonly struct CornerSmoothingCandidate
        {
            private CornerSmoothingCandidate(
                List<UnityVector3> points,
                bool isValid,
                string rejectReason,
                float selectedOffset,
                float minClearance,
                float meanClearance,
                int clearanceSamples)
            {
                Points = points;
                IsValid = isValid;
                SelectedOffset = selectedOffset;
                MinClearance = minClearance;
                MeanClearance = meanClearance;
                ClearanceSamples = clearanceSamples;
                Report = isValid
                    ? CornerSmoothingVertexReport.CreateSmoothed(0f, 0f, 0f, selectedOffset, minClearance, meanClearance, clearanceSamples)
                    : CornerSmoothingVertexReport.Kept(rejectReason, 0f, 0f, 0f, selectedOffset, minClearance, clearanceSamples, meanClearance);
            }

            public static readonly CornerSmoothingCandidate Invalid = Rejected("no_valid_curve");

            public List<UnityVector3> Points { get; }
            public bool IsValid { get; }
            public float SelectedOffset { get; }
            public float MinClearance { get; }
            public float MeanClearance { get; }
            public int ClearanceSamples { get; }
            public float RadiusUsed => MinClearance;
            public CornerSmoothingVertexReport Report { get; }

            public static CornerSmoothingCandidate Valid(List<UnityVector3> points, float selectedOffset, float minClearance, float meanClearance, int clearanceSamples)
            {
                return new CornerSmoothingCandidate(points, true, "none", selectedOffset, minClearance, meanClearance, clearanceSamples);
            }

            public static CornerSmoothingCandidate Rejected(string reason, float minClearance = float.NaN, float meanClearance = float.NaN, int clearanceSamples = 0)
            {
                return new CornerSmoothingCandidate(null, false, reason, 0f, minClearance, meanClearance, clearanceSamples);
            }
        }

        private readonly struct CornerSmoothingVertexReport
        {
            private CornerSmoothingVertexReport(
                bool smoothed,
                string rejectReason,
                float angleDeg,
                float previousLength,
                float nextLength,
                float selectedOffset,
                float minClearance,
                float meanClearance,
                int clearanceSamples)
            {
                Smoothed = smoothed;
                RejectReason = rejectReason;
                AngleDeg = angleDeg;
                PreviousLength = previousLength;
                NextLength = nextLength;
                SelectedOffset = selectedOffset;
                MinClearance = minClearance;
                MeanClearance = meanClearance;
                ClearanceSamples = clearanceSamples;
            }

            public bool Smoothed { get; }
            public string RejectReason { get; }
            public float AngleDeg { get; }
            public float PreviousLength { get; }
            public float NextLength { get; }
            public float SelectedOffset { get; }
            public float MinClearance { get; }
            public float MeanClearance { get; }
            public int ClearanceSamples { get; }
            public float RadiusUsed => MinClearance;

            public static CornerSmoothingVertexReport CreateSmoothed(
                float angleDeg,
                float previousLength,
                float nextLength,
                float selectedOffset,
                float minClearance,
                float meanClearance,
                int clearanceSamples)
            {
                return new CornerSmoothingVertexReport(true, "none", angleDeg, previousLength, nextLength, selectedOffset, minClearance, meanClearance, clearanceSamples);
            }

            public static CornerSmoothingVertexReport Kept(
                string rejectReason,
                float angleDeg,
                float previousLength,
                float nextLength,
                float selectedOffset,
                float minClearance,
                int clearanceSamples,
                float meanClearance = float.NaN)
            {
                return new CornerSmoothingVertexReport(false, rejectReason, angleDeg, previousLength, nextLength, selectedOffset, minClearance, meanClearance, clearanceSamples);
            }

            public CornerSmoothingVertexReport WithGeometry(float angleDeg, float previousLength, float nextLength)
            {
                return new CornerSmoothingVertexReport(Smoothed, RejectReason, angleDeg, previousLength, nextLength, SelectedOffset, MinClearance, MeanClearance, ClearanceSamples);
            }
        }

        private readonly struct PathFrame
        {
            public UnityVector3 GuidePoint { get; }
            public UnityVector3 FollowHeading { get; }
            public UnityVector3 CornerHeading { get; }
            public UnityVector3 CornerPoint { get; }
            public bool HasCorner { get; }
            public float CornerAngleDeg { get; }
            public float DistanceToCorner { get; }
            public bool UsesActiveCorner { get; }

            public PathFrame(
                UnityVector3 guidePoint,
                UnityVector3 followHeading,
                UnityVector3 cornerHeading,
                UnityVector3 cornerPoint,
                bool hasCorner,
                float cornerAngleDeg,
                float distanceToCorner,
                bool usesActiveCorner)
            {
                GuidePoint = guidePoint;
                FollowHeading = followHeading;
                CornerHeading = cornerHeading;
                CornerPoint = cornerPoint;
                HasCorner = hasCorner;
                CornerAngleDeg = cornerAngleDeg;
                DistanceToCorner = distanceToCorner;
                UsesActiveCorner = usesActiveCorner;
            }

            public static PathFrame FromLookahead(PathTrackingFrame trackingFrame)
            {
                return new PathFrame(
                    trackingFrame.LookaheadPoint,
                    trackingFrame.LookaheadHeading,
                    trackingFrame.LookaheadHeading,
                    trackingFrame.LookaheadPoint,
                    false,
                    0f,
                    trackingFrame.RemainingPathDistance,
                    false);
            }
        }

        private readonly struct ObstacleFrame
        {
            public static readonly ObstacleFrame None = new(ObstacleRaySample.None("front"), ObstacleRaySample.None("left"), ObstacleRaySample.None("right"));

            public ObstacleRaySample Front { get; }
            public ObstacleRaySample Left { get; }
            public ObstacleRaySample Right { get; }
            public float FrontOccupancy { get; }
            public float LeftOccupancy { get; }
            public float RightOccupancy { get; }
            public bool HasAnyObstacle => FrontOccupancy > 0f || LeftOccupancy > 0f || RightOccupancy > 0f;
            public float MaxOccupancy => Mathf.Max(FrontOccupancy, LeftOccupancy, RightOccupancy);

            public ObstacleFrame(ObstacleRaySample front, ObstacleRaySample left, ObstacleRaySample right)
            {
                Front = front;
                Left = left;
                Right = right;
                FrontOccupancy = front.Occupancy;
                LeftOccupancy = left.Occupancy;
                RightOccupancy = right.Occupancy;
            }
        }

        public readonly struct ObstacleRayDebugState
        {
            public string Label { get; }
            public UnityVector3 Origin { get; }
            public UnityVector3 Direction { get; }
            public float Length { get; }
            public bool Hit { get; }
            public UnityVector3 HitPoint { get; }

            public ObstacleRayDebugState(
                string label,
                UnityVector3 origin,
                UnityVector3 direction,
                float length,
                bool hit,
                UnityVector3 hitPoint)
            {
                Label = label;
                Origin = origin;
                Direction = direction;
                Length = length;
                Hit = hit;
                HitPoint = hitPoint;
            }
        }

        private readonly struct ObstacleRaySample
        {
            public string Label { get; }
            public float AngleDeg { get; }
            public float Occupancy { get; }
            public float FreeSpaceScore { get; }
            public float HitDistance { get; }
            public UnityVector3 Origin { get; }
            public UnityVector3 RayDirection { get; }
            public bool HasHit { get; }
            public UnityVector3 HitPoint { get; }
            public UnityVector3 HitNormal { get; }
            public UnityVector3 SampleNormalWeighted => HasHit ? HitNormal * Occupancy : Vector3.zero;

            public ObstacleRaySample(
                string label,
                float angleDeg,
                float occupancy,
                float freeSpaceScore,
                float hitDistance,
                UnityVector3 origin,
                UnityVector3 rayDirection,
                bool hasHit,
                UnityVector3 hitPoint,
                UnityVector3 hitNormal)
            {
                Label = label;
                AngleDeg = angleDeg;
                Occupancy = occupancy;
                FreeSpaceScore = freeSpaceScore;
                HitDistance = hitDistance;
                Origin = origin;
                RayDirection = rayDirection;
                HasHit = hasHit;
                HitPoint = hitPoint;
                HitNormal = hitNormal;
            }

            public static ObstacleRaySample None(string label)
            {
                return new ObstacleRaySample(label, 0f, 0f, 1f, 0f, Vector3.zero, Vector3.zero, false, Vector3.zero, Vector3.zero);
            }
        }

        private readonly struct CornerResolution
        {
            public static readonly CornerResolution None = new(false, false, false, 0f, 0f, 0f, "none");

            public bool IsAligned { get; }
            public bool HasProgressed { get; }
            public bool IsChanneled { get; }
            public float ErrorDeg { get; }
            public float OutgoingProgress { get; }
            public float DistanceToCorner { get; }
            public string ChannelReason { get; }

            public CornerResolution(
                bool isAligned,
                bool hasProgressed,
                bool isChanneled,
                float errorDeg,
                float outgoingProgress,
                float distanceToCorner,
                string channelReason)
            {
                IsAligned = isAligned;
                HasProgressed = hasProgressed;
                IsChanneled = isChanneled;
                ErrorDeg = errorDeg;
                OutgoingProgress = outgoingProgress;
                DistanceToCorner = distanceToCorner;
                ChannelReason = channelReason;
            }
        }

        private readonly struct DriveCommand
        {
            public float Linear { get; }
            public float Angular { get; }
            public float AngleErrorDeg { get; }

            public DriveCommand(float linear, float angular, float angleErrorDeg)
            {
                Linear = linear;
                Angular = angular;
                AngleErrorDeg = angleErrorDeg;
            }
        }
    }
}
