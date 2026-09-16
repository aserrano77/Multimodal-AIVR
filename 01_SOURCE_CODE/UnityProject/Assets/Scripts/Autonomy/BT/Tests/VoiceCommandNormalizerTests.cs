using Autonomy.Domain;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public sealed class VoiceCommandNormalizerTests
    {
        private VoiceCommandNormalizer _normalizer;

        [SetUp]
        public void SetUp()
        {
            _normalizer = new VoiceCommandNormalizer();
        }

        [Test]
        public void Whisper_Caza_Eh_Normalizes_To_Pick_Box_A()
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize("coge la caza, ¿eh?");

            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.Action, Is.EqualTo(VoiceCommandActionToken.Pick));
            Assert.That(result.Object, Is.EqualTo(ObjectToken.Box));
            Assert.That(result.ObjectLabel, Is.EqualTo("A"));
            Assert.That(result.NormalizedText, Is.EqualTo("coge la caja A"));
            Assert.That(result.Score, Is.GreaterThanOrEqualTo(0.7f));
        }

        [Test]
        public void Coce_Casa_Ah_Normalizes_To_Pick_Box_A()
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize("coce la casa, ah");

            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.Action, Is.EqualTo(VoiceCommandActionToken.Pick));
            Assert.That(result.Object, Is.EqualTo(ObjectToken.Box));
            Assert.That(result.ObjectLabel, Is.EqualTo("A"));
            Assert.That(result.NormalizedText, Is.EqualTo("coge la caja A"));
            Assert.That(result.CorrectionsApplied, Does.Contain("action alias: 'coce' -> 'coge'"));
            Assert.That(result.CorrectionsApplied, Does.Contain("object alias: 'casa' -> 'caja'"));
        }

        [Test]
        public void Whisper_Bosito_A_Normalizes_To_Transport_Box_To_Zone_A()
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize("lleva la caja a la zona de bósito, ¿a?");

            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.Action, Is.EqualTo(VoiceCommandActionToken.Transport));
            Assert.That(result.Object, Is.EqualTo(ObjectToken.Box));
            Assert.That(result.DestinationLabel, Is.EqualTo("A"));
            Assert.That(result.NormalizedText, Is.EqualTo("lleva la caja a zona A"));
            Assert.That(result.CorrectionsApplied, Does.Contain("destination alias: 'bosito' -> 'deposito'"));
        }

        [TestCase("recoge la caja A", VoiceCommandActionToken.Pick, "recoge la caja A")]
        [TestCase("deposita la caja en zona A", VoiceCommandActionToken.Transport, "deposita la caja a zona A")]
        [TestCase("toma el paquete B", VoiceCommandActionToken.Pick, "toma la caja B")]
        [TestCase("deja la caja en depósito C", VoiceCommandActionToken.Transport, "deja la caja a zona C")]
        public void Canonical_And_Alias_Commands_Are_Recognized(string transcript, VoiceCommandActionToken expectedAction, string expectedNormalizedText)
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize(transcript);

            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.Action, Is.EqualTo(expectedAction));
            Assert.That(result.Object, Is.EqualTo(ObjectToken.Box));
            Assert.That(result.NormalizedText, Is.EqualTo(expectedNormalizedText));
        }

        [TestCase("para")]
        [TestCase("parar")]
        [TestCase("para robot")]
        [TestCase("para la tarea")]
        [TestCase("detente")]
        [TestCase("alto")]
        [TestCase("alto robot")]
        [TestCase("stop")]
        [TestCase("stop robot")]
        [TestCase("cancela la tarea")]
        [TestCase("pausa seguridad")]
        public void Explicit_Stop_Commands_Are_Recognized(string transcript)
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize(transcript);

            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.Action, Is.EqualTo(VoiceCommandActionToken.Stop));
            Assert.That(result.Score, Is.GreaterThanOrEqualTo(0.9f));
        }

        [Test]
        public void Bada_Does_Not_Become_High_Confidence_Stop()
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize("¡Bada!");

            Assert.That(result.Action, Is.Not.EqualTo(VoiceCommandActionToken.Stop));
            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Unrecognized));
            Assert.That(result.Score, Is.LessThan(0.6f));
        }

        [Test]
        public void Gente_Does_Not_Become_Stop()
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize("gente.");

            Assert.That(result.Action, Is.Not.EqualTo(VoiceCommandActionToken.Stop));
            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Unrecognized));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("¿eh? ah por favor")]
        public void Empty_Or_Noise_Is_Unrecognized(string transcript)
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize(transcript);

            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Unrecognized));
            Assert.That(result.Action, Is.EqualTo(VoiceCommandActionToken.Unknown));
        }

        [Test]
        public void Single_Word_Para_Is_Recognized_As_Stop()
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize("para");

            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.Action, Is.EqualTo(VoiceCommandActionToken.Stop));
            Assert.That(result.Score, Is.GreaterThanOrEqualTo(0.9f));
        }

        [TestCase("reanuda")]
        [TestCase("reanuda la tarea")]
        [TestCase("reanuda la tarea actual")]
        [TestCase("retoma")]
        [TestCase("retoma la tarea")]
        [TestCase("retoma la tarea actual")]
        [TestCase("continúa")]
        [TestCase("continua")]
        [TestCase("continúa la tarea")]
        [TestCase("continua la tarea")]
        [TestCase("continúa con la tarea")]
        [TestCase("continua con la tarea")]
        [TestCase("sigue")]
        [TestCase("sigue con la tarea")]
        public void Explicit_Resume_Commands_Are_Recognized(string transcript)
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize(transcript);

            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.Action, Is.EqualTo(VoiceCommandActionToken.Resume));
            Assert.That(result.CanonicalPhrase, Is.EqualTo("reanuda la tarea"));
            Assert.That(result.Score, Is.GreaterThanOrEqualTo(0.9f));
        }

        [TestCase("vamos")]
        [TestCase("adelante")]
        [TestCase("hazlo")]
        [TestCase("termina")]
        [TestCase("acaba")]
        [TestCase("suelta")]
        [TestCase("déjalo")]
        [TestCase("prosigue")]
        public void Ambiguous_Continuation_Words_Are_Not_Recognized_As_Resume(string transcript)
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize(transcript);

            Assert.That(result.Action, Is.Not.EqualTo(VoiceCommandActionToken.Resume));
        }

        [TestCase("coge la caja ah", "A")]
        [TestCase("recoge la caja eh", "A")]
        [TestCase("toma la caja C", "C")]
        public void Object_Label_Is_Preserved_Or_Inferred_Prudently(string transcript, string expectedLabel)
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize(transcript);

            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.ObjectLabel, Is.EqualTo(expectedLabel));
        }

        [Test]
        public void Quest_Compact_Cofilacaza_Is_Reconstructed_As_Pick_Box_A_With_Penalty()
        {
            const string transcript = "cofilacaza a.";

            VoiceCommandNormalizationResult result = _normalizer.Normalize(transcript);

            Assert.That(result.RawTranscript, Is.EqualTo(transcript));
            Assert.That(result.CleanedTranscript, Is.EqualTo("cofilacaza a"));
            Assert.That(result.NormalizedText, Is.EqualTo("coge la caja A"));
            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.Action, Is.EqualTo(VoiceCommandActionToken.Pick));
            Assert.That(result.Object, Is.EqualTo(ObjectToken.Box));
            Assert.That(result.ObjectLabel, Is.EqualTo("A"));
            Assert.That(result.Score, Is.InRange(0.65f, 0.85f));
            Assert.That(result.CorrectionsApplied, Does.Contain("strong reconstruction: 'cofilacaza' -> 'coge la caja'"));
        }

        [Test]
        public void Quest_Cafe_Is_Contextual_Pick_Box_A_With_Correction()
        {
            const string transcript = "coge la caf\u00e9";

            VoiceCommandNormalizationResult result = _normalizer.Normalize(transcript);

            Assert.That(result.RawTranscript, Is.EqualTo(transcript));
            Assert.That(result.CleanedTranscript, Is.EqualTo("coge la cafe"));
            Assert.That(result.NormalizedText, Is.EqualTo("coge la caja A"));
            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.Action, Is.EqualTo(VoiceCommandActionToken.Pick));
            Assert.That(result.Object, Is.EqualTo(ObjectToken.Box));
            Assert.That(result.ObjectLabel, Is.EqualTo("A"));
            Assert.That(result.Score, Is.InRange(0.7f, 0.9f));
            Assert.That(result.CorrectionsApplied, Does.Contain("contextual object alias: 'cafe' -> 'caja A'"));
        }

        [Test]
        public void Quest_Debo_Casa_Zona_A_Is_Ambiguous_Transport_Not_Perfect()
        {
            const string transcript = "Debo a la casa, a la zona, a.";

            VoiceCommandNormalizationResult result = _normalizer.Normalize(transcript);

            Assert.That(result.RawTranscript, Is.EqualTo(transcript));
            Assert.That(result.CleanedTranscript, Is.EqualTo("debo a la casa a la zona a"));
            Assert.That(result.NormalizedText, Is.EqualTo("lleva la caja a zona A"));
            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Ambiguous));
            Assert.That(result.Action, Is.EqualTo(VoiceCommandActionToken.Transport));
            Assert.That(result.Object, Is.EqualTo(ObjectToken.Box));
            Assert.That(result.DestinationLabel, Is.EqualTo("A"));
            Assert.That(result.Score, Is.LessThan(0.7f));
            Assert.That(result.CorrectionsApplied, Does.Contain("contextual action alias: 'debo' -> 'lleva'"));
            Assert.That(result.CorrectionsApplied, Does.Contain("object alias: 'casa' -> 'caja'"));
            Assert.That(result.AmbiguityReason, Does.Contain("weak contextual ASR evidence"));
        }

        [Test]
        public void Quest_Lleva_Caza_Bosito_A_Still_Normalizes_To_Transport_Box_To_Zone_A()
        {
            const string transcript = "lleva la caza a la zona de de b\u00f3sito a";

            VoiceCommandNormalizationResult result = _normalizer.Normalize(transcript);

            Assert.That(result.RawTranscript, Is.EqualTo(transcript));
            Assert.That(result.CleanedTranscript, Is.EqualTo("lleva la caza a la zona de de bosito a"));
            Assert.That(result.NormalizedText, Is.EqualTo("lleva la caja a zona A"));
            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.Action, Is.EqualTo(VoiceCommandActionToken.Transport));
            Assert.That(result.Object, Is.EqualTo(ObjectToken.Box));
            Assert.That(result.DestinationLabel, Is.EqualTo("A"));
            Assert.That(result.Score, Is.InRange(0.7f, 0.95f));
            Assert.That(result.CorrectionsApplied, Does.Contain("object alias: 'caza' -> 'caja'"));
            Assert.That(result.CorrectionsApplied, Does.Contain("destination alias: 'bosito' -> 'deposito'"));
        }

        [TestCase("lleva la caja A1 a su zona", "A1", "SELF", "lleva la caja A1 a su zona")]
        [TestCase("lleva A1 a su zona", "A1", "SELF", "lleva la caja A1 a su zona")]
        [TestCase("lleva la caja a uno a su zona", "A1", "SELF", "lleva la caja A1 a su zona")]
        [TestCase("lleva la caja a dos a su zona", "A2", "SELF", "lleva la caja A2 a su zona")]
        [TestCase("gieba la caja a dos a su zona", "A2", "SELF", "lleva la caja A2 a su zona")]
        [TestCase("gieva la caja a dos a su zona", "A2", "SELF", "lleva la caja A2 a su zona")]
        [TestCase("gueva la caja a dos a su zona", "A2", "SELF", "lleva la caja A2 a su zona")]
        [TestCase("gueba la caja a dos a su zona", "A2", "SELF", "lleva la caja A2 a su zona")]
        [TestCase("lleva la caja be uno a su zona", "B1", "SELF", "lleva la caja B1 a su zona")]
        [TestCase("lleva la caja b uno a su zona", "B1", "SELF", "lleva la caja B1 a su zona")]
        [TestCase("lleva la caja ve uno a su zona", "B1", "SELF", "lleva la caja B1 a su zona")]
        [TestCase("lleva la caja veo uno a su zona", "B1", "SELF", "lleva la caja B1 a su zona")]
        [TestCase("lleva la caja ce uno a su zona", "C1", "SELF", "lleva la caja C1 a su zona")]
        [TestCase("lleva la caja c uno a su zona", "C1", "SELF", "lleva la caja C1 a su zona")]
        [TestCase("lleva la caja see uno a su zona", "C1", "SELF", "lleva la caja C1 a su zona")]
        [TestCase("lleva la caja A1 a su zona de deposito", "A1", "SELF", "lleva la caja A1 a su zona")]
        [TestCase("LLEVA LA CAJA B2 A SU ZONA DE DEPOSITO", "B2", "SELF", "lleva la caja B2 a su zona")]
        [TestCase("lleva la caja C1 a su zona de deposito", "C1", "SELF", "lleva la caja C1 a su zona")]
        [TestCase("lleva la caja C1 a la zona correspondiente", "C1", "SELF", "lleva la caja C1 a su zona")]
        [TestCase("lleva la caja C1 a su deposito", "C1", "SELF", "lleva la caja C1 a su zona")]
        [TestCase("mueve la caza de uno", "B1", "SELF", "mueve la caja B1 a su zona")]
        [TestCase("deposita la caza fe uno", "C1", "SELF", "deposita la caja C1 a su zona")]
        [TestCase("debosita la caja C1", "C1", "SELF", "deposita la caja C1 a su zona")]
        [TestCase("de posita la caja C1", "C1", "SELF", "deposita la caja C1 a su zona")]
        [TestCase("deposíta la caja C1", "C1", "SELF", "deposita la caja C1 a su zona")]
        [TestCase("mueve la caja A1", "A1", "SELF", "mueve la caja A1 a su zona")]
        [TestCase("deposita la caja A1", "A1", "SELF", "deposita la caja A1 a su zona")]
        [TestCase("lleva la caja 1", "1", "SELF", "lleva la caja 1 a su zona")]
        [TestCase("lleva la caja numero 1", "1", "SELF", "lleva la caja 1 a su zona")]
        [TestCase("lleva la caja A1 a la zona A", "A1", "A", "lleva la caja A1 a zona A")]
        [TestCase("lleva la caja A1 a la zona B", "A1", "B", "lleva la caja A1 a zona B")]
        [TestCase("lleva la caja C1 a zona C", "C1", "C", "lleva la caja C1 a zona C")]
        [TestCase("lleva la caja C1 a zona A", "C1", "A", "lleva la caja C1 a zona A")]
        public void Explicit_Box_Alias_Transport_Commands_Are_Recognized(
            string transcript,
            string expectedObjectLabel,
            string expectedDestination,
            string expectedNormalizedText)
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize(transcript);

            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.Action, Is.EqualTo(VoiceCommandActionToken.Transport));
            Assert.That(result.Object, Is.EqualTo(ObjectToken.Box));
            Assert.That(result.ObjectLabel, Is.EqualTo(expectedObjectLabel));
            Assert.That(result.DestinationLabel, Is.EqualTo(expectedDestination));
            Assert.That(result.NormalizedText, Is.EqualTo(expectedNormalizedText));
        }

        [TestCase("lleva la casa A1 a su zona")]
        [TestCase("lleva la caza A1 a su zona")]
        public void Transport_Context_Normalizes_Casa_Caza_To_Caja_For_Explicit_Alias(string transcript)
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize(transcript);

            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.Object, Is.EqualTo(ObjectToken.Box));
            Assert.That(result.ObjectLabel, Is.EqualTo("A1"));
            Assert.That(result.NormalizedText, Is.EqualTo("lleva la caja A1 a su zona"));
        }

        [Test]
        public void Spoken_Number_Without_Box_Context_Does_Not_Become_Box_Alias()
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize("uno");

            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Unrecognized));
            Assert.That(result.ObjectLabel, Is.Empty);
        }

        [Test]
        public void Quest_Para_Remains_Recognized()
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize("para.");

            Assert.That(result.RawTranscript, Is.EqualTo("para."));
            Assert.That(result.CleanedTranscript, Is.EqualTo("para"));
            Assert.That(result.NormalizedText, Is.EqualTo("para"));
            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.Action, Is.EqualTo(VoiceCommandActionToken.Stop));
            Assert.That(result.Score, Is.GreaterThanOrEqualTo(0.9f));
            Assert.That(result.AmbiguityReason, Is.Empty);
        }

        [Test]
        public void Quest_Para_Robot_Remains_Recognized()
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize("para robot.");

            Assert.That(result.RawTranscript, Is.EqualTo("para robot."));
            Assert.That(result.CleanedTranscript, Is.EqualTo("para robot"));
            Assert.That(result.NormalizedText, Is.EqualTo("para robot"));
            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.Action, Is.EqualTo(VoiceCommandActionToken.Stop));
            Assert.That(result.Score, Is.GreaterThanOrEqualTo(0.9f));
        }

        [Test]
        public void Quest_De_Tente_Normalizes_Prudently_To_Detente()
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize("de tente.");

            Assert.That(result.RawTranscript, Is.EqualTo("de tente."));
            Assert.That(result.CleanedTranscript, Is.EqualTo("de tente"));
            Assert.That(result.NormalizedText, Is.EqualTo("detente"));
            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.Action, Is.EqualTo(VoiceCommandActionToken.Stop));
            Assert.That(result.Score, Is.InRange(0.8f, 0.9f));
            Assert.That(result.CorrectionsApplied, Does.Contain("phonetic stop alias: 'de tente' -> 'detente'"));
        }

        [Test]
        public void P38D_Espeda_Short_Utterance_Normalizes_To_Stop()
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize("Espeda.");

            Assert.That(result.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.Action, Is.EqualTo(VoiceCommandActionToken.Stop));
            Assert.That(result.NormalizedText, Is.EqualTo("espera"));
            Assert.That(result.CorrectionsApplied, Does.Contain("phonetic stop alias: 'espeda' -> 'espera'"));
        }

        [Test]
        public void P38D_Espeda_Long_Utterance_Does_Not_Become_Stop()
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize("espeda la caja A1");

            Assert.That(result.Action, Is.Not.EqualTo(VoiceCommandActionToken.Stop));
        }

        [TestCase("coge la caja mas cercana", "coge la caja mas cercana")]
        [TestCase("coge la caza mas cercana", "coge la caja mas cercana")]
        [TestCase("coge la casa mas cercana", "coge la caja mas cercana")]
        [TestCase("la caja mas cercana", "la caja mas cercana")]
        [TestCase("la caza mas cercana", "la caja mas cercana")]
        [TestCase("la casa mas cercana", "la caja mas cercana")]
        public void P38D_Nearest_Ambiguity_Normalization_Preserves_Mas_Cercana(string transcript, string expectedNormalizedText)
        {
            VoiceCommandNormalizationResult result = _normalizer.Normalize(transcript);

            Assert.That(result.NormalizedText, Is.EqualTo(expectedNormalizedText));
            Assert.That(result.NormalizedText, Does.Contain("mas cercana"));
            Assert.That(result.Action, Is.EqualTo(VoiceCommandActionToken.Unknown));
            Assert.That(result.Object, Is.EqualTo(ObjectToken.Box));
        }
    }
}
