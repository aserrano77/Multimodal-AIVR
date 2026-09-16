using System.Collections.Generic;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using UnityEngine;

namespace Autonomy.BT.Tests
{
    public sealed class DepositZoneSlotAllocatorTests
    {
        [Test]
        public void EvaluateSlots_UsesFixedQuadrantCentersInsideUsableZone()
        {
            using ZoneRig rig = ZoneRig.Create();
            var allocator = new DepositZoneSlotAllocator();

            IReadOnlyList<DepositZoneSlot> slots = allocator.EvaluateSlots(
                "ZoneTest",
                rig.PlaceTransform,
                rig.NavigationTransform,
                Vector3.zero,
                "box_a",
                CreateSettings());

            Assert.That(slots, Has.Count.EqualTo(4));
            AssertSlot(slots[0], "ZoneTest:slot_NE:level0", "NorthEast", 1f, 1f);
            AssertSlot(slots[1], "ZoneTest:slot_NW:level0", "NorthWest", -1f, 1f);
            AssertSlot(slots[2], "ZoneTest:slot_SE:level0", "SouthEast", 1f, -1f);
            AssertSlot(slots[3], "ZoneTest:slot_SW:level0", "SouthWest", -1f, -1f);
            foreach (DepositZoneSlot slot in slots)
            {
                Assert.That(slot.DesiredEdgeMarginX, Is.EqualTo(0.1f).Within(0.0001f));
                Assert.That(slot.DesiredEdgeMarginZ, Is.EqualTo(0.1f).Within(0.0001f));
                Assert.That(slot.ActualEdgeMarginX, Is.EqualTo(0.6f).Within(0.0001f));
                Assert.That(slot.ActualEdgeMarginZ, Is.EqualTo(0.6f).Within(0.0001f));
                Assert.That(slot.RequiredSpacingX, Is.EqualTo(0.9f).Within(0.0001f));
                Assert.That(slot.RequiredSpacingZ, Is.EqualTo(0.9f).Within(0.0001f));
                Assert.That(slot.ActualNeighborSpacingX, Is.EqualTo(2f).Within(0.0001f));
                Assert.That(slot.ActualNeighborSpacingZ, Is.EqualTo(2f).Within(0.0001f));
                Assert.That(slot.EdgeMarginClampedX, Is.False);
                Assert.That(slot.EdgeMarginClampedZ, Is.False);
                Assert.That(slot.SlotsOverlap, Is.False);
                Assert.That(slot.GeometryValid, Is.True);
                Assert.That(slot.SlotInsideZone, Is.True);
                Assert.That(slot.CarvingBoundsInsideOrClamped, Is.True);
            }
        }

        [Test]
        public void EvaluateSlots_DoesNotChangeSlotCenters_WhenFallbackOrApproachChanges()
        {
            using ZoneRig rig = ZoneRig.Create();
            var allocator = new DepositZoneSlotAllocator();

            IReadOnlyList<DepositZoneSlot> first = allocator.EvaluateSlots(
                "ZoneTest",
                rig.PlaceTransform,
                rig.NavigationTransform,
                new Vector3(10f, 0f, -5f),
                "box_a",
                CreateSettings());
            IReadOnlyList<DepositZoneSlot> second = allocator.EvaluateSlots(
                "ZoneTest",
                rig.PlaceTransform,
                rig.NavigationTransform,
                new Vector3(-8f, 0f, 12f),
                "box_b",
                CreateSettings());

            for (int i = 0; i < first.Count; i++)
            {
                Assert.That(second[i].SlotId, Is.EqualTo(first[i].SlotId.Replace("level0", "level0")));
                Assert.That(second[i].SlotCenterLocal.x, Is.EqualTo(first[i].SlotCenterLocal.x).Within(0.0001f));
                Assert.That(second[i].SlotCenterLocal.z, Is.EqualTo(first[i].SlotCenterLocal.z).Within(0.0001f));
                Assert.That(second[i].Position.x, Is.EqualTo(first[i].Position.x).Within(0.0001f));
                Assert.That(second[i].Position.z, Is.EqualTo(first[i].Position.z).Within(0.0001f));
            }
        }

