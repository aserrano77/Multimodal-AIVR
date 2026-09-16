using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Autonomy.Domain;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

namespace Autonomy.UnityIntegration
{
    public readonly struct WhisperModelRuntimeInfo
    {
        public WhisperModelRuntimeInfo(
            ASRModelSize requestedModelSize,
            ASRModelSize effectiveModelSize,
            string effectiveModelFileName,
            string effectiveModelPath,
            bool modelAvailableAtStart,
            bool modelFallbackUsed,
            string modelOverrideFailedReason = "")
        {
            RequestedModelSize = requestedModelSize;
            EffectiveModelSize = effectiveModelSize;
            EffectiveModelFileName = effectiveModelFileName ?? string.Empty;
            EffectiveModelPath = effectiveModelPath ?? string.Empty;
            ModelAvailableAtStart = modelAvailableAtStart;
            ModelFallbackUsed = modelFallbackUsed;
            ModelOverrideFailedReason = modelOverrideFailedReason ?? string.Empty;
        }

        public ASRModelSize RequestedModelSize { get; }
        public ASRModelSize EffectiveModelSize { get; }
        public string EffectiveModelFileName { get; }
        public string EffectiveModelPath { get; }
        public bool ModelAvailableAtStart { get; }
        public bool ModelFallbackUsed { get; }
        public string ModelOverrideFailedReason { get; }
    }

    public readonly struct WhisperModelApplyResult
    {
        public WhisperModelApplyResult(
            ASRModelSize requestedModelSize,
            ASRModelSize effectiveModelSize,
            string requestedModelPath,
            string resolvedModelPath,
            string effectiveModelFileName,
            bool modelAvailableAtStart,
            bool modelFallbackUsed,
            string failedReason)
        {
            RequestedModelSize = requestedModelSize;
            EffectiveModelSize = effectiveModelSize;
            RequestedModelPath = requestedModelPath ?? string.Empty;
            ResolvedModelPath = resolvedModelPath ?? string.Empty;
            EffectiveModelFileName = effectiveModelFileName ?? string.Empty;
            ModelAvailableAtStart = modelAvailableAtStart;
            ModelFallbackUsed = modelFallbackUsed;
            FailedReason = failedReason ?? string.Empty;
        }

        public ASRModelSize RequestedModelSize { get; }
        public ASRModelSize EffectiveModelSize { get; }
        public string RequestedModelPath { get; }
        public string ResolvedModelPath { get; }
        public string EffectiveModelFileName { get; }
        public bool ModelAvailableAtStart { get; }
        public bool ModelFallbackUsed { get; }
        public string FailedReason { get; }
    }

