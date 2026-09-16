using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Autonomy.Domain;
using UnityEngine;
using UnityEngine.Networking;

namespace Autonomy.UnityIntegration
{
    public sealed class SherpaOnnxASRBackend : IUnityAudioASRBackend, IDisposable
    {
        public const string MissingArtifactsTag = "p45e01_sherpa_missing_artifacts";

        private static readonly string[] CandidateManagedBindingTypeNames =
        {
            "SherpaOnnx.OfflineRecognizer",
            "SherpaOnnx.OnlineRecognizer",
            "SherpaOnnx.SherpaOnnx",
            "SherpaOnnx.SherpaOnnxRecognizer"
        };

        private readonly SherpaOnnxBackendOptions _options;
        private readonly ASRDebugOptions _debugOptions;
        private readonly Func<string> _streamingAssetsPathProvider;
        private readonly Func<string> _dataPathProvider;
        private readonly Func<string> _persistentDataPathProvider;
        private readonly Func<string> _platformProvider;
        private readonly SherpaOnnxAndroidRecognizerBridge _androidBridge;
        private readonly SherpaOnnxAndroidDecodeWorker _androidDecodeWorker;
        private readonly object _modelResolveLock = new();
        private Task<SherpaModelResolveResult> _modelResolveTask;
        private SherpaModelResolveResult _lastModelResolve;
        private ASRBackendPreflightResult _lastPreflight;

        public SherpaOnnxASRBackend(
            SherpaOnnxBackendOptions options = null,
            ASRDebugOptions debugOptions = null,
            Func<string> streamingAssetsPathProvider = null,
            Func<string> dataPathProvider = null,
            Func<string> persistentDataPathProvider = null,
            Func<string> platformProvider = null)
        {
            _options = options ?? new SherpaOnnxBackendOptions();
            _debugOptions = debugOptions ?? new ASRDebugOptions();
            _streamingAssetsPathProvider = streamingAssetsPathProvider ?? (() => Application.streamingAssetsPath);
            _dataPathProvider = dataPathProvider ?? (() => Application.dataPath);
            _persistentDataPathProvider = persistentDataPathProvider ?? (() => Application.persistentDataPath);
            _platformProvider = platformProvider ?? (() => Application.platform.ToString());
            _androidBridge = new SherpaOnnxAndroidRecognizerBridge(_options);
            _androidDecodeWorker = new SherpaOnnxAndroidDecodeWorker(_options);
        }

        public ASRBackend Backend => ASRBackend.SherpaOnnx;
        public string ModelName => _options.ModelName;
        public string ModelRelativePath => _options.BuildStreamingAssetsRelativeModelPath();
        public ASRBackendPreflightResult LastPreflight => _lastPreflight;

        public void Dispose()
        {
            _androidDecodeWorker?.Dispose();
            _androidBridge?.Dispose();
        }

        public ASRBackendPreflightResult Preflight(ASRBackend requestedBackend, string language)
        {
            string platform = SafeInvoke(_platformProvider);
            string modelDirectory = BuildModelDirectoryForPreflight(platform);
            string streamingModelDirectory = BuildStreamingAssetsModelDirectory();
            string persistentModelDirectory = BuildPersistentModelDirectory();
            string normalizedLanguage = string.IsNullOrWhiteSpace(language) ? "es" : language.Trim();
            bool languageCompatible = IsSpanishOrMultilingualModel(_options.ModelName, modelDirectory);
            LogP45E05PreflightModelPathResolved(requestedBackend, platform, streamingModelDirectory, persistentModelDirectory, modelDirectory);

            if (!Directory.Exists(modelDirectory))
            {
                bool androidPlatform = IsAndroidPlatform(platform);
                string reason = androidPlatform && !string.IsNullOrWhiteSpace(_lastModelResolve.ErrorReason)
                    ? "model_resolve_failed"
                    : androidPlatform
                        ? "model_resolve_pending"
                        : "model_directory_missing";
                string error = androidPlatform
                    ? $"{reason}: streaming_model_path='{streamingModelDirectory}' persistent_model_path='{persistentModelDirectory}' error='{_lastModelResolve.ErrorReason}'"
                    : $"{MissingArtifactsTag}: model_directory_missing";
                return StoreUnavailable(
                    requestedBackend,
                    modelDirectory,
                    platform,
                    normalizedLanguage,
                    languageCompatible,
                    reason,
                    error,
                    string.Empty,
                    string.Empty,
                    RuntimeProbeResult.NotEvaluated(_options.AndroidAbi));
            }

            if (!TryResolveModelFileSet(
                    modelDirectory,
                    _options,
                    out string[] resolvedFiles,
                    out string selectedLayout,
                    out string filesReport,
                    out string missingFileReport))
            {
                return StoreUnavailable(
                    requestedBackend,
                    modelDirectory,
                    platform,
                    normalizedLanguage,
                    languageCompatible,
                    "model_files_missing",
                    $"{MissingArtifactsTag}: model_files_missing required_any_of={missingFileReport} found_files={filesReport}",
                    string.Empty,
                    filesReport,
                    RuntimeProbeResult.NotEvaluated(_options.AndroidAbi));
            }

            if (_options.RequireSpanishOrMultilingualModel && !languageCompatible)
            {
                return StoreUnavailable(
                    requestedBackend,
                    modelDirectory,
                    platform,
                    normalizedLanguage,
                    false,
                    "model_not_marked_spanish_or_multilingual",
                    $"{MissingArtifactsTag}: model_not_marked_spanish_or_multilingual selected_layout={selectedLayout} found_files={filesReport}",
                    selectedLayout,
                    filesReport,
                    RuntimeProbeResult.NotEvaluated(_options.AndroidAbi));
            }

            RuntimeProbeResult runtime = ProbeRuntime(platform);
            LogP45E05RuntimeProbe(requestedBackend, platform, modelDirectory, selectedLayout, filesReport, runtime);
            if (!runtime.ModelCanReachRuntime)
            {
                return StoreUnavailable(
                    requestedBackend,
                    modelDirectory,
                    platform,
                    normalizedLanguage,
                    languageCompatible,
                    runtime.Reason,
                    $"{runtime.Reason}: {runtime.Error} selected_layout={selectedLayout} found_files={filesReport} runtime_files={runtime.RuntimeFilesReport}",
                    selectedLayout,
                    filesReport,
                    runtime);
            }

            if (IsAndroidPlatform(platform))
            {
                return StoreAvailable(
                    requestedBackend,
                    modelDirectory,
                    platform,
                    normalizedLanguage,
                    languageCompatible,
                    runtime.Reason,
                    selectedLayout,
                    filesReport,
                    runtime);
            }

            return StoreUnavailable(
                requestedBackend,
                modelDirectory,
                platform,
                normalizedLanguage,
                languageCompatible,
                "recognizer_wrapper_not_implemented",
                $"recognizer_wrapper_not_implemented: sherpa runtime artifacts detected ({runtime.Reason}) but Android recognizer execution is only enabled in Android player selected_layout={selectedLayout} found_files={filesReport} runtime_files={runtime.RuntimeFilesReport}",
                selectedLayout,
                filesReport,
                runtime);
        }

        public async Task<string> PrepareModelForPreflightAsync()
        {
            SherpaModelResolveResult result = await EnsureModelResolvedForPreflightAsync();
            return result.Success ? result.ModelDirectory : result.ErrorReason;
        }

        public async Task<SherpaVoicePipelinePreparationResult> PrepareVoicePipelineAsync(
            Action<string, long, string> phaseObserver = null)
        {
            Stopwatch totalStopwatch = Stopwatch.StartNew();
            Stopwatch modelStopwatch = Stopwatch.StartNew();
            phaseObserver?.Invoke("model_load_started", 0L, string.Empty);
            SherpaModelResolveResult modelResolve = await EnsureModelResolvedForPreflightAsync();
            modelStopwatch.Stop();
            phaseObserver?.Invoke(
                modelResolve.Success ? "model_load_completed" : "model_load_failed",
                modelStopwatch.ElapsedMilliseconds,
                modelResolve.Success ? modelResolve.ModelDirectory : modelResolve.ErrorReason);
            if (!modelResolve.Success)
            {
                totalStopwatch.Stop();
                return SherpaVoicePipelinePreparationResult.Failed(
                    modelResolve.ErrorReason,
                    modelStopwatch.ElapsedMilliseconds,
                    0L,
                    totalStopwatch.ElapsedMilliseconds,
                    string.Empty);
            }

            string platform = SafeInvoke(_platformProvider);
            if (!IsAndroidPlatform(platform))
            {
                totalStopwatch.Stop();
                return SherpaVoicePipelinePreparationResult.Successful(
                    modelStopwatch.ElapsedMilliseconds,
                    0L,
                    totalStopwatch.ElapsedMilliseconds,
                    modelResolve.ModelDirectory,
                    recognizerReused: false);
            }

            ASRBackendPreflightResult preflight = Preflight(ASRBackend.SherpaOnnx, "es");
            if (preflight == null || !preflight.IsAvailable)
            {
                totalStopwatch.Stop();
                return SherpaVoicePipelinePreparationResult.Failed(
                    preflight?.Error ?? "sherpa_preflight_unavailable",
                    modelStopwatch.ElapsedMilliseconds,
                    0L,
                    totalStopwatch.ElapsedMilliseconds,
                    modelResolve.ModelDirectory);
            }

            Stopwatch recognizerStopwatch = Stopwatch.StartNew();
            phaseObserver?.Invoke("recognizer_create_started", 0L, modelResolve.ModelDirectory);
            bool recognizerReadyBefore = _options.UseAndroidDedicatedDecodeThread
                ? _androidDecodeWorker.IsRecognizerReadyForModel(modelResolve.ModelDirectory)
                : _androidBridge.IsRecognizerReadyForModel(modelResolve.ModelDirectory);
            SherpaOnnxAndroidTranscriptionResult recognizerResult = _options.UseAndroidDedicatedDecodeThread
                ? await _androidDecodeWorker.PrepareRecognizerAsync(
                    modelResolve.ModelDirectory,
                    Math.Max(45000, _options.AndroidDecodeTimeoutMs))
                : _androidBridge.PrepareRecognizer(modelResolve.ModelDirectory);
            recognizerStopwatch.Stop();
            phaseObserver?.Invoke(
                recognizerResult.Success ? "recognizer_create_completed" : "recognizer_create_failed",
                recognizerResult.RecognizerCreateMs > 0L
                    ? recognizerResult.RecognizerCreateMs
                    : recognizerStopwatch.ElapsedMilliseconds,
                recognizerResult.Success ? modelResolve.ModelDirectory : recognizerResult.ErrorReason);
            if (_options.UseAndroidDedicatedDecodeThread)
            {
                LogDedicatedWorkerTraceEvents(new Dictionary<string, object>
                {
                    ["persistent_model_path"] = modelResolve.ModelDirectory,
                    ["model_name"] = _options.ModelName,
                    ["prewarm"] = true
                });
            }

            totalStopwatch.Stop();
            if (!recognizerResult.Success)
            {
                return SherpaVoicePipelinePreparationResult.Failed(
                    recognizerResult.ErrorReason,
                    modelStopwatch.ElapsedMilliseconds,
                    recognizerStopwatch.ElapsedMilliseconds,
                    totalStopwatch.ElapsedMilliseconds,
                    modelResolve.ModelDirectory);
            }

            return SherpaVoicePipelinePreparationResult.Successful(
                modelStopwatch.ElapsedMilliseconds,
                recognizerResult.RecognizerCreateMs > 0L
                    ? recognizerResult.RecognizerCreateMs
                    : recognizerStopwatch.ElapsedMilliseconds,
                totalStopwatch.ElapsedMilliseconds,
                modelResolve.ModelDirectory,
                recognizerReadyBefore || recognizerResult.RecognizerReused);
        }

