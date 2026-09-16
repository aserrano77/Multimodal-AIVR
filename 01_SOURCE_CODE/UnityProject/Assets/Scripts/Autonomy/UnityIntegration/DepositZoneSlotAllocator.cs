using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Autonomy.UnityIntegration
{
    public readonly struct DepositZoneSlotSettings
    {
        public DepositZoneSlotSettings(
            float slotInset,
            float slotClearance,
            Vector3 occupancyCheckExtents,
            LayerMask obstacleLayerMask,
            float stackVerticalSpacing,
            float defaultBoxHeight,
            float keepZoneAccessMargin,
            float navMeshSampleRadius)
        {
            SlotInset = Mathf.Max(0f, slotInset);
            SlotClearance = Mathf.Max(0f, slotClearance);
            OccupancyCheckExtents = new Vector3(
                Mathf.Max(0.01f, occupancyCheckExtents.x),
                Mathf.Max(0.01f, occupancyCheckExtents.y),
                Mathf.Max(0.01f, occupancyCheckExtents.z));
            ObstacleLayerMask = obstacleLayerMask;
            StackVerticalSpacing = Mathf.Max(0f, stackVerticalSpacing);
            DefaultBoxHeight = Mathf.Max(0.01f, defaultBoxHeight);
            KeepZoneAccessMargin = Mathf.Max(0f, keepZoneAccessMargin);
            NavMeshSampleRadius = Mathf.Max(0.05f, navMeshSampleRadius);
        }

        public float SlotInset { get; }
        public float SlotClearance { get; }
        public Vector3 OccupancyCheckExtents { get; }
        public LayerMask ObstacleLayerMask { get; }
        public float StackVerticalSpacing { get; }
        public float DefaultBoxHeight { get; }
        public float KeepZoneAccessMargin { get; }
        public float NavMeshSampleRadius { get; }
    }

    public sealed class DepositZoneSlot
    {
        public string PlaceTargetId;
        public string SlotId;
        public string Quadrant;
        public Vector3 Position;
        public Quaternion Rotation;
        public int SlotIndex;
        public int StackLevel;
        public string State;
        public string AssignedBoxId;
        public float SlotInset;
        public float SlotClearance;
        public bool OccupancyCheckPassed;
        public string RejectionReason;
        public string AreaSource;
        public string BoundsSource;
        public string BoundsObjectName;
        public bool UsedFallbackBounds;
        public string BlockingObstacle;
        public int BlockingObstacleLayer = -1;
        public bool BlockingObstacleIsFloorOrOverlay;
        public int ObstacleLayerMask;
        public Vector3 SlotCenterLocal;
        public Vector3 ZoneCenterWorld;
        public Vector3 ZoneCenterLocal;
        public Vector3 ZoneSize;
        public Vector3 UsableZoneSize;
        public float InnerMarginX;
        public float InnerMarginZ;
        public Vector3 BoxFootprint;
        public Vector3 ObstacleFootprint;
        public float RequiredSpacingX;
        public float RequiredSpacingZ;
        public float ActualNeighborSpacingX;
        public float ActualNeighborSpacingZ;
        public float EdgeMarginX;
        public float EdgeMarginZ;
        public float DesiredEdgeMarginX;
        public float DesiredEdgeMarginZ;
        public float ActualEdgeMarginX;
        public float ActualEdgeMarginZ;
        public bool EdgeMarginClampedX;
        public bool EdgeMarginClampedZ;
        public float PhysicalRequiredTotalX;
        public float PhysicalRequiredTotalZ;
        public string GeometryInvalidReason;
        public bool SlotsOverlap;
        public bool GeometryValid;
        public bool SlotInsideZone;
        public bool CarvingBoundsInsideOrClamped;

        public bool IsAvailable => string.Equals(State, "available", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class DepositZoneSlotAllocator
    {
        private static readonly (string Quadrant, float X, float Z)[] Quadrants =
        {
            ("NorthEast", 1f, 1f),
            ("NorthWest", -1f, 1f),
            ("SouthEast", 1f, -1f),
            ("SouthWest", -1f, -1f)
        };

        private readonly Dictionary<string, DepositZoneSlot> _reservedByBox = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DepositZoneSlot> _occupiedBySlot = new(StringComparer.OrdinalIgnoreCase);

        public int ReservedCount => _reservedByBox.Count;
        public int OccupiedCount => _occupiedBySlot.Count;

        public void Clear()
        {
            _reservedByBox.Clear();
            _occupiedBySlot.Clear();
        }

        public bool TryGetReservedSlot(string boxId, out DepositZoneSlot slot)
        {
            return _reservedByBox.TryGetValue(boxId ?? string.Empty, out slot);
        }

        public void Reserve(DepositZoneSlot slot, string boxId)
        {
            if (slot == null || string.IsNullOrWhiteSpace(boxId))
            {
                return;
            }

            slot.State = "reserved";
            slot.AssignedBoxId = boxId;
            _reservedByBox[boxId] = Clone(slot);
        }

        public bool MarkOccupied(string boxId, out DepositZoneSlot slot)
        {
            if (!_reservedByBox.TryGetValue(boxId ?? string.Empty, out slot))
            {
                return false;
            }

            _reservedByBox.Remove(boxId);
            slot.State = "occupied";
            slot.AssignedBoxId = boxId;
            _occupiedBySlot[slot.SlotId] = Clone(slot);
            return true;
        }

        public bool Release(string boxId, out DepositZoneSlot slot)
        {
            if (!_reservedByBox.TryGetValue(boxId ?? string.Empty, out slot))
            {
                return false;
            }

            _reservedByBox.Remove(boxId);
            slot.State = "released";
            return true;
        }

        public IReadOnlyList<DepositZoneSlot> EvaluateSlots(
            string placeTargetId,
            Transform placeTransform,
            Transform navigationTransform,
            Vector3 fallbackPosition,
            string boxId,
            DepositZoneSlotSettings settings)
        {
            var results = new List<DepositZoneSlot>();
            if (TryGetReservedSlot(boxId, out DepositZoneSlot reserved))
            {
                results.Add(CloneAsAvailable(reserved, boxId));
                return results;
            }

            SlotZoneGeometry geometry = ResolveSlotZoneGeometry(placeTransform, navigationTransform, fallbackPosition, out string areaSource, out bool usedFallbackBounds);
            Vector3 placePoint = placeTransform != null ? placeTransform.position : fallbackPosition;
            SlotLayout layout = BuildSlotLayout(geometry, settings);
            int stackLevel = 0;

            for (int i = 0; i < Quadrants.Length; i++)
            {
                (string quadrant, float signX, float signZ) = Quadrants[i];
                DepositZoneSlot slot = null;
                for (stackLevel = 0; stackLevel < 8; stackLevel++)
                {
                    slot = BuildSlot(placeTargetId, quadrant, i, stackLevel, geometry, layout, placePoint, signX, signZ, areaSource, usedFallbackBounds, settings);
                    ApplyState(slot, boxId, settings);
                    if (!string.Equals(slot.RejectionReason, "slot_occupied", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(slot.RejectionReason, "slot_reserved", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }

                results.Add(slot);
            }

            return results;
        }

        public Dictionary<string, object> ToPayload(DepositZoneSlot slot)
        {
            return new Dictionary<string, object>
            {
                ["place_target_id"] = slot != null ? slot.PlaceTargetId : string.Empty,
                ["slot_id"] = slot != null ? slot.SlotId : string.Empty,
                ["quadrant"] = slot != null ? slot.Quadrant : string.Empty,
                ["stack_level"] = slot != null ? slot.StackLevel : 0,
                ["slot_index"] = slot != null ? slot.SlotIndex : 0,
                ["slot_position"] = slot != null ? slot.Position : Vector3.zero,
                ["slot_rotation"] = slot != null ? slot.Rotation.eulerAngles : Vector3.zero,
                ["slot_state"] = slot != null ? slot.State ?? string.Empty : string.Empty,
                ["assigned_box_id"] = slot != null ? slot.AssignedBoxId ?? string.Empty : string.Empty,
                ["slot_inset"] = slot != null ? slot.SlotInset : 0f,
                ["slot_clearance"] = slot != null ? slot.SlotClearance : 0f,
                ["occupancy_check_passed"] = slot != null && slot.OccupancyCheckPassed,
                ["rejection_reason"] = slot != null ? slot.RejectionReason ?? string.Empty : string.Empty,
                ["blocking_obstacle"] = slot != null ? slot.BlockingObstacle ?? string.Empty : string.Empty,
                ["blocking_obstacle_layer"] = slot != null ? slot.BlockingObstacleLayer : -1,
                ["blocking_obstacle_is_floor_or_overlay"] = slot != null && slot.BlockingObstacleIsFloorOrOverlay,
                ["obstacle_layer_mask"] = slot != null ? slot.ObstacleLayerMask : 0,
                ["place_area_source"] = slot != null ? slot.AreaSource ?? string.Empty : string.Empty,
                ["bounds_source"] = slot != null ? slot.BoundsSource ?? string.Empty : string.Empty,
                ["bounds_object_name"] = slot != null ? slot.BoundsObjectName ?? string.Empty : string.Empty,
                ["used_fallback_bounds"] = slot != null && slot.UsedFallbackBounds,
                ["zone_id"] = slot != null ? slot.PlaceTargetId : string.Empty,
                ["slot_center_world"] = slot != null ? slot.Position : Vector3.zero,
                ["slot_center_local"] = slot != null ? slot.SlotCenterLocal : Vector3.zero,
                ["zone_center_world"] = slot != null ? slot.ZoneCenterWorld : Vector3.zero,
                ["zone_center_local"] = slot != null ? slot.ZoneCenterLocal : Vector3.zero,
                ["zone_size"] = slot != null ? slot.ZoneSize : Vector3.zero,
                ["usable_zone_size"] = slot != null ? slot.UsableZoneSize : Vector3.zero,
                ["inner_margin_x"] = slot != null ? slot.InnerMarginX : 0f,
                ["inner_margin_z"] = slot != null ? slot.InnerMarginZ : 0f,
                ["box_footprint"] = slot != null ? slot.BoxFootprint : Vector3.zero,
                ["effective_box_footprint"] = slot != null ? slot.BoxFootprint : Vector3.zero,
                ["obstacle_footprint"] = slot != null ? slot.ObstacleFootprint : Vector3.zero,
                ["effective_obstacle_footprint"] = slot != null ? slot.ObstacleFootprint : Vector3.zero,
                ["required_spacing_x"] = slot != null ? slot.RequiredSpacingX : 0f,
                ["required_spacing_z"] = slot != null ? slot.RequiredSpacingZ : 0f,
                ["actual_neighbor_spacing_x"] = slot != null ? slot.ActualNeighborSpacingX : 0f,
                ["actual_neighbor_spacing_z"] = slot != null ? slot.ActualNeighborSpacingZ : 0f,
                ["neighbor_spacing_x"] = slot != null ? slot.ActualNeighborSpacingX : 0f,
                ["neighbor_spacing_z"] = slot != null ? slot.ActualNeighborSpacingZ : 0f,
                ["edge_margin_x"] = slot != null ? slot.EdgeMarginX : 0f,
                ["edge_margin_z"] = slot != null ? slot.EdgeMarginZ : 0f,
                ["desired_edge_margin_x"] = slot != null ? slot.DesiredEdgeMarginX : 0f,
                ["desired_edge_margin_z"] = slot != null ? slot.DesiredEdgeMarginZ : 0f,
                ["actual_edge_margin_x"] = slot != null ? slot.ActualEdgeMarginX : 0f,
                ["actual_edge_margin_z"] = slot != null ? slot.ActualEdgeMarginZ : 0f,
                ["edge_margin_clamped_x"] = slot != null && slot.EdgeMarginClampedX,
                ["edge_margin_clamped_z"] = slot != null && slot.EdgeMarginClampedZ,
                ["physical_required_total_x"] = slot != null ? slot.PhysicalRequiredTotalX : 0f,
                ["physical_required_total_z"] = slot != null ? slot.PhysicalRequiredTotalZ : 0f,
                ["slot_inside_zone"] = slot != null && slot.SlotInsideZone,
                ["carving_bounds_inside_or_clamped"] = slot != null && slot.CarvingBoundsInsideOrClamped,
                ["slots_overlap"] = slot != null && slot.SlotsOverlap,
                ["geometry_valid"] = slot != null && slot.GeometryValid,
                ["geometry_invalid_reason"] = slot != null ? slot.GeometryInvalidReason ?? string.Empty : string.Empty,
                ["used_place_slot_allocator"] = true
            };
        }

        private void ApplyState(DepositZoneSlot slot, string boxId, DepositZoneSlotSettings settings)
        {
            if (slot == null)
            {
                return;
            }

            if (!slot.GeometryValid)
            {
                slot.State = "blocked";
                slot.RejectionReason = "insufficient_zone_size_for_2x2_non_overlapping_slots";
                slot.OccupancyCheckPassed = false;
                return;
            }

            if (_occupiedBySlot.ContainsKey(slot.SlotId))
            {
                slot.State = "occupied";
                slot.RejectionReason = "slot_occupied";
                slot.OccupancyCheckPassed = false;
                return;
            }

            foreach (DepositZoneSlot reserved in _reservedByBox.Values)
            {
                if (string.Equals(reserved.SlotId, slot.SlotId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(reserved.AssignedBoxId, boxId, StringComparison.OrdinalIgnoreCase))
                {
                    slot.State = "reserved";
                    slot.AssignedBoxId = reserved.AssignedBoxId;
                    slot.RejectionReason = "slot_reserved";
                    slot.OccupancyCheckPassed = false;
                    return;
                }
            }

            Collider[] overlaps = Physics.OverlapBox(
                slot.Position,
                settings.OccupancyCheckExtents,
                slot.Rotation,
                settings.ObstacleLayerMask,
                QueryTriggerInteraction.Ignore);
            Collider blockingCollider = null;
            foreach (Collider overlap in overlaps)
            {
                if (IsIgnoredFloorOrOverlay(overlap))
                {
                    continue;
                }

                blockingCollider = overlap;
                break;
            }

            slot.OccupancyCheckPassed = blockingCollider == null;
            if (!slot.OccupancyCheckPassed)
            {
                slot.State = "blocked";
                slot.RejectionReason = "slot_blocked_by_physics";
                slot.BlockingObstacle = GetTransformPath(blockingCollider.transform);
                slot.BlockingObstacleLayer = blockingCollider.gameObject.layer;
                slot.BlockingObstacleIsFloorOrOverlay = IsIgnoredFloorOrOverlay(blockingCollider);
                return;
            }

            if (!NavMesh.SamplePosition(slot.Position, out _, settings.NavMeshSampleRadius, NavMesh.AllAreas))
            {
                slot.State = "blocked";
                slot.RejectionReason = "slot_not_near_navmesh";
                return;
            }

            slot.State = "available";
        }

        private static DepositZoneSlot BuildSlot(
            string placeTargetId,
            string quadrant,
            int slotIndex,
            int stackLevel,
            SlotZoneGeometry geometry,
            SlotLayout layout,
            Vector3 placePoint,
            float signX,
            float signZ,
            string areaSource,
            bool usedFallbackBounds,
            DepositZoneSlotSettings settings)
        {
            Vector3 localCenter = new(
                geometry.LocalCenter.x + signX * layout.CenterOffsetX,
                geometry.LocalCenter.y,
                geometry.LocalCenter.z + signZ * layout.CenterOffsetZ);
            Vector3 worldCenter = geometry.LocalToWorld.MultiplyPoint3x4(localCenter);
            Vector3 position = new(
                worldCenter.x,
                placePoint.y + stackLevel * (settings.DefaultBoxHeight + settings.StackVerticalSpacing),
                worldCenter.z);
            bool slotInsideZone =
                Mathf.Abs(localCenter.x - geometry.LocalCenter.x) <= geometry.LocalExtents.x + 0.0001f &&
                Mathf.Abs(localCenter.z - geometry.LocalCenter.z) <= geometry.LocalExtents.z + 0.0001f;
            bool carvingInside =
                Mathf.Abs(localCenter.x - geometry.LocalCenter.x) + layout.EffectiveHalfX <= geometry.LocalExtents.x + 0.0001f &&
                Mathf.Abs(localCenter.z - geometry.LocalCenter.z) + layout.EffectiveHalfZ <= geometry.LocalExtents.z + 0.0001f;
            string shortQuadrant = quadrant switch
            {
                "NorthEast" => "NE",
                "NorthWest" => "NW",
                "SouthEast" => "SE",
                "SouthWest" => "SW",
                _ => quadrant
            };

            return new DepositZoneSlot
            {
                PlaceTargetId = placeTargetId ?? string.Empty,
                SlotId = $"{placeTargetId}:slot_{shortQuadrant}:level{stackLevel}",
                Quadrant = quadrant,
                Position = position,
                Rotation = geometry.Rotation,
                SlotIndex = slotIndex,
                StackLevel = stackLevel,
                State = "candidate",
                SlotInset = settings.SlotInset,
                SlotClearance = settings.SlotClearance,
                ObstacleLayerMask = settings.ObstacleLayerMask.value,
                AreaSource = areaSource,
                BoundsSource = geometry.BoundsSource,
                BoundsObjectName = geometry.BoundsObjectName,
                UsedFallbackBounds = usedFallbackBounds,
                SlotCenterLocal = localCenter,
                ZoneCenterWorld = geometry.WorldCenter,
                ZoneCenterLocal = geometry.LocalCenter,
                ZoneSize = geometry.LocalSize,
                UsableZoneSize = new Vector3(layout.UsableSizeX, geometry.LocalSize.y, layout.UsableSizeZ),
                InnerMarginX = layout.EdgeMarginX,
                InnerMarginZ = layout.EdgeMarginZ,
                BoxFootprint = new Vector3(layout.EffectiveHalfX * 2f, settings.OccupancyCheckExtents.y * 2f, layout.EffectiveHalfZ * 2f),
                ObstacleFootprint = new Vector3(layout.EffectiveHalfX * 2f, settings.OccupancyCheckExtents.y * 2f, layout.EffectiveHalfZ * 2f),
                RequiredSpacingX = layout.RequiredSpacingX,
                RequiredSpacingZ = layout.RequiredSpacingZ,
                ActualNeighborSpacingX = layout.ActualSpacingX,
                ActualNeighborSpacingZ = layout.ActualSpacingZ,
                EdgeMarginX = layout.EdgeMarginX,
                EdgeMarginZ = layout.EdgeMarginZ,
                DesiredEdgeMarginX = layout.DesiredEdgeMarginX,
                DesiredEdgeMarginZ = layout.DesiredEdgeMarginZ,
                ActualEdgeMarginX = layout.ActualEdgeMarginX,
                ActualEdgeMarginZ = layout.ActualEdgeMarginZ,
                EdgeMarginClampedX = layout.EdgeMarginClampedX,
                EdgeMarginClampedZ = layout.EdgeMarginClampedZ,
                PhysicalRequiredTotalX = layout.PhysicalRequiredTotalX,
                PhysicalRequiredTotalZ = layout.PhysicalRequiredTotalZ,
                SlotsOverlap = layout.SlotsOverlap,
                GeometryValid = layout.GeometryValid,
                GeometryInvalidReason = layout.GeometryInvalidReason,
                SlotInsideZone = slotInsideZone,
                CarvingBoundsInsideOrClamped = carvingInside && layout.GeometryValid
            };
        }

        private readonly struct SlotLayout
        {
            public SlotLayout(
                float centerOffsetX,
                float centerOffsetZ,
                float effectiveHalfX,
                float effectiveHalfZ,
                float edgeMarginX,
                float edgeMarginZ,
                float requiredSpacingX,
                float requiredSpacingZ,
                float actualSpacingX,
                float actualSpacingZ,
                float usableSizeX,
                float usableSizeZ,
                float desiredEdgeMarginX,
                float desiredEdgeMarginZ,
                float actualEdgeMarginX,
                float actualEdgeMarginZ,
                bool edgeMarginClampedX,
                bool edgeMarginClampedZ,
                float physicalRequiredTotalX,
                float physicalRequiredTotalZ,
                bool geometryValid,
                string geometryInvalidReason)
            {
                CenterOffsetX = centerOffsetX;
                CenterOffsetZ = centerOffsetZ;
                EffectiveHalfX = effectiveHalfX;
                EffectiveHalfZ = effectiveHalfZ;
                EdgeMarginX = edgeMarginX;
                EdgeMarginZ = edgeMarginZ;
                RequiredSpacingX = requiredSpacingX;
                RequiredSpacingZ = requiredSpacingZ;
                ActualSpacingX = actualSpacingX;
                ActualSpacingZ = actualSpacingZ;
                UsableSizeX = usableSizeX;
                UsableSizeZ = usableSizeZ;
                DesiredEdgeMarginX = desiredEdgeMarginX;
                DesiredEdgeMarginZ = desiredEdgeMarginZ;
                ActualEdgeMarginX = actualEdgeMarginX;
                ActualEdgeMarginZ = actualEdgeMarginZ;
                EdgeMarginClampedX = edgeMarginClampedX;
                EdgeMarginClampedZ = edgeMarginClampedZ;
                PhysicalRequiredTotalX = physicalRequiredTotalX;
                PhysicalRequiredTotalZ = physicalRequiredTotalZ;
                GeometryValid = geometryValid;
                GeometryInvalidReason = geometryInvalidReason ?? string.Empty;
                SlotsOverlap = actualSpacingX + 0.0001f < requiredSpacingX || actualSpacingZ + 0.0001f < requiredSpacingZ;
            }

            public float CenterOffsetX { get; }
            public float CenterOffsetZ { get; }
            public float EffectiveHalfX { get; }
            public float EffectiveHalfZ { get; }
            public float EdgeMarginX { get; }
            public float EdgeMarginZ { get; }
            public float DesiredEdgeMarginX { get; }
            public float DesiredEdgeMarginZ { get; }
            public float ActualEdgeMarginX { get; }
            public float ActualEdgeMarginZ { get; }
            public bool EdgeMarginClampedX { get; }
            public bool EdgeMarginClampedZ { get; }
            public float RequiredSpacingX { get; }
            public float RequiredSpacingZ { get; }
            public float ActualSpacingX { get; }
            public float ActualSpacingZ { get; }
            public float UsableSizeX { get; }
            public float UsableSizeZ { get; }
            public float PhysicalRequiredTotalX { get; }
            public float PhysicalRequiredTotalZ { get; }
            public bool SlotsOverlap { get; }
            public bool GeometryValid { get; }
            public string GeometryInvalidReason { get; }
        }

        private static SlotLayout BuildSlotLayout(SlotZoneGeometry geometry, DepositZoneSlotSettings settings)
        {
            float effectiveHalfX = Mathf.Max(0.01f, settings.OccupancyCheckExtents.x);
            float effectiveHalfZ = Mathf.Max(0.01f, settings.OccupancyCheckExtents.z);
            float interSlotGap = Mathf.Max(0f, settings.SlotClearance);
            float desiredEdgeMarginX = Mathf.Max(settings.SlotInset, settings.KeepZoneAccessMargin);
            float desiredEdgeMarginZ = Mathf.Max(settings.SlotInset, settings.KeepZoneAccessMargin);
            float requiredSpacingX = 2f * effectiveHalfX + interSlotGap;
            float requiredSpacingZ = 2f * effectiveHalfZ + interSlotGap;
            float requiredTotalX = requiredSpacingX + 2f * effectiveHalfX;
            float requiredTotalZ = requiredSpacingZ + 2f * effectiveHalfZ;
            bool geometryValidX = geometry.LocalSize.x + 0.0001f >= requiredTotalX;
            bool geometryValidZ = geometry.LocalSize.z + 0.0001f >= requiredTotalZ;
            bool geometryValid = geometryValidX && geometryValidZ;
            string invalidReason = geometryValid
                ? string.Empty
                : "insufficient_zone_size_for_2x2_non_overlapping_slots";
            float minOffsetX = requiredSpacingX * 0.5f;
            float minOffsetZ = requiredSpacingZ * 0.5f;
            float desiredOffsetX = geometry.LocalExtents.x * 0.5f;
            float desiredOffsetZ = geometry.LocalExtents.z * 0.5f;
            const float hardMinimumEdgeMargin = 0.01f;
            float maxOffsetX = geometry.LocalExtents.x - hardMinimumEdgeMargin - effectiveHalfX;
            float maxOffsetZ = geometry.LocalExtents.z - hardMinimumEdgeMargin - effectiveHalfZ;
            float offsetX = geometryValid ? Mathf.Clamp(desiredOffsetX, minOffsetX, Mathf.Max(minOffsetX, maxOffsetX)) : Mathf.Max(0f, maxOffsetX);
            float offsetZ = geometryValid ? Mathf.Clamp(desiredOffsetZ, minOffsetZ, Mathf.Max(minOffsetZ, maxOffsetZ)) : Mathf.Max(0f, maxOffsetZ);
            float actualSpacingX = offsetX * 2f;
            float actualSpacingZ = offsetZ * 2f;
            float actualEdgeMarginX = geometry.LocalExtents.x - offsetX - effectiveHalfX;
            float actualEdgeMarginZ = geometry.LocalExtents.z - offsetZ - effectiveHalfZ;
            float usableSizeX = Mathf.Max(0f, geometry.LocalSize.x - 2f * actualEdgeMarginX);
            float usableSizeZ = Mathf.Max(0f, geometry.LocalSize.z - 2f * actualEdgeMarginZ);
            return new SlotLayout(
                offsetX,
                offsetZ,
                effectiveHalfX,
                effectiveHalfZ,
                actualEdgeMarginX,
                actualEdgeMarginZ,
                requiredSpacingX,
                requiredSpacingZ,
                actualSpacingX,
                actualSpacingZ,
                usableSizeX,
                usableSizeZ,
                desiredEdgeMarginX,
                desiredEdgeMarginZ,
                actualEdgeMarginX,
                actualEdgeMarginZ,
                actualEdgeMarginX + 0.0001f < desiredEdgeMarginX,
                actualEdgeMarginZ + 0.0001f < desiredEdgeMarginZ,
                requiredTotalX,
                requiredTotalZ,
                geometryValid,
                invalidReason);
        }

        private readonly struct SlotZoneGeometry
        {
            public SlotZoneGeometry(Vector3 localCenter, Vector3 localSize, Matrix4x4 localToWorld, Quaternion rotation, string boundsSource, string boundsObjectName)
            {
                LocalCenter = localCenter;
                LocalSize = new Vector3(Mathf.Max(0.01f, localSize.x), Mathf.Max(0.01f, localSize.y), Mathf.Max(0.01f, localSize.z));
                LocalExtents = LocalSize * 0.5f;
                LocalToWorld = localToWorld;
                Rotation = rotation;
                WorldCenter = localToWorld.MultiplyPoint3x4(localCenter);
                BoundsSource = boundsSource ?? string.Empty;
                BoundsObjectName = boundsObjectName ?? string.Empty;
            }

            public Vector3 LocalCenter { get; }
            public Vector3 LocalSize { get; }
            public Vector3 LocalExtents { get; }
            public Matrix4x4 LocalToWorld { get; }
            public Quaternion Rotation { get; }
            public Vector3 WorldCenter { get; }
            public string BoundsSource { get; }
            public string BoundsObjectName { get; }
        }

        private static SlotZoneGeometry ResolveSlotZoneGeometry(
            Transform placeTransform,
            Transform navigationTransform,
            Vector3 fallbackPosition,
            out string areaSource,
            out bool usedFallbackBounds)
        {
            Transform areaRoot = ResolveAreaRoot(placeTransform, navigationTransform);
            if (areaRoot != null)
            {
                if (TryResolveBestZoneBounds(areaRoot, out Bounds bounds, out string boundsSource, out string boundsObjectName))
                {
                    areaSource = boundsSource;
                    usedFallbackBounds = false;
                    return BuildGeometryFromWorldBounds(bounds, areaRoot, boundsSource, boundsObjectName);
                }
            }

            areaSource = "fallback_navigation_point";
            usedFallbackBounds = true;
            Matrix4x4 localToWorld = Matrix4x4.TRS(new Vector3(fallbackPosition.x, 0f, fallbackPosition.z), Quaternion.identity, Vector3.one);
            return new SlotZoneGeometry(Vector3.zero, new Vector3(0.8f, 0.1f, 0.8f), localToWorld, Quaternion.identity, "fallback_navigation_point", string.Empty);
        }

        private static bool TryResolveBestZoneBounds(Transform areaRoot, out Bounds bounds, out string boundsSource, out string boundsObjectName)
        {
            bounds = default;
            boundsSource = string.Empty;
            boundsObjectName = string.Empty;
            float bestArea = -1f;

            Collider[] colliders = areaRoot.GetComponentsInChildren<Collider>(true);
            foreach (Collider collider in colliders)
            {
                if (collider == null || IsLikelyBorderOrAuxiliary(collider.transform))
                {
                    continue;
                }

                float area = PlanarArea(collider.bounds);
                if (area > bestArea)
                {
                    bestArea = area;
                    bounds = collider.bounds;
                    boundsSource = collider.transform == areaRoot ? "deposit_zone_collider" : "deposit_zone_child_collider";
                    boundsObjectName = GetTransformPath(collider.transform);
                }
            }

            Renderer[] renderers = areaRoot.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || IsLikelyBorderOrAuxiliary(renderer.transform))
                {
                    continue;
                }

                float area = PlanarArea(renderer.bounds);
                if (area > bestArea)
                {
                    bestArea = area;
                    bounds = renderer.bounds;
                    boundsSource = renderer.transform == areaRoot ? "deposit_zone_renderer" : "deposit_zone_child_renderer";
                    boundsObjectName = GetTransformPath(renderer.transform);
                }
            }

            if (bestArea > 0f)
            {
                return true;
            }

            Collider fallbackCollider = areaRoot.GetComponent<Collider>() ?? areaRoot.GetComponentInChildren<Collider>(true);
            if (fallbackCollider != null)
            {
                bounds = fallbackCollider.bounds;
                boundsSource = "fallback_any_collider";
                boundsObjectName = GetTransformPath(fallbackCollider.transform);
                return true;
            }

            Renderer fallbackRenderer = areaRoot.GetComponentInChildren<Renderer>(true);
            if (fallbackRenderer != null)
            {
                bounds = fallbackRenderer.bounds;
                boundsSource = "fallback_any_renderer";
                boundsObjectName = GetTransformPath(fallbackRenderer.transform);
                return true;
            }

            return false;
        }

        private static SlotZoneGeometry BuildGeometryFromWorldBounds(Bounds worldBounds, Transform areaRoot, string boundsSource, string boundsObjectName)
        {
            Vector3 worldCenter = new(worldBounds.center.x, 0f, worldBounds.center.z);
            if (areaRoot == null)
            {
                Matrix4x4 fallbackLocalToWorld = Matrix4x4.TRS(worldCenter, Quaternion.identity, Vector3.one);
                return new SlotZoneGeometry(Vector3.zero, new Vector3(worldBounds.size.x, worldBounds.size.y, worldBounds.size.z), fallbackLocalToWorld, Quaternion.identity, boundsSource, boundsObjectName);
            }

            Vector3 localCenter = areaRoot.InverseTransformPoint(worldCenter);
            Vector3 localSize = new(
                Mathf.Abs(worldBounds.size.x / SafeScale(areaRoot.lossyScale.x)),
                Mathf.Abs(worldBounds.size.y / SafeScale(areaRoot.lossyScale.y)),
                Mathf.Abs(worldBounds.size.z / SafeScale(areaRoot.lossyScale.z)));
            Quaternion rotation = Quaternion.Euler(0f, areaRoot.rotation.eulerAngles.y, 0f);
            Matrix4x4 localToWorld = Matrix4x4.TRS(areaRoot.position, rotation, Vector3.one);
            return new SlotZoneGeometry(localCenter, localSize, localToWorld, rotation, boundsSource, boundsObjectName);
        }

        private static float SafeScale(float value)
        {
            return Mathf.Abs(value) < 0.0001f ? 1f : value;
        }

        private static float PlanarArea(Bounds bounds)
        {
            return Mathf.Max(0f, bounds.size.x) * Mathf.Max(0f, bounds.size.z);
        }

        private static bool IsLikelyBorderOrAuxiliary(Transform transform)
        {
            if (transform == null)
            {
                return false;
            }

            string path = GetTransformPath(transform).ToLowerInvariant();
            return path.Contains("border") ||
                   path.Contains("edge") ||
                   path.Contains("frame") ||
                   path.Contains("outline") ||
                   path.Contains("label") ||
                   path.Contains("text");
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

        private static DepositZoneSlot Clone(DepositZoneSlot slot)
        {
            return slot == null
                ? null
                : new DepositZoneSlot
                {
                    PlaceTargetId = slot.PlaceTargetId,
                    SlotId = slot.SlotId,
                    Quadrant = slot.Quadrant,
                    Position = slot.Position,
                    Rotation = slot.Rotation,
                    SlotIndex = slot.SlotIndex,
                    StackLevel = slot.StackLevel,
                    State = slot.State,
                    AssignedBoxId = slot.AssignedBoxId,
                    SlotInset = slot.SlotInset,
                    SlotClearance = slot.SlotClearance,
                    OccupancyCheckPassed = slot.OccupancyCheckPassed,
                    RejectionReason = slot.RejectionReason,
                    BlockingObstacle = slot.BlockingObstacle,
                    BlockingObstacleLayer = slot.BlockingObstacleLayer,
                    BlockingObstacleIsFloorOrOverlay = slot.BlockingObstacleIsFloorOrOverlay,
                    ObstacleLayerMask = slot.ObstacleLayerMask,
                    AreaSource = slot.AreaSource,
                    BoundsSource = slot.BoundsSource,
                    BoundsObjectName = slot.BoundsObjectName,
                    UsedFallbackBounds = slot.UsedFallbackBounds,
                    SlotCenterLocal = slot.SlotCenterLocal,
                    ZoneCenterWorld = slot.ZoneCenterWorld,
                    ZoneCenterLocal = slot.ZoneCenterLocal,
                    ZoneSize = slot.ZoneSize,
                    UsableZoneSize = slot.UsableZoneSize,
                    InnerMarginX = slot.InnerMarginX,
                    InnerMarginZ = slot.InnerMarginZ,
                    BoxFootprint = slot.BoxFootprint,
                    ObstacleFootprint = slot.ObstacleFootprint,
                    RequiredSpacingX = slot.RequiredSpacingX,
                    RequiredSpacingZ = slot.RequiredSpacingZ,
                    ActualNeighborSpacingX = slot.ActualNeighborSpacingX,
                    ActualNeighborSpacingZ = slot.ActualNeighborSpacingZ,
                    EdgeMarginX = slot.EdgeMarginX,
                    EdgeMarginZ = slot.EdgeMarginZ,
                    SlotsOverlap = slot.SlotsOverlap,
                    GeometryValid = slot.GeometryValid,
                    SlotInsideZone = slot.SlotInsideZone,
                    CarvingBoundsInsideOrClamped = slot.CarvingBoundsInsideOrClamped
                };
        }

        private static DepositZoneSlot CloneAsAvailable(DepositZoneSlot slot, string boxId)
        {
            DepositZoneSlot clone = Clone(slot);
            clone.State = "available";
            clone.AssignedBoxId = boxId ?? string.Empty;
            clone.RejectionReason = string.Empty;
            clone.OccupancyCheckPassed = true;
            return clone;
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
    }
}
