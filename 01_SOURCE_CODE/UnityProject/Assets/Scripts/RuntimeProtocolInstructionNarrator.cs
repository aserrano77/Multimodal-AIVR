using System;
using System.Collections.Generic;
using Autonomy.Domain;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class RuntimeProtocolInstructionNarrator : MonoBehaviour
    {
        private const string ResourceRoot = "ExperimentInstructions/";
        private const string BackendName = "resources_audio_clip";
        private const string VoiceSource = "experiment_instruction_narrator";
        private const bool UsesRobotTts = false;

        [SerializeField] private AudioSource _audioSource;
        [SerializeField] private float _volume = 0.92f;

        private string _activeConditionId = string.Empty;
        private string _activeClipId = string.Empty;
        private bool _completionPending;

        public static RuntimeProtocolInstructionNarrator EnsureAttached(GameObject protocolUiRoot)
        {
            if (protocolUiRoot == null)
            {
                return null;
            }

            RuntimeProtocolInstructionNarrator narrator =
                protocolUiRoot.GetComponentInChildren<RuntimeProtocolInstructionNarrator>(true);
            if (narrator == null)
            {
                var host = new GameObject("RuntimeProtocolInstructionNarrator");
                host.transform.SetParent(protocolUiRoot.transform, false);
                narrator = host.AddComponent<RuntimeProtocolInstructionNarrator>();
            }

            narrator.EnsureAudioSource();
            return narrator;
        }

        public void PlayInstruction(string conditionId, bool forceRestart = false)
        {
            PlayInstruction(conditionId, "Prueba", forceRestart);
        }

        public void PlayInstruction(string conditionId, string visibleTrialLabel, bool forceRestart = false)
        {
            string shortCode = ToShortCode(conditionId);
            string clipId = ResolveInstructionClipId(shortCode, visibleTrialLabel);
            if (string.IsNullOrWhiteSpace(clipId))
            {
                Log("experiment_instruction_narration_missing_clip", shortCode, string.Empty, false, "unknown_condition");
                return;
            }

            EnsureAudioSource();
            if (_audioSource == null)
            {
                Log("experiment_instruction_narration_missing_clip", shortCode, clipId, false, "missing_audio_source");
                return;
            }

            if (!forceRestart &&
                _audioSource.isPlaying &&
                string.Equals(_activeConditionId, shortCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_activeClipId, clipId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            AudioClip clip = Resources.Load<AudioClip>(ResourceRoot + clipId);
            bool available = clip != null;
            Log("experiment_instruction_narration_available", shortCode, clipId, available, available ? string.Empty : "clip_not_found");
            if (!available)
            {
                Debug.LogWarning($"[P45C-06][InstructionNarrator] Missing Resources clip {ResourceRoot}{clipId}. UI flow continues without narration.");
                Log("experiment_instruction_narration_missing_clip", shortCode, clipId, false, "clip_not_found");
                return;
            }

            StopNarration("restart_or_condition_change");
            _activeConditionId = shortCode;
            _activeClipId = clipId;
            _audioSource.clip = clip;
            _audioSource.volume = Mathf.Clamp01(_volume);
            _audioSource.loop = false;
            _audioSource.spatialBlend = 0f;
            _audioSource.playOnAwake = false;
            _audioSource.Play();
            _completionPending = true;
            Log("experiment_instruction_narration_started", shortCode, clipId, true, string.Empty);
        }

        public void PlayThankYou(bool forceRestart = true)
        {
            PlayClip("THANK_YOU", "instruction_thank_you", forceRestart);
        }

        public void StopNarration(string reason)
        {
            if (_audioSource == null || (!_audioSource.isPlaying && !_completionPending))
            {
                return;
            }

            string conditionId = _activeConditionId;
            string clipId = _activeClipId;
            _audioSource.Stop();
            _completionPending = false;
            Log("experiment_instruction_narration_stopped", conditionId, clipId, !string.IsNullOrWhiteSpace(clipId), reason ?? string.Empty);
        }

        private void Update()
        {
            if (!_completionPending || _audioSource == null || _audioSource.isPlaying)
            {
                return;
            }

            _completionPending = false;
            Log("experiment_instruction_narration_completed", _activeConditionId, _activeClipId, true, string.Empty);
        }

        private void OnDisable()
        {
            StopNarration("component_disabled");
        }

        private void EnsureAudioSource()
        {
            if (_audioSource == null)
            {
                _audioSource = GetComponent<AudioSource>();
            }

            if (_audioSource == null)
            {
                _audioSource = gameObject.AddComponent<AudioSource>();
            }

            _audioSource.playOnAwake = false;
            _audioSource.loop = false;
            _audioSource.spatialBlend = 0f;
        }

        public static string ResolveInstructionClipId(string conditionId, string visibleTrialLabel)
        {
            return ExperimentInstructionClipResolver.Resolve(conditionId, visibleTrialLabel);
        }

        private void PlayClip(string conditionId, string clipId, bool forceRestart)
        {
            if (string.IsNullOrWhiteSpace(clipId))
            {
                Log("experiment_instruction_narration_missing_clip", conditionId, string.Empty, false, "missing_clip_id");
                return;
            }

            EnsureAudioSource();
            if (_audioSource == null)
            {
                Log("experiment_instruction_narration_missing_clip", conditionId, clipId, false, "missing_audio_source");
                return;
            }

            if (!forceRestart &&
                _audioSource.isPlaying &&
                string.Equals(_activeConditionId, conditionId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_activeClipId, clipId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            AudioClip clip = Resources.Load<AudioClip>(ResourceRoot + clipId);
            bool available = clip != null;
            Log("experiment_instruction_narration_available", conditionId, clipId, available, available ? string.Empty : "clip_not_found");
            if (!available)
            {
                Debug.LogWarning($"[P45C-06][InstructionNarrator] Missing Resources clip {ResourceRoot}{clipId}. UI flow continues without narration.");
                Log("experiment_instruction_narration_missing_clip", conditionId, clipId, false, "clip_not_found");
                return;
            }

            StopNarration("restart_or_condition_change");
            _activeConditionId = conditionId;
            _activeClipId = clipId;
            _audioSource.clip = clip;
            _audioSource.volume = Mathf.Clamp01(_volume);
            _audioSource.loop = false;
            _audioSource.spatialBlend = 0f;
            _audioSource.playOnAwake = false;
            _audioSource.Play();
            _completionPending = true;
            Log("experiment_instruction_narration_started", conditionId, clipId, true, string.Empty);
        }

        private static string ToShortCode(string conditionId)
        {
            if (string.IsNullOrWhiteSpace(conditionId))
            {
                return string.Empty;
            }

            string value = conditionId.Trim().ToUpperInvariant();
            if (value.Contains("C00"))
            {
                return "C00";
            }

            if (value.Contains("C10"))
            {
                return "C10";
            }

            return value.Contains("C11") ? "C11" : value;
        }

        private static void Log(string eventType, string conditionId, string clipId, bool clipAvailable, string reason)
        {
            var payload = new Dictionary<string, object>
            {
                ["condition_id"] = conditionId ?? string.Empty,
                ["clip_id"] = clipId ?? string.Empty,
                ["instruction_clip_id"] = clipId ?? string.Empty,
                ["backend"] = BackendName,
                ["voice_source"] = VoiceSource,
                ["clip_available"] = clipAvailable,
                ["runtime_platform"] = Application.platform.ToString(),
                ["is_android_safe"] = true,
                ["uses_robot_tts"] = UsesRobotTts,
                ["reason"] = reason ?? string.Empty
            };
            TiagoExperimentTelemetry.LogEvent(eventType, payload);
            Debug.Log($"[P45C-06][InstructionNarrator] {eventType} | condition_id={conditionId} | clip_id={clipId} | clip_available={clipAvailable} | uses_robot_tts={UsesRobotTts}");
        }
    }
}
