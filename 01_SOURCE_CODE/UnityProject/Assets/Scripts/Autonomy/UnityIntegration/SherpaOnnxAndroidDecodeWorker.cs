using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Autonomy.UnityIntegration
{
    public sealed class SherpaOnnxAndroidDecodeWorker : IDisposable
    {
        private const string DecodeThreadMode = "android_dedicated_worker";

        private readonly SherpaOnnxBackendOptions _options;
        private readonly BlockingCollection<DecodeWorkItem> _queue = new();
        private readonly ConcurrentQueue<SherpaOnnxAndroidTraceEvent> _traceEvents = new();
        private readonly object _lifecycleLock = new();
        private Thread _thread;
        private volatile bool _disposed;
        private volatile bool _started;
        private volatile bool _jniAttached;
        private volatile string _fatalError = string.Empty;
        private volatile string _activeModelDirectory = string.Empty;

#if UNITY_ANDROID && !UNITY_EDITOR
        private UnityEngine.AndroidJavaObject _recognizer;
#endif

        public SherpaOnnxAndroidDecodeWorker(SherpaOnnxBackendOptions options)
        {
            _options = options ?? new SherpaOnnxBackendOptions();
            _started = false;
            _jniAttached = false;
        }

        public bool IsRecognizerReadyForModel(string modelDirectory)
        {
            return _started &&
                !_disposed &&
                string.IsNullOrWhiteSpace(_fatalError) &&
                !string.IsNullOrWhiteSpace(modelDirectory) &&
                string.Equals(_activeModelDirectory, modelDirectory, StringComparison.OrdinalIgnoreCase);
        }

        public IReadOnlyList<SherpaOnnxAndroidTraceEvent> DrainTraceEvents()
        {
            List<SherpaOnnxAndroidTraceEvent> events = new();
            while (_traceEvents.TryDequeue(out SherpaOnnxAndroidTraceEvent traceEvent))
            {
                events.Add(traceEvent);
            }

            return events;
        }

        public Task<SherpaOnnxAndroidTranscriptionResult> TranscribeAsync(
            string modelDirectory,
            float[] monoSamples,
            int sampleRate,
            long audioDurationMs,
            int timeoutMs)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_disposed)
            {
                return Task.FromResult(SherpaOnnxAndroidTranscriptionResult.Failed(
                    "dedicated_worker_disposed",
                    0L,
                    0L,
                    0L));
            }

            if (!EnsureStarted())
            {
                return Task.FromResult(SherpaOnnxAndroidTranscriptionResult.Failed(
                    string.IsNullOrWhiteSpace(_fatalError) ? "dedicated_worker_start_failed" : _fatalError,
                    0L,
                    0L,
                    0L));
            }

            if (!string.IsNullOrWhiteSpace(_fatalError))
            {
                return Task.FromResult(SherpaOnnxAndroidTranscriptionResult.Failed(
                    _fatalError,
                    0L,
                    0L,
                    0L,
                    recognizerCreated: false,
                    jniThreadAttached: _jniAttached));
            }

            TaskCompletionSource<SherpaOnnxAndroidTranscriptionResult> completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            DecodeWorkItem item = new(
                modelDirectory,
                monoSamples,
                sampleRate,
                audioDurationMs,
                Stopwatch.GetTimestamp(),
                completion,
                prepareOnly: false);

            try
            {
                _queue.Add(item);
            }
            catch (InvalidOperationException)
            {
                return Task.FromResult(SherpaOnnxAndroidTranscriptionResult.Failed(
                    "dedicated_worker_queue_closed",
                    0L,
                    0L,
                    0L,
                    recognizerCreated: false,
                    jniThreadAttached: _jniAttached));
            }

            return AwaitWithTimeoutAsync(item, Math.Max(1000, timeoutMs));
#else
            return Task.FromResult(SherpaOnnxAndroidTranscriptionResult.Failed(
                "android_runtime_not_available: dedicated Sherpa ONNX worker is only executable on Android player",
                0L,
                0L,
                0L));
#endif
        }

        public Task<SherpaOnnxAndroidTranscriptionResult> PrepareRecognizerAsync(
            string modelDirectory,
            int timeoutMs)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_disposed)
            {
                return Task.FromResult(SherpaOnnxAndroidTranscriptionResult.Failed(
                    "dedicated_worker_disposed",
                    0L,
                    0L,
                    0L));
            }

            if (!EnsureStarted())
            {
                return Task.FromResult(SherpaOnnxAndroidTranscriptionResult.Failed(
                    string.IsNullOrWhiteSpace(_fatalError) ? "dedicated_worker_start_failed" : _fatalError,
                    0L,
                    0L,
                    0L));
            }

            TaskCompletionSource<SherpaOnnxAndroidTranscriptionResult> completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            DecodeWorkItem item = new(
                modelDirectory,
                Array.Empty<float>(),
                16000,
                0L,
                Stopwatch.GetTimestamp(),
                completion,
                prepareOnly: true);

            try
            {
                _queue.Add(item);
            }
            catch (InvalidOperationException)
            {
                return Task.FromResult(SherpaOnnxAndroidTranscriptionResult.Failed(
                    "dedicated_worker_queue_closed",
                    0L,
                    0L,
                    0L));
            }

            return AwaitWithTimeoutAsync(item, Math.Max(1000, timeoutMs));
