using System;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    [Serializable]
    public sealed class SherpaOnnxBackendOptions
    {
        public const string DefaultStreamingAssetsRoot = "SherpaOnnx";
        public const string DefaultModelName = "spanish_or_multilingual_model";
        public const string QuestOperationalModelName = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";
        public const string DefaultEncoderFileName = "encoder.int8.onnx";
        public const string DefaultDecoderFileName = "decoder.int8.onnx";
        public const string DefaultJoinerFileName = "joiner.int8.onnx";
        public const string DefaultTokensFileName = "tokens.txt";
        public const string DefaultAndroidPluginRelativePath = "Plugins/Android";
        public const string DefaultAndroidAbi = "arm64-v8a";
        public const string DefaultAndroidJniLibraryFileName = "libsherpa-onnx-jni.so";
        public const string DefaultOnnxRuntimeLibraryFileName = "libonnxruntime.so";
        public const string DefaultAndroidRecognizerClassName = "com.k2fsa.sherpa.onnx.OfflineRecognizer";
        public const string DefaultPersistentDataRelativeRoot = "SherpaOnnx";
        public const int DefaultRecognizerNumThreads = 2;
        public const bool DefaultUseAndroidDedicatedDecodeThread = true;
        public const bool DefaultUseAndroidWorkerThreadForDecode = false;
        public const bool DefaultEnableSherpaHotwords = false;
        public const bool DefaultFallbackToMainThreadStableOnWorkerFailure = false;
        public const int DefaultAndroidDecodeTimeoutMs = 12000;

        [SerializeField] private string _streamingAssetsRelativeRoot = DefaultStreamingAssetsRoot;
        [SerializeField] private string _modelName = DefaultModelName;
        [SerializeField] private string _encoderFileName = DefaultEncoderFileName;
        [SerializeField] private string _decoderFileName = DefaultDecoderFileName;
        [SerializeField] private string _joinerFileName = DefaultJoinerFileName;
        [SerializeField] private string _tokensFileName = DefaultTokensFileName;
        [SerializeField] private string _androidPluginRelativePath = DefaultAndroidPluginRelativePath;
        [SerializeField] private string _androidAbi = DefaultAndroidAbi;
        [SerializeField] private string _androidJniLibraryFileName = DefaultAndroidJniLibraryFileName;
        [SerializeField] private string _onnxRuntimeLibraryFileName = DefaultOnnxRuntimeLibraryFileName;
        [SerializeField] private string _androidRecognizerClassName = DefaultAndroidRecognizerClassName;
        [SerializeField] private string _persistentDataRelativeRoot = DefaultPersistentDataRelativeRoot;
        [SerializeField] private int _recognizerNumThreads = DefaultRecognizerNumThreads;
        [SerializeField] private bool _useAndroidDedicatedDecodeThread = DefaultUseAndroidDedicatedDecodeThread;
        [SerializeField] private bool _useAndroidWorkerThreadForDecode = DefaultUseAndroidWorkerThreadForDecode;
        [SerializeField] private bool _enableSherpaHotwords = DefaultEnableSherpaHotwords;
        [SerializeField] private bool _fallbackToMainThreadStableOnWorkerFailure = DefaultFallbackToMainThreadStableOnWorkerFailure;
        [SerializeField] private int _androidDecodeTimeoutMs = DefaultAndroidDecodeTimeoutMs;
        [SerializeField] private bool _requireSpanishOrMultilingualModel = true;
        [SerializeField] private string[] _hotwords =
        {
            "para",
            "para el robot",
            "detente",
            "detente ahora",
            "deten la tarea",
            "deten la tarea actual",
            "cancela la tarea actual",
            "retoma",
            "retoma la tarea",
            "reanuda",
            "reanuda la tarea",
            "A1",
            "A2",
            "B1",
            "B2",
            "C1",
            "caja",
            "zona"
        };

        public SherpaOnnxBackendOptions()
        {
        }

        public SherpaOnnxBackendOptions(string modelName)
        {
            _modelName = string.IsNullOrWhiteSpace(modelName) ? DefaultModelName : modelName.Trim();
        }

        public static SherpaOnnxBackendOptions CreateQuestOperationalDefaults()
        {
            return new SherpaOnnxBackendOptions(QuestOperationalModelName);
        }

        public string StreamingAssetsRelativeRoot => string.IsNullOrWhiteSpace(_streamingAssetsRelativeRoot)
            ? DefaultStreamingAssetsRoot
            : _streamingAssetsRelativeRoot.Trim();

        public string ModelName => string.IsNullOrWhiteSpace(_modelName)
            ? DefaultModelName
            : _modelName.Trim();

        public string EncoderFileName => string.IsNullOrWhiteSpace(_encoderFileName)
            ? DefaultEncoderFileName
            : _encoderFileName.Trim();

        public string DecoderFileName => string.IsNullOrWhiteSpace(_decoderFileName)
            ? DefaultDecoderFileName
            : _decoderFileName.Trim();

        public string JoinerFileName => string.IsNullOrWhiteSpace(_joinerFileName)
            ? DefaultJoinerFileName
            : _joinerFileName.Trim();

        public string TokensFileName => string.IsNullOrWhiteSpace(_tokensFileName)
            ? DefaultTokensFileName
            : _tokensFileName.Trim();

        public string AndroidPluginRelativePath => string.IsNullOrWhiteSpace(_androidPluginRelativePath)
            ? DefaultAndroidPluginRelativePath
            : _androidPluginRelativePath.Trim();

        public string AndroidAbi => string.IsNullOrWhiteSpace(_androidAbi)
            ? DefaultAndroidAbi
            : _androidAbi.Trim();

        public string AndroidJniLibraryFileName => string.IsNullOrWhiteSpace(_androidJniLibraryFileName)
            ? DefaultAndroidJniLibraryFileName
            : _androidJniLibraryFileName.Trim();

        public string OnnxRuntimeLibraryFileName => string.IsNullOrWhiteSpace(_onnxRuntimeLibraryFileName)
            ? DefaultOnnxRuntimeLibraryFileName
            : _onnxRuntimeLibraryFileName.Trim();

        public string AndroidRecognizerClassName => string.IsNullOrWhiteSpace(_androidRecognizerClassName)
            ? DefaultAndroidRecognizerClassName
            : _androidRecognizerClassName.Trim();

        public string PersistentDataRelativeRoot => string.IsNullOrWhiteSpace(_persistentDataRelativeRoot)
            ? DefaultPersistentDataRelativeRoot
            : _persistentDataRelativeRoot.Trim();

        public int RecognizerNumThreads => Math.Max(1, _recognizerNumThreads);

        public bool UseAndroidDedicatedDecodeThread => _useAndroidDedicatedDecodeThread;
        public bool UseAndroidWorkerThreadForDecode => _useAndroidWorkerThreadForDecode;
        public bool EnableSherpaHotwords => _enableSherpaHotwords;
        public bool FallbackToMainThreadStableOnWorkerFailure => _fallbackToMainThreadStableOnWorkerFailure;
        public int AndroidDecodeTimeoutMs => Math.Max(1000, _androidDecodeTimeoutMs);
        public bool RequireSpanishOrMultilingualModel => _requireSpanishOrMultilingualModel;
        public string[] Hotwords => _hotwords ?? Array.Empty<string>();

        public string BuildStreamingAssetsRelativeModelPath()
        {
            return $"{StreamingAssetsRelativeRoot}/{ModelName}";
        }
    }
}
