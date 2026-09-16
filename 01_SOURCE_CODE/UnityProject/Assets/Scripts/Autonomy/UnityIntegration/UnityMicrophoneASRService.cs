using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autonomy.Domain;
using Autonomy.Services;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Autonomy.UnityIntegration
{
    public sealed class UnityMicrophoneASRService : IASRService, IDisposable
    {
        private readonly WhisperUnityReflectionClient _whisperClient;
        private readonly SherpaOnnxASRBackend _sherpaBackend;
        private readonly bool _ownsSherpaBackend;
        private readonly Func<string> _manualTranscriptProvider;
        private readonly Func<string> _conditionIdProvider;
        private readonly ASRDebugOptions _debugOptions;
        private readonly Stopwatch _stopwatch = new();
        private ASRRequest _activeRequest;
        private AudioClip _clip;
        private string _microphoneStartDevice;
        private string _resolvedMicrophoneDevice;
        private MicrophoneSelection _microphoneSelection;
        private ASRBackend _activeBackend = ASRBackend.Unsupported;
        private ASRBackendSelection _lastBackendSelection;
        private ASRBackendPreflightResult _lastBackendPreflight;

        public UnityMicrophoneASRService(
            Object whisperManager,
            Func<string> manualTranscriptProvider,
            ASRDebugOptions debugOptions = null,
            SherpaOnnxBackendOptions sherpaOptions = null,
            Func<string> conditionIdProvider = null,
            SherpaOnnxASRBackend sharedSherpaBackend = null)
        {
            _manualTranscriptProvider = manualTranscriptProvider;
            _conditionIdProvider = conditionIdProvider;
            _debugOptions = debugOptions ?? new ASRDebugOptions();
            _whisperClient = new WhisperUnityReflectionClient(whisperManager, _debugOptions);
            _sherpaBackend = sharedSherpaBackend ?? new SherpaOnnxASRBackend(sherpaOptions, _debugOptions);
            _ownsSherpaBackend = sharedSherpaBackend == null;
        }

        public ASRStatus Status { get; private set; } = ASRStatus.Idle;
        public string ActiveUtteranceId => _activeRequest != null ? _activeRequest.UtteranceId : string.Empty;
        public ASRBackend ActiveBackend => _activeBackend;
        public ASRBackendSelection LastBackendSelection => _lastBackendSelection;
        public ASRBackendPreflightResult LastBackendPreflight => _lastBackendPreflight;

        public void Dispose()
        {
            if (_ownsSherpaBackend)
            {
                _sherpaBackend?.Dispose();
            }
        }

        public async Task<ASRResult> StartListeningAsync(ASRRequest request)
        {
            if (Status == ASRStatus.Listening || Status == ASRStatus.Transcribing)
            {
                return ASRResult.Failed(
                    ActiveUtteranceId,
                    _activeBackend,
                    "asr_already_listening",
                    request?.Language ?? string.Empty,
                    _stopwatch.ElapsedMilliseconds,
                    request?.MicrophoneDevice ?? string.Empty,
                    request?.SampleRate ?? 0);
            }

            _activeRequest = request ?? new ASRRequest(ASRBackend.Auto, "es", string.Empty, 16000, 4f);
            await PrepareSherpaModelForSelectionAsync(_activeRequest);
            _lastBackendSelection = ResolveBackendSelection(_activeRequest.RequestedBackend);
            _lastBackendPreflight = _lastBackendSelection.Preflight;
            _activeBackend = _lastBackendSelection.EffectiveBackend;
            _microphoneSelection = ResolveMicrophoneSelection(_activeRequest.MicrophoneDevice, GetMicrophoneDevices());
            _resolvedMicrophoneDevice = _microphoneSelection.SelectedDevice;
            _microphoneStartDevice = string.IsNullOrWhiteSpace(_resolvedMicrophoneDevice)
                ? null
                : _resolvedMicrophoneDevice;
            _stopwatch.Restart();

            LogStartDiagnostics();

            if (_activeBackend == ASRBackend.ManualStub)
            {
                Status = ASRStatus.Listening;
                return BuildStartedResult(_activeBackend);
            }

            if (_activeBackend != ASRBackend.WhisperUnity && _activeBackend != ASRBackend.SherpaOnnx)
            {
                Status = ASRStatus.Failed;
                _stopwatch.Stop();
                return ASRResult.Failed(
                    _activeRequest.UtteranceId,
                    _activeBackend,
                    ResolveUnavailableReason(),
                    _activeRequest.Language,
                    0L,
                    _activeRequest.MicrophoneDevice,
                    _activeRequest.SampleRate);
            }

            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                LogMicrophoneSelectionEvent("voice_microphone_devices_detected", "no_microphones_detected");
                Status = ASRStatus.Failed;
                _stopwatch.Stop();
                return ASRResult.Failed(
                    _activeRequest.UtteranceId,
                    _activeBackend,
                    "microphone_device_not_found",
                    _activeRequest.Language,
                    0L,
                    _resolvedMicrophoneDevice,
                    _activeRequest.SampleRate);
            }

            try
            {
                int maxSeconds = Mathf.Max(1, Mathf.CeilToInt(_activeRequest.MaxCaptureSeconds));
                LogMicrophoneSelectionDiagnostics();
                _clip = Microphone.Start(_microphoneStartDevice, false, maxSeconds, Mathf.Max(8000, _activeRequest.SampleRate));
                Status = ASRStatus.Listening;
                return BuildStartedResult(_activeBackend);
            }
            catch (Exception ex)
            {
                Status = ASRStatus.Failed;
                _stopwatch.Stop();
                return ASRResult.Failed(
                    _activeRequest.UtteranceId,
                    _activeBackend,
                    ex.GetBaseException().Message,
                    _activeRequest.Language,
                    0L,
                    _resolvedMicrophoneDevice,
                    _activeRequest.SampleRate);
            }
        }

        public async Task<ASRResult> StopListeningAndTranscribeAsync(string manualTranscript = "")
        {
            if (Status != ASRStatus.Listening || _activeRequest == null)
            {
                Status = ASRStatus.Failed;
                return ASRResult.Failed(string.Empty, _activeBackend, "asr_not_listening");
            }

            Status = ASRStatus.Transcribing;
            _stopwatch.Stop();

            if (_activeBackend == ASRBackend.ManualStub)
            {
                string transcript = string.IsNullOrWhiteSpace(manualTranscript)
                    ? _manualTranscriptProvider?.Invoke()
                    : manualTranscript;
                return CompleteManualStub(transcript);
            }

            AudioClip clip = _clip;
            int microphonePosition = GetMicrophonePosition(_microphoneStartDevice);
            bool wasRecording = Microphone.IsRecording(_microphoneStartDevice);
            if (!string.IsNullOrEmpty(_microphoneStartDevice) || Microphone.IsRecording(null))
            {
                Microphone.End(_microphoneStartDevice);
            }

            if (clip == null)
            {
                ASRResult failed = ASRResult.Failed(
                    _activeRequest.UtteranceId,
                    _activeBackend,
                    "audio_clip_missing",
                    _activeRequest.Language,
                    _stopwatch.ElapsedMilliseconds,
                    _resolvedMicrophoneDevice,
                    _activeRequest.SampleRate);
                ResetActiveCapture();
                return failed;
            }

            CaptureSlice slice = BuildCaptureSlice(clip, microphonePosition);
            ASRAudioAnalysis analysis = ASRAudioDiagnostics.Analyze(
                slice.Samples,
                clip.channels,
                clip.frequency,
                _debugOptions.SilenceRmsThreshold,
                _debugOptions.NonSilentSampleThreshold);

            string diagnosticAudioPath = SaveDiagnosticWavIfRequested(slice.Samples, clip.channels, clip.frequency);
            LogStopDiagnostics(clip, microphonePosition, wasRecording, slice, analysis, diagnosticAudioPath);

            if (analysis.RecordedSamples == 0)
            {
                ASRResult failed = ASRResult.Failed(
                    _activeRequest.UtteranceId,
                    _activeBackend,
                    "microphone_no_recorded_samples",
                    _activeRequest.Language,
                    _stopwatch.ElapsedMilliseconds,
                    _resolvedMicrophoneDevice,
                    _activeRequest.SampleRate,
                    analysis,
                    diagnosticAudioPath);
                ResetActiveCapture();
                return failed;
            }

            if (analysis.SilenceDetected)
            {
                LogMicrophoneAudioTooQuiet(analysis, diagnosticAudioPath);
                ASRResult failed = ASRResult.Failed(
                    _activeRequest.UtteranceId,
                    _activeBackend,
                    "audio_too_quiet",
                    _activeRequest.Language,
                    _stopwatch.ElapsedMilliseconds,
                    _resolvedMicrophoneDevice,
                    _activeRequest.SampleRate,
                    analysis,
                    diagnosticAudioPath);
                ResetActiveCapture();
                return failed;
            }

            AudioClip trimmedClip = AudioClip.Create(
                $"{clip.name}_trimmed_{_activeRequest.UtteranceId}",
                slice.FrameCount,
                clip.channels,
                clip.frequency,
                false);
            trimmedClip.SetData(slice.Samples, 0);

            ASRResult result = await TranscribeWithActiveBackendAsync(
                trimmedClip,
                analysis,
                diagnosticAudioPath,
                _stopwatch.ElapsedMilliseconds);
            result = result.Success
                ? ASRResult.Successful(
                    result.UtteranceId,
                    result.Backend,
                    result.Transcript,
                    result.Language,
                    result.DurationMs,
                    result.MicrophoneDevice,
                    result.SampleRate,
                    result.Confidence,
                    analysis,
                    diagnosticAudioPath)
                : ASRResult.Failed(
                    result.UtteranceId,
                    result.Backend,
                    result.ErrorReason,
                    result.Language,
                    result.DurationMs,
                    result.MicrophoneDevice,
                    result.SampleRate,
                    analysis,
                    diagnosticAudioPath);

            Status = result.Success ? ASRStatus.Completed : ASRStatus.Failed;
            ResetActiveCapture();
            return result;
        }

        public async Task<ASRResult> TranscribeSegmentAsync(
            AudioClip clip,
            ASRRequest request,
            ASRAudioAnalysis analysis,
            string manualTranscript = "")
        {
            _activeRequest = request ?? new ASRRequest(ASRBackend.Auto, "es", string.Empty, 16000, 4f);
            await PrepareSherpaModelForSelectionAsync(_activeRequest);
            _lastBackendSelection = ResolveBackendSelection(_activeRequest.RequestedBackend);
            _lastBackendPreflight = _lastBackendSelection.Preflight;
            _activeBackend = _lastBackendSelection.EffectiveBackend;
            _microphoneSelection = ResolveMicrophoneSelection(_activeRequest.MicrophoneDevice, GetMicrophoneDevices());
            _resolvedMicrophoneDevice = _microphoneSelection.SelectedDevice;

            if (_activeBackend == ASRBackend.ManualStub)
            {
                string transcript = string.IsNullOrWhiteSpace(manualTranscript)
                    ? _manualTranscriptProvider?.Invoke()
                    : manualTranscript;
                ASRResult manualResult = string.IsNullOrWhiteSpace(transcript)
                    ? ASRResult.Failed(
                        _activeRequest.UtteranceId,
                        ASRBackend.ManualStub,
                        "manual_stub_empty_transcript",
                        _activeRequest.Language,
                        analysis.RecordedDurationMs,
                        _resolvedMicrophoneDevice,
                        _activeRequest.SampleRate,
                        analysis)
                    : ASRResult.Successful(
                        _activeRequest.UtteranceId,
                        ASRBackend.ManualStub,
                        transcript,
                        _activeRequest.Language,
                        analysis.RecordedDurationMs,
                        _resolvedMicrophoneDevice,
                        _activeRequest.SampleRate,
                        float.NaN,
                        analysis);
                Status = manualResult.Success ? ASRStatus.Completed : ASRStatus.Failed;
                ResetActiveCapture();
                return manualResult;
            }

            if (_activeBackend != ASRBackend.WhisperUnity && _activeBackend != ASRBackend.SherpaOnnx)
            {
                ASRResult failed = ASRResult.Failed(
                    _activeRequest.UtteranceId,
                    _activeBackend,
                    ResolveUnavailableReason(),
                    _activeRequest.Language,
                    analysis.RecordedDurationMs,
                    _resolvedMicrophoneDevice,
                    _activeRequest.SampleRate,
                    analysis);
                ResetActiveCapture();
                return failed;
            }

            if (clip == null)
            {
                ASRResult failed = ASRResult.Failed(
                    _activeRequest.UtteranceId,
                    _activeBackend,
                    "audio_clip_missing",
                    _activeRequest.Language,
                    analysis.RecordedDurationMs,
                    _resolvedMicrophoneDevice,
                    _activeRequest.SampleRate,
                    analysis);
                ResetActiveCapture();
                return failed;
            }

            if (_activeBackend == ASRBackend.WhisperUnity &&
                (!_whisperClient.TryResolveLoadState(out WhisperModelLoadState loadState, out string loadStateError) || !loadState.IsLoaded))
            {
                ASRResult failed = ASRResult.Failed(
                    _activeRequest.UtteranceId,
                    _activeBackend,
                    string.IsNullOrWhiteSpace(loadStateError)
                        ? "whisper_model_not_ready"
                        : "whisper_model_not_ready: " + loadStateError,
                    _activeRequest.Language,
                    analysis.RecordedDurationMs,
                    _resolvedMicrophoneDevice,
                    _activeRequest.SampleRate,
                    analysis);
                ResetActiveCapture();
                return failed;
            }

            string diagnosticAudioPath = SaveDiagnosticWavIfRequested(ReadClipSamples(clip), clip.channels, clip.frequency);
            ASRResult result = await TranscribeWithActiveBackendAsync(
                clip,
                analysis,
                diagnosticAudioPath,
                analysis.RecordedDurationMs);
            result = result.Success
                ? ASRResult.Successful(
                    result.UtteranceId,
                    result.Backend,
                    result.Transcript,
                    result.Language,
                    result.DurationMs,
                    result.MicrophoneDevice,
                    result.SampleRate,
                    result.Confidence,
                    analysis,
                    diagnosticAudioPath)
                : ASRResult.Failed(
                    result.UtteranceId,
                    result.Backend,
                    result.ErrorReason,
                    result.Language,
                    result.DurationMs,
                    result.MicrophoneDevice,
                    result.SampleRate,
                    analysis,
                    diagnosticAudioPath);

            Status = result.Success ? ASRStatus.Completed : ASRStatus.Failed;
            ResetActiveCapture();
            return result;
        }

        private ASRBackendSelection ResolveBackendSelection(ASRBackend requestedBackend)
        {
            ASRBackend normalized = ASRBackendSelectionPolicy.NormalizeRequestedBackend(requestedBackend);
            bool whisperAvailable = _whisperClient.TryResolveManager(out _, out _);
            ASRBackendPreflightResult sherpaPreflight =
                normalized == ASRBackend.Auto || normalized == ASRBackend.SherpaOnnx
                    ? _sherpaBackend.Preflight(requestedBackend, _activeRequest?.Language ?? "es")
                    : null;

            return ASRBackendSelectionPolicy.Select(
                requestedBackend,
                ExperimentDataPathResolver.IsAndroidRuntime(),
                whisperAvailable,
                sherpaPreflight,
                IsC11ExperimentalCondition(_conditionIdProvider?.Invoke()));
        }

        private async Task PrepareSherpaModelForSelectionAsync(ASRRequest request)
        {
            ASRBackend normalized = ASRBackendSelectionPolicy.NormalizeRequestedBackend(request?.RequestedBackend ?? ASRBackend.Auto);
            if ((normalized == ASRBackend.Auto || normalized == ASRBackend.SherpaOnnx) &&
                ExperimentDataPathResolver.IsAndroidRuntime())
            {
                await _sherpaBackend.PrepareModelForPreflightAsync();
            }
        }

        private async Task<ASRResult> TranscribeWithActiveBackendAsync(
            AudioClip clip,
            ASRAudioAnalysis analysis,
            string diagnosticAudioPath,
            long durationMs)
        {
            if (_activeBackend == ASRBackend.SherpaOnnx)
            {
                return await _sherpaBackend.TranscribeAsync(
                    clip,
                    _activeRequest,
                    _resolvedMicrophoneDevice,
                    _activeRequest.SampleRate,
                    durationMs,
                    analysis,
                    diagnosticAudioPath);
            }

            return await _whisperClient.TranscribeAsync(
                clip,
                _activeRequest,
                _resolvedMicrophoneDevice,
                _activeRequest.SampleRate,
                durationMs);
        }

        private string ResolveUnavailableReason()
        {
            if (_lastBackendPreflight != null && !string.IsNullOrWhiteSpace(_lastBackendPreflight.Error))
            {
                return _lastBackendPreflight.Error;
            }

            return string.IsNullOrWhiteSpace(_lastBackendSelection?.Reason)
                ? "asr_backend_unavailable"
                : _lastBackendSelection.Reason;
        }

        private static bool IsC11ExperimentalCondition(string conditionId)
        {
            return !string.IsNullOrWhiteSpace(conditionId) &&
                conditionId.IndexOf("C11", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private ASRResult BuildStartedResult(ASRBackend backend)
        {
            return ASRResult.Successful(
                _activeRequest.UtteranceId,
                backend,
                string.Empty,
                _activeRequest.Language,
                0L,
                _resolvedMicrophoneDevice,
                _activeRequest.SampleRate);
        }

        private ASRResult CompleteManualStub(string transcript)
        {
            ASRResult result;
            if (string.IsNullOrWhiteSpace(transcript))
            {
                result = ASRResult.Failed(
                    _activeRequest.UtteranceId,
                    ASRBackend.ManualStub,
                    "manual_stub_empty_transcript",
                    _activeRequest.Language,
                    _stopwatch.ElapsedMilliseconds,
                    _resolvedMicrophoneDevice,
                    _activeRequest.SampleRate);
            }
            else
            {
                result = ASRResult.Successful(
                    _activeRequest.UtteranceId,
                    ASRBackend.ManualStub,
                    transcript,
                    _activeRequest.Language,
                    _stopwatch.ElapsedMilliseconds,
                    _resolvedMicrophoneDevice,
                    _activeRequest.SampleRate);
            }

            Status = result.Success ? ASRStatus.Completed : ASRStatus.Failed;
            ResetActiveCapture();
            return result;
        }

        public static MicrophoneSelection ResolveMicrophoneSelection(string preferredDevice, IReadOnlyList<string> devices)
        {
            string requested = preferredDevice ?? string.Empty;
            string[] available = devices?
                .Where(device => !string.IsNullOrWhiteSpace(device))
                .Select(device => device.Trim())
                .ToArray() ?? Array.Empty<string>();

            if (available.Length == 0)
            {
                return new MicrophoneSelection(requested, string.Empty, false, !string.IsNullOrWhiteSpace(requested), available, "no_microphones_detected");
            }

            if (!string.IsNullOrWhiteSpace(requested))
            {
                string exact = available.FirstOrDefault(device => string.Equals(device, requested, StringComparison.Ordinal));
                if (!string.IsNullOrWhiteSpace(exact))
                {
                    return new MicrophoneSelection(requested, exact, false, false, available, "preferred_available");
                }

                string contains = available.FirstOrDefault(device => device.IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrWhiteSpace(contains))
                {
                    return new MicrophoneSelection(requested, contains, false, false, available, "preferred_partial_match");
                }

                return new MicrophoneSelection(requested, available[0], true, true, available, "preferred_unavailable_fallback_first_device");
            }

            return new MicrophoneSelection(string.Empty, available[0], true, false, available, "default_first_device");
        }

        private static int GetMicrophonePosition(string microphoneStartDevice)
        {
            try
            {
                return Microphone.GetPosition(microphoneStartDevice);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private CaptureSlice BuildCaptureSlice(AudioClip source, int microphonePosition)
        {
            int frameCount = ResolveRecordedFrameCount(source, microphonePosition);
            int sampleCount = Mathf.Clamp(frameCount * source.channels, 0, source.samples * source.channels);
            float[] allSamples = new float[source.samples * source.channels];
            source.GetData(allSamples, 0);
            float[] recordedSamples = new float[sampleCount];
            Array.Copy(allSamples, recordedSamples, sampleCount);
            return new CaptureSlice(recordedSamples, frameCount, microphonePosition);
        }

        private int ResolveRecordedFrameCount(AudioClip source, int microphonePosition)
        {
            if (microphonePosition > 0)
            {
                return Mathf.Min(microphonePosition, source.samples);
            }

            float elapsedSeconds = (float)_stopwatch.Elapsed.TotalSeconds;
            int elapsedFrames = Mathf.RoundToInt(elapsedSeconds * source.frequency);
            bool maxCaptureElapsed = _activeRequest != null &&
                _activeRequest.MaxCaptureSeconds > 0f &&
                elapsedSeconds >= _activeRequest.MaxCaptureSeconds - 0.05f;

            if (maxCaptureElapsed)
            {
                return source.samples;
            }

            return Mathf.Clamp(elapsedFrames, 0, source.samples);
        }

        private void LogStartDiagnostics()
        {
            if (!_debugOptions.DebugLogging)
            {
                return;
            }

            string requested = string.IsNullOrWhiteSpace(_activeRequest.MicrophoneDevice)
                ? "<default>"
                : _activeRequest.MicrophoneDevice;
            _debugOptions.Log?.Invoke(
                $"ASR start | utterance={_activeRequest.UtteranceId} backend={_activeBackend} requested_microphone={requested} resolved_microphone={_resolvedMicrophoneDevice} fallback_used={_microphoneSelection.FallbackUsed} preferred_unavailable={_microphoneSelection.PreferredUnavailable} microphone_selection_reason={_microphoneSelection.SelectionReason} available_microphones='{string.Join("|", _microphoneSelection.AvailableDevices)}' sample_rate={_activeRequest.SampleRate} max_capture_seconds={_activeRequest.MaxCaptureSeconds:0.###} language={_activeRequest.Language} timestamp={DateTime.Now:O}");
        }

        private void LogMicrophoneSelectionDiagnostics()
        {
            LogMicrophoneSelectionEvent("voice_microphone_devices_detected", _microphoneSelection.SelectionReason);
            if (_microphoneSelection.PreferredUnavailable)
            {
                LogMicrophoneSelectionEvent("voice_microphone_preferred_unavailable", _microphoneSelection.SelectionReason);
            }

            LogMicrophoneSelectionEvent(
                _microphoneSelection.FallbackUsed
                    ? "voice_microphone_fallback_selected"
                    : "voice_microphone_preferred_selected",
                _microphoneSelection.SelectionReason);
        }

        private void LogMicrophoneSelectionEvent(string eventType, string reason)
        {
            TiagoExperimentTelemetry.LogEvent(
                eventType,
                new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["preferred_microphone"] = _microphoneSelection.PreferredDevice,
                    ["selected_microphone"] = _microphoneSelection.SelectedDevice,
                    ["fallback_used"] = _microphoneSelection.FallbackUsed,
                    ["preferred_unavailable"] = _microphoneSelection.PreferredUnavailable,
                    ["available_microphones"] = string.Join("|", _microphoneSelection.AvailableDevices),
                    ["available_microphone_count"] = _microphoneSelection.AvailableDevices.Length,
                    ["utterance_id"] = _activeRequest != null ? _activeRequest.UtteranceId : string.Empty
                });
        }

        private void LogMicrophoneAudioTooQuiet(ASRAudioAnalysis analysis, string diagnosticAudioPath)
        {
            TiagoExperimentTelemetry.LogEvent(
                "voice_microphone_audio_too_quiet",
                new Dictionary<string, object>
                {
                    ["preferred_microphone"] = _microphoneSelection.PreferredDevice,
                    ["selected_microphone"] = _microphoneSelection.SelectedDevice,
                    ["fallback_used"] = _microphoneSelection.FallbackUsed,
                    ["preferred_unavailable"] = _microphoneSelection.PreferredUnavailable,
                    ["rms"] = analysis.Rms,
                    ["peak"] = analysis.Peak,
                    ["non_silent_sample_percent"] = analysis.NonSilentSamplePercent,
                    ["recorded_samples"] = analysis.RecordedSamples,
                    ["recorded_duration_ms"] = analysis.RecordedDurationMs,
                    ["diagnostic_audio_path"] = diagnosticAudioPath ?? string.Empty,
                    ["utterance_id"] = _activeRequest != null ? _activeRequest.UtteranceId : string.Empty,
                    ["reason"] = "recording_started_but_no_signal"
                });
        }

        private void LogStopDiagnostics(
            AudioClip clip,
            int microphonePosition,
            bool wasRecording,
            CaptureSlice slice,
            ASRAudioAnalysis analysis,
            string diagnosticAudioPath)
        {
            if (!_debugOptions.DebugLogging)
            {
                return;
            }

            string positionNote = microphonePosition == 0
                ? "position_zero_handled_by_elapsed_or_full_clip_fallback"
                : "position_used";
            _debugOptions.Log?.Invoke(
                $"ASR stop | utterance={_activeRequest.UtteranceId} microphone_position={microphonePosition} was_recording={wasRecording} {positionNote} recorded_duration_ms={analysis.RecordedDurationMs} recorded_samples={analysis.RecordedSamples} recorded_frames={slice.FrameCount} channels={clip.channels} frequency={clip.frequency} clip_samples={clip.samples} clip_length_seconds={clip.length:0.###} rms={analysis.Rms:0.000000} peak={analysis.Peak:0.000000} non_silent_percent={analysis.NonSilentSamplePercent:0.##} silence_detected={analysis.SilenceDetected} wav={diagnosticAudioPath}");
        }

        private string SaveDiagnosticWavIfRequested(float[] samples, int channels, int sampleRate)
        {
            if (!_debugOptions.SaveLastCapturedClipToWav)
            {
                return string.Empty;
            }

            try
            {
                string directory = Path.Combine(
                    ExperimentDataPathResolver.ResolveDataRoot(),
                    "AsrDiagnostics",
                    "utterances");
                string path = ASRWavWriter.Write(directory, _activeRequest.UtteranceId, samples, channels, sampleRate);
                _debugOptions.Log?.Invoke($"ASR diagnostic WAV saved | path={path}");
                return path;
            }
            catch (Exception ex)
            {
                _debugOptions.Warn?.Invoke($"ASR diagnostic WAV save failed | error={ex.GetBaseException().Message}");
                return string.Empty;
            }
        }

        private static float[] ReadClipSamples(AudioClip clip)
        {
            if (clip == null)
            {
                return Array.Empty<float>();
            }

            float[] samples = new float[clip.samples * clip.channels];
            clip.GetData(samples, 0);
            return samples;
        }

        private void ResetActiveCapture()
        {
            _clip = null;
            _activeRequest = null;
            _microphoneStartDevice = null;
            _resolvedMicrophoneDevice = string.Empty;
            _microphoneSelection = default;
        }

        public static string[] GetMicrophoneDevices()
        {
            return Microphone.devices ?? Array.Empty<string>();
        }

        public static bool IsConfiguredMicrophoneAvailable(string configuredDevice)
        {
            return string.IsNullOrWhiteSpace(configuredDevice) ||
                GetMicrophoneDevices().Any(device => device == configuredDevice);
        }

        public readonly struct MicrophoneSelection
        {
            public MicrophoneSelection(
                string preferredDevice,
                string selectedDevice,
                bool fallbackUsed,
                bool preferredUnavailable,
                IReadOnlyList<string> availableDevices,
                string selectionReason)
            {
                PreferredDevice = preferredDevice ?? string.Empty;
                SelectedDevice = selectedDevice ?? string.Empty;
                FallbackUsed = fallbackUsed;
                PreferredUnavailable = preferredUnavailable;
                AvailableDevices = availableDevices?.ToArray() ?? Array.Empty<string>();
                SelectionReason = selectionReason ?? string.Empty;
            }

            public string PreferredDevice { get; }
            public string SelectedDevice { get; }
            public bool FallbackUsed { get; }
            public bool PreferredUnavailable { get; }
            public string[] AvailableDevices { get; }
            public string SelectionReason { get; }
        }

        private readonly struct CaptureSlice
        {
            public CaptureSlice(float[] samples, int frameCount, int microphonePosition)
            {
                Samples = samples ?? Array.Empty<float>();
                FrameCount = Math.Max(0, frameCount);
                MicrophonePosition = Math.Max(0, microphonePosition);
            }

            public float[] Samples { get; }
            public int FrameCount { get; }
            public int MicrophonePosition { get; }
        }
    }
}
