using System;
using System.Collections.Generic;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(ExperimentSessionOrchestrator), true)]
[CanEditMultipleObjects]
public sealed class ExperimentSessionOrchestratorEditor : Editor
{
    private const string SelectedConditionIndexProperty = "_selectedConditionIndex";
    private const string RoundsPerConditionProperty = "_roundsPerCondition";
    private const string ConditionsProperty = "_conditions";
    private const string RunModeProperty = "_runMode";

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        SerializedProperty selectedConditionIndex = serializedObject.FindProperty(SelectedConditionIndexProperty);
        if (selectedConditionIndex == null)
        {
            EditorGUILayout.HelpBox(
                $"ExperimentSessionOrchestratorEditor could not find serialized property `{SelectedConditionIndexProperty}`. The semantic condition popup cannot be rendered safely.",
                MessageType.Error);
            serializedObject.ApplyModifiedProperties();
            return;
        }

        SerializedProperty iterator = serializedObject.GetIterator();
        bool enterChildren = true;
        bool selectedConditionDrawn = false;
        while (iterator.NextVisible(enterChildren))
        {
            enterChildren = false;
            SerializedProperty property = iterator.Copy();
            if (property.propertyPath == "m_Script")
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(property, true);
                }

                continue;
            }

            if (IsSelectedConditionIndexProperty(property))
            {
                DrawSelectedConditionPopup(selectedConditionIndex);
                selectedConditionDrawn = true;
                continue;
            }

            EditorGUILayout.PropertyField(property, true);
        }

        if (!selectedConditionDrawn)
        {
            EditorGUILayout.HelpBox(
                $"ExperimentSessionOrchestratorEditor found `{SelectedConditionIndexProperty}` through SerializedObject.FindProperty, but it was not encountered while rendering visible properties. The raw integer field was not drawn.",
                MessageType.Warning);
            DrawSelectedConditionPopup(selectedConditionIndex);
        }

        serializedObject.ApplyModifiedProperties();
    }

    private static bool IsSelectedConditionIndexProperty(SerializedProperty property)
    {
        return property != null &&
               (property.propertyPath == SelectedConditionIndexProperty ||
                property.name == SelectedConditionIndexProperty ||
                property.displayName == "Selected Condition Index");
    }

    private void DrawSelectedConditionPopup(SerializedProperty selectedIndex)
    {
        SerializedProperty conditions = serializedObject.FindProperty(ConditionsProperty);
        if (conditions == null || !conditions.isArray || conditions.arraySize == 0)
        {
            EditorGUILayout.HelpBox(
                $"ExperimentSessionOrchestratorEditor could not find a usable serialized `{ConditionsProperty}` array. Run the existing final-condition validation preflight/repair before selecting trials.",
                MessageType.Warning);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.LabelField("Selected Condition", $"Internal index: {selectedIndex.intValue}");
            }

            return;
        }

        List<Experiment2x2ConditionDefinition> conditionModels = ReadConditions(conditions);
        if (!Experiment2x2ConditionDefinition.IsCanonicalPresetMatrix(conditionModels, out string canonicalReason))
        {
            EditorGUILayout.HelpBox(
                $"The serialized final condition matrix is not canonical ({canonicalReason}). Use Tools/Multimodal AI-VR/Experiment 2x2 Validation/Repair Serialized Condition Matrix, then rerun Preflight.",
                MessageType.Warning);
        }

        bool orchestrated2x2 = IsOrchestrated2x2();
        if (orchestrated2x2)
        {
            EditorGUILayout.HelpBox(
                "In Orchestrated2x2 mode, Start Next Trial follows the expanded condition x round plan. Manual Selected Condition changes do not override the active plan during a session.",
                MessageType.Info);
        }

        string[] labels = BuildConditionLabels(conditions);
        int current = Mathf.Clamp(selectedIndex.intValue, 0, labels.Length - 1);
        EditorGUI.BeginChangeCheck();
        EditorGUI.showMixedValue = selectedIndex.hasMultipleDifferentValues;
        string popupLabel = orchestrated2x2
            ? "Selected Condition (Plan Reflection / Preview)"
            : "Selected Condition";
        bool disableManualSelection = orchestrated2x2 && Application.isPlaying;
        int next;
        using (new EditorGUI.DisabledScope(disableManualSelection))
        {
            next = EditorGUILayout.Popup(popupLabel, current, labels);
        }

        EditorGUI.showMixedValue = false;
        if (EditorGUI.EndChangeCheck())
        {
            selectedIndex.intValue = next;
        }

        DrawPlanSummary(conditions, current);
        if (!selectedIndex.hasMultipleDifferentValues)
        {
            DrawSelectedConditionSummary(conditions, next);
        }
    }

    private void DrawPlanSummary(SerializedProperty conditions, int selectedConditionIndex)
    {
        SerializedProperty roundsProperty = serializedObject.FindProperty(RoundsPerConditionProperty);
        int roundsPerCondition = Mathf.Max(1, roundsProperty != null ? roundsProperty.intValue : 1);
        List<Experiment2x2ConditionDefinition> conditionModels = ReadConditions(conditions);
        List<ExperimentTrialPlanEntry> plan = ExperimentTrialPlanBuilder.Build(conditionModels, roundsPerCondition);
        int totalTrials = plan.Count;
        var orchestrator = target as ExperimentSessionOrchestrator;
        int currentPlanEntryIndex = orchestrator != null && Application.isPlaying ? orchestrator.CurrentPlanEntryIndex : -1;
        int nextPlanEntryIndex = orchestrator != null && Application.isPlaying ? orchestrator.NextPlanEntryIndex : 0;
        ExperimentTrialPlanEntry currentEntry = EntryAt(plan, currentPlanEntryIndex);
        ExperimentTrialPlanEntry nextEntry = EntryAt(plan, nextPlanEntryIndex);
        int currentRoundWithinCondition = currentEntry != null
            ? currentEntry.RoundIndexWithinCondition
            : Mathf.Clamp(selectedConditionIndex >= 0 ? 1 : 0, 0, roundsPerCondition);
        int currentGlobalRoundIndex = currentEntry != null ? currentEntry.GlobalRoundIndex : 0;

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Expanded Trial Plan", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Rounds Per Condition", roundsPerCondition.ToString());
            EditorGUILayout.LabelField("Current Plan Entry Index", currentPlanEntryIndex.ToString());
            EditorGUILayout.LabelField("Next Plan Entry Index", nextPlanEntryIndex.ToString());
            EditorGUILayout.LabelField("Current Planned Condition", FormatConditionEntry(currentEntry, "none"));
            EditorGUILayout.LabelField("Current Round Within Condition", $"{currentRoundWithinCondition} / {roundsPerCondition}");
            EditorGUILayout.LabelField("Current Global Round Index", currentGlobalRoundIndex > 0 ? currentGlobalRoundIndex.ToString() : "none");
            EditorGUILayout.LabelField("Next Planned Trial", FormatConditionEntry(nextEntry, "none"));
            EditorGUILayout.LabelField("Total Planned Trials", totalTrials.ToString());
        }
    }

    private bool IsOrchestrated2x2()
    {
        SerializedProperty runMode = serializedObject.FindProperty(RunModeProperty);
        return runMode != null &&
               runMode.propertyType == SerializedPropertyType.Enum &&
               runMode.enumNames[runMode.enumValueIndex] == ExperimentRunMode.Orchestrated2x2.ToString();
    }

    private static ExperimentTrialPlanEntry EntryAt(IReadOnlyList<ExperimentTrialPlanEntry> plan, int index)
    {
        return plan != null && index >= 0 && index < plan.Count ? plan[index] : null;
    }

    private static string FormatConditionEntry(ExperimentTrialPlanEntry entry, string fallback)
    {
        if (entry == null || entry.Condition == null)
        {
            return fallback;
        }

        return $"trial_{entry.TrialIndex:000} | {entry.Condition.ConditionId} round {entry.RoundIndexWithinCondition} / {entry.RoundsPerCondition}";
    }

    private static string[] BuildConditionLabels(SerializedProperty conditions)
    {
        string[] labels = new string[conditions.arraySize];
        for (int i = 0; i < conditions.arraySize; i++)
        {
            SerializedProperty condition = conditions.GetArrayElementAtIndex(i);
            string conditionId = StringValue(condition, "ConditionId");
            string conditionName = StringValue(condition, "ConditionName");
            bool robotEnabled = BoolValue(condition, "RobotEnabled");
            bool voiceEnabled = BoolValue(condition, "VoiceEnabled");
            string shortId = ShortConditionId(conditionId, i);
            string displayName = !string.IsNullOrWhiteSpace(conditionName)
                ? conditionName
                : BuildDisplayName(robotEnabled, voiceEnabled);
            labels[i] = $"{shortId} - {displayName}";
        }

        return labels;
    }

    private static void DrawSelectedConditionSummary(SerializedProperty conditions, int selectedIndex)
    {
        if (selectedIndex < 0 || selectedIndex >= conditions.arraySize)
        {
            return;
        }

        SerializedProperty condition = conditions.GetArrayElementAtIndex(selectedIndex);
        EditorGUILayout.Space(2f);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Selected Condition Summary", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("condition_id", StringValue(condition, "ConditionId"));
            EditorGUILayout.LabelField("condition_name", StringValue(condition, "ConditionName"));
            EditorGUILayout.LabelField("robot_enabled", BoolValue(condition, "RobotEnabled").ToString());
            EditorGUILayout.LabelField("voice_enabled", BoolValue(condition, "VoiceEnabled").ToString());
            EditorGUILayout.LabelField("assistance_mode", EnumName(condition, "AssistanceMode"));
        }
    }

    private static List<Experiment2x2ConditionDefinition> ReadConditions(SerializedProperty conditions)
    {
        var result = new List<Experiment2x2ConditionDefinition>();
        for (int i = 0; i < conditions.arraySize; i++)
        {
            SerializedProperty condition = conditions.GetArrayElementAtIndex(i);
            result.Add(new Experiment2x2ConditionDefinition(
                StringValue(condition, "ConditionId"),
                StringValue(condition, "ConditionName"),
                BoolValue(condition, "RobotEnabled"),
                BoolValue(condition, "VoiceEnabled"),
                EnumValue<RobotAssistanceMode>(condition, "AssistanceMode"),
                EnumValue<SpawnGenerationMode>(condition, "SpawnGenerationMode"),
                StringValue(condition, "Notes")));
        }

        return result;
    }

    private static string ShortConditionId(string conditionId, int index)
    {
        if (!string.IsNullOrWhiteSpace(conditionId))
        {
            int separator = conditionId.IndexOf('_');
            return separator > 0 ? conditionId.Substring(0, separator) : conditionId;
        }

        return $"Condition {index}";
    }

    private static string BuildDisplayName(bool robotEnabled, bool voiceEnabled)
    {
        return $"Robot {(robotEnabled ? "ON" : "OFF")} + Voice {(voiceEnabled ? "ON" : "OFF")}";
    }

    private static string StringValue(SerializedProperty root, string relativePath)
    {
        SerializedProperty property = root.FindPropertyRelative(relativePath);
        return property != null && property.propertyType == SerializedPropertyType.String ? property.stringValue ?? string.Empty : string.Empty;
    }

    private static bool BoolValue(SerializedProperty root, string relativePath)
    {
        SerializedProperty property = root.FindPropertyRelative(relativePath);
        return property != null && property.propertyType == SerializedPropertyType.Boolean && property.boolValue;
    }

    private static string EnumName(SerializedProperty root, string relativePath)
    {
        SerializedProperty property = root.FindPropertyRelative(relativePath);
        if (property == null || property.propertyType != SerializedPropertyType.Enum)
        {
            return string.Empty;
        }

        return property.enumNames[property.enumValueIndex];
    }

    private static T EnumValue<T>(SerializedProperty root, string relativePath) where T : struct, Enum
    {
        string enumName = EnumName(root, relativePath);
        return Enum.TryParse(enumName, out T value) ? value : default;
    }
}
