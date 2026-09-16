using System;
using System.Collections.Generic;
using UnityEngine;

namespace Autonomy.Domain
{
    public enum BoxRoundStatus
    {
        Pending,
        Assigned,
        Completed,
        Excluded
    }

    public enum AutonomousSelectionPolicy
    {
        LocalPickCost
    }

    public sealed class BoxRoundItem
    {
        public BoxRoundItem(
            string boxId,
            string category,
            string placeTargetId,
            Vector3 pickupPosition,
            Vector3 placePosition,
            string pickupApproachCandidateId = "",
            string pickupApproachSide = "",
            string pickupApproachSource = "",
            string placeNavigationCandidateId = "",
            string placeNavigationSideOrCorner = "",
            string placeNavigationSource = "",
            Vector3? dynamicPlacePosePosition = null,
            bool useDynamicPlacePose = false,
            Vector3? placePointFallbackPosition = null,
            string placeAreaSource = "",
            float placePoseInset = 0f,
            string placeSlotId = "",
            string placeSlotQuadrant = "",
            int placeSlotIndex = -1,
            int placeStackLevel = 0,
            bool usedPlaceSlotAllocator = false,
            Vector3? postPlaceEgressPoint = null)
        {
            BoxId = boxId ?? string.Empty;
            Category = category ?? string.Empty;
            PlaceTargetId = placeTargetId ?? string.Empty;
            PickupPosition = pickupPosition;
            PlacePosition = placePosition;
            PickupApproachCandidateId = pickupApproachCandidateId ?? string.Empty;
            PickupApproachSide = pickupApproachSide ?? string.Empty;
            PickupApproachSource = pickupApproachSource ?? string.Empty;
            PlaceNavigationCandidateId = placeNavigationCandidateId ?? string.Empty;
            PlaceNavigationSideOrCorner = placeNavigationSideOrCorner ?? string.Empty;
            PlaceNavigationSource = placeNavigationSource ?? string.Empty;
            DynamicPlacePosePosition = dynamicPlacePosePosition ?? placePosition;
            UseDynamicPlacePose = useDynamicPlacePose;
            PlacePointFallbackPosition = placePointFallbackPosition ?? placePosition;
            PlaceAreaSource = placeAreaSource ?? string.Empty;
            PlacePoseInset = placePoseInset;
            PlaceSlotId = placeSlotId ?? string.Empty;
            PlaceSlotQuadrant = placeSlotQuadrant ?? string.Empty;
            PlaceSlotIndex = placeSlotIndex;
            PlaceStackLevel = placeStackLevel;
            UsedPlaceSlotAllocator = usedPlaceSlotAllocator;
            PostPlaceEgressPoint = postPlaceEgressPoint ?? placePosition;
        }

        public string BoxId { get; }
        public string Category { get; }
        public string PlaceTargetId { get; }
        public Vector3 PickupPosition { get; }
        public Vector3 PlacePosition { get; }
        public string PickupApproachCandidateId { get; }
        public string PickupApproachSide { get; }
        public string PickupApproachSource { get; }
        public string PlaceNavigationCandidateId { get; }
        public string PlaceNavigationSideOrCorner { get; }
        public string PlaceNavigationSource { get; }
        public Vector3 DynamicPlacePosePosition { get; }
        public bool UseDynamicPlacePose { get; }
        public Vector3 PlacePointFallbackPosition { get; }
        public string PlaceAreaSource { get; }
        public float PlacePoseInset { get; }
        public string PlaceSlotId { get; }
        public string PlaceSlotQuadrant { get; }
        public int PlaceSlotIndex { get; }
        public int PlaceStackLevel { get; }
        public bool UsedPlaceSlotAllocator { get; }
        public Vector3 PostPlaceEgressPoint { get; }
    }

    public sealed class BoxRoundState
    {
        private readonly Dictionary<string, BoxRoundStatus> _states = new(StringComparer.OrdinalIgnoreCase);