#else
            return Task.FromResult(SherpaOnnxAndroidTranscriptionResult.Successful(
                string.Empty,
                0L,
                0L,
                0L));
#endif
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _queue.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
                // Best effort shutdown.
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                if (_thread != null && _thread.IsAlive)
                {
                    _thread.Join(1000);
                }
            }
            catch (Exception)
            {
                // The worker is a background thread; never block app teardown indefinitely.
            }
#endif

            _queue.Dispose();
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private bool EnsureStarted()
        {
            if (_started)
            {
                return true;
            }

            lock (_lifecycleLock)
            {
                if (_started)
                {
                    return true;
                }

                try
                {
                    _thread = new Thread(ThreadMain)
                    {
                        IsBackground = true,
                        Name = "SherpaOnnxAndroidDecodeWorker"
                    };
                    _thread.Start();
                    _started = true;
                    return true;
                }
                catch (Exception ex)
                {
                    _fatalError = "dedicated_worker_start_failed: " + ex.GetBaseException().Message;
                    return false;
                }
            }
        }

        private async Task<SherpaOnnxAndroidTranscriptionResult> AwaitWithTimeoutAsync(
            DecodeWorkItem item,
            int timeoutMs)
        {
            Task completed = await Task.WhenAny(item.Completion.Task, Task.Delay(timeoutMs));
            if (completed == item.Completion.Task)
            {
                return await item.Completion.Task;
            }

            long queueWaitMs = ElapsedMilliseconds(item.EnqueuedTimestamp);
            return SherpaOnnxAndroidTranscriptionResult.Failed(
                $"dedicated_worker_timeout: timeout_ms={timeoutMs}",
                0L,
                0L,
                queueWaitMs,
                recognizerCreated: false,
                jniThreadAttached: _jniAttached,
                queueWaitMs: queueWaitMs);
        }

        private void ThreadMain()
        {
            EnqueueTrace("p45e05_sherpa_dedicated_worker_started", BasePayload());
            try
            {
                UnityEngine.AndroidJNI.AttachCurrentThread();
                _jniAttached = true;
                EnqueueTrace("p45e05_sherpa_dedicated_worker_jni_attached", BasePayload());
            }
            catch (Exception ex)
            {
                _fatalError = "dedicated_worker_jni_attach_failed: " + ex.GetBaseException().Message;
                EnqueueTrace("p45e05_sherpa_dedicated_worker_decode_failed", BasePayload(_fatalError));
            }

            try
            {
                foreach (DecodeWorkItem item in _queue.GetConsumingEnumerable())
                {
                    if (!string.IsNullOrWhiteSpace(_fatalError))
                    {
                        item.Completion.TrySetResult(SherpaOnnxAndroidTranscriptionResult.Failed(
                            _fatalError,
                            0L,
                            0L,
                            ElapsedMilliseconds(item.EnqueuedTimestamp),
                            recognizerCreated: false,
                            jniThreadAttached: _jniAttached,
                            queueWaitMs: ElapsedMilliseconds(item.EnqueuedTimestamp)));
                        continue;
                    }

                    Process(item);
                }
            }
            finally
            {
                DisposeRecognizerOnly();
                if (_jniAttached)
                {
                    try
                    {
                        UnityEngine.AndroidJNI.DetachCurrentThread();
                    }
                    catch (Exception)
                    {
                        // Best effort detach from the dedicated decode thread.
                    }

                    _jniAttached = false;
                    EnqueueTrace("p45e05_sherpa_dedicated_worker_jni_detached", BasePayload());
                }
            }
        }

        private void Process(DecodeWorkItem item)
        {
            long queueWaitMs = ElapsedMilliseconds(item.EnqueuedTimestamp);
            Stopwatch totalStopwatch = Stopwatch.StartNew();
            long recognizerCreateMs = 0L;
            long createStreamMs = 0L;
            long acceptWaveformMs = 0L;
            long decodeCallMs = 0L;
            long getResultMs = 0L;
            bool recognizerCreated = false;

            Dictionary<string, object> startedPayload = BasePayload();
            startedPayload["queue_wait_ms"] = queueWaitMs;
            startedPayload["sample_rate"] = item.SampleRate;
            startedPayload["audio_duration_ms"] = Math.Max(0L, item.AudioDurationMs);
            startedPayload["persistent_model_path"] = item.ModelDirectory;
            EnqueueTrace("p45e05_sherpa_dedicated_worker_decode_started", startedPayload);

            try
            {
                if (!EnsureRecognizer(item.ModelDirectory, out recognizerCreateMs, out recognizerCreated, out string createError))
                {
                    totalStopwatch.Stop();
                    item.Completion.TrySetResult(SherpaOnnxAndroidTranscriptionResult.Failed(
                        createError,
                        recognizerCreateMs,
                        0L,
                        queueWaitMs + totalStopwatch.ElapsedMilliseconds,
                        recognizerCreated,
                        _jniAttached,
                        queueWaitMs: queueWaitMs));
                    EnqueueTrace("p45e05_sherpa_dedicated_worker_decode_failed", BasePayload(createError));
                    return;
                }

                EnqueueTrace(
                    recognizerCreated
                        ? "p45e05_sherpa_worker_recognizer_created"
                        : "p45e05_sherpa_worker_recognizer_reused",
                    BuildStepPayload(item, queueWaitMs, recognizerCreateMs, 0L, 0L, 0L, 0L, string.Empty, recognizerCreated));

                if (item.PrepareOnly)
                {
                    totalStopwatch.Stop();
                    EnqueueTrace(
                        "p46m01_sherpa_worker_recognizer_prepared",
                        BuildStepPayload(item, queueWaitMs, recognizerCreateMs, 0L, 0L, 0L, 0L, string.Empty, recognizerCreated));
                    item.Completion.TrySetResult(SherpaOnnxAndroidTranscriptionResult.Successful(
                        string.Empty,
                        recognizerCreateMs,
                        0L,
                        queueWaitMs + totalStopwatch.ElapsedMilliseconds,
                        recognizerCreated,
                        _jniAttached,
                        queueWaitMs));
                    return;
                }

                Stopwatch inferenceStopwatch = Stopwatch.StartNew();
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
                    EnqueueTrace(
                        "p45e05_sherpa_worker_create_stream_completed",
                        BuildStepPayload(item, queueWaitMs, recognizerCreateMs, createStreamMs, acceptWaveformMs, decodeCallMs, getResultMs, string.Empty, recognizerCreated));

                    stepStopwatch.Restart();
                    stream.Call("acceptWaveform", item.MonoSamples, item.SampleRate);
                    stepStopwatch.Stop();
                    acceptWaveformMs = stepStopwatch.ElapsedMilliseconds;
                    EnqueueTrace(
                        "p45e05_sherpa_worker_accept_waveform_completed",
                        BuildStepPayload(item, queueWaitMs, recognizerCreateMs, createStreamMs, acceptWaveformMs, decodeCallMs, getResultMs, string.Empty, recognizerCreated));

                    stepStopwatch.Restart();
                    _recognizer.Call("decode", stream);
                    stepStopwatch.Stop();
                    decodeCallMs = stepStopwatch.ElapsedMilliseconds;
                    EnqueueTrace(
                        "p45e05_sherpa_worker_decode_call_completed",
                        BuildStepPayload(item, queueWaitMs, recognizerCreateMs, createStreamMs, acceptWaveformMs, decodeCallMs, getResultMs, string.Empty, recognizerCreated));

                    stepStopwatch.Restart();
                    using UnityEngine.AndroidJavaObject result = _recognizer.Call<UnityEngine.AndroidJavaObject>("getResult", stream);
                    string transcript = result == null ? string.Empty : result.Call<string>("getText");
                    stepStopwatch.Stop();
                    getResultMs = stepStopwatch.ElapsedMilliseconds;
                    inferenceStopwatch.Stop();
                    totalStopwatch.Stop();

                    EnqueueTrace(
                        "p45e05_sherpa_worker_get_result_completed",
                        BuildStepPayload(item, queueWaitMs, recognizerCreateMs, createStreamMs, acceptWaveformMs, decodeCallMs, getResultMs, transcript, recognizerCreated));
                    EnqueueTrace(
                        "p45e05_sherpa_dedicated_worker_decode_completed",
                        BuildStepPayload(item, queueWaitMs, recognizerCreateMs, createStreamMs, acceptWaveformMs, decodeCallMs, getResultMs, transcript, recognizerCreated));

                    item.Completion.TrySetResult(SherpaOnnxAndroidTranscriptionResult.Successful(
                        transcript,
                        recognizerCreateMs,
                        inferenceStopwatch.ElapsedMilliseconds,
                        queueWaitMs + totalStopwatch.ElapsedMilliseconds,
                        recognizerCreated,
                        _jniAttached,
                        queueWaitMs,
                        createStreamMs,
                        acceptWaveformMs,
                        decodeCallMs,
                        getResultMs));
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
                string error = ClassifyAndroidException("transcription_failed", ex);
                item.Completion.TrySetResult(SherpaOnnxAndroidTranscriptionResult.Failed(
                    error,
                    recognizerCreateMs,
                    0L,
                    queueWaitMs + totalStopwatch.ElapsedMilliseconds,
                    recognizerCreated,
                    _jniAttached,
                    queueWaitMs,
                    createStreamMs,
                    acceptWaveformMs,
                    decodeCallMs,
                    getResultMs));
                EnqueueTrace("p45e05_sherpa_dedicated_worker_decode_failed", BuildStepPayload(item, queueWaitMs, recognizerCreateMs, createStreamMs, acceptWaveformMs, decodeCallMs, getResultMs, string.Empty, recognizerCreated, error));
            }
            catch (Exception ex)
            {
                totalStopwatch.Stop();
                string error = "transcription_failed: " + ex.GetBaseException().Message;
                item.Completion.TrySetResult(SherpaOnnxAndroidTranscriptionResult.Failed(
                    error,
                    recognizerCreateMs,
                    0L,
                    queueWaitMs + totalStopwatch.ElapsedMilliseconds,
                    recognizerCreated,
                    _jniAttached,
                    queueWaitMs,
                    createStreamMs,
                    acceptWaveformMs,
                    decodeCallMs,
                    getResultMs));
                EnqueueTrace("p45e05_sherpa_dedicated_worker_decode_failed", BuildStepPayload(item, queueWaitMs, recognizerCreateMs, createStreamMs, acceptWaveformMs, decodeCallMs, getResultMs, string.Empty, recognizerCreated, error));
            }
        }

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
                message.IndexOf("NoClassDefFoundError", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("NoSuchMethod", StringComparison.OrdinalIgnoreCase) >= 0)
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

        private void EnqueueTrace(string eventName, Dictionary<string, object> payload)
        {
            _traceEvents.Enqueue(new SherpaOnnxAndroidTraceEvent(eventName, payload));
        }

        private Dictionary<string, object> BuildStepPayload(
            DecodeWorkItem item,
            long queueWaitMs,
            long recognizerCreateMs,
            long createStreamMs,
            long acceptWaveformMs,
            long decodeCallMs,
            long getResultMs,
            string transcript,
            bool recognizerCreated,
            string errorReason = "")
        {
            Dictionary<string, object> payload = BasePayload(errorReason);
            payload["model_path"] = item.ModelDirectory ?? string.Empty;
            payload["persistent_model_path"] = item.ModelDirectory ?? string.Empty;
            payload["sample_rate"] = item.SampleRate;
            payload["audio_duration_ms"] = Math.Max(0L, item.AudioDurationMs);
            payload["queue_wait_ms"] = Math.Max(0L, queueWaitMs);
            payload["recognizer_create_ms"] = Math.Max(0L, recognizerCreateMs);
            payload["recognizer_created_this_call"] = recognizerCreated;
            payload["recognizer_reused"] = !recognizerCreated && string.IsNullOrWhiteSpace(errorReason);
            payload["create_stream_ms"] = Math.Max(0L, createStreamMs);
            payload["accept_waveform_ms"] = Math.Max(0L, acceptWaveformMs);
            payload["decode_ms"] = Math.Max(0L, decodeCallMs);
            payload["get_result_ms"] = Math.Max(0L, getResultMs);
            payload["worker_decode_ms"] = Math.Max(0L, acceptWaveformMs + decodeCallMs + getResultMs);
            payload["transcript"] = transcript ?? string.Empty;
            return payload;
        }

        private Dictionary<string, object> BasePayload(string errorReason = "")
        {
            return new Dictionary<string, object>
            {
                ["backend_requested"] = "SherpaOnnx",
                ["backend_effective"] = "SherpaOnnx",
                ["model_name"] = _options.ModelName,
                ["model_layout"] = $"{_options.EncoderFileName}+{_options.DecoderFileName}+{_options.JoinerFileName}+{_options.TokensFileName}",
                ["runtime_source"] = "embedded_in_aar",
                ["bridge_class"] = _options.AndroidRecognizerClassName,
                ["architecture_abi"] = _options.AndroidAbi,
                ["platform"] = "Android",
                ["decode_thread_mode"] = DecodeThreadMode,
                ["worker_thread_used"] = true,
                ["jni_thread_attached"] = _jniAttached,
                ["hotwords_enabled"] = _options.EnableSherpaHotwords,
                ["fallback_used"] = false,
                ["error_reason"] = errorReason ?? string.Empty
            };
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

        private static long ElapsedMilliseconds(long startTimestamp)
        {
            long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
            return Math.Max(0L, (long)(elapsed * 1000.0 / Stopwatch.Frequency));
        }

        private sealed class DecodeWorkItem
        {
            public DecodeWorkItem(
                string modelDirectory,
                float[] monoSamples,
                int sampleRate,
                long audioDurationMs,
                long enqueuedTimestamp,
                TaskCompletionSource<SherpaOnnxAndroidTranscriptionResult> completion,
                bool prepareOnly)
            {
                ModelDirectory = modelDirectory ?? string.Empty;
                MonoSamples = monoSamples ?? Array.Empty<float>();
                SampleRate = sampleRate;
                AudioDurationMs = audioDurationMs;
                EnqueuedTimestamp = enqueuedTimestamp;
                Completion = completion;
                PrepareOnly = prepareOnly;
            }

            public string ModelDirectory { get; }
            public float[] MonoSamples { get; }
            public int SampleRate { get; }
            public long AudioDurationMs { get; }
            public long EnqueuedTimestamp { get; }
            public TaskCompletionSource<SherpaOnnxAndroidTranscriptionResult> Completion { get; }
            public bool PrepareOnly { get; }
        }
    }

    public readonly struct SherpaOnnxAndroidTraceEvent
    {
        public SherpaOnnxAndroidTraceEvent(string eventName, Dictionary<string, object> payload)
        {
            EventName = eventName ?? string.Empty;
            Payload = payload ?? new Dictionary<string, object>();
        }

        public string EventName { get; }
        public Dictionary<string, object> Payload { get; }
    }
}
