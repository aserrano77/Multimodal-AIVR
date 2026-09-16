using System;

namespace Autonomy.Domain
{
    public sealed class VoiceConfirmationPolicy
    {
        public VoiceConfirmationPolicy(
            VoiceConfirmationMode mode = VoiceConfirmationMode.Disabled,
            float timeoutSeconds = 8f,
            float lowConfidenceThreshold = 0.75f)
        {
            Mode = mode;
            TimeoutSeconds = Math.Max(0.1f, timeoutSeconds);
            LowConfidenceThreshold = Math.Max(0f, Math.Min(1f, lowConfidenceThreshold));
        }

        public VoiceConfirmationMode Mode { get; }
        public float TimeoutSeconds { get; }
        public float LowConfidenceThreshold { get; }

        public bool RequiresConfirmation(VoiceCommandIntentMappingResult mapping)
        {
            if (!IsExecutablePickAndPlace(mapping))
            {
                return false;
            }

            switch (Mode)
            {
                case VoiceConfirmationMode.AlwaysForExecutable:
                    return true;
                case VoiceConfirmationMode.LowConfidenceOnly:
                    return mapping.Score < LowConfidenceThreshold;
                default:
                    return false;
            }
        }

        public bool IsExecutablePickAndPlace(VoiceCommandIntentMappingResult mapping)
        {
            return mapping != null &&
                mapping.HasExecutableTaskIntent &&
                mapping.IntentKind == VoiceCommandIntentKind.PickAndPlace &&
                mapping.TaskIntent.TaskFlow == AutonomousTaskFlow.PickAndPlace;
        }
    }
}
