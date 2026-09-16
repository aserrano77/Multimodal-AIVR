using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Autonomy.BT.Core;
using Autonomy.Domain;
using Autonomy.Services;
using UnityEngine;
using UnityEngine.AI;

namespace Autonomy.UnityIntegration
{
    public enum HeldObjectAlignmentMode
    {
        PivotToAnchorOffset,
        BoundsCenterToAnchorOffset
    }

    public enum PlaceFailureRecoveryMode
    {
        StrictAbort,
        RelaxedRange,
        SimplifiedPlace
    }

    /// <summary>
    /// Minimal Unity-backed manipulation service for scene boxes.
    /// This is a semantic attach/release MVP, not arm IK or grasp planning.
    /// </summary>
    public sealed class TiagoUnityManipulationService : IManipulationService
    {
        private readonly Transform _robotReference;
        private readonly Transform _carryAnchor;
        private readonly float _pickRange;
        private readonly Vector3 _placeOffset;
        private readonly bool _requirePlaceWithinRange;
        private readonly float _maxExpectedPlaceDistance;
        private readonly float _placeDistanceSafetyMargin;
        private readonly float _maxRelaxedPlaceDistance;
        private readonly PlaceFailureRecoveryMode _placeFailureRecoveryMode;
        private readonly bool _disableGrabWhileHeld;
        private readonly bool _disableHeldObjectCollidersWhileHeld;
        private readonly Vector3 _heldLocalPositionOffset;
        private readonly Vector3 _heldLocalEulerOffset;
        private readonly HeldObjectAlignmentMode _heldObjectAlignmentMode;
        private readonly bool _enforceHeldPoseWhileHolding;
        private readonly float _heldPoseDriftWarningThreshold;
        private readonly float _heldPoseDriftCorrectionThreshold;
        private readonly bool _enableDepositedBoxNavMeshObstacle;
        private readonly Vector3 _depositedBoxObstacleSizePadding;
        private readonly bool _depositedBoxObstacleCarve;
        private readonly float _depositedBoxObstacleCarveMoveThreshold;

        private HeldObjectState _heldObject;
        private HeldObjectPoseLock _poseLock;
        private string _manipulationState = "Idle";
        private bool _serviceConfigLogged;
        private Dictionary<string, object> _lastPlaceDebugContext = new();

        public GameObject HeldObject => _heldObject.GameObject;
        public string HeldObjectId => _heldObject.IsValid ? _heldObject.ObjectId : string.Empty;
        public string ManipulationState => _manipulationState;

        public TiagoUnityManipulationService(
            Transform robotReference,
            Transform carryAnchor,
            float pickRange,
            Vector3 placeOffset,
            bool requirePlaceWithinRange = true,
            float maxExpectedPlaceDistance = 1.25f,
            float placeDistanceSafetyMargin = 0.05f,
            float maxRelaxedPlaceDistance = 1.45f,
            PlaceFailureRecoveryMode placeFailureRecoveryMode = PlaceFailureRecoveryMode.RelaxedRange,
            bool disableGrabWhileHeld = true,
            bool disableHeldObjectCollidersWhileHeld = true,
            Vector3 heldLocalPositionOffset = default,
            Vector3 heldLocalEulerOffset = default,
            HeldObjectAlignmentMode heldObjectAlignmentMode = HeldObjectAlignmentMode.BoundsCenterToAnchorOffset,
            bool enforceHeldPoseWhileHolding = true,
            float heldPoseDriftWarningThreshold = 0.03f,
            float heldPoseDriftCorrectionThreshold = 0.01f,
            bool enableDepositedBoxNavMeshObstacle = true,
            Vector3 depositedBoxObstacleSizePadding = default,
            bool depositedBoxObstacleCarve = true,
            float depositedBoxObstacleCarveMoveThreshold = 0.05f)
        {
            _robotReference = robotReference;
            _carryAnchor = carryAnchor != null ? carryAnchor : robotReference;
            _pickRange = Mathf.Max(0.05f, pickRange);
            _placeOffset = placeOffset;
            _requirePlaceWithinRange = requirePlaceWithinRange;
            _maxExpectedPlaceDistance = Mathf.Max(0.05f, maxExpectedPlaceDistance);
            _placeDistanceSafetyMargin = Mathf.Max(0f, placeDistanceSafetyMargin);
            _maxRelaxedPlaceDistance = Mathf.Max(_maxExpectedPlaceDistance, maxRelaxedPlaceDistance);
            _placeFailureRecoveryMode = placeFailureRecoveryMode;
            _disableGrabWhileHeld = disableGrabWhileHeld;
            _disableHeldObjectCollidersWhileHeld = disableHeldObjectCollidersWhileHeld;
            _heldLocalPositionOffset = heldLocalPositionOffset;
            _heldLocalEulerOffset = heldLocalEulerOffset;
            _heldObjectAlignmentMode = heldObjectAlignmentMode;
            _enforceHeldPoseWhileHolding = enforceHeldPoseWhileHolding;
            _heldPoseDriftWarningThreshold = Mathf.Max(0f, heldPoseDriftWarningThreshold);
            _heldPoseDriftCorrectionThreshold = Mathf.Max(0f, heldPoseDriftCorrectionThreshold);
            _enableDepositedBoxNavMeshObstacle = enableDepositedBoxNavMeshObstacle;
            _depositedBoxObstacleSizePadding = depositedBoxObstacleSizePadding == default
                ? new Vector3(0.08f, 0.02f, 0.08f)
                : depositedBoxObstacleSizePadding;
            _depositedBoxObstacleCarve = depositedBoxObstacleCarve;
            _depositedBoxObstacleCarveMoveThreshold = Mathf.Max(0f, depositedBoxObstacleCarveMoveThreshold);
        }

        public NodeStatus Pick(TargetDescriptor target)
        {
            if (target == null)
            {
                return Pick(string.Empty);
            }

            var targetPosition = new Vector3(target.Position.X, target.Position.Y, target.Position.Z);
            return Pick(target.Id, targetPosition);
        }

        public NodeStatus Pick(string objectId)
        {
            return Pick(objectId, null);
        }

        private NodeStatus Pick(string objectId, Vector3? targetPosition)
        {
            if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
            {
                return NodeStatus.Running;
            }

            LogServiceConfigIfNeeded();

            if (_heldObject.IsValid)
            {
                if (string.Equals(_heldObject.ObjectId, objectId, StringComparison.OrdinalIgnoreCase))
                {
                    LogEvent(
                        "manipulation_pick_skipped_already_holding_requested_object",
                        BuildPayload(objectId, _heldObject.GameObject, 0f, false, null, null));
                    _manipulationState = "Holding";
                    return NodeStatus.Success;
                }

                LogPickFailed(objectId, "already_holding_object", null, float.NaN, false);
                _manipulationState = "PickFailed:AlreadyHoldingObject";
                return NodeStatus.Failure;
            }

            _manipulationState = "Picking";
            LogPickRequested(objectId);

            if (!TryResolveBox(objectId, targetPosition, out Component metadata, out string resolveReason))
            {
                LogPickFailed(objectId, resolveReason, null, float.NaN, false);
                _manipulationState = $"PickFailed:{resolveReason}";
                return NodeStatus.Failure;
            }

            GameObject box = metadata.gameObject;
            Rigidbody rigidbody = box.GetComponent<Rigidbody>();
            Component grabInteractable = FindComponentByTypeName(box, "XRGrabInteractable");
            float distance = GetDistanceToAnchor(box.transform.position);
            bool isSelectedByUser = IsSelected(grabInteractable);

            if (GetBool(metadata, "isDeposited"))
            {
                LogPickFailed(objectId, "object_already_deposited", box, distance, isSelectedByUser);
                _manipulationState = "PickFailed:ObjectAlreadyDeposited";
                return NodeStatus.Failure;
            }

            if (GetBool(metadata, "isGrabbed") || isSelectedByUser)
            {
                LogPickFailed(objectId, isSelectedByUser ? "object_selected_by_xr" : "object_grabbed_by_user", box, distance, true);
                _manipulationState = isSelectedByUser ? "PickFailed:ObjectSelectedByXr" : "PickFailed:ObjectGrabbedByUser";
                return NodeStatus.Failure;
            }

            if (distance > _pickRange)
            {
                LogPickFailed(objectId, "object_out_of_range", box, distance, false);
                _manipulationState = "PickFailed:ObjectOutOfRange";
                return NodeStatus.Failure;
            }

            Vector3 objectPositionBeforeAttach = box.transform.position;
            _heldObject = HeldObjectState.Capture(box, metadata, rigidbody, grabInteractable);
            LogComponentAudit(objectId);

            if (_disableHeldObjectCollidersWhileHeld && _heldObject.Colliders.Length == 0)
            {
                LogPickFailed(objectId, "no_functional_colliders_found", box, distance, false, _heldObject.ColliderScan);
                _heldObject = default;
                _manipulationState = "PickFailed:NoFunctionalCollidersFound";
                return NodeStatus.Failure;
            }

            AttachTelemetry attachTelemetry = AttachHeldObject(objectPositionBeforeAttach);

            if (_disableHeldObjectCollidersWhileHeld && attachTelemetry.FunctionalCollidersRemainingEnabledCount > 0)
            {
                LogEvent("manipulation_held_object_collider_suppression_failed", BuildPayload(objectId, box, distance, false, null, "held_object_colliders_not_suppressed", attachTelemetry));
                RestoreHeldObject(_heldObject.OriginalPosition);
                _heldObject = default;
                _manipulationState = "PickFailed:HeldObjectCollidersNotSuppressed";
                LogPickFailed(objectId, "held_object_colliders_not_suppressed", box, distance, false);
                return NodeStatus.Failure;
            }

            _manipulationState = "Holding";
            AttachPoseLock();

            LogEvent("manipulation_object_attached", BuildPayload(objectId, box, distance, false, null, null, attachTelemetry));
            LogEvent("manipulation_pick_succeeded", BuildPayload(objectId, box, distance, false, null, null, attachTelemetry));
            StartSettleDiagnostics();
            return NodeStatus.Success;
        }

