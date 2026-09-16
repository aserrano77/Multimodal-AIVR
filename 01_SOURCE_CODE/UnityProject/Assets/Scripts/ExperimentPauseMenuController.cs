using System;
using System.Collections;
using System.Collections.Generic;
using Autonomy.UnityIntegration;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.UI;

[DisallowMultipleComponent]
public sealed class ExperimentPauseMenuController : MonoBehaviour
{
    private const string RootName = "ExperimentPauseMenuController";
    private const string ModalName = "ExperimentPauseModal";
    private const string FinalSceneName = "final_scene";
    private const string TargetSceneName = "autonomous_demo_step22_multimodal_bridge";
    private const string LogPrefix = "[P46D-PAUSE]";
    private const string UiLogPrefix = "[P46D-PAUSE-UI]";
    private const float DeviceSnapshotLogIntervalSeconds = 2.0f;
    private const float NoPointerEventThresholdSeconds = 2.0f;
    private const float FrontCanvasScale = 0.0018f;
    private const float FrontCanvasDistanceMeters = 1.45f;
    private const float FrontCanvasMaxWorldMeters = 2.5f;
    private const float PauseMenuPanelWidth = 800f;
    private const float PauseMenuPanelHeight = 700f;
    private const float PauseMenuCanvasWidth = 920f;
    private const float PauseMenuCanvasHeight = 840f;
    private const float PanelSafeHorizontalPadding = 44f;
    private const float PanelSafeVerticalPadding = 34f;
    private const float PauseMenuLayoutSpacing = 18f;
    private const float PauseMenuTitleHeight = 72f;
    private const float PauseMenuBodyHeight = 50f;
    private const float PauseMenuButtonAreaHeight = 68f;
    private static readonly Color PauseMenuPanelBackgroundColor = new(0.07f, 0.08f, 0.09f, 0.92f);
    private static readonly Color PauseMenuModalOverlayColor = new(0f, 0f, 0f, 0.18f);

    [SerializeField] private Vector2 _panelSize = new(PauseMenuCanvasWidth, PauseMenuCanvasHeight);

    private Canvas _canvas;
    private CanvasGroup _canvasGroup;
    private RectTransform _pauseRoot;
    private RectTransform _content;
    private ExperimentPauseFrontCanvasPointerBridge _frontPointerBridge;
    private ExperimentRuntimePauseCoordinator _pauseCoordinator;
    private readonly ExperimentPauseMenuStateMachine _state = new();
    private float _nextDeviceSnapshotLogAt;
    private bool _pollingLogged;
    private bool _secondaryUnsupportedLogged;
    private bool _deviceSelectionStateInitialized;
    private bool _lastDeviceSelectionFound;
    private bool _lastDeviceSelectionValid;
    private bool _lastDeviceSelectionTracked;
    private bool _lastDeviceSelectionSecondarySupported;
    private string _lastDeviceSelectionName = string.Empty;
    private bool _noPointerEventMissingLogged;
    private bool _usesRuntimeProtocolCanvas;
    private bool _frontCanvasSizeGuardPassed;
    private string _pauseOpenStateSignature = string.Empty;
    private string _restartFailureReason = string.Empty;
    private bool _restartFailureAfterPointOfNoReturn;
    private Coroutine _deferredDestructiveActionCoroutine;

    public static int LastContinueFrame { get; private set; } = -100000;
    public static float LastContinueRealtime { get; private set; } = -100000f;

