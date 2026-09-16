using Autonomy.Domain;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public class MultimodalTaskIntentTests
    {
        [Test]
        public void PickAndPlaceByCategory_Builds_Structured_Request()
        {
            MultimodalTaskIntent intent = MultimodalTaskIntent.PickAndPlaceByCategory("A", "ZoneA", "test");

            Assert.That(intent.TaskFlow, Is.EqualTo(AutonomousTaskFlow.PickAndPlace));
            Assert.That(intent.ObjectSelectionMode, Is.EqualTo(MultimodalObjectSelectionMode.Category));
            Assert.That(intent.ObjectCategory, Is.EqualTo("A"));
            Assert.That(intent.PlaceTargetId, Is.EqualTo("ZoneA"));
            Assert.That(intent.Source, Is.EqualTo("test"));
        }

        [Test]
        public void PickAndPlaceByTargetId_Builds_Explicit_Target_Request()
        {
            MultimodalTaskIntent intent = MultimodalTaskIntent.PickAndPlaceByTargetId("STEP19_TestBox_A_01", "ZoneA", "test");

            Assert.That(intent.TaskFlow, Is.EqualTo(AutonomousTaskFlow.PickAndPlace));
            Assert.That(intent.ObjectSelectionMode, Is.EqualTo(MultimodalObjectSelectionMode.ExplicitTargetId));
            Assert.That(intent.TargetId, Is.EqualTo("STEP19_TestBox_A_01"));
            Assert.That(intent.PlaceTargetId, Is.EqualTo("ZoneA"));
        }

        [Test]
        public void PickOnlyByCategory_Builds_Pick_Request_Without_Place_Target()
        {
            MultimodalTaskIntent intent = MultimodalTaskIntent.PickOnlyByCategory("A", "test");

            Assert.That(intent.TaskFlow, Is.EqualTo(AutonomousTaskFlow.PickOnly));
            Assert.That(intent.ObjectSelectionMode, Is.EqualTo(MultimodalObjectSelectionMode.Category));
            Assert.That(intent.ObjectCategory, Is.EqualTo("A"));
            Assert.That(intent.PlaceTargetId, Is.Empty);
            Assert.That(intent.Source, Is.EqualTo("test"));
        }
    }
}
