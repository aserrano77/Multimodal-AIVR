using System;
using System.Collections.Generic;

namespace Autonomy.Domain
{
    [Serializable]
    public sealed class Experiment2x2ConditionDefinition
    {
        public string ConditionId = "C11_robot_on_voice_on";
        public string ConditionName = "Robot ON + Voice ON";
        public bool RobotEnabled = true;
        public bool VoiceEnabled = true;
        public RobotAssistanceMode AssistanceMode = RobotAssistanceMode.AssistedSelection;
        public SpawnGenerationMode SpawnGenerationMode = SpawnGenerationMode.RandomBalanced;
        public string Notes = string.Empty;

        public Experiment2x2ConditionDefinition()
        {
        }

        public Experiment2x2ConditionDefinition(
            string conditionId,
            string conditionName,
            bool robotEnabled,
            bool voiceEnabled,
            RobotAssistanceMode assistanceMode,
            SpawnGenerationMode spawnGenerationMode,
            string notes = "")
        {
            ConditionId = conditionId ?? string.Empty;
            ConditionName = conditionName ?? string.Empty;
            RobotEnabled = robotEnabled;
            VoiceEnabled = voiceEnabled;
            AssistanceMode = robotEnabled ? assistanceMode : RobotAssistanceMode.Disabled;
            SpawnGenerationMode = spawnGenerationMode;
            Notes = notes ?? string.Empty;
        }

        public ExperimentConditionConfig ToConditionConfig()
        {
            return new ExperimentConditionConfig(RobotEnabled, VoiceEnabled, ConditionName, AssistanceMode);
        }

        public Dictionary<string, object> ToPayload(int conditionOrderIndex = -1)
        {
            return new Dictionary<string, object>
            {
                ["condition_id"] = ConditionId,
                ["condition_name"] = ConditionName,
                ["condition_order_index"] = conditionOrderIndex,
                ["robot_enabled"] = RobotEnabled,
                ["voice_enabled"] = VoiceEnabled,
                ["condition_2x2"] = ConditionName,
                ["assistance_mode"] = AssistanceMode.ToString(),
                ["spawn_generation_mode"] = SpawnGenerationMode.ToString(),
                ["notes"] = Notes ?? string.Empty
            };
        }

        public static List<Experiment2x2ConditionDefinition> CreateDefaultPresets(SpawnGenerationMode spawnMode)
        {
            return new List<Experiment2x2ConditionDefinition>
            {
                new Experiment2x2ConditionDefinition(
                    "C00_robot_off_voice_off",
                    "Robot OFF + Voice OFF",
                    robotEnabled: false,
                    voiceEnabled: false,
                    RobotAssistanceMode.Disabled,
                    spawnMode,
                    "Baseline without robot assistance or voice execution."),
                new Experiment2x2ConditionDefinition(
                    "C10_robot_on_voice_off",
                    "Robot ON + Voice OFF",
                    robotEnabled: true,
                    voiceEnabled: false,
                    RobotAssistanceMode.AssistedSelection,
                    spawnMode,
                    "Sequential robot assistance enabled, voice execution gated off."),
                new Experiment2x2ConditionDefinition(
                    "C11_robot_on_voice_on",
                    "Robot ON + Voice ON",
                    robotEnabled: true,
                    voiceEnabled: true,
                    RobotAssistanceMode.AssistedSelection,
                    spawnMode,
                    "Robot assistance enabled and valid explicit voice commands may execute.")
            };
        }

        public static bool EnsureCanonicalPresetMatrix(
            List<Experiment2x2ConditionDefinition> conditions,
            SpawnGenerationMode spawnMode)
        {
            if (conditions == null)
            {
                return false;
            }

            List<Experiment2x2ConditionDefinition> presets = CreateDefaultPresets(spawnMode);
            bool changed = false;
            if (conditions.Count != presets.Count)
            {
                conditions.Clear();
                conditions.AddRange(presets);
                return true;
            }

            for (int i = 0; i < presets.Count; i++)
            {
                Experiment2x2ConditionDefinition condition = conditions[i];
                Experiment2x2ConditionDefinition preset = presets[i];
                if (condition == null)
                {
                    conditions[i] = preset;
                    changed = true;
                    continue;
                }

                SpawnGenerationMode effectiveSpawnMode = condition.SpawnGenerationMode;
                if (!string.Equals(condition.ConditionId, preset.ConditionId, StringComparison.Ordinal) ||
                    !string.Equals(condition.ConditionName, preset.ConditionName, StringComparison.Ordinal) ||
                    condition.RobotEnabled != preset.RobotEnabled ||
                    condition.VoiceEnabled != preset.VoiceEnabled ||
                    condition.AssistanceMode != preset.AssistanceMode ||
                    string.IsNullOrWhiteSpace(condition.Notes))
                {
                    condition.ConditionId = preset.ConditionId;
                    condition.ConditionName = preset.ConditionName;
                    condition.RobotEnabled = preset.RobotEnabled;
                    condition.VoiceEnabled = preset.VoiceEnabled;
                    condition.AssistanceMode = preset.AssistanceMode;
                    condition.SpawnGenerationMode = effectiveSpawnMode;
                    condition.Notes = preset.Notes;
                    changed = true;
                }
            }

            return changed;
        }

        public static bool IsCanonicalPresetMatrix(
            IReadOnlyList<Experiment2x2ConditionDefinition> conditions,
            out string reason)
        {
            reason = string.Empty;
            if (conditions == null)
            {
                reason = "conditions_null";
                return false;
            }

            List<Experiment2x2ConditionDefinition> presets = CreateDefaultPresets(SpawnGenerationMode.RandomBalanced);
            if (conditions.Count != presets.Count)
            {
                reason = "condition_count_mismatch";
                return false;
            }

            for (int i = 0; i < presets.Count; i++)
            {
                Experiment2x2ConditionDefinition condition = conditions[i];
                Experiment2x2ConditionDefinition preset = presets[i];
                if (condition == null)
                {
                    reason = $"condition_{i}_null";
                    return false;
                }

                if (!string.Equals(condition.ConditionId, preset.ConditionId, StringComparison.Ordinal) ||
                    condition.RobotEnabled != preset.RobotEnabled ||
                    condition.VoiceEnabled != preset.VoiceEnabled ||
                    condition.AssistanceMode != preset.AssistanceMode)
                {
                    reason = $"condition_{i}_matrix_mismatch";
                    return false;
                }
            }

            return true;
        }
    }
}
