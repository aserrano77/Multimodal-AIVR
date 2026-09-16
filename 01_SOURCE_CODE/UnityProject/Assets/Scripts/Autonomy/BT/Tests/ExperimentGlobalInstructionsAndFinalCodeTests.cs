using System;
using System.IO;
using System.Linq;
using Autonomy.UnityIntegration;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    public sealed class ExperimentGlobalInstructionsAndFinalCodeTests
    {
        private string _tempDirectory;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "ExperimentGlobalInstructionsAndFinalCodeTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
            ExperimentDataPathResolver.ResetForTests(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            ExperimentSessionIdHistoryStore.ForceSaveFailureForTests(false);
            ExperimentSessionIdHistoryStore.OverrideDefaultPathForTests(null);
            ExperimentDataPathResolver.ResetForTests(null);
            if (!string.IsNullOrWhiteSpace(_tempDirectory) && Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public void GlobalInstructionsUseSeveralShortQuestReadablePages()
        {
            ExperimentGlobalInstructionsState state = ExperimentGlobalInstructionsState.CreateDefault();

            Assert.That(state.PageCount, Is.EqualTo(6));
            for (int i = 0; i < state.PageCount; i++)
            {
                ExperimentGlobalInstructionPage page = state.CurrentPage;
                Assert.That(page.Title, Is.Not.Empty);
                Assert.That(page.Lines, Has.Count.LessThanOrEqualTo(5));
                Assert.That(page.Body, Does.Not.Contain("C00"));
                Assert.That(page.Body, Does.Not.Contain("C10"));
                Assert.That(page.Body, Does.Not.Contain("C11"));
                state.MoveNext();
            }
        }

        [Test]
        public void GlobalInstructionsUseExitToStartAndSpanishPalesWording()
        {
            string allText = string.Join("\n", ExperimentGlobalInstructionsState.CreateDefaultPages().Select(page => page.Body));

            Assert.That(allText, Does.Contain("Salir al inicio sin guardar"));
            Assert.That(allText, Does.Contain("Guardar y volver al inicio"));
            Assert.That(allText, Does.Contain("palés"));
            Assert.That(allText, Does.Not.Contain("pallets"));
            Assert.That(allText, Does.Not.Contain("cerrar aplicacion"));
        }

        [Test]
        public void ClosedWithoutCompletionDoesNotCreateSavedExitRecoveryPrompt()
        {
            var entries = new[]
            {
                new ExperimentSessionIdHistoryEntry
                {
                    session_id = "S_closed",
                    questionnaire_code = "AB2CD3",
                    status = ExperimentSessionIdHistoryStore.ClosedWithoutCompletionStatus
                }
            };

            bool pending = ExperimentSavedExitResumePromptState.TryFindPendingSession(entries, out _);

            Assert.That(pending, Is.False);
        }

        [Test]
        public void SavedExitStillCreatesRecoveryPrompt()
        {
            var entries = new[]
            {
                new ExperimentSessionIdHistoryEntry
                {
                    session_id = "S_saved",
                    questionnaire_code = "AB2CD3",
                    status = ExperimentSessionIdHistoryStore.SavedExitStatus,
                    saved_condition_id = "C00_robot_off_voice_off",
                    saved_visible_prueba = 1,
                    resume_policy = "condition_start"
                }
            };

            bool pending = ExperimentSavedExitResumePromptState.TryFindPendingSession(entries, out ExperimentSessionIdHistoryEntry pendingEntry);

            Assert.That(pending, Is.True);
            Assert.That(pendingEntry.session_id, Is.EqualTo("S_saved"));
        }

        [Test]
        public void GlobalInstructionsCanAdvanceReturnAndCompleteOnlyOnLastPage()
        {
            ExperimentGlobalInstructionsState state = ExperimentGlobalInstructionsState.CreateDefault();

            Assert.That(state.HasPrevious, Is.False);
            Assert.That(state.HasNext, Is.True);
            Assert.That(state.CanComplete, Is.False);
            Assert.That(state.ResolveWizardButtonLabels(), Is.EqualTo(new[] { "Siguiente" }));

            state.MoveNext();
            Assert.That(state.PageIndex, Is.EqualTo(1));
            Assert.That(state.HasPrevious, Is.True);
            Assert.That(state.ResolveWizardButtonLabels(), Is.EqualTo(new[] { "Anterior", "Siguiente" }));

            state.MovePrevious();
            Assert.That(state.PageIndex, Is.EqualTo(0));

            while (state.HasNext)
            {
                state.MoveNext();
            }

            Assert.That(state.PageIndex, Is.EqualTo(state.PageCount - 1));
            Assert.That(state.CanComplete, Is.True);
            Assert.That(state.ResolveWizardButtonLabels(), Is.EqualTo(new[] { "Anterior", "Entendido / Comenzar" }));
            Assert.That(string.Join("|", state.ResolveWizardButtonLabels()), Does.Not.Contain("Historial"));
        }

        [Test]
        public void GlobalInstructionsNavigationStaysInRangeAndNeverShowsHistoryControl()
        {
            ExperimentGlobalInstructionsState state = ExperimentGlobalInstructionsState.CreateDefault();

            Assert.That(state.MovePrevious(), Is.EqualTo(0));
            Assert.That(state.PageIndex, Is.EqualTo(0));
            Assert.That(string.Join("|", state.ResolveWizardButtonLabels()), Does.Not.Contain("Historial"));
            Assert.That(string.Join("|", state.ResolveWizardButtonLabels()), Does.Not.Contain("sesiones"));
            Assert.That(string.Join("|", state.ResolveWizardButtonLabels()), Does.Not.Contain("IDs"));

            for (int i = 0; i < state.PageCount + 3; i++)
            {
                state.MoveNext();
            }

            Assert.That(state.PageIndex, Is.EqualTo(state.PageCount - 1));
            Assert.That(state.CanComplete, Is.True);
            Assert.That(string.Join("|", state.ResolveWizardButtonLabels()), Is.EqualTo("Anterior|Entendido / Comenzar"));

            for (int i = 0; i < state.PageCount + 3; i++)
            {
                state.MovePrevious();
            }

            Assert.That(state.PageIndex, Is.EqualTo(0));
            Assert.That(state.ResolveWizardButtonLabels(), Is.EqualTo(new[] { "Siguiente" }));
        }

        [Test]
        public void GlobalInstructionsSupportsImmediatePreviousThenNextAlternation()
        {
            ExperimentGlobalInstructionsState state = ExperimentGlobalInstructionsState.CreateDefault();

            Assert.That(state.MoveNext(), Is.EqualTo(1));
            Assert.That(state.MovePrevious(), Is.EqualTo(0));
            Assert.That(state.MoveNext(), Is.EqualTo(1));
            Assert.That(state.MovePrevious(), Is.EqualTo(0));
            Assert.That(state.MoveNext(), Is.EqualTo(1));
            Assert.That(state.PageIndex, Is.EqualTo(1));
            Assert.That(state.ResolveWizardButtonLabels(), Is.EqualTo(new[] { "Anterior", "Siguiente" }));
        }

        [Test]
        public void GlobalInstructionAudioDefinesSixResourceClips()
        {
            Assert.That(ExperimentGlobalInstructionsAudioPlayer.PageCount, Is.EqualTo(6));
            for (int i = 0; i < ExperimentGlobalInstructionsAudioPlayer.PageCount; i++)
            {
                string resourcePath = ExperimentGlobalInstructionsAudioPlayer.ResourcePathForPage(i);
                Assert.That(resourcePath, Does.StartWith("ExperimentInstructions/global_instruction_page_"));
                Assert.That(resourcePath, Does.Not.EndWith(".wav"));

                string projectRelativeWav = Path.Combine("Assets", "Resources", resourcePath + ".wav");
                string fullPath = Path.Combine(Directory.GetCurrentDirectory(), projectRelativeWav.Replace('/', Path.DirectorySeparatorChar));
                Assert.That(File.Exists(fullPath), Is.True, $"Missing generated global instruction audio clip: {projectRelativeWav}");
            }
        }

        [Test]
        public void GlobalInstructionAudioRejectsOutOfRangePageIndex()
        {
            Assert.That(ExperimentGlobalInstructionsAudioPlayer.ClipIdForPage(-1), Is.Empty);
            Assert.That(ExperimentGlobalInstructionsAudioPlayer.ResourcePathForPage(-1), Is.Empty);
            Assert.That(ExperimentGlobalInstructionsAudioPlayer.ClipIdForPage(ExperimentGlobalInstructionsAudioPlayer.PageCount), Is.Empty);
            Assert.That(ExperimentGlobalInstructionsAudioPlayer.ResourcePathForPage(ExperimentGlobalInstructionsAudioPlayer.PageCount), Is.Empty);
        }

        [Test]
        public void SavedExitPromptHasPriorityOverGlobalInstructions()
        {
            ExperimentStartScreenRenderMode mode = ExperimentStartScreenFlowPriority.Resolve(
                showingHistory: false,
                hasPendingSavedExit: true,
                showingGlobalInstructions: true);

            Assert.That(mode, Is.EqualTo(ExperimentStartScreenRenderMode.SavedExitPrompt));
        }

        [Test]
        public void FinalQuestionnaireScreenModelUsesGenericQuestionnaireInstructionAndSixCharacterCode()
        {
            var model = new ExperimentFinalQuestionnaireCompletionModel(
                "S20260707_120000",
                "AB2CD3",
                3,
                2,
                ExperimentSessionIdHistoryStore.CompletedStatus);

            Assert.That(model.Title, Is.EqualTo("Experimento completado"));
            Assert.That(model.PrimaryInstruction, Is.EqualTo("Introduce este codigo en el cuestionario final."));
            Assert.That(model.PrimaryInstruction, Does.Not.Contain("Google Forms"));
            Assert.That(model.QuestionnaireCode, Has.Length.EqualTo(6));
            Assert.That(model.HasValidQuestionnaireCode, Is.True);
        }

        [Test]
        public void FinalCompletionUsesExistingQuestionnaireCodeAndDoesNotCreateNewHistoryEntry()
        {
            ExperimentDataPathResolver.SessionContext session = ConfigureStartedSession("U20260707_130000", "S20260707_130000");
            string existingCode = session.QuestionnaireCode;

            var model = new ExperimentFinalQuestionnaireCompletionModel(
                session.SessionId,
                existingCode,
                3,
                2,
                ExperimentSessionIdHistoryStore.CompletedStatus);

            Assert.That(model.SessionId, Is.EqualTo(session.SessionId));
            Assert.That(model.QuestionnaireCode, Is.EqualTo(existingCode));
            Assert.That(ExperimentSessionIdHistoryStore.Load(ExperimentSessionIdHistoryStore.DefaultPath), Has.Count.EqualTo(1));
        }

        [Test]
        public void QuestionnaireAcknowledgementPersistsWithoutLosingCompletedCode()
        {
            ExperimentDataPathResolver.SessionContext session = ConfigureStartedSession("U20260707_140000", "S20260707_140000");
            string existingCode = session.QuestionnaireCode;

            ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.CompletedStatus);
            ExperimentSessionIdHistoryStore.MarkQuestionnaireCodeAcknowledged(session.SessionId);

            ExperimentSessionIdHistoryEntry entry = ExperimentSessionIdHistoryStore.Load(ExperimentSessionIdHistoryStore.DefaultPath)[0];
            Assert.That(entry.session_id, Is.EqualTo(session.SessionId));
            Assert.That(entry.questionnaire_code, Is.EqualTo(existingCode));
            Assert.That(entry.status, Is.EqualTo(ExperimentSessionIdHistoryStore.CompletedStatus));
            Assert.That(entry.questionnaire_code_acknowledged, Is.True);
            Assert.That(entry.questionnaire_code_acknowledged_at_iso, Is.Not.Empty);
        }

        [Test]
        public void CompletedAcknowledgedSessionIsNotTreatedAsIncompleteSavedExit()
        {
            ExperimentDataPathResolver.SessionContext session = ConfigureStartedSession("U20260707_150000", "S20260707_150000");
            ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.CompletedStatus);
            ExperimentSessionIdHistoryStore.MarkQuestionnaireCodeAcknowledged(session.SessionId);

            bool pending = ExperimentSavedExitResumePromptState.TryFindPendingSession(
                ExperimentSessionIdHistoryStore.Load(ExperimentSessionIdHistoryStore.DefaultPath),
                out _);

            Assert.That(pending, Is.False);
        }

        private static ExperimentDataPathResolver.SessionContext ConfigureStartedSession(string participantId, string sessionId)
        {
            return ExperimentDataPathResolver.ConfigureSession(
                participantId,
                sessionId,
                new[] { "C00_robot_off_voice_off", "C10_robot_on_voice_off", "C11_robot_on_voice_on" },
                2);
        }
    }
}
