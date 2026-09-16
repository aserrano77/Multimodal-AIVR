using System.Collections.Generic;

namespace Autonomy.Domain
{
    public sealed class RobotAssistancePlaceRecoveryConfig
    {
        public RobotAssistancePlaceRecoveryConfig(
            bool allowNonSlotDynamicPlaceFallback,
            bool useDynamicPlacePose,
            bool useDepositZoneSlotAllocator,
            float maxExpectedPlaceDistance,
            float maxRelaxedPlaceDistance,
            float placeCandidateReachabilityMargin,
            int maxPlaceApproachRetries)
        {
            AllowNonSlotDynamicPlaceFallback = allowNonSlotDynamicPlaceFallback;
            UseDynamicPlacePose = useDynamicPlacePose;
            UseDepositZoneSlotAllocator = useDepositZoneSlotAllocator;
            MaxExpectedPlaceDistance = maxExpectedPlaceDistance;
            MaxRelaxedPlaceDistance = maxRelaxedPlaceDistance;
            PlaceCandidateReachabilityMargin = placeCandidateReachabilityMargin;
            MaxPlaceApproachRetries = maxPlaceApproachRetries;
        }

        public bool AllowNonSlotDynamicPlaceFallback { get; }
        public bool UseDynamicPlacePose { get; }
        public bool UseDepositZoneSlotAllocator { get; }
        public float MaxExpectedPlaceDistance { get; }
        public float MaxRelaxedPlaceDistance { get; }
        public float PlaceCandidateReachabilityMargin { get; }
        public int MaxPlaceApproachRetries { get; }

        public Dictionary<string, object> ToPayload()
        {
            return new Dictionary<string, object>
            {
                ["allow_non_slot_dynamic_place_fallback"] = AllowNonSlotDynamicPlaceFallback,
                ["use_dynamic_place_pose"] = UseDynamicPlacePose,
                ["use_deposit_zone_slot_allocator"] = UseDepositZoneSlotAllocator,
                ["max_expected_place_distance"] = MaxExpectedPlaceDistance,
                ["max_relaxed_place_distance"] = MaxRelaxedPlaceDistance,
                ["place_candidate_reachability_margin"] = PlaceCandidateReachabilityMargin,
                ["max_place_approach_retries"] = MaxPlaceApproachRetries
            };
        }
    }
}
