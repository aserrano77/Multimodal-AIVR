using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Autonomy.UnityIntegration
{
    public sealed class SherpaOnnxAndroidRecognizerBridge : IDisposable
    {
        private readonly SherpaOnnxBackendOptions _options;
        private readonly object _recognizerLock = new();
        private string _activeModelDirectory = string.Empty;
        private bool _disposed;

#if UNITY_ANDROID && !UNITY_EDITOR
        private UnityEngine.AndroidJavaObject _recognizer;
#endif

        public SherpaOnnxAndroidRecognizerBridge(SherpaOnnxBackendOptions options)
        {
            _options = options ?? new SherpaOnnxBackendOptions();
        }

        public bool IsRecognizerReadyForModel(string modelDirectory)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            lock (_recognizerLock)
            {
                return !_disposed &&
                    _recognizer != null &&
                    !string.IsNullOrWhiteSpace(modelDirectory) &&
                    string.Equals(_activeModelDirectory, modelDirectory, StringComparison.OrdinalIgnoreCase);
            }
#else
            return false;
#endif
        }

        public SherpaOnnxAndroidTranscriptionResult PrepareRecognizer(string modelDirectory)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_disposed)
            {
                return SherpaOnnxAndroidTranscriptionResult.Failed("android_bridge_disposed", 0L, 0L, 0L);
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            bool success = EnsureRecognizer(
                modelDirectory,
                out long recognizerCreateMs,
                out bool recognizerCreated,
                out string error);
            stopwatch.Stop();
            return success
                ? SherpaOnnxAndroidTranscriptionResult.Successful(
                    string.Empty,
                    recognizerCreateMs,
                    0L,
                    stopwatch.ElapsedMilliseconds,
                    recognizerCreated,
                    false)
                : SherpaOnnxAndroidTranscriptionResult.Failed(
                    error,
                    recognizerCreateMs,
                    0L,
                    stopwatch.ElapsedMilliseconds,
                    recognizerCreated,
                    false);
#else
            return SherpaOnnxAndroidTranscriptionResult.Successful(string.Empty, 0L, 0L, 0L);
#endif
        }

        public SherpaOnnxAndroidTranscriptionResult Transcribe(
            string modelDirectory,
            float[] monoSamples,
            int sampleRate,
            long audioDurationMs,
            bool attachCurrentThread = false,
            bool emitStepLogs = true,
            string decodeThreadMode = "main_thread_stable")
        {
            Stopwatch totalStopwatch = Stopwatch.StartNew();
            long recognizerCreateMs = 0L;
            long inferenceLatencyMs = 0L;

            if (_disposed)
            {
                return SherpaOnnxAndroidTranscriptionResult.Failed(
                    "recognizer_bridge_disposed",
                    recognizerCreateMs,
                    inferenceLatencyMs,
                    totalStopwatch.ElapsedMilliseconds);
            }

            if (string.IsNullOrWhiteSpace(modelDirectory))
            {
                return SherpaOnnxAndroidTranscriptionResult.Failed(
                    "model_path_invalid: empty model directory",
                    recognizerCreateMs,
                    inferenceLatencyMs,
                    totalStopwatch.ElapsedMilliseconds);
            }

            if (monoSamples == null || monoSamples.Length == 0)
            {
                return SherpaOnnxAndroidTranscriptionResult.Failed(
                    "unsupported_audio_format: empty waveform",
                    recognizerCreateMs,
                    inferenceLatencyMs,
                    totalStopwatch.ElapsedMilliseconds);
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            lock (_recognizerLock)
            {
                return TranscribeOnAndroidThread(
                    modelDirectory,
                    monoSamples,
                    sampleRate,
                    audioDurationMs,
                    totalStopwatch,
                    recognizerCreateMs,
                    inferenceLatencyMs,
                    recognizerCreated: false,
                    jniThreadAttached: false,
                    attachCurrentThread: attachCurrentThread,
                    emitStepLogs: emitStepLogs,
                    decodeThreadMode: decodeThreadMode);
            }
#else
            totalStopwatch.Stop();
            return SherpaOnnxAndroidTranscriptionResult.Failed(
                "android_runtime_not_available: Sherpa ONNX Android bridge is only executable on Android player",
                recognizerCreateMs,
                inferenceLatencyMs,
                totalStopwatch.ElapsedMilliseconds);
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private SherpaOnnxAndroidTranscriptionResult TranscribeOnAndroidThread(
            string modelDirectory,
            float[] monoSamples,
            int sampleRate,
            long audioDurationMs,
            Stopwatch totalStopwatch,
            long recognizerCreateMs,
            long inferenceLatencyMs,
            bool recognizerCreated,
            bool jniThreadAttached,
            bool attachCurrentThread,
            bool emitStepLogs,
            string decodeThreadMode)
        {
            try
            {
                if (emitStepLogs)
                {
                    LogDecodeStep(
                        "p45e05_sherpa_decode_enter",
                        modelDirectory,
                        sampleRate,
                        audioDurationMs,
                        recognizerCreateMs,
                        inferenceLatencyMs,
                        totalStopwatch.ElapsedMilliseconds,
                        decodeThreadMode,
                        attachCurrentThread,
                        jniThreadAttached);
                }

                if (attachCurrentThread)
                {
                    try
                    {
                        UnityEngine.AndroidJNI.AttachCurrentThread();
                        jniThreadAttached = true;
                    }
                    catch (Exception ex)
                    {
                        totalStopwatch.Stop();
                        return SherpaOnnxAndroidTranscriptionResult.Failed(
                            "jni_thread_attach_failed: " + ex.GetBaseException().Message,
                            recognizerCreateMs,
                            inferenceLatencyMs,
                            totalStopwatch.ElapsedMilliseconds,
                            recognizerCreated,
                            jniThreadAttached);
                    }
                }

                if (!EnsureRecognizer(modelDirectory, out recognizerCreateMs, out recognizerCreated, out string createError))
                {
                    return SherpaOnnxAndroidTranscriptionResult.Failed(
                        createError,
                        recognizerCreateMs,
                        inferenceLatencyMs,
                        totalStopwatch.ElapsedMilliseconds,
                        recognizerCreated,
                        jniThreadAttached);
                }

                Stopwatch inferenceStopwatch = Stopwatch.StartNew();
                long createStreamMs = 0L;
                long acceptWaveformMs = 0L;
                long decodeMs = 0L;
                long getResultMs = 0L;
                UnityEngine.AndroidJavaObject stream = null;
                try
                {
                    string hotwords = _options.EnableSherpaHotwords
                        ? BuildHotwordsString(_options.Hotwords)
                        : string.Empty;
                    Stopwatch stepStopwatch = Stopwatch.StartNew();
                    stream = string.IsNullOrWhiteSpace(hotwords)
                        ? _recognizer.Call<UnityEngine.AndroidJavaObject>("createStream")
                        : _recognizer.Call<UnityEngine.AndroidJavaObject>("createStream", hotwords);
                    stepStopwatch.Stop();
                    createStreamMs = stepStopwatch.ElapsedMilliseconds;
                    if (emitStepLogs)
                    {
                        LogDecodeStep(
                            "p45e05_sherpa_create_stream_completed",
                            modelDirectory,
                            sampleRate,
                            audioDurationMs,
                            recognizerCreateMs,
                            inferenceStopwatch.ElapsedMilliseconds,
                            totalStopwatch.ElapsedMilliseconds,
                            decodeThreadMode,
                            attachCurrentThread,
                            jniThreadAttached,
                            createStreamMs,
                            acceptWaveformMs,
                            decodeMs,
                            getResultMs);
                    }

                    stepStopwatch.Restart();
                    stream.Call("acceptWaveform", monoSamples, sampleRate);
                    stepStopwatch.Stop();
                    acceptWaveformMs = stepStopwatch.ElapsedMilliseconds;
                    if (emitStepLogs)
                    {
                        LogDecodeStep(
                            "p45e05_sherpa_accept_waveform_completed",
                            modelDirectory,
                            sampleRate,
                            audioDurationMs,
                            recognizerCreateMs,
                            inferenceStopwatch.ElapsedMilliseconds,
                            totalStopwatch.ElapsedMilliseconds,
                            decodeThreadMode,
                            attachCurrentThread,
                            jniThreadAttached,
                            createStreamMs,
                            acceptWaveformMs,
                            decodeMs,
                            getResultMs);
                    }

                    stepStopwatch.Restart();
                    _recognizer.Call("decode", stream);
                    stepStopwatch.Stop();
                    decodeMs = stepStopwatch.ElapsedMilliseconds;
                    if (emitStepLogs)
                    {
                        LogDecodeStep(
                            "p45e05_sherpa_decode_completed",
                            modelDirectory,
                            sampleRate,
                            audioDurationMs,
                            recognizerCreateMs,
                            inferenceStopwatch.ElapsedMilliseconds,
                            totalStopwatch.ElapsedMilliseconds,
                            decodeThreadMode,
                            attachCurrentThread,
                            jniThreadAttached,
                            createStreamMs,
                            acceptWaveformMs,
                            decodeMs,
                            getResultMs);
                    }

                    stepStopwatch.Restart();
                    using UnityEngine.AndroidJavaObject result = _recognizer.Call<UnityEngine.AndroidJavaObject>("getResult", stream);
                    string transcript = result == null ? string.Empty : result.Call<string>("getText");
                    stepStopwatch.Stop();
                    getResultMs = stepStopwatch.ElapsedMilliseconds;
                    if (emitStepLogs)
                    {
                        LogDecodeStep(
                            "p45e05_sherpa_get_result_completed",
                            modelDirectory,
                            sampleRate,
                            audioDurationMs,
                            recognizerCreateMs,
                            inferenceStopwatch.ElapsedMilliseconds,
                            totalStopwatch.ElapsedMilliseconds,
                            decodeThreadMode,
                            attachCurrentThread,
                            jniThreadAttached,
                            createStreamMs,
                            acceptWaveformMs,
                            decodeMs,
                            getResultMs);
                    }

                    inferenceStopwatch.Stop();
                    totalStopwatch.Stop();
                    return SherpaOnnxAndroidTranscriptionResult.Successful(
                        transcript,
                        recognizerCreateMs,
                        inferenceStopwatch.ElapsedMilliseconds,
                        totalStopwatch.ElapsedMilliseconds,
                        recognizerCreated,
                        jniThreadAttached,
                        0L,
                        createStreamMs,
                        acceptWaveformMs,
                        decodeMs,
                        getResultMs);
                }
                finally
                {
                    try
                    {
                        stream?.Call("release");
                    }
                    catch (Exception)
                    {
                        // Best effort cleanup after native decode.
                    }

                    stream?.Dispose();
                }
            }
            catch (UnityEngine.AndroidJavaException ex)
            {
                totalStopwatch.Stop();
                return SherpaOnnxAndroidTranscriptionResult.Failed(
                    ClassifyAndroidException("transcription_failed", ex),
                    recognizerCreateMs,
                    inferenceLatencyMs,
                    totalStopwatch.ElapsedMilliseconds,
                    recognizerCreated,
                    jniThreadAttached);
            }
            catch (Exception ex)
            {
                totalStopwatch.Stop();
                return SherpaOnnxAndroidTranscriptionResult.Failed(
                    "transcription_failed: " + ex.GetBaseException().Message,
                    recognizerCreateMs,
                    inferenceLatencyMs,
                    totalStopwatch.ElapsedMilliseconds,
                    recognizerCreated,
                    jniThreadAttached);
            }
            finally
            {
                if (attachCurrentThread && jniThreadAttached)
                {
                    try
                    {
                        UnityEngine.AndroidJNI.DetachCurrentThread();
                    }
                    catch (Exception)
                    {
                        // Best effort detach from the worker thread.
                    }
                }
            }
        }
#endif

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                lock (_recognizerLock)
                {
                    _recognizer?.Call("release");
                    _recognizer?.Dispose();
                    _recognizer = null;
                    _activeModelDirectory = string.Empty;
                }
            }
            catch (Exception)
            {
                // Best effort native resource release.
            }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private bool EnsureRecognizer(string modelDirectory, out long recognizerCreateMs, out bool recognizerCreated, out string error)
        {
            recognizerCreateMs = 0L;
            recognizerCreated = false;
            error = string.Empty;
            if (_recognizer != null &&
                string.Equals(_activeModelDirectory, modelDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                DisposeRecognizerOnly();

                string encoder = System.IO.Path.Combine(modelDirectory, _options.EncoderFileName);
                string decoder = System.IO.Path.Combine(modelDirectory, _options.DecoderFileName);
                string joiner = System.IO.Path.Combine(modelDirectory, _options.JoinerFileName);
                string tokens = System.IO.Path.Combine(modelDirectory, _options.TokensFileName);

                using UnityEngine.AndroidJavaObject featConfig = new("com.k2fsa.sherpa.onnx.FeatureConfig");
                featConfig.Call("setSampleRate", 16000);
                featConfig.Call("setFeatureDim", 80);
                featConfig.Call("setDither", 0.0f);

                using UnityEngine.AndroidJavaObject transducerConfig = new("com.k2fsa.sherpa.onnx.OfflineTransducerModelConfig");
                transducerConfig.Call("setEncoder", encoder);
                transducerConfig.Call("setDecoder", decoder);
                transducerConfig.Call("setJoiner", joiner);

                using UnityEngine.AndroidJavaObject modelConfig = new("com.k2fsa.sherpa.onnx.OfflineModelConfig");
                modelConfig.Call("setTransducer", transducerConfig);
                modelConfig.Call("setTokens", tokens);
                modelConfig.Call("setNumThreads", _options.RecognizerNumThreads);
                modelConfig.Call("setProvider", "cpu");
                modelConfig.Call("setDebug", false);

                using UnityEngine.AndroidJavaObject recognizerConfig = new("com.k2fsa.sherpa.onnx.OfflineRecognizerConfig");
                recognizerConfig.Call("setFeatConfig", featConfig);
                recognizerConfig.Call("setModelConfig", modelConfig);
                recognizerConfig.Call("setDecodingMethod", "greedy_search");
                recognizerConfig.Call("setMaxActivePaths", 4);

                _recognizer = new UnityEngine.AndroidJavaObject(
                    _options.AndroidRecognizerClassName,
                    new object[] { null, recognizerConfig });
                _activeModelDirectory = modelDirectory;
                stopwatch.Stop();
                recognizerCreateMs = stopwatch.ElapsedMilliseconds;
                recognizerCreated = true;
                return true;
            }
            catch (UnityEngine.AndroidJavaException ex)
            {
                stopwatch.Stop();
                recognizerCreateMs = stopwatch.ElapsedMilliseconds;
                DisposeRecognizerOnly();
                error = ClassifyAndroidException("recognizer_create_failed", ex);
                return false;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                recognizerCreateMs = stopwatch.ElapsedMilliseconds;
                DisposeRecognizerOnly();
                error = "recognizer_create_failed: " + ex.GetBaseException().Message;
                return false;
            }
        }

        private static string BuildHotwordsString(string[] hotwords)
        {
            if (hotwords == null || hotwords.Length == 0)
            {
                return string.Empty;
            }

            return string.Join(
                "\n",
                hotwords
                    .Where(word => !string.IsNullOrWhiteSpace(word))
                    .Select(word => word.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private void LogDecodeStep(
            string eventName,
            string modelDirectory,
            int sampleRate,
            long audioDurationMs,
            long recognizerCreateMs,
            long inferenceLatencyMs,
            long totalLatencyMs,
            string decodeThreadMode,
            bool attachCurrentThread,
            bool jniThreadAttached,
            long createStreamMs = 0L,
            long acceptWaveformMs = 0L,
            long decodeMs = 0L,
            long getResultMs = 0L)
        {
            try
            {
                TiagoExperimentTelemetry.LogEvent(
                    eventName,
                    new Dictionary<string, object>
                    {
                        ["backend_requested"] = "SherpaOnnx",
                        ["backend_effective"] = "SherpaOnnx",
                        ["model_name"] = _options.ModelName,
                        ["model_path"] = modelDirectory ?? string.Empty,
                        ["persistent_model_path"] = modelDirectory ?? string.Empty,
                        ["model_layout"] = $"{_options.EncoderFileName}+{_options.DecoderFileName}+{_options.JoinerFileName}+{_options.TokensFileName}",
                        ["runtime_source"] = "embedded_in_aar",
                        ["bridge_class"] = _options.AndroidRecognizerClassName,
                        ["architecture_abi"] = _options.AndroidAbi,
                        ["platform"] = "Android",
                        ["sample_rate"] = sampleRate,
                        ["audio_duration_ms"] = Math.Max(0L, audioDurationMs),
                        ["recognizer_create_ms"] = Math.Max(0L, recognizerCreateMs),
                        ["inference_latency_ms"] = Math.Max(0L, inferenceLatencyMs),
                        ["total_latency_ms"] = Math.Max(0L, totalLatencyMs),
                        ["create_stream_ms"] = Math.Max(0L, createStreamMs),
                        ["accept_waveform_ms"] = Math.Max(0L, acceptWaveformMs),
                        ["decode_ms"] = Math.Max(0L, decodeMs),
                        ["get_result_ms"] = Math.Max(0L, getResultMs),
                        ["decode_thread_mode"] = decodeThreadMode ?? string.Empty,
                        ["worker_thread_used"] = string.Equals(decodeThreadMode, "worker_experimental", StringComparison.OrdinalIgnoreCase),
                        ["attach_current_thread_requested"] = attachCurrentThread,
                        ["jni_thread_attached"] = jniThreadAttached,
                        ["hotwords_enabled"] = _options.EnableSherpaHotwords,
                        ["fallback_used"] = false
                    });
            }
            catch (Exception)
            {
                // Decode step logs are diagnostic only; never let logging affect ASR stability.
            }
        }

        private void DisposeRecognizerOnly()
        {
            try
            {
                _recognizer?.Call("release");
            }
            catch (Exception)
            {
                // Best effort cleanup before replacing recognizer.
            }

            _recognizer?.Dispose();
            _recognizer = null;
            _activeModelDirectory = string.Empty;
        }

        private static string ClassifyAndroidException(string fallbackReason, UnityEngine.AndroidJavaException ex)
        {
            string message = ex?.GetBaseException().Message ?? string.Empty;
            if (message.IndexOf("ClassNotFoundException", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("NoClassDefFoundError", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "api_signature_mismatch: " + message;
            }

            if (message.IndexOf("NoSuchMethod", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "api_signature_mismatch: " + message;
            }

            if (message.IndexOf("UnsatisfiedLinkError", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("dlopen", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "native_load_failed: " + message;
            }

            if (message.IndexOf("Failed to read", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("No such file", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "model_path_invalid: " + message;
            }

            return fallbackReason + ": " + message;
        }
#endif
    }

    public readonly struct SherpaOnnxAndroidTranscriptionResult
    {
        private SherpaOnnxAndroidTranscriptionResult(
            bool success,
            string transcript,
            string errorReason,
            long recognizerCreateMs,
            long inferenceLatencyMs,
            long totalLatencyMs,
            bool recognizerCreated,
            bool jniThreadAttached,
            long queueWaitMs,
            long createStreamMs,
            long acceptWaveformMs,
            long decodeMs,
            long getResultMs)
        {
            Success = success;
            Transcript = transcript ?? string.Empty;
            ErrorReason = errorReason ?? string.Empty;
            RecognizerCreateMs = Math.Max(0L, recognizerCreateMs);
            InferenceLatencyMs = Math.Max(0L, inferenceLatencyMs);
            TotalLatencyMs = Math.Max(0L, totalLatencyMs);
            RecognizerCreated = recognizerCreated;
            RecognizerReused = !recognizerCreated && string.IsNullOrWhiteSpace(errorReason);
            JniThreadAttached = jniThreadAttached;
            QueueWaitMs = Math.Max(0L, queueWaitMs);
            CreateStreamMs = Math.Max(0L, createStreamMs);
            AcceptWaveformMs = Math.Max(0L, acceptWaveformMs);
            DecodeMs = Math.Max(0L, decodeMs);
            GetResultMs = Math.Max(0L, getResultMs);
        }

        public bool Success { get; }
        public string Transcript { get; }
        public string ErrorReason { get; }
        public long RecognizerCreateMs { get; }
        public long InferenceLatencyMs { get; }
        public long TotalLatencyMs { get; }
        public bool RecognizerCreated { get; }
        public bool RecognizerReused { get; }
        public bool JniThreadAttached { get; }
        public long QueueWaitMs { get; }
        public long CreateStreamMs { get; }
        public long AcceptWaveformMs { get; }
        public long DecodeMs { get; }
        public long GetResultMs { get; }

        public static SherpaOnnxAndroidTranscriptionResult Successful(
            string transcript,
            long recognizerCreateMs,
            long inferenceLatencyMs,
            long totalLatencyMs,
            bool recognizerCreated = false,
            bool jniThreadAttached = false,
            long queueWaitMs = 0L,
            long createStreamMs = 0L,
            long acceptWaveformMs = 0L,
            long decodeMs = 0L,
            long getResultMs = 0L)
        {
            return new SherpaOnnxAndroidTranscriptionResult(
                true,
                transcript,
                string.Empty,
                recognizerCreateMs,
                inferenceLatencyMs,
                totalLatencyMs,
                recognizerCreated,
                jniThreadAttached,
                queueWaitMs,
                createStreamMs,
                acceptWaveformMs,
                decodeMs,
                getResultMs);
        }

        public static SherpaOnnxAndroidTranscriptionResult Failed(
            string errorReason,
            long recognizerCreateMs,
            long inferenceLatencyMs,
            long totalLatencyMs,
            bool recognizerCreated = false,
            bool jniThreadAttached = false,
            long queueWaitMs = 0L,
            long createStreamMs = 0L,
            long acceptWaveformMs = 0L,
            long decodeMs = 0L,
            long getResultMs = 0L)
        {
            return new SherpaOnnxAndroidTranscriptionResult(
                false,
                string.Empty,
                errorReason,
                recognizerCreateMs,
                inferenceLatencyMs,
                totalLatencyMs,
                recognizerCreated,
                jniThreadAttached,
                queueWaitMs,
                createStreamMs,
                acceptWaveformMs,
                decodeMs,
                getResultMs);
        }
    }
}
