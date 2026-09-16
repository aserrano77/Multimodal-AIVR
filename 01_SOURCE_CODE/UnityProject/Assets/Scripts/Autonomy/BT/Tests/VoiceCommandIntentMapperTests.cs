using Autonomy.Domain;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public sealed class VoiceCommandIntentMapperTests
    {
        private VoiceCommandNormalizer _normalizer;
        private VoiceCommandIntentMapper _mapper;

        [SetUp]
        public void SetUp()
        {
            _normalizer = new VoiceCommandNormalizer();
            _mapper = new VoiceCommandIntentMapper();
        }

        [Test]
        public void Recognized_Pick_Box_A_Maps_To_PickOnly_Intent()
        {
            VoiceCommandIntentMappingResult result = Map("coge la caja A");

            Assert.That(result.Status, Is.EqualTo(VoiceCommandIntentMappingStatus.Mapped));
            Assert.That(result.Error, Is.EqualTo(VoiceCommandIntentMappingError.None));
            Assert.That(result.IntentKind, Is.EqualTo(VoiceCommandIntentKind.Pick));
            Assert.That(result.HasExecutableTaskIntent, Is.True);
            Assert.That(result.TaskIntent.TaskFlow, Is.EqualTo(AutonomousTaskFlow.PickOnly));
            Assert.That(result.TaskIntent.ObjectSelectionMode, Is.EqualTo(MultimodalObjectSelectionMode.Category));
            Assert.That(result.TaskIntent.ObjectCategory, Is.EqualTo("A"));
            Assert.That(result.TaskIntent.PlaceTargetId, Is.Empty);
            Assert.That(result.TaskIntent.Source, Is.EqualTo("voice_command"));
        }

        [Test]
        public void Recognized_Transport_Box_To_Zone_A_Maps_To_PickAndPlace_Intent()
        {
            VoiceCommandIntentMappingResult result = Map("lleva la caja a zona A");

            Assert.That(result.Status, Is.EqualTo(VoiceCommandIntentMappingStatus.Mapped));
            Assert.That(result.Error, Is.EqualTo(VoiceCommandIntentMappingError.None));
            Assert.That(result.IntentKind, Is.EqualTo(VoiceCommandIntentKind.PickAndPlace));
            Assert.That(result.HasExecutableTaskIntent, Is.True);
            Assert.That(result.TaskIntent.TaskFlow, Is.EqualTo(AutonomousTaskFlow.PickAndPlace));
            Assert.That(result.TaskIntent.ObjectSelectionMode, Is.EqualTo(MultimodalObjectSelectionMode.Category));
            Assert.That(result.TaskIntent.ObjectCategory, Is.EqualTo("A"));
            Assert.That(result.TaskIntent.PlaceTargetId, Is.EqualTo("ZoneA"));
            Assert.That(result.TaskIntent.Source, Is.EqualTo("voice_command"));
        }

        [Test]
        public void Recognized_Explicit_Box_Alias_Maps_To_Provisional_TargetId_Intent()
        {
            VoiceCommandIntentMappingResult result = Map("lleva la caja A1 a su zona");

            Assert.That(result.Status, Is.EqualTo(VoiceCommandIntentMappingStatus.Mapped));
            Assert.That(result.TaskIntent.ObjectSelectionMode, Is.EqualTo(MultimodalObjectSelectionMode.ExplicitTargetId));
            Assert.That(result.TaskIntent.TargetId, Is.EqualTo("A1"));
            Assert.That(result.TaskIntent.PlaceTargetId, Is.EqualTo("SELF"));
        }

        [Test]
        public void VoiceTargetResolver_Resolves_Explicit_Alias_To_Technical_Target()
        {
            VoiceCommandNormalizationResult normalization = _normalizer.Normalize("lleva la caja A1 a su zona");
            VoiceCommandIntentMappingResult mapped = _mapper.Map(normalization);

            VoiceTargetResolutionResult result = VoiceTargetResolver.Resolve(mapped.TaskIntent, normalization, Candidates());

            Assert.That(result.Status, Is.EqualTo(VoiceTargetResolutionStatus.Resolved));
            Assert.That(result.ObjectSelectionMode, Is.EqualTo("ExplicitTarget"));
            Assert.That(result.SelectedCandidate.TargetId, Is.EqualTo("round005_box03_A"));
            Assert.That(result.SelectedCandidate.PlaceTargetId, Is.EqualTo("ZoneA"));
            Assert.That(result.CandidateCount, Is.EqualTo(1));
        }

        [Test]
        public void VoiceTargetResolver_Resolves_Explicit_Alias_ForAssignedHeldRoundBox()
        {
            VoiceCommandNormalizationResult normalization = _normalizer.Normalize("lleva la caja C1 a su zona");
            VoiceCommandIntentMappingResult mapped = _mapper.Map(normalization);

            VoiceTargetResolutionResult result = VoiceTargetResolver.Resolve(
                mapped.TaskIntent,
                normalization,
                new[]
                {
                    new VoiceTargetCandidate("round001_box02_C", "C", "C1", "C1", "C1", "ZoneC", isAvailable: true)
                });

            Assert.That(result.Status, Is.EqualTo(VoiceTargetResolutionStatus.Resolved));
            Assert.That(result.SelectedCandidate.TargetId, Is.EqualTo("round001_box02_C"));
            Assert.That(result.SelectedCandidate.PlaceTargetId, Is.EqualTo("ZoneC"));
        }

        [TestCase("lleva la caja A1 a su zona de deposito", "round003_box00_A", "A1", "ZoneA")]
        [TestCase("lleva la caja B2 a su zona de deposito", "round003_box04_B", "B2", "ZoneB")]
        [TestCase("lleva la caja C1 a su zona de deposito", "round003_box02_C", "C1", "ZoneC")]
        [TestCase("LLEVA LA CAJA C1 A SU ZONA DE DEPOSITO", "round003_box02_C", "C1", "ZoneC")]
        [TestCase("lleva la caja C1 a zona C", "round003_box02_C", "C1", "ZoneC")]
        public void VoiceTargetResolver_Resolves_SelfZone_Explicit_Alias_To_Box_Deposit_Zone(
            string transcript,
            string expectedTargetId,
            string expectedAlias,
            string expectedPlaceTargetId)
        {
            VoiceCommandNormalizationResult normalization = _normalizer.Normalize(transcript);
            VoiceCommandIntentMappingResult mapped = _mapper.Map(normalization);

            VoiceTargetResolutionResult result = VoiceTargetResolver.Resolve(
                mapped.TaskIntent,
                normalization,
                Round003Candidates());

            Assert.That(normalization.ObjectLabel, Is.EqualTo(expectedAlias));
            Assert.That(result.Status, Is.EqualTo(VoiceTargetResolutionStatus.Resolved));
            Assert.That(result.SelectedCandidate.TargetId, Is.EqualTo(expectedTargetId));
            Assert.That(result.SelectedCandidate.PlaceTargetId, Is.EqualTo(expectedPlaceTargetId));
        }

        [Test]
        public void Explicit_Wrong_Zone_For_C1_Remains_Explicit_And_Can_Be_Rejected_By_Mismatch_Guard()
        {
            VoiceCommandNormalizationResult normalization = _normalizer.Normalize("lleva la caja C1 a zona A");
            VoiceCommandIntentMappingResult mapped = _mapper.Map(normalization);

            Assert.That(normalization.ObjectLabel, Is.EqualTo("C1"));
            Assert.That(normalization.DestinationLabel, Is.EqualTo("A"));
            Assert.That(mapped.Status, Is.EqualTo(VoiceCommandIntentMappingStatus.Mapped));
            Assert.That(mapped.TaskIntent.TargetId, Is.EqualTo("C1"));
            Assert.That(mapped.TaskIntent.PlaceTargetId, Is.EqualTo("ZoneA"));
        }

        [Test]
        public void VoiceTargetResolver_DoesNotResolve_MissingExplicitAlias_ByCategoryFallback()
        {
            VoiceCommandNormalizationResult normalization = _normalizer.Normalize("lleva la caja C1 a su zona");
            VoiceCommandIntentMappingResult mapped = _mapper.Map(normalization);

            VoiceTargetResolutionResult result = VoiceTargetResolver.Resolve(
                mapped.TaskIntent,
                normalization,
                new[]
                {
                    new VoiceTargetCandidate("round001_box05_C", "C", "C2", "C2", "C2", "ZoneC", isAvailable: true)
                });

            Assert.That(result.Status, Is.EqualTo(VoiceTargetResolutionStatus.NotFound));
            Assert.That(result.Reason, Is.EqualTo("target_alias_not_found"));
        }

        [Test]
        public void VoiceTargetResolver_Detects_Ambiguous_Category()
        {
            VoiceCommandNormalizationResult normalization = _normalizer.Normalize("lleva la caja A a zona A");
            VoiceCommandIntentMappingResult mapped = _mapper.Map(normalization);

            VoiceTargetResolutionResult result = VoiceTargetResolver.Resolve(mapped.TaskIntent, normalization, Candidates());

            Assert.That(result.Status, Is.EqualTo(VoiceTargetResolutionStatus.Ambiguous));
            Assert.That(result.Reason, Is.EqualTo("multiple_matching_boxes"));
            Assert.That(result.CandidateCount, Is.EqualTo(2));
        }

        [Test]
        public void VoiceTargetResolver_Allows_Category_When_Only_One_Candidate_Matches()
        {
            VoiceCommandNormalizationResult normalization = _normalizer.Normalize("lleva la caja B a zona B");
            VoiceCommandIntentMappingResult mapped = _mapper.Map(normalization);

            VoiceTargetResolutionResult result = VoiceTargetResolver.Resolve(mapped.TaskIntent, normalization, Candidates());

            Assert.That(result.Status, Is.EqualTo(VoiceTargetResolutionStatus.Resolved));
            Assert.That(result.ObjectSelectionMode, Is.EqualTo("CategoryUniqueMatch"));
            Assert.That(result.SelectedCandidate.TargetId, Is.EqualTo("round005_box04_B"));
        }

        [TestCase("para")]
        [TestCase("parar")]
        [TestCase("para robot")]
        [TestCase("detente")]
        [TestCase("alto")]
        [TestCase("stop")]
        [TestCase("espera")]
        public void Recognized_Stop_Maps_To_Structured_NonBridge_Stop_Result(string transcript)
        {
            VoiceCommandIntentMappingResult result = Map(transcript);

            Assert.That(result.Status, Is.EqualTo(VoiceCommandIntentMappingStatus.Mapped));
            Assert.That(result.Error, Is.EqualTo(VoiceCommandIntentMappingError.ControlIntentNoBridgeTask));
            Assert.That(result.IntentKind, Is.EqualTo(VoiceCommandIntentKind.Stop));
            Assert.That(result.HasExecutableTaskIntent, Is.False);
            Assert.That(result.TaskIntent, Is.Null);
            Assert.That(result.CandidateDescription, Is.EqualTo("Stop robot"));
        }

        [TestCase("cancela la orden pendiente", VoiceCommandIntentKind.CancelPendingOrder, "Cancel pending order")]
        [TestCase("la la orden pendiente", VoiceCommandIntentKind.CancelPendingOrder, "Cancel pending order")]
        [TestCase("la orden pendiente", VoiceCommandIntentKind.CancelPendingOrder, "Cancel pending order")]
        [TestCase("al orden pendiente", VoiceCommandIntentKind.CancelPendingOrder, "Cancel pending order")]
        [TestCase("cance la orden pendiente", VoiceCommandIntentKind.CancelPendingOrder, "Cancel pending order")]
        [TestCase("cance la lador de impendiente", VoiceCommandIntentKind.CancelPendingOrder, "Cancel pending order")]
        [TestCase("mas cercana a mi", VoiceCommandIntentKind.NearestToUser, "Nearest box to user")]
        [TestCase("más cercana a mí", VoiceCommandIntentKind.NearestToUser, "Nearest box to user")]
        [TestCase("mas cercana al robot", VoiceCommandIntentKind.NearestToRobot, "Nearest box to robot")]
        public void Diagnostic_Control_Commands_Map_To_NonExecutable_Intents(
            string transcript,
            VoiceCommandIntentKind expectedKind,
            string expectedDescription)
        {
            VoiceCommandIntentMappingResult result = Map(transcript);

            Assert.That(result.Status, Is.EqualTo(VoiceCommandIntentMappingStatus.Mapped));
            Assert.That(result.Error, Is.EqualTo(VoiceCommandIntentMappingError.DiagnosticIntentNotExecutable));
            Assert.That(result.IntentKind, Is.EqualTo(expectedKind));
            Assert.That(result.HasExecutableTaskIntent, Is.False);
            Assert.That(result.TaskIntent, Is.Null);
            Assert.That(result.CandidateDescription, Is.EqualTo(expectedDescription));
        }

        [TestCase("reanuda")]
        [TestCase("reanuda la tarea")]
        [TestCase("retoma")]
        [TestCase("retoma la tarea actual")]
        [TestCase("continua")]
        [TestCase("continúa")]
        [TestCase("continua con la tarea")]
        [TestCase("sigue con la tarea")]
        public void Recognized_Resume_Maps_To_Control_Route_Intent(string transcript)
        {
            VoiceCommandIntentMappingResult result = Map(transcript);

            Assert.That(result.Status, Is.EqualTo(VoiceCommandIntentMappingStatus.Mapped));
            Assert.That(result.Error, Is.EqualTo(VoiceCommandIntentMappingError.ControlIntentNoBridgeTask));
            Assert.That(result.IntentKind, Is.EqualTo(VoiceCommandIntentKind.Resume));
            Assert.That(result.HasExecutableTaskIntent, Is.False);
            Assert.That(result.TaskIntent, Is.Null);
            Assert.That(result.CandidateDescription, Is.EqualTo("Resume stopped task"));
        }

        [Test]
        [TestCase("coge la caja más cercana")]
        [TestCase("la caja más cercana")]
        [TestCase("la caza más cercana")]
        [TestCase("la casa más cercana")]
        public void Nearest_Box_Ambiguity_Classifies_As_Diagnostic_Clarification_Required(string transcript)
        {
            VoiceCommandIntentMappingResult result = Map(transcript);

            Assert.That(result.Status, Is.EqualTo(VoiceCommandIntentMappingStatus.Mapped));
            Assert.That(result.Error, Is.EqualTo(VoiceCommandIntentMappingError.DiagnosticIntentNotExecutable));
            Assert.That(result.IntentKind, Is.EqualTo(VoiceCommandIntentKind.NearestAmbiguity));
            Assert.That(result.HasExecutableTaskIntent, Is.False);
            Assert.That(result.TaskIntent, Is.Null);
        }

        [Test]
        public void SingleWordPara_Maps_To_Structured_NonBridge_Stop_Result()
        {
            VoiceCommandIntentMappingResult result = Map("para");

            Assert.That(result.Status, Is.EqualTo(VoiceCommandIntentMappingStatus.Mapped));
            Assert.That(result.Error, Is.EqualTo(VoiceCommandIntentMappingError.ControlIntentNoBridgeTask));
            Assert.That(result.IntentKind, Is.EqualTo(VoiceCommandIntentKind.Stop));
            Assert.That(result.HasExecutableTaskIntent, Is.False);
            Assert.That(result.TaskIntent, Is.Null);
            Assert.That(result.CandidateDescription, Is.EqualTo("Stop robot"));
        }

        [Test]
        [TestCase("coge la caza mas cercana")]
        [TestCase("coge la casa mas cercana")]
        public void P38D_Nearest_Box_Asr_Object_Variants_Classify_As_Diagnostic_Clarification_Required(string transcript)
        {
            VoiceCommandIntentMappingResult result = Map(transcript);

            Assert.That(result.Status, Is.EqualTo(VoiceCommandIntentMappingStatus.Mapped));
            Assert.That(result.Error, Is.EqualTo(VoiceCommandIntentMappingError.DiagnosticIntentNotExecutable));
            Assert.That(result.IntentKind, Is.EqualTo(VoiceCommandIntentKind.NearestAmbiguity));
            Assert.That(result.HasExecutableTaskIntent, Is.False);
            Assert.That(result.TaskIntent, Is.Null);
        }

        [Test]
        [TestCase("gieba la caja a dos a su zona", "A2", "SELF")]
        [TestCase("gueva la caja a dos a su zona", "A2", "SELF")]
        [TestCase("gueba la caja a dos a su zona", "A2", "SELF")]
        [TestCase("debosita la caja C1", "C1", "SELF")]
        [TestCase("de posita la caja C1", "C1", "SELF")]
        public void P38D_Base_Model_Lexical_Variants_Map_To_PickAndPlace(string transcript, string expectedTargetId, string expectedDestination)
        {
            VoiceCommandIntentMappingResult result = Map(transcript);

            Assert.That(result.Status, Is.EqualTo(VoiceCommandIntentMappingStatus.Mapped));
            Assert.That(result.IntentKind, Is.EqualTo(VoiceCommandIntentKind.PickAndPlace));
            Assert.That(result.HasExecutableTaskIntent, Is.True);
            Assert.That(result.TaskIntent.TargetId, Is.EqualTo(expectedTargetId));
            Assert.That(result.TaskIntent.PlaceTargetId, Is.EqualTo(expectedDestination));
        }

        [Test]
        public void P38D_Espeda_Short_Utterance_Maps_To_Stop()
        {
            VoiceCommandIntentMappingResult result = Map("espeda");

            Assert.That(result.Status, Is.EqualTo(VoiceCommandIntentMappingStatus.Mapped));
            Assert.That(result.IntentKind, Is.EqualTo(VoiceCommandIntentKind.Stop));
            Assert.That(result.HasExecutableTaskIntent, Is.False);
        }

        [Test]
        public void Unrecognized_Bada_Does_Not_Create_Executable_Intent()
        {
            VoiceCommandIntentMappingResult result = Map("bada");

            Assert.That(result.Status, Is.EqualTo(VoiceCommandIntentMappingStatus.NotMapped));
            Assert.That(result.Error, Is.EqualTo(VoiceCommandIntentMappingError.NormalizationUnrecognized));
            Assert.That(result.HasExecutableTaskIntent, Is.False);
            Assert.That(result.TaskIntent, Is.Null);
        }

        [Test]
        public void Recognized_But_Incomplete_Transport_Without_Destination_Does_Not_Create_Executable_Intent()
        {
            var normalization = new VoiceCommandNormalizationResult(
                "lleva la caja",
                "lleva la caja",
                "lleva la caja",
                VoiceCommandRecognitionStatus.Recognized,
                0.72f,
                "lleva la caja",
                VoiceCommandActionToken.Transport,
                ObjectToken.Box,
                "A",
                string.Empty,
                new[] { "test correction" },
                string.Empty);

            VoiceCommandIntentMappingResult result = _mapper.Map(normalization);

            Assert.That(result.Status, Is.EqualTo(VoiceCommandIntentMappingStatus.Incomplete));
            Assert.That(result.Error, Is.EqualTo(VoiceCommandIntentMappingError.MissingDestinationLabel));
            Assert.That(result.IntentKind, Is.EqualTo(VoiceCommandIntentKind.PickAndPlace));
            Assert.That(result.HasExecutableTaskIntent, Is.False);
            Assert.That(result.TaskIntent, Is.Null);
        }

        [Test]
        public void Mapping_Preserves_Normalization_Trace_And_Score()
        {
            const string transcript = "coce la caza a.";
            VoiceCommandNormalizationResult normalization = _normalizer.Normalize(transcript);

            VoiceCommandIntentMappingResult result = _mapper.Map(normalization);

            Assert.That(result.RawTranscript, Is.EqualTo(transcript));
            Assert.That(result.CleanedTranscript, Is.EqualTo(normalization.CleanedTranscript));
            Assert.That(result.NormalizedText, Is.EqualTo("coge la caja A"));
            Assert.That(result.NormalizationStatus, Is.EqualTo(VoiceCommandRecognitionStatus.Recognized));
            Assert.That(result.Score, Is.EqualTo(normalization.Score));
            Assert.That(result.CorrectionsApplied, Is.EquivalentTo(normalization.CorrectionsApplied));
            Assert.That(result.AmbiguityReason, Is.EqualTo(normalization.AmbiguityReason));
            Assert.That(result.Normalization, Is.SameAs(normalization));
        }

        private VoiceCommandIntentMappingResult Map(string transcript)
        {
            return _mapper.Map(_normalizer.Normalize(transcript));
        }

        private static VoiceTargetCandidate[] Candidates()
        {
            return new[]
            {
                new VoiceTargetCandidate("round005_box03_A", "A", "A1", "A1", "A1", "ZoneA"),
                new VoiceTargetCandidate("round005_box01_A", "A", "A2", "A2", "A2", "ZoneA"),
                new VoiceTargetCandidate("round005_box04_B", "B", "B1", "B1", "B1", "ZoneB")
            };
        }

        private static VoiceTargetCandidate[] Round003Candidates()
        {
            return new[]
            {
                new VoiceTargetCandidate("round003_box00_A", "A", "A1", "A1", "A1", "ZoneA"),
                new VoiceTargetCandidate("round003_box03_A", "A", "A2", "A2", "A2", "ZoneA"),
                new VoiceTargetCandidate("round003_box01_B", "B", "B1", "B1", "B1", "ZoneB"),
                new VoiceTargetCandidate("round003_box04_B", "B", "B2", "B2", "B2", "ZoneB"),
                new VoiceTargetCandidate("round003_box02_C", "C", "C1", "C1", "C1", "ZoneC")
            };
        }
    }
}
