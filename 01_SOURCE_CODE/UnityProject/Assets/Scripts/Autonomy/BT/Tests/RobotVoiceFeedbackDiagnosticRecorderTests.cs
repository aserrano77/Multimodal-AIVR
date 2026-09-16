using Autonomy.Domain;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public sealed class RobotVoiceFeedbackDiagnosticRecorderTests
    {
        [Test]
        public void DiagnosticRecord_FromMessage_CapturesCoreFeedbackFields()
        {
            RobotVoiceFeedbackMessage message = BuildMessage(
                RobotVoiceFeedbackKind.OrderAccepted,
                "Orden recibida: llevar\u00e9 la caja A1 a la zona A.",
                accepted: true);

            RobotVoiceFeedbackDiagnosticRecord record = RobotVoiceFeedbackDiagnosticRecord.FromMessage(
                message,
                "feedback_diag_test",
                1,
                "test_sink");

            Assert.That(record.FeedbackKind, Is.EqualTo("OrderAccepted"));
            Assert.That(record.FeedbackText, Is.EqualTo("Orden recibida: llevar\u00e9 la caja A1 a la zona A."));
            Assert.That(record.Intent, Is.EqualTo("PickAndPlace"));
            Assert.That(record.TargetAlias, Is.EqualTo("A1"));
            Assert.That(record.Destination, Is.EqualTo("A"));
            Assert.That(record.Accepted, Is.True);
            Assert.That(record.FeedbackOutcome, Is.EqualTo("accepted"));
            Assert.That(record.SinkType, Is.EqualTo("test_sink"));
        }

        [Test]
        public void DiagnosticRecord_CsvAndJsonl_IncludeTraceableFields()
        {
            RobotVoiceFeedbackDiagnosticRecord record = new()
            {
                Timestamp = "2026-06-07T10:00:00.0000000Z",
                RunId = "feedback_diag_test",
                SequenceIndex = 2,
                FeedbackKind = "RobotBusy",
                FeedbackText = "Estoy ocupado ahora.",
                Intent = "PickAndPlace",
                TargetAlias = "B2",
                Destination = "ZoneB",
                Accepted = false,
                SinkType = "debug"
            };

            string csv = record.ToCsvRow();
            string json = record.ToJsonLine();

            Assert.That(RobotVoiceFeedbackDiagnosticRecord.CsvHeader(), Does.Contain("feedback_kind"));
            Assert.That(csv, Does.Contain("\"RobotBusy\""));
            Assert.That(csv, Does.Contain("\"Estoy ocupado ahora.\""));
            Assert.That(csv, Does.Contain("\"B2\""));
            Assert.That(csv, Does.Contain("\"ZoneB\""));
            Assert.That(json, Does.Contain("\"feedback_kind\":\"RobotBusy\""));
            Assert.That(json, Does.Contain("\"feedback_text\":\"Estoy ocupado ahora.\""));
            Assert.That(json, Does.Contain("\"accepted\":false"));
            Assert.That(json, Does.Contain("\"feedback_outcome\":\"rejected\""));
            Assert.That(csv, Does.Contain("\"rejected\""));
        }

        [Test]
        public void SummaryMarkdown_IncludesCountsAndNoFeedbackWarning()
        {
            RobotVoiceFeedbackDiagnosticSummary empty = RobotVoiceFeedbackDiagnosticSummary.Calculate(
                Enumerable.Empty<RobotVoiceFeedbackDiagnosticRecord>(),
                "feedback_diag_empty");

            string emptyMarkdown = empty.ToMarkdown(Enumerable.Empty<RobotVoiceFeedbackDiagnosticRecord>());

            Assert.That(emptyMarkdown, Does.Contain("- run_id: feedback_diag_empty"));
            Assert.That(emptyMarkdown, Does.Contain("- total_feedbacks: 0"));
            Assert.That(emptyMarkdown, Does.Contain("no voice feedback events were emitted in this run"));

            RobotVoiceFeedbackDiagnosticRecord[] records =
            {
                new() { SequenceIndex = 1, FeedbackKind = "OrderAccepted", Intent = "PickAndPlace", TargetAlias = "A1", Destination = "A", FeedbackText = "Orden recibida.", Accepted = true },
                new() { SequenceIndex = 2, FeedbackKind = "RobotBusy", Intent = "PickAndPlace", TargetAlias = "B2", Destination = "B", FeedbackText = "Estoy ocupado.", Accepted = false },
                new() { SequenceIndex = 3, FeedbackKind = "ControlCommandAcknowledged", Intent = "Stop", FeedbackText = "No puedo detener la tarea en este estado.", Accepted = false },
                new() { SequenceIndex = 4, FeedbackKind = "PendingCommandCancelled", Intent = "CancelPendingOrder", FeedbackText = "Cancelo la orden pendiente.", Accepted = false },
                new() { SequenceIndex = 5, FeedbackKind = "NearestReferenceAmbiguous", Intent = "NearestAmbiguity", FeedbackText = "\u00bfM\u00e1s cercana a ti o m\u00e1s cercana a m\u00ed?", Accepted = false }
            };

            RobotVoiceFeedbackDiagnosticSummary summary = RobotVoiceFeedbackDiagnosticSummary.Calculate(records, "feedback_diag_counts");
            string markdown = summary.ToMarkdown(records);

            Assert.That(markdown, Does.Contain("- OrderAccepted: 1"));
            Assert.That(markdown, Does.Contain("- RobotBusy: 1"));
            Assert.That(markdown, Does.Contain("- ControlCommandAcknowledged: 1"));
            Assert.That(markdown, Does.Contain("- PendingCommandCancelled: 1"));
            Assert.That(markdown, Does.Contain("- NearestReferenceAmbiguous: 1"));
            Assert.That(markdown, Does.Contain("- accepted_feedbacks: 1"));
            Assert.That(markdown, Does.Contain("- rejected_feedbacks: 1"));
            Assert.That(markdown, Does.Contain("- acknowledged_feedbacks: 2"));
            Assert.That(markdown, Does.Contain("- clarification_feedbacks: 1"));
            Assert.That(markdown, Does.Contain("| 1 | PickAndPlace | A1 | A | OrderAccepted | Orden recibida. |"));
        }

        [Test]
        public void Recorder_WritesJsonlCsvAndMarkdownArtifacts()
        {
            GameObject host = new("voice_feedback_diag_test");
            RobotVoiceFeedbackDiagnosticRecorder recorder = host.AddComponent<RobotVoiceFeedbackDiagnosticRecorder>();
            SetPrivate(recorder, "_diagnosticModeEnabled", true);

            recorder.Emit(BuildMessage(
                RobotVoiceFeedbackKind.OrderAccepted,
                "Orden recibida: llevar\u00e9 la caja A1 a la zona A.",
                accepted: true));
            recorder.FinalizeRun();

            Assert.That(recorder.Records.Count, Is.EqualTo(1));
            Assert.That(File.Exists(Path.Combine(recorder.RunDirectory, $"voice_feedback_events_{recorder.RunId}.jsonl")), Is.True);
            Assert.That(File.Exists(Path.Combine(recorder.RunDirectory, $"voice_feedback_events_{recorder.RunId}.csv")), Is.True);
            Assert.That(File.Exists(Path.Combine(recorder.RunDirectory, $"voice_feedback_summary_{recorder.RunId}.md")), Is.True);
            Assert.That(File.ReadAllText(recorder.JsonlPath), Does.Contain("\"feedback_kind\":\"OrderAccepted\""));
            Assert.That(File.ReadAllText(recorder.CsvPath), Does.Contain("\"A1\""));
            Assert.That(File.ReadAllText(recorder.SummaryPath), Does.Contain("- OrderAccepted: 1"));

            Object.DestroyImmediate(host);
        }

        private static RobotVoiceFeedbackMessage BuildMessage(
            RobotVoiceFeedbackKind kind,
            string text,
            bool accepted)
        {
            return new RobotVoiceFeedbackMessage(
                kind,
                text,
                new RobotVoiceFeedbackContext
                {
                    Kind = kind,
                    RawTranscript = "lleva la caja A1 a zona A",
                    NormalizedText = "lleva la caja A1 a zona A",
                    IntentKind = VoiceCommandIntentKind.PickAndPlace,
                    TargetAlias = "A1",
                    TargetId = "A1",
                    Destination = "A",
                    Reason = accepted ? "submitted_pick_and_place" : "task_in_progress",
                    Accepted = accepted
                });
        }

        private static void SetPrivate(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(target, value);
        }
    }
}
