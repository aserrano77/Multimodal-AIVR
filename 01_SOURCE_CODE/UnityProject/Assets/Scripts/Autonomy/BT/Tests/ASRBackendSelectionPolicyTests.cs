using Autonomy.Domain;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public sealed class ASRBackendSelectionPolicyTests
    {
        [Test]
        public void Auto_In_Editor_Uses_Whisper_When_Available()
        {
            ASRBackendSelection selection = ASRBackendSelectionPolicy.Select(
                ASRBackend.Auto,
                isAndroidRuntime: false,
                whisperAvailable: true,
                sherpaPreflight: null,
                c11ExperimentalCondition: false);

            Assert.That(selection.EffectiveBackend, Is.EqualTo(ASRBackend.WhisperUnity));
            Assert.That(selection.FallbackUsed, Is.False);
            Assert.That(selection.Reason, Is.EqualTo("editor_or_pc_auto_whisper_available"));
        }

        [Test]
        public void Auto_In_Android_Prefers_Sherpa_When_Preflight_Passes()
        {
            ASRBackendPreflightResult sherpaReady = ASRBackendPreflightResult.Available(
                ASRBackend.Auto,
                ASRBackend.SherpaOnnx,
                "sherpa-multilingual-es",
                "Assets/StreamingAssets/SherpaOnnx/sherpa-multilingual-es",
                123456L,
                "Android",
                androidCompatible: true,
                language: "es",
                languageCompatible: true);

            ASRBackendSelection selection = ASRBackendSelectionPolicy.Select(
                ASRBackend.Auto,
                isAndroidRuntime: true,
                whisperAvailable: true,
                sherpaPreflight: sherpaReady,
                c11ExperimentalCondition: true);

            Assert.That(selection.EffectiveBackend, Is.EqualTo(ASRBackend.SherpaOnnx));
            Assert.That(selection.Preflight, Is.SameAs(sherpaReady));
            Assert.That(selection.FallbackUsed, Is.False);
        }

        [Test]
        public void Auto_In_Android_Does_Not_Fallback_To_Whisper_When_Sherpa_Missing_For_C11()
        {
            ASRBackendPreflightResult sherpaMissing = MissingSherpaPreflight();

            ASRBackendSelection selection = ASRBackendSelectionPolicy.Select(
                ASRBackend.Auto,
                isAndroidRuntime: true,
                whisperAvailable: true,
                sherpaPreflight: sherpaMissing,
                c11ExperimentalCondition: true);

            Assert.That(selection.EffectiveBackend, Is.EqualTo(ASRBackend.Unsupported));
            Assert.That(selection.FallbackUsed, Is.False);
            Assert.That(selection.Reason, Is.EqualTo("android_auto_c11_sherpa_unavailable_no_silent_fallback"));
            Assert.That(selection.Preflight.Error, Does.Contain("p45e01_sherpa_missing_artifacts"));
        }

        [Test]
        public void Manual_Whisper_Selection_Remains_Valid_When_Manager_Available()
        {
            ASRBackendSelection selection = ASRBackendSelectionPolicy.Select(
                ASRBackend.WhisperUnity,
                isAndroidRuntime: true,
                whisperAvailable: true,
                sherpaPreflight: MissingSherpaPreflight(),
                c11ExperimentalCondition: true);

            Assert.That(selection.EffectiveBackend, Is.EqualTo(ASRBackend.WhisperUnity));
            Assert.That(selection.FallbackUsed, Is.False);
        }

        [Test]
        public void Manual_Sherpa_Selection_Is_Unsupported_When_Preflight_Fails()
        {
            ASRBackendSelection selection = ASRBackendSelectionPolicy.Select(
                ASRBackend.SherpaOnnx,
                isAndroidRuntime: false,
                whisperAvailable: true,
                sherpaPreflight: MissingSherpaPreflight(),
                c11ExperimentalCondition: false);

            Assert.That(selection.EffectiveBackend, Is.EqualTo(ASRBackend.Unsupported));
            Assert.That(selection.Reason, Is.EqualTo("sherpa_unavailable_manual_selection"));
        }

        [TestCase(1500, ASRLatencyViabilityStatus.Good)]
        [TestCase(1501, ASRLatencyViabilityStatus.Acceptable)]
        [TestCase(2500, ASRLatencyViabilityStatus.Acceptable)]
        [TestCase(2501, ASRLatencyViabilityStatus.Borderline)]
        [TestCase(5000, ASRLatencyViabilityStatus.Borderline)]
        [TestCase(5001, ASRLatencyViabilityStatus.NotViable)]
        public void Latency_Viability_Uses_P45E01_Quest_Thresholds(long latencyMs, ASRLatencyViabilityStatus expected)
        {
            Assert.That(ASRLatencyViability.Evaluate(latencyMs), Is.EqualTo(expected));
        }

        private static ASRBackendPreflightResult MissingSherpaPreflight()
        {
            return ASRBackendPreflightResult.Unavailable(
                ASRBackend.Auto,
                ASRBackend.SherpaOnnx,
                "spanish_or_multilingual_model",
                "Assets/StreamingAssets/SherpaOnnx/spanish_or_multilingual_model",
                "Android",
                "es",
                "model_directory_missing",
                "p45e01_sherpa_missing_artifacts: model_directory_missing",
                androidCompatible: true,
                languageCompatible: true);
        }
    }
}
