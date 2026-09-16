using System;
using System.Collections.Generic;
using System.Reflection;
using Autonomy.BT.Core;
using Autonomy.Core;
using Autonomy.Domain;
using Autonomy.Integration;
using Autonomy.Provisioning;
using Autonomy.Services;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using UnityEngine;
using DomainVector3 = System.Numerics.Vector3;

namespace Autonomy.BT.Tests
{
    public sealed class P40AVoicePrePickReplacementTests
    {
        [Test]
        public void VoicePickAndPlace_ReplacesAutonomousTask_WhenPrePickAndNotHolding()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            MultimodalTaskIntent voiceIntent = VoiceIntent("new_box", "ZoneA", "voice_attempt_001");

            bool accepted = rig.Adapter.TrySubmitAutonomyRequest(
                voiceIntent,
                new TargetDescriptor("new_box", DomainVector3.One),
                new TargetDescriptor("ZoneA", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);

            Assert.That(accepted, Is.True);
            Assert.That(rig.Adapter.LastAutonomyRequestWasDeferred, Is.False);
            Assert.That(rig.RoundState.GetStatus("old_box"), Is.EqualTo(BoxRoundStatus.Pending));
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo("new_box"));
            Assert.That(rig.Adapter.ActiveP40Producer, Is.EqualTo("voice_command"));
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out TargetDescriptor current), Is.True);
            Assert.That(current.Id, Is.EqualTo("new_box"));
            Assert.That(rig.Navigation.StopCalled, Is.True);
        }

        [Test]
        public void VoicePickAndPlace_Defers_WhenHoldingObject()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));

            bool accepted = rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("new_box", "ZoneA", "voice_attempt_002"),
                new TargetDescriptor("new_box", DomainVector3.One),
                new TargetDescriptor("ZoneA", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);

            Assert.That(accepted, Is.True);
            Assert.That(rig.RoundState.GetStatus("old_box"), Is.EqualTo(BoxRoundStatus.Assigned));
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo("old_box"));
            Assert.That(rig.Adapter.HasPendingP40BVoiceOrder, Is.True);
            Assert.That(rig.Adapter.PendingP40BVoiceOrderTargetId, Is.EqualTo("new_box"));
            Assert.That(rig.Adapter.LastAutonomyRequestWasDeferred, Is.True);
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out TargetDescriptor currentTarget), Is.True);
            Assert.That(currentTarget.Id, Is.EqualTo("old_box"));
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out TargetDescriptor currentPlace), Is.True);
            Assert.That(currentPlace.Id, Is.EqualTo("ZoneA"));
            Assert.That(currentPlace.Position.X, Is.EqualTo(9f));
            Assert.That(rig.Navigation.StopCalled, Is.False);
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "p40b_voice_order_deferred_until_current_place_completed" &&
                PayloadValue(record, "pending_target_id") == "new_box" &&
                PayloadValue(record, "current_task_instance_id") == "old_task" &&
                PayloadValue(record, "held_object_id") == "old_box"));
        }

        [Test]
        public void DeferredVoiceOrder_IsPromoted_AfterCurrentPlaceCompleted()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("new_box", "ZoneA", "voice_attempt_promote"),
                new TargetDescriptor("new_box", DomainVector3.One),
                new TargetDescriptor("ZoneA", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);
            rig.ClearHeldObject();
            rig.CompleteAssignedTask("old_box");

            bool promoted = rig.Coordinator.TryPromotePendingP40BVoiceOrderBeforeAssistedSelection(intent =>
                rig.Adapter.TrySubmitAutonomyRequest(
                    intent,
                    new TargetDescriptor(intent.TargetId, DomainVector3.One),
                    new TargetDescriptor(intent.PlaceTargetId, new DomainVector3(2f, 0f, 0f)),
                    activateAutonomousMode: true));

            Assert.That(promoted, Is.True);
            Assert.That(rig.Adapter.HasPendingP40BVoiceOrder, Is.False);
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo("new_box"));
            Assert.That(rig.Adapter.ActiveP40Producer, Is.EqualTo("voice_command"));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "p40b_deferred_voice_order_promoted_after_place" &&
                PayloadValue(record, "pending_target_id") == "new_box" &&
                PayloadValue(record, "held_object_id") == string.Empty &&
                PayloadValue(record, "previous_held_object_id") == "old_box" &&
                PayloadValue(record, "completed_target_id") == "old_box"));
            Assert.That(rig.EventCount("p40b_deferred_voice_order_promoted_after_place"), Is.EqualTo(1));
        }

        [Test]
        public void DeferredVoiceOrder_IsNotPromoted_WhileCurrentPlaceStillActive()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("new_box", "ZoneA", "voice_attempt_too_early"),
                new TargetDescriptor("new_box", DomainVector3.One),
                new TargetDescriptor("ZoneA", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);
            int submitCount = 0;

            bool promoted = rig.Coordinator.TryPromotePendingP40BVoiceOrderBeforeAssistedSelection(_ =>
            {
                submitCount++;
                return true;
            });

            Assert.That(promoted, Is.False);
            Assert.That(submitCount, Is.EqualTo(0));
            Assert.That(rig.Adapter.HasPendingP40BVoiceOrder, Is.True);
            Assert.That(rig.Adapter.PendingP40BVoiceOrderTargetId, Is.EqualTo("new_box"));
            Assert.That(rig.EventCount("p40b_deferred_voice_order_promoted_after_place"), Is.EqualTo(0));
        }

        [Test]
        public void AutonomousSelection_DoesNotRunBeforeDeferredVoiceOrder()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("new_box", "ZoneA", "voice_attempt_priority"),
                new TargetDescriptor("new_box", DomainVector3.One),
                new TargetDescriptor("ZoneA", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);
            rig.ClearHeldObject();
            rig.CompleteAssignedTask("old_box");
            bool autonomousSelectionRan = false;

            bool promoted = rig.Coordinator.TryPromotePendingP40BVoiceOrderBeforeAssistedSelection(intent =>
            {
                if (autonomousSelectionRan)
                {
                    return false;
                }

                return rig.Adapter.TrySubmitAutonomyRequest(
                    intent,
                    new TargetDescriptor(intent.TargetId, DomainVector3.One),
                    new TargetDescriptor(intent.PlaceTargetId, new DomainVector3(2f, 0f, 0f)),
                    activateAutonomousMode: true);
            });

            Assert.That(promoted, Is.True);
            Assert.That(autonomousSelectionRan, Is.False);
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo("new_box"));
        }

        [Test]
        public void SecondVoiceOrder_IsRejected_WhenPendingAlreadyExists()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("new_box", "ZoneA", "voice_attempt_first"),
                new TargetDescriptor("new_box", DomainVector3.One),
                new TargetDescriptor("ZoneA", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);

            bool accepted = rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("third_box", "ZoneB", "voice_attempt_second"),
                new TargetDescriptor("third_box", new DomainVector3(3f, 0f, 0f)),
                new TargetDescriptor("ZoneB", new DomainVector3(4f, 0f, 0f)),
                activateAutonomousMode: true);

            Assert.That(accepted, Is.False);
            Assert.That(rig.Adapter.PendingP40BVoiceOrderTargetId, Is.EqualTo("new_box"));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "p40b_voice_order_defer_rejected" &&
                PayloadValue(record, "reason") == "pending_voice_order_already_exists" &&
                PayloadValue(record, "pending_target_id") == "new_box" &&
                PayloadValue(record, "existing_pending_target_id") == "new_box" &&
                PayloadValue(record, "rejected_target_id") == "third_box" &&
                PayloadValue(record, "rejected_destination") == "ZoneB"));
        }

        [Test]
        public void DeferredVoiceOrder_IsDiscarded_WhenTargetAlreadyDeposited()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("new_box", "ZoneA", "voice_attempt_discard"),
                new TargetDescriptor("new_box", DomainVector3.One),
                new TargetDescriptor("ZoneA", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);
            rig.RoundState.TryMarkCompleted("new_box");
            rig.ClearHeldObject();
            rig.CompleteAssignedTask("old_box");

            bool handled = rig.Coordinator.TryPromotePendingP40BVoiceOrderBeforeAssistedSelection(intent => true);

            Assert.That(handled, Is.True);
            Assert.That(rig.Adapter.HasPendingP40BVoiceOrder, Is.False);
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "p40b_deferred_voice_order_discarded" &&
                PayloadValue(record, "reason") == "target_already_deposited"));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "assisted_selection_reevaluation_scheduled" &&
                PayloadValue(record, "reason") == "pending_voice_order_discarded"));
        }

        [Test]
        public void VoicePickAndPlace_ToHeldCurrentBox_DefersThenDiscardsAfterPlace()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));

            bool accepted = rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("old_box", "ZoneA", "voice_attempt_current_held"),
                new TargetDescriptor("old_box", DomainVector3.One),
                new TargetDescriptor("ZoneA", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);

            Assert.That(accepted, Is.True);
            Assert.That(rig.Adapter.HasPendingP40BVoiceOrder, Is.True);
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "p40b_voice_order_deferred_until_current_place_completed" &&
                PayloadValue(record, "pending_target_id") == "old_box" &&
                PayloadValue(record, "current_target_id") == "old_box" &&
                PayloadValue(record, "held_object_id") == "old_box"));

            rig.ClearHeldObject();
            rig.CompleteAssignedTask("old_box");

            int submitCount = 0;
            bool handled = rig.Coordinator.TryPromotePendingP40BVoiceOrderBeforeAssistedSelection(_ =>
            {
                submitCount++;
                return true;
            });

            Assert.That(handled, Is.True);
            Assert.That(submitCount, Is.EqualTo(0));
            Assert.That(rig.Adapter.HasPendingP40BVoiceOrder, Is.False);
            Assert.That(rig.EventCount("p40b_deferred_voice_order_promoted_after_place"), Is.EqualTo(0));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "p40b_deferred_voice_order_discarded" &&
                PayloadValue(record, "pending_target_id") == "old_box" &&
                PayloadValue(record, "reason") == "target_already_deposited"));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "assisted_selection_reevaluation_scheduled" &&
                PayloadValue(record, "reason") == "pending_voice_order_discarded"));
        }

        [Test]
        public void BridgeGuard_AllowsVoiceTarget_WhenItIsCurrentAssignedHeldBox()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            using BridgeProbe probe = BridgeProbe.Create(rig.Adapter);

            bool allowed = probe.IsCurrentAssignedHeldVoiceTargetCandidate(VoiceIntent("old_box", "ZoneA", "voice_attempt_bridge_guard"));

            Assert.That(allowed, Is.True);
        }

        [Test]
        public void BridgeGuard_AllowsVoiceTarget_WhenBlackboardModeIdleButTaskInProgressAndHeldAssigned()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.SetBlackboardMode(RobotMode.Idle);
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            using BridgeProbe probe = BridgeProbe.Create(rig.Adapter);

            bool allowed = probe.IsCurrentAssignedHeldVoiceTargetCandidate(VoiceIntent("old_box", "ZoneA", "voice_attempt_idle_mode_bridge_guard"));

            Assert.That(allowed, Is.True);
        }

        [Test]
        public void BridgeGuard_DoesNotAllowUnavailableVoiceTarget_ThatIsNotCurrentAssignedOrHeld()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            using BridgeProbe probe = BridgeProbe.Create(rig.Adapter);

            bool allowed = probe.IsCurrentAssignedHeldVoiceTargetCandidate(VoiceIntent("missing_box", "ZoneA", "voice_attempt_missing_guard"));

            Assert.That(allowed, Is.False);
        }

        [Test]
        public void BridgeGuard_DoesNotAllowNonVoiceIntent()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            using BridgeProbe probe = BridgeProbe.Create(rig.Adapter);

            bool allowed = probe.IsCurrentAssignedHeldVoiceTargetCandidate(
                MultimodalTaskIntent.PickAndPlaceByTargetId("old_box", "ZoneA", "assisted_navmesh_selection"));

            Assert.That(allowed, Is.False);
        }

        [Test]
        public void BridgeGuard_DoesNotAllowUnresolvedSelfDestination()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            using BridgeProbe probe = BridgeProbe.Create(rig.Adapter);

            bool allowed = probe.IsCurrentAssignedHeldVoiceTargetCandidate(VoiceIntent("old_box", "SELF", "voice_attempt_self_guard"));

            Assert.That(allowed, Is.False);
        }

        [Test]
        public void VoicePickAndPlace_DoesNotReplace_WhenActiveTaskIsVoiceTask()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveVoiceTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");

            bool accepted = rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("new_box", "ZoneA", "voice_attempt_003"),
                new TargetDescriptor("new_box", DomainVector3.One),
                new TargetDescriptor("ZoneA", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);

            Assert.That(accepted, Is.False);
            Assert.That(rig.RoundState.GetStatus("old_box"), Is.EqualTo(BoxRoundStatus.Assigned));
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo("old_box"));
        }

        [Test]
        public void VoicePickAndPlace_DoesNotReplace_WhenDestinationUnresolved()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");

            bool accepted = rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("new_box", "SELF", "voice_attempt_004"),
                new TargetDescriptor("new_box", DomainVector3.One),
                new TargetDescriptor("SELF", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);

            Assert.That(accepted, Is.False);
            Assert.That(rig.RoundState.GetStatus("old_box"), Is.EqualTo(BoxRoundStatus.Assigned));
        }

        [Test]
        public void VoicePickAndPlace_ReleasesPreviousAssignment_OnlyIfNotHeldOrCompleted()
        {
            using TestRig rig = TestRig.Create();
            rig.MarkAssigned("old_box");
            rig.RoundState.TryMarkCompleted("old_box");

            bool released = rig.Coordinator.TryReleaseAssignedBoxForVoicePrePickReplacement(
                "old_box",
                "old_task",
                out string reason);

            Assert.That(released, Is.False);
            Assert.That(reason, Is.EqualTo("old_target_completed"));
            Assert.That(rig.RoundState.GetStatus("old_box"), Is.EqualTo(BoxRoundStatus.Completed));
        }

        [Test]
        public void AutonomyRequests_WithoutActiveTask_AreStillAccepted()
        {
            using TestRig rig = TestRig.Create(activeTask: false);

            bool accepted = rig.Adapter.TrySubmitAutonomyRequest(
                MultimodalTaskIntent.PickAndPlaceByTargetId("auto_box", "ZoneA", "assisted_navmesh_selection"),
                new TargetDescriptor("auto_box", DomainVector3.One),
                new TargetDescriptor("ZoneA", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);

            Assert.That(accepted, Is.True);
            Assert.That(rig.Adapter.ActiveP40Producer, Is.EqualTo("assisted_navmesh_selection"));
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo("auto_box"));
        }

        [Test]
        public void VoiceStop_CancelsAutonomousTask_BeforePick()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));

            VoiceStopCommandResult result = rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());

            Assert.That(result.Accepted, Is.True);
            Assert.That(result.Reason, Is.EqualTo("active_task_cancelled_by_voice_stop"));
            Assert.That(rig.Navigation.StopCalled, Is.True);
            Assert.That(rig.Manipulation.StopCalled, Is.True);
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out TargetDescriptor _), Is.False);
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out TargetDescriptor _), Is.False);
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out TaskStatus status), Is.True);
            Assert.That(status, Is.EqualTo(TaskStatus.None));
            Assert.That(rig.Blackboard.CurrentMode, Is.EqualTo(RobotMode.Idle));
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.Empty);
            Assert.That(rig.Adapter.VoiceStopLatched, Is.True);
            Assert.That(rig.Adapter.HasStoppedTaskSnapshotForVoiceResume, Is.True);
            Assert.That(rig.RoundState.GetStatus("old_box"), Is.EqualTo(BoxRoundStatus.Assigned));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record => record.EventType == "voice_stop_command_received"));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record => record.EventType == "voice_stop_command_accepted"));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record => record.EventType == "voice_stop_latch_activated"));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record => record.EventType == "voice_stop_active_task_cancellation_requested"));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_stop_navigation_manipulation_bt_cancellation_applied" &&
                PayloadValue(record, "previous_target_id") == "old_box" &&
                PayloadValue(record, "assignment_released") == "False" &&
                PayloadValue(record, "assignment_release_reason") == "preserved_for_supervisory_resume"));
        }

        [Test]
        public void VoiceStop_DuringNavigationTowardBox_StopsAndPreventsPick()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));

            VoiceStopCommandResult result = rig.Adapter.ApplyVoiceStopCommand("para robot", "para robot", VoiceCommandIntentKind.Stop.ToString());
            rig.ControllerTick();

            Assert.That(result.Accepted, Is.True);
            Assert.That(rig.Navigation.StopCalled, Is.True);
            Assert.That(rig.Manipulation.PickCalled, Is.False);
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out TargetDescriptor _), Is.False);
            Assert.That(rig.Blackboard.CurrentMode, Is.EqualTo(RobotMode.Idle));
        }

        [Test]
        public void VoiceStop_PrePickRepeatedAndTicks_BlockAutoResendThenResumeSameTask()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));

            VoiceStopCommandResult first = rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            VoiceStopCommandResult second = rig.Adapter.ApplyVoiceStopCommand("detente", "detente", VoiceCommandIntentKind.Stop.ToString());
            for (int tick = 0; tick < 5; tick++)
            {
                rig.ControllerTick();
            }

            bool resentAccepted = rig.Adapter.TrySubmitAutonomyRequest(
                MultimodalTaskIntent.PickAndPlaceByTargetId("auto_box", "ZoneA", "assisted_navmesh_selection"),
                new TargetDescriptor("auto_box", new DomainVector3(3f, 0f, 0f)),
                new TargetDescriptor("ZoneA", new DomainVector3(4f, 0f, 0f)),
                activateAutonomousMode: true);
            VoiceResumeCommandResult resume = rig.Adapter.ApplyVoiceResumeCommand("continua", "continua", VoiceCommandIntentKind.Resume.ToString());

            Assert.That(first.Reason, Is.EqualTo("active_task_cancelled_by_voice_stop"));
            Assert.That(second.Reason, Is.EqualTo("already_stopped_idempotent_noop"));
            Assert.That(second.FeedbackText, Is.EqualTo("Ya estoy parado."));
            Assert.That(resentAccepted, Is.False);
            Assert.That(rig.Adapter.LastAutonomyRequestRejectionReason, Is.EqualTo("voice_stop_latched_auto_task_blocked"));
            Assert.That(resume.Accepted, Is.True);
            Assert.That(resume.Reason, Is.EqualTo("pre_pick_task_restored"));
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo("old_box"));
            Assert.That(rig.Adapter.ActiveP40PlaceTargetId, Is.EqualTo("ZoneA"));
            Assert.That(rig.RoundState.GetStatus("old_box"), Is.EqualTo(BoxRoundStatus.Assigned));
            Assert.That(rig.EventCount("voice_resume_stopped_task_snapshot_created"), Is.EqualTo(1));
            Assert.That(rig.EventCount("voice_stop_navigation_manipulation_bt_cancellation_applied"), Is.EqualTo(1));
            Assert.That(rig.Events, Has.None.Matches<EventRecord>(record =>
                record.EventType == "autonomy_request_accepted_by_adapter" &&
                PayloadValue(record, "requested_target_id") == "auto_box"));
        }

        [Test]
        public void VoiceStop_FourTimes_OnlyFirstMutatesAndResumeConsumesOnce()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));

            VoiceStopCommandResult first = rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            VoiceStopCommandResult second = rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            VoiceStopCommandResult third = rig.Adapter.ApplyVoiceStopCommand("detente", "detente", VoiceCommandIntentKind.Stop.ToString());
            VoiceStopCommandResult fourth = rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            VoiceResumeCommandResult resumed = rig.Adapter.ApplyVoiceResumeCommand("retoma la tarea", "retoma la tarea", VoiceCommandIntentKind.Resume.ToString());
            VoiceResumeCommandResult repeatedResume = rig.Adapter.ApplyVoiceResumeCommand("continua", "continua", VoiceCommandIntentKind.Resume.ToString());

            Assert.That(first.Accepted, Is.True);
            Assert.That(new[] { second.Reason, third.Reason, fourth.Reason },
                Is.All.EqualTo("already_stopped_idempotent_noop"));
            Assert.That(rig.EventCount("voice_stop_latch_activated"), Is.EqualTo(1));
            Assert.That(rig.EventCount("voice_resume_stopped_task_snapshot_created"), Is.EqualTo(1));
            Assert.That(rig.EventCount("voice_stop_navigation_manipulation_bt_cancellation_applied"), Is.EqualTo(1));
            Assert.That(rig.EventCount("voice_stop_already_stopped_idempotent_noop"), Is.EqualTo(3));
            Assert.That(resumed.Accepted, Is.True);
            Assert.That(repeatedResume.Accepted, Is.False);
            Assert.That(repeatedResume.Reason, Is.EqualTo("no_stopped_task_to_resume"));
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo("old_box"));
        }

        [Test]
        public void VoiceStop_PreservesExistingP40BPendingVoiceOrder()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("new_box", "ZoneA", "voice_pending_before_stop"),
                new TargetDescriptor("new_box", DomainVector3.One),
                new TargetDescriptor("ZoneA", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);
            Assert.That(rig.Adapter.HasPendingP40BVoiceOrder, Is.True);

            VoiceStopCommandResult result = rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());

            Assert.That(result.Accepted, Is.True);
            Assert.That(result.Reason, Is.EqualTo("safe_stop_with_held_object"));
            Assert.That(rig.Adapter.HasPendingP40BVoiceOrder, Is.True);
            Assert.That(rig.Adapter.HasPausedPendingP40BVoiceOrder, Is.True);
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_stop_pending_voice_order_preserved" &&
                PayloadValue(record, "pending_target_id") == "new_box" &&
                PayloadValue(record, "pending_destination") == "ZoneA"));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_stop_pending_voice_order_paused" &&
                PayloadValue(record, "paused_pending_target_id") == "new_box"));
            Assert.That(rig.Events, Has.None.Matches<EventRecord>(record =>
                record.EventType == "voice_stop_pending_voice_order_cleared"));
        }

        [Test]
        public void VoiceStop_WithNoActiveTask_LeavesSystemStable()
        {
            using TestRig rig = TestRig.Create(activeTask: false);

            VoiceStopCommandResult result = rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            VoiceStopCommandResult repeated = rig.Adapter.ApplyVoiceStopCommand("detente", "detente", VoiceCommandIntentKind.Stop.ToString());
            VoiceResumeCommandResult resume = rig.Adapter.ApplyVoiceResumeCommand("continua", "continua", VoiceCommandIntentKind.Resume.ToString());

            Assert.That(result.Accepted, Is.True);
            Assert.That(result.Reason, Is.EqualTo("no_active_task_to_cancel"));
            Assert.That(repeated.Reason, Is.EqualTo("already_stopped_idempotent_noop"));
            Assert.That(resume.Accepted, Is.True);
            Assert.That(resume.Reason, Is.EqualTo("stop_latch_released_without_stopped_task"));
            Assert.That(rig.Navigation.StopCalled, Is.True);
            Assert.That(rig.Blackboard.CurrentMode, Is.EqualTo(RobotMode.Idle));
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out TargetDescriptor _), Is.False);
            Assert.That(rig.Adapter.HasPendingP40BVoiceOrder, Is.False);
            Assert.That(rig.Adapter.VoiceStopLatched, Is.False);
        }

        [Test]
        public void VoiceStop_WhileHoldingObject_StopsSafelyWithoutReleasingObjectOrContinuingPlace()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));

            VoiceStopCommandResult result = rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            rig.ControllerTick();

            Assert.That(result.Accepted, Is.True);
            Assert.That(result.Reason, Is.EqualTo("safe_stop_with_held_object"));
            Assert.That(rig.Adapter.HeldObjectId, Is.EqualTo("old_box"));
            Assert.That(rig.Adapter.VoiceStopLatched, Is.True);
            Assert.That(rig.Adapter.HasStoppedTaskSnapshotForVoiceResume, Is.True);
            Assert.That(rig.Navigation.StopCalled, Is.True);
            Assert.That(rig.Manipulation.PlaceCalled, Is.False);
            Assert.That(rig.RoundState.GetStatus("old_box"), Is.EqualTo(BoxRoundStatus.Assigned));
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out TargetDescriptor _), Is.False);
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_stop_navigation_manipulation_bt_cancellation_applied" &&
                PayloadValue(record, "reason") == "safe_stop_with_held_object" &&
                PayloadValue(record, "held_object_id") == "old_box"));
        }

        [Test]
        public void VoiceResume_AfterStopBeforePick_RestoresSameTargetAndDestination()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));

            VoiceStopCommandResult stop = rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            Assert.That(rig.Adapter.VoiceStopLatched, Is.True);
            VoiceResumeCommandResult resume = rig.Adapter.ApplyVoiceResumeCommand("reanuda la tarea", "reanuda la tarea", VoiceCommandIntentKind.Resume.ToString());

            Assert.That(stop.Accepted, Is.True);
            Assert.That(resume.Accepted, Is.True);
            Assert.That(resume.Reason, Is.EqualTo("pre_pick_task_restored"));
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo("old_box"));
            Assert.That(rig.Adapter.ActiveP40PlaceTargetId, Is.EqualTo("ZoneA"));
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out TargetDescriptor current), Is.True);
            Assert.That(current.Id, Is.EqualTo("old_box"));
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out TargetDescriptor place), Is.True);
            Assert.That(place.Id, Is.EqualTo("ZoneA"));
            Assert.That(rig.Blackboard.CurrentMode, Is.EqualTo(RobotMode.Autonomous));
            Assert.That(rig.Adapter.HasStoppedTaskSnapshotForVoiceResume, Is.False);
            Assert.That(rig.Adapter.VoiceStopLatched, Is.False);
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record => record.EventType == "voice_stop_latch_released"));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record => record.EventType == "voice_resume_stopped_task_snapshot_created"));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_resume_pre_pick_task_restored" &&
                PayloadValue(record, "snapshot_target_id") == "old_box" &&
                PayloadValue(record, "snapshot_place_target_id") == "ZoneA"));
        }

        [Test]
        public void VoiceStopLatch_BlocksAssistedSelection_AndPreservesSnapshot()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));

            rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            bool accepted = rig.Adapter.TrySubmitAutonomyRequest(
                MultimodalTaskIntent.PickAndPlaceByTargetId("auto_box", "ZoneA", "assisted_navmesh_selection"),
                new TargetDescriptor("auto_box", new DomainVector3(3f, 0f, 0f)),
                new TargetDescriptor("ZoneA", new DomainVector3(4f, 0f, 0f)),
                activateAutonomousMode: true);
            rig.ControllerTick();

            Assert.That(accepted, Is.False);
            Assert.That(rig.Adapter.LastAutonomyRequestRejectionReason, Is.EqualTo("voice_stop_latched_auto_task_blocked"));
            Assert.That(rig.Adapter.VoiceStopLatched, Is.True);
            Assert.That(rig.Adapter.HasStoppedTaskSnapshotForVoiceResume, Is.True);
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.Empty);
            Assert.That(rig.Blackboard.CurrentMode, Is.EqualTo(RobotMode.Idle));
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out TargetDescriptor _), Is.False);
            Assert.That(rig.Manipulation.PickCalled, Is.False);
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_stop_latched_auto_task_blocked" &&
                PayloadValue(record, "requested_producer") == "assisted_navmesh_selection" &&
                PayloadValue(record, "snapshot_target_id") == "old_box"));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_resume_snapshot_preserved_after_auto_task_block" &&
                PayloadValue(record, "snapshot_target_id") == "old_box"));
            Assert.That(rig.Events, Has.None.Matches<EventRecord>(record =>
                record.EventType == "voice_resume_stopped_task_snapshot_invalidated" &&
                PayloadValue(record, "reason") == "new_task_accepted"));
        }

        [Test]
        public void VoiceStopLatch_BlocksCoordinatorSelection_NeutralForRoundState()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));

            rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            InvokePrivate(rig.Coordinator, "TryLaunchAssistedSelection");

            Assert.That(rig.RoundState.GetStatus("old_box"), Is.EqualTo(BoxRoundStatus.Assigned));
            Assert.That(rig.RoundState.GetStatus("auto_box"), Is.EqualTo(BoxRoundStatus.Pending));
            Assert.That(rig.RoundState.ExcludedCount, Is.Zero);
            Assert.That(rig.RoundState.RoundCompleted, Is.False);
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_stop_latched_auto_selection_blocked_neutral" &&
                PayloadValue(record, "excluded_count") == "0" &&
                PayloadValue(record, "round_completed") == "False"));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_stop_latch_no_box_state_mutation" &&
                PayloadValue(record, "box_state_mutation") == "False"));
            Assert.That(rig.Events, Has.None.Matches<EventRecord>(record =>
                record.EventType == "assisted_round_completed" ||
                record.EventType == "assisted_round_incomplete"));
            Assert.That(rig.Events, Has.None.Matches<EventRecord>(record =>
                record.EventType == "robot_controller_task_reset_for_new_request" ||
                record.EventType == "autonomy_request_accepted_by_adapter" ||
                record.EventType == "robot_controller_task_start_requested"));
        }

        [Test]
        public void VoiceResume_AfterBlockedAssistedSelection_StillRestoresStoppedTask()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));

            rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            rig.Adapter.TrySubmitAutonomyRequest(
                MultimodalTaskIntent.PickAndPlaceByTargetId("auto_box", "ZoneA", "assisted_navmesh_selection"),
                new TargetDescriptor("auto_box", new DomainVector3(3f, 0f, 0f)),
                new TargetDescriptor("ZoneA", new DomainVector3(4f, 0f, 0f)),
                activateAutonomousMode: true);
            VoiceResumeCommandResult resume = rig.Adapter.ApplyVoiceResumeCommand("reanuda la tarea", "reanuda la tarea", VoiceCommandIntentKind.Resume.ToString());

            Assert.That(resume.Accepted, Is.True);
            Assert.That(rig.Adapter.VoiceStopLatched, Is.False);
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo("old_box"));
            Assert.That(rig.Adapter.ActiveP40PlaceTargetId, Is.EqualTo("ZoneA"));
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.Not.EqualTo("auto_box"));
        }

        [Test]
        public void VoiceResume_AfterStopDuringNavigation_DoesNotInvokeAutonomousFallback()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));

            rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            VoiceResumeCommandResult resume = rig.Adapter.ApplyVoiceResumeCommand("retoma la tarea actual", "retoma la tarea actual", VoiceCommandIntentKind.Resume.ToString());

            Assert.That(resume.Accepted, Is.True);
            Assert.That(rig.Adapter.ActiveP40Producer, Is.EqualTo("assisted_navmesh_selection"));
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo("old_box"));
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.Not.EqualTo("auto_box"));
            Assert.That(rig.Events, Has.None.Matches<EventRecord>(record =>
                record.EventType == "autonomy_request_accepted_by_adapter" &&
                PayloadValue(record, "requested_target_id") == "auto_box"));
        }

        [Test]
        public void VoiceResume_WithoutPreviousStop_IsRejected()
        {
            using TestRig rig = TestRig.Create(activeTask: false);

            VoiceResumeCommandResult resume = rig.Adapter.ApplyVoiceResumeCommand("reanuda", "reanuda", VoiceCommandIntentKind.Resume.ToString());

            Assert.That(resume.Accepted, Is.False);
            Assert.That(resume.Reason, Is.EqualTo("no_stopped_task_to_resume"));
            Assert.That(resume.FeedbackText, Is.EqualTo("No hay ninguna tarea detenida que pueda reanudar."));
            Assert.That(rig.Blackboard.CurrentMode, Is.EqualTo(RobotMode.Idle));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_resume_command_rejected" &&
                PayloadValue(record, "reason") == "no_stopped_task_to_resume"));
        }

        [Test]
        public void VoicePickAndPlace_DuringStop_IsRejectedAndDoesNotReplaceSnapshot()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));
            rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());

            bool newAccepted = rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("new_box", "ZoneA", "voice_new_after_stop"),
                new TargetDescriptor("new_box", DomainVector3.One),
                new TargetDescriptor("ZoneA", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);
            VoiceResumeCommandResult resume = rig.Adapter.ApplyVoiceResumeCommand("reanuda", "reanuda", VoiceCommandIntentKind.Resume.ToString());

            Assert.That(newAccepted, Is.False);
            Assert.That(rig.Adapter.LastAutonomyRequestRejectionReason, Is.EqualTo("voice_stop_latched_pick_and_place_rejected"));
            Assert.That(resume.Accepted, Is.True);
            Assert.That(resume.Reason, Is.EqualTo("pre_pick_task_restored"));
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo("old_box"));
            Assert.That(rig.Adapter.VoiceStopLatched, Is.False);
            Assert.That(rig.Events, Has.None.Matches<EventRecord>(record =>
                record.EventType == "autonomy_request_accepted_by_adapter" &&
                PayloadValue(record, "requested_target_id") == "new_box"));
        }

        [Test]
        public void VoiceResume_AfterStopWithPendingP40B_RestoresStoppedTaskFirstAndKeepsPending()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("new_box", "ZoneA", "voice_pending_before_stop_resume"),
                new TargetDescriptor("new_box", DomainVector3.One),
                new TargetDescriptor("ZoneA", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);
            Assert.That(rig.Adapter.HasPendingP40BVoiceOrder, Is.True);

            rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            VoiceResumeCommandResult resume = rig.Adapter.ApplyVoiceResumeCommand("sigue con la tarea", "sigue con la tarea", VoiceCommandIntentKind.Resume.ToString());

            Assert.That(resume.Accepted, Is.True);
            Assert.That(rig.Adapter.HasPendingP40BVoiceOrder, Is.True);
            Assert.That(rig.Adapter.HasPausedPendingP40BVoiceOrder, Is.False);
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo("old_box"));
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.Not.EqualTo("new_box"));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_resume_pending_voice_order_restored" &&
                PayloadValue(record, "pending_target_id") == "new_box"));
        }

        [Test]
        public void VoiceStop_WithPendingVoiceOrderAndNoActiveTask_PreservesPendingAndResumePromotes()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("new_box", "ZoneA", "voice_pending_no_active"),
                new TargetDescriptor("new_box", DomainVector3.One),
                new TargetDescriptor("ZoneA", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);
            rig.ClearActiveTaskForPendingOnly();

            VoiceStopCommandResult stop = rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            VoiceResumeCommandResult resume = rig.Adapter.ApplyVoiceResumeCommand("retoma la tarea", "retoma la tarea", VoiceCommandIntentKind.Resume.ToString());

            Assert.That(stop.Accepted, Is.True);
            Assert.That(stop.Reason, Is.EqualTo("pending_voice_order_paused_by_stop"));
            Assert.That(resume.Accepted, Is.True);
            Assert.That(resume.Reason, Is.EqualTo("pending_voice_order_promoted"));
            Assert.That(rig.Adapter.HasPendingP40BVoiceOrder, Is.False);
            Assert.That(rig.Adapter.HasPausedPendingP40BVoiceOrder, Is.False);
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo("new_box"));
            Assert.That(rig.Adapter.ActiveP40PlaceTargetId, Is.EqualTo("ZoneA"));
            Assert.That(rig.Adapter.VoiceStopLatched, Is.False);
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_stop_pending_voice_order_preserved" &&
                PayloadValue(record, "pending_target_id") == "new_box"));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_resume_pending_voice_order_promoted" &&
                PayloadValue(record, "target_id") == "new_box" &&
                PayloadValue(record, "destination_id") == "ZoneA"));
        }

        [TestCase("round003_box02_C", "ZoneC")]
        [TestCase("round003_box04_B", "ZoneB")]
        public void VoiceResume_WithPausedPendingSelfResolvedOrder_PromotesOriginalTargetAndDestination(string targetId, string destinationId)
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent(targetId, destinationId, $"voice_pending_{targetId}"),
                new TargetDescriptor(targetId, DomainVector3.One),
                new TargetDescriptor(destinationId, new DomainVector3(4f, 0f, 0f)),
                activateAutonomousMode: true);
            rig.ClearActiveTaskForPendingOnly();

            rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            VoiceResumeCommandResult resume = rig.Adapter.ApplyVoiceResumeCommand("retoma la tarea", "retoma la tarea", VoiceCommandIntentKind.Resume.ToString());

            Assert.That(resume.Accepted, Is.True);
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo(targetId));
            Assert.That(rig.Adapter.ActiveP40PlaceTargetId, Is.EqualTo(destinationId));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_resume_pending_voice_order_promoted" &&
                PayloadValue(record, "target_id") == targetId &&
                PayloadValue(record, "destination_id") == destinationId));
        }

        [Test]
        public void CancelPendingVoiceOrder_ClearsPendingOnlyOnExplicitCancel()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("new_box", "ZoneA", "voice_pending_explicit_cancel"),
                new TargetDescriptor("new_box", DomainVector3.One),
                new TargetDescriptor("ZoneA", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);

            bool cancelled = rig.Adapter.CancelPendingVoiceOrder(
                "cancela la orden pendiente",
                "cancela la orden pendiente",
                VoiceCommandIntentKind.CancelPendingOrder.ToString());

            Assert.That(cancelled, Is.True);
            Assert.That(rig.Adapter.HasPendingP40BVoiceOrder, Is.False);
            Assert.That(rig.Adapter.HasPausedPendingP40BVoiceOrder, Is.False);
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_stop_pending_voice_order_cleared" &&
                PayloadValue(record, "cleared_pending_target_id") == "new_box" &&
                PayloadValue(record, "cleared_pending_destination") == "ZoneA"));
        }

        [Test]
        public void PausedPendingVoiceOrder_BlocksAutonomousSelectionUntilResumeOrCancel()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("new_box", "ZoneA", "voice_pending_blocks_auto"),
                new TargetDescriptor("new_box", DomainVector3.One),
                new TargetDescriptor("ZoneA", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);
            rig.ClearActiveTaskForPendingOnly();
            rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());

            bool accepted = rig.Adapter.TrySubmitAutonomyRequest(
                MultimodalTaskIntent.PickAndPlaceByTargetId("auto_box", "ZoneA", "assisted_navmesh_selection"),
                new TargetDescriptor("auto_box", new DomainVector3(3f, 0f, 0f)),
                new TargetDescriptor("ZoneA", new DomainVector3(4f, 0f, 0f)),
                activateAutonomousMode: true);

            Assert.That(accepted, Is.False);
            Assert.That(rig.Adapter.LastAutonomyRequestRejectionReason, Is.EqualTo("voice_stop_latched_auto_task_blocked"));
            Assert.That(rig.Adapter.HasPausedPendingP40BVoiceOrder, Is.True);
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.Empty);
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_stop_latched_auto_task_blocked" &&
                PayloadValue(record, "paused_pending_target_id") == "new_box"));
        }

        [Test]
        public void PausedPendingVoiceOrder_RejectsSecondVoiceOrderWithoutConsumingTaskSlot()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("new_box", "ZoneA", "voice_pending_blocks_second"),
                new TargetDescriptor("new_box", DomainVector3.One),
                new TargetDescriptor("ZoneA", new DomainVector3(2f, 0f, 0f)),
                activateAutonomousMode: true);
            rig.ClearActiveTaskForPendingOnly();
            rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());

            bool accepted = rig.Adapter.TrySubmitAutonomyRequest(
                VoiceIntent("third_box", "ZoneB", "voice_second_while_paused_pending"),
                new TargetDescriptor("third_box", new DomainVector3(3f, 0f, 0f)),
                new TargetDescriptor("ZoneB", new DomainVector3(4f, 0f, 0f)),
                activateAutonomousMode: true);

            Assert.That(accepted, Is.False);
            Assert.That(rig.Adapter.LastAutonomyRequestRejectionReason, Is.EqualTo("voice_stop_latched_pick_and_place_rejected"));
            Assert.That(rig.Adapter.HasPendingP40BVoiceOrder, Is.True);
            Assert.That(rig.Adapter.PendingP40BVoiceOrderTargetId, Is.EqualTo("new_box"));
            Assert.That(rig.Adapter.HasPausedPendingP40BVoiceOrder, Is.True);
        }

        [Test]
        public void VoiceResume_AfterStopWhileHolding_ContinuesPlaceWithHeldObject()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));

            VoiceStopCommandResult stop = rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            VoiceStopCommandResult repeatedStop = rig.Adapter.ApplyVoiceStopCommand("detente", "detente", VoiceCommandIntentKind.Stop.ToString());
            VoiceResumeCommandResult resume = rig.Adapter.ApplyVoiceResumeCommand("retoma la tarea actual", "retoma la tarea actual", VoiceCommandIntentKind.Resume.ToString());

            Assert.That(stop.Accepted, Is.True);
            Assert.That(repeatedStop.Accepted, Is.True);
            Assert.That(repeatedStop.Reason, Is.EqualTo("already_stopped_idempotent_noop"));
            Assert.That(repeatedStop.FeedbackText, Is.EqualTo("Ya estoy parado."));
            Assert.That(resume.Accepted, Is.True);
            Assert.That(resume.Reason, Is.EqualTo("holding_place_task_continued"));
            Assert.That(rig.Adapter.VoiceStopLatched, Is.False);
            Assert.That(rig.Adapter.HeldObjectId, Is.EqualTo("old_box"));
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.ResumeHeldObjectId, out string resumeHeldObjectId), Is.True);
            Assert.That(resumeHeldObjectId, Is.EqualTo("old_box"));
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out TargetDescriptor current), Is.True);
            Assert.That(current.Id, Is.EqualTo("old_box"));
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out TargetDescriptor place), Is.True);
            Assert.That(place.Id, Is.EqualTo("ZoneA"));
            Assert.That(rig.Manipulation.PickCalled, Is.False);
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_resume_holding_place_task_continued" &&
                PayloadValue(record, "snapshot_held_object_id") == "old_box"));
            Assert.That(rig.EventCount("voice_resume_stopped_task_snapshot_created"), Is.EqualTo(1));
            Assert.That(rig.EventCount("voice_stop_navigation_manipulation_bt_cancellation_applied"), Is.EqualTo(1));
        }

        [Test]
        public void VoiceStop_CleanupClearsLatchAndSnapshot_ThenNextRoundRequestStartsNormally()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));

            rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            rig.Adapter.ClearExperimentRuntimeState("test_round_cleanup");
            bool nextRoundAccepted = rig.Adapter.TrySubmitAutonomyRequest(
                MultimodalTaskIntent.PickAndPlaceByTargetId("auto_box", "ZoneA", "assisted_navmesh_selection"),
                new TargetDescriptor("auto_box", new DomainVector3(3f, 0f, 0f)),
                new TargetDescriptor("ZoneA", new DomainVector3(4f, 0f, 0f)),
                activateAutonomousMode: true);

            Assert.That(rig.Adapter.VoiceStopLatched, Is.False);
            Assert.That(rig.Adapter.HasStoppedTaskSnapshotForVoiceResume, Is.False);
            Assert.That(nextRoundAccepted, Is.True);
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo("auto_box"));
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_stop_latch_released" &&
                PayloadValue(record, "reason") == "test_round_cleanup"));
        }

        [Test]
        public void VoiceResume_AfterStopWhileHolding_CompletesWithNonBusyCleanup()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));

            rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            VoiceResumeCommandResult resume = rig.Adapter.ApplyVoiceResumeCommand("reanuda la tarea", "reanuda la tarea", VoiceCommandIntentKind.Resume.ToString());
            rig.Blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.Succeeded);
            rig.SetBlackboardMode(RobotMode.Autonomous);
            InvokePrivate(rig.Adapter, "ObserveP40TaskLifecycle");

            Assert.That(resume.Accepted, Is.True);
            Assert.That(rig.Adapter.ActiveP40TargetId, Is.Empty);
            Assert.That(rig.Adapter.ActiveP40PlaceTargetId, Is.Empty);
            Assert.That(rig.Blackboard.CurrentMode, Is.EqualTo(RobotMode.Idle));
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out TargetDescriptor _), Is.False);
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out TargetDescriptor _), Is.False);
            Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.ResumeHeldObjectId, out string _), Is.False);
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_resume_post_completion_cleanup_verified" &&
                PayloadValue(record, "current_target_cleared") == "True" &&
                PayloadValue(record, "place_target_cleared") == "True"));
        }

        [Test]
        public void VoiceResume_WithInconsistentHeldSnapshot_ReleasesLatchAsUnsafeAndRejects()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));

            rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            rig.ClearHeldObject();
            VoiceResumeCommandResult resume = rig.Adapter.ApplyVoiceResumeCommand("reanuda", "reanuda", VoiceCommandIntentKind.Resume.ToString());

            Assert.That(resume.Accepted, Is.False);
            Assert.That(resume.Reason, Is.EqualTo("stopped_task_held_object_inconsistent"));
            Assert.That(resume.FeedbackText, Is.EqualTo("No puedo reanudar la tarea detenida porque el estado ya no es seguro."));
            Assert.That(rig.Adapter.VoiceStopLatched, Is.False);
            Assert.That(rig.Adapter.HasStoppedTaskSnapshotForVoiceResume, Is.False);
            Assert.That(rig.Events, Has.Some.Matches<EventRecord>(record =>
                record.EventType == "voice_stop_latch_released" &&
                PayloadValue(record, "reason") == "stopped_task_held_object_inconsistent"));
        }

        [Test]
        public void ExperimentPause_BlocksAutonomyRequestWithoutReplacingActiveTask_ThenResumesSameState()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));
            var pauseHost = new GameObject("p46l_adapter_pause_test");
            ExperimentRuntimePauseCoordinator pause = ExperimentRuntimePauseCoordinator.EnsureFor(pauseHost);
            try
            {
                Assert.That(pause.EnterPause("test_navigation"), Is.True);
                bool accepted = rig.Adapter.TrySubmitAutonomyRequest(
                    VoiceIntent("new_box", "ZoneA", "paused_request"),
                    new TargetDescriptor("new_box", DomainVector3.One * 2f),
                    new TargetDescriptor("ZoneA", DomainVector3.One * 3f),
                    activateAutonomousMode: true);

                Assert.That(accepted, Is.False);
                Assert.That(rig.Adapter.LastAutonomyRequestRejectionReason, Is.EqualTo("experiment_paused_autonomy_request_rejected"));
                Assert.That(rig.Adapter.ActiveP40TaskInstanceId, Is.EqualTo("old_task"));
                Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo("old_box"));
                Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out TargetDescriptor current), Is.True);
                Assert.That(current.Id, Is.EqualTo("old_box"));
                Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out TargetDescriptor place), Is.True);
                Assert.That(place.Position.X, Is.EqualTo(9f));

                Assert.That(pause.ResumeFromPause("test_navigation_continue"), Is.True);
                Assert.That(rig.Adapter.ActiveP40TaskInstanceId, Is.EqualTo("old_task"));
                Assert.That(rig.Adapter.ActiveP40TargetId, Is.EqualTo("old_box"));
                Assert.That(rig.Blackboard.CurrentMode, Is.EqualTo(RobotMode.Autonomous));
            }
            finally
            {
                pause.CleanupPauseState("test_cleanup");
                UnityEngine.Object.DestroyImmediate(pauseHost);
            }
        }

        [Test]
        public void ExperimentPause_WithHeldObject_PreservesAttachmentAndPlaceContext()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));
            SetPrivateField(rig.Adapter, "_tiagoUnityManipulationService", CreateHoldingManipulationService("old_box"));
            var pauseHost = new GameObject("p46l_held_pause_test");
            ExperimentRuntimePauseCoordinator pause = ExperimentRuntimePauseCoordinator.EnsureFor(pauseHost);
            try
            {
                pause.EnterPause("test_held_object");
                Assert.That(rig.Adapter.HeldObjectId, Is.EqualTo("old_box"));
                Assert.That(rig.Adapter.ActiveP40PlaceTargetId, Is.EqualTo("ZoneA"));
                Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out TargetDescriptor placeDuringPause), Is.True);
                Assert.That(placeDuringPause.Position.X, Is.EqualTo(9f));

                pause.ResumeFromPause("test_held_object_continue");
                Assert.That(rig.Adapter.HeldObjectId, Is.EqualTo("old_box"));
                Assert.That(rig.Adapter.ActiveP40TaskInstanceId, Is.EqualTo("old_task"));
                Assert.That(rig.Blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out TargetDescriptor placeAfterResume), Is.True);
                Assert.That(placeAfterResume.Position.X, Is.EqualTo(9f));
            }
            finally
            {
                pause.CleanupPauseState("test_cleanup");
                UnityEngine.Object.DestroyImmediate(pauseHost);
            }
        }

        [Test]
        public void ExperimentPause_DoesNotConsumePreExistingVoiceStopLatchOrSnapshot()
        {
            using TestRig rig = TestRig.Create();
            rig.SetActiveAssistedTask("old_box", "old_task", "old_req");
            rig.MarkAssigned("old_box");
            rig.Blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("old_box", DomainVector3.One));
            rig.Blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", new DomainVector3(9f, 0f, 0f)));
            VoiceStopCommandResult stopped = rig.Adapter.ApplyVoiceStopCommand("para", "para", VoiceCommandIntentKind.Stop.ToString());
            Assert.That(stopped.Accepted, Is.True);
            Assert.That(rig.Adapter.VoiceStopLatched, Is.True);
            Assert.That(rig.Adapter.HasStoppedTaskSnapshotForVoiceResume, Is.True);

            var pauseHost = new GameObject("p46l_voice_latch_pause_test");
            ExperimentRuntimePauseCoordinator pause = ExperimentRuntimePauseCoordinator.EnsureFor(pauseHost);
            try
            {
                pause.EnterPause("test_voice_latch");
                pause.ResumeFromPause("test_voice_latch_continue");

                Assert.That(rig.Adapter.VoiceStopLatched, Is.True);
                Assert.That(rig.Adapter.HasStoppedTaskSnapshotForVoiceResume, Is.True);
                VoiceResumeCommandResult resumed = rig.Adapter.ApplyVoiceResumeCommand("continua", "continua", VoiceCommandIntentKind.Resume.ToString());
                Assert.That(resumed.Accepted, Is.True);
                Assert.That(rig.Adapter.VoiceStopLatched, Is.False);
                Assert.That(rig.Adapter.ActiveP40TaskInstanceId, Is.EqualTo("old_task"));
            }
            finally
            {
                pause.CleanupPauseState("test_cleanup");
                UnityEngine.Object.DestroyImmediate(pauseHost);
            }
        }

        private static MultimodalTaskIntent VoiceIntent(string targetId, string placeTargetId, string voiceInteractionId)
        {
            var intent = MultimodalTaskIntent.PickAndPlaceByTargetId(targetId, placeTargetId, "voice_command");
            P40TraceContext.RegisterIntent(
                intent,
                new P40TraceMetadata
                {
                    Producer = "voice_command",
                    VoiceInteractionId = voiceInteractionId,
                    TargetAlias = targetId,
                    ResolvedDestination = placeTargetId,
                    SubmittedDestination = placeTargetId
                });
            return intent;
        }

        private static void SetPrivateField(object instance, string fieldName, object value)
        {
            instance.GetType()
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(instance, value);
        }

        private static object InvokePrivate(object instance, string methodName, params object[] arguments)
        {
            return instance.GetType()
                .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(instance, arguments);
        }

        private static TiagoUnityManipulationService CreateHoldingManipulationService(string heldObjectId)
        {
            var host = new GameObject(heldObjectId);
            var service = new TiagoUnityManipulationService(host.transform, host.transform, 1f, Vector3.zero);
            Type heldStateType = typeof(TiagoUnityManipulationService).GetNestedType("HeldObjectState", BindingFlags.NonPublic);
            object heldState = Activator.CreateInstance(heldStateType);
            heldStateType.GetField("GameObject").SetValue(heldState, host);
            heldStateType.GetField("ObjectId").SetValue(heldState, heldObjectId);
            typeof(TiagoUnityManipulationService)
                .GetField("_heldObject", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(service, heldState);
            return service;
        }

        private static string PayloadValue(EventRecord record, string key)
        {
            return record.Payload.TryGetValue(key, out object value) && value != null ? value.ToString() : string.Empty;
        }

        private sealed class TestRig : IDisposable
        {
            private readonly GameObject _adapterHost;
            private readonly GameObject _coordinatorHost;
            private GameObject _emptyGripperHost;
            private GameObject _emptyNavigationHost;
            private readonly RobotFSM _fsm;
            private readonly RobotController _controller;

            public AutonomousRobotAdapter Adapter { get; }
            public RobotAssistanceRoundCoordinator Coordinator { get; }
            public RobotBlackboard Blackboard { get; }
            public NavigationSpy Navigation { get; }
            public ManipulationSpy Manipulation { get; }
            public BoxRoundState RoundState { get; }
            public List<EventRecord> Events { get; } = new();

            private TestRig(bool activeTask)
            {
                TiagoExperimentTelemetry.StructuredEventLogged += CaptureEvent;
                _adapterHost = new GameObject("p40a_adapter_test");
                _coordinatorHost = new GameObject("p40a_coordinator_test");
                Adapter = _adapterHost.AddComponent<AutonomousRobotAdapter>();
                Coordinator = _coordinatorHost.AddComponent<RobotAssistanceRoundCoordinator>();
                Blackboard = new RobotBlackboard();
                _fsm = new RobotFSM();
                Navigation = new NavigationSpy();
                Manipulation = new ManipulationSpy();
                _controller = new RobotController(
                    _fsm,
                    Blackboard,
                    new BehaviorTreeRunner(new RunningNode()),
                    Navigation,
                    Manipulation,
                    new AlwaysSafeService());

                SetPrivateField(Adapter, "_blackboard", Blackboard);
                SetPrivateField(Adapter, "_fsm", _fsm);
                SetPrivateField(Adapter, "_robotController", _controller);
                SetPrivateField(Adapter, "_targetSeeder", new ManualTargetProvisioner(Blackboard));
                SetPrivateField(Adapter, "_placeTargetSeeder", new PlaceTargetProvisioner(Blackboard));
                SetPrivateField(Adapter, "_assistanceRoundCoordinator", Coordinator);
                SetPrivateField(Coordinator, "_robotAdapter", Adapter);

                RoundState = (BoxRoundState)typeof(RobotAssistanceRoundCoordinator)
                    .GetField("_roundState", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(Coordinator);
                RoundState.Initialize(new[]
                {
                    new BoxRoundItem("old_box", "A", "ZoneA", Vector3.zero, Vector3.one),
                    new BoxRoundItem("new_box", "A", "ZoneA", Vector3.one, Vector3.one * 2f),
                    new BoxRoundItem("third_box", "B", "ZoneB", Vector3.one * 5f, Vector3.one * 6f),
                    new BoxRoundItem("auto_box", "A", "ZoneA", Vector3.one * 3f, Vector3.one * 4f),
                    new BoxRoundItem("round003_box02_C", "C", "ZoneC", Vector3.one * 7f, Vector3.one * 8f),
                    new BoxRoundItem("round003_box04_B", "B", "ZoneB", Vector3.one * 9f, Vector3.one * 10f)
                });

                if (activeTask)
                {
                    _fsm.TryChangeMode(RobotMode.Autonomous);
                    Blackboard.SetMode(RobotMode.Autonomous);
                    Blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.InProgress);
                }
            }

            public static TestRig Create(bool activeTask = true) => new(activeTask);

            public void SetActiveAssistedTask(string targetId, string taskId, string requestId)
            {
                SetActiveTask(targetId, taskId, requestId, "assisted_navmesh_selection");
            }

            public void SetActiveVoiceTask(string targetId, string taskId, string requestId)
            {
                SetActiveTask(targetId, taskId, requestId, "voice_command");
            }

            public void MarkAssigned(string boxId)
            {
                RoundState.TryMarkAssigned(boxId);
                SetPrivateField(Coordinator, "_assignedBoxId", boxId);
            }

            private void SetActiveTask(string targetId, string taskId, string requestId, string producer)
            {
                SetPrivateField(Adapter, "_activeP40TaskInstanceId", taskId);
                SetPrivateField(Adapter, "_activeP40RequestId", requestId);
                SetPrivateField(Adapter, "_activeP40Producer", producer);
                SetPrivateField(Adapter, "_activeP40TargetId", targetId);
                SetPrivateField(Adapter, "_activeP40PlaceTargetId", "ZoneA");
            }

            public void CompleteAssignedTask(string boxId)
            {
                RoundState.TryMarkCompleted(boxId);
                Blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.Succeeded);
                _fsm.TryChangeMode(RobotMode.Idle);
                Blackboard.SetMode(RobotMode.Idle);
                SetPrivateField(Coordinator, "_assignedBoxId", string.Empty);
            }

            public void ClearHeldObject()
            {
                _emptyGripperHost = new GameObject("p40b_empty_gripper");
                _emptyNavigationHost = new GameObject("p40b_empty_navigation");
                SetPrivateField(
                    Adapter,
                    "_tiagoUnityManipulationService",
                    new TiagoUnityManipulationService(_emptyGripperHost.transform, _emptyNavigationHost.transform, 1f, Vector3.zero));
            }

            public void ClearActiveTaskForPendingOnly()
            {
                SetPrivateField(Adapter, "_activeP40TaskInstanceId", string.Empty);
                SetPrivateField(Adapter, "_activeP40RequestId", string.Empty);
                SetPrivateField(Adapter, "_activeP40Producer", "unknown");
                SetPrivateField(Adapter, "_activeP40VoiceInteractionId", string.Empty);
                SetPrivateField(Adapter, "_activeP40TargetId", string.Empty);
                SetPrivateField(Adapter, "_activeP40PlaceTargetId", string.Empty);
                Blackboard.Remove(TaskBlackboardKeys.CurrentTarget);
                Blackboard.Remove(TaskBlackboardKeys.PlaceTarget);
                Blackboard.Remove(TaskBlackboardKeys.ResumeHeldObjectId);
                Blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.None);
                _fsm.TryChangeMode(RobotMode.Idle);
                Blackboard.SetMode(RobotMode.Idle);
                ClearHeldObject();
            }

            public void SetBlackboardMode(RobotMode mode)
            {
                Blackboard.SetMode(mode);
            }

            public void ControllerTick()
            {
                _controller.Tick();
            }

            public int EventCount(string eventType)
            {
                int count = 0;
                foreach (EventRecord record in Events)
                {
                    if (record.EventType == eventType)
                    {
                        count++;
                    }
                }

                return count;
            }

            private void CaptureEvent(string eventType, Dictionary<string, object> payload, float unityTime)
            {
                Events.Add(new EventRecord(eventType, payload != null ? new Dictionary<string, object>(payload) : new Dictionary<string, object>()));
            }

            public void Dispose()
            {
                TiagoExperimentTelemetry.StructuredEventLogged -= CaptureEvent;
                _controller.Dispose();
                GameObject heldProbe = GameObject.Find("old_box");
                if (heldProbe != null)
                {
                    UnityEngine.Object.DestroyImmediate(heldProbe);
                }

                if (_emptyGripperHost != null)
                {
                    UnityEngine.Object.DestroyImmediate(_emptyGripperHost);
                }

                if (_emptyNavigationHost != null)
                {
                    UnityEngine.Object.DestroyImmediate(_emptyNavigationHost);
                }

                UnityEngine.Object.DestroyImmediate(_adapterHost);
                UnityEngine.Object.DestroyImmediate(_coordinatorHost);
            }
        }

        private sealed class RunningNode : Node
        {
            public override NodeStatus Tick() => NodeStatus.Running;
        }

        private sealed class AlwaysSafeService : ISafetyService
        {
            public bool IsSafeToOperate() => true;
        }

        private sealed class NavigationSpy : INavigationService
        {
            public bool StopCalled { get; private set; }
            public NodeStatus MoveTo(DomainVector3 position) => NodeStatus.Running;
            public void Stop() => StopCalled = true;
        }

        private class ManipulationSpy : IManipulationService
        {
            public bool PickCalled { get; private set; }
            public bool PlaceCalled { get; private set; }
            public bool StopCalled { get; private set; }

            public virtual NodeStatus Pick(TargetDescriptor target)
            {
                PickCalled = true;
                return NodeStatus.Running;
            }

            public virtual NodeStatus Pick(string objectId)
            {
                PickCalled = true;
                return NodeStatus.Running;
            }

            public virtual NodeStatus Place(string destinationId)
            {
                PlaceCalled = true;
                return NodeStatus.Running;
            }

            public virtual void Stop()
            {
                StopCalled = true;
            }
        }

        private sealed class EventRecord
        {
            public EventRecord(string eventType, Dictionary<string, object> payload)
            {
                EventType = eventType;
                Payload = payload;
            }

            public string EventType { get; }
            public Dictionary<string, object> Payload { get; }
        }

        private sealed class BridgeProbe : IDisposable
        {
            private readonly GameObject _host;
            private readonly MultimodalAutonomyCommandBridge _bridge;
            private readonly MethodInfo _guardMethod;

            private BridgeProbe(GameObject host, MultimodalAutonomyCommandBridge bridge)
            {
                _host = host;
                _bridge = bridge;
                _guardMethod = typeof(MultimodalAutonomyCommandBridge)
                    .GetMethod("IsCurrentAssignedHeldVoiceTargetCandidate", BindingFlags.Instance | BindingFlags.NonPublic);
            }

            public static BridgeProbe Create(AutonomousRobotAdapter adapter)
            {
                var host = new GameObject("p40b_bridge_probe");
                var bridge = host.AddComponent<MultimodalAutonomyCommandBridge>();
                SetPrivateField(bridge, "_robotAdapter", adapter);
                return new BridgeProbe(host, bridge);
            }

            public bool IsCurrentAssignedHeldVoiceTargetCandidate(MultimodalTaskIntent intent)
            {
                return (bool)_guardMethod.Invoke(_bridge, new object[] { intent });
            }

            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(_host);
            }
        }

    }
}
