using System;
using System.Collections.Generic;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    public readonly struct DynamicPlacePoseOverride
    {
        public DynamicPlacePoseOverride(
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

    public static class DynamicPlacePoseOverrideRegistry
    {
        private static readonly Dictionary<string, DynamicPlacePoseOverride> Overrides = new(StringComparer.OrdinalIgnoreCase);

        public static void Set(string placeTargetId, string objectId, DynamicPlacePoseOverride placePose)
        {
            if (string.IsNullOrWhiteSpace(placeTargetId) || !placePose.UsedDynamicPlacePose)
            {
                return;
            }

            Overrides[BuildKey(placeTargetId, objectId)] = placePose;
        }

        public static bool TryConsume(string placeTargetId, string objectId, out DynamicPlacePoseOverride placePose)
        {
            string exactKey = BuildKey(placeTargetId, objectId);
            if (Overrides.TryGetValue(exactKey, out placePose))
            {
                Overrides.Remove(exactKey);
                return true;
            }

            string wildcardKey = BuildKey(placeTargetId, string.Empty);
            if (Overrides.TryGetValue(wildcardKey, out placePose))
            {
                Overrides.Remove(wildcardKey);
                return true;
            }

            placePose = default;
            return false;
        }

        private static string BuildKey(string placeTargetId, string objectId)
        {
            return $"{placeTargetId ?? string.Empty}|{objectId ?? string.Empty}";
        }
    }
}