    public static bool WasContinueRecentlyAcknowledged(float maxSeconds = 30f)
    {
        return LastContinueFrame > 0 && Time.realtimeSinceStartup - LastContinueRealtime <= Mathf.Max(0.1f, maxSeconds);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        Debug.Log($"{LogPrefix} boot | scene={SceneManager.GetActiveScene().name} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        EnsureControllerForScene(SceneManager.GetActiveScene(), "runtime_initialize_before_scene_load");
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Debug.Log($"{LogPrefix} boot_scene_loaded | scene={scene.name} mode={mode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        EnsureControllerForScene(scene, "scene_loaded");
    }

    private static ExperimentPauseMenuController EnsureControllerForScene(Scene scene, string reason)
    {
        string sceneName = SceneManager.GetActiveScene().name;
        if (!string.IsNullOrWhiteSpace(scene.name))
        {
            sceneName = scene.name;
        }

        if (!IsProtocolScene(sceneName))
        {
            Debug.Log($"{LogPrefix} boot_scene_ignored | reason={reason ?? string.Empty} scene={sceneName} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            return null;
        }

        ExperimentPauseMenuController existing =
            FindFirstObjectByType<ExperimentPauseMenuController>(FindObjectsInactive.Include);
        if (existing != null)
        {
            if (!existing.gameObject.activeSelf)
            {
                existing.gameObject.SetActive(true);
            }

            if (!existing.enabled)
            {
                existing.enabled = true;
            }

            Debug.Log($"{LogPrefix} boot_existing_controller_reused | reason={reason ?? string.Empty} scene={sceneName} path={GetPath(existing.transform)} activeSelf={existing.gameObject.activeSelf} activeInHierarchy={existing.gameObject.activeInHierarchy} enabled={existing.enabled} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            return existing;
        }

        Transform parent = FindExperimentParent();

        var host = new GameObject(RootName);
        if (parent != null)
        {
            host.transform.SetParent(parent, false);
        }

        ExperimentPauseMenuController created = host.AddComponent<ExperimentPauseMenuController>();
        Debug.Log($"{LogPrefix} boot_controller_created | reason={reason ?? string.Empty} scene={sceneName} path={GetPath(host.transform)} parent={(parent != null ? GetPath(parent) : string.Empty)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        return created;
    }

    private void Awake()
    {
        _pauseCoordinator = ExperimentRuntimePauseCoordinator.EnsureFor(gameObject);
        BuildCanvas();
        SetVisible(false);
        Debug.Log($"{LogPrefix} awake | scene={SceneManager.GetActiveScene().name} path={GetPath(transform)} activeSelf={gameObject.activeSelf} activeInHierarchy={gameObject.activeInHierarchy} enabled={enabled} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_controller_initialized", BuildPausePayload("initialized"));
    }

    private void OnEnable()
    {
        Debug.Log($"{LogPrefix} on_enable | scene={SceneManager.GetActiveScene().name} path={GetPath(transform)} activeSelf={gameObject.activeSelf} activeInHierarchy={gameObject.activeInHierarchy} enabled={enabled} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
    }

    private void Start()
    {
        Debug.Log($"{LogPrefix} start | scene={SceneManager.GetActiveScene().name} path={GetPath(transform)} activeSelf={gameObject.activeSelf} activeInHierarchy={gameObject.activeInHierarchy} enabled={enabled} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        Debug.Log($"{LogPrefix} pause_menu_controller_active | scene={SceneManager.GetActiveScene().name} path={GetPath(transform)} activeSelf={gameObject.activeSelf} activeInHierarchy={gameObject.activeInHierarchy} enabled={enabled} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
    }

    private void Update()
    {
        if (!_pollingLogged)
        {
            _pollingLogged = true;
            Debug.Log($"{LogPrefix} pause_menu_polling_enabled | scene={SceneManager.GetActiveScene().name} path={GetPath(transform)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        }

        bool yPressed = IsLeftControllerYPressed(out ExperimentPauseInputDeviceSelection selection, out IReadOnlyList<ExperimentPauseInputDeviceSnapshot> snapshots);
        if (QuestLoggingPolicy.EmitLegacyContinuousDiagnostics)
        {
            MaybeLogDeviceSnapshot(selection, snapshots);
        }
        else
        {
            LogDeviceSelectionOnChange(selection);
        }

        if (QuestLoggingPolicy.EmitLegacyContinuousDiagnostics &&
            selection.Found &&
            selection.Device.SecondaryButtonSupported &&
            selection.Device.SecondaryButtonPressed)
        {
            Debug.Log($"{LogPrefix} left_controller_y_pressed | {FormatDevice(selection.Device)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        }

        ExperimentPauseMenuYButtonResult result = _state.UpdateYPressed(yPressed);
        if (result.RisingEdge)
        {
            Debug.Log($"{LogPrefix} left_controller_y_edge_detected | paused={_state.IsPaused} confirmation={_state.Confirmation} {FormatDevice(selection.Device)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_y_button_detected", BuildPausePayload("y_button_rising_edge"));
        }

        ExecuteStateCommand(result.Command, "y_button");
        MaybeLogNoPointerEvents();
    }

    public bool IsPaused => ExperimentRuntimePauseCoordinator.IsExperimentPaused;

    public bool ConfirmationActive => _state.Confirmation != ExperimentPauseMenuConfirmation.None;

    private void ExecuteStateCommand(ExperimentPauseMenuCommand command, string source)
    {
        switch (command)
        {
            case ExperimentPauseMenuCommand.OpenPause:
                OpenPause();
                break;
            case ExperimentPauseMenuCommand.Continue:
                if (_restartFailureAfterPointOfNoReturn)
                {
                    Debug.LogWarning($"{LogPrefix} pause_continue_blocked_after_late_restart_failure | reason={_restartFailureReason} scene={SceneManager.GetActiveScene().name}");
                    TiagoExperimentTelemetry.LogEvent(
                        "experiment_pause_menu_continue_blocked_after_late_restart_failure",
                        BuildPausePayload("continue_blocked_after_late_restart_failure"));
                    break;
                }

                Continue();
                break;
            case ExperimentPauseMenuCommand.YIgnoredDueToConfirmation:
                Debug.Log($"{LogPrefix} y_button_ignored_confirmation_active | source={source ?? string.Empty} confirmation={_state.Confirmation}");
                TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_y_ignored_confirmation_active", BuildPausePayload("y_ignored_confirmation_active"));
                break;
        }
    }

    private void OpenPause()
    {
        _state.Open();
        _pauseOpenStateSignature = BuildRuntimeStateSnapshot("pause_open_state_snapshot", out string openSnapshot);
        Debug.Log(openSnapshot);
        Debug.Log($"{LogPrefix} pause_open_requested | scene={SceneManager.GetActiveScene().name} path={GetPath(transform)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        if (!EnsureBuilt())
        {
            _state.Continue();
            Debug.LogError($"{UiLogPrefix} pause_ui_runtime_canvas_missing | scene={SceneManager.GetActiveScene().name} controller_path={GetPath(transform)} action=open_pause_aborted_no_fallback_canvas frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_runtime_canvas_missing", BuildPausePayload("runtime_canvas_missing"));
            return;
        }

        PositionInFrontOfUser();
        if (!ValidateFrontCanvasSizeGuard("pause_open_requested"))
        {
            _state.Continue();
            SetVisible(false);
            Debug.LogError($"{UiLogPrefix} pause_ui_front_canvas_size_guard_fail | action=open_pause_aborted scene={SceneManager.GetActiveScene().name} canvas_path={(_canvas != null ? GetPath(_canvas.transform) : string.Empty)} panel_path={GetPath(_pauseRoot)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            return;
        }

        EnsureEventSystem();
        Render();
        SetVisible(true);
        _frontPointerBridge?.Activate(_canvas);
        _pauseCoordinator ??= ExperimentRuntimePauseCoordinator.EnsureFor(gameObject);
        if (_pauseCoordinator == null ||
            (!_pauseCoordinator.EnterPause("pause_menu_open") && !ExperimentRuntimePauseCoordinator.IsExperimentPaused))
        {
            _state.Continue();
            SetVisible(false);
            Debug.LogError($"{LogPrefix} experiment_pause_enter_failed | scene={SceneManager.GetActiveScene().name} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            return;
        }

        LogUiState("pause_opened");
        Debug.Log($"{LogPrefix} pause_opened | scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} path={GetPath(transform)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_opened", BuildPausePayload("opened"));
    }

    private void Continue()
    {
        _restartFailureReason = string.Empty;
        _restartFailureAfterPointOfNoReturn = false;
        string beforeSignature = BuildRuntimeStateSnapshot("pause_continue_state_snapshot_before", out string beforeSnapshot);
        Debug.Log(beforeSnapshot);
        _state.Continue();
        SetVisible(false);
        _pauseCoordinator?.ResumeFromPause("pause_menu_continue");
        LastContinueFrame = Time.frameCount;
        LastContinueRealtime = Time.realtimeSinceStartup;
        string afterSignature = BuildRuntimeStateSnapshot("pause_continue_state_snapshot_after", out string afterSnapshot);
        Debug.Log(afterSnapshot);
        bool restored = string.Equals(beforeSignature, afterSignature, StringComparison.Ordinal) &&
            (string.IsNullOrWhiteSpace(_pauseOpenStateSignature) || string.Equals(_pauseOpenStateSignature, afterSignature, StringComparison.Ordinal));
        if (restored)
        {
            Debug.Log($"{LogPrefix} pause_continue_runtime_state_restored | scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        }
        else
        {
            Debug.LogWarning($"{LogPrefix} pause_continue_runtime_state_warning | scene={SceneManager.GetActiveScene().name} before_equals_after={string.Equals(beforeSignature, afterSignature, StringComparison.Ordinal)} open_equals_after={string.Equals(_pauseOpenStateSignature, afterSignature, StringComparison.Ordinal)} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        }

        LogUiState("pause_closed_continue");
        Debug.Log($"{LogPrefix} pause_closed_continue | scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} path={GetPath(transform)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_continued", BuildPausePayload("continued"));
    }

    private void RequestRestart()
    {
        _restartFailureReason = string.Empty;
        _restartFailureAfterPointOfNoReturn = false;
        _state.RequestRestart();
        Debug.Log($"{LogPrefix} pause_restart_confirm_requested | scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_restart_requested", BuildPausePayload("restart_requested"));
        Render();
    }

    private void ConfirmRestart()
    {
        _state.ConfirmRestart();
        BeginDeferredDestructiveAction("restart", DeferredRestartCoroutine());
    }

    private IEnumerator DeferredRestartCoroutine()
    {
        Debug.Log($"{LogPrefix} pause_restart_deferred_begin | scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        yield return null;
        bool restarted = false;
        try
        {
            Debug.Log($"{LogPrefix} pause_restart_deferred_execute | scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            restarted = TryRestartExperimentalSession();
            Debug.Log($"{LogPrefix} pause_restart_deferred_completed | restarted={restarted} scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        }
        catch (Exception ex)
        {
            _restartFailureReason = "restart_integration_failed:" + ex.GetType().Name;
            Debug.LogError($"{LogPrefix} pause_destructive_action_failed | action=restart error={ex.GetType().Name}:{ex.Message} scene={SceneManager.GetActiveScene().name}");
        }

        Debug.Log($"{LogPrefix} pause_restart_confirmed | restarted={restarted} scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_restart_confirmed", BuildPausePayload(restarted ? "restarted" : "restart_failed"));
        _deferredDestructiveActionCoroutine = null;
        if (restarted)
        {
            _restartFailureReason = string.Empty;
            _restartFailureAfterPointOfNoReturn = false;
            yield break;
        }

        ExperimentSessionOrchestrator orchestrator = FindFirstObjectByType<ExperimentSessionOrchestrator>();
        _restartFailureAfterPointOfNoReturn = orchestrator != null &&
            orchestrator.LastRuntimeRestartFailureWasAfterPointOfNoReturn;
        if (string.IsNullOrWhiteSpace(_restartFailureReason))
        {
            _restartFailureReason = orchestrator != null && !string.IsNullOrWhiteSpace(orchestrator.LastRuntimeRestartFailureReason)
                ? orchestrator.LastRuntimeRestartFailureReason
                : "orchestrator_restart_returned_false";
        }

        var failurePayload = BuildPausePayload("restart_failure_presented");
        failurePayload["failure_reason"] = _restartFailureReason;
        TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_restart_failure_presented", failurePayload);
        OpenPause();
    }

    private void SaveAndExit()
    {
        _state.RequestSaveExit();
        Debug.Log($"{LogPrefix} pause_save_return_to_start_confirm_requested | scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_save_and_exit_confirm_requested", BuildPausePayload("save_exit_confirm_requested"));
        Render();
    }

    private void ConfirmSaveAndExit()
    {
        _state.ConfirmSaveExit();
        Debug.Log($"{LogPrefix} pause_save_return_to_start_confirmed | scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_save_and_exit_requested", BuildPausePayload("save_and_exit_requested"));
        Debug.Log($"{LogPrefix} save_return_to_start_confirmed | scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        BeginDeferredDestructiveAction("save_return_to_start", DeferredExitCoroutine(ExperimentSessionIdHistoryStore.SavedExitStatus, "save_and_return_to_start"));
    }

    private void RequestExitWithoutCompletion()
    {
        _state.RequestExitWithoutCompletion();
        Debug.Log($"{LogPrefix} pause_exit_to_start_without_save_requested | scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        TiagoExperimentTelemetry.LogEvent("pause_exit_to_start_without_save_requested", BuildPausePayload("exit_to_start_without_save_requested"));
        TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_exit_without_completion_requested", BuildPausePayload("exit_without_completion_requested"));
        Render();
    }

    private void ConfirmExitWithoutCompletion()
    {
        _state.ConfirmExitWithoutCompletion();
        Debug.Log($"{LogPrefix} pause_exit_to_start_without_save_confirmed | scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        TiagoExperimentTelemetry.LogEvent("pause_exit_to_start_without_save_confirmed", BuildPausePayload("exit_to_start_without_save_confirmed"));
        TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_exit_without_completion_confirmed", BuildPausePayload("exit_without_completion_confirmed"));
        BeginDeferredDestructiveAction("exit_to_start_without_save", DeferredExitToStartWithoutSaveCoroutine());
    }

    private IEnumerator DeferredExitToStartWithoutSaveCoroutine()
    {
        yield return null;
        bool completed = false;
        try
        {
            completed = ExitToStartWithoutSave();
        }
        catch (Exception ex)
        {
            Debug.LogError($"{LogPrefix} pause_exit_to_start_without_save_failed | error={ex.GetType().Name}:{ex.Message} scene={SceneManager.GetActiveScene().name}");
            TiagoExperimentTelemetry.LogEvent("pause_exit_to_start_without_save_failed", BuildPausePayload("exit_to_start_without_save_failed:" + ex.GetType().Name));
        }

        Debug.Log($"{LogPrefix} pause_exit_to_start_without_save_completed | completed={completed} scene={SceneManager.GetActiveScene().name} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        TiagoExperimentTelemetry.LogEvent("pause_exit_to_start_without_save_completed", BuildPausePayload(completed ? "exit_to_start_without_save_completed" : "exit_to_start_without_save_failed"));
        _deferredDestructiveActionCoroutine = null;
    }

    private IEnumerator DeferredExitCoroutine(string status, string reason)
    {
        yield return null;
        try
        {
            EndSessionForExit(status, reason);
        }
        catch (Exception ex)
        {
            Debug.LogError($"{LogPrefix} pause_destructive_action_failed | action={reason ?? string.Empty} status={status ?? string.Empty} error={ex.GetType().Name}:{ex.Message} scene={SceneManager.GetActiveScene().name}");
        }

        _deferredDestructiveActionCoroutine = null;
    }

    private void BeginDeferredDestructiveAction(string actionName, IEnumerator coroutine)
    {
        if (_deferredDestructiveActionCoroutine != null)
        {
            Debug.LogWarning($"{LogPrefix} pause_destructive_action_ignored_already_pending | action={actionName ?? string.Empty} scene={SceneManager.GetActiveScene().name}");
            return;
        }

        Debug.Log($"{UiLogPrefix} pause_action_deferred_after_click | action={actionName ?? string.Empty} scene={SceneManager.GetActiveScene().name} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        if (_frontPointerBridge != null)
        {
            _frontPointerBridge.Deactivate("before_destructive_action_" + (actionName ?? string.Empty));
        }

        Debug.Log($"{UiLogPrefix} pause_bridge_disabled_before_destructive_action | action={actionName ?? string.Empty} bridge_active={(_frontPointerBridge != null && _frontPointerBridge.IsBridgeActive)} scene={SceneManager.GetActiveScene().name} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        if (_canvasGroup != null)
        {
            _canvasGroup.interactable = false;
            _canvasGroup.blocksRaycasts = false;
        }

        SetVisible(false);
        _pauseCoordinator?.CleanupPauseState("pause_menu_" + (actionName ?? "destructive_action"));
        _deferredDestructiveActionCoroutine = StartCoroutine(coroutine);
    }

    private void EndSessionForExit(string status, string reason)
    {
        bool sessionEndRequested = false;
        ExperimentRuntimeProtocolUI protocolUi = null;
        try
        {
            if (string.Equals(status, ExperimentSessionIdHistoryStore.SavedExitStatus, StringComparison.Ordinal))
            {
                if (!PersistSavedExitCheckpoint(reason))
                {
                    Debug.LogError($"{LogPrefix} pause_save_exit_aborted | reason=checkpoint_persistence_rejected session_id={ExperimentDataPathResolver.CurrentSessionId}");
                    return;
                }
            }

            protocolUi = FindFirstObjectByType<ExperimentRuntimeProtocolUI>(FindObjectsInactive.Include);
            if (protocolUi != null)
            {
                protocolUi.EndSessionFromPauseMenu(status, reason);
                sessionEndRequested = true;
            }
            else
            {
                ExperimentSessionOrchestrator orchestrator = FindFirstObjectByType<ExperimentSessionOrchestrator>();
                if (orchestrator != null)
                {
                    orchestrator.EndSessionFromRuntime(status, reason);
                    sessionEndRequested = true;
                }
                else if (ExperimentDataPathResolver.HasActiveSession)
                {
                    ExperimentDataPathResolver.EndCurrentSession(status);
                    sessionEndRequested = true;
                    Debug.LogWarning($"{LogPrefix} exit_used_data_path_resolver_fallback | status={status} reason={reason}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"{LogPrefix} exit_session_integration_failed_fail_soft | status={status} reason={reason} error={ex.GetType().Name}:{ex.Message}");
            TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_exit_integration_failed", BuildPausePayload("exit_integration_failed:" + ex.GetType().Name));
        }

        if (string.Equals(status, ExperimentSessionIdHistoryStore.SavedExitStatus, StringComparison.Ordinal))
        {
            Debug.Log($"{LogPrefix} pause_save_exit_confirmed | status={status} reason={reason} session_end_requested={sessionEndRequested} scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            Debug.Log($"{LogPrefix} saved_exit_persisted | status={status} reason={reason} session_end_requested={sessionEndRequested} scene={SceneManager.GetActiveScene().name} history_path={ExperimentSessionIdHistoryStore.DefaultPath}");
            Debug.Log($"{LogPrefix} pause_save_return_to_start_completed | status={status} reason={reason} session_end_requested={sessionEndRequested} scene={SceneManager.GetActiveScene().name}");
            TiagoExperimentTelemetry.LogEvent("pause_save_return_to_start_completed", BuildPausePayload(reason));
            if (protocolUi != null)
            {
                protocolUi.ReturnToStartScreenFromPauseMenu("pause_save_return_to_start");
            }
            else
            {
                SceneManager.LoadScene("experiment_start_scene");
            }

            return;
        }

        Debug.Log($"{LogPrefix} exit_final_application_quit | status={status} reason={reason} session_end_requested={sessionEndRequested}");
        TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_exit_requested", BuildPausePayload(reason));
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void Render()
    {
        ClearContent();
        AddTitle(_state.Confirmation == ExperimentPauseMenuConfirmation.None ? "Pausa" :
            _state.Confirmation == ExperimentPauseMenuConfirmation.Restart ? "Reiniciar sesion experimental" :
            _state.Confirmation == ExperimentPauseMenuConfirmation.SaveExit ? "Guardar y volver al inicio" : "Confirmar salida");
        AddSessionIds();
        AddSpacer(8f);
        if (_state.Confirmation == ExperimentPauseMenuConfirmation.Restart)
        {
            AddBody("Se cerrara el intento actual como reiniciado y se comenzara una nueva sesion desde la Prueba 1, Ronda 1. Quieres continuar?");
            AddButton("Reiniciar sesion", ConfirmRestart, ExperimentButtonRole.Warning, true);
            AddButton("Cancelar", () =>
            {
                _state.CancelConfirmation();
                Debug.Log($"{LogPrefix} pause_restart_confirm_cancelled | scene={SceneManager.GetActiveScene().name} session={ExperimentDataPathResolver.CurrentSessionId}");
                TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_restart_cancelled", BuildPausePayload("restart_cancelled"));
                Render();
            }, ExperimentButtonRole.Secondary, true);
            LogP46HCanvasLayoutConfigured("render_restart_confirmation");
            return;
        }

        if (_state.Confirmation == ExperimentPauseMenuConfirmation.SaveExit)
        {
            AddBody("Se guardara la sesion actual como interrumpida y volveras a la pantalla inicial. Podras continuar mas adelante desde la recuperacion.");
            AddButton("Guardar y volver al inicio", ConfirmSaveAndExit, ExperimentButtonRole.Primary, true);
            AddButton("Cancelar", () =>
            {
                _state.CancelConfirmation();
                Debug.Log($"{LogPrefix} pause_save_exit_confirm_cancelled | scene={SceneManager.GetActiveScene().name} session={ExperimentDataPathResolver.CurrentSessionId}");
                TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_save_exit_cancelled", BuildPausePayload("save_exit_cancelled"));
                Render();
            }, ExperimentButtonRole.Secondary, true);
            LogP46HCanvasLayoutConfigured("render_save_exit_confirmation");
            return;
        }

        if (_state.Confirmation == ExperimentPauseMenuConfirmation.ExitWithoutCompletion)
        {
            AddBody("Volveras a la pantalla inicial sin guardar un punto de recuperacion de esta sesion. Seguro que quieres salir al inicio?");
            AddButton("Salir al inicio", ConfirmExitWithoutCompletion, ExperimentButtonRole.Destructive, true);
            AddButton("Cancelar", () =>
            {
                _state.CancelConfirmation();
                Debug.Log($"{LogPrefix} exit_without_completion_cancelled | scene={SceneManager.GetActiveScene().name} session={ExperimentDataPathResolver.CurrentSessionId}");
                TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_exit_without_completion_cancelled", BuildPausePayload("exit_without_completion_cancelled"));
                Render();
            }, ExperimentButtonRole.Secondary, true);
            LogP46HCanvasLayoutConfigured("render_exit_without_save_confirmation");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_restartFailureReason))
        {
            if (_restartFailureAfterPointOfNoReturn)
            {
                AddBody("No se pudo iniciar la nueva sesion. La sesion anterior ya se ha cerrado y su estado se ha limpiado.");
                AddBody("Motivo tecnico: " + _restartFailureReason);
                AddBody("Vuelve al inicio para decidir como iniciar una sesion completamente nueva.");
                AddSpacer(8f);
                AddButton("Volver al inicio", ReturnToStartAfterLateRestartFailure, ExperimentButtonRole.Primary, true);
                LogP46HCanvasLayoutConfigured("render_late_restart_failure");
                return;
            }

            AddBody("No se pudo reiniciar la sesion. La sesion actual no se ha modificado.");
            AddBody("Motivo tecnico: " + _restartFailureReason);
            AddBody("Puedes continuar con la sesion actual o volver al inicio.");
            AddSpacer(8f);
        }

        AddButton("Continuar", Continue, ExperimentButtonRole.Primary, true);
        AddButton("Reiniciar sesion experimental", RequestRestart, ExperimentButtonRole.Warning, true);
        AddButton("Guardar y volver al inicio", SaveAndExit, ExperimentButtonRole.Secondary, true);
        AddButton("Salir al inicio sin guardar", RequestExitWithoutCompletion, ExperimentButtonRole.Destructive, true);
        LogP46HCanvasLayoutConfigured("render_pause_menu");
    }

    private void ReturnToStartAfterLateRestartFailure()
    {
        Debug.Log($"{LogPrefix} late_restart_failure_return_to_start | reason={_restartFailureReason} scene={SceneManager.GetActiveScene().name}");
        TiagoExperimentTelemetry.LogEvent(
            "experiment_pause_menu_late_restart_failure_return_to_start",
            BuildPausePayload("late_restart_failure_return_to_start"));
        _state.Continue();
        BeginDeferredDestructiveAction(
            "late_restart_failure_return_to_start",
            DeferredExitToStartWithoutSaveCoroutine());
    }

    private bool ExitToStartWithoutSave()
    {
        Debug.Log($"{LogPrefix} pause_exit_to_start_without_save_begin | scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode}");
        ExperimentRuntimeProtocolUI protocolUi = FindFirstObjectByType<ExperimentRuntimeProtocolUI>(FindObjectsInactive.Include);
        if (protocolUi != null)
        {
            protocolUi.ExitToStartWithoutSaveFromPauseMenu();
            return true;
        }

        ExperimentSessionOrchestrator orchestrator = FindFirstObjectByType<ExperimentSessionOrchestrator>();
        if (orchestrator != null)
        {
            orchestrator.EndSessionFromRuntime(ExperimentSessionIdHistoryStore.ClosedWithoutCompletionStatus, "exit_to_start_without_save");
            SceneManager.LoadScene("experiment_start_scene");
            return true;
        }

        if (ExperimentDataPathResolver.HasActiveSession)
        {
            ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.ClosedWithoutCompletionStatus);
        }

        SceneManager.LoadScene("experiment_start_scene");
        return true;
    }

    private bool TryRestartExperimentalSession()
    {
        try
        {
            Debug.Log($"{LogPrefix} experimental_session_restart_requested | scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode}");
            ExperimentRuntimeProtocolUI protocolUi = FindFirstObjectByType<ExperimentRuntimeProtocolUI>(FindObjectsInactive.Include);
            if (protocolUi != null)
            {
                return protocolUi.RestartSessionFromPauseMenu();
            }

            ExperimentSessionOrchestrator orchestrator = FindFirstObjectByType<ExperimentSessionOrchestrator>();
            if (orchestrator != null)
            {
                return orchestrator.RestartSessionFromRuntime("pause_menu_restart");
            }

            Debug.LogError($"{LogPrefix} restart_integration_missing | scene={SceneManager.GetActiveScene().name}");
            TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_restart_integration_missing", BuildPausePayload("restart_integration_missing"));
            return false;
        }
        catch (Exception ex)
        {
            Debug.LogError($"{LogPrefix} restart_integration_failed_fail_soft | error={ex.GetType().Name}:{ex.Message}");
            TiagoExperimentTelemetry.LogEvent("experiment_pause_menu_restart_integration_failed", BuildPausePayload("restart_integration_failed:" + ex.GetType().Name));
            return false;
        }
    }

    private bool PersistSavedExitCheckpoint(string reason)
    {
        try
        {
            ExperimentSessionOrchestrator orchestrator = FindFirstObjectByType<ExperimentSessionOrchestrator>();
            ExperimentRuntimeProtocolSnapshot snapshot = orchestrator != null ? orchestrator.GetRuntimeProtocolSnapshot() : null;
            bool saveCurrentOperation = snapshot != null && (snapshot.TrialActive || snapshot.TrialTransitionBusy);
            string conditionId = saveCurrentOperation
                ? snapshot.CurrentConditionId
                : !string.IsNullOrWhiteSpace(snapshot?.NextConditionId)
                    ? snapshot.NextConditionId
                    : snapshot?.CurrentConditionId ?? string.Empty;
            Debug.Log($"{LogPrefix} saved_exit_checkpoint_source_snapshot | reason={reason ?? string.Empty} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} current_condition_id={snapshot?.CurrentConditionId ?? string.Empty} next_condition_id={snapshot?.NextConditionId ?? string.Empty} current_round={snapshot?.CurrentRoundIndexWithinCondition ?? 0} next_round={snapshot?.NextRoundIndexWithinCondition ?? 0} trial_active={snapshot != null && snapshot.TrialActive} has_next_trial={snapshot != null && snapshot.HasNextTrial}");
            if (string.IsNullOrWhiteSpace(conditionId))
            {
                Debug.LogError($"{LogPrefix} saved_exit_checkpoint_missing_condition | reason={reason ?? string.Empty} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} current_condition_id={snapshot?.CurrentConditionId ?? string.Empty} next_condition_id={snapshot?.NextConditionId ?? string.Empty}");
                Debug.LogError($"{LogPrefix} saved_exit_checkpoint_validation_failed | reason=condition_id_empty session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode}");
                return false;
            }

            int visiblePrueba = ResolveVisiblePrueba(orchestrator, conditionId, snapshot);
            int roundIndex = Mathf.Max(1, saveCurrentOperation
                ? snapshot?.CurrentRoundIndexWithinCondition ?? 1
                : snapshot?.NextRoundIndexWithinCondition ?? 1);
            string conditionOrder = snapshot?.ConditionOrderSummary ?? string.Empty;
            IReadOnlyList<string> conditionOrderIds = orchestrator != null
                ? new List<string>(orchestrator.RuntimeConditionOrderIds)
                : Array.Empty<string>();
            int conditionOrderIndex = saveCurrentOperation && snapshot != null && snapshot.CurrentConditionOrderIndex >= 0
                ? snapshot.CurrentConditionOrderIndex
                : snapshot?.NextConditionOrderIndex ?? Math.Max(0, visiblePrueba - 1);
            int internalTrialAttemptIndex = snapshot?.InternalTrialAttemptIndex ?? 0;
            string partialTrialCloseReason = snapshot != null && snapshot.TrialActive && !snapshot.RoundCompleted
                ? ExperimentSessionIdHistoryStore.SavedExitIncompleteConditionRestartReason
                : string.Empty;
            bool checkpointPersisted = ExperimentSessionIdHistoryStore.UpdateSavedExitCheckpoint(
                ExperimentDataPathResolver.CurrentSessionId,
                conditionId,
                visiblePrueba,
                roundIndex,
                conditionOrder,
                "condition_start",
                conditionOrderIds,
                conditionOrderIndex,
                internalTrialAttemptIndex,
                partialTrialCloseReason);
            if (!checkpointPersisted)
            {
                Debug.LogError($"{LogPrefix} saved_exit_checkpoint_validation_failed | reason=history_identity_or_status_rejected session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode}");
                return false;
            }

            string persistedOrder = string.Join(" -> ", conditionOrderIds);
            Debug.Log($"{LogPrefix} saved_exit_checkpoint_validation_passed | session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} saved_condition_id={conditionId} saved_condition_order_index={conditionOrderIndex} saved_visible_prueba={visiblePrueba} saved_round_index={roundIndex} internal_trial_attempt_index={internalTrialAttemptIndex}");
            Debug.Log($"{LogPrefix} saved_exit_checkpoint_persisted | reason={reason ?? string.Empty} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} saved_condition_id={conditionId} saved_condition_order_index={conditionOrderIndex} saved_visible_prueba={visiblePrueba} saved_round_index={roundIndex} resume_policy=condition_start saved_exit_original_condition_order={persistedOrder} partial_trial_close_reason={partialTrialCloseReason} internal_trial_attempt_index={internalTrialAttemptIndex}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"{LogPrefix} saved_exit_checkpoint_persist_failed_fail_soft | reason={reason ?? string.Empty} session_id={ExperimentDataPathResolver.CurrentSessionId} error={ex.GetType().Name}:{ex.Message}");
            return false;
        }
    }

    private static int ResolveVisiblePrueba(
        ExperimentSessionOrchestrator orchestrator,
        string conditionId,
        ExperimentRuntimeProtocolSnapshot snapshot)
    {
        if (orchestrator != null && !string.IsNullOrWhiteSpace(conditionId))
        {
            IReadOnlyList<string> order = orchestrator.RuntimeConditionOrderIds;
            for (int i = 0; i < order.Count; i++)
            {
                if (string.Equals(order[i], conditionId, StringComparison.Ordinal))
                {
                    return i + 1;
                }
            }
        }

        if (snapshot != null && snapshot.RoundsPerCondition > 0 && snapshot.CurrentGlobalRoundIndex > 0)
        {
            return Mathf.Max(1, ((snapshot.CurrentGlobalRoundIndex - 1) / snapshot.RoundsPerCondition) + 1);
        }

        return 1;
    }

    private void AddSessionIds()
    {
        ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.CurrentSession;
        string fullId = !string.IsNullOrWhiteSpace(context.SessionId) ? context.SessionId : "(sin sesion activa)";
        string code = !string.IsNullOrWhiteSpace(context.QuestionnaireCode) ? context.QuestionnaireCode : "(pendiente)";
        AddBody($"ID completo: {fullId}");
        AddBody($"Codigo cuestionario: {code}");
    }

    private Dictionary<string, object> BuildPausePayload(string reason)
    {
        ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.CurrentSession;
        return new Dictionary<string, object>
        {
            ["reason"] = reason ?? string.Empty,
            ["scene"] = SceneManager.GetActiveScene().name,
            ["participant_id"] = context.ParticipantId,
            ["session_id"] = context.SessionId,
            ["questionnaire_code"] = context.QuestionnaireCode,
            ["paused"] = ExperimentRuntimePauseCoordinator.IsExperimentPaused,
            ["confirmation"] = _state.Confirmation.ToString()
        };
    }

    private string BuildRuntimeStateSnapshot(string eventName, out string message)
    {
        ExperimentSessionOrchestrator orchestrator = FindFirstObjectByType<ExperimentSessionOrchestrator>();
        ExperimentRuntimeProtocolSnapshot snapshot = orchestrator != null ? orchestrator.GetRuntimeProtocolSnapshot() : null;
        AutonomousRobotAdapter robot = FindFirstObjectByType<AutonomousRobotAdapter>();
        string taskStatus = string.Empty;
        string blackboardCurrentTarget = string.Empty;
        string blackboardPlaceTarget = string.Empty;
        string robotMode = string.Empty;
        if (robot != null && robot.Blackboard != null)
        {
            robotMode = robot.Blackboard.CurrentMode.ToString();
            if (robot.Blackboard.TryGet(Autonomy.Domain.TaskBlackboardKeys.LastTaskStatus, out Autonomy.Domain.TaskStatus status))
            {
                taskStatus = status.ToString();
            }

            if (robot.Blackboard.TryGet(Autonomy.Domain.TaskBlackboardKeys.CurrentTarget, out Autonomy.Domain.TargetDescriptor currentTarget) && currentTarget != null)
            {
                blackboardCurrentTarget = currentTarget.Id ?? string.Empty;
            }

            if (robot.Blackboard.TryGet(Autonomy.Domain.TaskBlackboardKeys.PlaceTarget, out Autonomy.Domain.TargetDescriptor placeTarget) && placeTarget != null)
            {
                blackboardPlaceTarget = placeTarget.Id ?? string.Empty;
            }
        }

        SpawnManager spawnManager = FindFirstObjectByType<SpawnManager>();
        int boxCount = 0;
        if (spawnManager != null && spawnManager.activeRoundContainer != null)
        {
            boxCount = spawnManager.activeRoundContainer.GetComponentsInChildren<BoxMetadata>(true).Length;
        }

        string signature =
            $"condition={snapshot?.CurrentConditionId ?? string.Empty};trial={snapshot?.CurrentTrialId ?? string.Empty};round={snapshot?.CurrentRoundIndexWithinCondition ?? 0};" +
            $"trial_active={snapshot != null && snapshot.TrialActive};transition={snapshot != null && snapshot.TrialTransitionBusy};round_active={snapshot != null && snapshot.RoundActive};" +
            $"robot_mode={robotMode};current_task={robot?.ActiveP40TaskInstanceId ?? string.Empty};pending_task={robot?.ActiveP40RequestId ?? string.Empty};" +
            $"task_status={taskStatus};held={robot?.HeldObjectId ?? string.Empty};target={robot?.ActiveP40TargetId ?? string.Empty};blackboard={blackboardCurrentTarget};place={blackboardPlaceTarget};" +
            $"box_count={boxCount}";
        message =
            $"{LogPrefix} {eventName} | scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} " +
            $"condition_id={snapshot?.CurrentConditionId ?? string.Empty} trial_index={snapshot?.CurrentGlobalRoundIndex ?? 0} round_index={snapshot?.CurrentRoundIndexWithinCondition ?? 0} " +
            $"robot_mode={robotMode} current_task={robot?.ActiveP40TaskInstanceId ?? string.Empty} pending_task={robot?.ActiveP40RequestId ?? string.Empty} task_status={taskStatus} " +
            $"held_object_id={robot?.HeldObjectId ?? string.Empty} current_target_id={robot?.ActiveP40TargetId ?? string.Empty} blackboard_target={blackboardCurrentTarget} navigation_active_request={robot?.ActiveP40RequestId ?? string.Empty} " +
            $"active_round_path={GetPath(spawnManager != null ? spawnManager.activeRoundContainer : null)} box_count={boxCount} timeScale={Time.timeScale:0.###} pause_isPaused={_state.IsPaused} confirmation={_state.Confirmation} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}";
        return signature;
    }

    private static bool IsLeftControllerYPressed(out ExperimentPauseInputDeviceSelection selection, out IReadOnlyList<ExperimentPauseInputDeviceSnapshot> snapshots)
    {
        List<InputDevice> devices = new();
        AddDevicesWithCharacteristics(InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.HeldInHand, devices);
        AddDevicesWithCharacteristics(InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller, devices);
        var allDevices = new List<InputDevice>();
        InputDevices.GetDevices(allDevices);
        foreach (InputDevice device in allDevices)
        {
            AddDeviceIfMissing(devices, device);
        }

        var deviceSnapshots = new List<ExperimentPauseInputDeviceSnapshot>();
        foreach (InputDevice device in devices)
        {
            deviceSnapshots.Add(BuildSnapshot(device));
        }

        snapshots = deviceSnapshots;
        selection = ExperimentPauseInputDeviceResolver.SelectBestLeftController(deviceSnapshots);
        return selection.Found &&
            selection.Device.SecondaryButtonSupported &&
            selection.Device.SecondaryButtonPressed;
    }

    private static void AddDevicesWithCharacteristics(
        InputDeviceCharacteristics characteristics,
        List<InputDevice> devices)
    {
        var matches = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(characteristics, matches);
        foreach (InputDevice match in matches)
        {
            AddDeviceIfMissing(devices, match);
        }
    }

    private static void AddDeviceIfMissing(List<InputDevice> devices, InputDevice candidate)
    {
        foreach (InputDevice existing in devices)
        {
            if (string.Equals(existing.name, candidate.name, StringComparison.Ordinal) &&
                existing.characteristics == candidate.characteristics)
            {
                return;
            }
        }

        devices.Add(candidate);
    }

    private static ExperimentPauseInputDeviceSnapshot BuildSnapshot(InputDevice device)
    {
        bool isTracked = device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked;
        bool secondarySupported = device.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondaryPressed);
        bool primarySupported = device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primaryPressed);
        bool menuSupported = device.TryGetFeatureValue(CommonUsages.menuButton, out bool menuPressed);
        return new ExperimentPauseInputDeviceSnapshot(
            device.name,
            device.characteristics,
            device.isValid,
            isTracked,
            secondarySupported,
            secondarySupported && secondaryPressed,
            primarySupported,
            primarySupported && primaryPressed,
            menuSupported,
            menuSupported && menuPressed);
    }

    private void MaybeLogDeviceSnapshot(
        ExperimentPauseInputDeviceSelection selection,
        IReadOnlyList<ExperimentPauseInputDeviceSnapshot> snapshots)
    {
        if (Time.unscaledTime < _nextDeviceSnapshotLogAt)
        {
            return;
        }

        _nextDeviceSnapshotLogAt = Time.unscaledTime + DeviceSnapshotLogIntervalSeconds;
        if (selection.Found)
        {
            Debug.Log($"{LogPrefix} left_controller_candidate_found | selected_index={selection.Index} score={selection.Score} {FormatDevice(selection.Device)} all_devices={FormatDevices(snapshots)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            Debug.Log($"{LogPrefix} left_controller_y_feature_supported | supported={selection.Device.SecondaryButtonSupported} value={selection.Device.SecondaryButtonPressed} {FormatDevice(selection.Device)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            if (!selection.Device.SecondaryButtonSupported && !_secondaryUnsupportedLogged)
            {
                _secondaryUnsupportedLogged = true;
                Debug.LogWarning($"{LogPrefix} left_controller_y_feature_supported | supported=False value=False warning=selected_controller_has_no_secondaryButton {FormatDevice(selection.Device)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            }
        }
        else
        {
            Debug.LogWarning($"{LogPrefix} left_controller_candidate_missing | all_devices={FormatDevices(snapshots)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        }

        Debug.Log($"{LogPrefix} left_controller_device_snapshot | candidate_found={selection.Found} all_devices={FormatDevices(snapshots)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
    }

    private void LogDeviceSelectionOnChange(ExperimentPauseInputDeviceSelection selection)
    {
        string deviceName = selection.Found ? selection.Device.Name ?? string.Empty : string.Empty;
        bool changed = !_deviceSelectionStateInitialized ||
            _lastDeviceSelectionFound != selection.Found ||
            _lastDeviceSelectionValid != selection.Device.IsValid ||
            _lastDeviceSelectionTracked != selection.Device.IsTracked ||
            _lastDeviceSelectionSecondarySupported != selection.Device.SecondaryButtonSupported ||
            !string.Equals(_lastDeviceSelectionName, deviceName, StringComparison.Ordinal);
        if (!changed)
        {
            return;
        }

        _deviceSelectionStateInitialized = true;
        _lastDeviceSelectionFound = selection.Found;
        _lastDeviceSelectionValid = selection.Device.IsValid;
        _lastDeviceSelectionTracked = selection.Device.IsTracked;
        _lastDeviceSelectionSecondarySupported = selection.Device.SecondaryButtonSupported;
        _lastDeviceSelectionName = deviceName;

        if (!selection.Found)
        {
            Debug.LogWarning($"{LogPrefix} left_controller_state_changed | candidate_found=False frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            return;
        }

        if (!selection.Device.SecondaryButtonSupported)
        {
            Debug.LogWarning($"{LogPrefix} left_controller_state_changed | candidate_found=True secondary_button_supported=False {FormatDevice(selection.Device)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            return;
        }

        Debug.Log($"{LogPrefix} left_controller_state_changed | candidate_found=True secondary_button_supported=True {FormatDevice(selection.Device)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
    }

    private bool BuildCanvas()
    {
        if (_content != null)
        {
            return true;
        }

        _usesRuntimeProtocolCanvas = false;
        Transform existingCanvas = transform.Find("ExperimentPauseFrontCanvas");
        GameObject canvasObject = existingCanvas != null ? existingCanvas.gameObject : new GameObject("ExperimentPauseFrontCanvas", typeof(RectTransform));
        canvasObject.transform.SetParent(transform, false);
        _canvas = canvasObject.GetComponent<Canvas>();
        if (_canvas == null)
        {
            _canvas = canvasObject.AddComponent<Canvas>();
        }

        _canvas.renderMode = RenderMode.WorldSpace;
        _canvas.sortingOrder = 500;
        _canvas.worldCamera = Camera.main ?? FindFirstObjectByType<Camera>();
        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        if (scaler == null)
        {
            scaler = canvasObject.AddComponent<CanvasScaler>();
        }

        scaler.dynamicPixelsPerUnit = 14f;

        RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
        canvasRect.anchorMin = new Vector2(0.5f, 0.5f);
        canvasRect.anchorMax = new Vector2(0.5f, 0.5f);
        canvasRect.pivot = new Vector2(0.5f, 0.5f);
        canvasRect.sizeDelta = _panelSize;
        canvasRect.localScale = Vector3.one * FrontCanvasScale;

        Transform existingModal = canvasObject.transform.Find(ModalName);
        GameObject modal = existingModal != null ? existingModal.gameObject : new GameObject(ModalName, typeof(RectTransform));
        modal.transform.SetParent(canvasObject.transform, false);
        modal.transform.SetAsLastSibling();

        RectTransform rect = modal.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.localScale = Vector3.one;
        rect.localPosition = Vector3.zero;
        _pauseRoot = rect;
        ClearChildrenImmediate(modal.transform);

        _canvasGroup = modal.GetComponent<CanvasGroup>();
        if (_canvasGroup == null)
        {
            _canvasGroup = modal.AddComponent<CanvasGroup>();
        }

        if (_canvasGroup == null)
        {
            Debug.LogError($"{UiLogPrefix} pause_ui_canvas_group_missing_after_add | scene={SceneManager.GetActiveScene().name} path={GetPath(modal.transform)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        }

        EnsureCanvasInfrastructure(_canvas.gameObject);
        ExperimentUiRaycastHitMarker.EnsureAttached(gameObject);
        _frontPointerBridge = ExperimentPauseFrontCanvasPointerBridge.Ensure(canvasObject, _canvas);
        Debug.Log($"{UiLogPrefix} pause_ui_front_canvas_created_or_found | scene={SceneManager.GetActiveScene().name} canvas_path={GetPath(_canvas.transform)} panel_path={GetPath(_pauseRoot)} has_own_canvas={_canvas != null} renderMode={_canvas.renderMode} uses_runtime_protocol_canvas=False frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");

        var overlay = new GameObject("ModalOverlay");
        overlay.transform.SetParent(modal.transform, false);
        Image overlayImage = overlay.AddComponent<Image>();
        overlayImage.color = PauseMenuModalOverlayColor;
        overlayImage.raycastTarget = true;
        RectTransform overlayRect = overlayImage.rectTransform;
        overlayRect.anchorMin = Vector2.zero;
        overlayRect.anchorMax = Vector2.one;
        overlayRect.offsetMin = Vector2.zero;
        overlayRect.offsetMax = Vector2.zero;
        var panel = new GameObject("Panel");
        panel.transform.SetParent(modal.transform, false);
        panel.transform.SetAsLastSibling();
        Image image = panel.AddComponent<Image>();
        image.color = PauseMenuPanelBackgroundColor;
        image.raycastTarget = true;
        RectTransform panelRect = image.rectTransform;
        panelRect.anchorMin = new Vector2(0.5f, 0.5f);
        panelRect.anchorMax = new Vector2(0.5f, 0.5f);
        panelRect.pivot = new Vector2(0.5f, 0.5f);
        panelRect.sizeDelta = new Vector2(PauseMenuPanelWidth, PauseMenuPanelHeight);
        panelRect.anchoredPosition = Vector2.zero;
        panelRect.localScale = Vector3.one;

        _content = CreateRect("Content", panel.transform);
        _content.anchorMin = Vector2.zero;
        _content.anchorMax = Vector2.one;
        _content.offsetMin = new Vector2(PanelSafeHorizontalPadding, PanelSafeVerticalPadding);
        _content.offsetMax = new Vector2(-PanelSafeHorizontalPadding, -PanelSafeVerticalPadding);
        VerticalLayoutGroup layout = _content.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = PauseMenuLayoutSpacing;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        SetUiLayerRecursively(canvasObject);
        Debug.Log($"[P46J-02] pause_menu_typography_configured | scene={SceneManager.GetActiveScene().name} controller_path={GetPath(transform)} modal_path={GetPath(_pauseRoot)} canvas_exists={_canvas != null} canvas_path={GetPath(_canvas.transform)} panel_size={panelRect.sizeDelta} uses_runtime_protocol_canvas={_usesRuntimeProtocolCanvas} title_font=50 body_font=29 button_font=32 text_mode=TextMeshProUGUI shared_material={ExperimentCanvasTypography.MaterialResourcePath} effects=none frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        LogPanelRect("build_canvas");
        LogUiState("build_canvas");
        LogP46HCanvasLayoutConfigured("build_canvas");
        return true;
    }

    private void SetVisible(bool visible)
    {
        if (_pauseRoot != null)
        {
            _pauseRoot.gameObject.SetActive(visible);
        }

        if (!visible)
        {
            _frontPointerBridge?.Deactivate("pause_menu_hidden");
        }

        if (_canvasGroup != null)
        {
            _canvasGroup.alpha = visible ? 1f : 0f;
            _canvasGroup.interactable = visible;
            _canvasGroup.blocksRaycasts = visible;
        }

        if (_canvas != null)
        {
            _canvas.enabled = visible;
        }
    }

    private void PositionInFrontOfUser()
    {
        if (_pauseRoot != null)
        {
            _pauseRoot.SetAsLastSibling();
        }

        if (_canvas == null)
        {
            return;
        }

        Camera camera = Camera.main ?? FindFirstObjectByType<Camera>();
        if (camera == null)
        {
            _canvas.transform.SetPositionAndRotation(new Vector3(0f, 1.45f, 1.45f), Quaternion.identity);
            Debug.LogWarning($"{UiLogPrefix} pause_ui_front_canvas_pose | camera_missing=True canvas_path={GetPath(_canvas.transform)} position={_canvas.transform.position} rotation={_canvas.transform.eulerAngles} distance_to_camera=-1 scene={SceneManager.GetActiveScene().name}");
            return;
        }

        Vector3 forward = camera.transform.forward;
        if (forward.sqrMagnitude < 0.001f)
        {
            forward = Vector3.forward;
        }

        forward.Normalize();
        Vector3 position = camera.transform.position + forward * FrontCanvasDistanceMeters;
        Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up);
        _canvas.transform.SetPositionAndRotation(position, rotation);
        _canvas.transform.localScale = Vector3.one * FrontCanvasScale;
        float distance = Vector3.Distance(camera.transform.position, _canvas.transform.position);
        Debug.Log($"{UiLogPrefix} pause_ui_front_canvas_pose | camera={camera.name} canvas_path={GetPath(_canvas.transform)} position={_canvas.transform.position} rotation={_canvas.transform.eulerAngles} distance_to_camera={distance:0.###} scene={SceneManager.GetActiveScene().name}");
        Debug.Log($"{UiLogPrefix} pause_ui_front_canvas_scale | canvas_path={GetPath(_canvas.transform)} localScale={_canvas.transform.localScale} lossyScale={_canvas.transform.lossyScale} renderMode={_canvas.renderMode} scene={SceneManager.GetActiveScene().name}");
    }

    private static void EnsureEventSystem()
    {
        EventSystem eventSystem = FindFirstObjectByType<EventSystem>();
        if (eventSystem == null)
        {
            eventSystem = new GameObject("EventSystem").AddComponent<EventSystem>();
        }

        XRUIInputModule xrInputModule = eventSystem.GetComponent<XRUIInputModule>();
        if (xrInputModule == null)
        {
            xrInputModule = eventSystem.gameObject.AddComponent<XRUIInputModule>();
        }

        xrInputModule.enabled = true;
        StandaloneInputModule standalone = eventSystem.GetComponent<StandaloneInputModule>();
        if (standalone != null)
        {
            standalone.enabled = false;
        }
    }

    private static void EnsureCanvasInfrastructure(GameObject target)
    {
        if (target == null)
        {
            return;
        }

        GraphicRaycaster graphicRaycaster = target.GetComponent<GraphicRaycaster>();
        if (graphicRaycaster == null)
        {
            graphicRaycaster = target.AddComponent<GraphicRaycaster>();
        }

        graphicRaycaster.enabled = true;
        EnsureTrackedDeviceGraphicRaycaster(target);
    }

    private static TrackedDeviceGraphicRaycaster EnsureTrackedDeviceGraphicRaycaster(GameObject target)
    {
        TrackedDeviceGraphicRaycaster raycaster = target.GetComponent<TrackedDeviceGraphicRaycaster>();
        if (raycaster == null)
        {
            raycaster = target.AddComponent<TrackedDeviceGraphicRaycaster>();
        }

        raycaster.enabled = true;
        raycaster.ignoreReversedGraphics = false;
        raycaster.checkFor3DOcclusion = false;
        raycaster.checkFor2DOcclusion = false;
        return raycaster;
    }

    private void ClearContent()
    {
        if (_content == null)
        {
            Debug.LogWarning($"{UiLogPrefix} pause_ui_content_missing_rebuild_requested | scene={SceneManager.GetActiveScene().name} path={GetPath(transform)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            EnsureBuilt();
            if (_content == null)
            {
                Debug.LogError($"{UiLogPrefix} pause_ui_content_missing_after_rebuild | scene={SceneManager.GetActiveScene().name} path={GetPath(transform)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
                return;
            }
        }

        for (int i = _content.childCount - 1; i >= 0; i--)
        {
            Destroy(_content.GetChild(i).gameObject);
        }
    }

    private bool EnsureBuilt()
    {
        if (_content == null)
        {
            if (!BuildCanvas())
            {
                return false;
            }

            SetVisible(_state.IsPaused);
        }

        return _content != null && _canvas != null;
    }

    private bool ValidateFrontCanvasSizeGuard(string reason)
    {
        RectTransform rect = _canvas != null ? _canvas.GetComponent<RectTransform>() : null;
        if (rect == null)
        {
            _frontCanvasSizeGuardPassed = false;
            Debug.LogError($"{UiLogPrefix} pause_ui_front_canvas_size_guard_fail | reason={reason} rect_missing=True scene={SceneManager.GetActiveScene().name}");
            return false;
        }

        Vector2 size = CalculateWorldSize(rect);
        Debug.Log($"{UiLogPrefix} pause_ui_front_canvas_world_corners | reason={reason} canvas_path={GetPath(rect)} world_corners={FormatCorners(rect)} approx_width_m={size.x:0.###} approx_height_m={size.y:0.###} scene={SceneManager.GetActiveScene().name}");
        _frontCanvasSizeGuardPassed = size.x <= FrontCanvasMaxWorldMeters && size.y <= FrontCanvasMaxWorldMeters;
        if (_frontCanvasSizeGuardPassed)
        {
            Debug.Log($"{UiLogPrefix} pause_ui_front_canvas_size_guard_pass | reason={reason} canvas_path={GetPath(rect)} approx_width_m={size.x:0.###} approx_height_m={size.y:0.###} max_m={FrontCanvasMaxWorldMeters:0.###} scene={SceneManager.GetActiveScene().name}");
            return true;
        }

        Debug.LogError($"{UiLogPrefix} pause_ui_front_canvas_size_guard_fail | reason={reason} canvas_path={GetPath(rect)} approx_width_m={size.x:0.###} approx_height_m={size.y:0.###} max_m={FrontCanvasMaxWorldMeters:0.###} scene={SceneManager.GetActiveScene().name}");
        return false;
    }

    private void AddTitle(string text)
    {
        TextMeshProUGUI title = AddText(text, 50, FontStyles.Bold, PauseMenuTitleHeight);
        title.color = Color.white;
    }

    private void AddBody(string text)
    {
        TextMeshProUGUI body = AddText(text, 29, FontStyles.Normal, PauseMenuBodyHeight);
        ExperimentCanvasTypography.ApplyLegacyLineSpacing(body, 1.08f);
    }

    private TextMeshProUGUI AddText(string text, int fontSize, FontStyles style, float minHeight)
    {
        RectTransform rect = CreateRect("Text", _content);
        TextMeshProUGUI label = rect.gameObject.AddComponent<TextMeshProUGUI>();
        ExperimentCanvasTypography.Configure(label);
        label.text = text ?? string.Empty;
        label.fontSize = fontSize;
        label.fontStyle = style;
        label.color = new Color(0.92f, 0.94f, 0.95f, 1f);
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.textWrappingMode = TextWrappingModes.Normal;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        rect.gameObject.AddComponent<LayoutElement>().minHeight = minHeight;
        return label;
    }

    private void AddSpacer(float height)
    {
        RectTransform spacer = CreateRect("Spacer", _content);
        spacer.gameObject.AddComponent<LayoutElement>().minHeight = height;
    }

    private void AddButton(string label, Action action, ExperimentButtonRole role, bool enabled)
    {
        RectTransform rect = CreateRect("Button_" + label, _content);
        Image image = rect.gameObject.AddComponent<Image>();
        Button button = rect.gameObject.AddComponent<Button>();
        Action tracedAction = () =>
        {
            Debug.Log($"{UiLogPrefix} pause_ui_button_clicked | label=\"{label}\" path={GetPath(rect)} scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            Debug.Log($"{LogPrefix} listener_invoked | label=\"{label}\" path={GetPath(rect)} scene={SceneManager.GetActiveScene().name} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            action?.Invoke();
        };
        int listenerCount = ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(button, image, tracedAction, role, enabled);
        ExperimentPauseButtonClickTracer tracer = ExperimentPauseButtonClickTracer.Ensure(rect.gameObject, label, GetPath(rect));
        Debug.Log($"{UiLogPrefix} pause_ui_button_click_tracer_attached | label=\"{label}\" path={GetPath(rect)} tracer={tracer != null} scene={SceneManager.GetActiveScene().name}");
        TextMeshProUGUI text = CreateButtonText("Label", rect, label);
        text.alignment = TextAlignmentOptions.Center;
        text.fontStyle = FontStyles.Bold;
        text.raycastTarget = false;
        rect.gameObject.AddComponent<LayoutElement>().minHeight = PauseMenuButtonAreaHeight;
        Debug.Log($"{LogPrefix} button_listener_configured | label={label} role={role} listener_count={listenerCount} enabled={enabled}");
        LogButtonState(label, button, image, listenerCount);
    }

    private TextMeshProUGUI CreateButtonText(string name, Transform parent, string value)
    {
        RectTransform rect = CreateRect(name, parent);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(12f, 6f);
        rect.offsetMax = new Vector2(-12f, -6f);
        TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        ExperimentCanvasTypography.Configure(text);
        text.text = value ?? string.Empty;
        text.fontSize = 32;
        text.color = Color.white;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode = TextOverflowModes.Overflow;
        return text;
    }

    private static RectTransform CreateRect(string name, Transform parent)
    {
        var child = new GameObject(name);
        child.transform.SetParent(parent, false);
        return child.AddComponent<RectTransform>();
    }

    private static void ClearChildrenImmediate(Transform parent)
    {
        if (parent == null)
        {
            return;
        }

        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            GameObject child = parent.GetChild(i).gameObject;
            if (Application.isPlaying)
            {
                Destroy(child);
            }
            else
            {
                DestroyImmediate(child);
            }
        }
    }

    private Canvas ResolveRuntimeProtocolCanvas()
    {
        ExperimentRuntimeProtocolUI protocolUi = FindFirstObjectByType<ExperimentRuntimeProtocolUI>(FindObjectsInactive.Include);
        if (protocolUi != null &&
            protocolUi.gameObject.activeInHierarchy &&
            protocolUi.TryGetComponent(out Canvas protocolCanvas) &&
            protocolCanvas.enabled)
        {
            return protocolCanvas;
        }

        GameObject runtimeProtocolUi = GameObject.Find("RuntimeProtocolUI");
        if (runtimeProtocolUi != null &&
            runtimeProtocolUi.activeInHierarchy &&
            runtimeProtocolUi.TryGetComponent(out Canvas namedCanvas) &&
            namedCanvas.enabled)
        {
            return namedCanvas;
        }

        return null;
    }

    private void LogUiState(string reason)
    {
        if (_canvas == null)
        {
            Debug.LogWarning($"{UiLogPrefix} pause_ui_canvas_resolved | reason={reason} resolved=False scene={SceneManager.GetActiveScene().name} panel_path={GetPath(_pauseRoot)} controller_path={GetPath(transform)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            return;
        }

        GraphicRaycaster graphicRaycaster = _canvas.GetComponent<GraphicRaycaster>();
        TrackedDeviceGraphicRaycaster trackedRaycaster = _canvas.GetComponent<TrackedDeviceGraphicRaycaster>();
        EventSystem eventSystem = EventSystem.current ?? FindFirstObjectByType<EventSystem>();
        BaseInputModule inputModule = eventSystem != null ? eventSystem.currentInputModule : null;
        Debug.Log($"{UiLogPrefix} pause_ui_canvas_resolved | reason={reason} scene={SceneManager.GetActiveScene().name} canvas_path={GetPath(_canvas.transform)} panel_path={GetPath(_pauseRoot)} controller_path={GetPath(transform)} activeSelf={(_pauseRoot != null && _pauseRoot.gameObject.activeSelf)} activeInHierarchy={(_pauseRoot != null && _pauseRoot.gameObject.activeInHierarchy)} uses_runtime_protocol_canvas={_usesRuntimeProtocolCanvas} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode}");
        Debug.Log($"{UiLogPrefix} pause_ui_canvas_mode | renderMode={_canvas.renderMode} sortingOrder={_canvas.sortingOrder} worldCamera={(_canvas.worldCamera != null ? _canvas.worldCamera.name : string.Empty)} position={_canvas.transform.position} rotation={_canvas.transform.eulerAngles} scale={_canvas.transform.lossyScale}");
        Debug.Log($"{UiLogPrefix} pause_ui_canvas_parent | canvas_path={GetPath(_canvas.transform)} parent={(_canvas.transform.parent != null ? GetPath(_canvas.transform.parent) : string.Empty)} panel_parent={(_pauseRoot != null && _pauseRoot.parent != null ? GetPath(_pauseRoot.parent) : string.Empty)} sibling_index={(_pauseRoot != null ? _pauseRoot.GetSiblingIndex() : -1)}");
        Debug.Log($"{UiLogPrefix} pause_ui_canvas_world_corners | canvas_path={GetPath(_canvas.transform)} corners={FormatCorners(_canvas.GetComponent<RectTransform>())} panel_corners={FormatCorners(_pauseRoot)}");
        Debug.Log($"{UiLogPrefix} pause_ui_canvas_group_state | panel_path={GetPath(_pauseRoot)} alpha={(_canvasGroup != null ? _canvasGroup.alpha : -1f)} interactable={(_canvasGroup != null && _canvasGroup.interactable)} blocksRaycasts={(_canvasGroup != null && _canvasGroup.blocksRaycasts)} activeSelf={(_pauseRoot != null && _pauseRoot.gameObject.activeSelf)} activeInHierarchy={(_pauseRoot != null && _pauseRoot.gameObject.activeInHierarchy)}");
        Debug.Log($"{UiLogPrefix} pause_ui_graphic_raycaster_state | canvas_path={GetPath(_canvas.transform)} exists={graphicRaycaster != null} enabled={graphicRaycaster != null && graphicRaycaster.enabled}");
        Debug.Log($"{UiLogPrefix} pause_ui_tracked_device_graphic_raycaster_state | canvas_path={GetPath(_canvas.transform)} exists={trackedRaycaster != null} enabled={trackedRaycaster != null && trackedRaycaster.enabled} ignoreReversedGraphics={(trackedRaycaster != null && trackedRaycaster.ignoreReversedGraphics)} check3D={(trackedRaycaster != null && trackedRaycaster.checkFor3DOcclusion)} check2D={(trackedRaycaster != null && trackedRaycaster.checkFor2DOcclusion)}");
        Debug.Log($"{UiLogPrefix} pause_ui_front_canvas_raycaster_state | canvas_path={GetPath(_canvas.transform)} graphic_exists={graphicRaycaster != null} graphic_enabled={graphicRaycaster != null && graphicRaycaster.enabled} tracked_exists={trackedRaycaster != null} tracked_enabled={trackedRaycaster != null && trackedRaycaster.enabled} check3D={(trackedRaycaster != null && trackedRaycaster.checkFor3DOcclusion)} check2D={(trackedRaycaster != null && trackedRaycaster.checkFor2DOcclusion)} scene={SceneManager.GetActiveScene().name}");
        Debug.Log($"{UiLogPrefix} pause_ui_event_system_state | exists={eventSystem != null} name={(eventSystem != null ? eventSystem.name : string.Empty)} current={(EventSystem.current != null ? EventSystem.current.name : string.Empty)}");
        Debug.Log($"{UiLogPrefix} pause_ui_front_canvas_event_system_state | exists={eventSystem != null} name={(eventSystem != null ? eventSystem.name : string.Empty)} active_module={(inputModule != null ? inputModule.GetType().FullName : string.Empty)} scene={SceneManager.GetActiveScene().name}");
        Debug.Log($"{UiLogPrefix} pause_ui_input_module_state | active_module={(inputModule != null ? inputModule.GetType().FullName : string.Empty)} xr_module={DescribeComponent(eventSystem != null ? eventSystem.GetComponent<XRUIInputModule>() : null)} standalone_module={DescribeComponent(eventSystem != null ? eventSystem.GetComponent<StandaloneInputModule>() : null)}");

        Button[] buttons = _pauseRoot != null ? _pauseRoot.GetComponentsInChildren<Button>(true) : Array.Empty<Button>();
        foreach (Button button in buttons)
        {
            if (button == null)
            {
                continue;
            }

            Image image = button.targetGraphic as Image ?? button.GetComponent<Image>();
            LogButtonState(button.name, button, image, 1);
        }
    }

    private static void LogButtonState(string label, Button button, Image image, int listenerCount)
    {
        if (button == null)
        {
            return;
        }

        RectTransform rect = button.GetComponent<RectTransform>();
        Graphic targetGraphic = button.targetGraphic;
        Debug.Log($"{UiLogPrefix} pause_ui_button_state | label=\"{label}\" path={GetPath(button.transform)} activeSelf={button.gameObject.activeSelf} activeInHierarchy={button.gameObject.activeInHierarchy} interactable={button.interactable} isInteractable={button.IsInteractable()} image_raycastTarget={(image != null && image.raycastTarget)} targetGraphic={(targetGraphic != null ? targetGraphic.GetType().Name : string.Empty)} targetGraphic_raycastTarget={(targetGraphic != null && targetGraphic.raycastTarget)} listener_count={listenerCount} scene={SceneManager.GetActiveScene().name}");
        Debug.Log($"{UiLogPrefix} pause_ui_button_rect | label=\"{label}\" path={GetPath(button.transform)} activeInHierarchy={button.gameObject.activeInHierarchy} interactable={button.interactable} isInteractable={button.IsInteractable()} targetGraphic={(targetGraphic != null ? targetGraphic.GetType().Name : string.Empty)} raycastTarget={(targetGraphic != null && targetGraphic.raycastTarget)} localPosition={rect.localPosition} sizeDelta={rect.sizeDelta} world_corners={FormatCorners(rect)}");
        Debug.Log($"{UiLogPrefix} pause_ui_button_world_corners | label=\"{label}\" path={GetPath(button.transform)} corners={FormatCorners(rect)}");
    }

    private void MaybeLogNoPointerEvents()
    {
        if (!_state.IsPaused)
        {
            _noPointerEventMissingLogged = false;
            return;
        }

        bool pointerEventsMissing = ExperimentPauseButtonClickTracer.SecondsSinceLastPointerEvent() >= NoPointerEventThresholdSeconds;
        if (!pointerEventsMissing)
        {
            _noPointerEventMissingLogged = false;
            return;
        }

        if (_noPointerEventMissingLogged)
        {
            return;
        }

        _noPointerEventMissingLogged = true;
        Debug.LogWarning($"{UiLogPrefix} pause_ui_no_pointer_events_detected | scene={SceneManager.GetActiveScene().name} panel_path={GetPath(_pauseRoot)} controller_path={GetPath(transform)} canvas_path={(_canvas != null ? GetPath(_canvas.transform) : string.Empty)} eventSystem={(EventSystem.current != null ? EventSystem.current.name : string.Empty)} activeInputModule={(EventSystem.current != null && EventSystem.current.currentInputModule != null ? EventSystem.current.currentInputModule.GetType().FullName : string.Empty)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
    }

    private void LogPanelRect(string reason)
    {
        if (_pauseRoot == null)
        {
            Debug.LogWarning($"{UiLogPrefix} pause_ui_panel_rect | reason={reason} panel_missing=True scene={SceneManager.GetActiveScene().name}");
            return;
        }

        Debug.Log($"{UiLogPrefix} pause_ui_panel_rect | reason={reason} panel_path={GetPath(_pauseRoot)} anchorMin={_pauseRoot.anchorMin} anchorMax={_pauseRoot.anchorMax} offsetMin={_pauseRoot.offsetMin} offsetMax={_pauseRoot.offsetMax} sizeDelta={_pauseRoot.sizeDelta} localScale={_pauseRoot.localScale} localPosition={_pauseRoot.localPosition} world_corners={FormatCorners(_pauseRoot)} scene={SceneManager.GetActiveScene().name}");
    }

    private void LogP46HCanvasLayoutConfigured(string source)
    {
        RectTransform canvasRect = _canvas != null ? _canvas.GetComponent<RectTransform>() : null;
        Transform canvasTransform = _canvas != null ? _canvas.transform : null;
        Transform panel = _content != null ? _content.parent : null;
        int buttonCount = _pauseRoot != null ? _pauseRoot.GetComponentsInChildren<Button>(false).Length : 0;
        Debug.Log(
            $"p46h_canvas_layout_configured | canvas_name={(_canvas != null ? _canvas.name : "ExperimentPauseFrontCanvas")} " +
            $"panel_path={GetPath(panel)} size_delta={(canvasRect != null ? canvasRect.sizeDelta : Vector2.zero)} " +
            $"local_scale={(canvasTransform != null ? canvasTransform.localScale : Vector3.zero)} " +
            $"world_position={(canvasTransform != null ? canvasTransform.position : Vector3.zero)} " +
            $"world_rotation={(canvasTransform != null ? canvasTransform.eulerAngles : Vector3.zero)} " +
            $"button_count={buttonCount} content_height={CalculateLayoutContentHeight(_content):0.#} " +
            $"safe_padding={PanelSafeVerticalPadding:0.#} layout_spacing={PauseMenuLayoutSpacing:0.#} " +
            $"panel_color={PauseMenuPanelBackgroundColor} overlay_color={PauseMenuModalOverlayColor} source={source ?? string.Empty}");
    }

    private static float CalculateLayoutContentHeight(RectTransform content)
    {
        if (content == null)
        {
            return 0f;
        }

        VerticalLayoutGroup layout = content.GetComponent<VerticalLayoutGroup>();
        float height = layout != null ? layout.padding.top + layout.padding.bottom : 0f;
        int activeChildren = 0;
        for (int i = 0; i < content.childCount; i++)
        {
            RectTransform child = content.GetChild(i) as RectTransform;
            if (child == null || !child.gameObject.activeSelf)
            {
                continue;
            }

            LayoutElement element = child.GetComponent<LayoutElement>();
            height += element != null
                ? Mathf.Max(element.minHeight, element.preferredHeight)
                : Mathf.Max(0f, child.sizeDelta.y);
            activeChildren++;
        }

        if (layout != null && activeChildren > 1)
        {
            height += layout.spacing * (activeChildren - 1);
        }

        return height;
    }

    private static string FormatCorners(RectTransform rect)
    {
        if (rect == null)
        {
            return string.Empty;
        }

        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        return $"{corners[0]} | {corners[1]} | {corners[2]} | {corners[3]}";
    }

    private static string FormatRect(RectTransform rect)
    {
        if (rect == null)
        {
            return string.Empty;
        }

        return $"anchorMin={rect.anchorMin} anchorMax={rect.anchorMax} offsetMin={rect.offsetMin} offsetMax={rect.offsetMax} sizeDelta={rect.sizeDelta} localScale={rect.localScale} localPosition={rect.localPosition}";
    }

    private static Vector2 CalculateWorldSize(RectTransform rect)
    {
        if (rect == null)
        {
            return Vector2.zero;
        }

        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        float width = Vector3.Distance(corners[0], corners[3]);
        float height = Vector3.Distance(corners[0], corners[1]);
        return new Vector2(width, height);
    }

    private static string DescribeComponent(Behaviour component)
    {
        return component != null ? $"{component.GetType().FullName}:enabled={component.enabled}" : string.Empty;
    }

    private static void SetUiLayerRecursively(GameObject root)
    {
        int uiLayer = LayerMask.NameToLayer("UI");
        if (root == null || uiLayer < 0)
        {
            return;
        }

        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            child.gameObject.layer = uiLayer;
        }
    }

    private static bool IsProtocolScene(string sceneName)
    {
        return string.Equals(sceneName, TargetSceneName, StringComparison.Ordinal) ||
            string.Equals(sceneName, FinalSceneName, StringComparison.Ordinal);
    }

    private static Transform FindExperimentParent()
    {
        GameObject root = GameObject.Find("Experiment");
        return root != null ? root.transform : null;
    }

    private static string GetPath(Transform target)
    {
        if (target == null)
        {
            return string.Empty;
        }

        var names = new Stack<string>();
        Transform current = target;
        while (current != null)
        {
            names.Push(current.name);
            current = current.parent;
        }

        return string.Join("/", names);
    }

    private static string FormatDevice(ExperimentPauseInputDeviceSnapshot device)
    {
        return $"device_name=\"{device.Name}\" characteristics=\"{device.Characteristics}\" isValid={device.IsValid} isTracked={device.IsTracked} secondaryButton_supported={device.SecondaryButtonSupported} secondaryButton_value={device.SecondaryButtonPressed} primaryButton_supported={device.PrimaryButtonSupported} primaryButton_value={device.PrimaryButtonPressed} menuButton_supported={device.MenuButtonSupported} menuButton_value={device.MenuButtonPressed} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode}";
    }

    private static string FormatDevices(IReadOnlyList<ExperimentPauseInputDeviceSnapshot> devices)
    {
        if (devices == null || devices.Count == 0)
        {
            return "(none)";
        }

        var parts = new List<string>();
        for (int i = 0; i < devices.Count; i++)
        {
            parts.Add($"#{i}:{FormatDevice(devices[i])}");
        }

        return string.Join(" || ", parts);
    }
}
