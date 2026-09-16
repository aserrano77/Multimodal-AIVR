using Autonomy.Perception;
using NUnit.Framework;
using UnityEngine;

namespace Autonomy.Tests.Perception
{
    [TestFixture]
    public class PerceptionTargetSelectorTests
    {
        [Test]
        public void TrySelect_FiltersDepositedGrabbedAndHeldObjects()
        {
            var query = new PerceptionQuery(PerceptionSelectionStrategy.FirstAvailable);
            PerceivedObject[] candidates =
            {
                Candidate("deposited", 1f, isDeposited: true),
                Candidate("grabbed", 2f, isGrabbed: true),
                Candidate("held", 3f, isHeld: true),
                Candidate("available", 4f)
            };

            bool selected = PerceptionTargetSelector.TrySelect(candidates, query, out PerceivedObject result, out string failureReason);

            Assert.That(selected, Is.True);
            Assert.That(result.ObjectId, Is.EqualTo("available"));
            Assert.That(failureReason, Is.Empty);
        }

        [Test]
        public void TrySelect_NearestAvailable_UsesDistanceToReference()
        {
            var query = new PerceptionQuery(PerceptionSelectionStrategy.NearestAvailable, referencePosition: Vector3.zero);
            PerceivedObject[] candidates =
            {
                Candidate("far", 10f),
                Candidate("near", 2f),
                Candidate("middle", 5f)
            };

            bool selected = PerceptionTargetSelector.TrySelect(candidates, query, out PerceivedObject result, out _);

            Assert.That(selected, Is.True);
            Assert.That(result.ObjectId, Is.EqualTo("near"));
        }

        [Test]
        public void TrySelect_WithPreferredObjectId_SelectsStableTargetOverNearerClone()
        {
            var query = new PerceptionQuery(
                PerceptionSelectionStrategy.NearestAvailable,
                category: "A",
                referencePosition: Vector3.zero,
                preferredObjectId: "STEP19_TestBox_A_01");
            PerceivedObject[] candidates =
            {
                Candidate("CajaA(Clone)", 1f),
                Candidate("STEP19_TestBox_A_01", 8f)
            };

            bool selected = PerceptionTargetSelector.TrySelect(candidates, query, out PerceivedObject result, out _);

            Assert.That(selected, Is.True);
            Assert.That(result.ObjectId, Is.EqualTo("STEP19_TestBox_A_01"));
        }

        [Test]
        public void TrySelect_WithUnavailablePreferredObjectId_FallsBackToNearestAvailable()
        {
            var query = new PerceptionQuery(
                PerceptionSelectionStrategy.NearestAvailable,
                category: "A",
                referencePosition: Vector3.zero,
                preferredObjectId: "STEP19_TestBox_A_01");
            PerceivedObject[] candidates =
            {
                Candidate("CajaA(Clone)", 1f),
                Candidate("STEP19_TestBox_A_01", 8f, isDeposited: true)
            };

            bool selected = PerceptionTargetSelector.TrySelect(candidates, query, out PerceivedObject result, out _);

            Assert.That(selected, Is.True);
            Assert.That(result.ObjectId, Is.EqualTo("CajaA(Clone)"));
        }

        [Test]
        public void TrySelect_FailsWhenNoCandidateIsAvailable()
        {
            var query = new PerceptionQuery(PerceptionSelectionStrategy.NearestAvailable);
            PerceivedObject[] candidates =
            {
                Candidate("deposited", 1f, isDeposited: true),
                Candidate("grabbed", 2f, isGrabbed: true)
            };

            bool selected = PerceptionTargetSelector.TrySelect(candidates, query, out PerceivedObject result, out string failureReason);

            Assert.That(selected, Is.False);
            Assert.That(result, Is.Null);
            Assert.That(failureReason, Is.EqualTo("no_available_candidates"));
        }

        [Test]
        public void ToTargetDescriptor_PreservesObjectIdAndNavigationPosition()
        {
            PerceivedObject perceivedObject = Candidate("box_01", 0f);
            Vector3 navigationPosition = new Vector3(1.25f, 0f, -3.5f);

            var target = PerceptionTargetSelector.ToTargetDescriptor(perceivedObject, navigationPosition);

            Assert.That(target.Id, Is.EqualTo("box_01"));
            Assert.That(target.Position.X, Is.EqualTo(1.25f));
            Assert.That(target.Position.Y, Is.EqualTo(0f));
            Assert.That(target.Position.Z, Is.EqualTo(-3.5f));
        }

        private static PerceivedObject Candidate(
            string objectId,
            float distance,
            bool isDeposited = false,
            bool isGrabbed = false,
            bool isHeld = false)
        {
            return new PerceivedObject(
                objectId,
                objectId,
                "A",
                null,
                new Vector3(distance, 0f, 0f),
                new Vector3(distance, 0f, 0f),
                Vector3.one,
                BoundsSource.Collider,
                true,
                true,
                isDeposited,
                isGrabbed,
                isHeld,
                distance,
                string.Empty);
        }
    }
}