        public NodeStatus Place(string destinationId)
        {
            if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
            {
                return NodeStatus.Running;
            }

            _manipulationState = "Placing";
            LogPlaceRequested(destinationId);

            if (!_heldObject.IsValid)
            {
                LogPlaceFailed(destinationId, "no_held_object", null);
                _manipulationState = "PlaceFailed:NoHeldObject";
                return NodeStatus.Failure;
            }

            GameObject box = _heldObject.GameObject;
            bool depositedBefore = GetBool(_heldObject.Metadata, "isDeposited");
            if (!TryResolvePlacePose(destinationId, box, out PlacePose placePose, out string resolveReason))
            {
                LogPlaceFailed(destinationId, resolveReason, box);
                _manipulationState = $"PlaceFailed:{resolveReason}";
                return NodeStatus.Failure;
            }

            PlaceRangeCheck rangeCheck = CheckPlaceRange(placePose);
            LogPlaceRangeChecked(destinationId, box, placePose, rangeCheck);
            if (_requirePlaceWithinRange && !rangeCheck.WithinRange)
            {
                if (!TryApplyPlaceRecovery(destinationId, box, placePose, rangeCheck))
                {
                    LogPlaceFailed(destinationId, "place_out_of_range", box, placePose, rangeCheck);
                    _manipulationState = "PlaceFailed:PlaceOutOfRange";
                    return NodeStatus.Failure;
                }
            }

            DestroyPoseLock();
            LogPlaceDebug("robotic_place_debug_before_registration", destinationId, box, placePose, null, "before_restore_held_object", depositedBefore, GetBool(_heldObject.Metadata, "isDeposited"));
            RestoreHeldObject(placePose.Position, placePose.Rotation);
            LogPlaceDebug("robotic_place_debug_before_registration", destinationId, box, placePose, null, "after_restore_held_object", depositedBefore, GetBool(_heldObject.Metadata, "isDeposited"));
            LogPlaceDebug("robotic_place_debug_registration_attempt", destinationId, box, placePose, null, "before_round_manager_registration", depositedBefore, GetBool(_heldObject.Metadata, "isDeposited"));
            RoboticDepositRegistrationResult depositRegistration = TryRegisterRoboticRoundDeposit(
                _heldObject.Metadata,
                destinationId,
                placePose);
            LogPlaceDebug("robotic_place_debug_registration_result", destinationId, box, placePose, depositRegistration, "after_round_manager_registration", depositedBefore, GetBool(_heldObject.Metadata, "isDeposited"));
            LogPlaceDebug("robotic_place_debug_before_registration", destinationId, box, placePose, depositRegistration, "before_lock_after_deposit", depositedBefore, GetBool(_heldObject.Metadata, "isDeposited"));
            bool shouldLockAfterDeposit = depositRegistration.Registered || !depositRegistration.RoundManagerFound;
            bool lockedAfterDeposit = shouldLockAfterDeposit && InvokeLockAfterDeposit(_heldObject.Metadata);
            bool obstacleRegistered = RegisterDepositedBoxObstacle(box, placePose, lockedAfterDeposit);
            _manipulationState = "Released";

            LogEvent(
                "manipulation_place_physics_policy",
                BuildPayload(destinationId, box, 0f, false, placePose.Position, null, null, null, placePose, lockedAfterDeposit));
            LogEvent("manipulation_object_released", BuildPayload(destinationId, box, 0f, false, placePose.Position, null, null, null, placePose, lockedAfterDeposit));
            Dictionary<string, object> successPayload = BuildPayload(destinationId, box, 0f, false, placePose.Position, null, null, null, placePose, lockedAfterDeposit);
            successPayload["deposited_box_obstacle_registered"] = obstacleRegistered;
            LogEvent("manipulation_place_succeeded", successPayload);
            LogPlaceDebug("robotic_place_debug_registration_result", destinationId, box, placePose, depositRegistration, "after_manipulation_place_succeeded", depositedBefore, GetBool(_heldObject.Metadata, "isDeposited"));
            LogRoboticDepositRegistration(destinationId, box, depositRegistration);
            _heldObject = default;
            _manipulationState = "Idle";
            return NodeStatus.Success;
        }

        public void Stop()
        {
            if (!_heldObject.IsValid)
            {
                _manipulationState = "Idle";
                return;
            }

            if (_heldObject.Rigidbody != null)
            {
                _heldObject.Rigidbody.linearVelocity = Vector3.zero;
                _heldObject.Rigidbody.angularVelocity = Vector3.zero;
            }
        }

        public bool AbortHeldObjectForExperimentBoundary(string reason)
        {
            if (!_heldObject.IsValid)
            {
                _manipulationState = "Idle";
                return false;
            }

            string objectId = HeldObjectId;
            GameObject box = _heldObject.GameObject;
            Vector3 heldPosition = box != null ? box.transform.position : Vector3.zero;
            try
            {
                RestoreHeldObject(_heldObject.OriginalPosition, _heldObject.OriginalRotation);
                LogEvent(
                    "manipulation_held_object_aborted_for_experiment_boundary",
                    BuildPayload(objectId, box, 0f, false, heldPosition, reason ?? string.Empty));
            }
            catch (Exception ex)
            {
                LogEvent(
                    "manipulation_held_object_abort_failed_for_experiment_boundary",
                    BuildPayload(objectId, box, 0f, false, heldPosition, ex.GetType().Name + ":" + ex.Message));
            }
            finally
            {
                _heldObject = default;
                _manipulationState = "Idle";
                DestroyPoseLock();
            }

            return true;
        }

        private AttachTelemetry AttachHeldObject(Vector3 objectPositionBeforeAttach)
        {
            GameObject box = _heldObject.GameObject;
            Transform boxTransform = box.transform;
            BoundsSnapshot boundsBefore = _heldObject.BoundsBeforeAttach;
            Vector3 desiredBoundsCenterWorld = _carryAnchor != null
                ? _carryAnchor.TransformPoint(_heldLocalPositionOffset)
                : boxTransform.position + _heldLocalPositionOffset;
            Vector3 pivotToBoundsCenterBefore = boundsBefore.IsValid
                ? boundsBefore.Center - boxTransform.position
                : Vector3.zero;
            Vector3 objectWorldPositionAfterAlignment = boxTransform.position;

            if (_heldObject.GrabInteractable != null && _disableGrabWhileHeld)
            {
                SetBehaviourEnabled(_heldObject.GrabInteractable, false);
            }

            SetHeldCollidersEnabled(false);
            Physics.SyncTransforms();

            if (_heldObject.Rigidbody != null)
            {
                _heldObject.Rigidbody.linearVelocity = Vector3.zero;
                _heldObject.Rigidbody.angularVelocity = Vector3.zero;
                _heldObject.Rigidbody.detectCollisions = false;
                _heldObject.Rigidbody.isKinematic = true;
                _heldObject.Rigidbody.useGravity = false;
                _heldObject.Rigidbody.constraints = RigidbodyConstraints.FreezeAll;
            }

            Quaternion desiredWorldRotation = _carryAnchor != null
                ? _carryAnchor.rotation * Quaternion.Euler(_heldLocalEulerOffset)
                : Quaternion.Euler(_heldLocalEulerOffset);

            if (_heldObjectAlignmentMode == HeldObjectAlignmentMode.BoundsCenterToAnchorOffset && boundsBefore.IsValid)
            {
                Vector3 localPivotToBoundsCenter = Quaternion.Inverse(boxTransform.rotation) * pivotToBoundsCenterBefore;
                objectWorldPositionAfterAlignment = desiredBoundsCenterWorld - desiredWorldRotation * localPivotToBoundsCenter;
                boxTransform.SetPositionAndRotation(objectWorldPositionAfterAlignment, desiredWorldRotation);
                boxTransform.SetParent(_carryAnchor, true);
                _heldObject.DesiredLocalPosition = boxTransform.localPosition;
                _heldObject.DesiredLocalRotation = boxTransform.localRotation;
            }
            else
            {
                boxTransform.SetParent(_carryAnchor, true);
                boxTransform.localPosition = _heldLocalPositionOffset;
                boxTransform.localRotation = Quaternion.Euler(_heldLocalEulerOffset);
                objectWorldPositionAfterAlignment = boxTransform.position;
                _heldObject.DesiredLocalPosition = boxTransform.localPosition;
                _heldObject.DesiredLocalRotation = boxTransform.localRotation;
            }

            Physics.SyncTransforms();
            _heldObject.Rigidbody?.Sleep();

            if (_heldObject.Metadata != null)
            {
                InvokeSetGrabbedState(_heldObject.Metadata, true);
            }

            int disabledCount = CountFunctionalCollidersMatchingEnabled(false);
            int remainingEnabledCount = CountFunctionalCollidersMatchingEnabled(true);
            BoundsSnapshot boundsAfter = CalculateCurrentHeldBounds();
            return new AttachTelemetry
            {
                ObjectPositionBeforeAttach = objectPositionBeforeAttach,
                ObjectPositionAfterAttach = boxTransform.position,
                ObjectLocalPositionAfterAttach = boxTransform.localPosition,
                ObjectLocalRotationAfterAttach = boxTransform.localRotation.eulerAngles,
                AlignmentMode = _heldObjectAlignmentMode.ToString(),
                BoundsSource = boundsBefore.Source,
                BoundsCenterBeforeAttach = boundsBefore.Center,
                BoundsExtentsBeforeAttach = boundsBefore.Extents,
                BoundsMinYBeforeAttach = boundsBefore.MinY,
                BoundsCenterAfterAttach = boundsAfter.Center,
                BoundsMinYAfterAttach = boundsAfter.MinY,
                PivotToBoundsCenterBeforeAttach = pivotToBoundsCenterBefore,
                DesiredBoundsCenterWorld = desiredBoundsCenterWorld,
                ObjectWorldPositionAfterAlignment = objectWorldPositionAfterAlignment,
                AnchorPosition = _carryAnchor != null ? _carryAnchor.position : Vector3.zero,
                AnchorName = _carryAnchor != null ? _carryAnchor.name : string.Empty,
                RigidbodyIsKinematicAfterAttach = _heldObject.Rigidbody != null && _heldObject.Rigidbody.isKinematic,
                UseGravityAfterAttach = _heldObject.Rigidbody != null && _heldObject.Rigidbody.useGravity,
                RigidbodyDetectCollisionsBeforeAttach = _heldObject.DetectedCollisions,
                RigidbodyDetectCollisionsAfterAttach = _heldObject.Rigidbody != null && _heldObject.Rigidbody.detectCollisions,
                DisableHeldObjectCollidersRequested = _disableHeldObjectCollidersWhileHeld,
                CollidersDisabledWhileHeld = _disableHeldObjectCollidersWhileHeld && _heldObject.Colliders.Length > 0 && remainingEnabledCount == 0,
                FunctionalCollidersFoundCount = _heldObject.Colliders.Length,
                FunctionalCollidersDisabledCount = disabledCount,
                FunctionalCollidersRemainingEnabledCount = remainingEnabledCount,
                AllCollidersFoundCount = _heldObject.ColliderScan.AllCollidersFoundCount,
                SkippedCollidersCount = _heldObject.ColliderScan.SkippedCollidersCount,
                SkippedCollidersNames = _heldObject.ColliderScan.SkippedCollidersNames,
                SkippedCollidersReasons = _heldObject.ColliderScan.SkippedCollidersReasons,
                ObjectRootName = box.name,
                RigidbodyOwnerName = _heldObject.Rigidbody != null ? _heldObject.Rigidbody.gameObject.name : string.Empty,
                BoxMetadataOwnerName = _heldObject.Metadata != null ? _heldObject.Metadata.gameObject.name : string.Empty,
                AuxiliaryCollidersSkippedCount = _heldObject.AuxiliaryCollidersSkippedCount,
                AuxiliaryCollidersSkippedReason = _heldObject.AuxiliaryCollidersSkippedReason,
                XRGrabDisabledWhileHeld = _disableGrabWhileHeld && _heldObject.GrabInteractable != null,
                HeldLocalPositionOffsetApplied = _heldLocalPositionOffset,
                HeldLocalEulerOffsetApplied = _heldLocalEulerOffset
            };
        }

        private void RestoreHeldObject(Vector3 worldPosition)
        {
            RestoreHeldObject(worldPosition, _heldObject.OriginalRotation);
        }

        private void RestoreHeldObject(Vector3 worldPosition, Quaternion worldRotation)
        {
            GameObject box = _heldObject.GameObject;
            Transform boxTransform = box.transform;
            boxTransform.position = worldPosition;
            boxTransform.rotation = worldRotation;
            Physics.SyncTransforms();
            boxTransform.SetParent(_heldObject.OriginalParent, true);
            Physics.SyncTransforms();

            RestoreHeldColliders();

            if (_heldObject.Rigidbody != null)
            {
                _heldObject.Rigidbody.linearVelocity = Vector3.zero;
                _heldObject.Rigidbody.angularVelocity = Vector3.zero;
                _heldObject.Rigidbody.collisionDetectionMode = _heldObject.CollisionDetectionMode;
                _heldObject.Rigidbody.interpolation = _heldObject.Interpolation;
                _heldObject.Rigidbody.detectCollisions = _heldObject.DetectedCollisions;
                _heldObject.Rigidbody.isKinematic = _heldObject.WasKinematic;
                _heldObject.Rigidbody.useGravity = _heldObject.UsedGravity;
                _heldObject.Rigidbody.constraints = _heldObject.Constraints;
                _heldObject.Rigidbody.linearVelocity = Vector3.zero;
                _heldObject.Rigidbody.angularVelocity = Vector3.zero;
                _heldObject.Rigidbody.Sleep();
            }

            if (_heldObject.GrabInteractable != null)
            {
                SetBehaviourEnabled(_heldObject.GrabInteractable, _heldObject.GrabInteractableWasEnabled);
            }

            if (_heldObject.Metadata != null)
            {
                InvokeSetGrabbedState(_heldObject.Metadata, false);
            }

            DestroyPoseLock();
        }