    public readonly struct WhisperModelLoadState
    {
        public WhisperModelLoadState(
            bool managerResolved,
            bool isLoaded,
            bool isLoading,
            string modelPath,
            string reason)
        {
            ManagerResolved = managerResolved;
            IsLoaded = isLoaded;
            IsLoading = isLoading;
            ModelPath = modelPath ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        public bool ManagerResolved { get; }
        public bool IsLoaded { get; }
        public bool IsLoading { get; }
        public string ModelPath { get; }
        public string Reason { get; }
    }

    public readonly struct WhisperManagerPathState
    {
        public WhisperManagerPathState(
            string rawPersistentPath,
            string modelPathForManager,
            bool isModelPathInStreamingAssets,
            bool fileExists,
            long fileSize)
        {
            RawPersistentPath = rawPersistentPath ?? string.Empty;
            ModelPathForManager = modelPathForManager ?? string.Empty;
            IsModelPathInStreamingAssets = isModelPathInStreamingAssets;
            FileExists = fileExists;
            FileSize = fileSize;
        }

        public string RawPersistentPath { get; }
        public string ModelPathForManager { get; }
        public bool IsModelPathInStreamingAssets { get; }
        public bool FileExists { get; }
        public long FileSize { get; }
    }

    public static class WhisperModelConfiguration
    {
        public static string BuildStreamingAssetsModelPath(ASRModelSize modelSize)
        {
            return modelSize switch
            {
                ASRModelSize.Tiny => "Whisper/ggml-tiny.bin",
                ASRModelSize.Base => "Whisper/ggml-base.bin",
                ASRModelSize.Small => "Whisper/ggml-small.bin",
                _ => string.Empty
            };
        }

        public static string ResolveModelName(Object whisperManager)
        {
            string path = ReadModelPath(whisperManager);
            if (string.IsNullOrWhiteSpace(path))
            {
                return "unknown";
            }

            return Path.GetFileNameWithoutExtension(path);
        }

        public static ASRModelSize ResolveModelSize(Object whisperManager)
        {
            return InferModelSize(ReadModelPath(whisperManager));
        }

        public static string ResolveModelPath(Object whisperManager)
        {
            return ReadModelPath(whisperManager);
        }

        public static string ResolveModelFileName(Object whisperManager)
        {
            string path = ReadModelPath(whisperManager);
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
        }

        public static bool IsModelAvailable(ASRModelSize modelSize)
        {
            return TryResolveRuntimeModelPath(modelSize, copyIfNeeded: true, out RuntimeModelPathResolution resolution) &&
                resolution.FileExists;
        }

        public static WhisperModelRuntimeInfo ResolveRuntimeInfo(
            Object whisperManager,
            ASRModelSize requestedModelSize,
            bool diagnosticOverrideEnabled)
        {
            string modelPath = ResolveModelPath(whisperManager);
            string effectiveFileName = string.IsNullOrWhiteSpace(modelPath) ? string.Empty : Path.GetFileName(modelPath);
            ASRModelSize effectiveSize = InferModelSize(modelPath);
            if (effectiveSize == ASRModelSize.Unknown && diagnosticOverrideEnabled && requestedModelSize != ASRModelSize.Unknown)
            {
                effectiveSize = requestedModelSize;
                modelPath = BuildStreamingAssetsModelPath(requestedModelSize);
                effectiveFileName = string.IsNullOrWhiteSpace(modelPath) ? string.Empty : Path.GetFileName(modelPath);
            }

            bool available = effectiveSize != ASRModelSize.Unknown && IsModelAvailable(effectiveSize);
            bool fallbackUsed = diagnosticOverrideEnabled &&
                requestedModelSize != ASRModelSize.Unknown &&
                effectiveSize != ASRModelSize.Unknown &&
                effectiveSize != requestedModelSize;

            return new WhisperModelRuntimeInfo(
                requestedModelSize,
                effectiveSize,
                effectiveFileName,
                modelPath,
                available,
                fallbackUsed);
        }

        public static bool TryApplyModel(Object whisperManager, ASRModelSize modelSize, out string message)
        {
            bool applied = TryApplyModel(whisperManager, modelSize, out WhisperModelApplyResult result);
            message = applied
                ? $"model_configured: {result.RequestedModelPath}"
                : result.FailedReason;
            return applied;
        }

        public static bool TryApplyModel(
            Object whisperManager,
            ASRModelSize modelSize,
            out WhisperModelApplyResult result)
        {
            string relativePath = BuildStreamingAssetsModelPath(modelSize);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                result = FailedResult(modelSize, relativePath, "unsupported_model_size");
                return false;
            }

            if (!TryResolveRuntimeModelPath(modelSize, copyIfNeeded: true, out RuntimeModelPathResolution resolution) ||
                !resolution.FileExists)
            {
                string reason = "model_file_not_found: " + resolution.ResolvedRuntimePath;
                if (modelSize == ASRModelSize.Base)
                {
                    LogBaseMissing(modelSize, resolution, reason);
                }

                result = FailedResult(modelSize, relativePath, reason);
                return false;
            }

            object manager = ResolveWhisperManagerObject(whisperManager);
            if (manager == null)
            {
                result = FailedResult(modelSize, relativePath, "whisper_manager_not_assigned_or_not_found");
                return false;
            }

            Type type = manager.GetType();
            if (ReadBoolProperty(type, manager, "IsLoaded") || ReadBoolProperty(type, manager, "IsLoading"))
            {
                result = FailedResult(modelSize, relativePath, "model_already_loaded_or_loading");
                return false;
            }

            string modelPathForManager = resolution.ModelPathForManager;
            bool isPathInStreamingAssets = resolution.ModelPathInStreamingAssets;
            if (!TryWriteModelPath(manager, modelPathForManager, isPathInStreamingAssets, out string failureReason))
            {
                result = FailedResult(modelSize, relativePath, failureReason);
                return false;
            }

            string effectivePath = ReadModelPathFromObject(manager);
            ASRModelSize effectiveSize = InferModelSize(effectivePath);
            result = new WhisperModelApplyResult(
                modelSize,
                effectiveSize,
                relativePath,
                resolution.ResolvedRuntimePath,
                Path.GetFileName(effectivePath),
                resolution.FileExists,
                effectiveSize != ASRModelSize.Unknown && effectiveSize != modelSize,
                string.Empty);
            LogModelResolution(modelSize, effectiveSize, relativePath, resolution);
            LogManagerPathApplied(modelSize, effectiveSize, resolution, manager);
            return true;
        }

        public static WhisperManagerPathState ResolveManagerPathState(Object whisperManager)
        {
            string managerPath = ResolveModelPath(whisperManager);
            string rawPath = managerPath;
            if (ExperimentDataPathResolver.IsAndroidRuntime() && managerPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                rawPath = Uri.UnescapeDataString(new Uri(managerPath).LocalPath);
            }

            bool exists = !string.IsNullOrWhiteSpace(rawPath) && File.Exists(rawPath);
            long size = exists ? new FileInfo(rawPath).Length : 0L;
            object manager = ResolveWhisperManagerObject(whisperManager);
            bool streaming = manager != null && ReadBoolPropertyOrField(manager.GetType(), manager, "IsModelPathInStreamingAssets", "isModelPathInStreamingAssets");
            return new WhisperManagerPathState(rawPath, managerPath, streaming, exists, size);
        }

        public static WhisperModelLoadState ResolveLoadState(Object whisperManager)
        {
            object manager = ResolveWhisperManagerObject(whisperManager);
            if (manager == null)
            {
                return new WhisperModelLoadState(false, false, false, string.Empty, "whisper_manager_not_assigned_or_not_found");
            }

            Type type = manager.GetType();
            bool loaded = ReadBoolProperty(type, manager, "IsLoaded");
            bool loading = ReadBoolProperty(type, manager, "IsLoading");
            string modelPath = ReadModelPathFromObject(manager);
            string reason = loaded
                ? "loaded"
                : loading
                    ? "loading"
                    : "not_loaded";
            return new WhisperModelLoadState(true, loaded, loading, modelPath, reason);
        }

        public static bool TryEnsureModelLoading(Object whisperManager, out string reason)
        {
            object manager = ResolveWhisperManagerObject(whisperManager);
            if (manager == null)
            {
                reason = "whisper_manager_not_assigned_or_not_found";
                return false;
            }

            Type type = manager.GetType();
            if (ReadBoolProperty(type, manager, "IsLoaded"))
            {
                reason = "already_loaded";
                return true;
            }

            if (ReadBoolProperty(type, manager, "IsLoading"))
            {
                reason = "already_loading";
                return true;
            }

            try
            {
                MethodInfo initModelMethod = type.GetMethod("InitModel", BindingFlags.Instance | BindingFlags.Public);
                if (initModelMethod == null)
                {
                    reason = "init_model_method_not_found";
                    return false;
                }

                initModelMethod.Invoke(manager, Array.Empty<object>());
                reason = "init_model_invoked";
                return true;
            }
            catch (TargetInvocationException ex)
            {
                reason = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private static string ReadModelPath(Object whisperManager)
        {
            object manager = ResolveWhisperManagerObject(whisperManager);
            return manager == null ? string.Empty : ReadModelPathFromObject(manager);
        }

        private static string ReadModelPathFromObject(object manager)
        {
            if (manager == null)
            {
                return string.Empty;
            }

            Type type = manager.GetType();
            PropertyInfo property = type.GetProperty("ModelPath", BindingFlags.Instance | BindingFlags.Public);
            object value = property != null ? property.GetValue(manager) : null;
            if (value == null)
            {
                FieldInfo field = type.GetField("modelPath", BindingFlags.Instance | BindingFlags.NonPublic);
                value = field != null ? field.GetValue(manager) : null;
            }

            return value?.ToString() ?? string.Empty;
        }

        public static object ResolveWhisperManagerObject(Object configuredObject)
        {
            if (configuredObject == null)
            {
                return null;
            }

            if (HasModelPathAccessor(configuredObject.GetType()))
            {
                return configuredObject;
            }

            Type managerType = ResolveWhisperManagerType();
            if (managerType == null)
            {
                return null;
            }

            if (managerType.IsInstanceOfType(configuredObject))
            {
                return configuredObject;
            }

            if (configuredObject is GameObject gameObject)
            {
                Component component = gameObject.GetComponent(managerType);
                if (component != null)
                {
                    return component;
                }
            }

            if (configuredObject is Component sourceComponent)
            {
                Component component = sourceComponent.GetComponent(managerType);
                if (component != null)
                {
                    return component;
                }

                component = sourceComponent.GetComponentInParent(managerType);
                if (component != null)
                {
                    return component;
                }
            }

            return Object.FindFirstObjectByType(managerType);
        }

        public static bool TryWriteModelPathForTesting(Object manager, string relativeModelPath, out string failedReason)
        {
            return TryWriteModelPath(manager, relativeModelPath, true, out failedReason);
        }

        private static bool TryWriteModelPath(object manager, string modelPath, bool isModelPathInStreamingAssets, out string failedReason)
        {
            failedReason = string.Empty;
            if (manager == null)
            {
                failedReason = "whisper_manager_not_assigned_or_not_found";
                return false;
            }

            Type type = manager.GetType();
            try
            {
                PropertyInfo modelPathProperty = type.GetProperty("ModelPath", BindingFlags.Instance | BindingFlags.Public);
                if (modelPathProperty != null && modelPathProperty.CanWrite)
                {
                    modelPathProperty.SetValue(manager, modelPath);
                }
                else
                {
                    FieldInfo modelPathField = type.GetField("modelPath", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (modelPathField == null || modelPathField.FieldType != typeof(string))
                    {
                        failedReason = "model_path_property_or_field_not_found";
                        return false;
                    }

                    modelPathField.SetValue(manager, modelPath);
                }

                PropertyInfo streamingProperty = type.GetProperty("IsModelPathInStreamingAssets", BindingFlags.Instance | BindingFlags.Public);
                if (streamingProperty != null && streamingProperty.CanWrite)
                {
                    streamingProperty.SetValue(manager, isModelPathInStreamingAssets);
                    return true;
                }

                FieldInfo streamingField = type.GetField("isModelPathInStreamingAssets", BindingFlags.Instance | BindingFlags.NonPublic);
                if (streamingField != null && streamingField.FieldType == typeof(bool))
                {
                    streamingField.SetValue(manager, isModelPathInStreamingAssets);
                }

                return true;
            }
            catch (TargetInvocationException ex)
            {
                failedReason = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                failedReason = ex.Message;
                return false;
            }
        }

        private static Type ResolveWhisperManagerType()
        {
            return AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(assembly => assembly.GetType("Whisper.WhisperManager"))
                .FirstOrDefault(type => type != null);
        }

        private static bool HasModelPathAccessor(Type type)
        {
            if (type == null)
            {
                return false;
            }

            return type.GetProperty("ModelPath", BindingFlags.Instance | BindingFlags.Public) != null ||
                type.GetField("modelPath", BindingFlags.Instance | BindingFlags.NonPublic) != null;
        }

        private static WhisperModelApplyResult FailedResult(
            ASRModelSize requestedSize,
            string relativePath,
            string failedReason)
        {
            string absolutePath = string.IsNullOrWhiteSpace(relativePath)
                ? string.Empty
                : Path.Combine(Application.streamingAssetsPath, relativePath);
            return new WhisperModelApplyResult(
                requestedSize,
                ASRModelSize.Unknown,
                relativePath,
                absolutePath,
                string.IsNullOrWhiteSpace(relativePath) ? string.Empty : Path.GetFileName(relativePath),
                !string.IsNullOrWhiteSpace(absolutePath) && File.Exists(absolutePath),
                false,
                failedReason);
        }

        private static bool TryResolveRuntimeModelPath(
            ASRModelSize modelSize,
            bool copyIfNeeded,
            out RuntimeModelPathResolution resolution)
        {
            string relativePath = BuildStreamingAssetsModelPath(modelSize);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                resolution = RuntimeModelPathResolution.Failed(relativePath, string.Empty, string.Empty, string.Empty, false, false, "unsupported_model_size");
                return false;
            }

            string sourcePath = BuildStreamingAssetsSourcePath(relativePath);
            if (!ExperimentDataPathResolver.IsAndroidRuntime())
            {
                string absolutePath = Path.Combine(Application.streamingAssetsPath, relativePath);
                bool exists = File.Exists(absolutePath);
                long size = exists ? new FileInfo(absolutePath).Length : 0L;
                resolution = new RuntimeModelPathResolution(
                    relativePath,
                    sourcePath,
                    absolutePath,
                    relativePath,
                    modelPathInStreamingAssets: true,
                    exists,
                    size,
                    copiedThisRun: false,
                    string.Empty);
                return exists;
            }

            string destinationDirectory = Path.Combine(Application.persistentDataPath, "Whisper");
            string destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(relativePath));
            Directory.CreateDirectory(destinationDirectory);
            long sourceSize = TryGetLocalFileSize(sourcePath);
            bool destinationExists = File.Exists(destinationPath);
            long destinationSize = destinationExists ? new FileInfo(destinationPath).Length : 0L;
            bool copied = false;
            string error = string.Empty;

            bool needsCopy = copyIfNeeded &&
                (!destinationExists ||
                 destinationSize <= 0L ||
                 (sourceSize > 0L && destinationSize != sourceSize));

            if (needsCopy)
            {
                try
                {
                    CopyStreamingAssetToFile(sourcePath, destinationPath);
                    copied = true;
                    destinationExists = File.Exists(destinationPath);
                    destinationSize = destinationExists ? new FileInfo(destinationPath).Length : 0L;
                }
                catch (Exception ex)
                {
                    error = ex.GetBaseException().Message;
                    destinationExists = File.Exists(destinationPath);
                    destinationSize = destinationExists ? new FileInfo(destinationPath).Length : 0L;
                }
            }

            resolution = new RuntimeModelPathResolution(
                relativePath,
                sourcePath,
                destinationPath,
                ToFileUri(destinationPath),
                modelPathInStreamingAssets: false,
                destinationExists && destinationSize > 0L,
                destinationSize,
                copied,
                error);
            return resolution.FileExists;
        }

        private static string ToFileUri(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string normalized = path.Replace("\\", "/");
            if (normalized.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            if (normalized.StartsWith("/", StringComparison.Ordinal))
            {
                return "file://" + normalized;
            }

            return new Uri(normalized).AbsoluteUri;
        }

        private static string BuildStreamingAssetsSourcePath(string relativePath)
        {
            string combined = Path.Combine(Application.streamingAssetsPath, relativePath);
            return combined.Replace("\\", "/");
        }

        private static long TryGetLocalFileSize(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && !path.Contains("://") && File.Exists(path))
                {
                    return new FileInfo(path).Length;
                }
            }
            catch (Exception)
            {
                // Size is only an optimization guard.
            }

            return -1L;
        }

        private static void CopyStreamingAssetToFile(string sourcePath, string destinationPath)
        {
            if (!sourcePath.Contains("://"))
            {
                File.Copy(sourcePath, destinationPath, true);
                return;
            }

            using UnityWebRequest request = UnityWebRequest.Get(sourcePath);
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            float startedAt = Time.realtimeSinceStartup;
            while (!operation.isDone)
            {
                if (Time.realtimeSinceStartup - startedAt > 120f)
                {
                    request.Abort();
                    throw new TimeoutException("whisper_model_copy_timeout");
                }
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                throw new IOException("streaming_assets_read_failed: " + request.error);
            }

            File.WriteAllBytes(destinationPath, request.downloadHandler.data);
        }

        private static void LogModelResolution(
            ASRModelSize requestedModel,
            ASRModelSize effectiveModel,
            string relativePath,
            RuntimeModelPathResolution resolution)
        {
            var payload = new System.Collections.Generic.Dictionary<string, object>
            {
                ["requested_model"] = requestedModel.ToString(),
                ["effective_model"] = effectiveModel.ToString(),
                ["source_path"] = resolution.SourcePath,
                ["requested_relative_path"] = relativePath ?? string.Empty,
                ["resolved_runtime_path"] = resolution.ResolvedRuntimePath,
                ["file_exists"] = resolution.FileExists,
                ["file_size"] = resolution.FileSize,
                ["copied_this_run"] = resolution.CopiedThisRun,
                ["fallback_used"] = effectiveModel != ASRModelSize.Unknown && effectiveModel != requestedModel,
                ["platform"] = Application.platform.ToString(),
                ["error"] = resolution.Error
            };
            TiagoExperimentTelemetry.LogEvent("p45d_asr_model_resolved", payload);
            UnityEngine.Debug.Log(
                $"[WhisperModelConfiguration] p45d_asr_model_resolved | requested={requestedModel} effective={effectiveModel} source='{resolution.SourcePath}' runtime='{resolution.ResolvedRuntimePath}' manager='{resolution.ModelPathForManager}' exists={resolution.FileExists} size={resolution.FileSize} copied={resolution.CopiedThisRun} platform={Application.platform}");
        }

        private static void LogManagerPathApplied(
            ASRModelSize requestedModel,
            ASRModelSize effectiveModel,
            RuntimeModelPathResolution resolution,
            object manager)
        {
            WhisperModelLoadState loadState = ResolveLoadState(manager as Object);
            bool isStreaming = manager != null && ReadBoolPropertyOrField(manager.GetType(), manager, "IsModelPathInStreamingAssets", "isModelPathInStreamingAssets");
            var payload = new System.Collections.Generic.Dictionary<string, object>
            {
                ["requested_model"] = requestedModel.ToString(),
                ["effective_model"] = effectiveModel.ToString(),
                ["raw_persistent_path"] = resolution.ResolvedRuntimePath,
                ["manager_model_path"] = resolution.ModelPathForManager,
                ["is_model_path_in_streaming_assets"] = isStreaming,
                ["file_exists"] = resolution.FileExists,
                ["file_size"] = resolution.FileSize,
                ["platform"] = Application.platform.ToString(),
                ["scene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                ["whisper_manager_is_loaded"] = loadState.IsLoaded,
                ["whisper_manager_is_loading"] = loadState.IsLoading
            };
            TiagoExperimentTelemetry.LogEvent("p45d03_asr_model_path_for_manager", payload);
            UnityEngine.Debug.Log(
                $"[WhisperModelConfiguration] p45d03_asr_model_path_for_manager | requested={requestedModel} effective={effectiveModel} raw='{resolution.ResolvedRuntimePath}' manager='{resolution.ModelPathForManager}' streaming={isStreaming} exists={resolution.FileExists} size={resolution.FileSize} loaded={loadState.IsLoaded} loading={loadState.IsLoading} platform={Application.platform}");
        }

        private static void LogBaseMissing(
            ASRModelSize requestedModel,
            RuntimeModelPathResolution resolution,
            string reason)
        {
            var payload = new System.Collections.Generic.Dictionary<string, object>
            {
                ["requested_model"] = requestedModel.ToString(),
                ["source_path"] = resolution.SourcePath,
                ["resolved_runtime_path"] = resolution.ResolvedRuntimePath,
                ["file_exists"] = resolution.FileExists,
                ["file_size"] = resolution.FileSize,
                ["copied_this_run"] = resolution.CopiedThisRun,
                ["platform"] = Application.platform.ToString(),
                ["reason"] = reason
            };
            TiagoExperimentTelemetry.LogEvent("p45d_asr_base_missing", payload);
            UnityEngine.Debug.LogError(
                $"[WhisperModelConfiguration] p45d_asr_base_missing | requested=Base source='{resolution.SourcePath}' runtime='{resolution.ResolvedRuntimePath}' reason={reason}");
        }

        private readonly struct RuntimeModelPathResolution
        {
            public RuntimeModelPathResolution(
                string relativePath,
                string sourcePath,
                string resolvedRuntimePath,
                string modelPathForManager,
                bool modelPathInStreamingAssets,
                bool fileExists,
                long fileSize,
                bool copiedThisRun,
                string error)
            {
                RelativePath = relativePath ?? string.Empty;
                SourcePath = sourcePath ?? string.Empty;
                ResolvedRuntimePath = resolvedRuntimePath ?? string.Empty;
                ModelPathForManager = modelPathForManager ?? string.Empty;
                ModelPathInStreamingAssets = modelPathInStreamingAssets;
                FileExists = fileExists;
                FileSize = fileSize;
                CopiedThisRun = copiedThisRun;
                Error = error ?? string.Empty;
            }

            public string RelativePath { get; }
            public string SourcePath { get; }
            public string ResolvedRuntimePath { get; }
            public string ModelPathForManager { get; }
            public bool ModelPathInStreamingAssets { get; }
            public bool FileExists { get; }
            public long FileSize { get; }
            public bool CopiedThisRun { get; }
            public string Error { get; }

            public static RuntimeModelPathResolution Failed(
                string relativePath,
                string sourcePath,
                string resolvedRuntimePath,
                string modelPathForManager,
                bool modelPathInStreamingAssets,
                bool copiedThisRun,
                string error)
            {
                return new RuntimeModelPathResolution(
                    relativePath,
                    sourcePath,
                    resolvedRuntimePath,
                    modelPathForManager,
                    modelPathInStreamingAssets,
                    fileExists: false,
                    fileSize: 0L,
                    copiedThisRun,
                    error);
            }
        }

        private static bool ReadBoolProperty(Type type, object instance, string propertyName)
        {
            PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            object value = property != null ? property.GetValue(instance) : null;
            return value is bool boolValue && boolValue;
        }

        private static bool ReadBoolPropertyOrField(Type type, object instance, string propertyName, string fieldName)
        {
            PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            object value = property != null ? property.GetValue(instance) : null;
            if (value is bool propertyValue)
            {
                return propertyValue;
            }

            FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            value = field != null ? field.GetValue(instance) : null;
            return value is bool fieldValue && fieldValue;
        }

        private static ASRModelSize InferModelSize(string modelPath)
        {
            string lower = (modelPath ?? string.Empty).ToLowerInvariant();
            if (lower.Contains("tiny"))
            {
                return ASRModelSize.Tiny;
            }

            if (lower.Contains("base"))
            {
                return ASRModelSize.Base;
            }

            if (lower.Contains("small"))
            {
                return ASRModelSize.Small;
            }

            return ASRModelSize.Unknown;
        }
    }
}
