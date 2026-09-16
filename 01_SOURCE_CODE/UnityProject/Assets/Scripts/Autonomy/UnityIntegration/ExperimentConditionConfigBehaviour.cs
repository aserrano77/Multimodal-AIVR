using System.Collections.Generic;
using Autonomy.Domain;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class ExperimentConditionConfigBehaviour : MonoBehaviour, IExperimentConditionProvider
    {
        [Header("2x2 Condition")]
        [SerializeField] private bool _robotEnabled = true;
        [SerializeField] private bool _voiceEnabled = true;
        [SerializeField] private string _conditionName = "";
        [SerializeField] private RobotAssistanceMode _assistanceMode = RobotAssistanceMode.AssistedSelection;
        [SerializeField, Tooltip("Legacy/debug only. In Orchestrated2x2 this component is a runtime gate controlled by ExperimentSessionOrchestrator.")]
        private bool _logConditionOnStart = false;
        [SerializeField] private ExperimentRunMode _runMode = ExperimentRunMode.LegacyDebug;

        public bool RobotEnabled
        {
            get => _robotEnabled;
            set => ApplyCondition(value, _voiceEnabled, _conditionName, _assistanceMode);
        }

        public bool VoiceEnabled
        {
            get => _voiceEnabled;
            set => ApplyCondition(_robotEnabled, value, _conditionName, _assistanceMode);
        }

        public ExperimentConditionConfig CurrentCondition { get; private set; }
        public ExperimentRunMode RunMode => _runMode;
        public bool IsOrchestrated => _runMode == ExperimentRunMode.Orchestrated2x2;

        private void Awake()
        {
            RefreshCondition();
        }

        private void Start()
        {
            if (_logConditionOnStart && !IsOrchestrated)
            {
                LogCondition("experiment_condition_applied");
            }
        }

        public void ApplyCondition(bool robotEnabled, bool voiceEnabled, string conditionName = "", RobotAssistanceMode assistanceMode = RobotAssistanceMode.AssistedSelection)
        {
            if (IsOrchestrated)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "experiment_condition_manual_apply_ignored",
                    new Dictionary<string, object>
                    {
                        ["reason"] = "orchestrated_2x2_context_controls_condition",
                        ["condition_component"] = name,
                        ["scene"] = SceneManager.GetActiveScene().name
                    });
                return;
            }

            ApplyConditionFromOrchestrator(robotEnabled, voiceEnabled, conditionName, assistanceMode, "legacy_manual");
        }

        public void SetRunMode(ExperimentRunMode runMode)
        {
            _runMode = runMode;
        }

        public void ApplyConditionFromOrchestrator(
            bool robotEnabled,
            bool voiceEnabled,
            string conditionName = "",
            RobotAssistanceMode assistanceMode = RobotAssistanceMode.AssistedSelection,
            string source = "orchestrator")
        {
            _robotEnabled = robotEnabled;
            _voiceEnabled = voiceEnabled;
            _conditionName = conditionName ?? string.Empty;
            _assistanceMode = assistanceMode;
            RefreshCondition();
            Dictionary<string, object> payload = CurrentCondition.ToPayload();
            payload["scene"] = SceneManager.GetActiveScene().name;
            payload["condition_component"] = name;
            payload["source"] = source ?? string.Empty;
            payload["experiment_run_mode"] = _runMode.ToString();
            TiagoExperimentTelemetry.LogEvent("experiment_condition_applied", payload);
        }

        private void RefreshCondition()
        {
            CurrentCondition = new ExperimentConditionConfig(_robotEnabled, _voiceEnabled, _conditionName, _assistanceMode);
            _conditionName = CurrentCondition.ConditionName;
        }

        private void LogCondition(string eventType)
        {
            Dictionary<string, object> payload = CurrentCondition.ToPayload();
            payload["scene"] = SceneManager.GetActiveScene().name;
            payload["condition_component"] = name;
            TiagoExperimentTelemetry.LogEvent(eventType, payload);
        }

        private void OnValidate()
        {
            RefreshCondition();
        }
    }
}
