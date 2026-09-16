using System;
using System.Collections.Generic;
using Autonomy.Perception;
using UnityEngine;
using UnityEngine.AI;

namespace Autonomy.UnityIntegration
{
    public sealed class PickupApproachCandidate
    {
        public string BoxId;
        public string CandidateId;
        public string Side;
        public Vector3 Position;
        public float RobotToCandidateCost;
        public float CandidateToPlaceCost;
        public float TotalCost;
        public float DistanceToBox;
        public bool WithinPickRange;
        public bool NavMeshValid;
        public bool PathComplete;
        public bool UsedEuclideanFallback;
        public string RejectionReason;
        public string Source;

        public bool IsSelectable => string.IsNullOrWhiteSpace(RejectionReason);
    }

    public readonly struct PickupApproachSettings
    {
        public PickupApproachSettings(
            bool useDynamicPickupApproach,
            bool useCornerPickupApproachCandidates,
            float approachStandoff,
            float maxExpectedPickDistance,
            float pickDistanceSafetyMargin,
            float navMeshSampleRadius)
        {
            UseDynamicPickupApproach = useDynamicPickupApproach;
            UseCornerPickupApproachCandidates = useCornerPickupApproachCandidates;
            ApproachStandoff = Mathf.Max(0.05f, approachStandoff);
            MaxExpectedPickDistance = Mathf.Max(0.05f, maxExpectedPickDistance);
            PickDistanceSafetyMargin = Mathf.Max(0f, pickDistanceSafetyMargin);
            NavMeshSampleRadius = Mathf.Max(0.05f, navMeshSampleRadius);
        }

        public bool UseDynamicPickupApproach { get; }
        public bool UseCornerPickupApproachCandidates { get; }
        public float ApproachStandoff { get; }
        public float MaxExpectedPickDistance { get; }
        public float PickDistanceSafetyMargin { get; }
        public float NavMeshSampleRadius { get; }
        public float EffectivePickDistance => Mathf.Max(0.05f, MaxExpectedPickDistance - PickDistanceSafetyMargin);
    }

    public static class PickupApproachSelector
    {
        private static readonly (string Side, Vector3 Direction)[] Sides =
        {
            ("East", Vector3.right),
            ("West", Vector3.left),
            ("North", Vector3.forward),
            ("South", Vector3.back)
        };

        private static readonly (string Side, Vector3 Direction)[] Corners =
        {
            ("NorthEast", new Vector3(1f, 0f, 1f)),
            ("NorthWest", new Vector3(-1f, 0f, 1f)),
            ("SouthEast", new Vector3(1f, 0f, -1f)),
            ("SouthWest", new Vector3(-1f, 0f, -1f))
        };