        private void AttachPoseLock()
        {
            DestroyPoseLock();
            if (!_enforceHeldPoseWhileHolding || !_heldObject.IsValid || _carryAnchor == null)
            {
                return;
            }

            _poseLock = _heldObject.GameObject.GetComponent<HeldObjectPoseLock>();
            if (_poseLock == null)
            {
                _poseLock = _heldObject.GameObject.AddComponent<HeldObjectPoseLock>();
            }

            _poseLock.Initialize(
                _carryAnchor,
                _heldObject.DesiredLocalPosition,
                _heldObject.DesiredLocalRotation,
                _heldObject.Rigidbody,
                _heldObject.GrabInteractable,
                _heldObject.Colliders,
                _heldPoseDriftWarningThreshold,
                _heldPoseDriftCorrectionThreshold,
                _heldObject.ObjectId,
                LogEvent);
        }

        private void DestroyPoseLock()
        {
            if (_poseLock != null)
            {
                UnityEngine.Object.Destroy(_poseLock);
                _poseLock = null;
            }
        }

        private void SetHeldCollidersEnabled(bool enabled)
        {
            if (!_disableHeldObjectCollidersWhileHeld || !_heldObject.IsValid)
            {
                return;
            }

            for (int i = 0; i < _heldObject.Colliders.Length; i++)
            {
                Collider collider = _heldObject.Colliders[i];
                if (collider != null)
                {
                    collider.enabled = enabled;
                }
            }
        }

        private void RestoreHeldColliders()
        {
            for (int i = 0; i < _heldObject.Colliders.Length; i++)
            {
                Collider collider = _heldObject.Colliders[i];
                if (collider != null && i < _heldObject.ColliderWasEnabled.Length)
                {
                    collider.enabled = _heldObject.ColliderWasEnabled[i];
                }
            }
        }

        private int CountFunctionalCollidersMatchingEnabled(bool enabled)
        {
            int count = 0;
            for (int i = 0; i < _heldObject.Colliders.Length; i++)
            {
                Collider collider = _heldObject.Colliders[i];
                if (collider != null && collider.enabled == enabled)
                {
                    count++;
                }
            }

            return count;
        }

        private BoundsSnapshot CalculateCurrentHeldBounds()
        {
            if (_heldObject.Colliders != null && TryCalculateBoundsFromColliders(_heldObject.Colliders, out Bounds colliderBounds))
            {
                return BoundsSnapshot.FromBounds(colliderBounds, "functional_colliders");
            }

            if (_heldObject.GameObject != null && TryCalculateRendererBounds(_heldObject.GameObject, out Bounds rendererBounds))
            {
                return BoundsSnapshot.FromBounds(rendererBounds, "renderers_fallback");
            }

            return BoundsSnapshot.Invalid;
        }

        private void StartSettleDiagnostics()
        {
            if (!_heldObject.IsValid)
            {
                return;
            }

            GameObject runnerObject = new GameObject("TiagoManipulationSettleDiagnostics");
            UnityEngine.Object.DontDestroyOnLoad(runnerObject);
            runnerObject.hideFlags = HideFlags.HideAndDontSave;
            SettleDiagnosticsRunner runner = runnerObject.AddComponent<SettleDiagnosticsRunner>();
            runner.Begin(this);
        }

        private void LogSettleSample(string phase)
        {
            if (!_heldObject.IsValid)
            {
                return;
            }

            LogEvent("manipulation_held_object_settle_sample", BuildSettlePayload(phase));
        }

        private void LogPickStabilized()
        {
            if (!_heldObject.IsValid)
            {
                return;
            }

            LogEvent("manipulation_pick_stabilized", BuildPickStabilizedPayload());
        }

        private Dictionary<string, object> BuildSettlePayload(string phase)
        {
            GameObject box = _heldObject.GameObject;
            Transform boxTransform = box != null ? box.transform : null;
            BoundsSnapshot bounds = CalculateCurrentHeldBounds();
            Rigidbody rb = _heldObject.Rigidbody;
            Vector3 localPosition = boxTransform != null ? boxTransform.localPosition : Vector3.zero;
            Quaternion localRotation = boxTransform != null ? boxTransform.localRotation : Quaternion.identity;

            return new Dictionary<string, object>
            {
                ["phase"] = phase,
                ["held_object_name"] = box != null ? box.name : string.Empty,
                ["held_object_parent"] = boxTransform != null && boxTransform.parent != null ? boxTransform.parent.name : string.Empty,
                ["held_object_world_position"] = boxTransform != null ? boxTransform.position : Vector3.zero,
                ["held_object_local_position"] = localPosition,
                ["held_object_world_rotation"] = boxTransform != null ? boxTransform.rotation.eulerAngles : Vector3.zero,
                ["expected_local_position"] = _heldObject.DesiredLocalPosition,
                ["expected_local_rotation"] = _heldObject.DesiredLocalRotation.eulerAngles,
                ["local_position_error"] = Vector3.Distance(localPosition, _heldObject.DesiredLocalPosition),
                ["local_rotation_error_deg"] = Quaternion.Angle(localRotation, _heldObject.DesiredLocalRotation),
                ["enforce_held_pose_while_holding"] = _enforceHeldPoseWhileHolding,
                ["pose_lock_component_active"] = _poseLock != null && _poseLock.IsActive,
                ["pose_lock_last_correction_time"] = _poseLock != null ? _poseLock.LastCorrectionTime : float.NaN,
                ["anchor_world_position"] = _carryAnchor != null ? _carryAnchor.position : Vector3.zero,
                ["anchor_world_rotation"] = _carryAnchor != null ? _carryAnchor.rotation.eulerAngles : Vector3.zero,
                ["bounds_center_world"] = bounds.Center,
                ["bounds_min_y"] = bounds.MinY,
                ["bounds_source"] = bounds.Source,
                ["rigidbody_is_kinematic"] = rb != null && rb.isKinematic,
                ["rigidbody_use_gravity"] = rb != null && rb.useGravity,
                ["rigidbody_detect_collisions"] = rb != null && rb.detectCollisions,
                ["rb_position"] = rb != null ? rb.position : Vector3.zero,
                ["rb_rotation"] = rb != null ? rb.rotation.eulerAngles : Vector3.zero,
                ["transform_position"] = boxTransform != null ? boxTransform.position : Vector3.zero,
                ["transform_rotation"] = boxTransform != null ? boxTransform.rotation.eulerAngles : Vector3.zero,
                ["xr_grab_enabled"] = IsBehaviourEnabled(_heldObject.GrabInteractable),
                ["xr_grab_is_selected"] = IsSelected(_heldObject.GrabInteractable),
                ["enabled_functional_colliders_count"] = CountFunctionalCollidersMatchingEnabled(true),
                ["enabled_all_colliders_count"] = CountEnabledColliders(box),
                ["manipulation_state"] = _manipulationState
            };
        }

        private Dictionary<string, object> BuildPickStabilizedPayload()
        {
            GameObject box = _heldObject.GameObject;
            Transform boxTransform = box != null ? box.transform : null;
            Rigidbody rb = _heldObject.Rigidbody;
            Vector3 actualLocalPosition = boxTransform != null ? boxTransform.localPosition : Vector3.zero;
            Quaternion actualLocalRotation = boxTransform != null ? boxTransform.localRotation : Quaternion.identity;
            float localPositionError = Vector3.Distance(actualLocalPosition, _heldObject.DesiredLocalPosition);
            float localRotationErrorDeg = Quaternion.Angle(actualLocalRotation, _heldObject.DesiredLocalRotation);
            int enabledFunctionalColliders = CountFunctionalCollidersMatchingEnabled(true);
            bool xrGrabEnabled = IsBehaviourEnabled(_heldObject.GrabInteractable);
            bool poseLockOk = !_enforceHeldPoseWhileHolding || (_poseLock != null && _poseLock.IsActive);
            bool parentOk = boxTransform != null && boxTransform.parent == _carryAnchor;
            bool poseOk = localPositionError <= _heldPoseDriftWarningThreshold && localRotationErrorDeg <= 0.5f;
            bool rbOk = rb == null || (rb.isKinematic && !rb.useGravity && !rb.detectCollisions);
            bool collidersOk = !_disableHeldObjectCollidersWhileHeld || enabledFunctionalColliders == 0;
            bool xrOk = !_disableGrabWhileHeld || !xrGrabEnabled;
            bool stabilized = _heldObject.IsValid &&
                parentOk &&
                poseLockOk &&
                poseOk &&
                rbOk &&
                collidersOk &&
                xrOk;

            return new Dictionary<string, object>
            {
                ["object_id"] = _heldObject.ObjectId,
                ["held_object"] = box != null ? box.name : string.Empty,
                ["held_object_parent"] = boxTransform != null && boxTransform.parent != null ? boxTransform.parent.name : string.Empty,
                ["manipulation_state"] = _manipulationState,
                ["expected_local_position"] = _heldObject.DesiredLocalPosition,
                ["actual_local_position"] = actualLocalPosition,
                ["local_position_error"] = localPositionError,
                ["expected_local_rotation"] = _heldObject.DesiredLocalRotation.eulerAngles,
                ["actual_local_rotation"] = actualLocalRotation.eulerAngles,
                ["local_rotation_error_deg"] = localRotationErrorDeg,
                ["pose_lock_component_active"] = _poseLock != null && _poseLock.IsActive,
                ["rb_is_kinematic"] = rb != null && rb.isKinematic,
                ["rb_use_gravity"] = rb != null && rb.useGravity,
                ["rb_detect_collisions"] = rb != null && rb.detectCollisions,
                ["enabled_functional_colliders_count"] = enabledFunctionalColliders,
                ["xr_grab_enabled"] = xrGrabEnabled,
                ["stabilized"] = stabilized,
                ["stabilization_reason"] = stabilized
                    ? "stable"
                    : $"parentOk={parentOk} poseLockOk={poseLockOk} poseOk={poseOk} rbOk={rbOk} collidersOk={collidersOk} xrOk={xrOk}"
            };
        }

        private void LogServiceConfigIfNeeded()
        {
            if (_serviceConfigLogged)
            {
                return;
            }

            _serviceConfigLogged = TiagoExperimentTelemetry.LogEvent(
                "manipulation_service_config",
                new Dictionary<string, object>
                {
                    ["manipulation_mode"] = "TiagoUnityScene",
                    ["service_type"] = nameof(TiagoUnityManipulationService),
                    ["manipulation_anchor_name"] = _carryAnchor != null ? _carryAnchor.name : string.Empty,
                    ["manipulation_range"] = _pickRange,
                    ["disable_xr_grab_while_held"] = _disableGrabWhileHeld,
                    ["disable_held_object_colliders_while_held"] = _disableHeldObjectCollidersWhileHeld,
                    ["held_object_alignment_mode"] = _heldObjectAlignmentMode.ToString(),
                    ["held_local_position_offset"] = _heldLocalPositionOffset,
                    ["held_local_euler_offset"] = _heldLocalEulerOffset,
                    ["enforce_held_pose_while_holding"] = _enforceHeldPoseWhileHolding,
                    ["held_pose_drift_warning_threshold"] = _heldPoseDriftWarningThreshold,
                    ["held_pose_drift_correction_threshold"] = _heldPoseDriftCorrectionThreshold
                });
        }

        private void LogComponentAudit(string objectId)
        {
            if (!_heldObject.IsValid)
            {
                return;
            }

            LogEvent(
                "manipulation_held_object_component_audit",
                new Dictionary<string, object>
                {
                    ["object_id"] = objectId ?? string.Empty,
                    ["object_name"] = _heldObject.GameObject.name,
                    ["components"] = BuildComponentAuditText(_heldObject.GameObject)
                });
        }

        private static string BuildComponentAuditText(GameObject root)
        {
            if (root == null)
            {
                return string.Empty;
            }

            List<string> entries = new();
            AppendComponentAudit(entries, root.transform, root.transform);
            for (int i = 0; i < root.transform.childCount; i++)
            {
                AppendComponentAudit(entries, root.transform.GetChild(i), root.transform);
            }

            return string.Join(" || ", entries);
        }

