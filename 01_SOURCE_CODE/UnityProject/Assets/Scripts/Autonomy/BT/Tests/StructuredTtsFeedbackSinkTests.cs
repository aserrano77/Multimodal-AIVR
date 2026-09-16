using Autonomy.Domain;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public sealed class StructuredTtsFeedbackSinkTests
    {
        [Test]
        public void Formatter_Acceptance_UsesSemanticTemplate()
        {
            RobotVoiceFeedbackTtsFormatter formatter = new();

            TtsFeedbackUtterance utterance = formatter.Format(Message(RobotVoiceFeedbackKind.OrderAccepted, "A1", "SELF"));

            Assert.That(utterance.Text, Is.EqualTo("Orden aceptada. Voy a por la caja A1."));
            Assert.That(utterance.Priority, Is.EqualTo(TtsFeedbackPriority.Normal));
        }

        [Test]
        public void Formatter_Rejection_ExplainsUnavailableTarget()
        {
            RobotVoiceFeedbackTtsFormatter formatter = new();

            TtsFeedbackUtterance utterance = formatter.Format(Message(RobotVoiceFeedbackKind.CommandRejectedTargetNotFound, "Z9", "SELF"));

            Assert.That(utterance.Text, Is.EqualTo("Orden rechazada. No encuentro esa caja."));
            Assert.That(utterance.Priority, Is.EqualTo(TtsFeedbackPriority.Critical));
        }

        [Test]
        public void Formatter_RobotBusy_UsesShortIntelligibleTemplate()
        {
            RobotVoiceFeedbackTtsFormatter formatter = new();

            TtsFeedbackUtterance utterance = formatter.Format(Message(RobotVoiceFeedbackKind.RobotBusy, "B1", "SELF"));

            Assert.That(utterance.Text, Is.EqualTo("Orden rechazada. Estoy ocupado."));
            Assert.That(utterance.Priority, Is.EqualTo(TtsFeedbackPriority.Critical));
        }

        [Test]
        public void Formatter_Deferral_ReferencesCurrentBox()
        {
            RobotVoiceFeedbackTtsFormatter formatter = new();
            RobotVoiceFeedbackMessage message = new(
                RobotVoiceFeedbackKind.CommandQueuedAsPending,
                string.Empty,
                new RobotVoiceFeedbackContext
                {
                    Kind = RobotVoiceFeedbackKind.CommandQueuedAsPending,
                    TargetAlias = "A1",
                    Destination = "SELF",
                    CurrentTargetAlias = "B2"
                });

            TtsFeedbackUtterance utterance = formatter.Format(message);

            Assert.That(utterance.Text, Is.EqualTo("Orden recibida: la ejecutar\u00e9 cuando termine de depositar la caja B2."));
        }

        [Test]
        public void Formatter_CompletionStatus_UsesContext()
        {
            RobotVoiceFeedbackTtsFormatter formatter = new();

            TtsFeedbackUtterance utterance = formatter.Format(Message(RobotVoiceFeedbackKind.CurrentTaskStatus, "A1", "ZoneA"));

            Assert.That(utterance.Text, Is.EqualTo("Tarea en curso: estoy llevando la caja A1 a la zona A."));
        }

        [Test]
        public void Formatter_Cancel_UsesClosedTemplate()
        {
            RobotVoiceFeedbackTtsFormatter formatter = new();

            TtsFeedbackUtterance utterance = formatter.Format(Message(RobotVoiceFeedbackKind.PendingCommandCancelled));

            Assert.That(utterance.Text, Is.EqualTo("Orden cancelada: detengo la orden pendiente."));
        }

        [Test]
        public void Formatter_StopLatchRejectedPickAndPlace_UsesClosedTemplate()
        {
            RobotVoiceFeedbackTtsFormatter formatter = new();

            TtsFeedbackUtterance utterance = formatter.Format(Message(RobotVoiceFeedbackKind.CommandRejectedStoppedByVoice));

            Assert.That(utterance.Text, Is.EqualTo("Estoy detenido. Di 'contin\u00faa' o 'retoma la tarea' para seguir."));
            Assert.That(utterance.Priority, Is.EqualTo(TtsFeedbackPriority.Critical));
        }

        [Test]
        public void Formatter_AlreadyStopped_UsesExactSpecificTemplate()
        {
            RobotVoiceFeedbackTtsFormatter formatter = new();

            TtsFeedbackUtterance utterance = formatter.Format(Message(RobotVoiceFeedbackKind.AlreadyStopped));

            Assert.That(utterance.Text, Is.EqualTo("Ya estoy parado."));
            Assert.That(utterance.Priority, Is.EqualTo(TtsFeedbackPriority.Critical));
            Assert.That(utterance.Reason, Is.EqualTo("AlreadyStopped"));
        }

        [Test]
        public void Formatter_MissingData_DegradesSafely()
        {
            RobotVoiceFeedbackTtsFormatter formatter = new();

            TtsFeedbackUtterance utterance = formatter.Format(Message(RobotVoiceFeedbackKind.OrderAccepted));

            Assert.That(utterance.Text, Is.EqualTo("Orden aceptada. Voy a por la caja solicitada."));
            Assert.That(utterance.Text, Does.Not.Contain("null"));
        }

        [Test]
        public void AudioClipBackend_NormalizesText_ToDeterministicClipKey()
        {
            string first = RobotVoiceClipTextKey.Normalize(" Orden recibida: reanudo la tarea detenida. ");
            string second = RobotVoiceClipTextKey.Normalize("orden recibida reanudo la tarea detenida");

            Assert.That(first, Is.EqualTo("orden_recibida_reanudo_la_tarea_detenida"));
            Assert.That(second, Is.EqualTo(first));
        }

        [Test]
        public void AudioClipBackend_FindsClip_ByFinalFormattedText()
        {
            GameObject host = new("tts_audio_clip_backend_test");
            try
            {
                AudioSource source = host.AddComponent<AudioSource>();
                AudioClip clip = AudioClip.Create("accepted_a1", 1600, 1, 16000, false);
                RobotVoiceClipEntry entry = new()
                {
                    text = "Orden aceptada. Voy a por la caja A1.",
                    resourcePath = "RobotVoice/es-ES/order_accepted_a1",
                    Clip = clip
                };
                AudioClipTtsSpeechBackend backend = new(source, new[] { entry }, null);

                Assert.That(backend.IsAvailable, Is.True);
                Assert.That(backend.ContainsClipForText("Orden aceptada. Voy a por la caja A1."), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void AudioClipBackend_ManifestResolvesAlreadyStoppedClip_WithoutFallback()
        {
            GameObject host = new("tts_audio_clip_already_stopped_test");
            List<string> events = new();
            try
            {
                AudioSource source = host.AddComponent<AudioSource>();
                AudioClipTtsSpeechBackend backend = new(
                    source,
                    AudioClipTtsSpeechBackend.DefaultManifestResourcePath,
                    (eventType, payload) => events.Add(eventType));

                Assert.That(backend.IsAvailable, Is.True, backend.UnavailableReason);
                Assert.That(backend.ContainsClipForText("Ya estoy parado."), Is.True);
                Assert.That(backend.TrySpeak("Ya estoy parado.", out string failureReason), Is.True, failureReason);
                Assert.That(events, Does.Contain("tts_audio_clip_started"));
                Assert.That(events, Does.Not.Contain("tts_audio_clip_missing"));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void AudioClipBackend_MissingClip_EmitsDiagnostic_AndDoesNotThrow()
        {
            GameObject host = new("tts_audio_clip_missing_test");
            List<string> events = new();
            try
            {
                AudioSource source = host.AddComponent<AudioSource>();
                AudioClip clip = AudioClip.Create("accepted_a1", 1600, 1, 16000, false);
                RobotVoiceClipEntry entry = new()
                {
                    text = "Orden aceptada. Voy a por la caja A1.",
                    Clip = clip
                };
                AudioClipTtsSpeechBackend backend = new(source, new[] { entry }, Capture);

                Assert.DoesNotThrow(() => backend.TrySpeak("Orden rechazada. Estoy ocupado.", out _));
                Assert.That(events, Does.Contain("tts_audio_clip_missing"));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }

            void Capture(string eventType, Dictionary<string, object> payload)
            {
                events.Add(eventType);
            }
        }

        [Test]
        public void BackendSelection_Auto_UsesAudioClips_OnAndroidRuntime()
        {
            Assert.That(
                StructuredTtsFeedbackSink.ResolveBackendKindForPlatform(
                    TtsBackendSelection.Auto,
                    diagnosticModeNoSpeech: false,
                    isAndroidRuntime: true),
                Is.EqualTo(ResolvedTtsBackendKind.AudioClips));
        }

        [Test]
        public void BackendSelection_Auto_KeepsWindowsSapi_OutsideAndroidRuntime()
        {
            Assert.That(
                StructuredTtsFeedbackSink.ResolveBackendKindForPlatform(
                    TtsBackendSelection.Auto,
                    diagnosticModeNoSpeech: false,
                    isAndroidRuntime: false),
                Is.EqualTo(ResolvedTtsBackendKind.WindowsSapi));
        }

        [Test]
        public void BackendSelection_WindowsSapi_UsesAudioClips_OnAndroidRuntime()
        {
            Assert.That(
                StructuredTtsFeedbackSink.ResolveBackendKindForPlatform(
                    TtsBackendSelection.WindowsSapi,
                    diagnosticModeNoSpeech: false,
                    isAndroidRuntime: true),
                Is.EqualTo(ResolvedTtsBackendKind.AudioClips));
        }

        [Test]
        public void Queue_PreservesOrder_AndDeduplicatesWithinCooldown()
        {
            TtsFeedbackQueue queue = new(maxQueueSize: 3, duplicateCooldownSeconds: 2.0);

            Assert.That(queue.TryEnqueue(new TtsFeedbackUtterance("uno", TtsFeedbackPriority.Normal, ""), 0.0, out _), Is.True);
            Assert.That(queue.TryEnqueue(new TtsFeedbackUtterance("uno", TtsFeedbackPriority.Normal, ""), 1.0, out string duplicateReason), Is.False);
            Assert.That(duplicateReason, Is.EqualTo("duplicate_cooldown"));
            Assert.That(queue.TryEnqueue(new TtsFeedbackUtterance("dos", TtsFeedbackPriority.Normal, ""), 1.1, out _), Is.True);

            Assert.That(queue.TryDequeue(out TtsFeedbackUtterance first), Is.True);
            Assert.That(first.Text, Is.EqualTo("uno"));
            Assert.That(queue.TryDequeue(out TtsFeedbackUtterance second), Is.True);
            Assert.That(second.Text, Is.EqualTo("dos"));
        }

        [Test]
        public void Queue_CriticalMessage_DropsOldestNormal_WhenFull()
        {
            TtsFeedbackQueue queue = new(maxQueueSize: 2, duplicateCooldownSeconds: 0.0);

            Assert.That(queue.TryEnqueue(new TtsFeedbackUtterance("normal 1", TtsFeedbackPriority.Normal, ""), 0.0, out _), Is.True);
            Assert.That(queue.TryEnqueue(new TtsFeedbackUtterance("normal 2", TtsFeedbackPriority.Normal, ""), 0.1, out _), Is.True);
            Assert.That(queue.TryEnqueue(new TtsFeedbackUtterance("critico", TtsFeedbackPriority.Critical, ""), 0.2, out string reason), Is.True);
            Assert.That(reason, Is.EqualTo("queued_after_dropping_normal"));

            Assert.That(queue.TryDequeue(out TtsFeedbackUtterance first), Is.True);
            Assert.That(first.Text, Is.EqualTo("normal 2"));
            Assert.That(queue.TryDequeue(out TtsFeedbackUtterance second), Is.True);
            Assert.That(second.Text, Is.EqualTo("critico"));
        }

        [Test]
        public void Sink_Disabled_DoesNotSpeak()
        {
            GameObject host = new("tts_sink_disabled_test");
            try
            {
                StructuredTtsFeedbackSink sink = host.AddComponent<StructuredTtsFeedbackSink>();
                FakeTtsBackend backend = new();
                sink.ConfigureForTests(enabled: false, diagnosticModeNoSpeech: false, maxQueueSize: 4, duplicateCooldownSeconds: 0f, backend);

                sink.Emit(Message(RobotVoiceFeedbackKind.OrderAccepted, "A1", "SELF"));
                sink.PumpForTests();

                Assert.That(backend.Spoken, Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Sink_FallbackUnavailable_DoesNotThrowOrQueue()
        {
            GameObject host = new("tts_sink_unavailable_test");
            try
            {
                StructuredTtsFeedbackSink sink = host.AddComponent<StructuredTtsFeedbackSink>();
                FakeTtsBackend backend = new() { Available = false, Unavailable = "fake_unavailable" };
                sink.ConfigureForTests(enabled: true, diagnosticModeNoSpeech: false, maxQueueSize: 4, duplicateCooldownSeconds: 0f, backend);

                Assert.DoesNotThrow(() => sink.Emit(Message(RobotVoiceFeedbackKind.OrderAccepted, "A1", "SELF")));
                sink.PumpForTests();

                Assert.That(backend.Spoken, Is.Empty);
                Assert.That(sink.PendingCount, Is.EqualTo(0));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Sink_SpeaksQueuedMessagesInOrder_WhenBackendIsFree()
        {
            GameObject host = new("tts_sink_order_test");
            try
            {
                StructuredTtsFeedbackSink sink = host.AddComponent<StructuredTtsFeedbackSink>();
                FakeTtsBackend backend = new();
                sink.ConfigureForTests(enabled: true, diagnosticModeNoSpeech: false, maxQueueSize: 4, duplicateCooldownSeconds: 0f, backend);

                sink.Emit(Message(RobotVoiceFeedbackKind.OrderAccepted, "A1", "SELF"));
                sink.Emit(Message(RobotVoiceFeedbackKind.PendingCommandCancelled));
                sink.PumpForTests();
                sink.PumpForTests();

                Assert.That(backend.Spoken, Has.Count.EqualTo(2));
                Assert.That(backend.Spoken[0], Is.EqualTo("Orden aceptada. Voy a por la caja A1."));
                Assert.That(backend.Spoken[1], Is.EqualTo("Orden cancelada: detengo la orden pendiente."));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Sink_AlreadyStopped_SpeaksExactTextOnce()
        {
            GameObject host = new("tts_sink_already_stopped_test");
            try
            {
                StructuredTtsFeedbackSink sink = host.AddComponent<StructuredTtsFeedbackSink>();
                FakeTtsBackend backend = new();
                sink.ConfigureForTests(enabled: true, diagnosticModeNoSpeech: false, maxQueueSize: 4, duplicateCooldownSeconds: 0f, backend);

                sink.Emit(Message(RobotVoiceFeedbackKind.AlreadyStopped));
                sink.PumpForTests();
                sink.PumpForTests();

                Assert.That(backend.Spoken, Is.EqualTo(new[] { "Ya estoy parado." }));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Sink_EmitsTelemetry_ForSuccessfulSpeechPath()
        {
            GameObject host = new("tts_sink_telemetry_test");
            List<string> events = new();
            TiagoExperimentTelemetry.StructuredEventLogged += Capture;
            try
            {
                StructuredTtsFeedbackSink sink = host.AddComponent<StructuredTtsFeedbackSink>();
                FakeTtsBackend backend = new();
                sink.ConfigureForTests(enabled: true, diagnosticModeNoSpeech: false, maxQueueSize: 4, duplicateCooldownSeconds: 0f, backend);

                sink.Emit(Message(RobotVoiceFeedbackKind.OrderAccepted, "A1", "SELF"));
                sink.PumpForTests();

                Assert.That(events, Does.Contain("tts_feedback_received"));
                Assert.That(events, Does.Contain("tts_condition_gate_evaluated"));
                Assert.That(events, Does.Contain("tts_message_formatted"));
                Assert.That(events, Does.Contain("tts_message_enqueued"));
                Assert.That(events, Does.Contain("tts_speak_requested"));
                Assert.That(events, Does.Contain("tts_speak_completed"));
                Assert.That(backend.Spoken.Single(), Is.EqualTo("Orden aceptada. Voy a por la caja A1."));
            }
            finally
            {
                TiagoExperimentTelemetry.StructuredEventLogged -= Capture;
                Object.DestroyImmediate(host);
            }

            void Capture(string eventType, Dictionary<string, object> payload, float unityTime)
            {
                events.Add(eventType);
            }
        }

        [Test]
        public void Sink_UnavailableBackend_EmitsExplicitTelemetry()
        {
            GameObject host = new("tts_sink_unavailable_telemetry_test");
            List<string> events = new();
            TiagoExperimentTelemetry.StructuredEventLogged += Capture;
            try
            {
                StructuredTtsFeedbackSink sink = host.AddComponent<StructuredTtsFeedbackSink>();
                FakeTtsBackend backend = new() { Available = false, Unavailable = "fake_unavailable" };
                sink.ConfigureForTests(enabled: true, diagnosticModeNoSpeech: false, maxQueueSize: 4, duplicateCooldownSeconds: 0f, backend);

                sink.Emit(Message(RobotVoiceFeedbackKind.OrderAccepted, "A1", "SELF"));
                sink.PumpForTests();

                Assert.That(events, Does.Contain("tts_backend_unavailable"));
                Assert.That(events, Does.Contain("tts_feedback_received"));
                Assert.That(events, Does.Contain("tts_message_formatted"));
                Assert.That(backend.Spoken, Is.Empty);
            }
            finally
            {
                TiagoExperimentTelemetry.StructuredEventLogged -= Capture;
                Object.DestroyImmediate(host);
            }

            void Capture(string eventType, Dictionary<string, object> payload, float unityTime)
            {
                events.Add(eventType);
            }
        }

        [Test]
        public void Sink_DebugSpeakTest_BypassesCommandPipeline_AndSpeaks()
        {
            GameObject host = new("tts_sink_debug_speak_test");
            List<string> events = new();
            TiagoExperimentTelemetry.StructuredEventLogged += Capture;
            try
            {
                StructuredTtsFeedbackSink sink = host.AddComponent<StructuredTtsFeedbackSink>();
                FakeTtsBackend backend = new();
                sink.ConfigureForTests(enabled: true, diagnosticModeNoSpeech: false, maxQueueSize: 4, duplicateCooldownSeconds: 0f, backend);

                sink.DebugSpeakTest();

                Assert.That(backend.Spoken.Single(), Is.EqualTo("Prueba de voz del robot."));
                Assert.That(events, Does.Contain("tts_debug_speak_test_requested"));
                Assert.That(events, Does.Contain("tts_speak_requested"));
                Assert.That(events, Does.Contain("tts_speak_completed"));
            }
            finally
            {
                TiagoExperimentTelemetry.StructuredEventLogged -= Capture;
                Object.DestroyImmediate(host);
            }

            void Capture(string eventType, Dictionary<string, object> payload, float unityTime)
            {
                events.Add(eventType);
            }
        }

        [Test]
        public void Sink_DebugSpanishSpeakTest_UsesSpanishDiagnosticText()
        {
            GameObject host = new("tts_sink_debug_spanish_speak_test");
            List<string> events = new();
            TiagoExperimentTelemetry.StructuredEventLogged += Capture;
            try
            {
                StructuredTtsFeedbackSink sink = host.AddComponent<StructuredTtsFeedbackSink>();
                FakeTtsBackend backend = new();
                sink.ConfigureForTests(enabled: true, diagnosticModeNoSpeech: false, maxQueueSize: 4, duplicateCooldownSeconds: 0f, backend);

                sink.DebugSpeakSpanishTest();

                Assert.That(backend.Spoken.Single(), Is.EqualTo("Prueba de voz del robot en espa\u00f1ol."));
                Assert.That(events, Does.Contain("tts_debug_speak_test_requested"));
                Assert.That(events, Does.Contain("tts_speak_requested"));
                Assert.That(events, Does.Contain("tts_speak_completed"));
            }
            finally
            {
                TiagoExperimentTelemetry.StructuredEventLogged -= Capture;
                Object.DestroyImmediate(host);
            }

            void Capture(string eventType, Dictionary<string, object> payload, float unityTime)
            {
                events.Add(eventType);
            }
        }

        private static RobotVoiceFeedbackMessage Message(
            RobotVoiceFeedbackKind kind,
            string targetAlias = "",
            string destination = "")
        {
            return new RobotVoiceFeedbackMessage(
                kind,
                string.Empty,
                new RobotVoiceFeedbackContext
                {
                    Kind = kind,
                    TargetAlias = targetAlias,
                    Destination = destination
                });
        }

        private sealed class FakeTtsBackend : ITtsSpeechBackend
        {
            public readonly List<string> Spoken = new();

            public bool Available = true;
            public string Unavailable = string.Empty;

            public bool IsAvailable => Available;
            public bool IsSpeaking { get; private set; }
            public string UnavailableReason => Unavailable;

            public void Configure(string languageOrVoice, float volume, float rate)
            {
            }

            public bool TrySpeak(string text, out string failureReason)
            {
                failureReason = Available ? string.Empty : Unavailable;
                if (!Available)
                {
                    return false;
                }

                Spoken.Add(text);
                IsSpeaking = false;
                return true;
            }

            public void Tick()
            {
                IsSpeaking = false;
            }

            public void Stop()
            {
                IsSpeaking = false;
            }
        }
    }
}