        public static IReadOnlyList<PickupApproachCandidate> EvaluateCandidates(
            PerceivedObject selected,
            Vector3 robotPosition,
            Vector3 placeNavigationPosition,
            PickupApproachSettings settings,
            IReadOnlyCollection<string> rejectedCandidateIds = null)
        {
            var results = new List<PickupApproachCandidate>();
            if (selected == null || selected.Transform == null || !settings.UseDynamicPickupApproach)
            {
                return results;
            }

            Bounds footprint = ResolveFootprintBounds(selected);
            Vector3 boxPosition = selected.Transform.position;
            var rejected = new HashSet<string>(rejectedCandidateIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            foreach ((string side, Vector3 direction) in EnumerateDirections(settings.UseCornerPickupApproachCandidates))
            {
                Vector3 candidatePosition = BuildCandidatePosition(footprint, direction, settings.ApproachStandoff);
                string candidateId = $"{selected.ObjectId}:{side}";
                var candidate = new PickupApproachCandidate
                {
                    BoxId = selected.ObjectId,
                    CandidateId = candidateId,
                    Side = side,
                    Position = candidatePosition,
                    Source = side.Contains("East") && side.Contains("North") ||
                             side.Contains("West") && side.Contains("North") ||
                             side.Contains("East") && side.Contains("South") ||
                             side.Contains("West") && side.Contains("South")
                        ? "DynamicBoundsCorner"
                        : "DynamicBoundsSide"
                };

                candidate.DistanceToBox = Vector3.Distance(candidatePosition, boxPosition);
                candidate.WithinPickRange = candidate.DistanceToBox <= settings.EffectivePickDistance;
                candidate.NavMeshValid = NavMesh.SamplePosition(candidatePosition, out NavMeshHit hit, settings.NavMeshSampleRadius, NavMesh.AllAreas);
                Vector3 sampledPosition = candidate.NavMeshValid ? hit.position : candidatePosition;
                candidate.Position = sampledPosition;
                candidate.RobotToCandidateCost = EstimateLeg(robotPosition, sampledPosition, settings, out bool robotFallback, out bool robotComplete);
                candidate.CandidateToPlaceCost = EstimateLeg(sampledPosition, placeNavigationPosition, settings, out bool placeFallback, out bool placeComplete);
                candidate.UsedEuclideanFallback = robotFallback || placeFallback;
                candidate.PathComplete = robotComplete && placeComplete;
                candidate.TotalCost = candidate.RobotToCandidateCost + candidate.CandidateToPlaceCost;

                if (rejected.Contains(candidate.CandidateId))
                {
                    candidate.RejectionReason = "candidate_previously_failed";
                }
                else if (!candidate.NavMeshValid)
                {
                    candidate.RejectionReason = "navmesh_sample_failed";
                }
                else if (!candidate.PathComplete)
                {
                    candidate.RejectionReason = "navmesh_path_incomplete";
                }
                else if (!candidate.WithinPickRange)
                {
                    candidate.RejectionReason = "outside_expected_pick_range";
                }

                results.Add(candidate);
            }

            return results;
        }

        private static IEnumerable<(string Side, Vector3 Direction)> EnumerateDirections(bool includeCorners)
        {
            foreach ((string side, Vector3 direction) in Sides)
            {
                yield return (side, direction);
            }

            if (!includeCorners)
            {
                yield break;
            }

            foreach ((string side, Vector3 direction) in Corners)
            {
                yield return (side, direction);
            }
        }

        public static bool TrySelectBest(
            IReadOnlyList<PickupApproachCandidate> candidates,
            out PickupApproachCandidate selected)
        {
            selected = null;
            if (candidates == null)
            {
                return false;
            }

            foreach (PickupApproachCandidate candidate in candidates)
            {
                if (candidate == null || !candidate.IsSelectable)
                {
                    continue;
                }

                if (selected == null || candidate.TotalCost < selected.TotalCost)
                {
                    selected = candidate;
                }
            }

            return selected != null;
        }

        public static Dictionary<string, object> ToPayload(PickupApproachCandidate candidate)
        {
            return new Dictionary<string, object>
            {
                ["box_id"] = candidate != null ? candidate.BoxId : string.Empty,
                ["candidate_id"] = candidate != null ? candidate.CandidateId : string.Empty,
                ["side"] = candidate != null ? candidate.Side : string.Empty,
                ["side_or_corner"] = candidate != null ? candidate.Side : string.Empty,
                ["candidate_position"] = candidate != null ? candidate.Position : Vector3.zero,
                ["robot_to_candidate_cost"] = candidate != null ? candidate.RobotToCandidateCost : 0f,
                ["candidate_to_place_cost"] = candidate != null ? candidate.CandidateToPlaceCost : 0f,
                ["total_cost"] = candidate != null ? candidate.TotalCost : 0f,
                ["distance_to_box"] = candidate != null ? candidate.DistanceToBox : 0f,
                ["within_pick_range"] = candidate != null && candidate.WithinPickRange,
                ["navmesh_valid"] = candidate != null && candidate.NavMeshValid,
                ["path_complete"] = candidate != null && candidate.PathComplete,
                ["used_euclidean_fallback"] = candidate != null && candidate.UsedEuclideanFallback,
                ["rejection_reason"] = candidate != null ? candidate.RejectionReason ?? string.Empty : string.Empty,
                ["source"] = candidate != null ? candidate.Source ?? string.Empty : string.Empty,
                ["candidate_kind"] = candidate != null && string.Equals(candidate.Source, "DynamicBoundsCorner", StringComparison.Ordinal)
                    ? "corner"
                    : "side"
            };
        }

        private static Bounds ResolveFootprintBounds(PerceivedObject selected)
        {
            NavMeshObstacle obstacle = selected.Transform.GetComponentInChildren<NavMeshObstacle>();
            if (obstacle != null)
            {
                Vector3 center = obstacle.transform.TransformPoint(obstacle.center);
                Vector3 size = obstacle.size;
                var bounds = new Bounds(center, size);
                bounds.center = new Vector3(bounds.center.x, 0f, bounds.center.z);
                return bounds;
            }

            Collider collider = selected.Transform.GetComponentInChildren<Collider>();
            if (collider != null)
            {
                Bounds bounds = collider.bounds;
                bounds.center = new Vector3(bounds.center.x, 0f, bounds.center.z);
                return bounds;
            }

            Renderer renderer = selected.Transform.GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                Bounds bounds = renderer.bounds;
                bounds.center = new Vector3(bounds.center.x, 0f, bounds.center.z);
                return bounds;
            }

            return new Bounds(
                new Vector3(selected.BoundsCenter.x, 0f, selected.BoundsCenter.z),
                new Vector3(Mathf.Max(0.2f, selected.BoundsExtents.x * 2f), 0.1f, Mathf.Max(0.2f, selected.BoundsExtents.z * 2f)));
        }

        private static Vector3 BuildCandidatePosition(Bounds footprint, Vector3 direction, float standoff)
        {
            Vector3 center = footprint.center;
            float x = center.x;
            float z = center.z;

            if (Mathf.Abs(direction.x) > 0.5f)
            {
                x += Mathf.Sign(direction.x) * (footprint.extents.x + standoff);
            }

            if (Mathf.Abs(direction.z) > 0.5f)
            {
                z += Mathf.Sign(direction.z) * (footprint.extents.z + standoff);
            }

            return new Vector3(x, 0f, z);
        }