        private static void AppendComponentAudit(List<string> entries, Transform transform, Transform root)
        {
            Component[] components = transform.GetComponents<Component>();
            string relativePath = GetRelativePath(transform, root);
            foreach (Component component in components)
            {
                if (component == null)
                {
                    continue;
                }

                entries.Add($"{relativePath}:{BuildComponentAuditEntry(component)}");
            }
        }

        private static string BuildComponentAuditEntry(Component component)
        {
            string typeName = component.GetType().Name;
            string enabled = component is Behaviour behaviour ? $" enabled={behaviour.enabled}" : string.Empty;
            if (component is Rigidbody rb)
            {
                return $"{typeName} isKinematic={rb.isKinematic} useGravity={rb.useGravity} detectCollisions={rb.detectCollisions} interpolation={rb.interpolation} constraints={rb.constraints}";
            }

            if (string.Equals(typeName, "XRGrabInteractable", StringComparison.Ordinal))
            {
                return $"{typeName}{enabled} isSelected={GetBool(component, "isSelected")} movementType={GetMemberText(component, "movementType")} trackPosition={GetMemberText(component, "trackPosition")} trackRotation={GetMemberText(component, "trackRotation")}";
            }

            if (component is MonoBehaviour)
            {
                return $"{typeName}{enabled}";
            }

            return $"{typeName}{enabled}";
        }

        private static string GetRelativePath(Transform transform, Transform root)
        {
            if (transform == null || root == null || transform == root)
            {
                return ".";
            }

            string path = transform.name;
            Transform current = transform.parent;
            while (current != null && current != root)
            {
                path = $"{current.name}/{path}";
                current = current.parent;
            }

            return path;
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

        private static string FindTransformPathByName(string objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName))
            {
                return string.Empty;
            }

            GameObject found = GameObject.Find(objectName);
            return found != null ? GetTransformPath(found.transform) : string.Empty;
        }

        private bool TryResolveBox(string objectId, Vector3? targetPosition, out Component metadata, out string reason)
        {
            metadata = null;
            reason = string.Empty;

            if (string.IsNullOrWhiteSpace(objectId))
            {
                reason = "missing_object_id";
                return false;
            }

            MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            Component nearestByPosition = null;
            float nearestDistance = float.PositiveInfinity;

            foreach (MonoBehaviour candidate in behaviours)
            {
                if (candidate == null || !string.Equals(candidate.GetType().Name, "BoxMetadata", StringComparison.Ordinal))
                {
                    continue;
                }

                GameObject box = candidate.gameObject;
                string boxType = GetMemberText(candidate, "boxType");
                if (string.Equals(box.name, objectId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(boxType, objectId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals($"Caja{boxType}", objectId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals($"box_{boxType}", objectId, StringComparison.OrdinalIgnoreCase))
                {
                    metadata = candidate;
                    return true;
                }

                if (targetPosition.HasValue)
                {
                    float candidateDistance = Vector3.Distance(candidate.transform.position, targetPosition.Value);
                    if (candidateDistance < nearestDistance)
                    {
                        nearestDistance = candidateDistance;
                        nearestByPosition = candidate;
                    }
                }
            }

            if (nearestByPosition != null && nearestDistance <= _pickRange)
            {
                metadata = nearestByPosition;
                return true;
            }

            reason = "object_not_found";
            return false;
        }

        private bool TryResolvePlacePose(string destinationId, GameObject box, out PlacePose placePose, out string reason)
        {
            placePose = default;
            reason = string.Empty;

            if (!string.IsNullOrWhiteSpace(destinationId))
            {
                MultimodalPlaceTargetRegistry registry = UnityEngine.Object.FindFirstObjectByType<MultimodalPlaceTargetRegistry>();
                if (registry != null &&
                    registry.TryResolveTransforms(destinationId, out string semanticId, out Transform placeTransform, out _, out _))
                {
                    if (TryBuildDynamicPlacePose(destinationId, semanticId, placeTransform, out placePose))
                    {
                        LogPlaceTargetSemanticResolution(destinationId, semanticId, "dynamic_place_pose_override", placeTransform, false);
                        return true;
                    }

                    placePose = new PlacePose(
                        placeTransform.position + _placeOffset,
                        placeTransform.rotation,
                        semanticId,
                        "MultimodalPlaceTargetRegistry",
                        placeTransform.name,
                        GetTransformPath(placeTransform),
                        placeTransform.position);
                    LogPlaceTargetSemanticResolution(destinationId, semanticId, "bridge_or_registry_semantic_id", placeTransform, false);
                    return true;
                }

                GameObject destination = GameObject.Find(destinationId);
                if (destination != null)
                {
                    placePose = new PlacePose(
                        destination.transform.position + _placeOffset,
                        destination.transform.rotation,
                        destination.name,
                        "GameObject",
                        destination.transform.name,
                        GetTransformPath(destination.transform),
                        destination.transform.position);
                    LogPlaceTargetSemanticResolution(destinationId, destinationId, "intent_original", destination.transform, false);
                    return true;
                }

                MonoBehaviour[] zones = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                foreach (MonoBehaviour zone in zones)
                {
                    if (zone != null &&
                        string.Equals(zone.GetType().Name, "DepositZone", StringComparison.Ordinal) &&
                        string.Equals(zone.name, destinationId, StringComparison.OrdinalIgnoreCase))
                    {
                        placePose = new PlacePose(
                            zone.transform.position + _placeOffset,
                            zone.transform.rotation,
                            zone.name,
                            "DepositZone",
                            zone.transform.name,
                            GetTransformPath(zone.transform),
                            zone.transform.position);
                        LogPlaceTargetSemanticResolution(destinationId, zone.name, "deposit_zone_name", zone.transform, !string.Equals(zone.name, destinationId, StringComparison.OrdinalIgnoreCase));
                        return true;
                    }
                }

                reason = "place_target_not_found";
                return false;
            }

            Transform anchor = _carryAnchor != null ? _carryAnchor : _robotReference;
            if (anchor != null)
            {
                placePose = new PlacePose(anchor.position + _placeOffset, anchor.rotation, anchor.name, "CarryAnchorFallback");
                LogPlaceTargetSemanticResolution(destinationId, anchor.name, "carry_anchor_fallback", anchor, true);
                return true;
            }

            placePose = new PlacePose(box.transform.position, box.transform.rotation, box.name, "HeldObjectFallback", string.Empty, GetTransformPath(box.transform), box.transform.position);
            LogPlaceTargetSemanticResolution(destinationId, box.name, "held_object_fallback", box != null ? box.transform : null, true);
            return true;
        }

        private bool TryBuildDynamicPlacePose(
            string requestedId,
            string semanticId,
            Transform placeTransform,
            out PlacePose placePose)
        {
            placePose = default;
            string heldObjectId = !string.IsNullOrWhiteSpace(_heldObject.ObjectId) ? _heldObject.ObjectId : HeldObjectId;
            if (!DynamicPlacePoseOverrideRegistry.TryConsume(semanticId, heldObjectId, out DynamicPlacePoseOverride dynamicPose) &&
                !DynamicPlacePoseOverrideRegistry.TryConsume(requestedId, heldObjectId, out dynamicPose))
            {
                return false;
            }

            if (!dynamicPose.UsedDynamicPlacePose)
            {
                return false;
            }

            placePose = new PlacePose(
                dynamicPose.Position + _placeOffset,
                placeTransform != null ? placeTransform.rotation : Quaternion.identity,
                semanticId,
                "DynamicPlacePose",
                string.IsNullOrWhiteSpace(dynamicPose.CandidateId) ? "DynamicPlacePose" : dynamicPose.CandidateId,
                GetTransformPath(placeTransform),
                dynamicPose.Position,
                dynamicPose.Position,
                dynamicPose.PlacePointFallbackPosition,
                dynamicPose.CandidateId,
                dynamicPose.CandidateKind,
                dynamicPose.AreaSource,
                dynamicPose.PlacePoseInset,
                true,
                false,
                dynamicPose.SlotId,
                dynamicPose.SlotQuadrant,
                dynamicPose.SlotIndex,
                dynamicPose.StackLevel,
                dynamicPose.UsedPlaceSlotAllocator,
                dynamicPose.PostPlaceEgressPoint);

            return true;
        }

        private float GetDistanceToAnchor(Vector3 objectPosition)
        {
            Transform anchor = _carryAnchor != null ? _carryAnchor : _robotReference;
            return anchor != null ? Vector3.Distance(anchor.position, objectPosition) : float.PositiveInfinity;
        }

        private PlaceRangeCheck CheckPlaceRange(PlacePose placePose)
        {
            Transform anchor = _carryAnchor != null ? _carryAnchor : _robotReference;
            Vector3 anchorPosition = anchor != null ? anchor.position : Vector3.zero;
            Vector3 robotPosition = _robotReference != null ? _robotReference.position : anchorPosition;
            float distance = anchor != null
                ? Vector3.Distance(anchorPosition, placePose.PlaceTransformPosition)
                : float.PositiveInfinity;
            float effectiveRange = Mathf.Max(0.05f, _maxExpectedPlaceDistance - _placeDistanceSafetyMargin);
            return new PlaceRangeCheck(anchorPosition, robotPosition, distance, effectiveRange, distance <= effectiveRange);
        }

        private bool TryApplyPlaceRecovery(string destinationId, GameObject box, PlacePose placePose, PlaceRangeCheck rangeCheck)
        {
            bool withinRelaxed = rangeCheck.DistanceToPlaceTransform <= _maxRelaxedPlaceDistance;
            if (_placeFailureRecoveryMode == PlaceFailureRecoveryMode.StrictAbort || !withinRelaxed)
            {
                return false;
            }

            string eventType = _placeFailureRecoveryMode == PlaceFailureRecoveryMode.SimplifiedPlace
                ? "manipulation_place_simplified_fallback_used"
                : "manipulation_place_relaxed_range_used";
            Dictionary<string, object> payload = BuildPayload(destinationId, box, rangeCheck.DistanceToPlaceTransform, false, placePose.Position, null, placePose: placePose);
            AddPlaceRangePayload(payload, placePose, rangeCheck);
            payload["recovery_mode"] = _placeFailureRecoveryMode.ToString();
            payload["actual_anchor_to_place_distance"] = rangeCheck.DistanceToPlaceTransform;
            payload["max_relaxed_place_distance"] = _maxRelaxedPlaceDistance;
            payload["within_strict_place_range"] = rangeCheck.WithinRange;
            payload["within_relaxed_place_range"] = withinRelaxed;
            LogEvent(eventType, payload);
            return true;
        }

        private static bool IsSelected(Component grabInteractable)
        {
            return GetBool(grabInteractable, "isSelected");
        }

        private static Component FindComponentByTypeName(GameObject owner, string typeName)
        {
            if (owner == null)
            {
                return null;
            }

            Component[] components = owner.GetComponents<Component>();
            foreach (Component component in components)
            {
                if (component != null && string.Equals(component.GetType().Name, typeName, StringComparison.Ordinal))
                {
                    return component;
                }
            }

            return null;
        }

        private static bool GetBool(Component component, string memberName)
        {
            if (component == null)
            {
                return false;
            }

            Type type = component.GetType();
            FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(bool))
            {
                return (bool)field.GetValue(component);
            }

            PropertyInfo property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.PropertyType == typeof(bool) && property.GetIndexParameters().Length == 0)
            {
                return (bool)property.GetValue(component);
            }

            return false;
        }

        private static string GetMemberText(Component component, string memberName)
        {
            if (component == null)
            {
                return string.Empty;
            }

            Type type = component.GetType();
            FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                return field.GetValue(component)?.ToString() ?? string.Empty;
            }

            PropertyInfo property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(component)?.ToString() ?? string.Empty;
            }

