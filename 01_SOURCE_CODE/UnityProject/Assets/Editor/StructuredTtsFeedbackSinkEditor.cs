using Autonomy.UnityIntegration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[CustomEditor(typeof(StructuredTtsFeedbackSink))]
public sealed class StructuredTtsFeedbackSinkEditor : Editor
{
    private const string AutoSpanishLabel = "Auto: Spanish es-ES";
    private const string TestText = "Prueba de voz seleccionada.";
    private static readonly List<InstalledVoice> InstalledVoices = new List<InstalledVoice>();
    private static string _lastRefreshStatus = "Not refreshed.";
    private static string _lastTestStatus = "No voice test has been run.";
    private static bool _advancedFoldout;

    private void OnEnable()
    {
        EnsureInstalledVoicesLoaded();
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        EnsureInstalledVoicesLoaded();
        DrawInspectorWithoutManualVoiceFields();
        serializedObject.ApplyModifiedProperties();
    }

    private void DrawInspectorWithoutManualVoiceFields()
    {
        SerializedProperty iterator = serializedObject.GetIterator();
        bool enterChildren = true;
        while (iterator.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (iterator.propertyPath == "m_Script")
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(iterator, includeChildren: true);
                }
                continue;
            }

            if (iterator.propertyPath == "_preferredCulture")
            {
                DrawVoiceSelectionSection();
                continue;
            }

            if (IsVoiceInternal(iterator.propertyPath))
            {
                continue;
            }