        public void Initialize(IEnumerable<BoxRoundItem> boxes)
        {
            _states.Clear();
            if (boxes == null)
            {
                return;
            }

            foreach (BoxRoundItem box in boxes)
            {
                if (box != null && !string.IsNullOrWhiteSpace(box.BoxId) && !_states.ContainsKey(box.BoxId))
                {
                    _states.Add(box.BoxId, BoxRoundStatus.Pending);
                }
            }
        }

        public bool HasAssignedBox => ContainsStatus(BoxRoundStatus.Assigned);
        public bool RoundCompleted => !ContainsStatus(BoxRoundStatus.Pending) && !ContainsStatus(BoxRoundStatus.Assigned);
        public int PendingCount => CountStatus(BoxRoundStatus.Pending);
        public int AssignedCount => CountStatus(BoxRoundStatus.Assigned);
        public int CompletedCount => CountStatus(BoxRoundStatus.Completed);
        public int ExcludedCount => CountStatus(BoxRoundStatus.Excluded);

        public bool IsSelectable(BoxRoundItem box)
        {
            return box != null &&
                   _states.TryGetValue(box.BoxId, out BoxRoundStatus status) &&
                   status == BoxRoundStatus.Pending;
        }

        public bool TryMarkAssigned(string boxId)
        {
            return TrySetStatus(boxId, BoxRoundStatus.Pending, BoxRoundStatus.Assigned);
        }

        public bool TryMarkCompleted(string boxId)
        {
            if (string.IsNullOrWhiteSpace(boxId) || !_states.ContainsKey(boxId))
            {
                return false;
            }

            _states[boxId] = BoxRoundStatus.Completed;
            return true;
        }

        public bool TryMarkExcluded(string boxId)
        {
            if (string.IsNullOrWhiteSpace(boxId) || !_states.ContainsKey(boxId))
            {
                return false;
            }

            _states[boxId] = BoxRoundStatus.Excluded;
            return true;
        }

        public bool TryMarkPendingFromAssigned(string boxId)
        {
            return TrySetStatus(boxId, BoxRoundStatus.Assigned, BoxRoundStatus.Pending);
        }

        public BoxRoundStatus GetStatus(string boxId)
        {
            return !string.IsNullOrWhiteSpace(boxId) && _states.TryGetValue(boxId, out BoxRoundStatus status)
                ? status
                : BoxRoundStatus.Excluded;
        }

        private bool TrySetStatus(string boxId, BoxRoundStatus expected, BoxRoundStatus next)
        {
            if (string.IsNullOrWhiteSpace(boxId) || !_states.TryGetValue(boxId, out BoxRoundStatus current) || current != expected)
            {
                return false;
            }

            _states[boxId] = next;
            return true;
        }

        private bool ContainsStatus(BoxRoundStatus status)
        {
            foreach (BoxRoundStatus current in _states.Values)
            {
                if (current == status)
                {
                    return true;
                }
            }

            return false;
        }