        [Test]
        public void EvaluateSlots_MarginIncludesEffectiveObstacleFootprint()
        {
            using ZoneRig rig = ZoneRig.Create();
            var allocator = new DepositZoneSlotAllocator();
            DepositZoneSlotSettings settings = CreateSettings(new Vector3(0.8f, 0.1f, 0.5f));

            IReadOnlyList<DepositZoneSlot> slots = allocator.EvaluateSlots(
                "ZoneTest",
                rig.PlaceTransform,
                rig.NavigationTransform,
                Vector3.zero,
                "box_a",
                settings);

            foreach (DepositZoneSlot slot in slots)
            {
                Assert.That(slot.RequiredSpacingX, Is.EqualTo(1.7f).Within(0.0001f));
                Assert.That(slot.RequiredSpacingZ, Is.EqualTo(1.1f).Within(0.0001f));
                Assert.That(Mathf.Abs(slot.SlotCenterLocal.x) + settings.OccupancyCheckExtents.x, Is.LessThanOrEqualTo(slot.ZoneSize.x * 0.5f + 0.0001f));
                Assert.That(Mathf.Abs(slot.SlotCenterLocal.z) + settings.OccupancyCheckExtents.z, Is.LessThanOrEqualTo(slot.ZoneSize.z * 0.5f + 0.0001f));
            }
        }

        [Test]
        public void EvaluateSlots_ClampsPreferredMargin_WhenPhysicalPackingFits()
        {
            using ZoneRig rig = ZoneRig.Create(new Vector3(1f, 0.1f, 1.5f));
            var allocator = new DepositZoneSlotAllocator();
            DepositZoneSlotSettings settings = CreateSettings(
                new Vector3(0.18f, 0.1f, 0.18f),
                slotInset: 0.30f,
                slotClearance: 0.10f,
                keepZoneAccessMargin: 0.10f);

            IReadOnlyList<DepositZoneSlot> slots = allocator.EvaluateSlots(
                "ZoneB",
                rig.PlaceTransform,
                rig.NavigationTransform,
                Vector3.zero,
                "box_a",
                settings);

            Assert.That(slots, Has.Count.EqualTo(4));
            foreach (DepositZoneSlot slot in slots)
            {
                Assert.That(slot.GeometryValid, Is.True);
                Assert.That(slot.SlotsOverlap, Is.False);
                Assert.That(slot.RequiredSpacingX, Is.EqualTo(0.46f).Within(0.0001f));
                Assert.That(slot.ActualNeighborSpacingX, Is.GreaterThanOrEqualTo(slot.RequiredSpacingX));
                Assert.That(slot.EdgeMarginClampedX, Is.True);
                Assert.That(slot.ActualEdgeMarginX, Is.LessThan(slot.DesiredEdgeMarginX));
            }
        }

        [Test]
        public void EvaluateSlots_RejectsGeometry_WhenFourSlotsPhysicallyCannotFit()
        {
            using ZoneRig rig = ZoneRig.Create(new Vector3(0.75f, 0.1f, 1.5f));
            var allocator = new DepositZoneSlotAllocator();
            DepositZoneSlotSettings settings = CreateSettings(
                new Vector3(0.18f, 0.1f, 0.18f),
                slotInset: 0.30f,
                slotClearance: 0.10f,
                keepZoneAccessMargin: 0.10f);

            IReadOnlyList<DepositZoneSlot> slots = allocator.EvaluateSlots(
                "ZoneTiny",
                rig.PlaceTransform,
                rig.NavigationTransform,
                Vector3.zero,
                "box_a",
                settings);

            Assert.That(slots, Has.Count.EqualTo(4));
            foreach (DepositZoneSlot slot in slots)
            {
                Assert.That(slot.GeometryValid, Is.False);
                Assert.That(slot.GeometryInvalidReason, Is.EqualTo("insufficient_zone_size_for_2x2_non_overlapping_slots"));
                Assert.That(slot.State, Is.EqualTo("blocked"));
                Assert.That(slot.PhysicalRequiredTotalX, Is.EqualTo(0.82f).Within(0.0001f));
            }
        }

        [Test]
        public void EvaluateSlots_UsesMainZoneSurface_InsteadOfBorderChild()
        {
            using ZoneRig rig = ZoneRig.Create(addBorderChild: true);
            var allocator = new DepositZoneSlotAllocator();

            IReadOnlyList<DepositZoneSlot> slots = allocator.EvaluateSlots(
                "ZoneTest",
                rig.PlaceTransform,
                rig.NavigationTransform,
                Vector3.zero,
                "box_a",
                CreateSettings());

            Assert.That(slots[0].BoundsObjectName, Does.Not.Contain("BoxBorder"));
            Assert.That(slots[0].ZoneSize.x, Is.EqualTo(4f).Within(0.0001f));
            Assert.That(slots[0].ZoneSize.z, Is.EqualTo(4f).Within(0.0001f));
        }