        private void LogP45E05RuntimeProbe(
            ASRBackend requestedBackend,
            string platform,
            string modelDirectory,
            string modelLayout,
            string modelFilesReport,
            RuntimeProbeResult runtime)
        {
            Dictionary<string, object> payload = new()
            {
                ["requested_backend"] = requestedBackend.ToString(),
                ["effective_backend"] = ASRBackend.SherpaOnnx.ToString(),
                ["model_name"] = _options.ModelName,
                ["model_layout"] = modelLayout ?? string.Empty,
                ["model_path"] = modelDirectory ?? string.Empty,
                ["model_files"] = modelFilesReport ?? string.Empty,
                ["persistent_model_path"] = string.Empty,
                ["runtime_source"] = ResolveRuntimeSourceKind(runtime),
                ["runtime_source_detail"] = runtime.Reason ?? string.Empty,
                ["runtime_path"] = runtime.RuntimePath ?? string.Empty,
                ["runtime_files"] = runtime.RuntimeFilesReport ?? string.Empty,
                ["runtime_files_ok_embedded_in_aar"] = RuntimeReportHasEmbeddedRuntime(runtime),
                ["explicit_native_libs_present"] = RuntimeReportHasExplicitRuntime(runtime),
                ["duplicate_native_libs"] = RuntimeReportHasDuplicateNativeLibraries(runtime),
                ["android_bridge_detected"] = !string.IsNullOrWhiteSpace(runtime.BindingTypeName),
                ["bridge_class"] = runtime.BindingTypeName ?? _options.AndroidRecognizerClassName,
                ["architecture_abi"] = runtime.ArchitectureAbi ?? _options.AndroidAbi,
                ["platform"] = platform ?? string.Empty,
                ["sample_rate"] = 16000,
                ["audio_duration_ms"] = 0L,
                ["main_thread_audio_extract_ms"] = 0L,
                ["main_thread_resample_ms"] = 0L,
                ["model_copy_ms"] = 0L,
                ["recognizer_create_ms"] = 0L,
                ["queue_wait_ms"] = 0L,
                ["create_stream_ms"] = 0L,
                ["accept_waveform_ms"] = 0L,
                ["decode_ms"] = 0L,
                ["get_result_ms"] = 0L,
                ["inference_latency_ms"] = 0L,
                ["worker_thread_used"] = false,
                ["dedicated_worker_enabled"] = false,
                ["legacy_worker_requested"] = false,
                ["worker_decode_experimental_enabled"] = false,
                ["decode_thread_mode"] = string.Empty,
                ["decode_route_fallback_enabled"] = false,
                ["decode_route_fallback_used"] = false,
                ["hotwords_enabled"] = _options.EnableSherpaHotwords,
                ["total_latency_ms"] = 0L,
                ["transcript"] = string.Empty,
                ["error_reason"] = runtime.Error ?? string.Empty,
                ["fallback_used"] = false
            };
            TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_duplicate_native_libs_check", payload);
            TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_runtime_source_selected", payload);
        }

        public Task<ASRResult> TranscribeAsync(
            AudioClip clip,
            ASRRequest request,
            string microphoneDevice,
            int sampleRate,
            long durationMs,
            ASRAudioAnalysis analysis,
            string diagnosticAudioPath)
        {
            return TranscribeAsyncInternal(clip, request, microphoneDevice, sampleRate, durationMs, analysis, diagnosticAudioPath);
        }

