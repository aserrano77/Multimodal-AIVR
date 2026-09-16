using System;
using System.Collections.Generic;
using Autonomy.BT.Core;
using Autonomy.Core;
using Autonomy.Domain;
using Autonomy.Services;
using Autonomy.UnityIntegration;

namespace Autonomy.BT.Nodes.Actions
{
    public class NavigateToBlackboardTargetAction : Node
    {
        private readonly IReadOnlyRobotBlackboard _blackboard;
        private readonly INavigationService _navigationService;
        private readonly BlackboardKey<TargetDescriptor> _targetKey;
        private readonly string _startedEvent;
        private readonly string _succeededEvent;
        private readonly string _failedEvent;
        private string _activeTargetId;
        private bool _startedLogged;

        public NavigateToBlackboardTargetAction(
            IReadOnlyRobotBlackboard blackboard,
            INavigationService navigationService,
            BlackboardKey<TargetDescriptor> targetKey,
            string startedEvent,
            string succeededEvent,
            string failedEvent = "")
        {
            _blackboard = blackboard ?? throw new ArgumentNullException(nameof(blackboard));
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _targetKey = targetKey;
            _startedEvent = startedEvent ?? string.Empty;
            _succeededEvent = succeededEvent ?? string.Empty;
            _failedEvent = failedEvent ?? string.Empty;
        }

        public override NodeStatus Tick()
        {
            if (!_blackboard.TryGet(_targetKey, out var target) || target == null)
            {
                return NodeStatus.Failure;
            }

            if (ShouldSkipPickNavigationForHeldResume(target))
            {
                TiagoExperimentTelemetry.LogEvent(
                    "voice_resume_holding_place_skip_pick_applied",
                    new Dictionary<string, object>
                    {
                        ["target_key"] = _targetKey.Id,
                        ["target_id"] = target.Id,
                        ["held_object_id"] = target.Id,
                        ["navigation_to_pick_started"] = false,
                        ["skip_pick"] = true
                    });
                Reset();
                return NodeStatus.Success;
            }

            if (!string.Equals(_activeTargetId, target.Id, StringComparison.Ordinal))
            {
                _activeTargetId = target.Id;
                _startedLogged = false;
            }

            if (!_startedLogged)
            {
                LogStage(_startedEvent, target);
                _startedLogged = true;
            }

            NodeStatus status = _navigationService.MoveTo(target.Position);
            if (status == NodeStatus.Success)
            {
                LogStage(_succeededEvent, target);
                Reset();
            }
            else if (status == NodeStatus.Failure)
            {
                LogStage(_failedEvent, target);
                Reset();
            }

            return status;
        }

        private bool ShouldSkipPickNavigationForHeldResume(TargetDescriptor target)
        {
            return target != null &&
                   _targetKey.Id == TaskBlackboardKeys.CurrentTarget.Id &&
                   _blackboard.TryGet(TaskBlackboardKeys.ResumeHeldObjectId, out string heldObjectId) &&
                   string.Equals(heldObjectId, target.Id, StringComparison.OrdinalIgnoreCase);
        }

        public override void Reset()
        {
            _activeTargetId = null;
            _startedLogged = false;
            base.Reset();
        }

        private void LogStage(string eventName, TargetDescriptor target)
        {
            if (string.IsNullOrWhiteSpace(eventName) || target == null)
            {
                return;
            }

            TiagoExperimentTelemetry.LogEvent(
                eventName,
                new Dictionary<string, object>
                {
                    ["target_key"] = _targetKey.Id,
                    ["target_id"] = target.Id,
                    ["target_position"] = target.Position,
                    ["resume_held_object_id"] = _blackboard.TryGet(TaskBlackboardKeys.ResumeHeldObjectId, out string heldObjectId) ? heldObjectId : string.Empty
                });

            if (string.Equals(eventName, "navigation_to_place_started", StringComparison.Ordinal) &&
                _blackboard.TryGet(TaskBlackboardKeys.ResumeHeldObjectId, out string resumeHeldObjectId) &&
                !string.IsNullOrWhiteSpace(resumeHeldObjectId))
            {
                TiagoExperimentTelemetry.LogEvent(
                    "voice_resume_direct_place_navigation_started",
                    new Dictionary<string, object>
                    {
                        ["target_key"] = _targetKey.Id,
                        ["place_target_id"] = target.Id,
                        ["held_object_id"] = resumeHeldObjectId,
                        ["skip_pick"] = true,
                        ["navigation_to_pick_started"] = false,
                        ["navigation_to_place_started"] = true
                    });
            }
        }
    }
}