        private static float EstimateLeg(
            Vector3 from,
            Vector3 to,
            PickupApproachSettings settings,
            out bool usedFallback,
            out bool pathComplete)
        {
            usedFallback = false;
            pathComplete = false;

            if (!NavMesh.SamplePosition(from, out NavMeshHit fromHit, settings.NavMeshSampleRadius, NavMesh.AllAreas) ||
                !NavMesh.SamplePosition(to, out NavMeshHit toHit, settings.NavMeshSampleRadius, NavMesh.AllAreas))
            {
                usedFallback = true;
                return Vector3.Distance(from, to);
            }

            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(fromHit.position, toHit.position, NavMesh.AllAreas, path) ||
                path.status != NavMeshPathStatus.PathComplete ||
                path.corners == null ||
                path.corners.Length == 0)
            {
                usedFallback = true;
                return Vector3.Distance(fromHit.position, toHit.position);
            }

            pathComplete = true;
            float length = 0f;
            Vector3 previous = path.corners[0];
            for (int i = 1; i < path.corners.Length; i++)
            {
                length += Vector3.Distance(previous, path.corners[i]);
                previous = path.corners[i];
            }

            return length;
        }
    }

    public sealed class PlaceNavigationCandidate
    {
        public string PlaceTargetId;
        public string CandidateId;
        public string SideOrCorner;
        public Vector3 Position;
        public Vector3 DynamicPlacePosePosition;
        public Vector3 PlacePointFallbackPosition;
        public float PickupToCandidateCost;
        public float TotalCost;
        public float DistanceCandidateToPlaceTransform;
        public float DistanceCandidateToDynamicPlacePose;
        public float EstimatedAnchorToPlaceDistance;
        public float EffectivePlaceRange;
        public float StrictEffectivePlaceRange;
        public float RelaxedEffectivePlaceRange;
        public bool WithinPlaceRange;
        public bool WithinStrictPlaceRange;
        public bool WithinRelaxedPlaceRange;
        public bool RequiresRelaxedPlace;
        public bool NavMeshValid;
        public bool PathComplete;
        public bool UsedEuclideanFallback;
        public bool UsedDynamicPlacePose;
        public bool UsedPlacePointFallback;
        public float PlacePoseInset;
        public float MinPlacePoseClearance;
        public string SlotId;
        public string SlotQuadrant;
        public int SlotIndex;
        public int StackLevel;
        public bool UsedPlaceSlotAllocator;
        public Vector3 PostPlaceEgressPoint;
        public bool PostPlaceEgressPathComplete;
        public bool PostPlaceClearancePassed;
        public float PostPlaceEgressDistance;
        public string PostPlaceEgressPathStatus;
        public string PostPlaceBlockingObstacle;
        public int PostPlaceBlockingObstacleLayer = -1;
        public bool PostPlaceBlockingObstacleIsFloorOrOverlay;
        public int PostPlaceObstacleLayerMask;
        public string RejectionReason;
        public string Source;
        public string AreaSource;

        public bool IsSelectable => string.IsNullOrWhiteSpace(RejectionReason);
    }

    public readonly struct PlaceNavigationSettings
    {
        public PlaceNavigationSettings(
            bool useDynamicPlaceNavigationCandidates,
            bool useCornerPlaceNavigationCandidates,
            float placeNavigationStandoff,
            float maxExpectedPlaceDistance,
            float placeDistanceSafetyMargin,
            float placeReachabilityMargin,
            float placeCandidateArrivalTolerance,
            bool useDynamicPlacePose,
            float placePoseInset,
            float minPlacePoseClearance,
            float navMeshSampleRadius,
            float maxRelaxedPlaceDistance = 1.45f,
            Vector3? fixedDynamicPlacePosePosition = null,
            string fixedSlotId = "",
            string fixedSlotQuadrant = "",
            int fixedSlotIndex = -1,
            int fixedStackLevel = 0,
            bool usePlaceSlotAllocator = false,
            bool usePostPlaceEgressValidation = false,
            float postPlaceEgressDistance = 0.60f,
            float postPlaceEgressSampleRadius = 0.75f,
            float postPlaceEgressClearanceRadius = 0.25f,
            float postPlaceEgressSafetyMargin = 0.15f,
            bool requirePostPlaceEgressPath = true,
            LayerMask postPlaceEgressObstacleLayerMask = default)
        {
            UseDynamicPlaceNavigationCandidates = useDynamicPlaceNavigationCandidates;
            UseCornerPlaceNavigationCandidates = useCornerPlaceNavigationCandidates;
            PlaceNavigationStandoff = Mathf.Max(0.05f, placeNavigationStandoff);
            MaxExpectedPlaceDistance = Mathf.Max(0.05f, maxExpectedPlaceDistance);
            PlaceDistanceSafetyMargin = Mathf.Max(0f, placeDistanceSafetyMargin);
            PlaceReachabilityMargin = Mathf.Max(0f, placeReachabilityMargin);
            PlaceCandidateArrivalTolerance = Mathf.Max(0f, placeCandidateArrivalTolerance);
            UseDynamicPlacePose = useDynamicPlacePose;
            PlacePoseInset = Mathf.Max(0f, placePoseInset);
            MinPlacePoseClearance = Mathf.Max(0f, minPlacePoseClearance);
            NavMeshSampleRadius = Mathf.Max(0.05f, navMeshSampleRadius);
            MaxRelaxedPlaceDistance = Mathf.Max(MaxExpectedPlaceDistance, maxRelaxedPlaceDistance);
            FixedDynamicPlacePosePosition = fixedDynamicPlacePosePosition;
            FixedSlotId = fixedSlotId ?? string.Empty;
            FixedSlotQuadrant = fixedSlotQuadrant ?? string.Empty;
            FixedSlotIndex = fixedSlotIndex;
            FixedStackLevel = Mathf.Max(0, fixedStackLevel);
            UsePlaceSlotAllocator = usePlaceSlotAllocator;
            UsePostPlaceEgressValidation = usePostPlaceEgressValidation;
            PostPlaceEgressDistance = Mathf.Max(0.05f, postPlaceEgressDistance);
            PostPlaceEgressSampleRadius = Mathf.Max(0.05f, postPlaceEgressSampleRadius);
            PostPlaceEgressClearanceRadius = Mathf.Max(0.01f, postPlaceEgressClearanceRadius);
            PostPlaceEgressSafetyMargin = Mathf.Max(0f, postPlaceEgressSafetyMargin);
            RequirePostPlaceEgressPath = requirePostPlaceEgressPath;
            PostPlaceEgressObstacleLayerMask = postPlaceEgressObstacleLayerMask.value == 0 ? ~0 : postPlaceEgressObstacleLayerMask;
        }

        public bool UseDynamicPlaceNavigationCandidates { get; }
        public bool UseCornerPlaceNavigationCandidates { get; }
        public float PlaceNavigationStandoff { get; }
        public float MaxExpectedPlaceDistance { get; }
        public float PlaceDistanceSafetyMargin { get; }
        public float PlaceReachabilityMargin { get; }
        public float PlaceCandidateArrivalTolerance { get; }
        public bool UseDynamicPlacePose { get; }
        public float PlacePoseInset { get; }
        public float MinPlacePoseClearance { get; }
        public float NavMeshSampleRadius { get; }
        public float MaxRelaxedPlaceDistance { get; }
        public Vector3? FixedDynamicPlacePosePosition { get; }
        public string FixedSlotId { get; }
        public string FixedSlotQuadrant { get; }
        public int FixedSlotIndex { get; }
        public int FixedStackLevel { get; }
        public bool UsePlaceSlotAllocator { get; }
        public bool UsePostPlaceEgressValidation { get; }
        public float PostPlaceEgressDistance { get; }
        public float PostPlaceEgressSampleRadius { get; }
        public float PostPlaceEgressClearanceRadius { get; }
        public float PostPlaceEgressSafetyMargin { get; }
        public bool RequirePostPlaceEgressPath { get; }
        public LayerMask PostPlaceEgressObstacleLayerMask { get; }
        public float EffectivePlaceDistance => Mathf.Max(0.05f, MaxExpectedPlaceDistance - PlaceDistanceSafetyMargin);
        public float RelaxedEffectivePlaceDistance => Mathf.Max(EffectivePlaceDistance, MaxRelaxedPlaceDistance - PlaceDistanceSafetyMargin);
    }

    public static class PlaceNavigationCandidateSelector
    {
        private static readonly (string Side, Vector3 Direction)[] Sides =
        {
            ("East", Vector3.right),
            ("West", Vector3.left),
            ("North", Vector3.forward),
            ("South", Vector3.back)
        };

        private static readonly (string Side, Vector3 Direction)[] Corners =
        {
            ("NorthEast", new Vector3(1f, 0f, 1f)),
            ("NorthWest", new Vector3(-1f, 0f, 1f)),
            ("SouthEast", new Vector3(1f, 0f, -1f)),
            ("SouthWest", new Vector3(-1f, 0f, -1f))
        };

        public static IReadOnlyList<PlaceNavigationCandidate> EvaluateCandidates(
            string placeTargetId,
            Transform placeTransform,
            Transform navigationTransform,
            Vector3 fallbackNavigationPosition,
            Vector3 pickupApproachPosition,
            PlaceNavigationSettings settings)
        {
            var results = new List<PlaceNavigationCandidate>();
            if (!settings.UseDynamicPlaceNavigationCandidates)
            {
                return results;
            }

            Bounds footprint = ResolveFootprintBounds(placeTransform, navigationTransform, fallbackNavigationPosition, out string areaSource);
            Vector3 placeTransformPosition = placeTransform != null ? placeTransform.position : fallbackNavigationPosition;
            foreach ((string side, Vector3 direction) in EnumerateDirections(settings.UseCornerPlaceNavigationCandidates))
            {
                Vector3 candidatePosition = settings.FixedDynamicPlacePosePosition.HasValue
                    ? BuildCandidatePositionAroundPose(settings.FixedDynamicPlacePosePosition.Value, direction, settings.PlaceNavigationStandoff)
                    : BuildCandidatePosition(footprint, direction, settings.PlaceNavigationStandoff);
                string candidateId = $"{placeTargetId}:{side}";
                bool isCorner = Mathf.Abs(direction.x) > 0.5f && Mathf.Abs(direction.z) > 0.5f;
                var candidate = new PlaceNavigationCandidate
                {
                    PlaceTargetId = placeTargetId ?? string.Empty,
                    CandidateId = candidateId,
                    SideOrCorner = side,
                    Position = candidatePosition,
                    Source = isCorner ? "DynamicPlaceCorner" : "DynamicPlaceSide",
                    AreaSource = areaSource,
                    PlacePointFallbackPosition = placeTransformPosition,
                    UsedDynamicPlacePose = (settings.UseDynamicPlacePose && areaSource != "fallback_navigation_point") ||
                        settings.FixedDynamicPlacePosePosition.HasValue,
                    UsedPlacePointFallback = !settings.UseDynamicPlacePose && !settings.FixedDynamicPlacePosePosition.HasValue,
                    PlacePoseInset = settings.PlacePoseInset,
                    MinPlacePoseClearance = settings.MinPlacePoseClearance,
                    SlotId = settings.FixedSlotId,
                    SlotQuadrant = settings.FixedSlotQuadrant,
                    SlotIndex = settings.FixedSlotIndex,
                    StackLevel = settings.FixedStackLevel,
                    UsedPlaceSlotAllocator = settings.UsePlaceSlotAllocator
                };

                candidate.DynamicPlacePosePosition = settings.FixedDynamicPlacePosePosition.HasValue
                    ? settings.FixedDynamicPlacePosePosition.Value
                    : candidate.UsedDynamicPlacePose
                    ? WithY(BuildDynamicPlacePosePosition(footprint, direction, settings.PlacePoseInset, settings.MinPlacePoseClearance), placeTransformPosition.y)
                    : placeTransformPosition;
                candidate.NavMeshValid = NavMesh.SamplePosition(candidatePosition, out NavMeshHit hit, settings.NavMeshSampleRadius, NavMesh.AllAreas);
                Vector3 sampledPosition = candidate.NavMeshValid ? hit.position : candidatePosition;
                candidate.Position = sampledPosition;
                candidate.DistanceCandidateToPlaceTransform = Vector3.Distance(sampledPosition, placeTransformPosition);
                candidate.DistanceCandidateToDynamicPlacePose = Vector3.Distance(sampledPosition, candidate.DynamicPlacePosePosition);
                candidate.EstimatedAnchorToPlaceDistance = candidate.DistanceCandidateToDynamicPlacePose +
                    settings.PlaceReachabilityMargin +
                    settings.PlaceCandidateArrivalTolerance;
                candidate.EffectivePlaceRange = settings.EffectivePlaceDistance;
                candidate.StrictEffectivePlaceRange = settings.EffectivePlaceDistance;
                candidate.RelaxedEffectivePlaceRange = settings.RelaxedEffectivePlaceDistance;
                candidate.WithinStrictPlaceRange = candidate.EstimatedAnchorToPlaceDistance <= settings.EffectivePlaceDistance;
                candidate.WithinRelaxedPlaceRange = candidate.EstimatedAnchorToPlaceDistance <= settings.RelaxedEffectivePlaceDistance;
                candidate.RequiresRelaxedPlace = !candidate.WithinStrictPlaceRange && candidate.WithinRelaxedPlaceRange;
                candidate.WithinPlaceRange = candidate.WithinRelaxedPlaceRange;
                candidate.PickupToCandidateCost = EstimateLeg(pickupApproachPosition, sampledPosition, settings, out bool usedFallback, out bool pathComplete);
                candidate.TotalCost = candidate.PickupToCandidateCost;
                candidate.UsedEuclideanFallback = usedFallback;
                candidate.PathComplete = pathComplete;
                EvaluatePostPlaceEgress(candidate, settings);

                if (!candidate.NavMeshValid)
                {
                    candidate.RejectionReason = "navmesh_sample_failed";
                }
                else if (!candidate.PathComplete)
                {
                    candidate.RejectionReason = "navmesh_path_incomplete";
                }
                else if (!candidate.WithinRelaxedPlaceRange)
                {
                    candidate.RejectionReason = "estimated_anchor_out_of_place_range";
                }
                else if (settings.UsePostPlaceEgressValidation && !candidate.PostPlaceEgressPathComplete && settings.RequirePostPlaceEgressPath)
                {
                    candidate.RejectionReason = "egress_path_incomplete";
                }
                else if (settings.UsePostPlaceEgressValidation && !candidate.PostPlaceClearancePassed)
                {
                    candidate.RejectionReason = "post_place_clearance_failed";
                }

                results.Add(candidate);
            }

            return results;
        }

        public static bool TrySelectBest(IReadOnlyList<PlaceNavigationCandidate> candidates, out PlaceNavigationCandidate selected)
        {
            selected = null;
            if (candidates == null)
            {
                return false;
            }

            foreach (PlaceNavigationCandidate candidate in candidates)
            {
                if (candidate == null || !candidate.IsSelectable)
                {
                    continue;
                }

                if (selected == null ||
                    (!candidate.RequiresRelaxedPlace && selected.RequiresRelaxedPlace) ||
                    (candidate.RequiresRelaxedPlace == selected.RequiresRelaxedPlace && candidate.TotalCost < selected.TotalCost))
                {
                    selected = candidate;
                }
            }

            return selected != null;
        }

        public static Dictionary<string, object> ToPayload(PlaceNavigationCandidate candidate)
        {
            return new Dictionary<string, object>
            {
                ["place_target_id"] = candidate != null ? candidate.PlaceTargetId : string.Empty,
                ["candidate_id"] = candidate != null ? candidate.CandidateId : string.Empty,
                ["side_or_corner"] = candidate != null ? candidate.SideOrCorner : string.Empty,
                ["candidate_position"] = candidate != null ? candidate.Position : Vector3.zero,
                ["place_navigation_candidate_position"] = candidate != null ? candidate.Position : Vector3.zero,
                ["dynamic_place_pose_position"] = candidate != null ? candidate.DynamicPlacePosePosition : Vector3.zero,
                ["place_point_fallback_position"] = candidate != null ? candidate.PlacePointFallbackPosition : Vector3.zero,
                ["pickup_to_candidate_cost"] = candidate != null ? candidate.PickupToCandidateCost : 0f,
                ["candidate_to_place_cost"] = candidate != null ? candidate.DistanceCandidateToPlaceTransform : 0f,
                ["total_cost"] = candidate != null ? candidate.TotalCost : 0f,
                ["distance_candidate_to_place_transform"] = candidate != null ? candidate.DistanceCandidateToPlaceTransform : 0f,
                ["distance_candidate_to_dynamic_place_pose"] = candidate != null ? candidate.DistanceCandidateToDynamicPlacePose : 0f,
                ["estimated_anchor_to_place_distance"] = candidate != null ? candidate.EstimatedAnchorToPlaceDistance : 0f,
                ["estimated_anchor_to_dynamic_place_pose_distance"] = candidate != null ? candidate.EstimatedAnchorToPlaceDistance : 0f,
                ["effective_place_range"] = candidate != null ? candidate.EffectivePlaceRange : 0f,
                ["strict_effective_place_range"] = candidate != null ? candidate.StrictEffectivePlaceRange : 0f,
                ["relaxed_effective_place_range"] = candidate != null ? candidate.RelaxedEffectivePlaceRange : 0f,
                ["requires_relaxed_place"] = candidate != null && candidate.RequiresRelaxedPlace,
                ["within_strict_place_range"] = candidate != null && candidate.WithinStrictPlaceRange,
                ["within_relaxed_place_range"] = candidate != null && candidate.WithinRelaxedPlaceRange,
                ["candidate_heading"] = candidate != null ? candidate.SideOrCorner : string.Empty,
                ["estimated_anchor_position"] = candidate != null ? candidate.Position : Vector3.zero,
                ["within_place_range"] = candidate != null && candidate.WithinPlaceRange,
                ["within_anchor_place_range"] = candidate != null && candidate.WithinPlaceRange,
                ["navmesh_valid"] = candidate != null && candidate.NavMeshValid,
                ["path_complete"] = candidate != null && candidate.PathComplete,
                ["used_euclidean_fallback"] = candidate != null && candidate.UsedEuclideanFallback,
                ["rejection_reason"] = candidate != null ? candidate.RejectionReason ?? string.Empty : string.Empty,
                ["source"] = candidate != null ? candidate.Source ?? string.Empty : string.Empty,
                ["area_source"] = candidate != null ? candidate.AreaSource ?? string.Empty : string.Empty,
                ["place_area_source"] = candidate != null ? candidate.AreaSource ?? string.Empty : string.Empty,
                ["used_dynamic_place_pose"] = candidate != null && candidate.UsedDynamicPlacePose,
                ["used_place_point_fallback"] = candidate != null && candidate.UsedPlacePointFallback,
                ["place_pose_inset"] = candidate != null ? candidate.PlacePoseInset : 0f,
                ["min_place_pose_clearance"] = candidate != null ? candidate.MinPlacePoseClearance : 0f,
                ["actual_anchor_to_dynamic_place_pose_distance"] = candidate != null ? candidate.EstimatedAnchorToPlaceDistance : 0f,
                ["slot_id"] = candidate != null ? candidate.SlotId ?? string.Empty : string.Empty,
                ["quadrant"] = candidate != null ? candidate.SlotQuadrant ?? string.Empty : string.Empty,
                ["slot_index"] = candidate != null ? candidate.SlotIndex : -1,
                ["stack_level"] = candidate != null ? candidate.StackLevel : 0,
                ["slot_position"] = candidate != null ? candidate.DynamicPlacePosePosition : Vector3.zero,
                ["used_place_slot_allocator"] = candidate != null && candidate.UsedPlaceSlotAllocator,
                ["post_place_egress_point"] = candidate != null ? candidate.PostPlaceEgressPoint : Vector3.zero,
                ["egress_path_complete"] = candidate != null && candidate.PostPlaceEgressPathComplete,
                ["post_place_clearance_passed"] = candidate != null && candidate.PostPlaceClearancePassed,
                ["egress_path_status"] = candidate != null ? candidate.PostPlaceEgressPathStatus ?? string.Empty : string.Empty,
                ["egress_distance"] = candidate != null ? candidate.PostPlaceEgressDistance : 0f,
                ["clearance_radius"] = candidate != null ? candidate.MinPlacePoseClearance : 0f,
                ["blocking_obstacle"] = candidate != null ? candidate.PostPlaceBlockingObstacle ?? string.Empty : string.Empty,
                ["blocking_obstacle_layer"] = candidate != null ? candidate.PostPlaceBlockingObstacleLayer : -1,
                ["blocking_obstacle_is_floor_or_overlay"] = candidate != null && candidate.PostPlaceBlockingObstacleIsFloorOrOverlay,
                ["obstacle_layer_mask"] = candidate != null ? candidate.PostPlaceObstacleLayerMask : 0,
                ["candidate_kind"] = candidate != null && candidate.Source != null && candidate.Source.Contains("Corner") ? "corner" : "side"
            };
        }

        private static void EvaluatePostPlaceEgress(PlaceNavigationCandidate candidate, PlaceNavigationSettings settings)
        {
            if (candidate == null || !settings.UsePostPlaceEgressValidation)
            {
                if (candidate != null)
                {
                    candidate.PostPlaceEgressPoint = candidate.Position;
                    candidate.PostPlaceEgressPathComplete = true;
                    candidate.PostPlaceClearancePassed = true;
                    candidate.PostPlaceEgressPathStatus = "disabled";
                }

                return;
            }

            Vector3 away = candidate.Position - candidate.DynamicPlacePosePosition;
            away.y = 0f;
            if (away.sqrMagnitude < 0.0001f)
            {
                away = candidate.Position.sqrMagnitude > 0.0001f ? candidate.Position.normalized : Vector3.back;
            }

            away.Normalize();
            Vector3 requestedEgress = candidate.Position + away * settings.PostPlaceEgressDistance;
            candidate.PostPlaceEgressDistance = settings.PostPlaceEgressDistance;
            if (!NavMesh.SamplePosition(requestedEgress, out NavMeshHit egressHit, settings.PostPlaceEgressSampleRadius, NavMesh.AllAreas))
            {
                candidate.PostPlaceEgressPoint = requestedEgress;
                candidate.PostPlaceEgressPathComplete = false;
                candidate.PostPlaceClearancePassed = false;
                candidate.PostPlaceEgressPathStatus = "egress_sample_failed";
                return;
            }

            candidate.PostPlaceEgressPoint = egressHit.position;
            candidate.PostPlaceObstacleLayerMask = settings.PostPlaceEgressObstacleLayerMask.value;
            var path = new NavMeshPath();
            candidate.PostPlaceEgressPathComplete =
                NavMesh.CalculatePath(candidate.Position, egressHit.position, NavMesh.AllAreas, path) &&
                path.status == NavMeshPathStatus.PathComplete;
            candidate.PostPlaceEgressPathStatus = candidate.PostPlaceEgressPathComplete ? "PathComplete" : path.status.ToString();
            float distanceToSlot = Vector3.Distance(candidate.PostPlaceEgressPoint, candidate.DynamicPlacePosePosition);
            candidate.PostPlaceClearancePassed = distanceToSlot >= settings.PostPlaceEgressClearanceRadius + settings.PostPlaceEgressSafetyMargin;
            Collider[] overlaps = Physics.OverlapSphere(
                candidate.PostPlaceEgressPoint,
                settings.PostPlaceEgressClearanceRadius,
                settings.PostPlaceEgressObstacleLayerMask,
                QueryTriggerInteraction.Ignore);
            foreach (Collider overlap in overlaps)
            {
                if (IsIgnoredFloorOrOverlay(overlap))
                {
                    continue;
                }

                candidate.PostPlaceClearancePassed = false;
                candidate.PostPlaceBlockingObstacle = GetTransformPath(overlap.transform);
                candidate.PostPlaceBlockingObstacleLayer = overlap.gameObject.layer;
                candidate.PostPlaceBlockingObstacleIsFloorOrOverlay = IsIgnoredFloorOrOverlay(overlap);
                break;
            }
        }

        private static bool IsIgnoredFloorOrOverlay(Collider collider)
        {
            if (collider == null)
            {
                return true;
            }

            string path = GetTransformPath(collider.transform).ToLowerInvariant();
            return path.Contains("floor") ||
                   path.Contains("ground") ||
                   path.Contains("overlay") ||
                   path.Contains("walkable") ||
                   collider.isTrigger;
        }

        private static IEnumerable<(string Side, Vector3 Direction)> EnumerateDirections(bool includeCorners)
        {
            foreach ((string side, Vector3 direction) in Sides)
            {
                yield return (side, direction);
            }

            if (!includeCorners)
            {
                yield break;
            }

            foreach ((string side, Vector3 direction) in Corners)
            {
                yield return (side, direction);
            }
        }

        private static Bounds ResolveFootprintBounds(
            Transform placeTransform,
            Transform navigationTransform,
            Vector3 fallbackNavigationPosition,
            out string areaSource)
        {
            Transform areaRoot = ResolveAreaRoot(placeTransform, navigationTransform);
            if (areaRoot != null)
            {
                Collider ownCollider = areaRoot.GetComponent<Collider>();
                if (ownCollider != null)
                {
                    Bounds bounds = ownCollider.bounds;
                    bounds.center = new Vector3(bounds.center.x, 0f, bounds.center.z);
                    areaSource = "deposit_zone_collider";
                    return bounds;
                }

                NavMeshObstacle obstacle = areaRoot.GetComponentInChildren<NavMeshObstacle>();
                if (obstacle != null)
                {
                    Vector3 center = obstacle.transform.TransformPoint(obstacle.center);
                    areaSource = "navmesh_obstacle";
                    return new Bounds(new Vector3(center.x, 0f, center.z), obstacle.size);
                }

                Collider collider = areaRoot.GetComponentInChildren<Collider>();
                if (collider != null)
                {
                    Bounds bounds = collider.bounds;
                    bounds.center = new Vector3(bounds.center.x, 0f, bounds.center.z);
                    areaSource = "parent_bounds_collider";
                    return bounds;
                }

                Renderer renderer = areaRoot.GetComponentInChildren<Renderer>();
                if (renderer != null)
                {
                    Bounds bounds = renderer.bounds;
                    bounds.center = new Vector3(bounds.center.x, 0f, bounds.center.z);
                    areaSource = "parent_bounds_renderer";
                    return bounds;
                }
            }

            areaSource = "fallback_navigation_point";
            return new Bounds(new Vector3(fallbackNavigationPosition.x, 0f, fallbackNavigationPosition.z), new Vector3(0.6f, 0.1f, 0.6f));
        }

        private static Transform ResolveAreaRoot(Transform placeTransform, Transform navigationTransform)
        {
            Transform current = placeTransform != null ? placeTransform.parent : null;
            while (current != null)
            {
                if (current.GetComponent("DepositZone") != null || current.GetComponent<Collider>() != null)
                {
                    return current;
                }

                current = current.parent;
            }

            current = navigationTransform != null ? navigationTransform.parent : null;
            while (current != null)
            {
                if (current.GetComponent("DepositZone") != null || current.GetComponent<Collider>() != null)
                {
                    return current;
                }

                current = current.parent;
            }

            return placeTransform != null ? placeTransform.parent : navigationTransform != null ? navigationTransform.parent : null;
        }

        private static Vector3 BuildCandidatePosition(Bounds footprint, Vector3 direction, float standoff)
        {
            Vector3 center = footprint.center;
            float x = center.x;
            float z = center.z;

            if (Mathf.Abs(direction.x) > 0.5f)
            {
                x += Mathf.Sign(direction.x) * (footprint.extents.x + standoff);
            }

            if (Mathf.Abs(direction.z) > 0.5f)
            {
                z += Mathf.Sign(direction.z) * (footprint.extents.z + standoff);
            }

            return new Vector3(x, 0f, z);
        }

        private static Vector3 BuildCandidatePositionAroundPose(Vector3 posePosition, Vector3 direction, float standoff)
        {
            Vector3 planarDirection = new Vector3(direction.x, 0f, direction.z);
            if (planarDirection.sqrMagnitude < 0.0001f)
            {
                planarDirection = Vector3.back;
            }

            planarDirection.Normalize();
            Vector3 position = posePosition + planarDirection * standoff;
            return new Vector3(position.x, 0f, position.z);
        }

        private static Vector3 BuildDynamicPlacePosePosition(Bounds footprint, Vector3 direction, float inset, float minClearance)
        {
            Vector3 center = footprint.center;
            float safeInset = Mathf.Max(inset, minClearance);
            float x = center.x;
            float z = center.z;

            if (Mathf.Abs(direction.x) > 0.5f)
            {
                x += Mathf.Sign(direction.x) * Mathf.Max(0f, footprint.extents.x - safeInset);
            }

            if (Mathf.Abs(direction.z) > 0.5f)
            {
                z += Mathf.Sign(direction.z) * Mathf.Max(0f, footprint.extents.z - safeInset);
            }

            return new Vector3(x, center.y, z);
        }

        private static Vector3 WithY(Vector3 value, float y)
        {
            return new Vector3(value.x, y, value.z);
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
                path = $"{current.name}/{path}";
                current = current.parent;
            }

            return path;
        }

        private static float EstimateLeg(
            Vector3 from,
            Vector3 to,
            PlaceNavigationSettings settings,
            out bool usedFallback,
            out bool pathComplete)
        {
            usedFallback = false;
            pathComplete = false;

            if (!NavMesh.SamplePosition(from, out NavMeshHit fromHit, settings.NavMeshSampleRadius, NavMesh.AllAreas) ||
                !NavMesh.SamplePosition(to, out NavMeshHit toHit, settings.NavMeshSampleRadius, NavMesh.AllAreas))
            {
                usedFallback = true;
                return Vector3.Distance(from, to);
            }

            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(fromHit.position, toHit.position, NavMesh.AllAreas, path) ||
                path.status != NavMeshPathStatus.PathComplete ||
                path.corners == null ||
                path.corners.Length == 0)
            {
                usedFallback = true;
                return Vector3.Distance(fromHit.position, toHit.position);
            }

            pathComplete = true;
            float length = 0f;
            Vector3 previous = path.corners[0];
            for (int i = 1; i < path.corners.Length; i++)
            {
                length += Vector3.Distance(previous, path.corners[i]);
                previous = path.corners[i];
            }

            return length;
        }
    }
}