        private async Task<ASRResult> TranscribeAsyncInternal(
            AudioClip clip,
            ASRRequest request,
            string microphoneDevice,
            int sampleRate,
            long durationMs,
            ASRAudioAnalysis analysis,
            string diagnosticAudioPath)
        {
            ASRRequest safeRequest = request ?? new ASRRequest(ASRBackend.SherpaOnnx, "es", microphoneDevice, sampleRate, 0f);
            string platform = SafeInvoke(_platformProvider);
            SherpaModelResolveResult modelResolve = default;
            if (IsAndroidPlatform(platform))
            {
                modelResolve = await EnsureModelResolvedForPreflightAsync();
                if (modelResolve.Success &&
                    (_lastPreflight == null || !string.Equals(_lastPreflight.ModelPath, modelResolve.ModelDirectory, StringComparison.Ordinal)))
                {
                    _lastPreflight = null;
                }
            }

            ASRBackendPreflightResult preflight = _lastPreflight ?? Preflight(safeRequest.RequestedBackend, safeRequest.Language);
            string preflightError = preflight != null && !string.IsNullOrWhiteSpace(preflight.Error)
                ? preflight.Error
                : "recognizer_unavailable: sherpa runtime preflight did not provide an executable recognizer";
            Dictionary<string, object> payload = BuildP45E03RuntimePayload(safeRequest, preflight, durationMs, preflightError, string.Empty);
            TiagoExperimentTelemetry.LogEvent("p45e03_sherpa_transcription_started", payload);
            TiagoExperimentTelemetry.LogEvent("p45e04_sherpa_transcription_started", payload);
            TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_transcription_started", payload);

            if (preflight == null || !preflight.IsAvailable || !IsAndroidPlatform(platform))
            {
                if (preflight != null && preflight.Reason.IndexOf("recognizer", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    TiagoExperimentTelemetry.LogEvent("p45e04_sherpa_recognizer_create_started", payload);
                    TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_recognizer_create_started", payload);
                    TiagoExperimentTelemetry.LogEvent("p45e03_sherpa_recognizer_create_failed", payload);
                    TiagoExperimentTelemetry.LogEvent("p45e04_sherpa_recognizer_create_failed", payload);
                    TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_recognizer_create_failed", payload);
                }

                _debugOptions.Warn?.Invoke(
                    $"Sherpa ONNX backend unavailable | utterance={safeRequest.UtteranceId} model='{_options.ModelName}' error='{preflightError}'");

                ASRResult failedUnavailable = ASRResult.Failed(
                    safeRequest.UtteranceId,
                    ASRBackend.SherpaOnnx,
                    preflightError,
                    safeRequest.Language,
                    durationMs,
                    microphoneDevice,
                    sampleRate,
                    analysis,
                    diagnosticAudioPath);
                TiagoExperimentTelemetry.LogEvent("p45e03_sherpa_transcription_failed", BuildP45E03RuntimePayload(safeRequest, preflight, durationMs, preflightError, failedUnavailable.Transcript));
                TiagoExperimentTelemetry.LogEvent("p45e04_sherpa_transcription_failed", BuildP45E03RuntimePayload(safeRequest, preflight, durationMs, preflightError, failedUnavailable.Transcript));
                TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_transcription_failed", BuildP45E03RuntimePayload(safeRequest, preflight, durationMs, preflightError, failedUnavailable.Transcript));
                return failedUnavailable;
            }

            if (!modelResolve.Success)
            {
                modelResolve = await EnsureModelResolvedForPreflightAsync();
            }

            Dictionary<string, object> resolvePayload = BuildP45E03RuntimePayload(safeRequest, preflight, durationMs, modelResolve.ErrorReason, string.Empty);
            resolvePayload["persistent_model_path"] = modelResolve.ModelDirectory;
            resolvePayload["model_copy_ms"] = modelResolve.CopyMs;
            TiagoExperimentTelemetry.LogEvent(
                modelResolve.Success
                    ? "p45e05_sherpa_model_resolve_completed"
                    : "p45e05_sherpa_model_resolve_failed",
                resolvePayload);

            if (!modelResolve.Success)
            {
                return FailSherpaTranscription(
                    safeRequest,
                    preflight,
                    durationMs,
                    microphoneDevice,
                    sampleRate,
                    analysis,
                    diagnosticAudioPath,
                    modelResolve.ErrorReason,
                    modelResolve.CopyMs,
                    0L,
                    0L);
            }

            if (!TryBuildMono16KhzWaveform(
                    clip,
                    out float[] waveform,
                    out int waveformSampleRate,
                    out string waveformError,
                    out long mainThreadAudioExtractMs,
                    out long mainThreadResampleMs))
            {
                return FailSherpaTranscription(
                    safeRequest,
                    preflight,
                    durationMs,
                    microphoneDevice,
                    sampleRate,
                    analysis,
                    diagnosticAudioPath,
                    waveformError,
                    modelResolve.CopyMs,
                    0L,
                    0L);
            }

            Dictionary<string, object> createPayload = BuildP45E03RuntimePayload(safeRequest, preflight, durationMs, string.Empty, string.Empty);
            createPayload["persistent_model_path"] = modelResolve.ModelDirectory;
            createPayload["model_copy_ms"] = modelResolve.CopyMs;
            createPayload["main_thread_audio_extract_ms"] = mainThreadAudioExtractMs;
            createPayload["main_thread_resample_ms"] = mainThreadResampleMs;
            bool legacyWorkerRequested = _options.UseAndroidWorkerThreadForDecode;
            bool useDedicatedWorker = IsAndroidPlatform(platform) && _options.UseAndroidDedicatedDecodeThread;
            string decodeThreadMode = useDedicatedWorker ? "android_dedicated_worker" : "main_thread_stable";
            createPayload["decode_thread_mode"] = decodeThreadMode;
            createPayload["worker_thread_used"] = useDedicatedWorker;
            createPayload["dedicated_worker_enabled"] = useDedicatedWorker;
            createPayload["legacy_worker_requested"] = legacyWorkerRequested;
            createPayload["worker_decode_experimental_enabled"] = false;
            createPayload["decode_route_fallback_enabled"] = _options.FallbackToMainThreadStableOnWorkerFailure;
            createPayload["decode_route_fallback_used"] = false;
            createPayload["hotwords_enabled"] = _options.EnableSherpaHotwords;
            bool recognizerReadyBeforeTranscription = useDedicatedWorker
                ? _androidDecodeWorker.IsRecognizerReadyForModel(modelResolve.ModelDirectory)
                : _androidBridge.IsRecognizerReadyForModel(modelResolve.ModelDirectory);
            if (recognizerReadyBeforeTranscription)
            {
                TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_recognizer_reused", createPayload);
            }
            else
            {
                TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_recognizer_create_started", createPayload);
            }

            TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_decode_thread_mode", createPayload);
            if (legacyWorkerRequested)
            {
                TiagoExperimentTelemetry.LogEvent("p45e05_worker_decode_disabled_for_android_stability", createPayload);
            }

            if (useDedicatedWorker)
            {
                TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_dedicated_worker_enqueue", createPayload);
            }
            else
            {
                TiagoExperimentTelemetry.LogEvent("p45e05_worker_decode_disabled_for_android_stability", createPayload);
                TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_decode_worker_disabled", createPayload);
                TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_decode_on_main_thread", createPayload);
            }

            Stopwatch decodeStopwatch = Stopwatch.StartNew();
            long workerTotalMs = 0L;
            long mainThreadBlockMs = 0L;
            bool decodeRouteFallbackUsed = false;
            SherpaOnnxAndroidTranscriptionResult transcription;
            if (useDedicatedWorker)
            {
                Task<SherpaOnnxAndroidTranscriptionResult> transcriptionTask = _androidDecodeWorker.TranscribeAsync(
                    modelResolve.ModelDirectory,
                    waveform,
                    waveformSampleRate,
                    durationMs,
                    _options.AndroidDecodeTimeoutMs);

                while (!transcriptionTask.IsCompleted)
                {
                    LogDedicatedWorkerTraceEvents(createPayload);
                    await Task.Delay(50);
                }

                transcription = await transcriptionTask;
                LogDedicatedWorkerTraceEvents(createPayload);
                workerTotalMs = transcription.TotalLatencyMs;

                if (!transcription.Success && _options.FallbackToMainThreadStableOnWorkerFailure)
                {
                    Dictionary<string, object> fallbackPayload = new(createPayload)
                    {
                        ["decode_thread_mode"] = "main_thread_stable_after_worker_failure",
                        ["decode_route_fallback_used"] = true,
                        ["worker_error_reason"] = transcription.ErrorReason
                    };
                    TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_dedicated_worker_decode_failed", fallbackPayload);
                    TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_decode_on_main_thread", fallbackPayload);
                    decodeRouteFallbackUsed = true;
                    decodeThreadMode = "main_thread_stable_after_worker_failure";
                    Stopwatch fallbackStopwatch = Stopwatch.StartNew();
                    transcription = _androidBridge.Transcribe(
                        modelResolve.ModelDirectory,
                        waveform,
                        waveformSampleRate,
                        durationMs,
                        attachCurrentThread: false,
                        emitStepLogs: true,
                        decodeThreadMode: decodeThreadMode);
                    fallbackStopwatch.Stop();
                    mainThreadBlockMs = fallbackStopwatch.ElapsedMilliseconds;
                }
            }
            else
            {
                transcription = _androidBridge.Transcribe(
                    modelResolve.ModelDirectory,
                    waveform,
                    waveformSampleRate,
                    durationMs,
                    attachCurrentThread: false,
                    emitStepLogs: true,
                    decodeThreadMode: decodeThreadMode);
                mainThreadBlockMs = decodeStopwatch.ElapsedMilliseconds;
            }

            decodeStopwatch.Stop();

            Dictionary<string, object> resultPayload = BuildP45E03RuntimePayload(
                safeRequest,
                preflight,
                durationMs,
                transcription.ErrorReason,
                transcription.Transcript);
            resultPayload["persistent_model_path"] = modelResolve.ModelDirectory;
            resultPayload["model_copy_ms"] = modelResolve.CopyMs;
            resultPayload["main_thread_audio_extract_ms"] = mainThreadAudioExtractMs;
            resultPayload["main_thread_resample_ms"] = mainThreadResampleMs;
            resultPayload["recognizer_create_ms"] = transcription.RecognizerCreateMs;
            resultPayload["recognizer_created_this_call"] = transcription.RecognizerCreated;
            resultPayload["recognizer_reused"] = transcription.RecognizerReused || recognizerReadyBeforeTranscription;
            resultPayload["inference_latency_ms"] = transcription.InferenceLatencyMs;
            resultPayload["queue_wait_ms"] = transcription.QueueWaitMs;
            resultPayload["create_stream_ms"] = transcription.CreateStreamMs;
            resultPayload["accept_waveform_ms"] = transcription.AcceptWaveformMs;
            resultPayload["decode_ms"] = transcription.DecodeMs;
            resultPayload["get_result_ms"] = transcription.GetResultMs;
            resultPayload["worker_decode_ms"] = useDedicatedWorker && !decodeRouteFallbackUsed ? transcription.InferenceLatencyMs : 0L;
            resultPayload["worker_total_ms"] = workerTotalMs;
            resultPayload["worker_thread_used"] = useDedicatedWorker && !decodeRouteFallbackUsed;
            resultPayload["dedicated_worker_enabled"] = useDedicatedWorker;
            resultPayload["legacy_worker_requested"] = legacyWorkerRequested;
            resultPayload["worker_decode_experimental_enabled"] = false;
            resultPayload["decode_thread_mode"] = decodeThreadMode;
            resultPayload["decode_route_fallback_enabled"] = _options.FallbackToMainThreadStableOnWorkerFailure;
            resultPayload["decode_route_fallback_used"] = decodeRouteFallbackUsed;
            resultPayload["hotwords_enabled"] = _options.EnableSherpaHotwords;
            resultPayload["jni_thread_attached"] = transcription.JniThreadAttached;
            resultPayload["main_thread_block_ms"] = mainThreadBlockMs;
            resultPayload["total_asr_latency_ms"] = transcription.TotalLatencyMs;
            resultPayload["total_latency_ms"] = transcription.TotalLatencyMs;

            if (!transcription.Success)
            {
                if (IsRecognizerCreateFailure(transcription.ErrorReason))
                {
                    TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_recognizer_create_failed", resultPayload);
                }

                if (useDedicatedWorker && !decodeRouteFallbackUsed)
                {
                    TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_dedicated_worker_decode_failed", resultPayload);
                }

                TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_transcription_failed", resultPayload);
                return ASRResult.Failed(
                    safeRequest.UtteranceId,
                    ASRBackend.SherpaOnnx,
                    transcription.ErrorReason,
                    safeRequest.Language,
                    durationMs,
                    microphoneDevice,
                    sampleRate,
                    analysis,
                    diagnosticAudioPath);
            }

            TiagoExperimentTelemetry.LogEvent(
                transcription.RecognizerCreated ? "p45e05_sherpa_recognizer_created" : "p45e05_sherpa_recognizer_reused",
                resultPayload);
            TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_transcription_completed", resultPayload);
            return ASRResult.Successful(
                safeRequest.UtteranceId,
                ASRBackend.SherpaOnnx,
                transcription.Transcript,
                safeRequest.Language,
                durationMs,
                microphoneDevice,
                sampleRate,
                float.NaN,
                analysis,
                diagnosticAudioPath);
        }

        private void LogDedicatedWorkerTraceEvents(Dictionary<string, object> basePayload)
        {
            IReadOnlyList<SherpaOnnxAndroidTraceEvent> traceEvents = _androidDecodeWorker.DrainTraceEvents();
            if (traceEvents.Count == 0)
            {
                return;
            }

            foreach (SherpaOnnxAndroidTraceEvent traceEvent in traceEvents)
            {
                if (string.IsNullOrWhiteSpace(traceEvent.EventName))
                {
                    continue;
                }

                Dictionary<string, object> payload = basePayload != null
                    ? new Dictionary<string, object>(basePayload)
                    : new Dictionary<string, object>();
                if (traceEvent.Payload != null)
                {
                    foreach (KeyValuePair<string, object> pair in traceEvent.Payload)
                    {
                        payload[pair.Key] = pair.Value;
                    }
                }

                payload["fallback_used"] = false;
                TiagoExperimentTelemetry.LogEvent(traceEvent.EventName, payload);
            }
        }

        private static bool IsRecognizerCreateFailure(string errorReason)
        {
            if (string.IsNullOrWhiteSpace(errorReason))
            {
                return false;
            }

            return errorReason.IndexOf("recognizer_create_failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                errorReason.IndexOf("native_load_failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                errorReason.IndexOf("api_signature_mismatch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                errorReason.IndexOf("model_path_invalid", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ResolveRuntimeSourceKind(ASRBackendPreflightResult preflight)
        {
            if (preflight == null)
            {
                return string.Empty;
            }

            string reason = preflight.Reason ?? string.Empty;
            string report = preflight.RuntimeFilesReport ?? string.Empty;
            if (reason.IndexOf("embedded_in_aar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                report.IndexOf("aar_embedded_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "embedded_in_aar";
            }

            if (reason.IndexOf("runtime_files_ok_explicit", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "explicit_native_libs";
            }

            return string.IsNullOrWhiteSpace(reason) ? string.Empty : reason;
        }

        private static string ResolveRuntimeSourceKind(RuntimeProbeResult runtime)
        {
            string reason = runtime.Reason ?? string.Empty;
            string report = runtime.RuntimeFilesReport ?? string.Empty;
            if (reason.IndexOf("embedded_in_aar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                report.IndexOf("aar_embedded_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "embedded_in_aar";
            }

            if (reason.IndexOf("runtime_files_ok_explicit", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "explicit_native_libs";
            }

            return string.IsNullOrWhiteSpace(reason) ? string.Empty : reason;
        }

        private static bool RuntimeReportHasEmbeddedRuntime(ASRBackendPreflightResult preflight)
        {
            return preflight != null && RuntimeReportHasEmbeddedRuntime(preflight.RuntimeFilesReport);
        }

        private static bool RuntimeReportHasEmbeddedRuntime(RuntimeProbeResult runtime)
        {
            return RuntimeReportHasEmbeddedRuntime(runtime.RuntimeFilesReport);
        }

        private static bool RuntimeReportHasEmbeddedRuntime(string report)
        {
            return !string.IsNullOrWhiteSpace(report) &&
                report.IndexOf("aar_embedded_libsherpa-onnx-jni.so", StringComparison.OrdinalIgnoreCase) >= 0 &&
                report.IndexOf("aar_embedded_libonnxruntime.so", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool RuntimeReportHasExplicitRuntime(ASRBackendPreflightResult preflight)
        {
            return preflight != null && RuntimeReportHasExplicitRuntime(preflight.RuntimeFilesReport);
        }

        private static bool RuntimeReportHasExplicitRuntime(RuntimeProbeResult runtime)
        {
            return RuntimeReportHasExplicitRuntime(runtime.RuntimeFilesReport);
        }

        private static bool RuntimeReportHasExplicitRuntime(string report)
        {
            if (string.IsNullOrWhiteSpace(report))
            {
                return false;
            }

            return RuntimeReportHasPresentEntry(report, "libsherpa-onnx-jni.so") &&
                RuntimeReportHasPresentEntry(report, "libonnxruntime.so");
        }

        private static bool RuntimeReportHasPresentEntry(string report, string fileName)
        {
            foreach (string entry in (report ?? string.Empty).Split(','))
            {
                if (entry.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) < 0 ||
                    entry.IndexOf("aar_embedded_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    entry.IndexOf(":missing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    entry.IndexOf(":missing_path", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private static bool RuntimeReportHasDuplicateNativeLibraries(ASRBackendPreflightResult preflight)
        {
            return preflight != null && RuntimeReportHasDuplicateNativeLibraries(preflight.Reason, preflight.Error);
        }

        private static bool RuntimeReportHasDuplicateNativeLibraries(RuntimeProbeResult runtime)
        {
            return RuntimeReportHasDuplicateNativeLibraries(runtime.Reason, runtime.Error);
        }

        private static bool RuntimeReportHasDuplicateNativeLibraries(string reason, string error)
        {
            string combined = $"{reason} {error}";
            return combined.IndexOf("duplicate_native_libs", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("runtime_files_duplicate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("android_packaging_duplicate_native_libs", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private ASRBackendPreflightResult StoreUnavailable(
            ASRBackend requestedBackend,
            string modelDirectory,
            string platform,
            string language,
            bool languageCompatible,
            string reason,
            string error,
            string modelLayout,
            string modelFilesReport,
            RuntimeProbeResult runtime)
        {
            _lastPreflight = ASRBackendPreflightResult.Unavailable(
                requestedBackend,
                ASRBackend.SherpaOnnx,
                _options.ModelName,
                modelDirectory,
                platform,
                language,
                reason,
                error,
                ResolveMainFileSize(modelDirectory),
                androidCompatible: true,
                languageCompatible: languageCompatible,
                modelLayout: modelLayout,
                modelFilesReport: modelFilesReport,
                runtimePath: runtime.RuntimePath,
                runtimeFilesReport: runtime.RuntimeFilesReport,
                runtimeBindingType: runtime.BindingTypeName,
                architectureAbi: runtime.ArchitectureAbi);
            _debugOptions.Warn?.Invoke(
                $"Sherpa ONNX preflight unavailable | reason={reason} model='{_options.ModelName}' path='{modelDirectory}' layout='{modelLayout}' files={modelFilesReport} runtime_path='{runtime.RuntimePath}' runtime_files={runtime.RuntimeFilesReport} binding='{runtime.BindingTypeName}' abi='{runtime.ArchitectureAbi}' error='{error}'");
            return _lastPreflight;
        }

        private ASRBackendPreflightResult StoreAvailable(
            ASRBackend requestedBackend,
            string modelDirectory,
            string platform,
            string language,
            bool languageCompatible,
            string reason,
            string modelLayout,
            string modelFilesReport,
            RuntimeProbeResult runtime)
        {
            _lastPreflight = ASRBackendPreflightResult.Available(
                requestedBackend,
                ASRBackend.SherpaOnnx,
                _options.ModelName,
                modelDirectory,
                ResolveMainFileSize(modelDirectory),
                platform,
                true,
                language,
                languageCompatible,
                reason: string.IsNullOrWhiteSpace(reason) ? "available" : reason,
                modelLayout: modelLayout,
                modelFilesReport: modelFilesReport,
                runtimePath: runtime.RuntimePath,
                runtimeFilesReport: runtime.RuntimeFilesReport,
                runtimeBindingType: runtime.BindingTypeName,
                architectureAbi: runtime.ArchitectureAbi);
            _debugOptions.Log?.Invoke(
                $"Sherpa ONNX preflight available | reason={reason} model='{_options.ModelName}' path='{modelDirectory}' layout='{modelLayout}' files={modelFilesReport} runtime_path='{runtime.RuntimePath}' runtime_files={runtime.RuntimeFilesReport} binding='{runtime.BindingTypeName}' abi='{runtime.ArchitectureAbi}'");
            return _lastPreflight;
        }

        private string BuildModelDirectory()
        {
            return BuildModelDirectoryForPreflight(SafeInvoke(_platformProvider));
        }

        private string BuildModelDirectoryForPreflight(string platform)
        {
            if (IsAndroidPlatform(platform))
            {
                if (_lastModelResolve.Success && !string.IsNullOrWhiteSpace(_lastModelResolve.ModelDirectory))
                {
                    return _lastModelResolve.ModelDirectory;
                }

                return BuildPersistentModelDirectory();
            }

            return BuildStreamingAssetsModelDirectory();
        }

        private string BuildStreamingAssetsModelDirectory()
        {
            return Path.Combine(
                _streamingAssetsPathProvider(),
                _options.StreamingAssetsRelativeRoot,
                _options.ModelName);
        }

        private string BuildPersistentModelDirectory()
        {
            return Path.Combine(
                SafeInvoke(_persistentDataPathProvider),
                _options.PersistentDataRelativeRoot,
                _options.ModelName);
        }

        private async Task<SherpaModelResolveResult> EnsureModelResolvedForPreflightAsync()
        {
            string platform = SafeInvoke(_platformProvider);
            if (!IsAndroidPlatform(platform))
            {
                string modelDirectory = BuildStreamingAssetsModelDirectory();
                return Directory.Exists(modelDirectory)
                    ? SherpaModelResolveResult.Successful(modelDirectory, 0L, "editor_streaming_assets")
                    : SherpaModelResolveResult.Failed("model_path_invalid: streaming assets model directory missing", 0L);
            }

            Task<SherpaModelResolveResult> task;
            lock (_modelResolveLock)
            {
                bool startNewTask = _modelResolveTask == null ||
                    (_modelResolveTask.IsCompleted && (!_lastModelResolve.Success || !Directory.Exists(_lastModelResolve.ModelDirectory)));

                if (startNewTask)
                {
                    _modelResolveTask = ResolveExecutableModelDirectoryAsync();
                }
                else if (!_modelResolveTask.IsCompleted)
                {
                    LogP45E05ModelResolveEvent(
                        "p45e05_sherpa_model_resolve_pending",
                        BuildStreamingAssetsModelDirectory(),
                        BuildPersistentModelDirectory(),
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        0L,
                        0L,
                        0L,
                        "model_resolve_pending");
                }

                task = _modelResolveTask;
            }

            try
            {
                SherpaModelResolveResult result = await task;
                _lastModelResolve = result;
                return result;
            }
            catch (Exception ex)
            {
                SherpaModelResolveResult failed = SherpaModelResolveResult.Failed(
                    "model_resolve_failed: " + ex.GetBaseException().Message,
                    0L);
                _lastModelResolve = failed;
                return failed;
            }
        }

        private async Task<SherpaModelResolveResult> ResolveExecutableModelDirectoryAsync()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            string streamingModelDirectory = BuildStreamingAssetsModelDirectory();
            if (!IsAndroidPlatform(SafeInvoke(_platformProvider)))
            {
                stopwatch.Stop();
                return Directory.Exists(streamingModelDirectory)
                    ? SherpaModelResolveResult.Successful(streamingModelDirectory, stopwatch.ElapsedMilliseconds, "editor_streaming_assets")
                    : SherpaModelResolveResult.Failed("model_path_invalid: streaming assets model directory missing", stopwatch.ElapsedMilliseconds);
            }

            string persistentModelDirectory = BuildPersistentModelDirectory();
            string[] requiredFiles =
            {
                _options.EncoderFileName,
                _options.DecoderFileName,
                _options.JoinerFileName,
                _options.TokensFileName
            };

            try
            {
                LogP45E05ModelResolveEvent(
                    "p45e05_sherpa_model_resolve_started",
                    streamingModelDirectory,
                    persistentModelDirectory,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0L,
                    0L,
                    0L,
                    string.Empty);
                Directory.CreateDirectory(persistentModelDirectory);
                bool allPresent = requiredFiles.All(file =>
                {
                    string destination = Path.Combine(persistentModelDirectory, file);
                    return File.Exists(destination) && new FileInfo(destination).Length > 0L;
                });
                if (allPresent)
                {
                    stopwatch.Stop();
                    SherpaModelResolveResult reused = SherpaModelResolveResult.Successful(
                        persistentModelDirectory,
                        stopwatch.ElapsedMilliseconds,
                        "persistent_model_reused");
                    foreach (string file in requiredFiles)
                    {
                        string destination = Path.Combine(persistentModelDirectory, file);
                        LogP45E05ModelResolveEvent(
                            "p45e05_sherpa_model_file_reused",
                            streamingModelDirectory,
                            persistentModelDirectory,
                            BuildStreamingAssetsUri(_options.StreamingAssetsRelativeRoot, _options.ModelName, file),
                            destination,
                            file,
                            new FileInfo(destination).Length,
                            0L,
                            stopwatch.ElapsedMilliseconds,
                            string.Empty);
                    }

                    LogP45E05ModelResolveEvent(
                        "p45e05_sherpa_model_resolve_completed",
                        streamingModelDirectory,
                        persistentModelDirectory,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        0L,
                        0L,
                        stopwatch.ElapsedMilliseconds,
                        reused.Reason);
                    return reused;
                }

                foreach (string file in requiredFiles)
                {
                    string destination = Path.Combine(persistentModelDirectory, file);
                    if (File.Exists(destination) && new FileInfo(destination).Length > 0L)
                    {
                        LogP45E05ModelResolveEvent(
                            "p45e05_sherpa_model_file_reused",
                            streamingModelDirectory,
                            persistentModelDirectory,
                            BuildStreamingAssetsUri(_options.StreamingAssetsRelativeRoot, _options.ModelName, file),
                            destination,
                            file,
                            new FileInfo(destination).Length,
                            0L,
                            stopwatch.ElapsedMilliseconds,
                            string.Empty);
                        continue;
                    }

                    Stopwatch fileStopwatch = Stopwatch.StartNew();
                    string sourceUri = BuildStreamingAssetsUri(_options.StreamingAssetsRelativeRoot, _options.ModelName, file);
                    string temporaryDestination = destination + ".tmp";
                    if (File.Exists(temporaryDestination))
                    {
                        File.Delete(temporaryDestination);
                    }

                    LogP45E05ModelResolveEvent(
                        "p45e05_sherpa_model_file_copy_started",
                        streamingModelDirectory,
                        persistentModelDirectory,
                        sourceUri,
                        destination,
                        file,
                        0L,
                        0L,
                        stopwatch.ElapsedMilliseconds,
                        string.Empty);
                    using UnityWebRequest request = UnityWebRequest.Get(sourceUri);
                    request.downloadHandler = new DownloadHandlerFile(temporaryDestination);
                    UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                    while (!operation.isDone)
                    {
                        await Task.Yield();
                    }

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        fileStopwatch.Stop();
                        stopwatch.Stop();
                        string error = $"model_copy_failed: source='{sourceUri}' target='{destination}' error='{request.error}'";
                        LogP45E05ModelResolveEvent(
                            "p45e05_sherpa_model_resolve_failed",
                            streamingModelDirectory,
                            persistentModelDirectory,
                            sourceUri,
                            destination,
                            file,
                            0L,
                            fileStopwatch.ElapsedMilliseconds,
                            stopwatch.ElapsedMilliseconds,
                            error);
                        return SherpaModelResolveResult.Failed(
                            error,
                            stopwatch.ElapsedMilliseconds);
                    }

                    fileStopwatch.Stop();
                    request.Dispose();
                    if (File.Exists(destination))
                    {
                        File.Delete(destination);
                    }

                    File.Move(temporaryDestination, destination);
                    long fileSize = File.Exists(destination) ? new FileInfo(destination).Length : 0L;
                    if (fileSize <= 0L)
                    {
                        stopwatch.Stop();
                        string error = $"file_size_zero: source='{sourceUri}' target='{destination}'";
                        LogP45E05ModelResolveEvent(
                            "p45e05_sherpa_model_resolve_failed",
                            streamingModelDirectory,
                            persistentModelDirectory,
                            sourceUri,
                            destination,
                            file,
                            fileSize,
                            fileStopwatch.ElapsedMilliseconds,
                            stopwatch.ElapsedMilliseconds,
                            error);
                        return SherpaModelResolveResult.Failed(error, stopwatch.ElapsedMilliseconds);
                    }

                    LogP45E05ModelResolveEvent(
                        "p45e05_sherpa_model_file_copy_completed",
                        streamingModelDirectory,
                        persistentModelDirectory,
                        sourceUri,
                        destination,
                        file,
                        fileSize,
                        fileStopwatch.ElapsedMilliseconds,
                        stopwatch.ElapsedMilliseconds,
                        string.Empty);
                }

                stopwatch.Stop();
                SherpaModelResolveResult copied = SherpaModelResolveResult.Successful(
                    persistentModelDirectory,
                    stopwatch.ElapsedMilliseconds,
                    "streaming_assets_copied_to_persistent_data");
                LogP45E05ModelResolveEvent(
                    "p45e05_sherpa_model_resolve_completed",
                    streamingModelDirectory,
                    persistentModelDirectory,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0L,
                    0L,
                    stopwatch.ElapsedMilliseconds,
                    copied.Reason);
                return copied;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                string error = "model_copy_failed: " + ex.GetBaseException().Message;
                LogP45E05ModelResolveEvent(
                    "p45e05_sherpa_model_resolve_failed",
                    streamingModelDirectory,
                    persistentModelDirectory,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0L,
                    0L,
                    stopwatch.ElapsedMilliseconds,
                    error);
                return SherpaModelResolveResult.Failed(
                    error,
                    stopwatch.ElapsedMilliseconds);
            }
        }

        private string BuildStreamingAssetsUri(params string[] segments)
        {
            string root = SafeInvoke(_streamingAssetsPathProvider).Replace("\\", "/").TrimEnd('/');
            string relative = string.Join("/", segments.Where(segment => !string.IsNullOrWhiteSpace(segment))
                .Select(segment => segment.Trim('/').Replace("\\", "/")));
            return string.IsNullOrWhiteSpace(relative) ? root : $"{root}/{relative}";
        }

        private void LogP45E05PreflightModelPathResolved(
            ASRBackend requestedBackend,
            string platform,
            string streamingModelPath,
            string persistentModelPath,
            string selectedModelPath)
        {
            Dictionary<string, object> payload = BuildP45E05ModelResolvePayload(
                requestedBackend,
                platform,
                streamingModelPath,
                persistentModelPath,
                string.Empty,
                selectedModelPath,
                string.Empty,
                0L,
                0L,
                0L,
                string.Empty);
            payload["model_path"] = selectedModelPath ?? string.Empty;
            TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_preflight_model_path_resolved", payload);
        }

        private void LogP45E05ModelResolveEvent(
            string eventName,
            string streamingModelPath,
            string persistentModelPath,
            string sourceUri,
            string targetPath,
            string fileName,
            long fileSize,
            long copyMs,
            long resolveMs,
            string errorReason)
        {
            Dictionary<string, object> payload = BuildP45E05ModelResolvePayload(
                ASRBackend.SherpaOnnx,
                SafeInvoke(_platformProvider),
                streamingModelPath,
                persistentModelPath,
                sourceUri,
                targetPath,
                fileName,
                fileSize,
                copyMs,
                resolveMs,
                errorReason);
            TiagoExperimentTelemetry.LogEvent(eventName, payload);
        }

        private Dictionary<string, object> BuildP45E05ModelResolvePayload(
            ASRBackend requestedBackend,
            string platform,
            string streamingModelPath,
            string persistentModelPath,
            string sourceUri,
            string targetPath,
            string fileName,
            long fileSize,
            long copyMs,
            long resolveMs,
            string errorReason)
        {
            TryResolveModelFileSet(
                string.IsNullOrWhiteSpace(persistentModelPath) ? BuildModelDirectoryForPreflight(platform) : persistentModelPath,
                _options,
                out _,
                out string modelLayout,
                out string modelFilesReport,
                out _);
            return new Dictionary<string, object>
            {
                ["requested_backend"] = requestedBackend.ToString(),
                ["effective_backend"] = ASRBackend.SherpaOnnx.ToString(),
                ["model_name"] = _options.ModelName,
                ["streaming_model_path"] = streamingModelPath ?? string.Empty,
                ["persistent_model_path"] = persistentModelPath ?? string.Empty,
                ["model_path"] = persistentModelPath ?? string.Empty,
                ["source_uri"] = sourceUri ?? string.Empty,
                ["target_path"] = targetPath ?? string.Empty,
                ["file_name"] = fileName ?? string.Empty,
                ["file_size"] = Math.Max(0L, fileSize),
                ["copy_ms"] = Math.Max(0L, copyMs),
                ["model_copy_ms"] = Math.Max(0L, copyMs),
                ["model_resolve_ms"] = Math.Max(0L, resolveMs),
                ["model_layout"] = modelLayout ?? string.Empty,
                ["model_files"] = modelFilesReport ?? string.Empty,
                ["runtime_source"] = ResolveRuntimeSourceKind(_lastPreflight),
                ["runtime_source_detail"] = _lastPreflight?.Reason ?? string.Empty,
                ["runtime_path"] = _lastPreflight?.RuntimePath ?? string.Empty,
                ["runtime_files"] = _lastPreflight?.RuntimeFilesReport ?? string.Empty,
                ["runtime_files_ok_embedded_in_aar"] = RuntimeReportHasEmbeddedRuntime(_lastPreflight),
                ["explicit_native_libs_present"] = RuntimeReportHasExplicitRuntime(_lastPreflight),
                ["duplicate_native_libs"] = RuntimeReportHasDuplicateNativeLibraries(_lastPreflight),
                ["android_bridge_detected"] = !string.IsNullOrWhiteSpace(_lastPreflight?.RuntimeBindingType),
                ["bridge_class"] = _lastPreflight?.RuntimeBindingType ?? _options.AndroidRecognizerClassName,
                ["platform"] = platform ?? string.Empty,
                ["architecture_abi"] = _lastPreflight?.ArchitectureAbi ?? _options.AndroidAbi,
                ["sample_rate"] = 16000,
                ["audio_duration_ms"] = 0L,
                ["recognizer_create_ms"] = 0L,
                ["inference_latency_ms"] = 0L,
                ["total_latency_ms"] = 0L,
                ["transcript"] = string.Empty,
                ["error_reason"] = errorReason ?? string.Empty,
                ["fallback_used"] = false
            };
        }

        private ASRResult FailSherpaTranscription(
            ASRRequest request,
            ASRBackendPreflightResult preflight,
            long durationMs,
            string microphoneDevice,
            int sampleRate,
            ASRAudioAnalysis analysis,
            string diagnosticAudioPath,
            string error,
            long modelCopyMs,
            long recognizerCreateMs,
            long inferenceLatencyMs)
        {
            ASRResult failed = ASRResult.Failed(
                request.UtteranceId,
                ASRBackend.SherpaOnnx,
                error,
                request.Language,
                durationMs,
                microphoneDevice,
                sampleRate,
                analysis,
                diagnosticAudioPath);
            Dictionary<string, object> failedPayload = BuildP45E03RuntimePayload(request, preflight, durationMs, error, failed.Transcript);
            failedPayload["model_copy_ms"] = modelCopyMs;
            failedPayload["recognizer_create_ms"] = recognizerCreateMs;
            failedPayload["inference_latency_ms"] = inferenceLatencyMs;
            TiagoExperimentTelemetry.LogEvent("p45e03_sherpa_transcription_failed", failedPayload);
            TiagoExperimentTelemetry.LogEvent("p45e04_sherpa_transcription_failed", failedPayload);
            TiagoExperimentTelemetry.LogEvent("p45e05_sherpa_transcription_failed", failedPayload);
            return failed;
        }

        private static bool TryBuildMono16KhzWaveform(
            AudioClip clip,
            out float[] waveform,
            out int sampleRate,
            out string error,
            out long mainThreadAudioExtractMs,
            out long mainThreadResampleMs)
        {
            waveform = Array.Empty<float>();
            sampleRate = 16000;
            error = string.Empty;
            mainThreadAudioExtractMs = 0L;
            mainThreadResampleMs = 0L;
            if (clip == null)
            {
                error = "unsupported_audio_format: audio_clip_missing";
                return false;
            }

            if (clip.samples <= 0 || clip.channels <= 0 || clip.frequency <= 0)
            {
                error = $"unsupported_audio_format: samples={clip.samples} channels={clip.channels} frequency={clip.frequency}";
                return false;
            }

            Stopwatch extractStopwatch = Stopwatch.StartNew();
            float[] interleaved = new float[clip.samples * clip.channels];
            if (!clip.GetData(interleaved, 0))
            {
                extractStopwatch.Stop();
                mainThreadAudioExtractMs = extractStopwatch.ElapsedMilliseconds;
                error = "unsupported_audio_format: clip_get_data_failed";
                return false;
            }

            float[] mono = new float[clip.samples];
            for (int frame = 0; frame < clip.samples; frame++)
            {
                double sum = 0.0;
                int offset = frame * clip.channels;
                for (int channel = 0; channel < clip.channels; channel++)
                {
                    sum += interleaved[offset + channel];
                }

                mono[frame] = (float)(sum / clip.channels);
            }
            extractStopwatch.Stop();
            mainThreadAudioExtractMs = extractStopwatch.ElapsedMilliseconds;

            const int targetRate = 16000;
            sampleRate = targetRate;
            if (clip.frequency == targetRate)
            {
                waveform = mono;
                return true;
            }

            Stopwatch resampleStopwatch = Stopwatch.StartNew();
            int targetLength = Math.Max(1, (int)Math.Round((double)mono.Length * targetRate / clip.frequency));
            waveform = new float[targetLength];
            double sourceStep = (double)clip.frequency / targetRate;
            for (int i = 0; i < targetLength; i++)
            {
                double sourcePosition = i * sourceStep;
                int lower = Math.Min(mono.Length - 1, (int)Math.Floor(sourcePosition));
                int upper = Math.Min(mono.Length - 1, lower + 1);
                double fraction = sourcePosition - lower;
                waveform[i] = (float)(mono[lower] + (mono[upper] - mono[lower]) * fraction);
            }
            resampleStopwatch.Stop();
            mainThreadResampleMs = resampleStopwatch.ElapsedMilliseconds;

            return true;
        }

        private RuntimeProbeResult ProbeRuntime(string platform)
        {
            string dataPath = SafeInvoke(_dataPathProvider);
            string androidPluginRoot = string.IsNullOrWhiteSpace(dataPath)
                ? string.Empty
                : Path.Combine(dataPath, _options.AndroidPluginRelativePath);
            string nativeLibraryDirectory = string.IsNullOrWhiteSpace(androidPluginRoot)
                ? string.Empty
                : Path.Combine(androidPluginRoot, "libs", _options.AndroidAbi);
            string jniLibraryPath = string.IsNullOrWhiteSpace(nativeLibraryDirectory)
                ? string.Empty
                : Path.Combine(nativeLibraryDirectory, _options.AndroidJniLibraryFileName);
            string onnxRuntimePath = string.IsNullOrWhiteSpace(nativeLibraryDirectory)
                ? string.Empty
                : Path.Combine(nativeLibraryDirectory, _options.OnnxRuntimeLibraryFileName);
            AndroidBridgeInspection bridgeInspection = InspectAndroidBridge(
                androidPluginRoot,
                _options.AndroidRecognizerClassName,
                _options.AndroidAbi,
                _options.AndroidJniLibraryFileName,
                _options.OnnxRuntimeLibraryFileName);
            string runtimeFilesReport = BuildRuntimeFilesReport(
                androidPluginRoot,
                nativeLibraryDirectory,
                jniLibraryPath,
                onnxRuntimePath,
                bridgeInspection);
            string bindingTypeName = TryResolveManagedBinding(out string resolvedBinding)
                ? resolvedBinding
                : string.Empty;

            bool hasManagedBinding = !string.IsNullOrWhiteSpace(bindingTypeName);
            bool hasStandaloneJniLibrary = FileExistsWithBytes(jniLibraryPath);
            bool hasStandaloneOnnxRuntime = FileExistsWithBytes(onnxRuntimePath);
            bool hasAndroidArchive = bridgeInspection.HasArchive;
            bool hasValidAndroidBridge = bridgeInspection.HasExpectedClass;
            bool hasEmbeddedJniLibrary = bridgeInspection.HasEmbeddedJniLibrary;
            bool hasEmbeddedOnnxRuntime = bridgeInspection.HasEmbeddedOnnxRuntime;
            bool hasAnyJniLibrary = hasStandaloneJniLibrary || hasEmbeddedJniLibrary;
            bool hasAnyOnnxRuntime = hasStandaloneOnnxRuntime || hasEmbeddedOnnxRuntime;
            bool hasDuplicateNativeLibraries = hasStandaloneJniLibrary && hasEmbeddedJniLibrary ||
                hasStandaloneOnnxRuntime && hasEmbeddedOnnxRuntime;
            bool duplicateNativeLibrariesSameHash = hasDuplicateNativeLibraries &&
                (!hasStandaloneJniLibrary || !hasEmbeddedJniLibrary || HashMatches(jniLibraryPath, bridgeInspection.EmbeddedJniSha256)) &&
                (!hasStandaloneOnnxRuntime || !hasEmbeddedOnnxRuntime || HashMatches(onnxRuntimePath, bridgeInspection.EmbeddedOnnxRuntimeSha256));
            bool isAndroidRuntime = IsAndroidPlatform(platform);

#if UNITY_ANDROID && !UNITY_EDITOR
            if (isAndroidRuntime)
            {
                try
                {
                    using AndroidJavaClass recognizerClass = new(_options.AndroidRecognizerClassName);
                    string className = recognizerClass.GetRawClass().ToString();
                    return RuntimeProbeResult.Ready(
                        androidPluginRoot,
                        runtimeFilesReport,
                        string.IsNullOrWhiteSpace(bindingTypeName) ? _options.AndroidRecognizerClassName : bindingTypeName,
                        _options.AndroidAbi,
                        "runtime_files_ok_embedded_in_aar android_bridge_detected android_jni_class_loaded:" + className);
                }
                catch (AndroidJavaException ex)
                {
                    string message = ex.GetBaseException().Message ?? string.Empty;
                    string reason = message.IndexOf("UnsatisfiedLinkError", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        message.IndexOf("no sherpa", StringComparison.OrdinalIgnoreCase) >= 0
                            ? "native_library_missing"
                            : "jni_bridge_missing";
                    return RuntimeProbeResult.Missing(
                        androidPluginRoot,
                        runtimeFilesReport,
                        string.IsNullOrWhiteSpace(bindingTypeName) ? _options.AndroidRecognizerClassName : bindingTypeName,
                        _options.AndroidAbi,
                        reason,
                        $"Android JNI bridge load failed for {_options.AndroidRecognizerClassName}: {message}");
                }
            }
#endif

            if (hasDuplicateNativeLibraries)
            {
                return RuntimeProbeResult.Missing(
                    androidPluginRoot,
                    runtimeFilesReport,
                    string.IsNullOrWhiteSpace(bindingTypeName) ? _options.AndroidRecognizerClassName : bindingTypeName,
                    _options.AndroidAbi,
                    duplicateNativeLibrariesSameHash
                        ? "android_packaging_duplicate_native_libs"
                        : "runtime_files_duplicate_conflict",
                    duplicateNativeLibrariesSameHash
                        ? "runtime_files_duplicate_same_hash android_packaging_duplicate_native_libs: native libraries exist both as explicit Unity plugins and embedded inside AAR. Use a single source before building APK."
                        : "runtime_files_duplicate_conflict: explicit native libraries and AAR embedded libraries differ.");
            }

            if (!hasManagedBinding && !hasAnyJniLibrary && !hasAndroidArchive)
            {
                return RuntimeProbeResult.Missing(
                    androidPluginRoot,
                    runtimeFilesReport,
                    string.Empty,
                    _options.AndroidAbi,
                    "runtime_binding_missing",
                    $"managed SherpaOnnx recognizer type not found; Android package not found. Expected C# binding type one of {string.Join("|", CandidateManagedBindingTypeNames)} or Android bridge class {_options.AndroidRecognizerClassName} from AAR/JAR under {androidPluginRoot} or native library {jniLibraryPath}");
            }

            if (hasAndroidArchive && !hasValidAndroidBridge)
            {
                return RuntimeProbeResult.Missing(
                    androidPluginRoot,
                    runtimeFilesReport,
                    string.Empty,
                    _options.AndroidAbi,
                    "aar_missing_expected_classes",
                    $"android_bridge_invalid aar_missing_expected_classes: Android AAR/JAR detected but {_options.AndroidRecognizerClassName} was not found. bridge_archive='{bridgeInspection.ArchivePath}' inspected_classes='{bridgeInspection.Details}'");
            }

            if (hasValidAndroidBridge && !hasAnyJniLibrary)
            {
                return RuntimeProbeResult.Missing(
                    androidPluginRoot,
                    runtimeFilesReport,
                    string.IsNullOrWhiteSpace(bindingTypeName) ? _options.AndroidRecognizerClassName : bindingTypeName,
                    _options.AndroidAbi,
                    "native_library_missing",
                    $"android_bridge_detected native_library_missing: Android AAR/JAR contains {_options.AndroidRecognizerClassName}, but {_options.AndroidJniLibraryFileName} was not found explicitly or embedded in the AAR. bridge_archive='{bridgeInspection.ArchivePath}'");
            }

            if (hasAnyJniLibrary && !hasValidAndroidBridge && !hasManagedBinding)
            {
                return RuntimeProbeResult.Missing(
                    androidPluginRoot,
                    runtimeFilesReport,
                    string.Empty,
                    _options.AndroidAbi,
                    "android_bridge_missing",
                    $"runtime_files_ok android_bridge_missing: native JNI runtime files found but no Android AAR/JAR or managed wrapper exposes {_options.AndroidRecognizerClassName}.");
            }

            if (hasAnyJniLibrary && !hasAnyOnnxRuntime)
            {
                return RuntimeProbeResult.Missing(
                    androidPluginRoot,
                    runtimeFilesReport,
                    string.IsNullOrWhiteSpace(bindingTypeName) ? _options.AndroidRecognizerClassName : bindingTypeName,
                    _options.AndroidAbi,
                    "native_library_missing",
                    $"Found {_options.AndroidJniLibraryFileName} but {_options.OnnxRuntimeLibraryFileName} is missing; if sherpa was built static this may be acceptable, but this prototype requires explicit confirmation before enabling runtime.");
            }

            string runtimeSourceReason = hasEmbeddedJniLibrary && hasEmbeddedOnnxRuntime && !hasStandaloneJniLibrary && !hasStandaloneOnnxRuntime
                ? "runtime_files_ok_embedded_in_aar android_bridge_detected"
                : "runtime_files_ok_explicit android_bridge_detected";
            return RuntimeProbeResult.Ready(
                androidPluginRoot,
                runtimeFilesReport,
                string.IsNullOrWhiteSpace(bindingTypeName) ? _options.AndroidRecognizerClassName : bindingTypeName,
                _options.AndroidAbi,
                hasValidAndroidBridge ? runtimeSourceReason : "runtime_artifacts_detected");
        }

        private static bool TryResolveModelFileSet(
            string modelDirectory,
            SherpaOnnxBackendOptions options,
            out string[] resolvedFiles,
            out string selectedLayout,
            out string filesReport,
            out string missingFileReport)
        {
            resolvedFiles = Array.Empty<string>();
            selectedLayout = string.Empty;
            filesReport = BuildFilesReport(modelDirectory);
            List<string> candidateReports = new();
            foreach (string[] requiredSet in BuildSupportedModelFileSets(options))
            {
                string[] fullPaths = requiredSet.Select(file => Path.Combine(modelDirectory, file)).ToArray();
                string[] missing = fullPaths.Where(path => !File.Exists(path) || new FileInfo(path).Length <= 0L).ToArray();
                if (missing.Length == 0)
                {
                    resolvedFiles = fullPaths;
                    selectedLayout = string.Join("+", requiredSet);
                    missingFileReport = string.Empty;
                    return true;
                }

                candidateReports.Add(string.Join("+", requiredSet));
            }

            missingFileReport = string.Join(" | ", candidateReports);
            return false;
        }

        private static IEnumerable<string[]> BuildSupportedModelFileSets(SherpaOnnxBackendOptions options)
        {
            string[][] candidates =
            {
                new[] { options.EncoderFileName, options.DecoderFileName, options.JoinerFileName, options.TokensFileName },
                new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" },
                new[] { "encoder.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt" },
                new[] { "model.onnx", "tokens.txt" }
            };

            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (string[] candidate in candidates)
            {
                if (candidate.Any(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                string key = string.Join("|", candidate.Select(file => file.Trim()));
                if (seen.Add(key))
                {
                    yield return candidate.Select(file => file.Trim()).ToArray();
                }
            }
        }

        private static string BuildFilesReport(string modelDirectory)
        {
            if (string.IsNullOrWhiteSpace(modelDirectory) || !Directory.Exists(modelDirectory))
            {
                return string.Empty;
            }

            return string.Join(
                ",",
                Directory.GetFiles(modelDirectory, "*", SearchOption.TopDirectoryOnly)
                    .Where(File.Exists)
                    .Select(path => new FileInfo(path))
                    .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(info => $"{info.Name}:{info.Length}"));
        }

        private static bool IsSpanishOrMultilingualModel(string modelName, string modelDirectory)
        {
            string lower = (modelName ?? string.Empty).ToLowerInvariant();
            bool nameLooksCompatible = lower.Contains("spanish") ||
                lower.Contains("es-") ||
                lower.Contains("-es") ||
                lower.Contains("_es") ||
                lower.Contains("multilingual") ||
                lower.Contains("multi") ||
                lower.Contains("whisper") ||
                lower.Contains("parakeet");

            if (nameLooksCompatible)
            {
                return true;
            }

            string spanishTestWav = Path.Combine(modelDirectory ?? string.Empty, "test_wavs", "es.wav");
            return File.Exists(spanishTestWav);
        }

        private static bool TryResolveManagedBinding(out string typeName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (string candidate in CandidateManagedBindingTypeNames)
                {
                    Type type = assembly.GetType(candidate, throwOnError: false);
                    if (type != null)
                    {
                        typeName = type.FullName;
                        return true;
                    }
                }
            }

            typeName = string.Empty;
            return false;
        }

        private static bool IsAndroidPlatform(string platform)
        {
            return !string.IsNullOrWhiteSpace(platform) &&
                platform.IndexOf("Android", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string SafeInvoke(Func<string> provider)
        {
            try
            {
                return provider?.Invoke() ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool FileExistsWithBytes(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path).Length > 0L;
        }

        private static AndroidBridgeInspection InspectAndroidBridge(
            string androidPluginRoot,
            string recognizerClassName,
            string androidAbi,
            string jniLibraryFileName,
            string onnxRuntimeLibraryFileName)
        {
            if (string.IsNullOrWhiteSpace(androidPluginRoot) || !Directory.Exists(androidPluginRoot))
            {
                return AndroidBridgeInspection.Missing();
            }

            string expectedClassPath = (string.IsNullOrWhiteSpace(recognizerClassName)
                    ? SherpaOnnxBackendOptions.DefaultAndroidRecognizerClassName
                    : recognizerClassName)
                .Replace('.', '/') + ".class";

            foreach (string archivePath in Directory.GetFiles(androidPluginRoot, "*.aar", SearchOption.TopDirectoryOnly)
                         .Concat(Directory.GetFiles(androidPluginRoot, "*.jar", SearchOption.TopDirectoryOnly))
                         .Where(FileExistsWithBytes)
                         .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                if (InspectArchive(
                        archivePath,
                        expectedClassPath,
                        androidAbi,
                        jniLibraryFileName,
                        onnxRuntimeLibraryFileName,
                        out string details,
                        out bool hasEmbeddedJniLibrary,
                        out long embeddedJniSize,
                        out string embeddedJniSha256,
                        out bool hasEmbeddedOnnxRuntime,
                        out long embeddedOnnxRuntimeSize,
                        out string embeddedOnnxRuntimeSha256))
                {
                    return AndroidBridgeInspection.Valid(
                        archivePath,
                        details,
                        hasEmbeddedJniLibrary,
                        embeddedJniSize,
                        embeddedJniSha256,
                        hasEmbeddedOnnxRuntime,
                        embeddedOnnxRuntimeSize,
                        embeddedOnnxRuntimeSha256);
                }

                return AndroidBridgeInspection.Invalid(
                    archivePath,
                    details,
                    hasEmbeddedJniLibrary,
                    embeddedJniSize,
                    embeddedJniSha256,
                    hasEmbeddedOnnxRuntime,
                    embeddedOnnxRuntimeSize,
                    embeddedOnnxRuntimeSha256);
            }

            return AndroidBridgeInspection.Missing();
        }

        private static bool InspectArchive(
            string archivePath,
            string expectedClassPath,
            string androidAbi,
            string jniLibraryFileName,
            string onnxRuntimeLibraryFileName,
            out string details,
            out bool hasEmbeddedJniLibrary,
            out long embeddedJniSize,
            out string embeddedJniSha256,
            out bool hasEmbeddedOnnxRuntime,
            out long embeddedOnnxRuntimeSize,
            out string embeddedOnnxRuntimeSha256)
        {
            details = string.Empty;
            hasEmbeddedJniLibrary = false;
            embeddedJniSize = 0L;
            embeddedJniSha256 = string.Empty;
            hasEmbeddedOnnxRuntime = false;
            embeddedOnnxRuntimeSize = 0L;
            embeddedOnnxRuntimeSha256 = string.Empty;
            try
            {
                using ZipArchive archive = ZipFile.OpenRead(archivePath);
                string jniEntryPath = $"jni/{androidAbi}/{jniLibraryFileName}";
                string onnxRuntimeEntryPath = $"jni/{androidAbi}/{onnxRuntimeLibraryFileName}";
                ZipArchiveEntry jniEntry = archive.GetEntry(jniEntryPath);
                ZipArchiveEntry onnxRuntimeEntry = archive.GetEntry(onnxRuntimeEntryPath);
                if (jniEntry != null && jniEntry.Length > 0L)
                {
                    hasEmbeddedJniLibrary = true;
                    embeddedJniSize = jniEntry.Length;
                    embeddedJniSha256 = ComputeEntrySha256(jniEntry);
                }

                if (onnxRuntimeEntry != null && onnxRuntimeEntry.Length > 0L)
                {
                    hasEmbeddedOnnxRuntime = true;
                    embeddedOnnxRuntimeSize = onnxRuntimeEntry.Length;
                    embeddedOnnxRuntimeSha256 = ComputeEntrySha256(onnxRuntimeEntry);
                }

                bool isJar = archivePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
                if (isJar)
                {
                    bool hasClass = archive.Entries.Any(entry =>
                        entry.FullName.Equals(expectedClassPath, StringComparison.OrdinalIgnoreCase));
                    details = hasClass ? expectedClassPath : "jar_missing:" + expectedClassPath;
                    return hasClass;
                }

                ZipArchiveEntry classesEntry = archive.GetEntry("classes.jar");
                if (classesEntry == null)
                {
                    details = "aar_missing:classes.jar";
                    return false;
                }

                using Stream classesStream = classesEntry.Open();
                using MemoryStream classesBytes = new();
                classesStream.CopyTo(classesBytes);
                classesBytes.Position = 0L;
                using ZipArchive classesArchive = new(classesBytes, ZipArchiveMode.Read, leaveOpen: false);
                bool aarHasClass = classesArchive.Entries.Any(entry =>
                    entry.FullName.Equals(expectedClassPath, StringComparison.OrdinalIgnoreCase));
                details = aarHasClass ? "classes.jar:" + expectedClassPath : "classes.jar_missing:" + expectedClassPath;
                return aarHasClass;
            }
            catch (Exception ex)
            {
                details = "archive_read_failed:" + ex.GetBaseException().Message;
                return false;
            }
        }

        private static string ComputeEntrySha256(ZipArchiveEntry entry)
        {
            using Stream stream = entry.Open();
            using SHA256 sha256 = SHA256.Create();
            return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static bool HashMatches(string filePath, string expectedSha256)
        {
            if (!FileExistsWithBytes(filePath) || string.IsNullOrWhiteSpace(expectedSha256))
            {
                return false;
            }

            using FileStream stream = File.OpenRead(filePath);
            using SHA256 sha256 = SHA256.Create();
            string actual = BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty);
            return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildRuntimeFilesReport(
            string androidPluginRoot,
            string nativeLibraryDirectory,
            string jniLibraryPath,
            string onnxRuntimePath,
            AndroidBridgeInspection bridgeInspection)
        {
            List<string> report = new();
            AddPathReport(report, "android_plugin_root", androidPluginRoot, requireFile: false);
            AddPathReport(report, "native_lib_dir", nativeLibraryDirectory, requireFile: false);
            string explicitJniLabel = bridgeInspection.HasEmbeddedJniLibrary
                ? "explicit_" + Path.GetFileName(jniLibraryPath) + "_optional"
                : Path.GetFileName(jniLibraryPath);
            string explicitOnnxLabel = bridgeInspection.HasEmbeddedOnnxRuntime
                ? "explicit_" + Path.GetFileName(onnxRuntimePath) + "_optional"
                : Path.GetFileName(onnxRuntimePath);
            if (bridgeInspection.HasEmbeddedJniLibrary)
            {
                AddOptionalPathReport(report, explicitJniLabel, jniLibraryPath);
            }
            else
            {
                AddPathReport(report, explicitJniLabel, jniLibraryPath, requireFile: true);
            }

            if (bridgeInspection.HasEmbeddedOnnxRuntime)
            {
                AddOptionalPathReport(report, explicitOnnxLabel, onnxRuntimePath);
            }
            else
            {
                AddPathReport(report, explicitOnnxLabel, onnxRuntimePath, requireFile: true);
            }
            AddEmbeddedLibraryReport(
                report,
                "aar_embedded_" + Path.GetFileName(jniLibraryPath),
                bridgeInspection.HasEmbeddedJniLibrary,
                bridgeInspection.EmbeddedJniSize,
                bridgeInspection.EmbeddedJniSha256,
                bridgeInspection.ArchivePath);
            AddEmbeddedLibraryReport(
                report,
                "aar_embedded_" + Path.GetFileName(onnxRuntimePath),
                bridgeInspection.HasEmbeddedOnnxRuntime,
                bridgeInspection.EmbeddedOnnxRuntimeSize,
                bridgeInspection.EmbeddedOnnxRuntimeSha256,
                bridgeInspection.ArchivePath);

            if (Directory.Exists(androidPluginRoot))
            {
                foreach (string file in Directory.GetFiles(androidPluginRoot, "*.aar", SearchOption.TopDirectoryOnly)
                             .Concat(Directory.GetFiles(androidPluginRoot, "*.jar", SearchOption.TopDirectoryOnly))
                             .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                {
                    AddPathReport(report, Path.GetFileName(file), file, requireFile: true);
                }
            }

            return string.Join(",", report);
        }

        private static void AddEmbeddedLibraryReport(
            List<string> report,
            string label,
            bool exists,
            long size,
            string sha256,
            string archivePath)
        {
            report.Add(exists
                ? $"{label}:{size}:sha256={sha256}:{archivePath}"
                : $"{label}:missing:{archivePath}");
        }

        private static void AddPathReport(List<string> report, string label, string path, bool requireFile)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                report.Add($"{label}:missing_path");
                return;
            }

            if (requireFile)
            {
                report.Add(FileExistsWithBytes(path)
                    ? $"{label}:{new FileInfo(path).Length}:{path}"
                    : $"{label}:missing:{path}");
                return;
            }

            report.Add(Directory.Exists(path)
                ? $"{label}:exists:{path}"
                : $"{label}:missing:{path}");
        }

        private static void AddOptionalPathReport(List<string> report, string label, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                report.Add($"{label}:not_configured");
                return;
            }

            report.Add(FileExistsWithBytes(path)
                ? $"{label}:{new FileInfo(path).Length}:{path}"
                : $"{label}:not_present:{path}");
        }

        private static long ResolveMainFileSize(IEnumerable<string> modelFiles)
        {
            if (modelFiles == null)
            {
                return 0L;
            }

            string main = modelFiles.FirstOrDefault(path =>
                Path.GetFileName(path).Equals("model.onnx", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Equals("encoder.onnx", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Equals("encoder.int8.onnx", StringComparison.OrdinalIgnoreCase));
            return !string.IsNullOrWhiteSpace(main) && File.Exists(main)
                ? new FileInfo(main).Length
                : 0L;
        }

        private static long ResolveMainFileSize(string modelDirectory)
        {
            if (string.IsNullOrWhiteSpace(modelDirectory) || !Directory.Exists(modelDirectory))
            {
                return 0L;
            }

            string model = Directory.GetFiles(modelDirectory, "*.onnx", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => new FileInfo(path).Length)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(model) ? 0L : new FileInfo(model).Length;
        }

        private Dictionary<string, object> BuildP45E03RuntimePayload(
            ASRRequest request,
            ASRBackendPreflightResult preflight,
            long audioDurationMs,
            string error,
            string transcript)
        {
            return new Dictionary<string, object>
            {
                ["backend_requested"] = request?.RequestedBackend.ToString() ?? ASRBackend.SherpaOnnx.ToString(),
                ["backend_effective"] = ASRBackend.SherpaOnnx.ToString(),
                ["model_name"] = preflight?.ModelName ?? _options.ModelName,
                ["model_layout"] = preflight?.ModelLayout ?? string.Empty,
                ["model_path"] = preflight?.ModelPath ?? BuildModelDirectory(),
                ["persistent_model_path"] = string.Empty,
                ["streaming_model_path"] = BuildStreamingAssetsModelDirectory(),
                ["source_uri"] = string.Empty,
                ["target_path"] = string.Empty,
                ["file_name"] = string.Empty,
                ["file_size"] = 0L,
                ["copy_ms"] = 0L,
                ["model_resolve_ms"] = 0L,
                ["runtime_source"] = ResolveRuntimeSourceKind(preflight),
                ["runtime_source_detail"] = preflight?.Reason ?? string.Empty,
                ["runtime_path"] = preflight?.RuntimePath ?? string.Empty,
                ["plugin_path"] = preflight?.RuntimePath ?? string.Empty,
                ["runtime_files"] = preflight?.RuntimeFilesReport ?? string.Empty,
                ["runtime_files_ok_embedded_in_aar"] = RuntimeReportHasEmbeddedRuntime(preflight),
                ["explicit_native_libs_present"] = RuntimeReportHasExplicitRuntime(preflight),
                ["duplicate_native_libs"] = RuntimeReportHasDuplicateNativeLibraries(preflight),
                ["android_bridge_detected"] = !string.IsNullOrWhiteSpace(preflight?.RuntimeBindingType),
                ["runtime_binding_type"] = preflight?.RuntimeBindingType ?? string.Empty,
                ["bridge_class"] = preflight?.RuntimeBindingType ?? _options.AndroidRecognizerClassName,
                ["platform"] = SafeInvoke(_platformProvider),
                ["architecture"] = preflight?.ArchitectureAbi ?? _options.AndroidAbi,
                ["architecture_abi"] = preflight?.ArchitectureAbi ?? _options.AndroidAbi,
                ["sample_rate"] = request?.SampleRate ?? 0,
                ["audio_duration_ms"] = Math.Max(0L, audioDurationMs),
                ["main_thread_audio_extract_ms"] = 0L,
                ["main_thread_resample_ms"] = 0L,
                ["model_copy_ms"] = 0L,
                ["recognizer_create_ms"] = 0L,
                ["recognizer_created_this_call"] = false,
                ["recognizer_reused"] = false,
                ["queue_wait_ms"] = 0L,
                ["create_stream_ms"] = 0L,
                ["accept_waveform_ms"] = 0L,
                ["decode_ms"] = 0L,
                ["get_result_ms"] = 0L,
                ["worker_decode_ms"] = 0L,
                ["worker_total_ms"] = 0L,
                ["worker_thread_used"] = false,
                ["dedicated_worker_enabled"] = false,
                ["legacy_worker_requested"] = false,
                ["worker_decode_experimental_enabled"] = false,
                ["decode_thread_mode"] = string.Empty,
                ["decode_route_fallback_enabled"] = false,
                ["decode_route_fallback_used"] = false,
                ["jni_thread_attached"] = false,
                ["hotwords_enabled"] = _options.EnableSherpaHotwords,
                ["main_thread_block_ms"] = 0L,
                ["inference_latency_ms"] = 0L,
                ["total_asr_latency_ms"] = 0L,
                ["total_latency_ms"] = 0L,
                ["transcript"] = transcript ?? string.Empty,
                ["error_reason"] = error ?? string.Empty,
                ["fallback_used"] = false,
                ["utterance_id"] = request?.UtteranceId ?? string.Empty
            };
        }

        private readonly struct SherpaModelResolveResult
        {
            private SherpaModelResolveResult(bool success, string modelDirectory, long copyMs, string reason, string errorReason)
            {
                Success = success;
                ModelDirectory = modelDirectory ?? string.Empty;
                CopyMs = Math.Max(0L, copyMs);
                Reason = reason ?? string.Empty;
                ErrorReason = errorReason ?? string.Empty;
            }

            public bool Success { get; }
            public string ModelDirectory { get; }
            public long CopyMs { get; }
            public string Reason { get; }
            public string ErrorReason { get; }

            public static SherpaModelResolveResult Successful(string modelDirectory, long copyMs, string reason)
            {
                return new SherpaModelResolveResult(true, modelDirectory, copyMs, reason, string.Empty);
            }

            public static SherpaModelResolveResult Failed(string errorReason, long copyMs)
            {
                return new SherpaModelResolveResult(false, string.Empty, copyMs, string.Empty, errorReason);
            }
        }

        private readonly struct RuntimeProbeResult
        {
            private RuntimeProbeResult(
                bool modelCanReachRuntime,
                string reason,
                string error,
                string runtimePath,
                string runtimeFilesReport,
                string bindingTypeName,
                string architectureAbi)
            {
                ModelCanReachRuntime = modelCanReachRuntime;
                Reason = reason ?? string.Empty;
                Error = error ?? string.Empty;
                RuntimePath = runtimePath ?? string.Empty;
                RuntimeFilesReport = runtimeFilesReport ?? string.Empty;
                BindingTypeName = bindingTypeName ?? string.Empty;
                ArchitectureAbi = architectureAbi ?? string.Empty;
            }

            public bool ModelCanReachRuntime { get; }
            public string Reason { get; }
            public string Error { get; }
            public string RuntimePath { get; }
            public string RuntimeFilesReport { get; }
            public string BindingTypeName { get; }
            public string ArchitectureAbi { get; }

            public static RuntimeProbeResult NotEvaluated(string architectureAbi)
            {
                return new RuntimeProbeResult(
                    false,
                    "runtime_not_evaluated",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    architectureAbi);
            }

            public static RuntimeProbeResult Missing(
                string runtimePath,
                string runtimeFilesReport,
                string bindingTypeName,
                string architectureAbi,
                string reason,
                string error)
            {
                return new RuntimeProbeResult(
                    false,
                    reason,
                    error,
                    runtimePath,
                    runtimeFilesReport,
                    bindingTypeName,
                    architectureAbi);
            }

            public static RuntimeProbeResult Ready(
                string runtimePath,
                string runtimeFilesReport,
                string bindingTypeName,
                string architectureAbi,
                string reason)
            {
                return new RuntimeProbeResult(
                    true,
                    reason,
                    string.Empty,
                    runtimePath,
                    runtimeFilesReport,
                    bindingTypeName,
                    architectureAbi);
            }
        }

        private readonly struct AndroidBridgeInspection
        {
            private AndroidBridgeInspection(
                bool hasArchive,
                bool hasExpectedClass,
                string archivePath,
                string details,
                bool hasEmbeddedJniLibrary,
                long embeddedJniSize,
                string embeddedJniSha256,
                bool hasEmbeddedOnnxRuntime,
                long embeddedOnnxRuntimeSize,
                string embeddedOnnxRuntimeSha256)
            {
                HasArchive = hasArchive;
                HasExpectedClass = hasExpectedClass;
                ArchivePath = archivePath ?? string.Empty;
                Details = details ?? string.Empty;
                HasEmbeddedJniLibrary = hasEmbeddedJniLibrary;
                EmbeddedJniSize = Math.Max(0L, embeddedJniSize);
                EmbeddedJniSha256 = embeddedJniSha256 ?? string.Empty;
                HasEmbeddedOnnxRuntime = hasEmbeddedOnnxRuntime;
                EmbeddedOnnxRuntimeSize = Math.Max(0L, embeddedOnnxRuntimeSize);
                EmbeddedOnnxRuntimeSha256 = embeddedOnnxRuntimeSha256 ?? string.Empty;
            }

            public bool HasArchive { get; }
            public bool HasExpectedClass { get; }
            public string ArchivePath { get; }
            public string Details { get; }
            public bool HasEmbeddedJniLibrary { get; }
            public long EmbeddedJniSize { get; }
            public string EmbeddedJniSha256 { get; }
            public bool HasEmbeddedOnnxRuntime { get; }
            public long EmbeddedOnnxRuntimeSize { get; }
            public string EmbeddedOnnxRuntimeSha256 { get; }

            public static AndroidBridgeInspection Missing()
            {
                return new AndroidBridgeInspection(false, false, string.Empty, string.Empty, false, 0L, string.Empty, false, 0L, string.Empty);
            }

            public static AndroidBridgeInspection Invalid(
                string archivePath,
                string details,
                bool hasEmbeddedJniLibrary,
                long embeddedJniSize,
                string embeddedJniSha256,
                bool hasEmbeddedOnnxRuntime,
                long embeddedOnnxRuntimeSize,
                string embeddedOnnxRuntimeSha256)
            {
                return new AndroidBridgeInspection(
                    true,
                    false,
                    archivePath,
                    details,
                    hasEmbeddedJniLibrary,
                    embeddedJniSize,
                    embeddedJniSha256,
                    hasEmbeddedOnnxRuntime,
                    embeddedOnnxRuntimeSize,
                    embeddedOnnxRuntimeSha256);
            }

            public static AndroidBridgeInspection Valid(
                string archivePath,
                string details,
                bool hasEmbeddedJniLibrary,
                long embeddedJniSize,
                string embeddedJniSha256,
                bool hasEmbeddedOnnxRuntime,
                long embeddedOnnxRuntimeSize,
                string embeddedOnnxRuntimeSha256)
            {
                return new AndroidBridgeInspection(
                    true,
                    true,
                    archivePath,
                    details,
                    hasEmbeddedJniLibrary,
                    embeddedJniSize,
                    embeddedJniSha256,
                    hasEmbeddedOnnxRuntime,
                    embeddedOnnxRuntimeSize,
                    embeddedOnnxRuntimeSha256);
            }
        }
    }

    public readonly struct SherpaVoicePipelinePreparationResult
    {
        private SherpaVoicePipelinePreparationResult(
            bool success,
            string errorReason,
            long modelLoadMs,
            long recognizerCreateMs,
            long totalMs,
            string modelDirectory,
            bool recognizerReused)
        {
            Success = success;
            ErrorReason = errorReason ?? string.Empty;
            ModelLoadMs = Math.Max(0L, modelLoadMs);
            RecognizerCreateMs = Math.Max(0L, recognizerCreateMs);
            TotalMs = Math.Max(0L, totalMs);
            ModelDirectory = modelDirectory ?? string.Empty;
            RecognizerReused = recognizerReused;
        }

        public bool Success { get; }
        public string ErrorReason { get; }
        public long ModelLoadMs { get; }
        public long RecognizerCreateMs { get; }
        public long TotalMs { get; }
        public string ModelDirectory { get; }
        public bool RecognizerReused { get; }

        public static SherpaVoicePipelinePreparationResult Successful(
            long modelLoadMs,
            long recognizerCreateMs,
            long totalMs,
            string modelDirectory,
            bool recognizerReused)
        {
            return new SherpaVoicePipelinePreparationResult(
                true,
                string.Empty,
                modelLoadMs,
                recognizerCreateMs,
                totalMs,
                modelDirectory,
                recognizerReused);
        }

        public static SherpaVoicePipelinePreparationResult Failed(
            string errorReason,
            long modelLoadMs,
            long recognizerCreateMs,
            long totalMs,
            string modelDirectory)
        {
            return new SherpaVoicePipelinePreparationResult(
                false,
                errorReason,
                modelLoadMs,
                recognizerCreateMs,
                totalMs,
                modelDirectory,
                false);
        }
    }
}
