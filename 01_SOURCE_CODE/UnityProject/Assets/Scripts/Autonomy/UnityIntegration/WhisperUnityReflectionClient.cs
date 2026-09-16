using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Autonomy.Domain;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Autonomy.UnityIntegration
{
    internal sealed class WhisperUnityReflectionClient
    {
        private const string WhisperManagerTypeName = "Whisper.WhisperManager";
        private static readonly string[] TranscriptMemberNames =
        {
            "Result",
            "Text",
            "Transcript",
            "Transcription",
            "Output",
            "Message"
        };

        private static readonly string[] SegmentMemberNames =
        {
            "Segments",
            "Segment",
            "Results",
            "Items"
        };

        private readonly Object _configuredManager;
        private readonly ASRDebugOptions _debugOptions;

        public WhisperUnityReflectionClient(Object configuredManager, ASRDebugOptions debugOptions = null)
        {
            _configuredManager = configuredManager;
            _debugOptions = debugOptions ?? new ASRDebugOptions();
        }

        public bool TryResolveManager(out object manager, out string errorReason)
        {
            manager = null;
            errorReason = string.Empty;

            Type managerType = ResolveWhisperManagerType();
            if (managerType == null)
            {
                errorReason = "whisper_unity_type_not_found";
                return false;
            }

            if (_configuredManager != null && managerType.IsInstanceOfType(_configuredManager))
            {
                manager = _configuredManager;
                return true;
            }

            manager = Object.FindFirstObjectByType(managerType);
            if (manager == null)
            {
                errorReason = "whisper_manager_not_found_in_scene";
                return false;
            }

            return true;
        }

        public bool TryResolveLoadState(out WhisperModelLoadState loadState, out string errorReason)
        {
            loadState = default;
            if (!TryResolveManager(out object manager, out errorReason))
            {
                return false;
            }

            Type type = manager.GetType();
            loadState = new WhisperModelLoadState(
                managerResolved: true,
                isLoaded: ReadBoolProperty(type, manager, "IsLoaded"),
                isLoading: ReadBoolProperty(type, manager, "IsLoading"),
                modelPath: ReadStringMember(manager, "ModelPath"),
                reason: ReadBoolProperty(type, manager, "IsLoaded")
                    ? "loaded"
                    : ReadBoolProperty(type, manager, "IsLoading")
                        ? "loading"
                        : "not_loaded");
            return true;
        }

        public async Task<ASRResult> TranscribeAsync(
            AudioClip clip,
            ASRRequest request,
            string microphoneDevice,
            int sampleRate,
            long durationMs)
        {
            if (clip == null)
            {
                return ASRResult.Failed(
                    request.UtteranceId,
                    ASRBackend.WhisperUnity,
                    "audio_clip_missing",
                    request.Language,
                    durationMs,
                    microphoneDevice,
                    sampleRate);
            }

            if (!TryResolveManager(out object manager, out string resolveError))
            {
                return ASRResult.Failed(
                    request.UtteranceId,
                    ASRBackend.WhisperUnity,
                    resolveError,
                    request.Language,
                    durationMs,
                    microphoneDevice,
                    sampleRate);
            }

            try
            {
                ApplyLanguage(manager, request.Language);

                if (!TryResolveLoadState(out WhisperModelLoadState loadState, out string loadStateError) || !loadState.IsLoaded)
                {
                    return ASRResult.Failed(
                        request.UtteranceId,
                        ASRBackend.WhisperUnity,
                        string.IsNullOrWhiteSpace(loadStateError)
                            ? "whisper_model_not_ready"
                            : "whisper_model_not_ready: " + loadStateError,
                        request.Language,
                        durationMs,
                        microphoneDevice,
                        sampleRate);
                }

                MethodInfo getTextMethod = manager.GetType().GetMethod("GetTextAsync", new[] { typeof(AudioClip) });
                if (getTextMethod == null)
                {
                    return ASRResult.Failed(
                        request.UtteranceId,
                        ASRBackend.WhisperUnity,
                        "whisper_method_not_found",
                        request.Language,
                        durationMs,
                        microphoneDevice,
                        sampleRate);
                }

                object taskObject = getTextMethod.Invoke(manager, new object[] { clip });
                if (taskObject is not Task task)
                {
                    return ASRResult.Failed(
                        request.UtteranceId,
                        ASRBackend.WhisperUnity,
                        "whisper_get_text_async_invalid_return",
                        request.Language,
                        durationMs,
                        microphoneDevice,
                        sampleRate);
                }

                await task;
                object whisperResult = GetTaskResult(task);
                if (whisperResult == null)
                {
                    return ASRResult.Failed(
                        request.UtteranceId,
                        ASRBackend.WhisperUnity,
                        "whisper_transcription_returned_null",
                        request.Language,
                        durationMs,
                        microphoneDevice,
                        sampleRate);
                    }

                TranscriptExtraction extraction = ExtractTranscript(whisperResult);
                LogWhisperResultDiagnostics(whisperResult, extraction);

                string language = ReadStringMember(whisperResult, "Language");
                if (string.IsNullOrWhiteSpace(language))
                {
                    language = request.Language;
                }

                if (extraction.Failed)
                {
                    return ASRResult.Failed(
                        request.UtteranceId,
                        ASRBackend.WhisperUnity,
                        extraction.ErrorReason,
                        language,
                        durationMs,
                        microphoneDevice,
                        sampleRate);
                }

                if (!extraction.FoundRecognizedMember)
                {
                    return ASRResult.Failed(
                        request.UtteranceId,
                        ASRBackend.WhisperUnity,
                        "whisper_result_property_not_found",
                        language,
                        durationMs,
                        microphoneDevice,
                        sampleRate);
                }

                if (string.IsNullOrWhiteSpace(extraction.Transcript))
                {
                    return ASRResult.Failed(
                        request.UtteranceId,
                        ASRBackend.WhisperUnity,
                        "whisper_empty_transcript",
                        language,
                        durationMs,
                        microphoneDevice,
                        sampleRate);
                }

                return ASRResult.Successful(
                    request.UtteranceId,
                    ASRBackend.WhisperUnity,
                    extraction.Transcript,
                    language,
                    durationMs,
                    microphoneDevice,
                    sampleRate);
            }
            catch (Exception ex)
            {
                return ASRResult.Failed(
                    request.UtteranceId,
                    ASRBackend.WhisperUnity,
                    "whisper_result_extraction_failed: " + ex.GetBaseException().Message,
                    request.Language,
                    durationMs,
                    microphoneDevice,
                    sampleRate);
            }
        }

        private static Type ResolveWhisperManagerType()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(WhisperManagerTypeName, throwOnError: false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static void ApplyLanguage(object manager, string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return;
            }

            FieldInfo languageField = manager.GetType().GetField("language", BindingFlags.Instance | BindingFlags.Public);
            if (languageField != null && languageField.FieldType == typeof(string))
            {
                languageField.SetValue(manager, language);
            }
        }

        private static object GetTaskResult(Task task)
        {
            PropertyInfo resultProperty = task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
            return resultProperty != null ? resultProperty.GetValue(task) : null;
        }

        private static string ReadStringMember(object source, string memberName)
        {
            Type type = source.GetType();
            PropertyInfo property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public);
            object value = property != null ? property.GetValue(source) : null;
            if (value == null)
            {
                FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public);
                value = field != null ? field.GetValue(source) : null;
            }

            return value != null ? value.ToString() : string.Empty;
        }

        private static bool ReadBoolProperty(Type type, object instance, string propertyName)
        {
            PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            object value = property != null ? property.GetValue(instance) : null;
            return value is bool boolValue && boolValue;
        }

        private static TranscriptExtraction ExtractTranscript(object whisperResult)
        {
            try
            {
                Type type = whisperResult.GetType();
                bool foundRecognizedMember = false;

                foreach (string memberName in TranscriptMemberNames)
                {
                    if (!TryReadMember(type, whisperResult, memberName, out object value))
                    {
                        continue;
                    }

                    foundRecognizedMember = true;
                    string text = value as string ?? value?.ToString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return TranscriptExtraction.Success(text.Trim(), memberName, foundRecognizedMember, -1, string.Empty);
                    }
                }

                foreach (string memberName in SegmentMemberNames)
                {
                    if (!TryReadMember(type, whisperResult, memberName, out object value))
                    {
                        continue;
                    }

                    foundRecognizedMember = true;
                    if (TryExtractSegmentsText(value, out string segmentText, out int segmentCount, out string segmentPreview) &&
                        !string.IsNullOrWhiteSpace(segmentText))
                    {
                        return TranscriptExtraction.Success(
                            segmentText.Trim(),
                            memberName,
                            foundRecognizedMember,
                            segmentCount,
                            segmentPreview);
                    }

                    return TranscriptExtraction.Success(
                        string.Empty,
                        memberName,
                        foundRecognizedMember,
                        segmentCount,
                        segmentPreview);
                }

                return TranscriptExtraction.Success(string.Empty, string.Empty, foundRecognizedMember, -1, string.Empty);
            }
            catch (Exception ex)
            {
                return TranscriptExtraction.Failure("whisper_result_extraction_failed: " + ex.GetBaseException().Message);
            }
        }

        private static bool TryReadMember(Type type, object source, string memberName, out object value)
        {
            value = null;
            PropertyInfo property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public);
            if (property != null)
            {
                value = property.GetValue(source);
                return true;
            }

            FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public);
            if (field != null)
            {
                value = field.GetValue(source);
                return true;
            }

            return false;
        }

        private static bool TryExtractSegmentsText(object segmentsObject, out string transcript, out int segmentCount, out string segmentPreview)
        {
            transcript = string.Empty;
            segmentCount = -1;
            segmentPreview = string.Empty;

            if (segmentsObject == null)
            {
                segmentCount = 0;
                return true;
            }

            if (segmentsObject is not IEnumerable enumerable || segmentsObject is string)
            {
                return false;
            }

            StringBuilder builder = new();
            List<string> previews = new();
            int count = 0;
            foreach (object segment in enumerable)
            {
                count++;
                if (segment == null)
                {
                    continue;
                }

                string segmentText = string.Empty;
                Type segmentType = segment.GetType();
                foreach (string memberName in TranscriptMemberNames)
                {
                    if (TryReadMember(segmentType, segment, memberName, out object value))
                    {
                        segmentText = value as string ?? value?.ToString() ?? string.Empty;
                        break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(segmentText))
                {
                    builder.Append(segmentText);
                    if (previews.Count < 3)
                    {
                        previews.Add(Limit(segmentText.Trim(), 80));
                    }
                }
            }

            segmentCount = count;
            transcript = builder.ToString();
            segmentPreview = string.Join(" | ", previews);
            return true;
        }

        private void LogWhisperResultDiagnostics(object whisperResult, TranscriptExtraction extraction)
        {
            if (!_debugOptions.DebugLogging)
            {
                return;
            }

            Type type = whisperResult.GetType();
            PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            string toStringValue = SafeToString(whisperResult);

            _debugOptions.Log?.Invoke(
                "Whisper result diagnostics | " +
                $"whisper_result_type={type.FullName} " +
                $"whisper_result_public_properties={FormatMembers(properties.Select(p => p.Name + ':' + p.PropertyType.Name))} " +
                $"whisper_result_public_fields={FormatMembers(fields.Select(f => f.Name + ':' + f.FieldType.Name))} " +
                $"non_null_values={FormatNonNullValues(whisperResult, properties, fields)} " +
                $"transcript_extraction_source={extraction.Source} " +
                $"raw_transcript_length={extraction.RawLength} " +
                $"raw_transcript_preview='{Limit(extraction.Transcript, 120)}' " +
                $"segments_count={extraction.SegmentsCount} " +
                $"segment_text_preview='{Limit(extraction.SegmentPreview, 120)}' " +
                $"to_string='{Limit(toStringValue, 300)}'");
        }

        private static string FormatMembers(IEnumerable<string> members)
        {
            return "[" + string.Join(", ", members) + "]";
        }

        private static string FormatNonNullValues(object source, PropertyInfo[] properties, FieldInfo[] fields)
        {
            List<string> values = new();
            foreach (PropertyInfo property in properties)
            {
                object value = SafeGet(() => property.GetValue(source));
                if (value != null)
                {
                    values.Add($"{property.Name}={Limit(SummarizeValue(value), 80)}");
                }
            }

            foreach (FieldInfo field in fields)
            {
                object value = SafeGet(() => field.GetValue(source));
                if (value != null)
                {
                    values.Add($"{field.Name}={Limit(SummarizeValue(value), 80)}");
                }
            }

            return "[" + string.Join(", ", values) + "]";
        }

        private static object SafeGet(Func<object> getter)
        {
            try
            {
                return getter();
            }
            catch (Exception ex)
            {
                return "<read_failed:" + ex.GetBaseException().Message + ">";
            }
        }

        private static string SummarizeValue(object value)
        {
            if (value is string stringValue)
            {
                return stringValue;
            }

            if (value is ICollection collection)
            {
                return $"{value.GetType().Name}(Count={collection.Count})";
            }

            if (value is IEnumerable && value is not string)
            {
                return value.GetType().Name;
            }

            return value.ToString();
        }

        private static string SafeToString(object source)
        {
            try
            {
                return source?.ToString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                return "<to_string_failed:" + ex.GetBaseException().Message + ">";
            }
        }

        private static string Limit(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            value = value.Replace("\r", "\\r").Replace("\n", "\\n");
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
        }

        private readonly struct TranscriptExtraction
        {
            private TranscriptExtraction(
                string transcript,
                string source,
                bool foundRecognizedMember,
                int segmentsCount,
                string segmentPreview,
                bool failed,
                string errorReason)
            {
                Transcript = transcript ?? string.Empty;
                Source = source ?? string.Empty;
                FoundRecognizedMember = foundRecognizedMember;
                SegmentsCount = segmentsCount;
                SegmentPreview = segmentPreview ?? string.Empty;
                Failed = failed;
                ErrorReason = errorReason ?? string.Empty;
            }

            public string Transcript { get; }
            public string Source { get; }
            public bool FoundRecognizedMember { get; }
            public int SegmentsCount { get; }
            public string SegmentPreview { get; }
            public bool Failed { get; }
            public string ErrorReason { get; }
            public int RawLength => Transcript.Length;

            public static TranscriptExtraction Success(
                string transcript,
                string source,
                bool foundRecognizedMember,
                int segmentsCount,
                string segmentPreview)
            {
                return new TranscriptExtraction(
                    transcript,
                    source,
                    foundRecognizedMember,
                    segmentsCount,
                    segmentPreview,
                    false,
                    string.Empty);
            }

            public static TranscriptExtraction Failure(string errorReason)
            {
                return new TranscriptExtraction(
                    string.Empty,
                    string.Empty,
                    false,
                    -1,
                    string.Empty,
                    true,
                    errorReason);
            }
        }
    }
}