            return string.Empty;
        }

        private static void InvokeSetGrabbedState(Component metadata, bool grabbed)
        {
            if (metadata == null)
            {
                return;
            }

            MethodInfo method = metadata.GetType().GetMethod("SetGrabbedState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            method?.Invoke(metadata, new object[] { grabbed });
        }

        private static bool InvokeLockAfterDeposit(Component metadata)
        {
            if (metadata == null)
            {
                return false;
            }

            MethodInfo method = metadata.GetType().GetMethod("LockAfterDeposit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                return false;
            }

            method.Invoke(metadata, Array.Empty<object>());
            return true;
        }

        private bool RegisterDepositedBoxObstacle(GameObject box, PlacePose placePose, bool lockedAfterDeposit)
        {
            if (placePose.UsedPlaceSlotAllocator && placePose.PostPlaceEgressPoint != default)
            {
                LogEvent(
                    "deposited_box_obstacle_registration_deferred_until_egress",
                    new Dictionary<string, object>
                    {
                        ["box_id"] = box != null ? box.name : string.Empty,
                        ["slot_id"] = placePose.SlotId,
                        ["slot_position"] = placePose.DynamicPlacePosePosition,
                        ["post_place_egress_point"] = placePose.PostPlaceEgressPoint,
                        ["used_place_slot_allocator"] = placePose.UsedPlaceSlotAllocator
                    });
                return false;
            }

            if (!_enableDepositedBoxNavMeshObstacle || box == null || !lockedAfterDeposit)
            {
                LogEvent(
                    "deposited_box_obstacle_registered",
                    new Dictionary<string, object>
                    {
                        ["box_id"] = box != null ? box.name : string.Empty,
                        ["registered"] = false,
                        ["reason"] = !_enableDepositedBoxNavMeshObstacle ? "disabled" : "not_locked_after_deposit",
                        ["slot_id"] = placePose.SlotId,
                        ["used_place_slot_allocator"] = placePose.UsedPlaceSlotAllocator
                    });
                return false;
            }

            P40NavTraceDiagnostics.LogPostPlaceObstacleRegistrationDiagnostic(
                "before_registration",
                "manipulation_register_deposited_box_obstacle",
                box,
                obstacleRegistered: false,
                pathsInvalidated: false,
                boxId: box != null ? box.name : string.Empty,
                zoneId: placePose.Name,
                egressRequired: placePose.PostPlaceEgressPoint != default);
            P40NavTraceDiagnostics.LogObstacleRegistrySnapshot(
                "before_deposited_box_obstacle_registered",
                "manipulation_register_deposited_box_obstacle",
                lastDepositedBoxId: box != null ? box.name : string.Empty,
                robotPosition: _robotReference != null ? _robotReference.position : Vector3.zero);

            NavMeshObstacle obstacle = box.GetComponent<NavMeshObstacle>();
            if (obstacle == null)
            {
                obstacle = box.AddComponent<NavMeshObstacle>();
            }

            Bounds bounds = CalculateObjectBounds(box);
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.center = box.transform.InverseTransformPoint(bounds.center);
            obstacle.size = new Vector3(
                Mathf.Max(0.05f, bounds.size.x + _depositedBoxObstacleSizePadding.x),
                Mathf.Max(0.05f, bounds.size.y + _depositedBoxObstacleSizePadding.y),
                Mathf.Max(0.05f, bounds.size.z + _depositedBoxObstacleSizePadding.z));
            obstacle.carving = _depositedBoxObstacleCarve;
            obstacle.carveOnlyStationary = true;
            obstacle.carvingMoveThreshold = _depositedBoxObstacleCarveMoveThreshold;
            obstacle.enabled = true;

            LogEvent(
                "deposited_box_obstacle_registered",
                new Dictionary<string, object>
                {
                    ["box_id"] = box.name,
                    ["registered"] = true,
                    ["slot_id"] = placePose.SlotId,
                    ["slot_position"] = placePose.DynamicPlacePosePosition,
                    ["obstacle_size"] = obstacle.size,
                    ["obstacle_center"] = obstacle.center,
                    ["carving"] = obstacle.carving,
                    ["carve_move_threshold"] = obstacle.carvingMoveThreshold,
                    ["used_place_slot_allocator"] = placePose.UsedPlaceSlotAllocator,
                    ["post_place_egress_point"] = placePose.PostPlaceEgressPoint
                });
            P40NavTraceDiagnostics.LogPostPlaceObstacleRegistrationDiagnostic(
                "after_registration",
                "registered",
                box,
                obstacleRegistered: true,
                pathsInvalidated: false,
                boxId: box.name,
                zoneId: placePose.Name,
                egressRequired: placePose.PostPlaceEgressPoint != default);
            P40NavTraceDiagnostics.LogObstacleRegistrySnapshot(
                "after_deposited_box_obstacle_registered",
                "registered",
                lastDepositedBoxId: box.name,
                robotPosition: _robotReference != null ? _robotReference.position : Vector3.zero);
            return true;
        }

        public bool RegisterDepositedBoxObstacle(string boxId)
        {
            if (string.IsNullOrWhiteSpace(boxId))
            {
                return false;
            }

            GameObject box = GameObject.Find(boxId);
            if (box == null)
            {
                return false;
            }

            return RegisterDepositedBoxObstacle(box, boxId);
        }

        public bool RegisterDepositedBoxObstacle(GameObject box, string boxId)
        {
            if (box == null)
            {
                return false;
            }

            return RegisterDepositedBoxObstacleImmediate(box, boxId);
        }

        private bool RegisterDepositedBoxObstacleImmediate(GameObject box, string boxId)
        {
            if (!_enableDepositedBoxNavMeshObstacle || box == null)
            {
                return false;
            }

            NavMeshObstacle obstacle = box.GetComponent<NavMeshObstacle>();
            if (obstacle == null)
            {
                obstacle = box.AddComponent<NavMeshObstacle>();
            }

            Bounds bounds = CalculateObjectBounds(box);
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.center = box.transform.InverseTransformPoint(bounds.center);
            obstacle.size = new Vector3(
                Mathf.Max(0.05f, bounds.size.x + _depositedBoxObstacleSizePadding.x),
                Mathf.Max(0.05f, bounds.size.y + _depositedBoxObstacleSizePadding.y),
                Mathf.Max(0.05f, bounds.size.z + _depositedBoxObstacleSizePadding.z));
            obstacle.carving = _depositedBoxObstacleCarve;
            obstacle.carveOnlyStationary = true;
            obstacle.carvingMoveThreshold = _depositedBoxObstacleCarveMoveThreshold;
            obstacle.enabled = true;
            LogEvent(
                "deposited_box_obstacle_registered",
                new Dictionary<string, object>
                {
                    ["box_id"] = boxId ?? box.name,
                    ["registered"] = true,
                    ["obstacle_size"] = obstacle.size,
                    ["obstacle_center"] = obstacle.center,
                    ["carving"] = obstacle.carving,
                    ["carve_move_threshold"] = obstacle.carvingMoveThreshold,
                    ["registration_phase"] = "after_post_place_egress"
                });
            return true;
        }

        private static Bounds CalculateObjectBounds(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return new Bounds(Vector3.zero, Vector3.one * 0.1f);
            }

            Collider[] colliders = gameObject.GetComponentsInChildren<Collider>(true);
            bool hasBounds = false;
            Bounds bounds = default;
            foreach (Collider collider in colliders)
            {
                if (collider == null || collider.isTrigger)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = collider.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }

            if (hasBounds)
            {
                return bounds;
            }

            Renderer renderer = gameObject.GetComponentInChildren<Renderer>();
            return renderer != null ? renderer.bounds : new Bounds(gameObject.transform.position, Vector3.one * 0.1f);
        }

        private static RoboticDepositRegistrationResult TryRegisterRoboticRoundDeposit(
            Component metadata,
            string destinationId,
            PlacePose placePose)
        {
            if (metadata == null)
            {
                return RoboticDepositRegistrationResult.Skipped("box_metadata_missing", false, false);
            }

            string boxType = GetMemberText(metadata, "boxType");
            if (!DestinationMatchesBoxType(destinationId, placePose.Name, boxType))
            {
                return RoboticDepositRegistrationResult.Skipped("destination_does_not_match_box_type", false, false);
            }

            MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour is not IExperimentalRoundDepositRegistrar registrar)
                {
                    continue;
                }

                bool belongsToActiveRound = true;
                if (behaviour is IExperimentalRoundLifecycle lifecycle)
                {
                    belongsToActiveRound = lifecycle.IsInActiveRound(metadata);
                }

                if (!belongsToActiveRound)
                {
                    return RoboticDepositRegistrationResult.Skipped("box_outside_active_round", true, false);
                }

                bool registered = registrar.TryRegisterCorrectDeposit(metadata, out string rejectionReason);
                if (!registered)
                {
                    return RoboticDepositRegistrationResult.Skipped(
                        string.IsNullOrWhiteSpace(rejectionReason) ? "round_manager_rejected_deposit" : rejectionReason,
                        true,
                        belongsToActiveRound);
                }

                return RoboticDepositRegistrationResult.Success("registered_with_round_manager", true, belongsToActiveRound);
            }

            return RoboticDepositRegistrationResult.Skipped("round_manager_missing", false, false);
        }

        private static bool DestinationMatchesBoxType(string destinationId, string placePoseName, string boxType)
        {
            if (string.IsNullOrWhiteSpace(boxType))
            {
                return false;
            }

            string normalizedType = NormalizeToken(boxType);
            return ContainsDestinationToken(destinationId, normalizedType) ||
                   ContainsDestinationToken(placePoseName, normalizedType);
        }

        private static bool ContainsDestinationToken(string value, string token)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            string normalized = NormalizeToken(value);
            return normalized.EndsWith(token, StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains($"ZONE{token}", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains($"PLACEPOINT{token}", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains($"DEPOSIT{token}", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .Trim()
                .ToUpperInvariant();
        }

        private void LogRoboticDepositRegistration(
            string destinationId,
            GameObject box,
            RoboticDepositRegistrationResult result)
        {
            LogEvent(
                "robotic_deposit_round_registration",
                new Dictionary<string, object>
                {
                    ["box_id"] = box != null ? box.name : string.Empty,
                    ["destination_id"] = destinationId ?? string.Empty,
                    ["registered"] = result.Registered,
                    ["reason"] = result.Reason,
                    ["round_manager_found"] = result.RoundManagerFound,
                    ["belongs_to_active_round"] = result.BelongsToActiveRound
                });
        }

        public void DebugDumpLastPlaceContext()
        {
            LogEvent("robotic_place_debug_last_context_snapshot", _lastPlaceDebugContext ?? new Dictionary<string, object>());
        }

        private void LogPlaceDebug(
            string eventType,
            string destinationId,
            GameObject box,
            PlacePose placePose,
            RoboticDepositRegistrationResult? registration,
            string phase,
            bool depositedBefore,
            bool depositedAfter)
        {
            Dictionary<string, object> payload = BuildPlaceDebugPayload(destinationId, box, placePose, registration, phase, depositedBefore, depositedAfter);
            _lastPlaceDebugContext = payload;
            LogEvent(eventType, payload);
        }

        private Dictionary<string, object> BuildPlaceDebugPayload(
            string destinationId,
            GameObject box,
            PlacePose placePose,
            RoboticDepositRegistrationResult? registration,
            string phase,
            bool depositedBefore,
            bool depositedAfter)
        {
            Component metadata = _heldObject.Metadata;
            RoboticDepositRegistrationResult result = registration ?? RoboticDepositRegistrationResult.Skipped("not_attempted", false, false);
            return new Dictionary<string, object>
            {
                ["phase"] = phase ?? string.Empty,
                ["held_box_id"] = box != null ? box.name : string.Empty,
                ["held_box_name"] = box != null ? box.name : string.Empty,
                ["box_category"] = GetMemberText(metadata, "boxType"),
                ["box_is_deposited_before"] = depositedBefore,
                ["box_is_deposited_after"] = depositedAfter,
                ["requested_place_target_id"] = destinationId ?? string.Empty,
                ["semantic_place_target_id"] = placePose.Name,
                ["destination_transform_name"] = placePose.TransformName,
                ["destination_transform_path"] = placePose.TransformPath,
                ["place_transform_position"] = placePose.PlaceTransformPosition,
                ["dynamic_place_pose_position"] = placePose.DynamicPlacePosePosition,
                ["place_point_fallback_position"] = placePose.PlacePointFallbackPosition,
                ["used_dynamic_place_pose"] = placePose.UsedDynamicPlacePose,
                ["used_place_point_fallback"] = placePose.UsedPlacePointFallback,
                ["place_navigation_candidate_id"] = placePose.PlaceNavigationCandidateId,
                ["candidate_kind"] = placePose.CandidateKind,
                ["place_area_source"] = placePose.PlaceAreaSource,
                ["place_pose_inset"] = placePose.PlacePoseInset,
                ["slot_id"] = placePose.SlotId,
                ["quadrant"] = placePose.SlotQuadrant,
                ["slot_index"] = placePose.SlotIndex,
                ["stack_level"] = placePose.StackLevel,
                ["slot_position"] = placePose.DynamicPlacePosePosition,
                ["used_place_slot_allocator"] = placePose.UsedPlaceSlotAllocator,
                ["post_place_egress_point"] = placePose.PostPlaceEgressPoint,
                ["round_manager_found"] = result.RoundManagerFound,
                ["belongs_to_active_round"] = result.BelongsToActiveRound,
                ["registered"] = result.Registered,
                ["rejection_reason"] = result.Registered ? string.Empty : result.Reason,
                ["frame_count"] = Time.frameCount,
                ["time_since_start"] = Time.time
            };
        }

        private void LogPlaceTargetSemanticResolution(string requestedId, string resolvedId, string source, Transform transform, bool fallback)
        {
            LogEvent(
                "place_target_semantic_resolution_debug",
                new Dictionary<string, object>
                {
                    ["requested_place_target_id"] = requestedId ?? string.Empty,
                    ["semantic_place_target_id"] = resolvedId ?? string.Empty,
                    ["resolution_source"] = source ?? string.Empty,
                    ["from_intent_original"] = string.Equals(requestedId, resolvedId, StringComparison.OrdinalIgnoreCase),
                    ["from_transform_name"] = transform != null,
                    ["fallback_by_name"] = fallback,
                    ["destination_transform_name"] = transform != null ? transform.name : string.Empty,
                    ["destination_transform_path"] = GetTransformPath(transform),
                    ["frame_count"] = Time.frameCount,
                    ["time_since_start"] = Time.time
                });
        }

        private static bool IsBehaviourEnabled(Component component)
        {
            return component is Behaviour behaviour && behaviour.enabled;
        }

        private static void SetBehaviourEnabled(Component component, bool enabled)
        {
            if (component is Behaviour behaviour)
            {
                behaviour.enabled = enabled;
            }
        }

        private void LogPickRequested(string objectId)
        {
            LogEvent("manipulation_pick_requested", BuildPayload(objectId, null, float.NaN, false, null, null));
        }

        private void LogPickFailed(string objectId, string reason, GameObject box, float distance, bool grabbedByUser, ColliderScanTelemetry? colliderScan = null)
        {
            LogEvent("manipulation_pick_failed", BuildPayload(objectId, box, distance, grabbedByUser, null, reason, null, colliderScan));
        }

        private void LogPlaceRequested(string destinationId)
        {
            LogEvent("manipulation_place_requested", BuildPayload(destinationId, _heldObject.GameObject, 0f, false, null, null));
        }

        private void LogPlaceFailed(string destinationId, string reason, GameObject box)
        {
            LogEvent("manipulation_place_failed", BuildPayload(destinationId, box, 0f, false, null, reason));
        }

        private void LogPlaceFailed(string destinationId, string reason, GameObject box, PlacePose placePose, PlaceRangeCheck rangeCheck)
        {
            Dictionary<string, object> payload = BuildPayload(destinationId, box, rangeCheck.DistanceToPlaceTransform, false, placePose.Position, reason, placePose: placePose);
            AddPlaceRangePayload(payload, placePose, rangeCheck);
            LogEvent("manipulation_place_failed", payload);
        }

        private void LogPlaceRangeChecked(string destinationId, GameObject box, PlacePose placePose, PlaceRangeCheck rangeCheck)
        {
            Dictionary<string, object> payload = BuildPayload(destinationId, box, rangeCheck.DistanceToPlaceTransform, false, placePose.Position, null, placePose: placePose);
            AddPlaceRangePayload(payload, placePose, rangeCheck);
            LogEvent("manipulation_place_range_checked", payload);
        }

        private void AddPlaceRangePayload(Dictionary<string, object> payload, PlacePose placePose, PlaceRangeCheck rangeCheck)
        {
            payload["place_target_id"] = placePose.Name;
            payload["place_transform_name"] = placePose.TransformName;
            payload["place_transform_path"] = placePose.TransformPath;
            payload["place_transform_position"] = placePose.PlaceTransformPosition;
            payload["anchor_position"] = rangeCheck.AnchorPosition;
            payload["robot_position"] = rangeCheck.RobotPosition;
            payload["distance_robot_or_anchor_to_place_transform"] = rangeCheck.DistanceToPlaceTransform;
            payload["actual_anchor_to_place_distance"] = rangeCheck.DistanceToPlaceTransform;
            payload["effective_place_range"] = rangeCheck.EffectiveRange;
            payload["max_relaxed_place_distance"] = _maxRelaxedPlaceDistance;
            payload["within_strict_place_range"] = rangeCheck.WithinRange;
            payload["within_relaxed_place_range"] = rangeCheck.DistanceToPlaceTransform <= _maxRelaxedPlaceDistance;
            payload["recovery_mode"] = _placeFailureRecoveryMode.ToString();
            payload["within_place_range"] = rangeCheck.WithinRange;
            payload["dynamic_place_pose_position"] = placePose.DynamicPlacePosePosition;
            payload["place_point_fallback_position"] = placePose.PlacePointFallbackPosition;
            payload["actual_anchor_to_dynamic_place_pose_distance"] = rangeCheck.DistanceToPlaceTransform;
            payload["used_dynamic_place_pose"] = placePose.UsedDynamicPlacePose;
            payload["used_place_point_fallback"] = placePose.UsedPlacePointFallback;
            payload["place_navigation_candidate_id"] = placePose.PlaceNavigationCandidateId;
            payload["candidate_id"] = placePose.PlaceNavigationCandidateId;
            payload["candidate_kind"] = placePose.CandidateKind;
            payload["place_area_source"] = placePose.PlaceAreaSource;
            payload["place_pose_inset"] = placePose.PlacePoseInset;
            payload["slot_id"] = placePose.SlotId;
            payload["quadrant"] = placePose.SlotQuadrant;
            payload["slot_index"] = placePose.SlotIndex;
            payload["stack_level"] = placePose.StackLevel;
            payload["slot_position"] = placePose.DynamicPlacePosePosition;
            payload["used_place_slot_allocator"] = placePose.UsedPlaceSlotAllocator;
            payload["post_place_egress_point"] = placePose.PostPlaceEgressPoint;
        }

        private void LogEvent(string eventType, Dictionary<string, object> payload)
        {
            TiagoExperimentTelemetry.LogEvent(eventType, payload);
        }

        private Dictionary<string, object> BuildPayload(
            string objectId,
            GameObject box,
            float distance,
            bool grabbedByUser,
            Vector3? placePosition,
            string reason,
            AttachTelemetry? attachTelemetry = null,
            ColliderScanTelemetry? colliderScan = null,
            PlacePose? placePose = null,
            bool lockedAfterDeposit = false)
        {
            Vector3 robotPosition = _robotReference != null ? _robotReference.position : Vector3.zero;
            Vector3 objectPosition = box != null ? box.transform.position : Vector3.zero;

            var payload = new Dictionary<string, object>
            {
                ["object_id"] = objectId ?? string.Empty,
                ["object_name"] = box != null ? box.name : string.Empty,
                ["robot_position"] = robotPosition,
                ["object_position"] = objectPosition,
                ["held_object"] = _heldObject.IsValid ? _heldObject.GameObject.name : string.Empty,
                ["held_object_id"] = HeldObjectId,
                ["manipulation_state"] = _manipulationState,
                ["distance_to_object"] = distance,
                ["grabbed_by_user"] = grabbedByUser
            };

            if (placePosition.HasValue)
            {
                payload["target_place_position"] = placePosition.Value;
            }

            if (placePose.HasValue)
            {
                PlacePose pose = placePose.Value;
                payload["place_pose_source"] = pose.Source;
                payload["place_pose_name"] = pose.Name;
                payload["place_transform_name"] = pose.TransformName;
                payload["place_transform_path"] = pose.TransformPath;
                payload["place_transform_position"] = pose.PlaceTransformPosition;
                payload["dynamic_place_pose_position"] = pose.DynamicPlacePosePosition;
                payload["place_point_fallback_position"] = pose.PlacePointFallbackPosition;
                payload["used_dynamic_place_pose"] = pose.UsedDynamicPlacePose;
                payload["used_place_point_fallback"] = pose.UsedPlacePointFallback;
                payload["place_navigation_candidate_id"] = pose.PlaceNavigationCandidateId;
                payload["candidate_id"] = pose.PlaceNavigationCandidateId;
                payload["candidate_kind"] = pose.CandidateKind;
                payload["place_area_source"] = pose.PlaceAreaSource;
                payload["place_pose_inset"] = pose.PlacePoseInset;
                payload["slot_id"] = pose.SlotId;
                payload["quadrant"] = pose.SlotQuadrant;
                payload["slot_index"] = pose.SlotIndex;
                payload["stack_level"] = pose.StackLevel;
                payload["slot_position"] = pose.DynamicPlacePosePosition;
                payload["used_place_slot_allocator"] = pose.UsedPlaceSlotAllocator;
                payload["post_place_egress_point"] = pose.PostPlaceEgressPoint;
                payload["target_place_rotation"] = pose.Rotation.eulerAngles;
                payload["metadata_lock_after_deposit_invoked"] = lockedAfterDeposit;
                payload["release_physics_policy"] = lockedAfterDeposit
                    ? "BoxMetadata.LockAfterDeposit"
                    : "restore_original_rigidbody_state";
            }

            if (!string.IsNullOrWhiteSpace(reason))
            {
                payload["reason"] = reason;
            }

            if (attachTelemetry.HasValue)
            {
                AttachTelemetry telemetry = attachTelemetry.Value;
                payload["object_position_before_attach"] = telemetry.ObjectPositionBeforeAttach;
                payload["object_position_after_attach"] = telemetry.ObjectPositionAfterAttach;
                payload["object_local_position_after_attach"] = telemetry.ObjectLocalPositionAfterAttach;
                payload["object_local_rotation_after_attach"] = telemetry.ObjectLocalRotationAfterAttach;
                payload["anchor_position"] = telemetry.AnchorPosition;
                payload["anchor_name"] = telemetry.AnchorName;
                payload["rigidbody_is_kinematic_after_attach"] = telemetry.RigidbodyIsKinematicAfterAttach;
                payload["use_gravity_after_attach"] = telemetry.UseGravityAfterAttach;
                payload["rigidbody_detect_collisions_before_attach"] = telemetry.RigidbodyDetectCollisionsBeforeAttach;
                payload["rigidbody_detect_collisions_after_attach"] = telemetry.RigidbodyDetectCollisionsAfterAttach;
                payload["disable_held_object_colliders_requested"] = telemetry.DisableHeldObjectCollidersRequested;
                payload["colliders_disabled_while_held"] = telemetry.CollidersDisabledWhileHeld;
                payload["functional_colliders_found_count"] = telemetry.FunctionalCollidersFoundCount;
                payload["functional_colliders_disabled_count"] = telemetry.FunctionalCollidersDisabledCount;
                payload["functional_colliders_remaining_enabled_count"] = telemetry.FunctionalCollidersRemainingEnabledCount;
                payload["auxiliary_colliders_skipped_count"] = telemetry.AuxiliaryCollidersSkippedCount;
                payload["auxiliary_colliders_skipped_reason"] = telemetry.AuxiliaryCollidersSkippedReason;
                payload["xr_grab_disabled_while_held"] = telemetry.XRGrabDisabledWhileHeld;
                payload["held_local_position_offset_applied"] = telemetry.HeldLocalPositionOffsetApplied;
                payload["held_local_euler_offset_applied"] = telemetry.HeldLocalEulerOffsetApplied;
                payload["alignment_mode"] = telemetry.AlignmentMode;
                payload["bounds_source"] = telemetry.BoundsSource;
                payload["bounds_center_before_attach"] = telemetry.BoundsCenterBeforeAttach;
                payload["bounds_extents_before_attach"] = telemetry.BoundsExtentsBeforeAttach;
                payload["bounds_min_y_before_attach"] = telemetry.BoundsMinYBeforeAttach;
                payload["bounds_center_after_attach"] = telemetry.BoundsCenterAfterAttach;
                payload["bounds_min_y_after_attach"] = telemetry.BoundsMinYAfterAttach;
                payload["pivot_to_bounds_center_before_attach"] = telemetry.PivotToBoundsCenterBeforeAttach;
                payload["desired_bounds_center_world"] = telemetry.DesiredBoundsCenterWorld;
                payload["object_world_position_after_alignment"] = telemetry.ObjectWorldPositionAfterAlignment;
                payload["all_colliders_found_count"] = telemetry.AllCollidersFoundCount;
                payload["skipped_colliders_count"] = telemetry.SkippedCollidersCount;
                payload["skipped_colliders_names"] = telemetry.SkippedCollidersNames;
                payload["skipped_colliders_reasons"] = telemetry.SkippedCollidersReasons;
                payload["object_root_name"] = telemetry.ObjectRootName;
                payload["rigidbody_owner_name"] = telemetry.RigidbodyOwnerName;
                payload["box_metadata_owner_name"] = telemetry.BoxMetadataOwnerName;
            }

            if (colliderScan.HasValue)
            {
                ColliderScanTelemetry scan = colliderScan.Value;
                payload["all_colliders_found_count"] = scan.AllCollidersFoundCount;
                payload["skipped_colliders_count"] = scan.SkippedCollidersCount;
                payload["skipped_colliders_names"] = scan.SkippedCollidersNames;
                payload["skipped_colliders_reasons"] = scan.SkippedCollidersReasons;
                payload["object_root_name"] = scan.ObjectRootName;
                payload["rigidbody_owner_name"] = scan.RigidbodyOwnerName;
                payload["box_metadata_owner_name"] = scan.BoxMetadataOwnerName;
            }

            return payload;
        }

        private struct AttachTelemetry
        {
            public Vector3 ObjectPositionBeforeAttach;
            public Vector3 ObjectPositionAfterAttach;
            public Vector3 ObjectLocalPositionAfterAttach;
            public Vector3 ObjectLocalRotationAfterAttach;
            public string AlignmentMode;
            public string BoundsSource;
            public Vector3 BoundsCenterBeforeAttach;
            public Vector3 BoundsExtentsBeforeAttach;
            public float BoundsMinYBeforeAttach;
            public Vector3 BoundsCenterAfterAttach;
            public float BoundsMinYAfterAttach;
            public Vector3 PivotToBoundsCenterBeforeAttach;
            public Vector3 DesiredBoundsCenterWorld;
            public Vector3 ObjectWorldPositionAfterAlignment;
            public Vector3 AnchorPosition;
            public string AnchorName;
            public bool RigidbodyIsKinematicAfterAttach;
            public bool UseGravityAfterAttach;
            public bool RigidbodyDetectCollisionsBeforeAttach;
            public bool RigidbodyDetectCollisionsAfterAttach;
            public bool DisableHeldObjectCollidersRequested;
            public bool CollidersDisabledWhileHeld;
            public int FunctionalCollidersFoundCount;
            public int FunctionalCollidersDisabledCount;
            public int FunctionalCollidersRemainingEnabledCount;
            public int AllCollidersFoundCount;
            public int SkippedCollidersCount;
            public string SkippedCollidersNames;
            public string SkippedCollidersReasons;
            public string ObjectRootName;
            public string RigidbodyOwnerName;
            public string BoxMetadataOwnerName;
            public int AuxiliaryCollidersSkippedCount;
            public string AuxiliaryCollidersSkippedReason;
            public bool XRGrabDisabledWhileHeld;
            public Vector3 HeldLocalPositionOffsetApplied;
            public Vector3 HeldLocalEulerOffsetApplied;
        }

        private struct HeldObjectState
        {
            public GameObject GameObject;
            public Component Metadata;
            public Rigidbody Rigidbody;
            public Component GrabInteractable;
            public Transform OriginalParent;
            public Vector3 OriginalPosition;
            public Quaternion OriginalRotation;
            public bool WasKinematic;
            public bool UsedGravity;
            public RigidbodyConstraints Constraints;
            public Vector3 LinearVelocity;
            public Vector3 AngularVelocity;
            public CollisionDetectionMode CollisionDetectionMode;
            public RigidbodyInterpolation Interpolation;
            public bool DetectedCollisions;
            public bool GrabInteractableWasEnabled;
            public Collider[] Colliders;
            public bool[] ColliderWasEnabled;
            public int AuxiliaryCollidersSkippedCount;
            public string AuxiliaryCollidersSkippedReason;
            public ColliderScanTelemetry ColliderScan;
            public BoundsSnapshot BoundsBeforeAttach;
            public Vector3 DesiredLocalPosition;
            public Quaternion DesiredLocalRotation;
            public string ObjectId;

            public bool IsValid => GameObject != null;

            public static HeldObjectState Capture(
                GameObject gameObject,
                Component metadata,
                Rigidbody rigidbody,
                Component grabInteractable)
            {
                Collider[] colliders = CollectFunctionalColliders(gameObject, rigidbody, metadata, out bool[] colliderWasEnabled, out ColliderScanTelemetry colliderScan);
                BoundsSnapshot boundsSnapshot = BoundsSnapshot.Invalid;
                if (TryCalculateBoundsFromColliders(colliders, out Bounds colliderBounds))
                {
                    boundsSnapshot = BoundsSnapshot.FromBounds(colliderBounds, "functional_colliders");
                }
                else if (TryCalculateRendererBounds(gameObject, out Bounds rendererBounds))
                {
                    boundsSnapshot = BoundsSnapshot.FromBounds(rendererBounds, "renderers_fallback");
                }

                return new HeldObjectState
                {
                    GameObject = gameObject,
                    Metadata = metadata,
                    Rigidbody = rigidbody,
                    GrabInteractable = grabInteractable,
                    OriginalParent = gameObject.transform.parent,
                    OriginalPosition = gameObject.transform.position,
                    OriginalRotation = gameObject.transform.rotation,
                    WasKinematic = rigidbody != null && rigidbody.isKinematic,
                    UsedGravity = rigidbody == null || rigidbody.useGravity,
                    Constraints = rigidbody != null ? rigidbody.constraints : RigidbodyConstraints.None,
                    LinearVelocity = rigidbody != null ? rigidbody.linearVelocity : Vector3.zero,
                    AngularVelocity = rigidbody != null ? rigidbody.angularVelocity : Vector3.zero,
                    CollisionDetectionMode = rigidbody != null ? rigidbody.collisionDetectionMode : CollisionDetectionMode.Discrete,
                    Interpolation = rigidbody != null ? rigidbody.interpolation : RigidbodyInterpolation.None,
                    DetectedCollisions = rigidbody == null || rigidbody.detectCollisions,
                    GrabInteractableWasEnabled = IsBehaviourEnabled(grabInteractable),
                    Colliders = colliders,
                    ColliderWasEnabled = colliderWasEnabled,
                    AuxiliaryCollidersSkippedCount = colliderScan.SkippedCollidersCount,
                    AuxiliaryCollidersSkippedReason = colliderScan.SkippedCollidersReasons,
                    ColliderScan = colliderScan,
                    BoundsBeforeAttach = boundsSnapshot,
                    ObjectId = gameObject.name
                };
            }
        }

        private static Collider[] CollectFunctionalColliders(GameObject gameObject, Rigidbody rigidbody, Component metadata, out bool[] colliderWasEnabled, out ColliderScanTelemetry scan)
        {
            Collider[] allColliders = gameObject.GetComponentsInChildren<Collider>(true);
            List<Collider> functionalColliders = new();
            List<string> skippedNames = new();
            List<string> skippedReasons = new();
            foreach (Collider collider in allColliders)
            {
                if (collider == null)
                {
                    continue;
                }

                if (IsBoxEdgeFrameCollider(collider))
                {
                    skippedNames.Add(collider.name);
                    skippedReasons.Add("box_edge_frame");
                    continue;
                }

                if (collider.isTrigger && !IsPrimaryObjectCollider(collider, gameObject, rigidbody))
                {
                    skippedNames.Add(collider.name);
                    skippedReasons.Add("trigger_non_primary");
                    continue;
                }

                functionalColliders.Add(collider);
            }

            colliderWasEnabled = new bool[functionalColliders.Count];
            for (int i = 0; i < functionalColliders.Count; i++)
            {
                colliderWasEnabled[i] = functionalColliders[i] != null && functionalColliders[i].enabled;
            }

            scan = new ColliderScanTelemetry
            {
                AllCollidersFoundCount = allColliders.Length,
                SkippedCollidersCount = skippedNames.Count,
                SkippedCollidersNames = string.Join("|", skippedNames),
                SkippedCollidersReasons = string.Join("|", skippedReasons),
                ObjectRootName = gameObject != null ? gameObject.name : string.Empty,
                RigidbodyOwnerName = rigidbody != null ? rigidbody.gameObject.name : string.Empty,
                BoxMetadataOwnerName = metadata != null ? metadata.gameObject.name : string.Empty
            };
            return functionalColliders.ToArray();
        }

        private static bool IsBoxEdgeFrameCollider(Collider collider)
        {
            if (collider == null)
            {
                return false;
            }

            Component[] components = collider.GetComponents<Component>();
            foreach (Component component in components)
            {
                if (component == null)
                {
                    continue;
                }

                string typeName = component.GetType().Name;
                if (string.Equals(typeName, "BoxEdgeFrame", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPrimaryObjectCollider(Collider collider, GameObject root, Rigidbody rigidbody)
        {
            if (collider == null || root == null)
            {
                return false;
            }

            return collider.gameObject == root ||
                (rigidbody != null && collider.gameObject == rigidbody.gameObject) ||
                collider.transform.parent == root.transform;
        }

        private static bool TryCalculateBoundsFromColliders(Collider[] colliders, out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;
            if (colliders == null)
            {
                return false;
            }

            foreach (Collider collider in colliders)
            {
                if (collider == null)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = collider.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }

            return hasBounds;
        }

        private static bool TryCalculateRendererBounds(GameObject gameObject, out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;
            if (gameObject == null)
            {
                return false;
            }

            Renderer[] renderers = gameObject.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasBounds;
        }

        private static int CountEnabledColliders(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return 0;
            }

            Collider[] colliders = gameObject.GetComponentsInChildren<Collider>(true);
            int count = 0;
            foreach (Collider collider in colliders)
            {
                if (collider != null && collider.enabled)
                {
                    count++;
                }
            }

            return count;
        }

        private readonly struct BoundsSnapshot
        {
            private BoundsSnapshot(bool isValid, Vector3 center, Vector3 extents, float minY, string source)
            {
                IsValid = isValid;
                Center = center;
                Extents = extents;
                MinY = minY;
                Source = source;
            }

            public bool IsValid { get; }
            public Vector3 Center { get; }
            public Vector3 Extents { get; }
            public float MinY { get; }
            public string Source { get; }

            public static BoundsSnapshot Invalid => new(false, Vector3.zero, Vector3.zero, float.NaN, "none");

            public static BoundsSnapshot FromBounds(Bounds bounds, string source)
            {
                return new BoundsSnapshot(true, bounds.center, bounds.extents, bounds.min.y, source);
            }
        }

        private struct ColliderScanTelemetry
        {
            public int AllCollidersFoundCount;
            public int SkippedCollidersCount;
            public string SkippedCollidersNames;
            public string SkippedCollidersReasons;
            public string ObjectRootName;
            public string RigidbodyOwnerName;
            public string BoxMetadataOwnerName;
        }

        private readonly struct PlacePose
        {
            public PlacePose(
                Vector3 position,
                Quaternion rotation,
                string name,
                string source,
                string transformName = "",
                string transformPath = "",
                Vector3 placeTransformPosition = default,
                Vector3 dynamicPlacePosePosition = default,
                Vector3 placePointFallbackPosition = default,
                string placeNavigationCandidateId = "",
                string candidateKind = "",
                string placeAreaSource = "",
                float placePoseInset = 0f,
                bool usedDynamicPlacePose = false,
                bool usedPlacePointFallback = true,
                string slotId = "",
                string slotQuadrant = "",
                int slotIndex = -1,
                int stackLevel = 0,
                bool usedPlaceSlotAllocator = false,
                Vector3 postPlaceEgressPoint = default)
            {
                Position = position;
                Rotation = rotation;
                Name = name ?? string.Empty;
                Source = source ?? string.Empty;
                TransformName = string.IsNullOrWhiteSpace(transformName) ? Name : transformName;
                TransformPath = transformPath ?? string.Empty;
                PlaceTransformPosition = placeTransformPosition == default ? position : placeTransformPosition;
                DynamicPlacePosePosition = dynamicPlacePosePosition == default ? PlaceTransformPosition : dynamicPlacePosePosition;
                PlacePointFallbackPosition = placePointFallbackPosition == default ? PlaceTransformPosition : placePointFallbackPosition;
                PlaceNavigationCandidateId = placeNavigationCandidateId ?? string.Empty;
                CandidateKind = candidateKind ?? string.Empty;
                PlaceAreaSource = placeAreaSource ?? string.Empty;
                PlacePoseInset = placePoseInset;
                UsedDynamicPlacePose = usedDynamicPlacePose;
                UsedPlacePointFallback = usedPlacePointFallback;
                SlotId = slotId ?? string.Empty;
                SlotQuadrant = slotQuadrant ?? string.Empty;
                SlotIndex = slotIndex;
                StackLevel = stackLevel;
                UsedPlaceSlotAllocator = usedPlaceSlotAllocator;
                PostPlaceEgressPoint = postPlaceEgressPoint == default ? PlaceTransformPosition : postPlaceEgressPoint;
            }

            public Vector3 Position { get; }
            public Quaternion Rotation { get; }
            public string Name { get; }
            public string Source { get; }
            public string TransformName { get; }
            public string TransformPath { get; }
            public Vector3 PlaceTransformPosition { get; }
            public Vector3 DynamicPlacePosePosition { get; }
            public Vector3 PlacePointFallbackPosition { get; }
            public string PlaceNavigationCandidateId { get; }
            public string CandidateKind { get; }
            public string PlaceAreaSource { get; }
            public float PlacePoseInset { get; }
            public bool UsedDynamicPlacePose { get; }
            public bool UsedPlacePointFallback { get; }
            public string SlotId { get; }
            public string SlotQuadrant { get; }
            public int SlotIndex { get; }
            public int StackLevel { get; }
            public bool UsedPlaceSlotAllocator { get; }
            public Vector3 PostPlaceEgressPoint { get; }
        }

        private readonly struct PlaceRangeCheck
        {
            public PlaceRangeCheck(Vector3 anchorPosition, Vector3 robotPosition, float distanceToPlaceTransform, float effectiveRange, bool withinRange)
            {
                AnchorPosition = anchorPosition;
                RobotPosition = robotPosition;
                DistanceToPlaceTransform = distanceToPlaceTransform;
                EffectiveRange = effectiveRange;
                WithinRange = withinRange;
            }

            public Vector3 AnchorPosition { get; }
            public Vector3 RobotPosition { get; }
            public float DistanceToPlaceTransform { get; }
            public float EffectiveRange { get; }
            public bool WithinRange { get; }
        }

        private readonly struct RoboticDepositRegistrationResult
        {
            private RoboticDepositRegistrationResult(bool registered, string reason, bool roundManagerFound, bool belongsToActiveRound)
            {
                Registered = registered;
                Reason = reason ?? string.Empty;
                RoundManagerFound = roundManagerFound;
                BelongsToActiveRound = belongsToActiveRound;
            }

            public bool Registered { get; }
            public string Reason { get; }
            public bool RoundManagerFound { get; }
            public bool BelongsToActiveRound { get; }

            public static RoboticDepositRegistrationResult Success(string reason, bool roundManagerFound, bool belongsToActiveRound)
            {
                return new RoboticDepositRegistrationResult(true, reason, roundManagerFound, belongsToActiveRound);
            }

            public static RoboticDepositRegistrationResult Skipped(string reason, bool roundManagerFound, bool belongsToActiveRound)
            {
                return new RoboticDepositRegistrationResult(false, reason, roundManagerFound, belongsToActiveRound);
            }
        }

        private sealed class SettleDiagnosticsRunner : MonoBehaviour
        {
            private TiagoUnityManipulationService _service;

            public void Begin(TiagoUnityManipulationService service)
            {
                _service = service;
                StartCoroutine(Run());
            }

            private IEnumerator Run()
            {
                _service?.LogSettleSample("attach_frame");
                yield return new WaitForFixedUpdate();
                _service?.LogSettleSample("next_fixed_update");
                yield return new WaitForSeconds(0.25f);
                _service?.LogSettleSample("after_0_25s");
                _service?.LogPickStabilized();
                Destroy(gameObject);
            }
        }
    }

    [DefaultExecutionOrder(10000)]
    public sealed class HeldObjectPoseLock : MonoBehaviour
    {
        private Transform _anchor;
        private Rigidbody _rigidbody;
        private Vector3 _desiredLocalPosition;
        private Quaternion _desiredLocalRotation;
        private float _driftWarningThreshold;
        private float _driftCorrectionThreshold;
        private Action<string, Dictionary<string, object>> _logEvent;
        private string _objectId;
        private Component _grabInteractable;
        private Collider[] _functionalColliders = Array.Empty<Collider>();
        private bool _initialized;
        private bool _driftEpisodeLogged;
        private bool _enforcementEpisodeLogged;
        private int _lastDriftFrame = -1;

        public bool IsActive => _initialized && enabled;
        public float LastCorrectionTime { get; private set; } = float.NegativeInfinity;
        public Vector3 DesiredLocalPosition => _desiredLocalPosition;
        public Vector3 DesiredLocalRotationEuler => _desiredLocalRotation.eulerAngles;

        public void Initialize(
            Transform anchor,
            Vector3 desiredLocalPosition,
            Quaternion desiredLocalRotation,
            Rigidbody rigidbody,
            Component grabInteractable,
            Collider[] functionalColliders,
            float driftWarningThreshold,
            float driftCorrectionThreshold,
            string objectId,
            Action<string, Dictionary<string, object>> logEvent)
        {
            _anchor = anchor;
            _desiredLocalPosition = desiredLocalPosition;
            _desiredLocalRotation = desiredLocalRotation;
            _rigidbody = rigidbody;
            _grabInteractable = grabInteractable;
            _functionalColliders = functionalColliders ?? Array.Empty<Collider>();
            _driftWarningThreshold = Mathf.Max(0f, driftWarningThreshold);
            _driftCorrectionThreshold = Mathf.Max(0f, driftCorrectionThreshold);
            _objectId = objectId ?? string.Empty;
            _logEvent = logEvent;
            _driftEpisodeLogged = false;
            _enforcementEpisodeLogged = false;
            _lastDriftFrame = -1;
            _initialized = _anchor != null;
            Enforce("initialize");
        }

        private void FixedUpdate()
        {
            Enforce("fixed_update");
        }

        private void LateUpdate()
        {
            Enforce("late_update");
        }

        private void Enforce(string phase)
        {
            if (!_initialized || _anchor == null)
            {
                return;
            }

            Transform current = transform;
            Transform previousParent = current.parent;
            Vector3 previousLocalPosition = current.localPosition;
            Quaternion previousLocalRotation = current.localRotation;
            float localPositionError = Vector3.Distance(previousLocalPosition, _desiredLocalPosition);
            float localRotationErrorDeg = Quaternion.Angle(previousLocalRotation, _desiredLocalRotation);
            bool parentDrifted = previousParent != _anchor;
            bool shouldCorrect = parentDrifted ||
                localPositionError > _driftCorrectionThreshold ||
                localRotationErrorDeg > 0.1f;
            bool shouldWarn = shouldCorrect || localPositionError > _driftWarningThreshold;

            if (!shouldWarn)
            {
                if (phase == "late_update" && _lastDriftFrame < Time.frameCount)
                {
                    _driftEpisodeLogged = false;
                    _enforcementEpisodeLogged = false;
                }
                return;
            }

            bool logDrift = !_driftEpisodeLogged || parentDrifted;
            bool logEnforcement = shouldCorrect && (!_enforcementEpisodeLogged || parentDrifted);
            Dictionary<string, object> payload = null;
            if (logDrift || logEnforcement)
            {
                payload = BuildPayload(
                    phase,
                    previousParent,
                    previousLocalPosition,
                    previousLocalRotation,
                    localPositionError,
                    localRotationErrorDeg);
            }

            if (logDrift)
            {
                _logEvent?.Invoke("manipulation_held_pose_drift_detected", payload);
                _driftEpisodeLogged = true;
            }
            _lastDriftFrame = Time.frameCount;

            if (!shouldCorrect)
            {
                return;
            }

            if (parentDrifted)
            {
                current.SetParent(_anchor, false);
            }

            current.localPosition = _desiredLocalPosition;
            current.localRotation = _desiredLocalRotation;

            if (_rigidbody != null)
            {
                _rigidbody.position = current.position;
                _rigidbody.rotation = current.rotation;
                if (!_rigidbody.isKinematic)
                {
                    _rigidbody.linearVelocity = Vector3.zero;
                    _rigidbody.angularVelocity = Vector3.zero;
                }

                _rigidbody.Sleep();
            }

            Physics.SyncTransforms();
            LastCorrectionTime = Time.time;
            if (logEnforcement)
            {
                _logEvent?.Invoke("manipulation_held_pose_enforced", payload);
                _enforcementEpisodeLogged = true;
            }
        }

        private Dictionary<string, object> BuildPayload(
            string phase,
            Transform previousParent,
            Vector3 previousLocalPosition,
            Quaternion previousLocalRotation,
            float localPositionError,
            float localRotationErrorDeg)
        {
            return new Dictionary<string, object>
            {
                ["object_id"] = _objectId,
                ["phase"] = phase,
                ["update_phase"] = phase,
                ["previous_local_position"] = previousLocalPosition,
                ["desired_local_position"] = _desiredLocalPosition,
                ["local_position_error"] = localPositionError,
                ["previous_local_rotation"] = previousLocalRotation.eulerAngles,
                ["desired_local_rotation"] = _desiredLocalRotation.eulerAngles,
                ["local_rotation_error_deg"] = localRotationErrorDeg,
                ["parent_name"] = previousParent != null ? previousParent.name : string.Empty,
                ["desired_parent_name"] = _anchor != null ? _anchor.name : string.Empty,
                ["rb_is_kinematic"] = _rigidbody != null && _rigidbody.isKinematic,
                ["rb_detect_collisions"] = _rigidbody != null && _rigidbody.detectCollisions,
                ["xr_grab_enabled"] = IsBehaviourEnabled(_grabInteractable),
                ["enabled_colliders_count"] = CountEnabledFunctionalColliders()
            };
        }

        private int CountEnabledFunctionalColliders()
        {
            int count = 0;
            for (int i = 0; i < _functionalColliders.Length; i++)
            {
                Collider collider = _functionalColliders[i];
                if (collider != null && collider.enabled)
                {
                    count++;
                }
            }

            return count;
        }

        private static bool IsBehaviourEnabled(Component component)
        {
            return component is Behaviour behaviour && behaviour.enabled;
        }
    }
}