            EditorGUILayout.PropertyField(iterator, includeChildren: true);
        }
    }

    private void DrawVoiceSelectionSection()
    {
        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("Voice Selection", EditorStyles.boldLabel);

        if (GUILayout.Button("Refresh installed SAPI voices"))
        {
            RefreshInstalledVoices();
        }

        EditorGUILayout.HelpBox(_lastRefreshStatus, MessageType.Info);

        string[] labels = BuildVoiceLabels();
        int currentIndex = ResolveDropdownIndex();
        int newIndex = EditorGUILayout.Popup("Voice", currentIndex, labels);
        if (newIndex != currentIndex)
        {
            ApplyDropdownSelection(newIndex);
            currentIndex = newIndex;
        }

        string currentExactVoice = ReadString("_preferredVoiceName");
        if (string.IsNullOrWhiteSpace(currentExactVoice))
        {
            EditorGUILayout.LabelField("Current voice mode: Auto Spanish es-ES");
        }
        else
        {
            EditorGUILayout.LabelField($"Current exact voice: {currentExactVoice}");
        }

        InstalledVoice selectedVoice = ResolveVoiceFromDropdownIndex(currentIndex);
        if (!selectedVoice.IsEmpty && string.Equals(selectedVoice.Culture, "en-US", StringComparison.OrdinalIgnoreCase))
        {
            EditorGUILayout.HelpBox("This voice is en-US and may pronounce Spanish text incorrectly.", MessageType.Warning);
        }

        if (!HasSpanishMaleVoice())
        {
            EditorGUILayout.HelpBox("No Spanish male SAPI voice is available through System.Speech on this machine.", MessageType.Info);
        }

        if (GUILayout.Button("Test selected voice"))
        {
            _lastTestStatus = TestDropdownSelection(currentIndex);
        }

        EditorGUILayout.HelpBox(_lastTestStatus, MessageType.None);
        DrawAdvancedVoiceInternals();
    }

    private void DrawAdvancedVoiceInternals()
    {
        _advancedFoldout = EditorGUILayout.Foldout(_advancedFoldout, "Advanced voice selection internals", toggleOnLabelClick: true);
        if (!_advancedFoldout)
        {
            return;
        }

        EditorGUI.indentLevel++;
        EditorGUILayout.PropertyField(serializedObject.FindProperty("_preferredCulture"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("_preferredVoiceName"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("_preferredVoiceGender"));
        EditorGUI.indentLevel--;
    }

    private string[] BuildVoiceLabels()
    {
        List<string> labels = new List<string> { AutoSpanishLabel };
        labels.AddRange(InstalledVoices
            .Where(voice => voice.Enabled)
            .Select(voice => $"{voice.Name} | {voice.Culture} | {voice.Gender} | {voice.Age}"));
        return labels.ToArray();
    }

    private int ResolveDropdownIndex()
    {
        string exactVoice = ReadString("_preferredVoiceName");
        if (string.IsNullOrWhiteSpace(exactVoice))
        {
            return 0;
        }

        int voiceIndex = InstalledVoices.FindIndex(voice =>
            voice.Enabled &&
            string.Equals(voice.Name, exactVoice, StringComparison.OrdinalIgnoreCase));
        return voiceIndex >= 0 ? voiceIndex + 1 : 0;
    }

    private InstalledVoice ResolveVoiceFromDropdownIndex(int dropdownIndex)
    {
        if (dropdownIndex <= 0)
        {
            return ResolveAutoSpanishVoice();
        }

        int voiceIndex = dropdownIndex - 1;
        return voiceIndex >= 0 && voiceIndex < InstalledVoices.Count
            ? InstalledVoices[voiceIndex]
            : default;
    }

    private InstalledVoice ResolveAutoSpanishVoice()
    {
        InstalledVoice spanish = InstalledVoices.FirstOrDefault(voice =>
            voice.Enabled &&
            string.Equals(voice.Culture, "es-ES", StringComparison.OrdinalIgnoreCase));
        return !spanish.IsEmpty ? spanish : InstalledVoices.FirstOrDefault(voice => voice.Enabled);
    }

    private void ApplyDropdownSelection(int dropdownIndex)
    {
        Undo.RecordObject(target, "Select SAPI voice");
        serializedObject.Update();
        if (dropdownIndex <= 0)
        {
            SetStringProperty("_preferredCulture", "es-ES");
            SetStringProperty("_preferredVoiceName", string.Empty);
            SetStringProperty("_preferredVoiceGender", string.Empty);
        }
        else
        {
            InstalledVoice selected = ResolveVoiceFromDropdownIndex(dropdownIndex);
            SetStringProperty("_preferredVoiceName", selected.Name);
        }

        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
        if (target is Component component && component.gameObject.scene.IsValid())
        {
            EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
        }

        Repaint();
    }

    private string TestDropdownSelection(int dropdownIndex)
    {
        InstalledVoice selected = ResolveVoiceFromDropdownIndex(dropdownIndex);
        if (selected.IsEmpty)
        {
            return "No installed System.Speech voice is available for this selection.";
        }

        return TestVoice(selected.Name, selected.Culture);
    }

    private static bool HasSpanishMaleVoice()
    {
        return InstalledVoices.Any(voice =>
            voice.Enabled &&
            string.Equals(voice.Culture, "es-ES", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(voice.Gender, "Male", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVoiceInternal(string propertyPath)
    {
        return propertyPath == "_preferredCulture" ||
               propertyPath == "_preferredVoiceName" ||
               propertyPath == "_preferredVoiceGender";
    }

    private string ReadString(string propertyName)
    {
        SerializedProperty property = serializedObject.FindProperty(propertyName);
        return property != null ? property.stringValue ?? string.Empty : string.Empty;
    }

    private void SetStringProperty(string propertyName, string value)
    {
        SerializedProperty property = serializedObject.FindProperty(propertyName);
        if (property == null)
        {
            UnityEngine.Debug.LogWarning($"[StructuredTtsFeedbackSinkEditor] Missing serialized property '{propertyName}'.");
            return;
        }

        property.stringValue = value ?? string.Empty;
    }

    private static void EnsureInstalledVoicesLoaded()
    {
        if (InstalledVoices.Count == 0)
        {
            RefreshInstalledVoices();
        }
    }

    private static void RefreshInstalledVoices()
    {
        InstalledVoices.Clear();
        string script =
            "Add-Type -AssemblyName System.Speech; " +
            "function Clean($v) { if ($null -eq $v) { return '' }; return (($v.ToString()) -replace '\\|','/'); }; " +
            "$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
            "$voices = @($synth.GetInstalledVoices() | Sort-Object { $_.VoiceInfo.Name }); " +
            "foreach ($v in $voices) { $i = $v.VoiceInfo; Write-Output ((Clean $i.Name)+'|'+(Clean $i.Culture.Name)+'|'+(Clean $i.Gender)+'|'+(Clean $i.Age)+'|'+(Clean $v.Enabled)); }";

        if (!RunPowerShell(script, 7000, out List<string> lines, out string failureReason))
        {
            _lastRefreshStatus = $"Failed to list installed SAPI voices: {failureReason}";
            return;
        }

        foreach (string line in lines)
        {
            InstalledVoice voice = InstalledVoice.Parse(line);
            if (!voice.IsEmpty)
            {
                InstalledVoices.Add(voice);
                UnityEngine.Debug.Log($"[StructuredTtsFeedbackSinkEditor] installed_voice name='{voice.Name}' culture='{voice.Culture}' gender='{voice.Gender}' age='{voice.Age}' enabled='{voice.Enabled}'");
            }
        }

        _lastRefreshStatus = $"Detected {InstalledVoices.Count} System.Speech SAPI voice(s).";
    }

    private static string TestVoice(string exactVoiceName, string culture)
    {
        string script =
            "Add-Type -AssemblyName System.Speech; " +
            "$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
            $"$voiceName = {ToPowerShellSingleQuotedString(exactVoiceName)}; " +
            "$synth.SelectVoice($voiceName); " +
            "$synth.Volume = 100; $synth.Rate = -1; " +
            $"$ssml = {ToPowerShellSingleQuotedString(BuildSsml(TestText, culture))}; " +
            "$synth.SpeakSsml($ssml); " +
            "Write-Output ('SELECTED|' + $synth.Voice.Name + '|' + $synth.Voice.Culture.Name);";

        if (!RunPowerShell(script, 10000, out List<string> lines, out string failureReason))
        {
            return $"Voice test failed for {exactVoiceName}: {failureReason}";
        }

        string selectedLine = lines.FirstOrDefault(line => line.StartsWith("SELECTED|", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(selectedLine)
            ? $"Voice test completed for {exactVoiceName}."
            : $"Voice test completed: {selectedLine}";
    }

    private static bool RunPowerShell(string script, int timeoutMilliseconds, out List<string> lines, out string failureReason)
    {
        lines = new List<string>();
        failureReason = string.Empty;
        string systemPath = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string powershellPath = Path.Combine(systemPath, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershellPath))
        {
            powershellPath = "powershell.exe";
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = powershellPath,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using Process process = Process.Start(startInfo);
            if (process == null)
            {
                failureReason = "powershell_process_not_started";
                return false;
            }

            if (!process.WaitForExit(timeoutMilliseconds))
            {
                process.Kill();
                failureReason = "powershell_timeout";
                return false;
            }

            while (!process.StandardOutput.EndOfStream)
            {
                lines.Add(process.StandardOutput.ReadLine());
            }

            string error = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0)
            {
                failureReason = string.IsNullOrWhiteSpace(error)
                    ? $"powershell_exit_{process.ExitCode}"
                    : $"powershell_exit_{process.ExitCode}:{error.Trim()}";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                failureReason = error.Trim();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"{ex.GetType().Name}:{ex.Message}";
            return false;
        }
    }

    private static string BuildSsml(string text, string culture)
    {
        string lang = string.IsNullOrWhiteSpace(culture) ? "es-ES" : culture;
        return
            $"<speak version='1.0' xml:lang='{EscapeXmlAttribute(lang)}' xmlns='http://www.w3.org/2001/10/synthesis'>" +
            "<break time='350ms'/>" +
            EscapeXmlText(text) +
            "</speak>";
    }

    private static string ToPowerShellSingleQuotedString(string value)
    {
        return $"'{(value ?? string.Empty).Replace("'", "''")}'";
    }

    private static string EscapeXmlText(string value)
    {
        return (value ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    private static string EscapeXmlAttribute(string value)
    {
        return EscapeXmlText(value)
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private readonly struct InstalledVoice
    {
        private InstalledVoice(string name, string culture, string gender, string age, bool enabled)
        {
            Name = name;
            Culture = culture;
            Gender = gender;
            Age = age;
            Enabled = enabled;
        }

        public string Name { get; }
        public string Culture { get; }
        public string Gender { get; }
        public string Age { get; }
        public bool Enabled { get; }
        public bool IsEmpty => string.IsNullOrWhiteSpace(Name);

        public static InstalledVoice Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return default;
            }

            string[] parts = line.Split('|');
            return parts.Length >= 5
                ? new InstalledVoice(parts[0], parts[1], parts[2], parts[3], string.Equals(parts[4], "True", StringComparison.OrdinalIgnoreCase))
                : default;
        }
    }
}