        private int CountStatus(BoxRoundStatus status)
        {
            int count = 0;
            foreach (BoxRoundStatus current in _states.Values)
            {
                if (current == status)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public interface IAssistedRouteCostEstimator
    {
        bool TryEstimatePathLength(Vector3 from, Vector3 to, out float length, out string failureReason);
    }

    public sealed class AssistedBoxCandidateEvaluation
    {
        public string BoxId;
        public string Category;
        public string PlaceTargetId;
        public float RobotToPickupCost;
        public float PickupToPlaceCost;
        public float TotalCost;
        public float SelectionCost;
        public AutonomousSelectionPolicy Policy;
        public string SelectionReason;
        public bool Eligible;
        public string IgnoredReason;
        public bool UsedEuclideanFallback;
        public string DiagnosticReason;
        public string PickupApproachCandidateId;
        public string PickupApproachSide;
        public string PickupApproachSource;
        public string PlaceNavigationCandidateId;
        public string PlaceNavigationSideOrCorner;
        public string PlaceNavigationSource;
        public Vector3 DynamicPlacePosePosition;
        public bool UseDynamicPlacePose;
        public Vector3 PlacePointFallbackPosition;
        public string PlaceAreaSource;
        public float PlacePoseInset;
        public string PlaceSlotId;
        public string PlaceSlotQuadrant;
        public int PlaceSlotIndex;
        public int PlaceStackLevel;
        public bool UsedPlaceSlotAllocator;
        public Vector3 PostPlaceEgressPoint;
    }

    public sealed class AssistedBoxSelectionResult
    {
        public AssistedBoxSelectionResult(
            BoxRoundItem selectedBox,
            IReadOnlyList<AssistedBoxCandidateEvaluation> evaluations,
            AutonomousSelectionPolicy policy)
        {
            SelectedBox = selectedBox;
            Evaluations = evaluations ?? Array.Empty<AssistedBoxCandidateEvaluation>();
            Policy = policy;
        }

        public BoxRoundItem SelectedBox { get; }
        public IReadOnlyList<AssistedBoxCandidateEvaluation> Evaluations { get; }
        public AutonomousSelectionPolicy Policy { get; }
        public bool HasSelection => SelectedBox != null;
    }

    public sealed class AssistedBoxSelectionPolicy
    {
        public AssistedBoxSelectionResult SelectNext(
            IEnumerable<BoxRoundItem> candidates,
            BoxRoundState roundState,
            Vector3 robotPosition,
            IAssistedRouteCostEstimator routeCostEstimator,
            AutonomousSelectionPolicy policy = AutonomousSelectionPolicy.LocalPickCost)
        {
            var evaluations = new List<AssistedBoxCandidateEvaluation>();
            BoxRoundItem bestBox = null;
            float bestCost = float.PositiveInfinity;

            if (candidates == null || roundState == null)
            {
                return new AssistedBoxSelectionResult(null, evaluations, policy);
            }

            foreach (BoxRoundItem box in candidates)
            {
                if (!roundState.IsSelectable(box))
                {
                    if (box != null)
                    {
                        evaluations.Add(EvaluateIgnored(box, roundState.GetStatus(box.BoxId), policy));
                    }

                    continue;
                }

                AssistedBoxCandidateEvaluation evaluation = Evaluate(box, robotPosition, routeCostEstimator, policy);
                evaluations.Add(evaluation);

                if (evaluation.SelectionCost < bestCost)
                {
                    bestCost = evaluation.SelectionCost;
                    bestBox = box;
                }
            }

            return new AssistedBoxSelectionResult(bestBox, evaluations, policy);
        }

        private static AssistedBoxCandidateEvaluation Evaluate(
            BoxRoundItem box,
            Vector3 robotPosition,
            IAssistedRouteCostEstimator routeCostEstimator,
            AutonomousSelectionPolicy policy)
        {
            bool usedFallback = false;
            string diagnostic = string.Empty;
            float robotToPickup = EstimateLeg(robotPosition, box.PickupPosition, routeCostEstimator, ref usedFallback, ref diagnostic);
            float pickupToPlace = EstimateLeg(box.PickupPosition, box.PlacePosition, routeCostEstimator, ref usedFallback, ref diagnostic);
            float totalPickPlace = robotToPickup + pickupToPlace;
            float selectionCost = policy == AutonomousSelectionPolicy.LocalPickCost
                ? robotToPickup
                : robotToPickup;

            return new AssistedBoxCandidateEvaluation
            {
                BoxId = box.BoxId,
                Category = box.Category,
                PlaceTargetId = box.PlaceTargetId,
                PickupApproachCandidateId = box.PickupApproachCandidateId,
                PickupApproachSide = box.PickupApproachSide,
                PickupApproachSource = box.PickupApproachSource,
                PlaceNavigationCandidateId = box.PlaceNavigationCandidateId,
                PlaceNavigationSideOrCorner = box.PlaceNavigationSideOrCorner,
                PlaceNavigationSource = box.PlaceNavigationSource,
                DynamicPlacePosePosition = box.DynamicPlacePosePosition,
                UseDynamicPlacePose = box.UseDynamicPlacePose,
                PlacePointFallbackPosition = box.PlacePointFallbackPosition,
                PlaceAreaSource = box.PlaceAreaSource,
                PlacePoseInset = box.PlacePoseInset,
                PlaceSlotId = box.PlaceSlotId,
                PlaceSlotQuadrant = box.PlaceSlotQuadrant,
                PlaceSlotIndex = box.PlaceSlotIndex,
                PlaceStackLevel = box.PlaceStackLevel,
                UsedPlaceSlotAllocator = box.UsedPlaceSlotAllocator,
                PostPlaceEgressPoint = box.PostPlaceEgressPoint,
                RobotToPickupCost = robotToPickup,
                PickupToPlaceCost = pickupToPlace,
                TotalCost = totalPickPlace,
                SelectionCost = selectionCost,
                Policy = policy,
                SelectionReason = policy == AutonomousSelectionPolicy.LocalPickCost
                    ? "lowest_robot_to_pickup_cost"
                    : "lowest_robot_to_pickup_cost",
                Eligible = true,
                IgnoredReason = string.Empty,
                UsedEuclideanFallback = usedFallback,
                DiagnosticReason = diagnostic
            };
        }

        private static AssistedBoxCandidateEvaluation EvaluateIgnored(
            BoxRoundItem box,
            BoxRoundStatus status,
            AutonomousSelectionPolicy policy)
        {
            return new AssistedBoxCandidateEvaluation
            {
                BoxId = box.BoxId,
                Category = box.Category,
                PlaceTargetId = box.PlaceTargetId,
                PickupApproachCandidateId = box.PickupApproachCandidateId,
                PickupApproachSide = box.PickupApproachSide,
                PickupApproachSource = box.PickupApproachSource,
                PlaceNavigationCandidateId = box.PlaceNavigationCandidateId,
                PlaceNavigationSideOrCorner = box.PlaceNavigationSideOrCorner,
                PlaceNavigationSource = box.PlaceNavigationSource,
                DynamicPlacePosePosition = box.DynamicPlacePosePosition,
                UseDynamicPlacePose = box.UseDynamicPlacePose,
                PlacePointFallbackPosition = box.PlacePointFallbackPosition,
                PlaceAreaSource = box.PlaceAreaSource,
                PlacePoseInset = box.PlacePoseInset,
                PlaceSlotId = box.PlaceSlotId,
                PlaceSlotQuadrant = box.PlaceSlotQuadrant,
                PlaceSlotIndex = box.PlaceSlotIndex,
                PlaceStackLevel = box.PlaceStackLevel,
                UsedPlaceSlotAllocator = box.UsedPlaceSlotAllocator,
                PostPlaceEgressPoint = box.PostPlaceEgressPoint,
                RobotToPickupCost = -1f,
                PickupToPlaceCost = -1f,
                TotalCost = -1f,
                SelectionCost = -1f,
                Policy = policy,
                SelectionReason = string.Empty,
                Eligible = false,
                IgnoredReason = IgnoredReasonForStatus(status),
                UsedEuclideanFallback = false,
                DiagnosticReason = status.ToString()
            };
        }

        private static string IgnoredReasonForStatus(BoxRoundStatus status)
        {
            return status switch
            {
                BoxRoundStatus.Assigned => "assigned",
                BoxRoundStatus.Completed => "deposited",
                BoxRoundStatus.Excluded => "unavailable",
                _ => string.Empty
            };
        }

        private static float EstimateLeg(
            Vector3 from,
            Vector3 to,
            IAssistedRouteCostEstimator routeCostEstimator,
            ref bool usedFallback,
            ref string diagnostic)
        {
            string failureReason = string.Empty;
            if (routeCostEstimator != null &&
                routeCostEstimator.TryEstimatePathLength(from, to, out float length, out failureReason))
            {
                return length;
            }

            usedFallback = true;
            if (string.IsNullOrWhiteSpace(diagnostic))
            {
                diagnostic = string.IsNullOrWhiteSpace(failureReason)
                    ? "navmesh_cost_unavailable_euclidean_fallback"
                    : failureReason;
            }

            return Vector3.Distance(from, to);
        }
    }
}
