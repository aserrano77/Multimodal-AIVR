using System;
using System.Collections.Generic;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class ExperimentGlobalInstructionsAudioPlayer : MonoBehaviour
    {
        private const string ResourceRoot = "ExperimentInstructions/";
        private const string BackendName = "resources_audio_clip";
        private const string VoiceSource = "global_instructions_wizard";

        private static readonly string[] ClipIds =
        {
            "global_instruction_page_01_structure",
            "global_instruction_page_02_controls",
            "global_instruction_page_03_wall_panel",
            "global_instruction_page_04_pause",
            "global_instruction_page_05_questionnaire",
            "global_instruction_page_06_rules"
        };

        [SerializeField] private AudioSource _audioSource;
        [SerializeField] private float _volume = 0.92f;

        private int _activePageIndex = -1;
        private string _activeClipId = string.Empty;
        private bool _completionPending;

        public static int PageCount => ClipIds.Length;

        public static ExperimentGlobalInstructionsAudioPlayer EnsureAttached(GameObject root)
        {
            if (root == null)
            {
                return null;
            }

            ExperimentGlobalInstructionsAudioPlayer player =
                root.GetComponentInChildren<ExperimentGlobalInstructionsAudioPlayer>(true);
            if (player == null)
            {
                var host = new GameObject("ExperimentGlobalInstructionsAudioPlayer");
                host.transform.SetParent(root.transform, false);
                player = host.AddComponent<ExperimentGlobalInstructionsAudioPlayer>();
            }

            player.EnsureAudioSource();
            return player;
        }

        public static string ClipIdForPage(int pageIndex)
        {
            return pageIndex >= 0 && pageIndex < ClipIds.Length ? ClipIds[pageIndex] : string.Empty;
        }

        public static string ResourcePathForPage(int pageIndex)
        {
            string clipId = ClipIdForPage(pageIndex);
            return string.IsNullOrWhiteSpace(clipId) ? string.Empty : ResourceRoot + clipId;
        }

        public void PlayPage(int pageIndex, bool forceRestart = false)
        {
            string clipId = ClipIdForPage(pageIndex);
            Log("global_instructions_audio_play_requested", pageIndex, clipId, false, string.Empty);
            if (string.IsNullOrWhiteSpace(clipId))
            {
                Log("global_instructions_audio_error", pageIndex, clipId, false, "invalid_page_index");
                return;
            }

            EnsureAudioSource();
            if (_audioSource == null)
            {
                Log("global_instructions_audio_error", pageIndex, clipId, false, "missing_audio_source");
                return;
            }

            if (!forceRestart &&
                _audioSource.isPlaying &&
                _activePageIndex == pageIndex &&
                string.Equals(_activeClipId, clipId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            AudioClip clip = Resources.Load<AudioClip>(ResourceRoot + clipId);
            if (clip == null)
            {
                Log("global_instructions_audio_missing", pageIndex, clipId, false, "clip_not_found");
                Debug.LogWarning($"[P46G-INSTRUCTIONS] global_instructions_audio_missing | page={pageIndex + 1} clip_id={clipId} resource_path={ResourceRoot + clipId}");
                return;
            }

            StopAudio("page_changed");
            _activePageIndex = pageIndex;
            _activeClipId = clipId;
            _audioSource.clip = clip;
            _audioSource.volume = Mathf.Clamp01(_volume);
            _audioSource.loop = false;
            _audioSource.spatialBlend = 0f;
            _audioSource.playOnAwake = false;
            _audioSource.Play();
            _completionPending = true;
            Log("global_instructions_audio_started", pageIndex, clipId, true, string.Empty);
        }

        public void StopAudio(string reason)
        {
            if (_audioSource == null || (!_audioSource.isPlaying && !_completionPending))
            {
                return;
            }

            int pageIndex = _activePageIndex;
            string clipId = _activeClipId;
            _audioSource.Stop();
            _completionPending = false;
            Log("global_instructions_audio_stopped", pageIndex, clipId, !string.IsNullOrWhiteSpace(clipId), reason ?? string.Empty);
        }

        private void Update()
        {
            if (!_completionPending || _audioSource == null || _audioSource.isPlaying)
            {
                return;
            }

            _completionPending = false;
        }

        private void OnDisable()
        {
            StopAudio("component_disabled");
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

        private static void Log(string eventType, int pageIndex, string clipId, bool clipAvailable, string reason)
        {
            var payload = new Dictionary<string, object>
            {
                ["event_name"] = eventType ?? string.Empty,
                ["page_index"] = pageIndex,
                ["page_number"] = pageIndex >= 0 ? pageIndex + 1 : 0,
                ["clip_id"] = clipId ?? string.Empty,
                ["resource_path"] = string.IsNullOrWhiteSpace(clipId) ? string.Empty : ResourceRoot + clipId,
                ["backend"] = BackendName,
                ["voice_source"] = VoiceSource,
                ["clip_available"] = clipAvailable,
                ["runtime_platform"] = Application.platform.ToString(),
                ["is_android_safe"] = true,
                ["uses_robot_tts"] = false,
                ["reason"] = reason ?? string.Empty
            };
            TiagoExperimentTelemetry.LogEvent(eventType, payload);
            Debug.Log($"[P46G-INSTRUCTIONS] {eventType} | page={payload["page_number"]} clip_id={clipId ?? string.Empty} clip_available={clipAvailable} reason={reason ?? string.Empty}");
        }
    }
}
