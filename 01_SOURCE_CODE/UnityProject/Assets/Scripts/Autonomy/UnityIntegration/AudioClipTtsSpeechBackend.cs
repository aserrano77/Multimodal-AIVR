using Autonomy.Domain;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    public enum TtsBackendSelection
    {
        Auto,
        WindowsSapi,
        AudioClips,
        DiagnosticNoSpeech
    }

    public enum ResolvedTtsBackendKind
    {
        WindowsSapi,
        AudioClips,
        DiagnosticNoSpeech
    }

    public static class RobotVoiceClipTextKey
    {
        public static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            StringBuilder builder = new();
            bool previousSeparator = false;
            foreach (char character in decomposed)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(character);
                    previousSeparator = false;
                }
                else if (!previousSeparator && builder.Length > 0)
                {
                    builder.Append('_');
                    previousSeparator = true;
                }
            }

            if (builder.Length > 0 && builder[builder.Length - 1] == '_')
            {
                builder.Length--;
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }

    [Serializable]
    public sealed class RobotVoiceClipManifest
    {
        public string culture = "es-ES";
        public RobotVoiceClipEntry[] clips = Array.Empty<RobotVoiceClipEntry>();
    }

    [Serializable]
    public sealed class RobotVoiceClipEntry
    {
        public string text = string.Empty;
        public string key = string.Empty;
        public string clipId = string.Empty;
        public string resourcePath = string.Empty;
        public string category = string.Empty;
        public bool critical;

        [NonSerialized] public AudioClip Clip;

        public string EffectiveKey => string.IsNullOrWhiteSpace(key)
            ? RobotVoiceClipTextKey.Normalize(text)
            : key.Trim();
    }

    public sealed class AudioClipTtsSpeechBackend : ITtsSpeechBackend
    {
        public const string DefaultManifestResourcePath = "RobotVoice/robot_voice_clip_manifest";

        private readonly AudioSource _audioSource;
        private readonly Action<string, Dictionary<string, object>> _logEvent;
        private readonly Dictionary<string, RobotVoiceClipEntry> _entriesByKey = new(StringComparer.Ordinal);
        private string _unavailableReason = "not_initialized";
        private bool _manifestLoaded;
        private float _volume = 1f;

        public AudioClipTtsSpeechBackend(
            AudioSource audioSource,
            string manifestResourcePath,
            Action<string, Dictionary<string, object>> logEvent)
        {
            _audioSource = audioSource;
            _logEvent = logEvent;
            LoadManifest(string.IsNullOrWhiteSpace(manifestResourcePath) ? DefaultManifestResourcePath : manifestResourcePath.Trim());
        }

        public AudioClipTtsSpeechBackend(
            AudioSource audioSource,
            IEnumerable<RobotVoiceClipEntry> entries,
            Action<string, Dictionary<string, object>> logEvent)
        {
            _audioSource = audioSource;
            _logEvent = logEvent;
            LoadEntries(entries, "test_entries");
        }

        public bool IsAvailable => _audioSource != null && _manifestLoaded && _entriesByKey.Count > 0;
        public bool IsSpeaking => _audioSource != null && _audioSource.isPlaying;
        public string UnavailableReason => IsAvailable ? string.Empty : _unavailableReason;
        public string BackendMode => "audio_clips";

        public void Configure(string languageOrVoice, float volume, float rate)
        {
            _volume = Mathf.Clamp01(volume);
            if (_audioSource != null)
            {
                _audioSource.playOnAwake = false;
                _audioSource.volume = _volume;
            }
        }

        public bool TrySpeak(string text, out string failureReason)
        {
            failureReason = string.Empty;
            if (!IsAvailable)
            {
                failureReason = _unavailableReason;
                return false;
            }

            string key = RobotVoiceClipTextKey.Normalize(text);
            if (!_entriesByKey.TryGetValue(key, out RobotVoiceClipEntry entry))
            {
                failureReason = $"audio_clip_missing:{key}";
                Log("tts_audio_clip_missing", text, key, string.Empty, failureReason);
                return false;
            }

            AudioClip clip = entry.Clip;
            if (clip == null)
            {
                failureReason = $"audio_clip_reference_missing:{key}";
                Log("tts_audio_clip_missing", text, key, entry.resourcePath, failureReason);
                return false;
            }

            _audioSource.Stop();
            _audioSource.clip = clip;
            _audioSource.volume = _volume;
            _audioSource.Play();
            Log("tts_audio_clip_started", text, key, entry.resourcePath, "audio_clip_play");
            return true;
        }

        public void Tick()
        {
        }

        public void Stop()
        {
            if (_audioSource != null)
            {
                _audioSource.Stop();
            }
        }

        public bool ContainsClipForText(string text)
        {
            return _entriesByKey.ContainsKey(RobotVoiceClipTextKey.Normalize(text));
        }

        private void LoadManifest(string manifestResourcePath)
        {
            TextAsset manifestAsset = Resources.Load<TextAsset>(manifestResourcePath);
            if (manifestAsset == null)
            {
                _manifestLoaded = false;
                _unavailableReason = $"audio_clip_manifest_not_found:{manifestResourcePath}";
                Log("tts_audio_clip_manifest_missing", string.Empty, string.Empty, manifestResourcePath, _unavailableReason);
                return;
            }

            try
            {
                RobotVoiceClipManifest manifest = JsonUtility.FromJson<RobotVoiceClipManifest>(manifestAsset.text);
                LoadEntries(manifest?.clips, manifestResourcePath);
            }
            catch (Exception ex)
            {
                _manifestLoaded = false;
                _unavailableReason = $"audio_clip_manifest_invalid:{ex.GetType().Name}:{ex.Message}";
                Log("tts_audio_clip_manifest_invalid", string.Empty, string.Empty, manifestResourcePath, _unavailableReason);
            }
        }

        private void LoadEntries(IEnumerable<RobotVoiceClipEntry> entries, string source)
        {
            _entriesByKey.Clear();
            _manifestLoaded = true;
            if (_audioSource == null)
            {
                _unavailableReason = "audio_source_missing";
                return;
            }

            int missingClipCount = 0;
            foreach (RobotVoiceClipEntry entry in entries ?? Array.Empty<RobotVoiceClipEntry>())
            {
                if (entry == null)
                {
                    continue;
                }

                string key = entry.EffectiveKey;
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (entry.Clip == null && !string.IsNullOrWhiteSpace(entry.resourcePath))
                {
                    entry.Clip = Resources.Load<AudioClip>(entry.resourcePath.Trim());
                }

                if (entry.Clip == null)
                {
                    missingClipCount++;
                    Log("tts_audio_clip_reference_missing", entry.text, key, entry.resourcePath, "clip_resource_not_loaded");
                    continue;
                }

                _entriesByKey[key] = entry;
            }

            _unavailableReason = _entriesByKey.Count == 0
                ? $"audio_clip_library_empty_or_missing:{source}:missing_clips={missingClipCount}"
                : string.Empty;
            Log(
                _entriesByKey.Count > 0 ? "tts_audio_clip_manifest_loaded" : "tts_audio_clip_manifest_empty",
                string.Empty,
                string.Empty,
                source,
                _unavailableReason);
        }

        private void Log(string eventType, string text, string key, string resourcePath, string reason)
        {
            _logEvent?.Invoke(
                eventType,
                new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["tts_text"] = text ?? string.Empty,
                    ["clip_key"] = key ?? string.Empty,
                    ["clip_resource_path"] = resourcePath ?? string.Empty,
                    ["backend_mode"] = BackendMode,
                    ["loaded_clip_count"] = _entriesByKey.Count
                });
        }
    }
}
