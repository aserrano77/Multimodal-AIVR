using System;
using System.Collections.Generic;

namespace Autonomy.Domain
{
    public enum RobotAssistanceMode
    {
        Disabled,
        AssistedSelection
    }

    public sealed class ExperimentConditionConfig
    {
        public ExperimentConditionConfig(
            bool robotEnabled,
            bool voiceEnabled,
            string conditionName = "",
            RobotAssistanceMode assistanceMode = RobotAssistanceMode.AssistedSelection)
        {
            RobotEnabled = robotEnabled;
            VoiceEnabled = voiceEnabled;
            AssistanceMode = robotEnabled ? assistanceMode : RobotAssistanceMode.Disabled;
            ConditionName = string.IsNullOrWhiteSpace(conditionName)
                ? BuildConditionName(robotEnabled, voiceEnabled)
                : conditionName;
        }

        public bool RobotEnabled { get; }
        public bool VoiceEnabled { get; }
        public string ConditionName { get; }
        public RobotAssistanceMode AssistanceMode { get; }

        public bool RobotAssistanceEnabled => RobotEnabled && AssistanceMode == RobotAssistanceMode.AssistedSelection;

        public VoiceCommandExecutionGateResult EvaluateVoiceCommand(MultimodalTaskIntent intent)
        {
            if (!VoiceEnabled)
            {
                return VoiceCommandExecutionGateResult.Blocked(
                    "voice_disabled_by_condition",
                    "La voz esta desactivada en esta condicion experimental.");
            }

            if (!RobotEnabled)
            {
                return VoiceCommandExecutionGateResult.Blocked(
                    "robot_disabled_by_condition",
                    "La ayuda robotica esta desactivada en esta condicion experimental.");
            }

            return VoiceCommandExecutionGateResult.Allow("condition_allows_voice_robot_execution");
        }

        public AssistanceGateResult EvaluateRobotAssistance(bool robotBusy, bool explicitIntentProcessing, bool hasAssignedBox)
        {
            if (!RobotEnabled)
            {
                return AssistanceGateResult.Blocked("robot_disabled_by_condition");
            }

            if (AssistanceMode != RobotAssistanceMode.AssistedSelection)
            {
                return AssistanceGateResult.Blocked("assistance_mode_disabled");
            }

            if (robotBusy)
            {
                return AssistanceGateResult.Blocked("robot_busy");
            }

            if (explicitIntentProcessing)
            {
                return AssistanceGateResult.Blocked("explicit_intent_processing");
            }

            if (hasAssignedBox)
            {
                return AssistanceGateResult.Blocked("box_already_assigned");
            }

            return AssistanceGateResult.Allow("condition_allows_robot_assistance");
        }

        public Dictionary<string, object> ToPayload()
        {
            return new Dictionary<string, object>
            {
                ["robot_enabled"] = RobotEnabled,
                ["voice_enabled"] = VoiceEnabled,
                ["condition_2x2"] = ConditionName,
                ["assistance_mode"] = AssistanceMode.ToString()
            };
        }

        public static string BuildConditionName(bool robotEnabled, bool voiceEnabled)
        {
            return $"robot_{(robotEnabled ? "on" : "off")}_voice_{(voiceEnabled ? "on" : "off")}";
        }
    }

    public interface IExperimentConditionProvider
    {
        ExperimentConditionConfig CurrentCondition { get; }
    }

    public interface IExplicitAutonomyIntentActivity
    {
        void NotifyExplicitIntentProcessingStarted(MultimodalTaskIntent intent, string source);
        void NotifyExplicitIntentProcessingFinished(MultimodalTaskIntent intent, string source, bool accepted);
    }

    public readonly struct VoiceCommandExecutionGateResult
    {
        private VoiceCommandExecutionGateResult(bool allowed, string reason, string feedbackText)
        {
            Allowed = allowed;
            Reason = reason ?? string.Empty;
            FeedbackText = feedbackText ?? string.Empty;
        }

        public bool Allowed { get; }
        public string Reason { get; }
        public string FeedbackText { get; }

        public static VoiceCommandExecutionGateResult Allow(string reason)
        {
            return new VoiceCommandExecutionGateResult(true, reason, string.Empty);
        }

        public static VoiceCommandExecutionGateResult Blocked(string reason, string feedbackText)
        {
            return new VoiceCommandExecutionGateResult(false, reason, feedbackText);
        }
    }

    public readonly struct AssistanceGateResult
    {
        private AssistanceGateResult(bool allowed, string reason)
        {
            Allowed = allowed;
            Reason = reason ?? string.Empty;
        }

        public bool Allowed { get; }
        public string Reason { get; }

        public static AssistanceGateResult Allow(string reason)
        {
            return new AssistanceGateResult(true, reason);
        }

        public static AssistanceGateResult Blocked(string reason)
        {
            return new AssistanceGateResult(false, reason);
        }
    }
}
