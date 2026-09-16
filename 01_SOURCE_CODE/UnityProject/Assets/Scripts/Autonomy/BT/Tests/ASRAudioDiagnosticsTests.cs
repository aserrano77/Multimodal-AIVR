using Autonomy.Domain;
using Autonomy.UnityIntegration;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public sealed class ASRAudioDiagnosticsTests
    {
        [Test]
        public void Analyze_Detects_Silence_For_Zero_Buffer()
        {
            ASRAudioAnalysis analysis = ASRAudioDiagnostics.Analyze(
                new float[] { 0f, 0f, 0f, 0f },
                1,
                16000,
                0.005f,
                0.01f);

            Assert.That(analysis.Rms, Is.EqualTo(0f));
            Assert.That(analysis.Peak, Is.EqualTo(0f));
            Assert.That(analysis.SilenceDetected, Is.True);
            Assert.That(analysis.NonSilentSamplePercent, Is.EqualTo(0f));
        }

        [Test]
        public void Analyze_Computes_Rms_Peak_And_NonSilent_Percent()
        {
            ASRAudioAnalysis analysis = ASRAudioDiagnostics.Analyze(
                new float[] { 0f, 0.5f, -0.5f, 1f },
                1,
                16000,
                0.005f,
                0.01f);

            Assert.That(analysis.Rms, Is.EqualTo(0.6123724f).Within(0.0001f));
            Assert.That(analysis.Peak, Is.EqualTo(1f));
            Assert.That(analysis.SilenceDetected, Is.False);
            Assert.That(analysis.NonSilentSamplePercent, Is.EqualTo(75f));
        }

        [Test]
        public void RecordedDuration_Uses_Samples_Channels_And_Frequency()
        {
            ASRAudioAnalysis analysis = new(32000, 2, 16000, 0.1f, 0.2f, 50f, false);

            Assert.That(analysis.RecordedDurationMs, Is.EqualTo(1000));
        }

        [Test]
        public void MicrophoneSelection_UsesPreferred_WhenAvailable()
        {
            UnityMicrophoneASRService.MicrophoneSelection selection =
                UnityMicrophoneASRService.ResolveMicrophoneSelection(
                    "Meta Quest 3 Microphone",
                    new[] { "PC Microphone", "Meta Quest 3 Microphone" });

            Assert.That(selection.SelectedDevice, Is.EqualTo("Meta Quest 3 Microphone"));
            Assert.That(selection.FallbackUsed, Is.False);
            Assert.That(selection.PreferredUnavailable, Is.False);
            Assert.That(selection.SelectionReason, Is.EqualTo("preferred_available"));
        }

        [Test]
        public void MicrophoneSelection_FallsBack_WhenPreferredUnavailable()
        {
            UnityMicrophoneASRService.MicrophoneSelection selection =
                UnityMicrophoneASRService.ResolveMicrophoneSelection(
                    "Meta Quest 3 Microphone",
                    new[] { "PC Microphone", "Webcam Microphone" });

            Assert.That(selection.SelectedDevice, Is.EqualTo("PC Microphone"));
            Assert.That(selection.FallbackUsed, Is.True);
            Assert.That(selection.PreferredUnavailable, Is.True);
            Assert.That(selection.SelectionReason, Is.EqualTo("preferred_unavailable_fallback_first_device"));
        }

        [Test]
        public void MicrophoneSelection_RecordsSelectedDevice_ForDefaultFallback()
        {
            UnityMicrophoneASRService.MicrophoneSelection selection =
                UnityMicrophoneASRService.ResolveMicrophoneSelection(
                    string.Empty,
                    new[] { "", "Laptop Array" });

            Assert.That(selection.SelectedDevice, Is.EqualTo("Laptop Array"));
            Assert.That(selection.FallbackUsed, Is.True);
            Assert.That(selection.AvailableDevices, Is.EqualTo(new[] { "Laptop Array" }));
            Assert.That(selection.SelectionReason, Is.EqualTo("default_first_device"));
        }
    }
}
