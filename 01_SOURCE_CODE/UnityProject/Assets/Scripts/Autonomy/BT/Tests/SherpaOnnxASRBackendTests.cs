using Autonomy.Domain;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using System;
using System.IO;
using System.IO.Compression;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public sealed class SherpaOnnxASRBackendTests
    {
        [Test]
        public void Sherpa_Options_Default_To_Dedicated_Android_Decode_Mode()
        {
            SherpaOnnxBackendOptions options = new();

            Assert.That(options.UseAndroidDedicatedDecodeThread, Is.True);
            Assert.That(options.UseAndroidWorkerThreadForDecode, Is.False);
            Assert.That(options.EnableSherpaHotwords, Is.False);
            Assert.That(options.FallbackToMainThreadStableOnWorkerFailure, Is.False);
            Assert.That(options.AndroidDecodeTimeoutMs, Is.GreaterThanOrEqualTo(1000));
            Assert.That(options.Hotwords, Does.Contain("para el robot"));
            Assert.That(options.Hotwords, Does.Contain("detente ahora"));
            Assert.That(options.Hotwords, Does.Contain("deten la tarea actual"));
            Assert.That(options.Hotwords, Does.Contain("cancela la tarea actual"));
        }

        [Test]
        public void Sherpa_Preflight_Reports_Unavailable_When_Artifacts_Are_Missing()
        {
            string missingRoot = Path.Combine("Temp", "SherpaOnnxMissing", Guid.NewGuid().ToString("N"));
            SherpaOnnxASRBackend backend = new(
                new SherpaOnnxBackendOptions(),
                null,
                streamingAssetsPathProvider: () => missingRoot,
                dataPathProvider: () => Path.Combine(missingRoot, "Assets"));

            ASRBackendPreflightResult preflight = backend.Preflight(ASRBackend.SherpaOnnx, "es");

            Assert.That(preflight.IsAvailable, Is.False);
            Assert.That(preflight.EffectiveBackend, Is.EqualTo(ASRBackend.SherpaOnnx));
            Assert.That(preflight.Reason, Is.EqualTo("model_directory_missing"));
            Assert.That(preflight.Error, Does.Contain(SherpaOnnxASRBackend.MissingArtifactsTag));
            Assert.That(preflight.ModelPath, Does.Contain(Path.Combine("SherpaOnnx", "spanish_or_multilingual_model")));
            Assert.That(preflight.ArchitectureAbi, Is.EqualTo(SherpaOnnxBackendOptions.DefaultAndroidAbi));
        }

        [Test]
        public void Sherpa_Preflight_Accepts_Parakeet_Int8_Transducer_Layout_Before_Runtime_Check()
        {
            string tempRoot = Path.Combine("Temp", "SherpaOnnxParakeet", Guid.NewGuid().ToString("N"));
            string tempAssets = Path.Combine(tempRoot, "Assets");
            string modelDirectory = Path.Combine(
                tempRoot,
                SherpaOnnxBackendOptions.DefaultStreamingAssetsRoot,
                SherpaOnnxBackendOptions.DefaultModelName);
            Directory.CreateDirectory(modelDirectory);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "encoder.int8.onnx"), 1024);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "decoder.int8.onnx"), 512);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "joiner.int8.onnx"), 256);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "tokens.txt"), 64);

            try
            {
                SherpaOnnxASRBackend backend = new(
                    new SherpaOnnxBackendOptions(),
                    null,
                    streamingAssetsPathProvider: () => tempRoot,
                    dataPathProvider: () => tempAssets);

                ASRBackendPreflightResult preflight = backend.Preflight(ASRBackend.SherpaOnnx, "es");

                Assert.That(preflight.IsAvailable, Is.False);
                Assert.That(preflight.Reason, Is.EqualTo("runtime_binding_missing"));
                Assert.That(preflight.Error, Does.Not.Contain("model_files_missing"));
                Assert.That(preflight.Error, Does.Contain(SherpaOnnxBackendOptions.DefaultAndroidRecognizerClassName));
                Assert.That(preflight.ModelLayout, Is.EqualTo("encoder.int8.onnx+decoder.int8.onnx+joiner.int8.onnx+tokens.txt"));
                Assert.That(preflight.ModelFilesReport, Does.Contain("encoder.int8.onnx:1024"));
                Assert.That(preflight.ModelFilesReport, Does.Contain("decoder.int8.onnx:512"));
                Assert.That(preflight.ModelFilesReport, Does.Contain("joiner.int8.onnx:256"));
                Assert.That(preflight.ModelFilesReport, Does.Contain("tokens.txt:64"));
                Assert.That(preflight.ModelMainFileSizeBytes, Is.EqualTo(1024));
                Assert.That(preflight.LanguageCompatible, Is.True);
                Assert.That(preflight.RuntimeFilesReport, Does.Contain(SherpaOnnxBackendOptions.DefaultAndroidJniLibraryFileName));
                Assert.That(preflight.RuntimeFilesReport, Does.Contain("missing"));
                Assert.That(preflight.RuntimePath, Does.Contain("Assets"));
                Assert.That(preflight.RuntimePath, Does.Contain("Plugins"));
                Assert.That(preflight.RuntimePath, Does.Contain("Android"));
                Assert.That(preflight.RuntimeBindingType, Is.Empty);
                Assert.That(preflight.ArchitectureAbi, Is.EqualTo(SherpaOnnxBackendOptions.DefaultAndroidAbi));
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [Test]
        public void Sherpa_Preflight_Reports_Recognizer_Wrapper_Not_Implemented_When_Runtime_And_Bridge_Are_Present()
        {
            string tempRoot = Path.Combine("Temp", "SherpaOnnxRuntime", Guid.NewGuid().ToString("N"));
            string tempAssets = Path.Combine(tempRoot, "Assets");
            string modelDirectory = Path.Combine(
                tempRoot,
                SherpaOnnxBackendOptions.DefaultStreamingAssetsRoot,
                SherpaOnnxBackendOptions.DefaultModelName);
            string androidPluginRoot = Path.Combine(tempAssets, SherpaOnnxBackendOptions.DefaultAndroidPluginRelativePath);
            string nativeDirectory = Path.Combine(androidPluginRoot, "libs", SherpaOnnxBackendOptions.DefaultAndroidAbi);
            Directory.CreateDirectory(modelDirectory);
            Directory.CreateDirectory(nativeDirectory);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "encoder.int8.onnx"), 1024);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "decoder.int8.onnx"), 512);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "joiner.int8.onnx"), 256);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "tokens.txt"), 64);
            WriteNonEmptyFile(Path.Combine(nativeDirectory, SherpaOnnxBackendOptions.DefaultAndroidJniLibraryFileName), 128);
            WriteNonEmptyFile(Path.Combine(nativeDirectory, SherpaOnnxBackendOptions.DefaultOnnxRuntimeLibraryFileName), 128);
            WriteAndroidBridgeAar(
                Path.Combine(androidPluginRoot, "sherpa-onnx-android.aar"),
                SherpaOnnxBackendOptions.DefaultAndroidRecognizerClassName);

            try
            {
                SherpaOnnxASRBackend backend = new(
                    new SherpaOnnxBackendOptions(),
                    null,
                    streamingAssetsPathProvider: () => tempRoot,
                    dataPathProvider: () => tempAssets);

                ASRBackendPreflightResult preflight = backend.Preflight(ASRBackend.SherpaOnnx, "es");

                Assert.That(preflight.IsAvailable, Is.False);
                Assert.That(preflight.Reason, Is.EqualTo("recognizer_wrapper_not_implemented"));
                Assert.That(preflight.Error, Does.Not.Contain("model_files_missing"));
                Assert.That(preflight.Error, Does.Contain("android_bridge_detected"));
                Assert.That(preflight.ModelLayout, Is.EqualTo("encoder.int8.onnx+decoder.int8.onnx+joiner.int8.onnx+tokens.txt"));
                Assert.That(preflight.RuntimeFilesReport, Does.Contain(SherpaOnnxBackendOptions.DefaultAndroidJniLibraryFileName));
                Assert.That(preflight.RuntimeFilesReport, Does.Contain(SherpaOnnxBackendOptions.DefaultOnnxRuntimeLibraryFileName));
                Assert.That(preflight.RuntimeFilesReport, Does.Contain("sherpa-onnx-android.aar"));
                Assert.That(preflight.RuntimeBindingType, Is.EqualTo(SherpaOnnxBackendOptions.DefaultAndroidRecognizerClassName));
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [Test]
        public void Sherpa_Preflight_Accepts_Runtime_Embedded_In_Aar()
        {
            string tempRoot = Path.Combine("Temp", "SherpaOnnxEmbeddedRuntime", Guid.NewGuid().ToString("N"));
            string tempAssets = Path.Combine(tempRoot, "Assets");
            string modelDirectory = Path.Combine(
                tempRoot,
                SherpaOnnxBackendOptions.DefaultStreamingAssetsRoot,
                SherpaOnnxBackendOptions.DefaultModelName);
            string androidPluginRoot = Path.Combine(tempAssets, SherpaOnnxBackendOptions.DefaultAndroidPluginRelativePath);
            Directory.CreateDirectory(modelDirectory);
            Directory.CreateDirectory(androidPluginRoot);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "encoder.int8.onnx"), 1024);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "decoder.int8.onnx"), 512);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "joiner.int8.onnx"), 256);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "tokens.txt"), 64);
            WriteAndroidBridgeAar(
                Path.Combine(androidPluginRoot, "sherpa-onnx-android.aar"),
                SherpaOnnxBackendOptions.DefaultAndroidRecognizerClassName,
                includeNativeLibraries: true);

            try
            {
                SherpaOnnxASRBackend backend = new(
                    new SherpaOnnxBackendOptions(),
                    null,
                    streamingAssetsPathProvider: () => tempRoot,
                    dataPathProvider: () => tempAssets);

                ASRBackendPreflightResult preflight = backend.Preflight(ASRBackend.SherpaOnnx, "es");

                Assert.That(preflight.IsAvailable, Is.False);
                Assert.That(preflight.Reason, Is.EqualTo("recognizer_wrapper_not_implemented"));
                Assert.That(preflight.Error, Does.Contain("runtime_files_ok_embedded_in_aar"));
                Assert.That(preflight.Error, Does.Contain("android_bridge_detected"));
                Assert.That(preflight.RuntimeFilesReport, Does.Contain("aar_embedded_" + SherpaOnnxBackendOptions.DefaultAndroidJniLibraryFileName));
                Assert.That(preflight.RuntimeFilesReport, Does.Contain("aar_embedded_" + SherpaOnnxBackendOptions.DefaultOnnxRuntimeLibraryFileName));
                Assert.That(preflight.RuntimeFilesReport, Does.Contain("explicit_" + SherpaOnnxBackendOptions.DefaultAndroidJniLibraryFileName + "_optional:not_present"));
                Assert.That(preflight.RuntimeFilesReport, Does.Not.Contain("explicit_" + SherpaOnnxBackendOptions.DefaultAndroidJniLibraryFileName + "_optional:missing"));
                Assert.That(preflight.RuntimeBindingType, Is.EqualTo(SherpaOnnxBackendOptions.DefaultAndroidRecognizerClassName));
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [Test]
        public void Sherpa_Preflight_OnAndroid_Uses_Persistent_Model_Path()
        {
            string tempRoot = Path.Combine("Temp", "SherpaOnnxAndroidPersistent", Guid.NewGuid().ToString("N"));
            string tempAssets = Path.Combine(tempRoot, "Assets");
            string persistentRoot = Path.Combine(tempRoot, "Persistent");
            string modelDirectory = Path.Combine(
                persistentRoot,
                SherpaOnnxBackendOptions.DefaultPersistentDataRelativeRoot,
                SherpaOnnxBackendOptions.DefaultModelName);
            string androidPluginRoot = Path.Combine(tempAssets, SherpaOnnxBackendOptions.DefaultAndroidPluginRelativePath);
            Directory.CreateDirectory(modelDirectory);
            Directory.CreateDirectory(androidPluginRoot);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "encoder.int8.onnx"), 1024);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "decoder.int8.onnx"), 512);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "joiner.int8.onnx"), 256);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "tokens.txt"), 64);
            WriteAndroidBridgeAar(
                Path.Combine(androidPluginRoot, "sherpa-onnx-android.aar"),
                SherpaOnnxBackendOptions.DefaultAndroidRecognizerClassName,
                includeNativeLibraries: true);

            try
            {
                SherpaOnnxASRBackend backend = new(
                    new SherpaOnnxBackendOptions(),
                    null,
                    streamingAssetsPathProvider: () => "jar:file:///data/app/example/base.apk!/assets",
                    dataPathProvider: () => tempAssets,
                    persistentDataPathProvider: () => persistentRoot,
                    platformProvider: () => "Android");

                ASRBackendPreflightResult preflight = backend.Preflight(ASRBackend.SherpaOnnx, "es");

                Assert.That(preflight.IsAvailable, Is.True);
                Assert.That(preflight.ModelPath, Is.EqualTo(modelDirectory));
                Assert.That(preflight.ModelPath, Does.Not.Contain("jar:file"));
                Assert.That(preflight.Reason, Does.Contain("runtime_files_ok_embedded_in_aar"));
                Assert.That(preflight.Error, Does.Not.Contain("model_directory_missing"));
                Assert.That(preflight.ModelLayout, Is.EqualTo("encoder.int8.onnx+decoder.int8.onnx+joiner.int8.onnx+tokens.txt"));
                Assert.That(preflight.RuntimeFilesReport, Does.Contain("aar_embedded_" + SherpaOnnxBackendOptions.DefaultAndroidJniLibraryFileName));
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [Test]
        public void Sherpa_Preflight_OnAndroid_Reports_Model_Resolve_Pending_Before_Copy()
        {
            string tempRoot = Path.Combine("Temp", "SherpaOnnxAndroidPending", Guid.NewGuid().ToString("N"));
            string tempAssets = Path.Combine(tempRoot, "Assets");
            string persistentRoot = Path.Combine(tempRoot, "Persistent");

            try
            {
                SherpaOnnxASRBackend backend = new(
                    new SherpaOnnxBackendOptions(),
                    null,
                    streamingAssetsPathProvider: () => "jar:file:///data/app/example/base.apk!/assets",
                    dataPathProvider: () => tempAssets,
                    persistentDataPathProvider: () => persistentRoot,
                    platformProvider: () => "Android");

                ASRBackendPreflightResult preflight = backend.Preflight(ASRBackend.SherpaOnnx, "es");

                Assert.That(preflight.IsAvailable, Is.False);
                Assert.That(preflight.Reason, Is.EqualTo("model_resolve_pending"));
                Assert.That(preflight.Error, Does.Contain("persistent_model_path"));
                Assert.That(preflight.Error, Does.Not.Contain(SherpaOnnxASRBackend.MissingArtifactsTag));
                Assert.That(preflight.Error, Does.Not.Contain("model_directory_missing"));
                Assert.That(preflight.ModelPath, Does.Contain(Path.Combine(
                    SherpaOnnxBackendOptions.DefaultPersistentDataRelativeRoot,
                    SherpaOnnxBackendOptions.DefaultModelName)));
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [Test]
        public void Sherpa_Preflight_Reports_Duplicate_Native_Libraries_When_Explicit_And_Aar_Runtime_Are_Present()
        {
            string tempRoot = Path.Combine("Temp", "SherpaOnnxDuplicateRuntime", Guid.NewGuid().ToString("N"));
            string tempAssets = Path.Combine(tempRoot, "Assets");
            string modelDirectory = Path.Combine(
                tempRoot,
                SherpaOnnxBackendOptions.DefaultStreamingAssetsRoot,
                SherpaOnnxBackendOptions.DefaultModelName);
            string androidPluginRoot = Path.Combine(tempAssets, SherpaOnnxBackendOptions.DefaultAndroidPluginRelativePath);
            string nativeDirectory = Path.Combine(androidPluginRoot, "libs", SherpaOnnxBackendOptions.DefaultAndroidAbi);
            Directory.CreateDirectory(modelDirectory);
            Directory.CreateDirectory(nativeDirectory);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "encoder.int8.onnx"), 1024);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "decoder.int8.onnx"), 512);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "joiner.int8.onnx"), 256);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "tokens.txt"), 64);
            WriteBytes(Path.Combine(nativeDirectory, SherpaOnnxBackendOptions.DefaultAndroidJniLibraryFileName), new byte[] { 1, 2, 3 });
            WriteBytes(Path.Combine(nativeDirectory, SherpaOnnxBackendOptions.DefaultOnnxRuntimeLibraryFileName), new byte[] { 4, 5, 6 });
            WriteAndroidBridgeAar(
                Path.Combine(androidPluginRoot, "sherpa-onnx-android.aar"),
                SherpaOnnxBackendOptions.DefaultAndroidRecognizerClassName,
                includeNativeLibraries: true,
                jniBytes: new byte[] { 1, 2, 3 },
                onnxRuntimeBytes: new byte[] { 4, 5, 6 });

            try
            {
                SherpaOnnxASRBackend backend = new(
                    new SherpaOnnxBackendOptions(),
                    null,
                    streamingAssetsPathProvider: () => tempRoot,
                    dataPathProvider: () => tempAssets);

                ASRBackendPreflightResult preflight = backend.Preflight(ASRBackend.SherpaOnnx, "es");

                Assert.That(preflight.IsAvailable, Is.False);
                Assert.That(preflight.Reason, Is.EqualTo("android_packaging_duplicate_native_libs"));
                Assert.That(preflight.Error, Does.Contain("runtime_files_duplicate_same_hash"));
                Assert.That(preflight.Error, Does.Contain("android_packaging_duplicate_native_libs"));
                Assert.That(preflight.RuntimeBindingType, Is.EqualTo(SherpaOnnxBackendOptions.DefaultAndroidRecognizerClassName));
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [Test]
        public void Sherpa_Preflight_Rejects_Aar_Without_OfflineRecognizer_Class()
        {
            string tempRoot = Path.Combine("Temp", "SherpaOnnxInvalidBridge", Guid.NewGuid().ToString("N"));
            string tempAssets = Path.Combine(tempRoot, "Assets");
            string modelDirectory = Path.Combine(
                tempRoot,
                SherpaOnnxBackendOptions.DefaultStreamingAssetsRoot,
                SherpaOnnxBackendOptions.DefaultModelName);
            string androidPluginRoot = Path.Combine(tempAssets, SherpaOnnxBackendOptions.DefaultAndroidPluginRelativePath);
            string nativeDirectory = Path.Combine(androidPluginRoot, "libs", SherpaOnnxBackendOptions.DefaultAndroidAbi);
            Directory.CreateDirectory(modelDirectory);
            Directory.CreateDirectory(nativeDirectory);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "encoder.int8.onnx"), 1024);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "decoder.int8.onnx"), 512);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "joiner.int8.onnx"), 256);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "tokens.txt"), 64);
            WriteNonEmptyFile(Path.Combine(nativeDirectory, SherpaOnnxBackendOptions.DefaultAndroidJniLibraryFileName), 128);
            WriteNonEmptyFile(Path.Combine(nativeDirectory, SherpaOnnxBackendOptions.DefaultOnnxRuntimeLibraryFileName), 128);
            WriteNonEmptyFile(Path.Combine(androidPluginRoot, "sherpa-onnx-invalid.aar"), 128);

            try
            {
                SherpaOnnxASRBackend backend = new(
                    new SherpaOnnxBackendOptions(),
                    null,
                    streamingAssetsPathProvider: () => tempRoot,
                    dataPathProvider: () => tempAssets);

                ASRBackendPreflightResult preflight = backend.Preflight(ASRBackend.SherpaOnnx, "es");

                Assert.That(preflight.IsAvailable, Is.False);
                Assert.That(preflight.Reason, Is.EqualTo("aar_missing_expected_classes"));
                Assert.That(preflight.Error, Does.Contain("android_bridge_invalid"));
                Assert.That(preflight.Error, Does.Contain(SherpaOnnxBackendOptions.DefaultAndroidRecognizerClassName));
                Assert.That(preflight.RuntimeBindingType, Is.Empty);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [Test]
        public void Sherpa_Preflight_Reports_Android_Bridge_Missing_When_Native_Runtime_Is_Present()
        {
            string tempRoot = Path.Combine("Temp", "SherpaOnnxNativeRuntime", Guid.NewGuid().ToString("N"));
            string tempAssets = Path.Combine(tempRoot, "Assets");
            string modelDirectory = Path.Combine(
                tempRoot,
                SherpaOnnxBackendOptions.DefaultStreamingAssetsRoot,
                SherpaOnnxBackendOptions.DefaultModelName);
            string androidPluginRoot = Path.Combine(tempAssets, SherpaOnnxBackendOptions.DefaultAndroidPluginRelativePath);
            string nativeDirectory = Path.Combine(androidPluginRoot, "libs", SherpaOnnxBackendOptions.DefaultAndroidAbi);
            Directory.CreateDirectory(modelDirectory);
            Directory.CreateDirectory(nativeDirectory);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "encoder.int8.onnx"), 1024);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "decoder.int8.onnx"), 512);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "joiner.int8.onnx"), 256);
            WriteNonEmptyFile(Path.Combine(modelDirectory, "tokens.txt"), 64);
            WriteNonEmptyFile(Path.Combine(nativeDirectory, SherpaOnnxBackendOptions.DefaultAndroidJniLibraryFileName), 128);
            WriteNonEmptyFile(Path.Combine(nativeDirectory, SherpaOnnxBackendOptions.DefaultOnnxRuntimeLibraryFileName), 128);

            try
            {
                SherpaOnnxASRBackend backend = new(
                    new SherpaOnnxBackendOptions(),
                    null,
                    streamingAssetsPathProvider: () => tempRoot,
                    dataPathProvider: () => tempAssets);

                ASRBackendPreflightResult preflight = backend.Preflight(ASRBackend.SherpaOnnx, "es");

                Assert.That(preflight.IsAvailable, Is.False);
                Assert.That(preflight.Reason, Is.EqualTo("android_bridge_missing"));
                Assert.That(preflight.Error, Does.Contain("runtime_files_ok"));
                Assert.That(preflight.Error, Does.Contain("android_bridge_missing"));
                Assert.That(preflight.Error, Does.Contain(SherpaOnnxBackendOptions.DefaultAndroidRecognizerClassName));
                Assert.That(preflight.RuntimeFilesReport, Does.Contain(SherpaOnnxBackendOptions.DefaultAndroidJniLibraryFileName));
                Assert.That(preflight.RuntimeFilesReport, Does.Contain(SherpaOnnxBackendOptions.DefaultOnnxRuntimeLibraryFileName));
                Assert.That(preflight.RuntimeBindingType, Is.Empty);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        private static void WriteNonEmptyFile(string path, int byteCount)
        {
            byte[] bytes = new byte[byteCount];
            bytes[0] = 1;
            WriteBytes(path, bytes);
        }

        private static void WriteBytes(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllBytes(path, bytes);
        }

        private static void WriteAndroidBridgeAar(
            string path,
            string recognizerClassName,
            bool includeNativeLibraries = false,
            byte[] jniBytes = null,
            byte[] onnxRuntimeBytes = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            using MemoryStream classesBytes = new();
            using (ZipArchive classesArchive = new(classesBytes, ZipArchiveMode.Create, leaveOpen: true))
            {
                string classPath = recognizerClassName.Replace('.', '/') + ".class";
                ZipArchiveEntry classEntry = classesArchive.CreateEntry(classPath);
                using Stream classStream = classEntry.Open();
                classStream.WriteByte(1);
            }

            classesBytes.Position = 0L;
            using ZipArchive aar = ZipFile.Open(path, ZipArchiveMode.Create);
            ZipArchiveEntry classesEntry = aar.CreateEntry("classes.jar");
            using Stream aarClassesStream = classesEntry.Open();
            classesBytes.CopyTo(aarClassesStream);

            if (!includeNativeLibraries)
            {
                return;
            }

            WriteAarEntry(
                aar,
                $"jni/{SherpaOnnxBackendOptions.DefaultAndroidAbi}/{SherpaOnnxBackendOptions.DefaultAndroidJniLibraryFileName}",
                jniBytes ?? new byte[] { 1, 2, 3 });
            WriteAarEntry(
                aar,
                $"jni/{SherpaOnnxBackendOptions.DefaultAndroidAbi}/{SherpaOnnxBackendOptions.DefaultOnnxRuntimeLibraryFileName}",
                onnxRuntimeBytes ?? new byte[] { 4, 5, 6 });
        }

        private static void WriteAarEntry(ZipArchive archive, string entryName, byte[] bytes)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName);
            using Stream stream = entry.Open();
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