        [Test]
        public void Reserve_PreservesGeometryDiagnostics_ForReservedPayload()
        {
            using ZoneRig rig = ZoneRig.Create(new Vector3(1f, 0.1f, 1.5f));
            var allocator = new DepositZoneSlotAllocator();
            DepositZoneSlotSettings settings = CreateSettings(
                new Vector3(0.18f, 0.1f, 0.18f),
                slotInset: 0.30f,
                slotClearance: 0.10f,
                keepZoneAccessMargin: 0.10f);
            IReadOnlyList<DepositZoneSlot> slots = allocator.EvaluateSlots(
                "ZoneB",
                rig.PlaceTransform,
                rig.NavigationTransform,
                Vector3.zero,
                "box_a",
                settings);

            allocator.Reserve(slots[0], "box_a");
            Assert.That(allocator.TryGetReservedSlot("box_a", out DepositZoneSlot reserved), Is.True);
            Dictionary<string, object> payload = allocator.ToPayload(reserved);

            Assert.That(reserved.GeometryValid, Is.True);
            Assert.That(reserved.SlotsOverlap, Is.False);
            Assert.That(reserved.ZoneSize, Is.Not.EqualTo(Vector3.zero));
            Assert.That(reserved.RequiredSpacingX, Is.EqualTo(0.46f).Within(0.0001f));
            Assert.That(reserved.ActualNeighborSpacingX, Is.GreaterThanOrEqualTo(reserved.RequiredSpacingX));
            Assert.That(payload["geometry_valid"], Is.EqualTo(true));
            Assert.That(payload["slots_overlap"], Is.EqualTo(false));
            Assert.That(payload["zone_size"], Is.Not.EqualTo(Vector3.zero));
            Assert.That(payload["slot_inside_zone"], Is.EqualTo(true));
        }

        private static void AssertSlot(DepositZoneSlot slot, string slotId, string quadrant, float expectedX, float expectedZ)
        {
            Assert.That(slot.SlotId, Is.EqualTo(slotId));
            Assert.That(slot.Quadrant, Is.EqualTo(quadrant));
            Assert.That(slot.SlotCenterLocal.x, Is.EqualTo(expectedX).Within(0.0001f));
            Assert.That(slot.SlotCenterLocal.z, Is.EqualTo(expectedZ).Within(0.0001f));
            Assert.That(slot.Position.x, Is.EqualTo(expectedX).Within(0.0001f));
            Assert.That(slot.Position.z, Is.EqualTo(expectedZ).Within(0.0001f));
        }

        private static DepositZoneSlotSettings CreateSettings(
            Vector3? occupancyExtents = null,
            float slotInset = 0.1f,
            float slotClearance = 0.1f,
            float keepZoneAccessMargin = 0.1f)
        {
            return new DepositZoneSlotSettings(
                slotInset: slotInset,
                slotClearance: slotClearance,
                occupancyCheckExtents: occupancyExtents ?? new Vector3(0.4f, 0.1f, 0.4f),
                obstacleLayerMask: 0,
                stackVerticalSpacing: 0.05f,
                defaultBoxHeight: 0.2f,
                keepZoneAccessMargin: keepZoneAccessMargin,
                navMeshSampleRadius: 0.2f);
        }

        private sealed class ZoneRig : System.IDisposable
        {
            private readonly GameObject _root;

            private ZoneRig(GameObject root, Transform placeTransform, Transform navigationTransform)
            {
                _root = root;
                PlaceTransform = placeTransform;
                NavigationTransform = navigationTransform;
            }

            public Transform PlaceTransform { get; }
            public Transform NavigationTransform { get; }

            public static ZoneRig Create(Vector3? zoneSize = null, bool addBorderChild = false)
            {
                var root = new GameObject("ZoneTest");
                var collider = root.AddComponent<BoxCollider>();
                collider.size = zoneSize ?? new Vector3(4f, 0.1f, 4f);
                collider.center = Vector3.zero;

                if (addBorderChild)
                {
                    var border = new GameObject("BoxBorderA");
                    border.transform.SetParent(root.transform, false);
                    border.transform.localPosition = new Vector3(0f, 0f, 0f);
                    var borderCollider = border.AddComponent<BoxCollider>();
                    borderCollider.size = new Vector3(1f, 0.1f, 1.5f);
                    borderCollider.center = Vector3.zero;
                }

                var place = new GameObject("PlacePoint");
                place.transform.SetParent(root.transform, false);
                place.transform.localPosition = Vector3.zero;

                var navigation = new GameObject("NavigationPoint");
                navigation.transform.SetParent(root.transform, false);
                navigation.transform.localPosition = new Vector3(0f, 0f, -2f);

                return new ZoneRig(root, place.transform, navigation.transform);
            }

            public void Dispose()
            {
                Object.DestroyImmediate(_root);
            }
        }
    }
}
