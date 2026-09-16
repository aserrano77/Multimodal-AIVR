using System.Collections.Generic;
using Autonomy.Domain;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public class ExperimentTrialMetricsAccumulatorTests
    {
        [Test]
        public void BuildSummary_Computes_Successful_PickAndPlace_Durations()
        {
            var accumulator = new ExperimentTrialMetricsAccumulator(Metadata(), 10f);

            accumulator.ObserveEvent("multimodal_intent_resolved", new Dictionary<string, object>
            {
                ["selected_object_id"] = "STEP19_TestBox_A_01",
                ["selected_object_category"] = "A",
                ["requested_place_target_id"] = "ZoneA"
            }, 10.5f);
            accumulator.ObserveEvent("navigation_to_pick_started", null, 11f);
            accumulator.ObserveEvent("navigation_to_pick_succeeded", null, 15f);
            accumulator.ObserveEvent("manipulation_pick_requested", new Dictionary<string, object> { ["object_id"] = "STEP19_TestBox_A_01" }, 16f);
            accumulator.ObserveEvent("manipulation_pick_succeeded", null, 17.25f);
            accumulator.ObserveEvent("navigation_to_place_started", null, 18f);
            accumulator.ObserveEvent("navigation_to_place_succeeded", null, 24f);
            accumulator.ObserveEvent("manipulation_place_requested", new Dictionary<string, object> { ["object_id"] = "PlacePoint_A" }, 25f);
            accumulator.ObserveEvent("manipulation_place_succeeded", null, 26f);

            ExperimentTrialSummary summary = accumulator.BuildSummary(true, false, string.Empty, 27f, "end");

            Assert.That(summary.Success, Is.True);
            Assert.That(summary.Aborted, Is.False);
            Assert.That(summary.SelectedTargetId, Is.EqualTo("STEP19_TestBox_A_01"));
            Assert.That(summary.SelectedTargetCategory, Is.EqualTo("A"));
            Assert.That(summary.PlaceTargetId, Is.EqualTo("PlacePoint_A"));
            Assert.That(summary.TotalDurationSeconds, Is.EqualTo(17f).Within(0.001f));
            Assert.That(summary.NavigationToPickDurationSeconds, Is.EqualTo(4f).Within(0.001f));
            Assert.That(summary.PickDurationSeconds, Is.EqualTo(1.25f).Within(0.001f));
            Assert.That(summary.NavigationToPlaceDurationSeconds, Is.EqualTo(6f).Within(0.001f));
            Assert.That(summary.PlaceDurationSeconds, Is.EqualTo(1f).Within(0.001f));
            Assert.That(summary.ErrorCount, Is.EqualTo(0));
        }

        [Test]
        public void BuildSummary_Records_Failure_And_ErrorCount()
        {
            var accumulator = new ExperimentTrialMetricsAccumulator(Metadata(), 0f);

            accumulator.ObserveEvent("manipulation_place_failed", new Dictionary<string, object>
            {
                ["reason"] = "place_target_not_found"
            }, 5f);

            ExperimentTrialSummary summary = accumulator.BuildSummary(false, false, string.Empty, 6f, "end");

            Assert.That(summary.Success, Is.False);
            Assert.That(summary.ErrorCount, Is.EqualTo(1));
            Assert.That(summary.FailureReason, Is.EqualTo("place_target_not_found"));
        }

        [Test]
        public void BuildSummary_Classifies_PostPlaceEgress_As_NonTerminalWarning()
        {
            var accumulator = new ExperimentTrialMetricsAccumulator(Metadata(), 0f);

            accumulator.ObserveEvent("post_place_egress_failed", new Dictionary<string, object>
            {
                ["reason"] = "local_retreat_timeout"
            }, 5f);
            accumulator.ObserveEvent("post_place_egress_continue_with_warning", null, 6f);

            ExperimentTrialSummary summary = accumulator.BuildSummary(true, false, string.Empty, 7f, "end");

            Assert.That(summary.Success, Is.True);
            Assert.That(summary.ErrorCount, Is.EqualTo(0));
            Assert.That(summary.NonTerminalWarningCount, Is.EqualTo(2));
            Assert.That(summary.NonTerminalWarnings, Does.Contain("post_place_egress_failed"));
            Assert.That(summary.FailureReason, Is.Empty);
        }

        [Test]
        public void BuildSummary_Records_Aborted_State()
        {
            var accumulator = new ExperimentTrialMetricsAccumulator(Metadata(), 2f);

            ExperimentTrialSummary summary = accumulator.BuildSummary(false, true, "manual_abort_key", 4.5f, "end");

            Assert.That(summary.Success, Is.False);
            Assert.That(summary.Aborted, Is.True);
            Assert.That(summary.FailureReason, Is.EqualTo("manual_abort_key"));
            Assert.That(summary.TotalDurationSeconds, Is.EqualTo(2.5f).Within(0.001f));
        }

        private static ExperimentTrialMetadata Metadata()
        {
            var session = new ExperimentSessionMetadata(
                "P001",
                "S001",
                "C01",
                "PickAndPlace_A_to_ZoneA",
                "MultimodalSimulated",
                "Realistic",
                "Safe",
                "test_scene",
                "notes");
            return new ExperimentTrialMetadata(session, "run_001", "trial_001", 1, "start");
        }
    }
}
