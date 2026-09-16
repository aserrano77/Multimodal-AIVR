using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.XR;

namespace Autonomy.BT.Tests
{
    public sealed class ExperimentPauseMenuStateMachineTests
    {
        private string _tempDirectory;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "ExperimentPauseMenuStateMachineTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
            ExperimentDataPathResolver.ResetForTests(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            ExperimentSessionIdHistoryStore.ForceSaveFailureForTests(false);
            ExperimentSessionIdHistoryStore.OverrideQuestionnaireCodeGeneratorForTests(null);
            ExperimentSessionIdHistoryStore.OverrideDefaultPathForTests(null);
            ExperimentDataPathResolver.ResetForTests(null);
            if (!string.IsNullOrWhiteSpace(_tempDirectory) && Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public void YButtonUsesRisingEdgeAndDoesNotRepeatWhileHeld()
        {
            var state = new ExperimentPauseMenuStateMachine();

            ExperimentPauseMenuYButtonResult first = state.UpdateYPressed(true);
            ExperimentPauseMenuYButtonResult held = state.UpdateYPressed(true);
            ExperimentPauseMenuYButtonResult released = state.UpdateYPressed(false);
            ExperimentPauseMenuYButtonResult second = state.UpdateYPressed(true);

            Assert.That(first.RisingEdge, Is.True);
            Assert.That(first.Command, Is.EqualTo(ExperimentPauseMenuCommand.OpenPause));
            Assert.That(held.RisingEdge, Is.False);
            Assert.That(held.Command, Is.EqualTo(ExperimentPauseMenuCommand.None));
            Assert.That(released.Command, Is.EqualTo(ExperimentPauseMenuCommand.None));
            Assert.That(second.RisingEdge, Is.True);
            Assert.That(second.Command, Is.EqualTo(ExperimentPauseMenuCommand.OpenPause));
        }

        [Test]
        public void ContinueClosesPauseWithoutMutatingSessionContextOrHistory()
        {
            ExperimentDataPathResolver.SessionContext before = ConfigureStartedSession("U20260705_120000", "S20260705_120000");
            var state = new ExperimentPauseMenuStateMachine();

            state.Open();
            state.Continue();

            ExperimentDataPathResolver.SessionContext after = ExperimentDataPathResolver.CurrentSession;
            Assert.That(state.IsPaused, Is.False);
            Assert.That(after.ParticipantId, Is.EqualTo(before.ParticipantId));
            Assert.That(after.SessionId, Is.EqualTo(before.SessionId));
            Assert.That(after.QuestionnaireCode, Is.EqualTo(before.QuestionnaireCode));
            Assert.That(after.QuestionnaireCodeScheme, Is.EqualTo(before.QuestionnaireCodeScheme));
            Assert.That(ExperimentSessionIdHistoryStore.Load(ExperimentSessionIdHistoryStore.DefaultPath)[0].status, Is.EqualTo(ExperimentSessionIdHistoryStore.StartedStatus));
        }

        [Test]
        public void RestartSessionMarksPreviousAttemptRestartedAndCreatesTraceableNewAttempt()
        {
            ExperimentDataPathResolver.SessionContext first = ConfigureStartedSession("U20260705_121000", "S20260705_121000");

            ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.RestartedStatus);
            ExperimentDataPathResolver.PrepareForNewSessionStart();
            ExperimentDataPathResolver.SessionContext second = ExperimentDataPathResolver.ConfigureSession(
                first.ParticipantId,
                string.Empty,
                new[] { "C00_robot_off_voice_off", "C10_robot_on_voice_off", "C11_robot_on_voice_on" },
                2);

            Assert.That(second.ParticipantId, Does.StartWith(first.ParticipantId));
            Assert.That(second.SessionId, Is.Not.EqualTo(first.SessionId));
            Assert.That(second.QuestionnaireCode, Is.Not.EqualTo(first.QuestionnaireCode));
            Assert.That(second.QuestionnaireCodeScheme, Is.EqualTo(QuestionnaireCodeCodec.Scheme));
            Assert.That(QuestionnaireCodeCodec.TryDecodeOrder(second.QuestionnaireCode, out string[] decodedOrder, out _, out string decodeError), Is.True, decodeError);
            Assert.That(decodedOrder, Is.EqualTo(second.ConditionOrder));
            var entries = ExperimentSessionIdHistoryStore.Load(ExperimentSessionIdHistoryStore.DefaultPath);
            Assert.That(entries, Has.Count.EqualTo(2));
            Assert.That(entries.Exists(entry => entry.session_id == first.SessionId && entry.status == ExperimentSessionIdHistoryStore.RestartedStatus), Is.True);
            Assert.That(entries.Exists(entry => entry.session_id == second.SessionId && entry.status == ExperimentSessionIdHistoryStore.StartedStatus), Is.True);
        }

        [Test]
        public void SaveAndExitMarksCurrentSessionSavedExit()
        {
            ExperimentDataPathResolver.SessionContext session = ConfigureStartedSession("U20260705_122000", "S20260705_122000");

            ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.SavedExitStatus);

            var entries = ExperimentSessionIdHistoryStore.Load(ExperimentSessionIdHistoryStore.DefaultPath);
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].session_id, Is.EqualTo(session.SessionId));
            Assert.That(entries[0].status, Is.EqualTo(ExperimentSessionIdHistoryStore.SavedExitStatus));
        }

        [Test]
        public void SaveAndExitCheckpointPersistsConditionStartResumeMetadata()
        {
            ExperimentDataPathResolver.SessionContext session = ConfigureStartedSession("U20260706_160000", "S20260706_160000");

            ExperimentSessionIdHistoryStore.UpdateSavedExitCheckpoint(
                session.SessionId,
                "C10_robot_on_voice_off",
                2,
                1,
                "C00_robot_off_voice_off,C10_robot_on_voice_off,C11_robot_on_voice_on",
                "condition_start");
            ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.SavedExitStatus);

            ExperimentSessionIdHistoryEntry entry = ExperimentSessionIdHistoryStore.Load(ExperimentSessionIdHistoryStore.DefaultPath)[0];
            Assert.That(entry.status, Is.EqualTo(ExperimentSessionIdHistoryStore.SavedExitStatus));
            Assert.That(entry.saved_condition_id, Is.EqualTo("C10_robot_on_voice_off"));
            Assert.That(entry.saved_visible_prueba, Is.EqualTo(2));
            Assert.That(entry.saved_round_index, Is.EqualTo(1));
            Assert.That(entry.resume_policy, Is.EqualTo("condition_start"));
        }

        [Test]
        public void SavedExitPromptBuildsContinueFromSavedPruebaLabel()
        {
            var entry = new ExperimentSessionIdHistoryEntry
            {
                status = ExperimentSessionIdHistoryStore.SavedExitStatus,
                saved_condition_id = "C10_robot_on_voice_off",
                saved_visible_prueba = 2,
                resume_policy = "condition_start"
            };

            Assert.That(ExperimentSavedExitResumePromptState.HasValidCheckpoint(entry), Is.True);
            Assert.That(ExperimentSavedExitResumePromptState.BuildResumeButtonLabel(entry), Is.EqualTo("Continuar desde Prueba 2"));
            Assert.That(ExperimentSavedExitResumePromptState.BuildCheckpointDescription(entry), Is.EqualTo("Prueba 2"));
            Assert.That(ExperimentSavedExitResumePromptState.ToParticipantDisplayStatus(entry.status), Is.EqualTo("Sesion guardada sin finalizar"));
        }

        [Test]
        public void SavedExitPromptRejectsResumeCheckpointWithoutConditionId()
        {
            var entry = new ExperimentSessionIdHistoryEntry
            {
                status = ExperimentSessionIdHistoryStore.StartedStatus,
                saved_visible_prueba = 1,
                saved_round_index = 1,
                resume_policy = "condition_start"
            };

            Assert.That(ExperimentSavedExitResumePromptState.HasValidCheckpoint(entry), Is.False);
        }

        [Test]
        public void PauseBridgeClickCanSurviveTargetDestroyedByClickAction()
        {
            EnsureSimpleEventSystem();
            var canvasObject = new GameObject("StartScreenUI", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            var buttonObject = new GameObject("Button_DestroyDuringClick", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(canvasObject.transform, false);
            try
            {
                Canvas canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                Button button = buttonObject.GetComponent<Button>();
                Image image = buttonObject.GetComponent<Image>();
                ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(button, image, () => UnityEngine.Object.DestroyImmediate(buttonObject), ExperimentButtonRole.Primary, true);

                Assert.DoesNotThrow(() => ExperimentPauseFrontCanvasPointerBridge.InvokeClickForTests(buttonObject, canvas));
            }
            finally
            {
                if (buttonObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(buttonObject);
                }

                UnityEngine.Object.DestroyImmediate(canvasObject);
                DestroyEventSystem();
            }
        }

        [Test]
        public void ConfigureExistingSessionForResumeReusesSavedSessionAndQuestionnaireCode()
        {
            ExperimentDataPathResolver.SessionContext session = ConfigureStartedSession("U20260706_161000", "S20260706_161000");
            string questionnaireCode = session.QuestionnaireCode;
            ExperimentSessionIdHistoryStore.UpdateSavedExitCheckpoint(
                session.SessionId,
                "C10_robot_on_voice_off",
                2,
                1,
                "C00_robot_off_voice_off,C10_robot_on_voice_off,C11_robot_on_voice_on",
                "condition_start");
            ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.SavedExitStatus);
            ExperimentDataPathResolver.PrepareForNewSessionStart();

            ExperimentDataPathResolver.SessionContext resumed = ExperimentDataPathResolver.ConfigureExistingSessionForResume(
                session.ParticipantId,
                session.SessionId,
                questionnaireCode,
                new[] { "C00_robot_off_voice_off", "C10_robot_on_voice_off", "C11_robot_on_voice_on" },
                2,
                session.QuestionnaireCodeScheme);

            Assert.That(resumed.ParticipantId, Is.EqualTo(session.ParticipantId));
            Assert.That(resumed.SessionId, Is.EqualTo(session.SessionId));
            Assert.That(resumed.QuestionnaireCode, Is.EqualTo(questionnaireCode));
            Assert.That(resumed.QuestionnaireCodeScheme, Is.EqualTo(session.QuestionnaireCodeScheme));
            Assert.That(ExperimentSessionIdHistoryStore.Load(ExperimentSessionIdHistoryStore.DefaultPath), Has.Count.EqualTo(1));
        }

        [Test]
        public void ConfigureExistingLegacySessionPreservesOriginalCodeWithoutValidationOrRegeneration()
        {
            string[] order =
            {
                ExperimentCompensatedConditionOrder.C11,
                ExperimentCompensatedConditionOrder.C10,
                ExperimentCompensatedConditionOrder.C00
            };
            const string legacyCode = "01ILOA";

            ExperimentDataPathResolver.SessionContext resumed = ExperimentDataPathResolver.ConfigureExistingSessionForResume(
                "U_LEGACY",
                "S_LEGACY",
                legacyCode,
                order,
                2,
                questionnaireCodeScheme: "");

            Assert.That(resumed.QuestionnaireCode, Is.EqualTo(legacyCode));
            Assert.That(resumed.QuestionnaireCodeScheme, Is.Empty);
            Assert.That(resumed.ConditionOrder, Is.EqualTo(order));
            Assert.That(QuestionnaireCodeCodec.TryValidate(resumed.QuestionnaireCode, out _, out _), Is.False);
            string exportInfo = File.ReadAllText(Path.Combine(resumed.SessionRoot, "session_export_info.json"));
            Assert.That(exportInfo, Does.Contain("\"questionnaire_code\":\"" + legacyCode + "\""));
            Assert.That(exportInfo, Does.Contain("\"questionnaire_code_scheme\":\"\""));
        }

        [Test]
        public void StartNewAttemptAfterSavedExitSupersedesPreviousAttempt()
        {
            ExperimentDataPathResolver.SessionContext session = ConfigureStartedSession("U20260706_162000", "S20260706_162000");
            ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.SavedExitStatus);

            ExperimentSessionIdHistoryStore.UpdateSessionStatus(session.SessionId, ExperimentSessionIdHistoryStore.SupersededAfterSavedExitStatus);

            ExperimentSessionIdHistoryEntry entry = ExperimentSessionIdHistoryStore.Load(ExperimentSessionIdHistoryStore.DefaultPath)[0];
            Assert.That(entry.status, Is.EqualTo(ExperimentSessionIdHistoryStore.SupersededAfterSavedExitStatus));
            Assert.That(ExperimentSavedExitResumePromptState.ToParticipantDisplayStatus(entry.status), Is.EqualTo("Sesion guardada sustituida por un nuevo intento"));
        }

        [Test]
        public void CompletedSavedExitResumeIsNotPendingAnymore()
        {
            ExperimentDataPathResolver.SessionContext session = ConfigureStartedSession("U20260706_163000", "S20260706_163000");
            ExperimentSessionIdHistoryStore.UpdateSavedExitCheckpoint(
                session.SessionId,
                "C10_robot_on_voice_off",
                2,
                1,
                "C00_robot_off_voice_off,C10_robot_on_voice_off,C11_robot_on_voice_on",
                "condition_start");
            ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.SavedExitStatus);

            ExperimentSessionIdHistoryStore.UpdateSessionStatus(session.SessionId, ExperimentSessionIdHistoryStore.StartedStatus);
            ExperimentSessionIdHistoryStore.UpdateSessionStatus(session.SessionId, ExperimentSessionIdHistoryStore.CompletedStatus);

            var entries = ExperimentSessionIdHistoryStore.Load(ExperimentSessionIdHistoryStore.DefaultPath);
            Assert.That(ExperimentSavedExitResumePromptState.TryFindPendingSession(entries, out _), Is.False);
            Assert.That(entries[0].status, Is.EqualTo(ExperimentSessionIdHistoryStore.CompletedStatus));
        }

        [Test]
        public void SavedExitPromptFindsEveryButtonCandidateIndependently()
        {
            var root = new GameObject("PromptRoot");
            var first = new GameObject("Button_Continuar", typeof(RectTransform), typeof(Image), typeof(Button));
            var second = new GameObject("Button_Empezar nuevo intento", typeof(RectTransform), typeof(Image), typeof(Button));
            var third = new GameObject("Button_Historial", typeof(RectTransform), typeof(Image), typeof(Button));
            var fourth = new GameObject("Button_Salir", typeof(RectTransform), typeof(Image), typeof(Button));
            first.transform.SetParent(root.transform, false);
            second.transform.SetParent(root.transform, false);
            third.transform.SetParent(root.transform, false);
            fourth.transform.SetParent(root.transform, false);
            try
            {
                Assert.That(ExperimentPauseFrontCanvasPointerBridge.ResolveButtonCandidate(first), Is.EqualTo(first));
                Assert.That(ExperimentPauseFrontCanvasPointerBridge.ResolveButtonCandidate(second), Is.EqualTo(second));
                Assert.That(ExperimentPauseFrontCanvasPointerBridge.ResolveButtonCandidate(third), Is.EqualTo(third));
                Assert.That(ExperimentPauseFrontCanvasPointerBridge.ResolveButtonCandidate(fourth), Is.EqualTo(fourth));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SavedExitPromptButtonFirstLocalPointResolvesEachRecoveryButton()
        {
            var canvasObject = new GameObject("StartScreenUI", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            var canvasRect = canvasObject.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(920f, 820f);
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var buttons = new List<Button>();

            Button CreateButton(string label, float y)
            {
                var buttonObject = new GameObject("Button_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
                buttonObject.transform.SetParent(canvasObject.transform, false);
                var rect = buttonObject.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(700f, 58f);
                rect.anchoredPosition = new Vector2(0f, y);
                var image = buttonObject.GetComponent<Image>();
                image.raycastTarget = true;
                Button button = buttonObject.GetComponent<Button>();
                button.targetGraphic = image;
                buttons.Add(button);
                return button;
            }

            Button continueButton = CreateButton("Continuar desde Prueba 2", 120f);
            Button newAttemptButton = CreateButton("Empezar nuevo intento", 40f);
            Button historyButton = CreateButton("Historial", -40f);
            Button exitButton = CreateButton("Salir", -120f);

            try
            {
                Assert.That(
                    ExperimentPauseFrontCanvasPointerBridge.ResolveBestButtonTargetAtCanvasLocalPoint(canvas, continueButton.GetComponent<RectTransform>().anchoredPosition, buttons, out string continueReason),
                    Is.EqualTo(continueButton.gameObject),
                    continueReason);
                Assert.That(
                    ExperimentPauseFrontCanvasPointerBridge.ResolveBestButtonTargetAtCanvasLocalPoint(canvas, newAttemptButton.GetComponent<RectTransform>().anchoredPosition, buttons, out string newAttemptReason),
                    Is.EqualTo(newAttemptButton.gameObject),
                    newAttemptReason);
                Assert.That(
                    ExperimentPauseFrontCanvasPointerBridge.ResolveBestButtonTargetAtCanvasLocalPoint(canvas, historyButton.GetComponent<RectTransform>().anchoredPosition, buttons, out string historyReason),
                    Is.EqualTo(historyButton.gameObject),
                    historyReason);
                Assert.That(
                    ExperimentPauseFrontCanvasPointerBridge.ResolveBestButtonTargetAtCanvasLocalPoint(canvas, exitButton.GetComponent<RectTransform>().anchoredPosition, buttons, out string exitReason),
                    Is.EqualTo(exitButton.gameObject),
                    exitReason);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvasObject);
            }
        }

        [Test]
        public void SavedExitPromptButtonFirstLocalPointRejectsEmptyPromptArea()
        {
            var canvasObject = new GameObject("StartScreenUI", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            var canvasRect = canvasObject.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(920f, 820f);
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var buttons = new List<Button>();

            var buttonObject = new GameObject("Button_Continuar desde Prueba 2", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(canvasObject.transform, false);
            var buttonRect = buttonObject.GetComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(0.5f, 0.5f);
            buttonRect.anchorMax = new Vector2(0.5f, 0.5f);
            buttonRect.pivot = new Vector2(0.5f, 0.5f);
            buttonRect.sizeDelta = new Vector2(700f, 58f);
            buttonRect.anchoredPosition = Vector2.zero;
            var image = buttonObject.GetComponent<Image>();
            image.raycastTarget = true;
            Button button = buttonObject.GetComponent<Button>();
            button.targetGraphic = image;
            buttons.Add(button);

            try
            {
                GameObject resolved = ExperimentPauseFrontCanvasPointerBridge.ResolveBestButtonTargetAtCanvasLocalPoint(
                    canvas,
                    new Vector2(0f, 220f),
                    buttons,
                    out string reason);

                Assert.That(resolved, Is.Null);
                Assert.That(reason, Does.Contain("no_rect_contains_local_point"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvasObject);
            }
        }

        [Test]
        public void SaveAndExitRequiresExplicitConfirmationAndCancelDoesNotPersist()
        {
            ConfigureStartedSession("U20260706_140000", "S20260706_140000");
            var state = new ExperimentPauseMenuStateMachine();

            state.Open();
            state.RequestSaveExit();
            state.CancelConfirmation();

            Assert.That(state.IsPaused, Is.True);
            Assert.That(state.Confirmation, Is.EqualTo(ExperimentPauseMenuConfirmation.None));
            Assert.That(ExperimentSessionIdHistoryStore.Load(ExperimentSessionIdHistoryStore.DefaultPath)[0].status, Is.EqualTo(ExperimentSessionIdHistoryStore.StartedStatus));
        }

        [Test]
        public void SaveAndExitConfirmationCanMarkSavedExitOnlyAfterConfirm()
        {
            ExperimentDataPathResolver.SessionContext session = ConfigureStartedSession("U20260706_141000", "S20260706_141000");
            var state = new ExperimentPauseMenuStateMachine();

            state.Open();
            state.RequestSaveExit();
            state.ConfirmSaveExit();
            ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.SavedExitStatus);

            Assert.That(state.IsPaused, Is.False);
            var entries = ExperimentSessionIdHistoryStore.Load(ExperimentSessionIdHistoryStore.DefaultPath);
            Assert.That(entries[0].session_id, Is.EqualTo(session.SessionId));
            Assert.That(entries[0].status, Is.EqualTo(ExperimentSessionIdHistoryStore.SavedExitStatus));
        }

        [Test]
        public void ExitWithoutCompletionRequiresExplicitConfirmation()
        {
            var state = new ExperimentPauseMenuStateMachine();

            state.Open();
            state.RequestExitWithoutCompletion();
            ExperimentPauseMenuYButtonResult yWhileModal = state.UpdateYPressed(true);

            Assert.That(state.IsPaused, Is.True);
            Assert.That(state.Confirmation, Is.EqualTo(ExperimentPauseMenuConfirmation.ExitWithoutCompletion));
            Assert.That(yWhileModal.Command, Is.EqualTo(ExperimentPauseMenuCommand.YIgnoredDueToConfirmation));
        }

        [Test]
        public void RestartSessionRequiresExplicitConfirmation()
        {
            var state = new ExperimentPauseMenuStateMachine();

            state.Open();
            state.RequestRestart();
            ExperimentPauseMenuYButtonResult yWhileModal = state.UpdateYPressed(true);

            Assert.That(state.IsPaused, Is.True);
            Assert.That(state.Confirmation, Is.EqualTo(ExperimentPauseMenuConfirmation.Restart));
            Assert.That(yWhileModal.Command, Is.EqualTo(ExperimentPauseMenuCommand.YIgnoredDueToConfirmation));
        }

        [Test]
        public void ConfirmExitWithoutCompletionCanMarkCurrentSessionClosedWithoutCompletion()
        {
            ExperimentDataPathResolver.SessionContext session = ConfigureStartedSession("U20260705_123000", "S20260705_123000");
            var state = new ExperimentPauseMenuStateMachine();

            state.Open();
            state.RequestExitWithoutCompletion();
            state.ConfirmExitWithoutCompletion();
            ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.ClosedWithoutCompletionStatus);

            Assert.That(state.IsPaused, Is.False);
            var entries = ExperimentSessionIdHistoryStore.Load(ExperimentSessionIdHistoryStore.DefaultPath);
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].session_id, Is.EqualTo(session.SessionId));
            Assert.That(entries[0].status, Is.EqualTo(ExperimentSessionIdHistoryStore.ClosedWithoutCompletionStatus));
        }

        [Test]
        public void CancelConfirmationReturnsToPauseWithoutChangingHistory()
        {
            ConfigureStartedSession("U20260705_124000", "S20260705_124000");
            var state = new ExperimentPauseMenuStateMachine();

            state.Open();
            state.RequestExitWithoutCompletion();
            state.CancelConfirmation();

            Assert.That(state.IsPaused, Is.True);
            Assert.That(state.Confirmation, Is.EqualTo(ExperimentPauseMenuConfirmation.None));
            Assert.That(ExperimentSessionIdHistoryStore.Load(ExperimentSessionIdHistoryStore.DefaultPath)[0].status, Is.EqualTo(ExperimentSessionIdHistoryStore.StartedStatus));
        }

        [Test]
        public void PauseButtonsUseIdempotentListenerRegistration()
        {
            var target = new GameObject("Button_PauseContinue");
            try
            {
                Image image = target.AddComponent<Image>();
                Button button = target.AddComponent<Button>();
                int invokeCount = 0;

                ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(button, image, () => invokeCount += 10, ExperimentButtonRole.Primary, true);
                int listenerCount = ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(button, image, () => invokeCount++, ExperimentButtonRole.Primary, true);
                button.onClick.Invoke();

                Assert.That(listenerCount, Is.EqualTo(1));
                Assert.That(invokeCount, Is.EqualTo(1));
                Assert.That(button.interactable, Is.True);
                Assert.That(image.raycastTarget, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void PausePanelUsesFrontWorldSpaceCanvasAndDoesNotAttachToRuntimeProtocolCanvas()
        {
            GameObject runtimeCanvas = CreateRuntimeProtocolCanvas();
            GameObject host = new GameObject("ExperimentPauseMenuController");

            try
            {
                Component controller = host.AddComponent(ResolvePauseControllerType());
                InvokePrivate(controller, "OpenPause");
                Transform frontCanvas = host.transform.Find("ExperimentPauseFrontCanvas");
                Transform modal = frontCanvas != null ? frontCanvas.Find("ExperimentPauseModal") : null;
                Canvas canvas = frontCanvas != null ? frontCanvas.GetComponent<Canvas>() : null;
                Vector2 worldSize = WorldSize(frontCanvas as RectTransform);

                Assert.That(host.GetComponent<Canvas>(), Is.Null);
                Assert.That(runtimeCanvas.transform.Find("ExperimentPauseModal"), Is.Null);
                Assert.That(frontCanvas, Is.Not.Null);
                Assert.That(modal, Is.Not.Null);
                Assert.That(canvas, Is.Not.Null);
                Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.WorldSpace));
                Assert.That(frontCanvas.localScale.x, Is.LessThan(0.01f));
                Assert.That(worldSize.x, Is.GreaterThan(0.5f).And.LessThan(2.5f));
                Assert.That(worldSize.y, Is.GreaterThan(0.4f).And.LessThan(2.5f));
                Assert.That(GetPrivateBool(controller, "_usesRuntimeProtocolCanvas"), Is.False);
                Assert.That(frontCanvas.GetComponent<GraphicRaycaster>(), Is.Not.Null);
                Assert.That(HasComponentNamed(frontCanvas.gameObject, "TrackedDeviceGraphicRaycaster"), Is.True);
                ExperimentPauseFrontCanvasPointerBridge bridge = frontCanvas.GetComponent<ExperimentPauseFrontCanvasPointerBridge>();
                Assert.That(bridge, Is.Not.Null);
                Assert.That(bridge.TargetCanvas, Is.EqualTo(canvas));
                EventSystem eventSystem = EventSystem.current ?? UnityEngine.Object.FindFirstObjectByType<EventSystem>();
                Assert.That(eventSystem, Is.Not.Null);
                Assert.That(HasComponentNamed(eventSystem.gameObject, "XRUIInputModule"), Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(runtimeCanvas);
                UnityEngine.Object.DestroyImmediate(host);
                DestroyEventSystem();
            }
        }

        [Test]
        public void PausePanelButtonsAreInteractableAndBlockUnderlyingUiWhenOpen()
        {
            GameObject host = new GameObject("ExperimentPauseMenuController");

            try
            {
                Component controller = host.AddComponent(ResolvePauseControllerType());
                InvokePrivate(controller, "OpenPause");
                Transform modal = FindPauseModal(host);

                CanvasGroup group = modal.GetComponent<CanvasGroup>();
                Button[] buttons = modal.GetComponentsInChildren<Button>(true);

                Assert.That(modal, Is.Not.Null);
                Assert.That(group, Is.Not.Null);
                Assert.That(group.interactable, Is.True);
                Assert.That(group.blocksRaycasts, Is.True);
                Assert.That(buttons, Has.Length.EqualTo(4));
                foreach (Button button in buttons)
                {
                    Assert.That(button.gameObject.activeInHierarchy, Is.True);
                    Assert.That(button.interactable, Is.True);
                    Assert.That(button.targetGraphic, Is.Not.Null);
                    Assert.That(button.targetGraphic.raycastTarget, Is.True);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                DestroyEventSystem();
            }
        }

        [Test]
        public void PauseFrontPointerBridgeActivatesOnlyWhilePauseIsOpen()
        {
            GameObject host = new GameObject("ExperimentPauseMenuController");

            try
            {
                Component controller = host.AddComponent(ResolvePauseControllerType());
                InvokePrivate(controller, "OpenPause");
                ExperimentPauseFrontCanvasPointerBridge bridge = host
                    .transform
                    .Find("ExperimentPauseFrontCanvas")
                    .GetComponent<ExperimentPauseFrontCanvasPointerBridge>();

                Assert.That(bridge, Is.Not.Null);
                Assert.That(bridge.IsBridgeActive, Is.True);

                InvokePrivate(controller, "Continue");

                Assert.That(bridge.IsBridgeActive, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                DestroyEventSystem();
            }
        }

        [Test]
        public void PauseFrontPointerBridgeTargetsFrontCanvasInsteadOfRuntimeProtocolCanvas()
        {
            GameObject runtimeCanvas = CreateRuntimeProtocolCanvas();
            GameObject host = new GameObject("ExperimentPauseMenuController");

            try
            {
                Component controller = host.AddComponent(ResolvePauseControllerType());
                InvokePrivate(controller, "OpenPause");
                Transform frontCanvas = host.transform.Find("ExperimentPauseFrontCanvas");
                ExperimentPauseFrontCanvasPointerBridge bridge = frontCanvas.GetComponent<ExperimentPauseFrontCanvasPointerBridge>();

                Assert.That(bridge.TargetCanvas, Is.EqualTo(frontCanvas.GetComponent<Canvas>()));
                Assert.That(bridge.TargetCanvas, Is.Not.EqualTo(runtimeCanvas.GetComponent<Canvas>()));
                Assert.That(runtimeCanvas.transform.Find("ExperimentPauseModal"), Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(runtimeCanvas);
                UnityEngine.Object.DestroyImmediate(host);
                DestroyEventSystem();
            }
        }

        [Test]
        public void PauseFrontPointerBridgeResolvesButtonCandidateFromGraphicHit()
        {
            GameObject buttonObject = new GameObject("Button_Test", typeof(RectTransform));

            try
            {
                Image image = buttonObject.AddComponent<Image>();
                Button button = buttonObject.AddComponent<Button>();
                button.targetGraphic = image;

                GameObject candidate = ExperimentPauseFrontCanvasPointerBridge.ResolveButtonCandidate(image.gameObject);

                Assert.That(candidate, Is.EqualTo(buttonObject));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(buttonObject);
            }
        }

        [Test]
        public void PauseFrontPointerBridgeInvokesButtonClickOnce()
        {
            EnsureSimpleEventSystem();
            GameObject canvasObject = new GameObject("PauseCanvas", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            GameObject buttonObject = new GameObject("Button_Test", typeof(RectTransform));

            try
            {
                Canvas canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                buttonObject.transform.SetParent(canvasObject.transform, false);
                Image image = buttonObject.AddComponent<Image>();
                Button button = buttonObject.AddComponent<Button>();
                button.targetGraphic = image;
                int clickCount = 0;
                button.onClick.AddListener(() => clickCount++);

                bool invoked = ExperimentPauseFrontCanvasPointerBridge.InvokeClickForTests(buttonObject, canvas);

                Assert.That(invoked, Is.True);
                Assert.That(clickCount, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(buttonObject);
                UnityEngine.Object.DestroyImmediate(canvasObject);
                DestroyEventSystem();
            }
        }

        [Test]
        public void PauseFrontPointerBridgeDoesNotReportCanvasHitForMissingCanvas()
        {
            PauseCanvasHit hit = ExperimentPauseFrontCanvasPointerBridge.RaycastPauseCanvas(null, new Ray(Vector3.zero, Vector3.forward));

            Assert.That(hit.CanvasHit, Is.False);
            Assert.That(hit.Valid, Is.False);
        }

        [Test]
        public void PauseButtonsReceiveExactlyOneTracerEach()
        {
            GameObject host = new GameObject("ExperimentPauseMenuController");

            try
            {
                Component controller = host.AddComponent(ResolvePauseControllerType());
                InvokePrivate(controller, "OpenPause");
                Transform modal = FindPauseModal(host);

                Button[] buttons = modal.GetComponentsInChildren<Button>(true);
                ExperimentPauseButtonClickTracer[] tracers = modal.GetComponentsInChildren<ExperimentPauseButtonClickTracer>(true);

                Assert.That(buttons, Has.Length.EqualTo(4));
                Assert.That(tracers, Has.Length.EqualTo(4));
                foreach (Button button in buttons)
                {
                    Assert.That(button.GetComponents<ExperimentPauseButtonClickTracer>(), Has.Length.EqualTo(1));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                DestroyEventSystem();
            }
        }

        [Test]
        public void PauseMenuWithoutRuntimeProtocolUiCreatesOnlyReasonableFrontCanvas()
        {
            GameObject host = new GameObject("ExperimentPauseMenuController");

            try
            {
                Component controller = host.AddComponent(ResolvePauseControllerType());
                InvokePrivate(controller, "OpenPause");
                Transform frontCanvas = host.transform.Find("ExperimentPauseFrontCanvas");

                Assert.That(host.GetComponent<Canvas>(), Is.Null);
                Assert.That(frontCanvas, Is.Not.Null);
                Assert.That(UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None), Has.Length.EqualTo(1));
                Assert.That(WorldSize(frontCanvas as RectTransform).x, Is.LessThan(2.5f));
                Assert.That(WorldSize(frontCanvas as RectTransform).y, Is.LessThan(2.5f));
                Assert.That(GetPrivateBool(controller, "_usesRuntimeProtocolCanvas"), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                DestroyEventSystem();
            }
        }

        [Test]
        public void PauseModalUsesFullscreenAnchorsWithinFrontCanvasAndOverlayStaysBelowButtons()
        {
            GameObject host = new GameObject("ExperimentPauseMenuController");

            try
            {
                Component controller = host.AddComponent(ResolvePauseControllerType());
                InvokePrivate(controller, "OpenPause");

                RectTransform modal = FindPauseModal(host) as RectTransform;
                Transform overlay = modal.Find("ModalOverlay");
                Transform panel = modal.Find("Panel");

                Assert.That(modal.anchorMin, Is.EqualTo(Vector2.zero));
                Assert.That(modal.anchorMax, Is.EqualTo(Vector2.one));
                Assert.That(modal.offsetMin, Is.EqualTo(Vector2.zero));
                Assert.That(modal.offsetMax, Is.EqualTo(Vector2.zero));
                Assert.That(modal.localScale, Is.EqualTo(Vector3.one));
                Assert.That(overlay, Is.Not.Null);
                Assert.That(panel, Is.Not.Null);
                Assert.That(overlay.GetSiblingIndex(), Is.LessThan(panel.GetSiblingIndex()));
                Assert.That(panel.GetComponentInChildren<Button>(true), Is.Not.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                DestroyEventSystem();
            }
        }

        [Test]
        public void SavedExitPromptDetectsPendingSavedExitBeforeNewIdCreation()
        {
            ExperimentSessionIdHistoryEntry saved = ExperimentSessionIdHistoryEntry.Create(
                "U20260706_120000",
                "S20260706_120000",
                "ABC123",
                "2026-07-06T12:00:00.0000000Z",
                ExperimentSessionIdHistoryStore.SavedExitStatus,
                1);
            var entries = new[] { saved };

            bool found = ExperimentSavedExitResumePromptState.TryFindPendingSession(entries, out ExperimentSessionIdHistoryEntry pending);

            Assert.That(found, Is.True);
            Assert.That(pending.session_id, Is.EqualTo(saved.session_id));
            Assert.That(pending.questionnaire_code, Is.EqualTo(saved.questionnaire_code));
        }

        [Test]
        public void SavedExitPromptMapsInternalStatusToParticipantFriendlyLabel()
        {
            string saved = ExperimentSavedExitResumePromptState.ToParticipantDisplayStatus(ExperimentSessionIdHistoryStore.SavedExitStatus);
            string started = ExperimentSavedExitResumePromptState.ToParticipantDisplayStatus(ExperimentSessionIdHistoryStore.StartedStatus);

            Assert.That(saved, Is.EqualTo("Sesion guardada sin finalizar"));
            Assert.That(started, Is.EqualTo("Sesion iniciada sin finalizar"));
            Assert.That(saved, Is.Not.EqualTo(ExperimentSessionIdHistoryStore.SavedExitStatus));
        }

        [Test]
        public void SavedExitPromptIgnoresCompletedSessions()
        {
            ExperimentSessionIdHistoryEntry completed = ExperimentSessionIdHistoryEntry.Create(
                "U20260706_121000",
                "S20260706_121000",
                "XYZ789",
                "2026-07-06T12:10:00.0000000Z",
                ExperimentSessionIdHistoryStore.CompletedStatus,
                1);

            bool found = ExperimentSavedExitResumePromptState.TryFindPendingSession(
                new[] { completed },
                out ExperimentSessionIdHistoryEntry pending);

            Assert.That(found, Is.False);
            Assert.That(pending, Is.Null);
        }

        [Test]
        public void DeviceResolverPrefersPhysicalLeftControllerOverHandInteraction()
        {
            var devices = new[]
            {
                Device(
                    "Hand Interaction OpenXR",
                    InputDeviceCharacteristics.Left | InputDeviceCharacteristics.HandTracking | InputDeviceCharacteristics.TrackedDevice,
                    isValid: true,
                    isTracked: true,
                    secondarySupported: false,
                    secondaryPressed: false),
                Device(
                    "Oculus Touch Controller OpenXR",
                    InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.HeldInHand | InputDeviceCharacteristics.TrackedDevice,
                    isValid: true,
                    isTracked: true,
                    secondarySupported: true,
                    secondaryPressed: true)
            };

            ExperimentPauseInputDeviceSelection selection = ExperimentPauseInputDeviceResolver.SelectBestLeftController(devices);

            Assert.That(selection.Found, Is.True);
            Assert.That(selection.Index, Is.EqualTo(1));
            Assert.That(selection.Device.Name, Is.EqualTo("Oculus Touch Controller OpenXR"));
            Assert.That(selection.Device.SecondaryButtonPressed, Is.True);
        }

        [Test]
        public void DeviceResolverDoesNotSelectHandOnlyPseudoDevice()
        {
            var devices = new[]
            {
                Device(
                    "Hand Interaction Poses OpenXR",
                    InputDeviceCharacteristics.Left | InputDeviceCharacteristics.HandTracking | InputDeviceCharacteristics.TrackedDevice,
                    isValid: true,
                    isTracked: true,
                    secondarySupported: false,
                    secondaryPressed: false),
                Device(
                    "Palm Pose Interaction OpenXR",
                    InputDeviceCharacteristics.Left | InputDeviceCharacteristics.HandTracking | InputDeviceCharacteristics.TrackedDevice,
                    isValid: true,
                    isTracked: true,
                    secondarySupported: false,
                    secondaryPressed: false)
            };

            ExperimentPauseInputDeviceSelection selection = ExperimentPauseInputDeviceResolver.SelectBestLeftController(devices);

            Assert.That(selection.Found, Is.False);
        }

        [Test]
        public void DeviceResolverAllowsControllerWithoutSecondaryButtonButReportsUnsupported()
        {
            var devices = new[]
            {
                Device(
                    "Generic Left Controller OpenXR",
                    InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.HeldInHand | InputDeviceCharacteristics.TrackedDevice,
                    isValid: true,
                    isTracked: true,
                    secondarySupported: false,
                    secondaryPressed: false)
            };

            ExperimentPauseInputDeviceSelection selection = ExperimentPauseInputDeviceResolver.SelectBestLeftController(devices);

            Assert.That(selection.Found, Is.True);
            Assert.That(selection.Device.SecondaryButtonSupported, Is.False);
            Assert.That(selection.Device.SecondaryButtonPressed, Is.False);
        }

        private static ExperimentDataPathResolver.SessionContext ConfigureStartedSession(string participantId, string sessionId)
        {
            return ExperimentDataPathResolver.ConfigureSession(
                participantId,
                sessionId,
                new[] { "C00_robot_off_voice_off", "C10_robot_on_voice_off", "C11_robot_on_voice_on" },
                2);
        }

        private static ExperimentPauseInputDeviceSnapshot Device(
            string name,
            InputDeviceCharacteristics characteristics,
            bool isValid,
            bool isTracked,
            bool secondarySupported,
            bool secondaryPressed)
        {
            return new ExperimentPauseInputDeviceSnapshot(
                name,
                characteristics,
                isValid,
                isTracked,
                secondarySupported,
                secondarySupported && secondaryPressed,
                primaryButtonSupported: true,
                primaryButtonPressed: false,
                menuButtonSupported: true,
                menuButtonPressed: false);
        }

        private static GameObject CreateRuntimeProtocolCanvas()
        {
            var runtimeCanvas = new GameObject(
                "RuntimeProtocolUI",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            Canvas canvas = runtimeCanvas.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 100;
            RectTransform rect = runtimeCanvas.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(900f, 760f);
            rect.localScale = Vector3.one * 0.0018f;
            return runtimeCanvas;
        }

        private static Transform FindPauseModal(GameObject host)
        {
            Transform frontCanvas = host != null ? host.transform.Find("ExperimentPauseFrontCanvas") : null;
            Assert.That(frontCanvas, Is.Not.Null);
            Transform modal = frontCanvas.Find("ExperimentPauseModal");
            Assert.That(modal, Is.Not.Null);
            return modal;
        }

        private static Vector2 WorldSize(RectTransform rect)
        {
            Assert.That(rect, Is.Not.Null);
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return new Vector2(
                Vector3.Distance(corners[0], corners[3]),
                Vector3.Distance(corners[0], corners[1]));
        }

        private static void InvokePrivate(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(target, Array.Empty<object>());
        }

        private static Type ResolvePauseControllerType()
        {
            Type type = Type.GetType("ExperimentPauseMenuController, Assembly-CSharp");
            Assert.That(type, Is.Not.Null);
            return type;
        }

        private static bool GetPrivateBool(Component target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return (bool)field.GetValue(target);
        }

        private static void DestroyEventSystem()
        {
            EventSystem eventSystem = EventSystem.current ?? UnityEngine.Object.FindFirstObjectByType<EventSystem>();
            if (eventSystem != null)
            {
                UnityEngine.Object.DestroyImmediate(eventSystem.gameObject);
            }
        }

        private static void EnsureSimpleEventSystem()
        {
            if (EventSystem.current != null)
            {
                return;
            }

            var eventSystem = new GameObject("EventSystem");
            eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<StandaloneInputModule>();
        }

        private static bool HasComponentNamed(GameObject target, string typeName)
        {
            foreach (Component component in target.GetComponents<Component>())
            {
                if (component != null && component.GetType().Name == typeName)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
