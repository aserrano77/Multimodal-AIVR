using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Autonomy.Domain;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Object = UnityEngine.Object;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class ExperimentRuntimeProtocolUI : MonoBehaviour
    {
        private const string TargetSceneName = "autonomous_demo_step22_multimodal_bridge";
        private const string FinalSceneName = "final_scene";
        private const string StartSceneName = "experiment_start_scene";
        private const string AnchorName = "RuntimeProtocolUIAnchor";
        private const float RefreshSeconds = 0.35f;
        private const float RuntimeCanvasDynamicPixelsPerUnit = 14f;
        private const float GlobalInstructionsFrontCanvasDistanceMeters = 1.75f;
        private const float GlobalInstructionsFrontCanvasScale = 0.0015f;
        private const float GlobalInstructionsFallbackCanvasY = 1.40f;
        private const float GlobalInstructionsMinimumCanvasY = 1.15f;
        private const float GlobalInstructionsHorizontalOffsetMeters = 0.08f;
        private const float GlobalInstructionsPanelWidth = 960f;
        private const float GlobalInstructionsPanelHeight = 760f;
        private const float GlobalInstructionsSafeHorizontalPadding = 46f;
        private const float GlobalInstructionsSafeVerticalPadding = 40f;
        private const float GlobalInstructionsLayoutSpacing = 12f;
        private const float GlobalInstructionsBodyHeight = 430f;
        private const float GlobalInstructionsButtonAreaHeight = 74f;
        private static readonly Color ProtocolPanelBackgroundColor = new(0.07f, 0.08f, 0.09f, 0.92f);
        private static bool s_launchFromStartScenePending;
        private static bool s_globalInstructionsBeforeStartPending;
        private static bool s_sceneLoadedHandlerRegistered;
        private static ExperimentSessionIdHistoryEntry s_pendingSavedExitCheckpoint;

        private enum ScreenState
        {
            Start,
            GlobalInstructions,
            ResumeFailure,
            Instructions,
            Progress,
            Final
        }

        [SerializeField] private ExperimentSessionOrchestrator _orchestrator;
        [SerializeField] private ExperimentXrRigResetter _xrRigResetter;
        [SerializeField] private Vector3 _fixedPanelPosition = new(-9.9f, 1.68f, -3.35f);
        [SerializeField] private Vector3 _fixedPanelEulerAngles = new(0f, -90f, 0f);
        [SerializeField] private Vector2 _panelSize = new(960f, 1050f);
        [SerializeField] private bool _showTechnicalDiagnostics;
        [SerializeField] private bool _enableVerboseXrUiRayDiagnostics;

        private Canvas _canvas;
        private RectTransform _content;
        private RuntimeProtocolInstructionNarrator _instructionNarrator;
        private ExperimentRuntimeUiRayInteractorBootstrap _rayBootstrap;
        private ScreenState _screen = ScreenState.Start;
        private float _nextRefreshAt;
        private string _lastRenderKey = string.Empty;
        private string _lastLoggedC11PreflightKey = string.Empty;
        private string _lastNarratedInstructionConditionId = string.Empty;
        private string _lastInstructionCanvasLogKey = string.Empty;
        private bool _incidentConfirmationPending;
        private bool _finishingSession;
        private bool _launchedFromStartScene;
        private bool _pendingGlobalInstructionsBeforeStart;
        private bool _started;
        private bool _beginFromStartSceneWhenReady;
        private bool _beginFromStartSceneAlreadyHandled;
        private bool _autoBeginDirectFinalSceneHandled;
        private bool _restartIntroAwaitingBeginButton;
        private string _savedExitResumeFailureReason = string.Empty;
        private string _finalCompletionSessionId = string.Empty;
        private string _finalCompletionQuestionnaireCode = string.Empty;
        private int _finalCompletionVisiblePrueba;
        private int _finalCompletionRoundIndex;
        private string _finalCompletionStatus = ExperimentSessionIdHistoryStore.CompletedStatus;
        private bool _finalCompletionAcknowledged;
        private bool _finalCompletionScreenLogged;
        private bool _protocolStationPoseCaptured;
        private bool _finalCompletionModalPositioned;
        private bool _experimentalXrGateReleased;
        private ExperimentSimulationPauseAuthority.PauseLease _experimentalXrGatePauseLease;

        public bool ExperimentalXrGateReleased => _experimentalXrGateReleased;
        private ExperimentGlobalInstructionsState _globalInstructionsState;
        private ExperimentGlobalInstructionsAudioPlayer _globalInstructionsAudioPlayer;
        private GameObject _globalInstructionsFrontCanvasObject;
        private Canvas _globalInstructionsFrontCanvas;
        private RectTransform _globalInstructionsFrontContent;
        private CanvasGroup _globalInstructionsFrontCanvasGroup;
        private bool _globalInstructionsFrontCanvasPoseLocked;
        private bool _protocolPanelRaycastDisabledForGlobalInstructions;
        private CanvasGroup _protocolCanvasGroup;
        private bool _protocolPanelVisualStateSaved;
        private float _protocolPanelSavedAlpha = 1f;
        private bool _protocolPanelSavedInteractable = true;
        private bool _protocolPanelSavedBlocksRaycasts = true;
        private int _lastNarratedGlobalInstructionPage = -1;
        private int _globalInstructionsLastNavFrame = -1;
        private string _lastP46HGlobalInstructionsLayoutLogKey = string.Empty;
        private Vector3 _protocolStationPosition;
        private Quaternion _protocolStationRotation;
        private readonly List<string> _completedConditionIds = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            string activeSceneName = SceneManager.GetActiveScene().name;
            bool isProtocolScene = IsProtocolScene(activeSceneName, out string matchedSceneName);
            if (isProtocolScene)
            {
                TiagoExperimentTelemetry.LogEvent(
                    s_launchFromStartScenePending
                        ? "p45c17d_runtime_protocol_bootstrap_scene_transition"
                        : "p45c17d_runtime_protocol_bootstrap_direct_start",
                    new Dictionary<string, object>
                    {
                        ["active_scene_name"] = activeSceneName,
                        ["launch_pending"] = s_launchFromStartScenePending
                    });
            }

            TiagoExperimentTelemetry.LogEvent(
                "p45c17_protocol_scene_check",
                new Dictionary<string, object>
                {
                    ["active_scene_name"] = activeSceneName,
                    ["is_protocol_scene"] = isProtocolScene,
                    ["matched_scene_name"] = matchedSceneName,
                    ["reason"] = isProtocolScene ? "supported_protocol_scene" : "unsupported_scene"
                });
            TiagoExperimentTelemetry.LogEvent(
                "p45c17_runtime_protocol_bootstrap_checked",
                new Dictionary<string, object>
                {
                    ["active_scene_name"] = activeSceneName,
                    ["supported_scene"] = isProtocolScene,
                    ["existing_protocol_ui_found"] = FindFirstObjectByType<ExperimentRuntimeProtocolUI>() != null,
                    ["created"] = false,
                    ["parent_path"] = string.Empty,
                    ["host_path"] = string.Empty
                });

            if (!isProtocolScene)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "p45c17_runtime_protocol_bootstrap_skipped",
                    new Dictionary<string, object>
                    {
                        ["active_scene_name"] = activeSceneName,
                        ["supported_scene"] = false,
                        ["existing_protocol_ui_found"] = false,
                        ["created"] = false,
                        ["parent_path"] = string.Empty,
                        ["host_path"] = string.Empty
                    });
                return;
            }

            ExperimentRuntimeProtocolUI protocolUi = EnsureRuntimeProtocolUiInActiveScene("runtime_initialize_after_scene_load");
            if (protocolUi == null)
            {
                return;
            }

            if (s_launchFromStartScenePending && string.Equals(activeSceneName, FinalSceneName, StringComparison.Ordinal))
            {
                protocolUi.RequestBeginFromStartSceneAfterStart("runtime_initialize_after_scene_load");
                ConsumeLaunchFromStartScene("runtime_initialize_after_scene_load");
                UnregisterSceneLoadedHandlerIfNeeded();
            }
        }

        public static void MarkLaunchFromStartScene()
        {
            s_pendingSavedExitCheckpoint = null;
            s_launchFromStartScenePending = true;
            s_globalInstructionsBeforeStartPending = true;
            RegisterSceneLoadedHandlerIfNeeded();
            TiagoExperimentTelemetry.LogEvent(
                "p45c17_launch_from_start_scene_marked",
                new Dictionary<string, object>
                {
                    ["active_scene_name"] = SceneManager.GetActiveScene().name,
                    ["target_scene_name"] = FinalSceneName,
                    ["global_instructions_before_start_pending"] = true,
                    ["participant_id"] = "auto_pending"
                });
        }

        public static void MarkLaunchFromSavedExitCheckpoint(ExperimentSessionIdHistoryEntry checkpoint)
        {
            s_pendingSavedExitCheckpoint = checkpoint;
            s_launchFromStartScenePending = true;
            s_globalInstructionsBeforeStartPending = false;
            RegisterSceneLoadedHandlerIfNeeded();
            Debug.Log($"[P46D-PAUSE] saved_exit_resume_checkpoint_begin | previous_session_id={checkpoint?.session_id ?? string.Empty} questionnaire_code={checkpoint?.questionnaire_code ?? string.Empty} saved_condition_id={checkpoint?.saved_condition_id ?? string.Empty} saved_visible_prueba={checkpoint?.saved_visible_prueba ?? 0} saved_round_index={checkpoint?.saved_round_index ?? 0} resume_policy={checkpoint?.resume_policy ?? string.Empty} scene={SceneManager.GetActiveScene().name}");
            TiagoExperimentTelemetry.LogEvent(
                "p46d_saved_exit_checkpoint_launch_marked",
                new Dictionary<string, object>
                {
                    ["active_scene_name"] = SceneManager.GetActiveScene().name,
                    ["target_scene_name"] = FinalSceneName,
                    ["previous_session_id"] = checkpoint?.session_id ?? string.Empty,
                    ["questionnaire_code"] = checkpoint?.questionnaire_code ?? string.Empty,
                    ["saved_condition_id"] = checkpoint?.saved_condition_id ?? string.Empty,
                    ["resume_policy"] = checkpoint?.resume_policy ?? string.Empty,
                    ["global_instructions_before_start_pending"] = false
                });
        }

        private static void RegisterSceneLoadedHandlerIfNeeded()
        {
            if (s_sceneLoadedHandlerRegistered)
            {
                return;
            }

            SceneManager.sceneLoaded += OnSceneLoadedForStartSceneLaunch;
            s_sceneLoadedHandlerRegistered = true;
            TiagoExperimentTelemetry.LogEvent(
                "p45c17d_scene_loaded_handler_registered",
                new Dictionary<string, object>
                {
                    ["scene_name"] = SceneManager.GetActiveScene().name,
                    ["launch_pending"] = s_launchFromStartScenePending,
                    ["handler_registered"] = true
                });
        }

        private static void UnregisterSceneLoadedHandlerIfNeeded()
        {
            if (!s_sceneLoadedHandlerRegistered)
            {
                return;
            }

            SceneManager.sceneLoaded -= OnSceneLoadedForStartSceneLaunch;
            s_sceneLoadedHandlerRegistered = false;
        }

        private static void OnSceneLoadedForStartSceneLaunch(Scene scene, LoadSceneMode mode)
        {
            TiagoExperimentTelemetry.LogEvent(
                "p45c17d_scene_loaded_received",
                new Dictionary<string, object>
                {
                    ["scene_name"] = scene.name,
                    ["load_scene_mode"] = mode.ToString(),
                    ["launch_pending"] = s_launchFromStartScenePending,
                    ["handler_registered"] = s_sceneLoadedHandlerRegistered,
                    ["action_taken"] = "received"
                });

            if (!s_launchFromStartScenePending || !string.Equals(scene.name, FinalSceneName, StringComparison.Ordinal))
            {
                TiagoExperimentTelemetry.LogEvent(
                    "p45c17d_scene_loaded_ignored",
                    new Dictionary<string, object>
                    {
                        ["scene_name"] = scene.name,
                        ["load_scene_mode"] = mode.ToString(),
                        ["launch_pending"] = s_launchFromStartScenePending,
                        ["handler_registered"] = s_sceneLoadedHandlerRegistered,
                        ["action_taken"] = s_launchFromStartScenePending ? "waiting_for_final_scene" : "no_pending_launch"
                    });
                return;
            }

            TiagoExperimentTelemetry.LogEvent(
                "p45c17d_final_scene_loaded_from_start_scene",
                new Dictionary<string, object>
                {
                    ["scene_name"] = scene.name,
                    ["load_scene_mode"] = mode.ToString(),
                    ["launch_pending"] = true,
                    ["handler_registered"] = s_sceneLoadedHandlerRegistered,
                    ["action_taken"] = "ensure_runtime_protocol_ui"
                });

            ExperimentRuntimeProtocolUI protocolUi = EnsureRuntimeProtocolUiInActiveScene("scene_loaded_from_start_scene");
            if (protocolUi != null)
            {
                protocolUi.RequestBeginFromStartSceneAfterStart("scene_loaded_from_start_scene");
                ConsumeLaunchFromStartScene("scene_loaded_from_start_scene");
                UnregisterSceneLoadedHandlerIfNeeded();
            }
        }

        private static void ConsumeLaunchFromStartScene(string reason)
        {
            s_launchFromStartScenePending = false;
            s_globalInstructionsBeforeStartPending = false;
            TiagoExperimentTelemetry.LogEvent(
                "p45c17_launch_from_start_scene_consumed",
                new Dictionary<string, object>
                {
                    ["active_scene_name"] = SceneManager.GetActiveScene().name,
                    ["reason"] = reason ?? string.Empty
                });
        }

        private static bool IsProtocolScene(string sceneName)
        {
            return IsProtocolScene(sceneName, out _);
        }

        private static bool IsProtocolScene(string sceneName, out string matchedSceneName)
        {
            if (string.Equals(sceneName, TargetSceneName, StringComparison.Ordinal))
            {
                matchedSceneName = TargetSceneName;
                return true;
            }

            if (string.Equals(sceneName, FinalSceneName, StringComparison.Ordinal))
            {
                matchedSceneName = FinalSceneName;
                return true;
            }

            matchedSceneName = string.Empty;
            return false;
        }

        private static Transform FindExperimentParent()
        {
            GameObject root = GameObject.Find("Experiment");
            return root != null ? root.transform : null;
        }

        private static ExperimentRuntimeProtocolUI EnsureRuntimeProtocolUiInActiveScene(string reason)
        {
            string activeSceneName = SceneManager.GetActiveScene().name;
            bool protocolScene = IsProtocolScene(activeSceneName, out _);
            TiagoExperimentTelemetry.LogEvent(
                "p45c17d_runtime_protocol_ensure_requested",
                new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["active_scene_name"] = activeSceneName,
                    ["protocol_scene"] = protocolScene,
                    ["existing_found"] = false,
                    ["created"] = false,
                    ["parent_path"] = string.Empty,
                    ["host_path"] = string.Empty
                });

            if (!protocolScene)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "p45c17d_runtime_protocol_ensure_failed",
                    new Dictionary<string, object>
                    {
                        ["reason"] = reason ?? string.Empty,
                        ["active_scene_name"] = activeSceneName,
                        ["protocol_scene"] = false,
                        ["existing_found"] = false,
                        ["created"] = false,
                        ["parent_path"] = string.Empty,
                        ["host_path"] = string.Empty
                    });
                return null;
            }

            ExperimentRuntimeProtocolUI existingProtocolUi =
                FindFirstObjectByType<ExperimentRuntimeProtocolUI>(FindObjectsInactive.Include);
            if (existingProtocolUi != null)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "p45c17d_runtime_protocol_ensure_found_existing",
                    new Dictionary<string, object>
                    {
                        ["reason"] = reason ?? string.Empty,
                        ["active_scene_name"] = activeSceneName,
                        ["protocol_scene"] = true,
                        ["existing_found"] = true,
                        ["created"] = false,
                        ["parent_path"] = existingProtocolUi.transform.parent != null ? GetTransformPath(existingProtocolUi.transform.parent) : string.Empty,
                        ["host_path"] = GetTransformPath(existingProtocolUi.transform)
                    });
                return existingProtocolUi;
            }

            Transform parent = FindExperimentParent();
            var host = new GameObject("RuntimeProtocolUI");
            if (parent != null)
            {
                host.transform.SetParent(parent, false);
            }

            ExperimentRuntimeProtocolUI protocolUi = host.AddComponent<ExperimentRuntimeProtocolUI>();
            TiagoExperimentTelemetry.LogEvent(
                "p45c17d_runtime_protocol_ensure_created",
                new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["active_scene_name"] = activeSceneName,
                    ["protocol_scene"] = true,
                    ["existing_found"] = false,
                    ["created"] = true,
                    ["parent_path"] = parent != null ? GetTransformPath(parent) : string.Empty,
                    ["host_path"] = GetTransformPath(host.transform)
                });
            TiagoExperimentTelemetry.LogEvent(
                "p45c17_runtime_protocol_bootstrap_created",
                new Dictionary<string, object>
                {
                    ["active_scene_name"] = activeSceneName,
                    ["supported_scene"] = true,
                    ["existing_protocol_ui_found"] = false,
                    ["created"] = true,
                    ["parent_path"] = parent != null ? GetTransformPath(parent) : string.Empty,
                    ["host_path"] = GetTransformPath(host.transform)
                });
            return protocolUi;
        }

        private void Awake()
        {
            _orchestrator ??= FindFirstObjectByType<ExperimentSessionOrchestrator>();
            BuildCanvas();
            SetProtocolVisible(false);
        }

        private IEnumerator Start()
        {
            EnsureEventSystem();
            const string gateSource = "experimental_scene_ui_initialization";
            if (!ExperimentSimulationPauseAuthority.NormalizeForRunningScene("final_scene_pre_session", this))
            {
                ExperimentLocomotionStateGuard.CaptureAndLog("experiment_locomotion_state_invariant_failed", "final_scene_pre_session", false);
                yield break;
            }

            ExperimentLocomotionStateGuard.CaptureAndLog("experiment_locomotion_state_before_xr_gate", gateSource, false);
            _experimentalXrGatePauseLease = ExperimentSimulationPauseAuthority.Acquire("experimental_xr_alignment_gate", gateSource, this);
            _xrRigResetter ??= FindFirstObjectByType<ExperimentXrRigResetter>(FindObjectsInactive.Include);
            try
            {
                if (_xrRigResetter != null)
                {
                    _xrRigResetter.AlignmentGateReleased += HandleXrAlignmentGateReleased;
                    ExperimentXrRigAlignmentResult initialAlignment = null;
                    yield return _xrRigResetter.AlignBeforeProtocolUiCoroutine(result => initialAlignment = result);
                    Debug.Log(
                        $"[ExperimentRuntimeProtocolUI] experiment_xr_alignment_gate_released | " +
                        $"scope=pre_session succeeded={initialAlignment != null && initialAlignment.Succeeded} " +
                        $"timed_out={initialAlignment != null && initialAlignment.TimedOut} " +
                        $"reason={initialAlignment?.FailureReason ?? "alignment_result_missing"} " +
                        $"attempts={initialAlignment?.AttemptCount ?? 0} fallback={(initialAlignment != null && initialAlignment.Succeeded ? "none" : "show_ui_fail_soft")}");
                }
                else
                {
                    Debug.LogWarning(
                        "[ExperimentRuntimeProtocolUI] experiment_xr_alignment_failed | " +
                        "scope=pre_session reason=xr_rig_resetter_missing fallback=show_ui_fail_soft");
                }
            }
            finally
            {
                ReleaseExperimentalXrGatePause("pre_session_alignment_terminal");
            }

            _experimentalXrGateReleased = true;
            ExperimentLocomotionStateReport locomotion = ExperimentLocomotionStateGuard.RestoreAndValidate(gateSource, true);
            ExperimentLocomotionStateGuard.CaptureAndLog("experiment_locomotion_state_after_xr_gate", gateSource, true);
            ExperimentLocomotionStateGuard.CaptureAndLog("experiment_locomotion_state_before_user_control", gateSource, true);
            if (!locomotion.IsValid)
            {
                Debug.LogError($"[P46O-04] experiment_user_control_gate_blocked | source={gateSource} failure_reason={locomotion.FailureReason} time_scale={Time.timeScale:0.###}", this);
                yield break;
            }

            PositionAtProtocolStation();
            ConfigureExistingXrUiInteractors();
            _instructionNarrator = RuntimeProtocolInstructionNarrator.EnsureAttached(gameObject);
            _rayBootstrap = ExperimentRuntimeUiRayInteractorBootstrap.EnsureAttached(gameObject, _canvas);
            Render(force: true);
            if (_enableVerboseXrUiRayDiagnostics)
            {
                ExperimentRuntimeXrUiRayDiagnostic.EnsureAttached(gameObject, _canvas);
            }

            _started = true;
            if (_beginFromStartSceneWhenReady)
            {
                ExecuteBeginFromStartScene("start_completed");
            }
            else
            {
                MaybeAutoBeginDirectFinalScene("start_completed");
            }
        }

        private void OnDisable()
        {
            ReleaseExperimentalXrGatePause("protocol_ui_disabled");
            if (_xrRigResetter != null)
            {
                _xrRigResetter.AlignmentGateReleased -= HandleXrAlignmentGateReleased;
            }

            _instructionNarrator?.StopNarration("protocol_ui_disabled");
            _globalInstructionsAudioPlayer?.StopAudio("protocol_ui_disabled");
            DestroyGlobalInstructionsFrontCanvas("protocol_ui_disabled");
        }

        private void OnDestroy()
        {
            ReleaseExperimentalXrGatePause("protocol_ui_destroyed");
            _instructionNarrator?.StopNarration("protocol_ui_destroyed");
            _globalInstructionsAudioPlayer?.StopAudio("protocol_ui_destroyed");
            DestroyGlobalInstructionsFrontCanvas("protocol_ui_destroyed");
        }

        private void ReleaseExperimentalXrGatePause(string source)
        {
            if (_experimentalXrGatePauseLease == null)
            {
                return;
            }

            _experimentalXrGatePauseLease.Dispose();
            _experimentalXrGatePauseLease = null;
            Debug.Log($"[P46O-04] experiment_xr_time_gate_released | source={source ?? string.Empty} scene={SceneManager.GetActiveScene().name} time_scale={Time.timeScale:0.###} pending_pause_owners={ExperimentSimulationPauseAuthority.ActiveOwnerCount} owners={ExperimentSimulationPauseAuthority.ActiveOwners}", this);
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextRefreshAt)
            {
                return;
            }

            _nextRefreshAt = Time.unscaledTime + RefreshSeconds;
            Render(force: false);
        }

        private void RequestBeginFromStartSceneAfterStart(string reason)
        {
            _launchedFromStartScene = true;
            _pendingGlobalInstructionsBeforeStart = s_globalInstructionsBeforeStartPending && s_pendingSavedExitCheckpoint == null;
            TiagoExperimentTelemetry.LogEvent(
                "p45c17d_runtime_protocol_begin_requested",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["protocol_ui_path"] = GetTransformPath(transform),
                    ["started"] = _started,
                    ["already_handled"] = _beginFromStartSceneAlreadyHandled,
                    ["global_instructions_before_start_pending"] = _pendingGlobalInstructionsBeforeStart,
                    ["participant_id"] = "auto_pending",
                    ["reason"] = reason ?? string.Empty
                });

            if (_beginFromStartSceneAlreadyHandled)
            {
                return;
            }

            if (_started)
            {
                ExecuteBeginFromStartScene(reason);
                return;
            }

            _beginFromStartSceneWhenReady = true;
            TiagoExperimentTelemetry.LogEvent(
                "p45c17d_runtime_protocol_begin_deferred_until_start",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["protocol_ui_path"] = GetTransformPath(transform),
                    ["reason"] = reason ?? string.Empty
                });
        }

        private void ExecuteBeginFromStartScene(string reason)
        {
            if (_beginFromStartSceneAlreadyHandled)
            {
                return;
            }

            _launchedFromStartScene = true;
            _beginFromStartSceneWhenReady = false;
            _beginFromStartSceneAlreadyHandled = true;
            TiagoExperimentTelemetry.LogEvent(
                "p45c17_runtime_protocol_begin_from_start_scene",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["protocol_ui_path"] = GetTransformPath(transform),
                    ["reason"] = reason ?? string.Empty
                });
            TiagoExperimentTelemetry.LogEvent(
                "p45c17d_runtime_protocol_begin_executed",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["protocol_ui_path"] = GetTransformPath(transform),
                    ["reason"] = reason ?? string.Empty
                });
            BeginProtocolFromStartScreen();
        }

        private void MaybeAutoBeginDirectFinalScene(string reason)
        {
            _orchestrator ??= FindFirstObjectByType<ExperimentSessionOrchestrator>();
            ExperimentRuntimeProtocolSnapshot snapshot = _orchestrator != null ? _orchestrator.GetRuntimeProtocolSnapshot() : null;
            bool finalScene = string.Equals(SceneManager.GetActiveScene().name, FinalSceneName, StringComparison.Ordinal);
            bool beginFromStartPending = _beginFromStartSceneWhenReady || _beginFromStartSceneAlreadyHandled || _launchedFromStartScene;
            bool sessionActive = snapshot != null && snapshot.SessionActive;
            bool canAutoBegin = finalScene &&
                !beginFromStartPending &&
                !_autoBeginDirectFinalSceneHandled &&
                _orchestrator != null &&
                !sessionActive;

            TiagoExperimentTelemetry.LogEvent(
                canAutoBegin
                    ? "p45c17e_direct_final_scene_auto_begin_requested"
                    : "p45c17e_direct_final_scene_auto_begin_skipped",
                new Dictionary<string, object>
                {
                    ["scene_name"] = SceneManager.GetActiveScene().name,
                    ["orchestrator_found"] = _orchestrator != null,
                    ["session_active"] = sessionActive,
                    ["begin_from_start_pending"] = beginFromStartPending,
                    ["already_handled"] = _autoBeginDirectFinalSceneHandled,
                    ["participant_id"] = "auto_pending",
                    ["reason"] = reason ?? string.Empty
                });

            if (!canAutoBegin)
            {
                return;
            }

            _autoBeginDirectFinalSceneHandled = true;
            TiagoExperimentTelemetry.LogEvent(
                "p45c17e_direct_final_scene_auto_begin_executed",
                new Dictionary<string, object>
                {
                    ["scene_name"] = SceneManager.GetActiveScene().name,
                    ["orchestrator_found"] = true,
                    ["session_active"] = false,
                    ["begin_from_start_pending"] = false,
                    ["already_handled"] = true,
                    ["participant_id"] = "auto_pending",
                    ["reason"] = reason ?? string.Empty
                });
            BeginProtocolFromStartScreen();
        }

        private void BuildCanvas()
        {
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.sortingOrder = 100;

            RectTransform rect = _canvas.GetComponent<RectTransform>();
            rect.sizeDelta = _panelSize;
            rect.localScale = Vector3.one * 0.0018f;

            CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = RuntimeCanvasDynamicPixelsPerUnit;
            scaler.referencePixelsPerUnit = 100f;
            Debug.Log($"[P46J-02] protocol_canvas_typography_configured | panel_size={_panelSize} canvas_scale=0.0018 dynamic_pixels_per_unit={scaler.dynamicPixelsPerUnit} reference_pixels_per_unit={scaler.referencePixelsPerUnit} title_font=50 body_font=30 instruction_font=34 button_font=32 text_mode=TextMeshProUGUI shared_material={ExperimentCanvasTypography.MaterialResourcePath} effects=none");
            gameObject.AddComponent<GraphicRaycaster>();
            EnsureTrackedDeviceGraphicRaycaster(gameObject);
            _protocolCanvasGroup = gameObject.GetComponent<CanvasGroup>();
            if (_protocolCanvasGroup == null)
            {
                _protocolCanvasGroup = gameObject.AddComponent<CanvasGroup>();
            }

            var background = new GameObject("Panel");
            background.transform.SetParent(transform, false);
            var image = background.AddComponent<Image>();
            image.color = ProtocolPanelBackgroundColor;
            image.raycastTarget = false;
            RectTransform backgroundRect = image.rectTransform;
            backgroundRect.anchorMin = Vector2.zero;
            backgroundRect.anchorMax = Vector2.one;
            backgroundRect.offsetMin = Vector2.zero;
            backgroundRect.offsetMax = Vector2.zero;

            _content = CreateRect("Content", background.transform);
            _content.anchorMin = Vector2.zero;
            _content.anchorMax = Vector2.one;
            _content.offsetMin = new Vector2(34f, 28f);
            _content.offsetMax = new Vector2(-34f, -28f);
            VerticalLayoutGroup layout = _content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 18f;
            layout.padding = new RectOffset(8, 8, 4, 4);
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
        }

        private static TrackedDeviceGraphicRaycaster EnsureTrackedDeviceGraphicRaycaster(GameObject target)
        {
            if (target == null)
            {
                return null;
            }

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

        private void EnsureEventSystem()
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

        private void ConfigureExistingXrUiInteractors()
        {
            Component[] components = FindObjectsByType<Component>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            int nearFarCount = 0;
            int uiInteractionEnabled = 0;
            int uiInteractionRegistrationPulsed = 0;
            int uiInteractorCount = 0;
            int lineVisualComponentCount = 0;
            int lineVisualsActiveAndEnabled = 0;
            int lineRenderersCurrentlyEnabled = 0;
            List<string> nearFarPaths = new();
            List<string> lineVisualPaths = new();
            foreach (Component component in components)
            {
                if (component == null)
                {
                    continue;
                }

                if (component is IUIInteractor)
                {
                    uiInteractorCount++;
                }

                string typeName = component.GetType().Name;
                if (typeName.IndexOf("NearFarInteractor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (typeName.IndexOf("Near", StringComparison.OrdinalIgnoreCase) >= 0 &&
                     typeName.IndexOf("Far", StringComparison.OrdinalIgnoreCase) >= 0 &&
                     typeName.IndexOf("Interactor", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    nearFarCount++;
                    nearFarPaths.Add(GetTransformPath(component.transform));
                    if (TryPulseOrSetBoolMember(component, true,
                            "enableUIInteraction",
                            "EnableUIInteraction",
                            "m_EnableUIInteraction",
                            "_enableUIInteraction"))
                    {
                        uiInteractionEnabled++;
                        uiInteractionRegistrationPulsed++;
                    }

                    continue;
                }

                if (typeName.IndexOf("LineVisual", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string path = GetTransformPath(component.transform);
                    if (path.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    lineVisualComponentCount++;
                    lineVisualPaths.Add(path);
                    if (component is Behaviour behaviour)
                    {
                        if (behaviour.isActiveAndEnabled)
                        {
                            lineVisualsActiveAndEnabled++;
                        }
                    }

                    LineRenderer lineRenderer = component.GetComponent<LineRenderer>();
                    if (lineRenderer != null && lineRenderer.enabled)
                    {
                        lineRenderersCurrentlyEnabled++;
                    }
                }
            }

            LogXrUiRaySetup(
                nearFarCount,
                uiInteractorCount,
                uiInteractionEnabled,
                uiInteractionRegistrationPulsed,
                lineVisualComponentCount,
                lineVisualsActiveAndEnabled,
                lineRenderersCurrentlyEnabled,
                nearFarPaths,
                lineVisualPaths);
        }

        private void LogXrUiRaySetup(
            int nearFarCount,
            int uiInteractorCount,
            int uiInteractionEnabled,
            int uiInteractionRegistrationPulsed,
            int lineVisualComponentCount,
            int lineVisualsActiveAndEnabled,
            int lineRenderersCurrentlyEnabled,
            List<string> nearFarPaths,
            List<string> lineVisualPaths)
        {
            EventSystem eventSystem = FindFirstObjectByType<EventSystem>();
            BaseInputModule activeInputModule = eventSystem != null ? eventSystem.currentInputModule : null;
            TrackedDeviceGraphicRaycaster trackedRaycaster = EnsureTrackedDeviceGraphicRaycaster(gameObject);
            bool hasXrUiModule = eventSystem != null && eventSystem.GetComponent<XRUIInputModule>() != null;
            bool hasStandaloneModule = eventSystem != null && eventSystem.GetComponent<StandaloneInputModule>() != null;
            TiagoExperimentTelemetry.LogEvent(
                "experiment_runtime_xr_ui_ray_setup",
                new Dictionary<string, object>
                {
                    ["ui_path"] = GetTransformPath(transform),
                    ["world_position"] = transform.position,
                    ["world_rotation_euler"] = transform.eulerAngles,
                    ["has_graphic_raycaster"] = GetComponent<GraphicRaycaster>() != null,
                    ["has_tracked_device_graphic_raycaster"] = trackedRaycaster != null,
                    ["tracked_device_graphic_raycaster_enabled"] = trackedRaycaster != null && trackedRaycaster.enabled,
                    ["event_system"] = eventSystem != null ? eventSystem.name : string.Empty,
                    ["event_system_found"] = eventSystem != null,
                    ["active_input_module_type"] = activeInputModule != null ? activeInputModule.GetType().FullName : string.Empty,
                    ["has_xr_ui_input_module"] = hasXrUiModule,
                    ["has_standalone_input_module"] = hasStandaloneModule,
                    ["near_far_interactor_count"] = nearFarCount,
                    ["ui_interactor_count"] = uiInteractorCount,
                    ["near_far_paths"] = string.Join(" | ", nearFarPaths),
                    ["ui_interaction_members_enabled"] = uiInteractionEnabled,
                    ["ui_interaction_registration_pulsed"] = uiInteractionRegistrationPulsed,
                    ["line_visual_component_count"] = lineVisualComponentCount,
                    ["line_visuals_active_and_enabled"] = lineVisualsActiveAndEnabled,
                    ["line_renderers_currently_enabled"] = lineRenderersCurrentlyEnabled,
                    ["line_visual_paths"] = string.Join(" | ", lineVisualPaths),
                    ["explicit_ui_ray_interactors_added"] = false,
                    ["object_interaction_layer_masks_changed"] = false,
                    ["physics_layers_changed"] = false,
                    ["remote_box_manipulation_enabled_by_protocol_ui"] = false,
                    ["direct_interaction_fallback"] = true
                });
        }

        private static bool HasComponentNamed(GameObject target, string typeName)
        {
            if (target == null || string.IsNullOrWhiteSpace(typeName))
            {
                return false;
            }

            Component[] components = target.GetComponents<Component>();
            foreach (Component component in components)
            {
                if (component != null && component.GetType().Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private void PositionAtProtocolStation()
        {
            Transform anchor = ResolveRuntimeProtocolAnchor(out bool anchorFound, out bool fallbackUsed);
            if (anchor != null)
            {
                transform.SetPositionAndRotation(anchor.position, anchor.rotation);
            }
            else
            {
                transform.SetPositionAndRotation(_fixedPanelPosition, Quaternion.Euler(_fixedPanelEulerAngles));
                fallbackUsed = true;
            }

            CaptureProtocolStationPose();

            TiagoExperimentTelemetry.LogEvent(
                "experiment_runtime_ui_anchor_resolved",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["anchor_found"] = anchorFound,
                    ["anchor_path"] = anchor != null ? GetTransformPath(anchor) : string.Empty,
                    ["panel_position"] = transform.position,
                    ["panel_rotation"] = transform.eulerAngles,
                    ["fallback_used"] = fallbackUsed
                });
        }

        private Transform ResolveRuntimeProtocolAnchor(out bool anchorFound, out bool fallbackUsed)
        {
            fallbackUsed = false;
            Transform experiment = FindExperimentParent();
            Transform anchor = experiment != null ? experiment.Find(AnchorName) : null;
            if (anchor == null)
            {
                GameObject existing = GameObject.Find(AnchorName);
                anchor = existing != null ? existing.transform : null;
            }

            anchorFound = anchor != null;
            if (anchorFound)
            {
                return anchor;
            }

            fallbackUsed = true;
            var anchorObject = new GameObject(AnchorName);
            anchorObject.transform.SetPositionAndRotation(_fixedPanelPosition, Quaternion.Euler(_fixedPanelEulerAngles));
            if (experiment != null)
            {
                anchorObject.transform.SetParent(experiment, true);
            }

            return anchorObject.transform;
        }

        private void CaptureProtocolStationPose()
        {
            _protocolStationPosition = transform.position;
            _protocolStationRotation = transform.rotation;
            _protocolStationPoseCaptured = true;
        }

        private void RestoreProtocolStationPoseIfNeeded(string reason)
        {
            if (!_finalCompletionModalPositioned)
            {
                return;
            }

            if (_protocolStationPoseCaptured)
            {
                transform.SetPositionAndRotation(_protocolStationPosition, _protocolStationRotation);
            }
            else
            {
                PositionAtProtocolStation();
            }

            if (_canvas != null)
            {
                _canvas.sortingOrder = 100;
            }

            _finalCompletionModalPositioned = false;
            TiagoExperimentTelemetry.LogEvent(
                "final_completion_front_modal_restored_to_protocol_station",
                new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["position"] = transform.position,
                    ["rotation"] = transform.eulerAngles
                });
        }

        private void PositionFinalCompletionModalInFrontOfUser()
        {
            Camera camera = Camera.main ?? FindFirstObjectByType<Camera>();
            if (camera == null)
            {
                transform.SetPositionAndRotation(new Vector3(0f, 1.45f, 1.95f), Quaternion.identity);
            }
            else
            {
                Vector3 forward = camera.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.001f)
                {
                    forward = Vector3.forward;
                }

                forward.Normalize();
                Vector3 position = camera.transform.position + forward * 2.05f + Vector3.up * 0.02f;
                Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up);
                transform.SetPositionAndRotation(position, rotation);
            }

            if (_canvas != null)
            {
                _canvas.sortingOrder = 140;
            }

            _finalCompletionModalPositioned = true;
            TiagoExperimentTelemetry.LogEvent(
                "final_completion_front_modal_positioned",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["session_id"] = _finalCompletionSessionId,
                    ["questionnaire_code"] = _finalCompletionQuestionnaireCode,
                    ["visible_prueba"] = _finalCompletionVisiblePrueba,
                    ["round_index"] = _finalCompletionRoundIndex,
                    ["position"] = transform.position,
                    ["rotation"] = transform.eulerAngles,
                    ["canvas_sorting_order"] = _canvas != null ? _canvas.sortingOrder : 0
                });
        }

        private void Render(bool force)
        {
            _orchestrator ??= FindFirstObjectByType<ExperimentSessionOrchestrator>();
            ExperimentRuntimeProtocolSnapshot snapshot = _orchestrator != null ? _orchestrator.GetRuntimeProtocolSnapshot() : null;
            string renderKey = BuildRenderKey(snapshot);
            if (!force && string.Equals(renderKey, _lastRenderKey, StringComparison.Ordinal))
            {
                return;
            }

            _lastRenderKey = renderKey;
            ClearContent();
            if (_screen != ScreenState.Final)
            {
                RestoreProtocolStationPoseIfNeeded("render_" + _screen);
            }

            switch (_screen)
            {
                case ScreenState.GlobalInstructions:
                    RenderRuntimeGlobalInstructions();
                    break;
                case ScreenState.ResumeFailure:
                    RenderSavedExitResumeFailure();
                    break;
                case ScreenState.Instructions:
                    RenderInstructions(snapshot);
                    break;
                case ScreenState.Progress:
                    RenderProgress(snapshot);
                    break;
                case ScreenState.Final:
                    RenderFinal(snapshot);
                    break;
                default:
                    RenderStart(snapshot);
                    break;
            }

            if (_screen == ScreenState.GlobalInstructions)
            {
                RefreshGlobalInstructionsFrontCanvasRayTargets("render_global_instructions");
            }
            else
            {
                RefreshRuntimeUiRayTargets("render_" + _screen);
            }
        }

        private void RefreshRuntimeUiRayTargets(string reason)
        {
            if (_canvas == null)
            {
                return;
            }

            EnsureEventSystem();
            GraphicRaycaster graphicRaycaster = GetComponent<GraphicRaycaster>();
            if (graphicRaycaster == null)
            {
                graphicRaycaster = gameObject.AddComponent<GraphicRaycaster>();
            }

            graphicRaycaster.enabled = true;
            TrackedDeviceGraphicRaycaster trackedRaycaster = EnsureTrackedDeviceGraphicRaycaster(gameObject);
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0)
            {
                foreach (Transform child in _canvas.GetComponentsInChildren<Transform>(true))
                {
                    child.gameObject.layer = uiLayer;
                }
            }

            _rayBootstrap = ExperimentRuntimeUiRayInteractorBootstrap.EnsureAttached(gameObject, _canvas);

            Button[] buttons = _canvas.GetComponentsInChildren<Button>(false);
            int interactableButtonCount = 0;
            foreach (Button button in buttons)
            {
                if (button != null && button.IsInteractable())
                {
                    interactableButtonCount++;
                }
            }

            int blockingGraphics = CountPotentialBlockingGraphics();
            EventSystem eventSystem = EventSystem.current ?? FindFirstObjectByType<EventSystem>();
            bool eventSystemOk = eventSystem != null &&
                eventSystem.GetComponent<XRUIInputModule>() != null &&
                eventSystem.GetComponent<XRUIInputModule>().enabled;

            TiagoExperimentTelemetry.LogEvent(
                "experiment_runtime_ui_ray_rebound",
                new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["canvas_path"] = GetTransformPath(_canvas.transform),
                    ["raycaster_ok"] = trackedRaycaster != null && trackedRaycaster.enabled,
                    ["graphic_raycaster_ok"] = graphicRaycaster != null && graphicRaycaster.enabled,
                    ["event_system_ok"] = eventSystemOk,
                    ["button_count"] = buttons.Length,
                    ["interactable_button_count"] = interactableButtonCount,
                    ["blocking_graphics_count_if_detected"] = blockingGraphics,
                    ["runtime_ray_bootstrap_ok"] = _rayBootstrap != null,
                    ["remote_box_manipulation_enabled"] = false
                });
        }

        private int CountPotentialBlockingGraphics()
        {
            if (_canvas == null)
            {
                return 0;
            }

            int count = 0;
            foreach (Graphic graphic in _canvas.GetComponentsInChildren<Graphic>(false))
            {
                if (graphic == null || !graphic.raycastTarget || !graphic.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (graphic.GetComponentInParent<Button>() != null ||
                    graphic.GetComponentInParent<InputField>() != null ||
                    graphic.GetComponentInParent<TMP_InputField>() != null ||
                    graphic.GetComponentInParent<Toggle>() != null ||
                    graphic.GetComponentInParent<Slider>() != null ||
                    graphic.GetComponentInParent<Scrollbar>() != null)
                {
                    continue;
                }

                count++;
            }

            return count;
        }

        private string BuildRenderKey(ExperimentRuntimeProtocolSnapshot snapshot)
        {
            return string.Join("|",
                _screen,
                snapshot?.SessionActive ?? false,
                snapshot?.TrialActive ?? false,
                snapshot?.TrialTransitionBusy ?? false,
                snapshot?.CurrentConditionId ?? string.Empty,
                snapshot?.NextConditionId ?? string.Empty,
                snapshot?.CurrentRoundIndexWithinCondition ?? 0,
                snapshot?.CompletedBoxes ?? 0,
                snapshot?.TotalBoxes ?? 0,
                snapshot?.CanAdvance ?? false,
                snapshot?.AdvanceReason ?? string.Empty,
                _finishingSession,
                _incidentConfirmationPending,
                _finalCompletionSessionId,
                _finalCompletionQuestionnaireCode,
                _finalCompletionAcknowledged,
                ExperimentVoicePipelineBootstrap.Instance?.ReadinessToken ?? string.Empty);
        }

        public void SetProtocolVisible(bool visible)
        {
            Debug.Log($"[P46D-04] protocol_set_visible_requested | visible={visible} scene={SceneManager.GetActiveScene().name} protocol_ui_path={GetTransformPath(transform)} canvas_exists={_canvas != null}");
            if (_canvas != null)
            {
                _canvas.enabled = visible;
                GraphicRaycaster graphicRaycaster = GetComponent<GraphicRaycaster>();
                if (graphicRaycaster != null)
                {
                    graphicRaycaster.enabled = visible;
                }
            }

            Transform rayRoot = transform.Find("RuntimeUiRayInteractors");
            if (rayRoot != null)
            {
                rayRoot.gameObject.SetActive(visible);
            }

            Debug.Log($"[P46D-04] protocol_set_visible_applied | visible={visible} scene={SceneManager.GetActiveScene().name} canvas_enabled={_canvas != null && _canvas.enabled} active_button_count={(_canvas != null ? _canvas.GetComponentsInChildren<Button>(false).Length : 0)}");
        }

        public void BeginProtocolFromStartScreen()
        {
            Debug.Log($"[P46D-04] protocol_begin_from_start_screen_started | scene={SceneManager.GetActiveScene().name} protocol_ui_path={GetTransformPath(transform)}");
            _screen = ScreenState.Start;
            _incidentConfirmationPending = false;
            _finishingSession = false;
            _savedExitResumeFailureReason = string.Empty;
            ResetFinalCompletionState();
            SetProtocolVisible(true);
            if (_pendingGlobalInstructionsBeforeStart && s_pendingSavedExitCheckpoint == null)
            {
                ShowRuntimeGlobalInstructionsBeforeStart("begin_from_start_screen");
                return;
            }

            TiagoExperimentTelemetry.LogEvent(
                "experiment_runtime_protocol_ui_start_screen_skipped",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["reason"] = "external_start_screen_active",
                    ["participant_id"] = "auto_pending"
                });
            Debug.Log($"[P46D-04] protocol_begin_from_start_screen_before_start_session | scene={SceneManager.GetActiveScene().name} protocol_ui_path={GetTransformPath(transform)}");
            StartSession();
            Debug.Log($"[P46D-04] protocol_begin_from_start_screen_after_start_session | scene={SceneManager.GetActiveScene().name} protocol_ui_path={GetTransformPath(transform)}");
        }

        private void ShowRuntimeGlobalInstructionsBeforeStart(string reason)
        {
            _screen = ScreenState.GlobalInstructions;
            _globalInstructionsState = ExperimentGlobalInstructionsState.CreateDefault();
            _globalInstructionsLastNavFrame = -1;
            _lastNarratedGlobalInstructionPage = -1;
            TiagoExperimentTelemetry.LogEvent(
                "global_instructions_runtime_pre_session_requested",
                BuildRuntimeGlobalInstructionsPayload("global_instructions_runtime_pre_session_requested", reason));
            Debug.Log($"[P46G-INSTRUCTIONS] global_instructions_runtime_pre_session_requested | reason={reason ?? string.Empty} path={GetTransformPath(transform)} scene={SceneManager.GetActiveScene().name}");
            Render(force: true);
            StartCoroutine(StabilizeGlobalInstructionsFrontCanvasPose());
        }

        private IEnumerator StabilizeGlobalInstructionsFrontCanvasPose()
        {
            yield return null;
            yield return null;
            if (_screen == ScreenState.GlobalInstructions && _globalInstructionsFrontCanvas != null)
            {
                PositionGlobalInstructionsFrontCanvas();
            }

            yield return new WaitForSecondsRealtime(0.25f);
            if (_screen == ScreenState.GlobalInstructions && _globalInstructionsFrontCanvas != null)
            {
                PositionGlobalInstructionsFrontCanvas();
            }

            yield return new WaitForSecondsRealtime(0.50f);
            if (_screen == ScreenState.GlobalInstructions && _globalInstructionsFrontCanvas != null)
            {
                PositionGlobalInstructionsFrontCanvas();
                LockGlobalInstructionsFrontCanvasPose("initial_stabilization_complete");
            }
        }

        private void RenderRuntimeGlobalInstructions()
        {
            if (_globalInstructionsState == null || _globalInstructionsState.PageCount == 0)
            {
                _globalInstructionsState = ExperimentGlobalInstructionsState.CreateDefault();
            }

            EnsureGlobalInstructionsFrontCanvas();
            ClearGlobalInstructionsFrontContent();
            SetRuntimeProtocolPanelRaycastBlocked(true, "global_instructions_front_canvas_active");
            ExperimentGlobalInstructionPage page = _globalInstructionsState.CurrentPage;

            AddFrontText(page?.Title ?? "Instrucciones iniciales", 44, FontStyles.Bold, 62f, TextAlignmentOptions.MidlineLeft, Color.white);
            TextMeshProUGUI pageText = AddFrontText($"P\u00e1gina {_globalInstructionsState.PageIndex + 1} de {_globalInstructionsState.PageCount}", 25, FontStyles.Bold, 34f, TextAlignmentOptions.MidlineLeft, new Color(0.72f, 0.84f, 0.92f, 1f));
            if (pageText != null)
            {
                pageText.color = new Color(0.72f, 0.84f, 0.92f, 1f);
            }
            AddRuntimeGlobalInstructionBullets(page);
            AddFrontSpacer(6f);
            AddRuntimeGlobalInstructionButtons();
            LogP46HGlobalInstructionsCanvasLayoutConfigured("render_runtime_global_instructions");
            PlayRuntimeGlobalInstructionsAudioForCurrentPage();
        }

        private void EnsureGlobalInstructionsFrontCanvas()
        {
            if (_globalInstructionsFrontCanvasObject != null &&
                _globalInstructionsFrontCanvas != null &&
                _globalInstructionsFrontContent != null)
            {
                return;
            }

            Camera camera = Camera.main ?? FindFirstObjectByType<Camera>();
            _globalInstructionsFrontCanvasObject = new GameObject("GlobalInstructionsRuntimeFrontCanvasRoot", typeof(RectTransform));
            _globalInstructionsFrontCanvasPoseLocked = false;
            RectTransform canvasRect = _globalInstructionsFrontCanvasObject.GetComponent<RectTransform>();
            canvasRect.anchorMin = new Vector2(0.5f, 0.5f);
            canvasRect.anchorMax = new Vector2(0.5f, 0.5f);
            canvasRect.pivot = new Vector2(0.5f, 0.5f);
            canvasRect.sizeDelta = new Vector2(GlobalInstructionsPanelWidth, GlobalInstructionsPanelHeight);
            canvasRect.localScale = Vector3.one * GlobalInstructionsFrontCanvasScale;

            _globalInstructionsFrontCanvas = _globalInstructionsFrontCanvasObject.AddComponent<Canvas>();
            _globalInstructionsFrontCanvas.renderMode = RenderMode.WorldSpace;
            _globalInstructionsFrontCanvas.sortingOrder = 560;
            _globalInstructionsFrontCanvas.worldCamera = camera;

            CanvasScaler scaler = _globalInstructionsFrontCanvasObject.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = RuntimeCanvasDynamicPixelsPerUnit;
            scaler.referencePixelsPerUnit = 100f;

            _globalInstructionsFrontCanvasObject.AddComponent<GraphicRaycaster>();
            EnsureTrackedDeviceGraphicRaycaster(_globalInstructionsFrontCanvasObject);
            _globalInstructionsFrontCanvasGroup = _globalInstructionsFrontCanvasObject.AddComponent<CanvasGroup>();
            _globalInstructionsFrontCanvasGroup.alpha = 1f;
            _globalInstructionsFrontCanvasGroup.interactable = true;
            _globalInstructionsFrontCanvasGroup.blocksRaycasts = true;

            RectTransform panel = CreateRect("GlobalInstructionsRuntimeFrontPanel", _globalInstructionsFrontCanvasObject.transform);
            panel.anchorMin = Vector2.zero;
            panel.anchorMax = Vector2.one;
            panel.offsetMin = Vector2.zero;
            panel.offsetMax = Vector2.zero;
            Image panelImage = panel.gameObject.AddComponent<Image>();
            panelImage.color = ProtocolPanelBackgroundColor;
            panelImage.raycastTarget = true;
            Dictionary<string, object> backgroundPayload = BuildRuntimeGlobalInstructionsPayload("global_instructions_front_canvas_background_configured", "runtime_protocol_panel_style");
            backgroundPayload["color"] = ProtocolPanelBackgroundColor;
            backgroundPayload["alpha"] = ProtocolPanelBackgroundColor.a;
            backgroundPayload["source"] = "runtime_protocol_panel_style";
            TiagoExperimentTelemetry.LogEvent("global_instructions_front_canvas_background_configured", backgroundPayload);
            Debug.Log($"[P46G-INSTRUCTIONS] global_instructions_front_canvas_background_configured | canvas_path={backgroundPayload["canvas_path"]} color={ProtocolPanelBackgroundColor} alpha={ProtocolPanelBackgroundColor.a:0.###} source=runtime_protocol_panel_style scene={SceneManager.GetActiveScene().name}");

            _globalInstructionsFrontContent = CreateRect("Content", panel);
            _globalInstructionsFrontContent.anchorMin = Vector2.zero;
            _globalInstructionsFrontContent.anchorMax = Vector2.one;
            _globalInstructionsFrontContent.offsetMin = new Vector2(GlobalInstructionsSafeHorizontalPadding, GlobalInstructionsSafeVerticalPadding);
            _globalInstructionsFrontContent.offsetMax = new Vector2(-GlobalInstructionsSafeHorizontalPadding, -GlobalInstructionsSafeVerticalPadding);
            VerticalLayoutGroup layout = _globalInstructionsFrontContent.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = GlobalInstructionsLayoutSpacing;
            layout.padding = new RectOffset(8, 8, 4, 4);
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;

            SetUiLayerRecursively(_globalInstructionsFrontCanvasObject);
            PositionGlobalInstructionsFrontCanvas();
            Dictionary<string, object> payload = BuildRuntimeGlobalInstructionsPayload("global_instructions_front_canvas_created", "created");
            TiagoExperimentTelemetry.LogEvent("global_instructions_front_canvas_created", payload);
            Debug.Log($"[P46G-INSTRUCTIONS] global_instructions_front_canvas_created | canvas_path={payload["canvas_path"]} parent_path={payload["parent_path"]} position={payload["position"]} rotation={payload["rotation"]} camera_path={payload["camera_path"]} distance_to_camera={payload["distance_to_camera"]}");
            LogP46HGlobalInstructionsCanvasLayoutConfigured("ensure_front_canvas_created");
        }

        private void PositionGlobalInstructionsFrontCanvas()
        {
            if (_globalInstructionsFrontCanvas == null)
            {
                return;
            }

            if (_globalInstructionsFrontCanvasPoseLocked)
            {
                Dictionary<string, object> lockedPayload = BuildRuntimeGlobalInstructionsPayload(
                    "global_instructions_front_canvas_reposition_skipped_locked",
                    "pose_locked");
                TiagoExperimentTelemetry.LogEvent("global_instructions_front_canvas_reposition_skipped_locked", lockedPayload);
                Debug.Log($"[P46G-INSTRUCTIONS] global_instructions_front_canvas_reposition_skipped_locked | canvas_path={lockedPayload["canvas_path"]} position={lockedPayload["position"]} rotation={lockedPayload["rotation"]} reason=pose_locked scene={SceneManager.GetActiveScene().name}");
                return;
            }

            Camera camera = Camera.main ?? FindFirstObjectByType<Camera>();
            if (camera == null)
            {
                Vector3 fallbackPosition = new Vector3(0f, GlobalInstructionsFallbackCanvasY, GlobalInstructionsFrontCanvasDistanceMeters);
                Vector3 fallbackRight = Vector3.right;
                fallbackPosition += fallbackRight * GlobalInstructionsHorizontalOffsetMeters;
                Quaternion fallbackRotation = Quaternion.identity;
                LogGlobalInstructionsFrontCanvasPose("global_instructions_front_canvas_positioning_input", null, Vector3.forward, fallbackPosition, fallbackRotation, false, fallbackRight, GlobalInstructionsHorizontalOffsetMeters);
                _globalInstructionsFrontCanvas.transform.SetPositionAndRotation(fallbackPosition, fallbackRotation);
                _globalInstructionsFrontCanvas.transform.localScale = Vector3.one * GlobalInstructionsFrontCanvasScale;
                LogGlobalInstructionsFrontCanvasPose("global_instructions_front_canvas_positioned", null, Vector3.forward, fallbackPosition, fallbackRotation, false, fallbackRight, GlobalInstructionsHorizontalOffsetMeters);
                return;
            }

            Vector3 cameraForward = camera.transform.forward;
            Vector3 flatForward = new Vector3(cameraForward.x, 0f, cameraForward.z);
            if (flatForward.sqrMagnitude < 0.001f)
            {
                flatForward = Vector3.forward;
            }

            flatForward.Normalize();
            Vector3 flatRight = new Vector3(camera.transform.right.x, 0f, camera.transform.right.z);
            if (flatRight.sqrMagnitude < 0.001f)
            {
                flatRight = Vector3.Cross(Vector3.up, flatForward);
            }

            flatRight.Normalize();
            bool cameraYReliable = camera.transform.position.y >= 1.0f && camera.transform.position.y <= 2.4f;
            float canvasY = cameraYReliable
                ? Mathf.Clamp(camera.transform.position.y - 0.10f, 1.20f, 1.55f)
                : GlobalInstructionsFallbackCanvasY;
            canvasY = Mathf.Max(GlobalInstructionsMinimumCanvasY, canvasY);
            Vector3 position = camera.transform.position + flatForward * GlobalInstructionsFrontCanvasDistanceMeters;
            position += flatRight * GlobalInstructionsHorizontalOffsetMeters;
            position.y = canvasY;
            Quaternion rotation = Quaternion.LookRotation(flatForward, Vector3.up);
            LogGlobalInstructionsFrontCanvasPose("global_instructions_front_canvas_positioning_input", camera, flatForward, position, rotation, cameraYReliable, flatRight, GlobalInstructionsHorizontalOffsetMeters);
            _globalInstructionsFrontCanvas.transform.SetPositionAndRotation(position, rotation);
            _globalInstructionsFrontCanvas.transform.localScale = Vector3.one * GlobalInstructionsFrontCanvasScale;
            _globalInstructionsFrontCanvas.worldCamera = camera;
            LogGlobalInstructionsFrontCanvasPose("global_instructions_front_canvas_positioned", camera, flatForward, position, rotation, cameraYReliable, flatRight, GlobalInstructionsHorizontalOffsetMeters);
        }

        private void LockGlobalInstructionsFrontCanvasPose(string reason)
        {
            if (_globalInstructionsFrontCanvas == null || _globalInstructionsFrontCanvasPoseLocked)
            {
                return;
            }

            _globalInstructionsFrontCanvasPoseLocked = true;
            Dictionary<string, object> payload = BuildRuntimeGlobalInstructionsPayload(
                "global_instructions_front_canvas_pose_locked",
                reason);
            TiagoExperimentTelemetry.LogEvent("global_instructions_front_canvas_pose_locked", payload);
            Debug.Log($"[P46G-INSTRUCTIONS] global_instructions_front_canvas_pose_locked | canvas_path={payload["canvas_path"]} position={payload["position"]} rotation={payload["rotation"]} local_scale={payload["local_scale"]} reason={reason ?? string.Empty} scene={SceneManager.GetActiveScene().name}");
        }

        private void LogGlobalInstructionsFrontCanvasPose(
            string eventName,
            Camera camera,
            Vector3 flatForward,
            Vector3 canvasPosition,
            Quaternion canvasRotation,
            bool cameraYReliable,
            Vector3 horizontalRight,
            float horizontalOffsetMeters)
        {
            Dictionary<string, object> payload = BuildRuntimeGlobalInstructionsPayload(eventName, "positioned");
            Vector3 cameraPosition = camera != null ? camera.transform.position : Vector3.zero;
            Vector3 cameraForward = camera != null ? camera.transform.forward : Vector3.forward;
            payload["camera_position"] = cameraPosition;
            payload["camera_forward"] = cameraForward;
            payload["flat_forward"] = flatForward;
            payload["horizontal_right"] = horizontalRight;
            payload["horizontal_offset_meters"] = horizontalOffsetMeters;
            payload["camera_y_reliable"] = cameraYReliable;
            payload["canvas_position"] = canvasPosition;
            payload["canvas_y"] = canvasPosition.y;
            payload["height_delta"] = camera != null ? canvasPosition.y - cameraPosition.y : 0f;
            payload["rotation"] = canvasRotation.eulerAngles;
            payload["position"] = canvasPosition;
            payload["distance_to_camera"] = camera != null ? Vector3.Distance(cameraPosition, canvasPosition) : -1f;
            TiagoExperimentTelemetry.LogEvent(eventName, payload);
            Debug.Log($"[P46G-INSTRUCTIONS] {eventName} | canvas_path={payload["canvas_path"]} parent_path={payload["parent_path"]} camera_position={payload["camera_position"]} camera_forward={payload["camera_forward"]} flat_forward={payload["flat_forward"]} horizontal_right={payload["horizontal_right"]} horizontal_offset_meters={payload["horizontal_offset_meters"]} camera_y_reliable={payload["camera_y_reliable"]} canvas_position={payload["canvas_position"]} canvas_y={payload["canvas_y"]} height_delta={payload["height_delta"]} rotation={payload["rotation"]} camera_path={payload["camera_path"]} distance_to_camera={payload["distance_to_camera"]} physical_width={payload["physical_width"]} physical_height={payload["physical_height"]} local_scale={payload["local_scale"]}");
        }

        private void ClearGlobalInstructionsFrontContent()
        {
            if (_globalInstructionsFrontContent == null)
            {
                return;
            }

            for (int i = _globalInstructionsFrontContent.childCount - 1; i >= 0; i--)
            {
                Destroy(_globalInstructionsFrontContent.GetChild(i).gameObject);
            }
        }

        private void RefreshGlobalInstructionsFrontCanvasRayTargets(string reason)
        {
            if (_globalInstructionsFrontCanvasObject == null)
            {
                return;
            }

            EnsureEventSystem();
            SetUiLayerRecursively(_globalInstructionsFrontCanvasObject);
            GraphicRaycaster graphicRaycaster = _globalInstructionsFrontCanvasObject.GetComponent<GraphicRaycaster>() ??
                _globalInstructionsFrontCanvasObject.AddComponent<GraphicRaycaster>();
            graphicRaycaster.enabled = true;
            TrackedDeviceGraphicRaycaster trackedRaycaster = EnsureTrackedDeviceGraphicRaycaster(_globalInstructionsFrontCanvasObject);
            if (_globalInstructionsFrontCanvasGroup != null)
            {
                _globalInstructionsFrontCanvasGroup.alpha = 1f;
                _globalInstructionsFrontCanvasGroup.interactable = true;
                _globalInstructionsFrontCanvasGroup.blocksRaycasts = true;
            }

            SetRuntimeProtocolPanelRaycastBlocked(true, reason);
            Debug.Log($"[P46G-INSTRUCTIONS] global_instructions_front_canvas_ray_targets_refreshed | reason={reason ?? string.Empty} canvas_path={GetTransformPath(_globalInstructionsFrontCanvasObject.transform)} graphic_raycaster={graphicRaycaster.enabled} tracked_raycaster={trackedRaycaster != null && trackedRaycaster.enabled} scene={SceneManager.GetActiveScene().name}");
        }

        private void DestroyGlobalInstructionsFrontCanvas(string reason)
        {
            if (_globalInstructionsFrontCanvasObject != null)
            {
                Dictionary<string, object> payload = BuildRuntimeGlobalInstructionsPayload("global_instructions_front_canvas_destroyed", reason);
                TiagoExperimentTelemetry.LogEvent("global_instructions_front_canvas_destroyed", payload);
                Debug.Log($"[P46G-INSTRUCTIONS] global_instructions_front_canvas_destroyed | canvas_path={payload["canvas_path"]} parent_path={payload["parent_path"]} position={payload["position"]} rotation={payload["rotation"]} camera_path={payload["camera_path"]} distance_to_camera={payload["distance_to_camera"]} reason={reason ?? string.Empty}");
                Destroy(_globalInstructionsFrontCanvasObject);
            }

            _globalInstructionsFrontCanvasObject = null;
            _globalInstructionsFrontCanvas = null;
            _globalInstructionsFrontContent = null;
            _globalInstructionsFrontCanvasGroup = null;
            _globalInstructionsFrontCanvasPoseLocked = false;
            _lastNarratedGlobalInstructionPage = -1;
            SetRuntimeProtocolPanelRaycastBlocked(false, reason);
        }

        private void SetRuntimeProtocolPanelRaycastBlocked(bool blocked, string reason)
        {
            if (blocked)
            {
                HideRuntimeProtocolPanelVisualsForGlobalInstructions(reason);
            }
            else
            {
                RestoreRuntimeProtocolPanelVisualsAfterGlobalInstructions(reason);
            }

            GraphicRaycaster graphicRaycaster = GetComponent<GraphicRaycaster>();
            if (graphicRaycaster != null)
            {
                graphicRaycaster.enabled = !blocked;
            }

            TrackedDeviceGraphicRaycaster trackedRaycaster = GetComponent<TrackedDeviceGraphicRaycaster>();
            if (trackedRaycaster != null)
            {
                trackedRaycaster.enabled = !blocked;
            }

            _protocolPanelRaycastDisabledForGlobalInstructions = blocked;
            Debug.Log($"[P46G-INSTRUCTIONS] global_instructions_protocol_panel_raycast_block | blocked={blocked} reason={reason ?? string.Empty} protocol_path={GetTransformPath(transform)} scene={SceneManager.GetActiveScene().name}");
        }

        private void HideRuntimeProtocolPanelVisualsForGlobalInstructions(string reason)
        {
            if (_protocolCanvasGroup == null)
            {
                _protocolCanvasGroup = gameObject.GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
            }

            if (!_protocolPanelVisualStateSaved)
            {
                _protocolPanelSavedAlpha = _protocolCanvasGroup.alpha;
                _protocolPanelSavedInteractable = _protocolCanvasGroup.interactable;
                _protocolPanelSavedBlocksRaycasts = _protocolCanvasGroup.blocksRaycasts;
                _protocolPanelVisualStateSaved = true;
                TiagoExperimentTelemetry.LogEvent(
                    "global_instructions_protocol_panel_visual_state_saved",
                    BuildRuntimeGlobalInstructionsPayload("global_instructions_protocol_panel_visual_state_saved", reason));
                Debug.Log($"[P46G-INSTRUCTIONS] global_instructions_protocol_panel_visual_state_saved | protocol_path={GetTransformPath(transform)} alpha={_protocolPanelSavedAlpha:0.###} interactable={_protocolPanelSavedInteractable} blocksRaycasts={_protocolPanelSavedBlocksRaycasts} reason={reason ?? string.Empty}");
            }

            _protocolCanvasGroup.alpha = 0f;
            _protocolCanvasGroup.interactable = false;
            _protocolCanvasGroup.blocksRaycasts = false;
            TiagoExperimentTelemetry.LogEvent(
                "global_instructions_protocol_panel_visual_hidden",
                BuildRuntimeGlobalInstructionsPayload("global_instructions_protocol_panel_visual_hidden", reason));
            Debug.Log($"[P46G-INSTRUCTIONS] global_instructions_protocol_panel_visual_hidden | protocol_path={GetTransformPath(transform)} alpha={_protocolCanvasGroup.alpha:0.###} reason={reason ?? string.Empty}");
        }

        private void RestoreRuntimeProtocolPanelVisualsAfterGlobalInstructions(string reason)
        {
            if (!_protocolPanelVisualStateSaved || _protocolCanvasGroup == null)
            {
                return;
            }

            _protocolCanvasGroup.alpha = _protocolPanelSavedAlpha;
            _protocolCanvasGroup.interactable = _protocolPanelSavedInteractable;
            _protocolCanvasGroup.blocksRaycasts = _protocolPanelSavedBlocksRaycasts;
            _protocolPanelVisualStateSaved = false;
            TiagoExperimentTelemetry.LogEvent(
                "global_instructions_protocol_panel_visual_restored",
                BuildRuntimeGlobalInstructionsPayload("global_instructions_protocol_panel_visual_restored", reason));
            Debug.Log($"[P46G-INSTRUCTIONS] global_instructions_protocol_panel_visual_restored | protocol_path={GetTransformPath(transform)} alpha={_protocolCanvasGroup.alpha:0.###} interactable={_protocolCanvasGroup.interactable} blocksRaycasts={_protocolCanvasGroup.blocksRaycasts} reason={reason ?? string.Empty}");
        }

        private void AddRuntimeGlobalInstructionBullets(ExperimentGlobalInstructionPage page)
        {
            if (_globalInstructionsFrontContent == null)
            {
                return;
            }

            RectTransform container = CreateRect("GlobalInstructionsRuntimeBullets", _globalInstructionsFrontContent);
            container.anchorMin = new Vector2(0f, 1f);
            container.anchorMax = new Vector2(1f, 1f);
            container.pivot = new Vector2(0f, 1f);
            VerticalLayoutGroup bulletGroup = container.gameObject.AddComponent<VerticalLayoutGroup>();
            bulletGroup.padding = new RectOffset(4, 4, 8, 6);
            bulletGroup.spacing = 12f;
            bulletGroup.childAlignment = TextAnchor.UpperLeft;
            bulletGroup.childControlWidth = true;
            bulletGroup.childControlHeight = true;
            bulletGroup.childForceExpandWidth = true;
            bulletGroup.childForceExpandHeight = false;
            LayoutElement containerLayout = container.gameObject.AddComponent<LayoutElement>();
            containerLayout.minHeight = GlobalInstructionsBodyHeight;
            containerLayout.preferredHeight = GlobalInstructionsBodyHeight;
            containerLayout.flexibleHeight = 1f;

            if (page?.Lines == null)
            {
                return;
            }

            foreach (string line in page.Lines)
            {
                RectTransform row = CreateRect("InstructionBulletRow", container);
                row.anchorMin = new Vector2(0f, 1f);
                row.anchorMax = new Vector2(1f, 1f);
                row.pivot = new Vector2(0f, 1f);
                row.sizeDelta = new Vector2(0f, 54f);
                HorizontalLayoutGroup rowLayout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
                rowLayout.spacing = 14f;
                rowLayout.childAlignment = TextAnchor.UpperLeft;
                rowLayout.childControlWidth = true;
                rowLayout.childControlHeight = true;
                rowLayout.childForceExpandWidth = true;
                rowLayout.childForceExpandHeight = false;
                LayoutElement rowElement = row.gameObject.AddComponent<LayoutElement>();
                rowElement.minHeight = 52f;
                rowElement.preferredHeight = 58f;
                rowElement.minWidth = 780f;
                rowElement.preferredWidth = 820f;
                rowElement.flexibleWidth = 1f;

                RectTransform markerWrap = CreateRect("BulletMarkerWrap", row);
                markerWrap.anchorMin = new Vector2(0f, 1f);
                markerWrap.anchorMax = new Vector2(0f, 1f);
                markerWrap.pivot = new Vector2(0f, 1f);
                LayoutElement markerLayout = markerWrap.gameObject.AddComponent<LayoutElement>();
                markerLayout.minWidth = 28f;
                markerLayout.preferredWidth = 28f;
                markerLayout.minHeight = 42f;
                markerLayout.flexibleWidth = 0f;

                RectTransform marker = CreateRect("BulletMarker", markerWrap);
                marker.anchorMin = new Vector2(0.5f, 0.5f);
                marker.anchorMax = new Vector2(0.5f, 0.5f);
                marker.sizeDelta = new Vector2(12f, 12f);
                marker.anchoredPosition = new Vector2(0f, 8f);
                Image markerImage = marker.gameObject.AddComponent<Image>();
                markerImage.color = new Color(0.48f, 0.76f, 0.86f, 1f);
                markerImage.raycastTarget = false;

                TextMeshProUGUI text = CreateTmpTextInParent("BulletText", row, line ?? string.Empty, 28, 56f);
                text.alignment = TextAlignmentOptions.TopLeft;
                text.textWrappingMode = TextWrappingModes.Normal;
                text.lineSpacing = 16f;
                text.raycastTarget = false;
                LayoutElement textLayout = text.gameObject.AddComponent<LayoutElement>();
                textLayout.minWidth = 700f;
                textLayout.preferredWidth = 760f;
                textLayout.minHeight = 52f;
                textLayout.preferredHeight = 60f;
                textLayout.flexibleWidth = 1f;
            }
        }

        private void AddRuntimeGlobalInstructionButtons()
        {
            if (_globalInstructionsFrontContent == null)
            {
                return;
            }

            RectTransform row = CreateRect("GlobalInstructionsRuntimeButtonRow", _globalInstructionsFrontContent);
            HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 16f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
            LayoutElement rowLayout = row.gameObject.AddComponent<LayoutElement>();
            rowLayout.minHeight = GlobalInstructionsButtonAreaHeight;
            rowLayout.preferredHeight = GlobalInstructionsButtonAreaHeight;

            AddButton("Anterior", PreviousRuntimeGlobalInstructionsPagePressed, ExperimentButtonRole.Secondary, _globalInstructionsState != null && _globalInstructionsState.HasPrevious, row);
            if (_globalInstructionsState != null && _globalInstructionsState.CanComplete)
            {
                AddButton("Entendido / Comenzar", CompleteRuntimeGlobalInstructionsPressed, ExperimentButtonRole.Primary, true, row);
            }
            else
            {
                AddButton("Siguiente", NextRuntimeGlobalInstructionsPagePressed, ExperimentButtonRole.Primary, _globalInstructionsState != null && _globalInstructionsState.HasNext, row);
            }
        }

        private void LogP46HGlobalInstructionsCanvasLayoutConfigured(string source)
        {
            RectTransform canvasRect = _globalInstructionsFrontCanvas != null
                ? _globalInstructionsFrontCanvas.GetComponent<RectTransform>()
                : null;
            Transform canvasTransform = _globalInstructionsFrontCanvas != null ? _globalInstructionsFrontCanvas.transform : null;
            Transform panel = _globalInstructionsFrontContent != null ? _globalInstructionsFrontContent.parent : null;
            float contentHeight = CalculateLayoutContentHeight(_globalInstructionsFrontContent);
            int buttonCount = _globalInstructionsFrontCanvas != null
                ? _globalInstructionsFrontCanvas.GetComponentsInChildren<Button>(false).Length
                : 0;
            string key = $"{source}|{_globalInstructionsState?.PageIndex ?? -1}|{buttonCount}|{contentHeight:0.#}|{(canvasRect != null ? canvasRect.sizeDelta.ToString() : string.Empty)}";
            if (string.Equals(_lastP46HGlobalInstructionsLayoutLogKey, key, StringComparison.Ordinal))
            {
                return;
            }

            _lastP46HGlobalInstructionsLayoutLogKey = key;
            Debug.Log(
                $"p46h_canvas_layout_configured | canvas_name={(_globalInstructionsFrontCanvas != null ? _globalInstructionsFrontCanvas.name : "GlobalInstructionsRuntimeFrontCanvasRoot")} " +
                $"panel_path={GetTransformPath(panel)} size_delta={(canvasRect != null ? canvasRect.sizeDelta : Vector2.zero)} " +
                $"local_scale={(canvasTransform != null ? canvasTransform.localScale : Vector3.zero)} " +
                $"world_position={(canvasTransform != null ? canvasTransform.position : Vector3.zero)} " +
                $"world_rotation={(canvasTransform != null ? canvasTransform.eulerAngles : Vector3.zero)} " +
                $"button_count={buttonCount} content_height={contentHeight:0.#} " +
                $"safe_padding={GlobalInstructionsSafeVerticalPadding:0.#} source={source ?? string.Empty}");
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

        private void NextRuntimeGlobalInstructionsPagePressed()
        {
            NavigateRuntimeGlobalInstructions(1, "next");
        }

        private void PreviousRuntimeGlobalInstructionsPagePressed()
        {
            NavigateRuntimeGlobalInstructions(-1, "previous");
        }

        private void NavigateRuntimeGlobalInstructions(int direction, string action)
        {
            if (_globalInstructionsState == null)
            {
                _globalInstructionsState = ExperimentGlobalInstructionsState.CreateDefault();
            }

            int frame = Time.frameCount;
            if (_globalInstructionsLastNavFrame == frame)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "global_instructions_nav_debounce_ignored",
                    BuildRuntimeGlobalInstructionsPayload("global_instructions_nav_debounce_ignored", action));
                return;
            }

            _globalInstructionsLastNavFrame = frame;
            int before = _globalInstructionsState.PageIndex;
            int after = direction < 0 ? _globalInstructionsState.MovePrevious() : _globalInstructionsState.MoveNext();
            Dictionary<string, object> payload = BuildRuntimeGlobalInstructionsPayload("global_instructions_nav_clicked", action);
            payload["page_before"] = before + 1;
            payload["page_after"] = after + 1;
            TiagoExperimentTelemetry.LogEvent("global_instructions_nav_clicked", payload);
            if (after != before)
            {
                Dictionary<string, object> changedPayload = BuildRuntimeGlobalInstructionsPayload("global_instructions_page_changed", action);
                changedPayload["page_before"] = before + 1;
                changedPayload["page_after"] = after + 1;
                TiagoExperimentTelemetry.LogEvent("global_instructions_page_changed", changedPayload);
            }

            Render(force: true);
        }

        private void CompleteRuntimeGlobalInstructionsPressed()
        {
            if (_globalInstructionsState == null || !_globalInstructionsState.CanComplete)
            {
                return;
            }

            _globalInstructionsAudioPlayer?.StopAudio("global_instructions_runtime_completed");
            _pendingGlobalInstructionsBeforeStart = false;
            TiagoExperimentTelemetry.LogEvent(
                "global_instructions_runtime_completed_start_session",
                BuildRuntimeGlobalInstructionsPayload("global_instructions_runtime_completed_start_session", "completed"));
            Debug.Log($"[P46G-INSTRUCTIONS] global_instructions_runtime_completed_start_session | path={GetTransformPath(transform)} scene={SceneManager.GetActiveScene().name}");
            DestroyGlobalInstructionsFrontCanvas("global_instructions_runtime_completed_start_session");
            StartSession();
        }

        private void PlayRuntimeGlobalInstructionsAudioForCurrentPage()
        {
            if (_globalInstructionsState == null)
            {
                return;
            }

            int pageIndex = _globalInstructionsState.PageIndex;
            if (_lastNarratedGlobalInstructionPage == pageIndex)
            {
                return;
            }

            _lastNarratedGlobalInstructionPage = pageIndex;
            _globalInstructionsAudioPlayer ??= ExperimentGlobalInstructionsAudioPlayer.EnsureAttached(gameObject);
            _globalInstructionsAudioPlayer?.PlayPage(pageIndex);
        }

        private Dictionary<string, object> BuildRuntimeGlobalInstructionsPayload(string eventName, string status)
        {
            Camera camera = Camera.main ?? FindFirstObjectByType<Camera>();
            Transform canvasTransform = _globalInstructionsFrontCanvas != null ? _globalInstructionsFrontCanvas.transform : null;
            RectTransform canvasRect = _globalInstructionsFrontCanvas != null
                ? _globalInstructionsFrontCanvas.GetComponent<RectTransform>()
                : null;
            float distanceToCamera = camera != null && canvasTransform != null
                ? Vector3.Distance(camera.transform.position, canvasTransform.position)
                : -1f;
            Vector3 localScale = canvasTransform != null ? canvasTransform.localScale : Vector3.zero;
            float physicalWidth = canvasRect != null ? canvasRect.sizeDelta.x * localScale.x : 0f;
            float physicalHeight = canvasRect != null ? canvasRect.sizeDelta.y * localScale.y : 0f;
            return new Dictionary<string, object>
            {
                ["event_name"] = eventName ?? string.Empty,
                ["status"] = status ?? string.Empty,
                ["scene"] = SceneManager.GetActiveScene().name,
                ["path"] = GetTransformPath(transform),
                ["modal_path"] = canvasTransform != null ? GetTransformPath(canvasTransform) : string.Empty,
                ["canvas_path"] = canvasTransform != null ? GetTransformPath(canvasTransform) : string.Empty,
                ["parent_path"] = canvasTransform != null && canvasTransform.parent != null ? GetTransformPath(canvasTransform.parent) : string.Empty,
                ["position"] = canvasTransform != null ? canvasTransform.position : Vector3.zero,
                ["rotation"] = canvasTransform != null ? canvasTransform.eulerAngles : Vector3.zero,
                ["camera_path"] = camera != null ? GetTransformPath(camera.transform) : string.Empty,
                ["distance_to_camera"] = distanceToCamera,
                ["physical_width"] = physicalWidth,
                ["physical_height"] = physicalHeight,
                ["local_scale"] = localScale,
                ["session_id"] = ExperimentDataPathResolver.CurrentSessionId,
                ["questionnaire_code"] = ExperimentDataPathResolver.CurrentQuestionnaireCode,
                ["visible_prueba"] = 0,
                ["round_index"] = 0,
                ["page_index"] = _globalInstructionsState != null ? _globalInstructionsState.PageIndex : 0,
                ["page_number"] = _globalInstructionsState != null ? _globalInstructionsState.PageIndex + 1 : 0,
                ["page_count"] = _globalInstructionsState != null ? _globalInstructionsState.PageCount : 0,
                ["timestamp"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };
        }

        public bool RestartSessionFromPauseMenu()
        {
            _orchestrator ??= FindFirstObjectByType<ExperimentSessionOrchestrator>();
            _instructionNarrator?.StopNarration("pause_menu_restart");
            Debug.Log($"[P46D-PAUSE] protocol_restart_begin | ui_path={GetTransformPath(transform)} scene={SceneManager.GetActiveScene().name}");
            bool restarted = _orchestrator != null && _orchestrator.RestartSessionFromRuntime("pause_menu_restart");
            if (!restarted)
            {
                string failureReason = _orchestrator != null
                    ? _orchestrator.LastRuntimeRestartFailureReason
                    : "orchestrator_missing";
                Debug.LogError($"[P46D-PAUSE] protocol_restart_failed | reason={failureReason} ui_path={GetTransformPath(transform)} scene={SceneManager.GetActiveScene().name}");
                return false;
            }

            _completedConditionIds.Clear();
            _lastNarratedInstructionConditionId = string.Empty;
            _incidentConfirmationPending = false;
            _finishingSession = false;
            ResetFinalCompletionState();
            ExperimentRuntimeProtocolSnapshot restartSnapshot = _orchestrator != null ? _orchestrator.GetRuntimeProtocolSnapshot() : null;
            _restartIntroAwaitingBeginButton = restartSnapshot != null &&
                restartSnapshot.SessionActive &&
                restartSnapshot.HasNextTrial &&
                !restartSnapshot.TrialActive &&
                !restartSnapshot.RoundActive &&
                !restartSnapshot.TrialTransitionBusy;
            _screen = _restartIntroAwaitingBeginButton
                ? ScreenState.Instructions
                : restartSnapshot != null && (restartSnapshot.TrialActive || restartSnapshot.RoundActive || restartSnapshot.TrialTransitionBusy)
                    ? ScreenState.Progress
                    : ScreenState.Instructions;
            SetProtocolVisible(true);
            Debug.Log($"[P46D-PAUSE] protocol_restart_intro_screen_requested | ui_path={GetTransformPath(transform)} scene={SceneManager.GetActiveScene().name} condition_id={restartSnapshot?.NextConditionId ?? string.Empty} visible_prueba={restartSnapshot?.NextVisiblePruebaNumber ?? 0} condition_order_index={restartSnapshot?.NextConditionOrderIndex ?? -1} trial_index={restartSnapshot?.NextTrialIndex ?? 0} round_index={restartSnapshot?.NextRoundIndexWithinCondition ?? 0} screen={_screen} trial_active={restartSnapshot != null && restartSnapshot.TrialActive} round_active={restartSnapshot != null && restartSnapshot.RoundActive}");
            Debug.Log($"[P46D-PAUSE] protocol_restart_ui_reset | screen={_screen} completed_conditions={_completedConditionIds.Count} incident_pending={_incidentConfirmationPending} finishing={_finishingSession}");
            Render(force: true);
            ExperimentRuntimeProtocolSnapshot snapshot = _orchestrator != null ? _orchestrator.GetRuntimeProtocolSnapshot() : null;
            Button beginButton = FindButtonByLabel("Comenzar la prueba") ?? FindButtonByLabel("Comenzar prueba");
            int activeButtonCount = _canvas != null ? _canvas.GetComponentsInChildren<Button>(false).Length : 0;
            int interactableButtonCount = 0;
            if (_canvas != null)
            {
                foreach (Button button in _canvas.GetComponentsInChildren<Button>(false))
                {
                    if (button != null && button.interactable)
                    {
                        interactableButtonCount++;
                    }
                }
            }

            Debug.Log($"[P46D-PAUSE] protocol_restart_buttons_enabled | active_button_count={activeButtonCount} interactable_button_count={interactableButtonCount} next_condition={snapshot?.NextConditionId ?? string.Empty} screen={_screen}");
            Debug.Log($"[P46D-PAUSE] protocol_restart_intro_screen_shown | session_id={snapshot?.SessionId ?? string.Empty} condition_id={snapshot?.NextConditionId ?? string.Empty} visible_prueba={snapshot?.NextVisiblePruebaNumber ?? 0} condition_order_index={snapshot?.NextConditionOrderIndex ?? -1} trial_index={snapshot?.NextTrialIndex ?? 0} round_index={snapshot?.NextRoundIndexWithinCondition ?? 0} screen={_screen} begin_button_active={beginButton != null && beginButton.gameObject.activeInHierarchy} begin_button_interactable={beginButton != null && beginButton.interactable && beginButton.IsInteractable()} trial_active={snapshot != null && snapshot.TrialActive} round_active={snapshot != null && snapshot.RoundActive}");
            Debug.Log($"[P46D-PAUSE] protocol_restart_waiting_for_begin_button | session_id={snapshot?.SessionId ?? string.Empty} condition_id={snapshot?.NextConditionId ?? string.Empty} visible_prueba={snapshot?.NextVisiblePruebaNumber ?? 0} condition_order_index={snapshot?.NextConditionOrderIndex ?? -1} protocol_ui_state={_screen} begin_button_active={beginButton != null && beginButton.gameObject.activeInHierarchy} begin_button_interactable={beginButton != null && beginButton.interactable && beginButton.IsInteractable()} robot_mode={ReadRobotModeForRestartLog()} current_task={ReadRobotCurrentTaskForRestartLog()} pending_task={ReadRobotPendingTaskForRestartLog()} held_object_id={ReadHeldObjectForRestartLog()}");
            if (snapshot == null || !snapshot.SessionActive || string.IsNullOrWhiteSpace(snapshot.NextConditionId) || beginButton == null || !beginButton.interactable || snapshot.TrialActive || snapshot.RoundActive)
            {
                Debug.LogError($"[P46D-PAUSE] protocol_restart_failed | reason=intro_not_ready_after_render session_active={snapshot != null && snapshot.SessionActive} next_condition={snapshot?.NextConditionId ?? string.Empty} active_button_count={activeButtonCount} interactable_button_count={interactableButtonCount} begin_button_found={beginButton != null} trial_active={snapshot != null && snapshot.TrialActive} round_active={snapshot != null && snapshot.RoundActive}");
            }
            else
            {
                Debug.Log($"[P46D-PAUSE] protocol_restart_ready_for_first_trial | session_id={snapshot.SessionId} next_condition={snapshot.NextConditionId} next_round={snapshot.NextRoundIndexWithinCondition} screen={_screen}");
            }

            return true;
        }

        public void EndSessionFromPauseMenu(string historyStatus, string source)
        {
            _orchestrator ??= FindFirstObjectByType<ExperimentSessionOrchestrator>();
            _instructionNarrator?.StopNarration(source ?? "pause_menu_exit");
            _incidentConfirmationPending = false;
            _finishingSession = true;
            _orchestrator?.EndSessionFromRuntime(historyStatus, source ?? "pause_menu_exit");
        }

        public void ExitToStartWithoutSaveFromPauseMenu()
        {
            _orchestrator ??= FindFirstObjectByType<ExperimentSessionOrchestrator>();
            _instructionNarrator?.StopNarration("pause_exit_to_start_without_save");
            _incidentConfirmationPending = false;
            _finishingSession = true;
            _orchestrator?.EndSessionFromRuntime(
                ExperimentSessionIdHistoryStore.ClosedWithoutCompletionStatus,
                "exit_to_start_without_save");
            ResetFinalCompletionState();
            ReturnToStartScreenAfterRuntimeExit("pause_exit_to_start_without_save");
        }

        public void ReturnToStartScreenFromPauseMenu(string reason)
        {
            ReturnToStartScreenAfterRuntimeExit(reason ?? "pause_menu_return_to_start");
        }

        private void RenderStart(ExperimentRuntimeProtocolSnapshot snapshot)
        {
            TiagoExperimentTelemetry.LogEvent(
                "p45c17e_legacy_runtime_protocol_start_screen_suppressed",
                new Dictionary<string, object>
                {
                    ["scene_name"] = SceneManager.GetActiveScene().name,
                    ["session_active"] = snapshot != null && snapshot.SessionActive,
                    ["launched_from_start_scene"] = _launchedFromStartScene,
                    ["reason"] = "runtime_protocol_start_screen_removed"
                });
            AddTitle("Preparando protocolo experimental");
            AddInstructionBody("Espera un momento...");
            if (_orchestrator == null)
            {
                AddBody("Orquestador no encontrado.");
            }
        }

        private void RenderSavedExitResumeFailure()
        {
            AddTitle("No se pudo recuperar la sesión");
            if (string.Equals(
                    _savedExitResumeFailureReason,
                    ExperimentSessionOrchestrator.ConditionOrderAssignmentMissingForResumeReason,
                    StringComparison.Ordinal))
            {
                AddInstructionBody("No se encontro una asignacion original valida para este participante. La sesion no se ha iniciado ni modificado.");
                AddBody("Vuelve al inicio para decidir si descartas el punto guardado y comienzas una sesion nueva.");
                AddBody("Motivo tecnico: " + _savedExitResumeFailureReason);
            }
            else
            {
                AddInstructionBody("El punto guardado no ha superado la validación y el orden experimental no se ha modificado.");
                AddBody("Vuelve al inicio para conservar el punto guardado y elegir una opción segura.");
                if (_showTechnicalDiagnostics && !string.IsNullOrWhiteSpace(_savedExitResumeFailureReason))
                {
                    AddBody("Diagnóstico: " + _savedExitResumeFailureReason);
                }
            }

            AddSpacer(16f);
            AddButton("Volver al inicio", ReturnToStartAfterSavedExitResumeFailure, ExperimentButtonRole.Secondary, true);
        }

        private void ReturnToStartAfterSavedExitResumeFailure()
        {
            TiagoExperimentTelemetry.LogEvent(
                "p46j03r1_saved_exit_resume_failure_return_to_start",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["failure_reason"] = _savedExitResumeFailureReason,
                    ["checkpoint_preserved"] = true
                });
            _savedExitResumeFailureReason = string.Empty;
            ReturnToStartScreenAfterRuntimeExit("saved_exit_resume_validation_failed");
        }

        private void RenderInstructions(ExperimentRuntimeProtocolSnapshot snapshot)
        {
            string conditionId = snapshot?.NextConditionId ?? string.Empty;
            UserFacingTrialLabel userLabel = ResolveUserFacingTrialLabel(conditionId, snapshot, useNextTrial: true);
            ParticipantInstruction instruction = InstructionFor(conditionId, userLabel.Label);
            if (!string.IsNullOrWhiteSpace(conditionId) &&
                !string.Equals(_lastNarratedInstructionConditionId, conditionId + "|" + userLabel.Label, StringComparison.Ordinal))
            {
                _lastNarratedInstructionConditionId = conditionId + "|" + userLabel.Label;
                _instructionNarrator?.PlayInstruction(conditionId, userLabel.Label);
            }

            LogUserFacingLabelResolved(conditionId, userLabel, instruction);
            LogInstructionCanvasRendered(conditionId, userLabel, instruction);
            AddTitle(instruction.Title);
            AddSectionLabel(instruction.Subtitle);
            AddInstructionBody(instruction.Body);
            AddSectionLabel("Instrucciones operativas");
            AddInstructionBody(OperationalInstructionFor(conditionId));
            if (string.Equals(conditionId, ExperimentCompensatedConditionOrder.C11, StringComparison.Ordinal))
            {
                ExperimentVoicePipelineBootstrap voiceBootstrap = ExperimentVoicePipelineBootstrap.EnsureExists();
                voiceBootstrap.EnsurePreparationStarted();
                AddSectionLabel("Ejemplos de ordenes vocales");
                AddExamples(instruction.Examples);
                if (!voiceBootstrap.IsPrepared)
                {
                    AddSectionLabel("Reconocimiento vocal");
                    AddInstructionBody(voiceBootstrap.BuildParticipantStatusMessage());
                    if (voiceBootstrap.CanRetryPermission || voiceBootstrap.CanRetryPreparation)
                    {
                        AddButton(
                            "Reintentar reconocimiento vocal",
                            voiceBootstrap.RetryPermissionOrPreparation,
                            ExperimentButtonRole.Primary,
                            true);
                    }

                    if (voiceBootstrap.PermissionState == MicrophonePermissionState.DeniedDontAskAgain ||
                        voiceBootstrap.PreparationState == VoicePipelinePreparationState.Failed)
                    {
                        AddButton(
                            "Volver al inicio",
                            ReturnToStartAfterVoicePreparationFailure,
                            ExperimentButtonRole.Secondary,
                            true);
                    }
                }

                LogC11Preflight(conditionId);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (_showTechnicalDiagnostics)
                {
                    AddBody(FormatC11PreflightDiagnostics(BuildC11PreflightPayload(conditionId)));
                }
#endif
            }

            AddSpacer(10f);
            AddButton("Repetir instrucciones", () => _instructionNarrator?.PlayInstruction(conditionId, userLabel.Label, forceRestart: true), ExperimentButtonRole.Secondary, !string.IsNullOrWhiteSpace(conditionId));
            bool voiceReady = !string.Equals(conditionId, ExperimentCompensatedConditionOrder.C11, StringComparison.Ordinal) ||
                (ExperimentVoicePipelineBootstrap.Instance != null && ExperimentVoicePipelineBootstrap.Instance.IsPrepared);
            AddButton("Comenzar la prueba", BeginCondition, ExperimentButtonRole.Primary, _orchestrator != null && snapshot != null && snapshot.SessionActive && snapshot.HasNextTrial && !snapshot.TrialTransitionBusy && voiceReady);
        }

        private void ReturnToStartAfterVoicePreparationFailure()
        {
            ExperimentVoicePipelineBootstrap bootstrap = ExperimentVoicePipelineBootstrap.Instance;
            TiagoExperimentTelemetry.LogEvent(
                "voice_pipeline_failure_return_to_start",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["permission_state"] = bootstrap?.PermissionState.ToString() ?? string.Empty,
                    ["preparation_state"] = bootstrap?.PreparationState.ToString() ?? string.Empty,
                    ["failure_reason"] = bootstrap?.FailureReason ?? string.Empty
                });
            _orchestrator?.EndSessionFromRuntime(
                ExperimentSessionIdHistoryStore.ClosedWithoutCompletionStatus,
                "voice_pipeline_preparation_failed");
            ReturnToStartScreenAfterRuntimeExit("voice_pipeline_preparation_failed");
        }

        private void RenderProgress(ExperimentRuntimeProtocolSnapshot snapshot)
        {
            AddTitle("Progreso");
            if (snapshot == null)
            {
                AddBody("Orquestador no encontrado.");
                return;
            }

            UserFacingTrialLabel userLabel = ResolveUserFacingTrialLabel(snapshot.CurrentConditionId, snapshot, useNextTrial: false);
            AddBody($"Prueba actual: {userLabel.Label}");
            AddBody($"Ronda: {snapshot.CurrentRoundIndexWithinCondition} / {snapshot.RoundsPerCondition}");
            AddBody($"Cajas: {snapshot.CompletedBoxes} / {snapshot.TotalBoxes}");
            AddBody($"Estado: {StatusText(snapshot)}");

            if (snapshot.CanAdvance && snapshot.HasNextTrial)
            {
                bool sameCondition = string.Equals(snapshot.CurrentConditionId, snapshot.NextConditionId, StringComparison.Ordinal);
                if (sameCondition)
                {
                    AddButton("Siguiente ronda", BeginCondition, ExperimentButtonRole.Primary, !snapshot.TrialTransitionBusy);
                }
                else
                {
                    AddButton("Siguiente prueba", OpenNextConditionInstructions, ExperimentButtonRole.Primary, !snapshot.TrialTransitionBusy);
                }
            }
            else if (snapshot.CanAdvance && !snapshot.HasNextTrial)
            {
                AddButton("Finalizar sesion", FinishSession, ExperimentButtonRole.Primary, !snapshot.TrialTransitionBusy);
            }
            else
            {
                AddButton("Avance no disponible", () => { }, ExperimentButtonRole.Primary, false);
            }

            AddSpacer(28f);
            AddSectionLabel("Incidencia tecnica");
            if (_incidentConfirmationPending)
            {
                AddBody("Accion excepcional: cierra la ronda como no valida para continuar la sesion.");
                AddButton("Confirmar cierre por incidencia", ConfirmTechnicalIncidentClose, ExperimentButtonRole.Warning, !snapshot.TrialTransitionBusy);
                AddButton("Cancelar incidencia", () =>
                {
                    _incidentConfirmationPending = false;
                    Render(force: true);
                }, ExperimentButtonRole.Secondary, true);
            }
            else
            {
                AddButton("Cerrar ronda por incidencia", () =>
                {
                    _incidentConfirmationPending = true;
                    ExperimentRuntimeProtocolSnapshot clickedSnapshot = _orchestrator != null ? _orchestrator.GetRuntimeProtocolSnapshot() : snapshot;
                    Debug.Log($"[P46D-PAUSE] protocol_wall_incident_skip_clicked | scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={clickedSnapshot?.CurrentConditionId ?? string.Empty} trial_index={clickedSnapshot?.CurrentGlobalRoundIndex ?? 0} round_index={clickedSnapshot?.CurrentRoundIndexWithinCondition ?? 0}");
                    Render(force: true);
                }, ExperimentButtonRole.Warning, snapshot.SessionActive && !snapshot.TrialTransitionBusy && !snapshot.CanAdvance);
            }
        }

        private void RenderFinal(ExperimentRuntimeProtocolSnapshot snapshot)
        {
            PositionFinalCompletionModalInFrontOfUser();
            ExperimentFinalQuestionnaireCompletionModel model = BuildFinalCompletionModel(snapshot);
            LogFinalCompletionScreenShownIfNeeded(model);
            AddTitle(model.Title);
            AddInstructionBody("Muchas gracias por tu participacion.");
            AddQuestionnaireCodeBlock(model.QuestionnaireCode);
            AddBody(model.PrimaryInstruction);
            AddBody(model.AssociationExplanation);
            AddBody(model.HistoryNote);
            AddSpacer(12f);
            AddButton("He anotado el codigo / Finalizar experimento", AcknowledgeFinalCompletion, ExperimentButtonRole.Primary, model.HasValidQuestionnaireCode);
        }

        private void StartSession()
        {
            Debug.Log($"[P46D-04] protocol_start_session_before_orchestrator | scene={SceneManager.GetActiveScene().name} protocol_ui_path={GetTransformPath(transform)}");
            ExperimentSessionIdHistoryEntry checkpoint = s_pendingSavedExitCheckpoint;
            bool started = checkpoint != null
                ? _orchestrator != null && _orchestrator.StartSessionFromSavedExitCheckpoint(checkpoint, Array.Empty<string>())
                : _orchestrator != null && _orchestrator.StartSessionFromRuntime(0, Array.Empty<string>());
            if (started)
            {
                ResetFinalCompletionState();
                _completedConditionIds.Clear();
                _lastNarratedInstructionConditionId = string.Empty;
                _screen = ScreenState.Instructions;
                if (checkpoint != null)
                {
                    Debug.Log($"[P46D-PAUSE] saved_exit_resume_from_checkpoint_selected | previous_session_id={checkpoint.session_id} questionnaire_code={checkpoint.questionnaire_code} saved_condition_id={checkpoint.saved_condition_id} saved_visible_prueba={checkpoint.saved_visible_prueba} resume_policy={checkpoint.resume_policy}");
                    s_pendingSavedExitCheckpoint = null;
                }
            }
            else if (checkpoint != null)
            {
                _savedExitResumeFailureReason = _orchestrator != null
                    ? _orchestrator.LastSavedExitResumeFailureReason
                    : "orchestrator_missing";
                s_pendingSavedExitCheckpoint = null;
                _screen = ScreenState.ResumeFailure;
                TiagoExperimentTelemetry.LogEvent(
                    "p46j03r1_saved_exit_resume_failure_presented",
                    new Dictionary<string, object>
                    {
                        ["scene"] = SceneManager.GetActiveScene().name,
                        ["failure_reason"] = _savedExitResumeFailureReason,
                        ["checkpoint_preserved"] = true,
                        ["return_action_available"] = true
                    });
            }

            Debug.Log($"[P46D-04] protocol_start_session_before_render | scene={SceneManager.GetActiveScene().name} screen={_screen}");
            Render(force: true);
            Debug.Log($"[P46D-04] protocol_start_session_after_render | scene={SceneManager.GetActiveScene().name} screen={_screen} active_button_count={(_canvas != null ? _canvas.GetComponentsInChildren<Button>(false).Length : 0)}");
        }

        private void BeginCondition()
        {
            _incidentConfirmationPending = false;
            bool restartBegin = _restartIntroAwaitingBeginButton;
            ExperimentRuntimeProtocolSnapshot beforeSnapshot = _orchestrator != null ? _orchestrator.GetRuntimeProtocolSnapshot() : null;
            if (!RevalidateAdvanceCallback(beforeSnapshot, "begin_condition"))
            {
                return;
            }

            if (string.Equals(beforeSnapshot?.NextConditionId, ExperimentCompensatedConditionOrder.C11, StringComparison.Ordinal) &&
                (ExperimentVoicePipelineBootstrap.Instance == null || !ExperimentVoicePipelineBootstrap.Instance.IsPrepared))
            {
                ExperimentVoicePipelineBootstrap.EnsureExists().EnsurePreparationStarted();
                TiagoExperimentTelemetry.LogEvent(
                    "c11_trial_start_blocked_voice_pipeline_not_ready",
                    new Dictionary<string, object>
                    {
                        ["scene"] = SceneManager.GetActiveScene().name,
                        ["condition_id"] = beforeSnapshot?.NextConditionId ?? string.Empty,
                        ["visible_prueba"] = beforeSnapshot?.NextVisiblePruebaNumber ?? 0,
                        ["round"] = beforeSnapshot?.NextRoundIndexWithinCondition ?? 0,
                        ["voice_pipeline_state"] = ExperimentVoicePipelineBootstrap.Instance?.ReadinessToken ?? string.Empty
                    });
                _screen = ScreenState.Instructions;
                Render(force: true);
                return;
            }

            _instructionNarrator?.StopNarration("condition_started");
            if (_xrRigResetter != null)
            {
                SetProtocolVisible(false);
            }

            if (restartBegin)
            {
                Debug.Log($"[P46D-PAUSE] protocol_restart_begin_button_clicked | session_id={beforeSnapshot?.SessionId ?? string.Empty} condition_id={beforeSnapshot?.NextConditionId ?? string.Empty} visible_prueba={beforeSnapshot?.NextVisiblePruebaNumber ?? 0} condition_order_index={beforeSnapshot?.NextConditionOrderIndex ?? -1} trial_index={beforeSnapshot?.NextTrialIndex ?? 0} round_index={beforeSnapshot?.NextRoundIndexWithinCondition ?? 0} protocol_ui_state={_screen} robot_mode={ReadRobotModeForRestartLog()} current_task={ReadRobotCurrentTaskForRestartLog()} pending_task={ReadRobotPendingTaskForRestartLog()} held_object_id={ReadHeldObjectForRestartLog()}");
            }

            _orchestrator?.StartCurrentConditionFromRuntime();
            _restartIntroAwaitingBeginButton = false;
            _screen = ScreenState.Progress;
            if (_xrRigResetter == null)
            {
                Render(force: true);
            }
            if (restartBegin)
            {
                ExperimentRuntimeProtocolSnapshot afterSnapshot = _orchestrator != null ? _orchestrator.GetRuntimeProtocolSnapshot() : null;
                Debug.Log($"[P46D-PAUSE] protocol_restart_trial_started_after_user_begin | session_id={afterSnapshot?.SessionId ?? string.Empty} condition_id={afterSnapshot?.CurrentConditionId ?? beforeSnapshot?.NextConditionId ?? string.Empty} visible_prueba={afterSnapshot?.CurrentVisiblePruebaNumber ?? beforeSnapshot?.NextVisiblePruebaNumber ?? 0} condition_order_index={afterSnapshot?.CurrentConditionOrderIndex ?? beforeSnapshot?.NextConditionOrderIndex ?? -1} trial_index={afterSnapshot?.CurrentTrialIndex ?? 0} round_index={afterSnapshot?.CurrentRoundIndexWithinCondition ?? 0} trial_active={afterSnapshot != null && afterSnapshot.TrialActive} round_active={afterSnapshot != null && afterSnapshot.RoundActive} robot_mode={ReadRobotModeForRestartLog()} current_task={ReadRobotCurrentTaskForRestartLog()} pending_task={ReadRobotPendingTaskForRestartLog()} held_object_id={ReadHeldObjectForRestartLog()}");
            }
        }

        private void HandleXrAlignmentGateReleased(
            ExperimentRuntimeContext context,
            ExperimentXrRigAlignmentResult result)
        {
            if (!_started || context == null)
            {
                return;
            }

            PositionAtProtocolStation();
            SetProtocolVisible(true);
            Render(force: true);
        }

        private void ConfirmTechnicalIncidentClose()
        {
            _incidentConfirmationPending = false;
            ExperimentRuntimeProtocolSnapshot snapshot = _orchestrator != null ? _orchestrator.GetRuntimeProtocolSnapshot() : null;
            Debug.Log($"[P46D-PAUSE] protocol_wall_incident_confirm_clicked | scene={SceneManager.GetActiveScene().name} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={snapshot?.CurrentConditionId ?? string.Empty} trial_index={snapshot?.CurrentGlobalRoundIndex ?? 0} round_index={snapshot?.CurrentRoundIndexWithinCondition ?? 0}");
            _orchestrator?.CloseCurrentTrialForTechnicalIncidentFromRuntime("runtime_protocol_ui_confirmed");
            _screen = ScreenState.Progress;
            Render(force: true);
        }

        private void FinishSession()
        {
            ExperimentRuntimeProtocolSnapshot snapshot = _orchestrator != null ? _orchestrator.GetRuntimeProtocolSnapshot() : null;
            if (!RevalidateAdvanceCallback(snapshot, "finish_session"))
            {
                return;
            }

            _instructionNarrator?.StopNarration("session_finished");
            if (snapshot != null)
            {
                RememberCompleted(snapshot.CurrentConditionId);
            }

            _incidentConfirmationPending = false;
            _finishingSession = true;
            CaptureFinalCompletionState(snapshot);
            TiagoExperimentTelemetry.LogEvent("experiment_runtime_protocol_finish_requested", BuildFinishPayload(snapshot, "finish_requested"));
            _orchestrator?.EndSessionFromRuntime();
            ExperimentRuntimeProtocolSnapshot finishedSnapshot = _orchestrator != null ? _orchestrator.GetRuntimeProtocolSnapshot() : snapshot;
            TiagoExperimentTelemetry.LogEvent("experiment_runtime_protocol_session_finished", BuildFinishPayload(finishedSnapshot ?? snapshot, "completed_by_user"));
            _screen = ScreenState.Final;
            Render(force: true);
            _instructionNarrator?.PlayThankYou();
        }

        private void OpenNextConditionInstructions()
        {
            ExperimentRuntimeProtocolSnapshot snapshot = _orchestrator != null ? _orchestrator.GetRuntimeProtocolSnapshot() : null;
            if (!RevalidateAdvanceCallback(snapshot, "open_next_condition_instructions"))
            {
                return;
            }

            RememberCompleted(snapshot?.CurrentConditionId);
            _incidentConfirmationPending = false;
            _screen = ScreenState.Instructions;
            Render(force: true);
        }

        private bool RevalidateAdvanceCallback(ExperimentRuntimeProtocolSnapshot snapshot, string action)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.CurrentTrialId))
            {
                return true;
            }

            string reason = "orchestrator_missing";
            if (_orchestrator != null && _orchestrator.CanAdvanceCurrentTrialFromRuntime(out reason))
            {
                return true;
            }

            TiagoExperimentTelemetry.LogEvent(
                "experiment_runtime_protocol_advance_callback_blocked",
                new Dictionary<string, object>
                {
                    ["action"] = action ?? string.Empty,
                    ["reason"] = reason,
                    ["trial_id"] = snapshot.CurrentTrialId,
                    ["condition_id"] = snapshot.CurrentConditionId,
                    ["round_index"] = snapshot.CurrentRoundIndexWithinCondition
                });
            _screen = ScreenState.Progress;
            Render(force: true);
            return false;
        }

        private void AcknowledgeFinalCompletion()
        {
            ExperimentFinalQuestionnaireCompletionModel model = BuildFinalCompletionModel(null);
            if (!_finalCompletionAcknowledged)
            {
                _finalCompletionAcknowledged = true;
                ExperimentSessionIdHistoryStore.MarkQuestionnaireCodeAcknowledged(model.SessionId);
                LogFinalCompletionEvent("questionnaire_code_acknowledged", model, "acknowledged");
                LogFinalCompletionEvent("final_completion_acknowledged", model, "acknowledged");
            }

            ReturnToWelcomeAfterFinalAcknowledgement();
        }

        private void ReturnToWelcomeAfterFinalAcknowledgement()
        {
            ReturnToStartScreenAfterRuntimeExit("final_completion_acknowledged");
        }

        private void ReturnToStartScreenAfterRuntimeExit(string reason)
        {
            _instructionNarrator?.StopNarration("return_to_welcome");
            _completedConditionIds.Clear();
            _lastNarratedInstructionConditionId = string.Empty;
            _incidentConfirmationPending = false;
            _finishingSession = false;
            _screen = ScreenState.Start;
            SetProtocolVisible(false);
            if (_launchedFromStartScene)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "p45c17_return_to_start_scene_requested",
                    new Dictionary<string, object>
                    {
                        ["from_scene"] = SceneManager.GetActiveScene().name,
                        ["target_scene"] = StartSceneName,
                        ["reason"] = reason ?? string.Empty,
                        ["launched_from_start_scene"] = true
                    });
                TiagoExperimentTelemetry.LogEvent(
                    "p45c17_loading_start_scene",
                    new Dictionary<string, object>
                    {
                        ["from_scene"] = SceneManager.GetActiveScene().name,
                        ["target_scene"] = StartSceneName,
                        ["reason"] = reason ?? string.Empty
                    });
                TiagoExperimentTelemetry.LogEvent(
                    "p45c17d_return_to_start_scene_requested",
                    new Dictionary<string, object>
                    {
                        ["from_scene"] = SceneManager.GetActiveScene().name,
                        ["target_scene"] = StartSceneName,
                        ["reason"] = reason ?? string.Empty,
                        ["launched_from_start_scene"] = true
                    });
                TiagoExperimentTelemetry.LogEvent(
                    "p45c17d_loading_start_scene",
                    new Dictionary<string, object>
                    {
                        ["from_scene"] = SceneManager.GetActiveScene().name,
                        ["target_scene"] = StartSceneName,
                        ["reason"] = reason ?? string.Empty
                    });
                if (!ExperimentSimulationPauseAuthority.NormalizeForRunningScene("runtime_protocol_return_to_start:" + (reason ?? string.Empty), this))
                {
                    Debug.LogError($"[P46O-04] experiment_return_to_start_blocked_by_pause_owner | reason={reason ?? string.Empty} owners={ExperimentSimulationPauseAuthority.ActiveOwners} time_scale={Time.timeScale:0.###}", this);
                    return;
                }

                SceneManager.LoadScene(StartSceneName);
                return;
            }

            TiagoExperimentTelemetry.LogEvent(
                "p45c17_return_existing_single_scene_behavior",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["reason"] = reason ?? string.Empty,
                    ["launched_from_start_scene"] = false
                });
            ExperimentRuntimeStartScreenUI startScreen = FindFirstObjectByType<ExperimentRuntimeStartScreenUI>(FindObjectsInactive.Include);
            if (startScreen != null)
            {
                startScreen.ShowAfterProtocolFinished();
            }
            else
            {
                SetProtocolVisible(true);
                Render(force: true);
            }
        }

        private void CaptureFinalCompletionState(ExperimentRuntimeProtocolSnapshot snapshot)
        {
            UserFacingTrialLabel label = ResolveUserFacingTrialLabel(snapshot?.CurrentConditionId ?? string.Empty, snapshot, useNextTrial: false);
            _finalCompletionSessionId = snapshot?.SessionId ?? ExperimentDataPathResolver.CurrentSessionId;
            _finalCompletionQuestionnaireCode = ExperimentDataPathResolver.CurrentQuestionnaireCode;
            _finalCompletionVisiblePrueba = label.SequenceIndex > 0 ? label.SequenceIndex : 3;
            _finalCompletionRoundIndex = snapshot != null ? snapshot.CurrentRoundIndexWithinCondition : 2;
            _finalCompletionStatus = ExperimentSessionIdHistoryStore.CompletedStatus;
            _finalCompletionAcknowledged = false;
            _finalCompletionScreenLogged = false;
        }

        private void ResetFinalCompletionState()
        {
            _finalCompletionSessionId = string.Empty;
            _finalCompletionQuestionnaireCode = string.Empty;
            _finalCompletionVisiblePrueba = 0;
            _finalCompletionRoundIndex = 0;
            _finalCompletionStatus = ExperimentSessionIdHistoryStore.CompletedStatus;
            _finalCompletionAcknowledged = false;
            _finalCompletionScreenLogged = false;
        }

        private ExperimentFinalQuestionnaireCompletionModel BuildFinalCompletionModel(ExperimentRuntimeProtocolSnapshot snapshot)
        {
            string sessionId = !string.IsNullOrWhiteSpace(_finalCompletionSessionId)
                ? _finalCompletionSessionId
                : snapshot?.SessionId ?? ExperimentDataPathResolver.CurrentSessionId;
            string questionnaireCode = !string.IsNullOrWhiteSpace(_finalCompletionQuestionnaireCode)
                ? _finalCompletionQuestionnaireCode
                : ExperimentDataPathResolver.CurrentQuestionnaireCode;
            int visiblePrueba = _finalCompletionVisiblePrueba > 0
                ? _finalCompletionVisiblePrueba
                : snapshot != null ? ResolveUserFacingTrialLabel(snapshot.CurrentConditionId, snapshot, useNextTrial: false).SequenceIndex : 3;
            int roundIndex = _finalCompletionRoundIndex > 0
                ? _finalCompletionRoundIndex
                : snapshot?.CurrentRoundIndexWithinCondition ?? 2;
            return new ExperimentFinalQuestionnaireCompletionModel(
                sessionId,
                questionnaireCode,
                visiblePrueba,
                roundIndex,
                _finalCompletionStatus);
        }

        private void LogFinalCompletionScreenShownIfNeeded(ExperimentFinalQuestionnaireCompletionModel model)
        {
            if (_finalCompletionScreenLogged)
            {
                return;
            }

            _finalCompletionScreenLogged = true;
            LogFinalCompletionEvent("final_completion_screen_shown", model, "shown");
            LogFinalCompletionEvent("questionnaire_code_shown", model, "shown");
        }

        private void LogFinalCompletionEvent(string eventName, ExperimentFinalQuestionnaireCompletionModel model, string status)
        {
            model ??= BuildFinalCompletionModel(null);
            Dictionary<string, object> payload = model.ToEventPayload(eventName);
            payload["action_status"] = status ?? string.Empty;
            payload["scene"] = SceneManager.GetActiveScene().name;
            TiagoExperimentTelemetry.LogEvent(eventName, payload);
            Debug.Log($"[P46G-INSTRUCTIONS] {eventName} | session_id={model.SessionId} questionnaire_code={model.QuestionnaireCode} visible_prueba={model.VisiblePrueba} round_index={model.RoundIndex} status={model.Status} action_status={payload["action_status"]} timestamp={payload["timestamp"]}");
        }

        private Dictionary<string, object> BuildFinishPayload(ExperimentRuntimeProtocolSnapshot snapshot, string result)
        {
            return new Dictionary<string, object>
            {
                ["participant_id"] = snapshot?.ParticipantId ?? "auto_pending",
                ["session_id"] = snapshot?.SessionId ?? string.Empty,
                ["questionnaire_code"] = ExperimentDataPathResolver.CurrentQuestionnaireCode,
                ["condition_id"] = snapshot?.CurrentConditionId ?? string.Empty,
                ["trial_id"] = snapshot?.CurrentTrialId ?? string.Empty,
                ["round_id"] = snapshot?.CurrentRoundId ?? string.Empty,
                ["total_planned_trials"] = snapshot?.TotalPlannedTrials ?? 0,
                ["completed_trials"] = EstimateCompletedTrials(snapshot),
                ["scene"] = SceneManager.GetActiveScene().name,
                ["result"] = result ?? string.Empty,
                ["can_advance"] = snapshot?.CanAdvance ?? false,
                ["has_next_trial"] = snapshot?.HasNextTrial ?? false,
                ["round_completed"] = snapshot?.RoundCompleted ?? false,
                ["completed_boxes"] = snapshot?.CompletedBoxes ?? 0,
                ["total_boxes"] = snapshot?.TotalBoxes ?? 0
            };
        }

        private static int EstimateCompletedTrials(ExperimentRuntimeProtocolSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return 0;
            }

            if (snapshot.CanAdvance && !snapshot.HasNextTrial)
            {
                return snapshot.TotalPlannedTrials;
            }

            return Mathf.Clamp(Mathf.Max(snapshot.CurrentPlanEntryIndex, snapshot.CurrentGlobalRoundIndex), 0, snapshot.TotalPlannedTrials);
        }

        private void RememberCompleted(string conditionId)
        {
            if (!string.IsNullOrWhiteSpace(conditionId) && !_completedConditionIds.Contains(conditionId))
            {
                _completedConditionIds.Add(conditionId);
            }
        }

        private static ParticipantInstruction InstructionFor(string conditionId, string visibleLabel)
        {
            string label = string.IsNullOrWhiteSpace(visibleLabel) ? "Prueba" : visibleLabel;
            return conditionId switch
            {
                ExperimentCompensatedConditionOrder.C00 => new ParticipantInstruction(
                    label,
                    "Tarea manual",
                    "En esta prueba debes trasladar manualmente las cajas a su zona de deposito correspondiente. El robot no intervendra y las ordenes de voz estaran desactivadas."),
                ExperimentCompensatedConditionOrder.C10 => new ParticipantInstruction(
                    label,
                    "Colaboracion con robot autonomo",
                    "En esta prueba el robot colaborara de forma autonoma en el traslado de cajas. Las ordenes de voz estaran desactivadas. Continua realizando la tarea manualmente mientras el robot trabaja."),
                ExperimentCompensatedConditionOrder.C11 => new ParticipantInstruction(
                    label,
                    "Colaboracion con robot y voz",
                    "En esta prueba el robot colaborara de forma autonoma y podras intervenir mediante ordenes de voz. Usa ordenes breves y claras.",
                    "lleva la caja A1 a su zona de dep\u00f3sito",
                    "lleva la caja B2 a su zona de dep\u00f3sito",
                    "coge la caja m\u00e1s cercana",
                    "para",
                    "detente",
                    "retoma la tarea",
                    "contin\u00faa la tarea actual"),
                _ => new ParticipantInstruction(label, "Prueba pendiente", "Espera a que el protocolo active la siguiente prueba.")
            };
        }

        private static string OperationalInstructionFor(string conditionId)
        {
            return conditionId switch
            {
                ExperimentCompensatedConditionOrder.C00 =>
                    "Realiza la tarea de forma manual. Coloca cada caja en su zona de deposito correspondiente.",
                ExperimentCompensatedConditionOrder.C10 =>
                    "Continua trabajando manualmente mientras el robot colabora de forma autonoma. No uses ordenes de voz.",
                ExperimentCompensatedConditionOrder.C11 =>
                    "Puedes intervenir con ordenes de voz breves. Si necesitas detener al robot, usa una orden de parada clara.",
                _ => "Espera a que el protocolo active la siguiente prueba."
            };
        }

        private UserFacingTrialLabel ResolveUserFacingTrialLabel(string conditionId, ExperimentRuntimeProtocolSnapshot snapshot, bool useNextTrial)
        {
            int stableConditionOrderIndex = useNextTrial
                ? snapshot?.NextConditionOrderIndex ?? -1
                : snapshot?.CurrentConditionOrderIndex ?? -1;
            int sequenceIndex;
            if (stableConditionOrderIndex >= 0)
            {
                sequenceIndex = Mathf.Clamp(stableConditionOrderIndex, 0, 2);
            }
            else
            {
                int roundsPerCondition = snapshot != null ? Mathf.Max(1, snapshot.RoundsPerCondition) : 1;
                int planIndex = useNextTrial
                    ? snapshot?.NextPlanEntryIndex ?? 0
                    : snapshot?.CurrentPlanEntryIndex ?? 0;
                sequenceIndex = Mathf.Clamp(planIndex / roundsPerCondition, 0, 2);
            }

            return new UserFacingTrialLabel($"Prueba {sequenceIndex + 1}", sequenceIndex + 1, 3);
        }

        private void LogUserFacingLabelResolved(string conditionId, UserFacingTrialLabel label, ParticipantInstruction instruction)
        {
            TiagoExperimentTelemetry.LogEvent(
                "experiment_runtime_user_facing_label_resolved",
                new Dictionary<string, object>
                {
                    ["internal_condition_id"] = conditionId ?? string.Empty,
                    ["user_visible_trial_label"] = label.Label,
                    ["resolved_condition_order_index"] = label.SequenceIndex - 1,
                    ["resolved_visible_prueba_number"] = label.SequenceIndex,
                    ["sequence_index"] = label.SequenceIndex,
                    ["total_visible_trials"] = label.TotalVisibleTrials,
                    ["user_visible_title"] = instruction.Title,
                    ["hides_internal_condition_code"] = true
                });
        }

        private void LogInstructionCanvasRendered(string conditionId, UserFacingTrialLabel label, ParticipantInstruction instruction)
        {
            bool robotEnabled = RobotEnabledForCondition(conditionId);
            bool voiceEnabled = VoiceEnabledForCondition(conditionId);
            string textKey = InstructionTextKeyFor(label.Label, conditionId);
            string logKey = string.Join("|", label.SequenceIndex, conditionId ?? string.Empty, textKey, instruction.Title, instruction.Body);
            if (string.Equals(_lastInstructionCanvasLogKey, logKey, StringComparison.Ordinal))
            {
                return;
            }

            _lastInstructionCanvasLogKey = logKey;
            bool coherent = IsInstructionCoherent(conditionId, robotEnabled, voiceEnabled, instruction);
            var payload = new Dictionary<string, object>
            {
                ["prueba_index"] = label.SequenceIndex,
                ["instruction_condition_order_index"] = label.SequenceIndex - 1,
                ["instruction_visible_prueba_number"] = label.SequenceIndex,
                ["runtime_test_label"] = label.Label,
                ["instruction_condition_id"] = conditionId ?? string.Empty,
                ["condition_semantics"] = ConditionSemantics(conditionId),
                ["instruction_robot_enabled"] = robotEnabled,
                ["instruction_voice_enabled"] = voiceEnabled,
                ["instruction_text_key"] = textKey,
                ["instruction_clip_id"] = RuntimeProtocolInstructionNarrator.ResolveInstructionClipId(conditionId, label.Label),
                ["instruction_title"] = instruction.Title,
                ["instruction_body_preview"] = Preview(instruction.Body, 140),
                ["instruction_coherent"] = coherent
            };
            TiagoExperimentTelemetry.LogEvent("experiment_instruction_canvas_rendered", payload);
            if (!coherent)
            {
                payload["error_reason"] = "instruction_condition_mismatch";
                TiagoExperimentTelemetry.LogEvent("experiment_instruction_canvas_mismatch", payload);
                Debug.LogError($"[ExperimentRuntimeProtocolUI] instruction_condition_mismatch | prueba={label.Label} condition_id={conditionId} robot_enabled={robotEnabled} voice_enabled={voiceEnabled} title='{instruction.Title}' body='{Preview(instruction.Body, 80)}'", this);
            }
        }

        private static bool RobotEnabledForCondition(string conditionId)
        {
            return string.Equals(conditionId, ExperimentCompensatedConditionOrder.C10, StringComparison.Ordinal) ||
                string.Equals(conditionId, ExperimentCompensatedConditionOrder.C11, StringComparison.Ordinal);
        }

        private static bool VoiceEnabledForCondition(string conditionId)
        {
            return string.Equals(conditionId, ExperimentCompensatedConditionOrder.C11, StringComparison.Ordinal);
        }

        private static string ConditionSemantics(string conditionId)
        {
            return conditionId switch
            {
                ExperimentCompensatedConditionOrder.C00 => "robot_off_voice_off",
                ExperimentCompensatedConditionOrder.C10 => "robot_on_voice_off",
                ExperimentCompensatedConditionOrder.C11 => "robot_on_voice_on",
                _ => "unknown"
            };
        }

        private static string InstructionTextKeyFor(string visibleTrialLabel, string conditionId)
        {
            string ordinal = string.IsNullOrWhiteSpace(visibleTrialLabel)
                ? "prueba"
                : visibleTrialLabel.Trim().ToLowerInvariant().Replace(" ", "_");
            string suffix = conditionId switch
            {
                ExperimentCompensatedConditionOrder.C00 => "manual",
                ExperimentCompensatedConditionOrder.C10 => "robot_autonomous",
                ExperimentCompensatedConditionOrder.C11 => "robot_voice",
                _ => "unknown"
            };
            return $"instruction_{ordinal}_{suffix}";
        }

        private static bool IsInstructionCoherent(string conditionId, bool robotEnabled, bool voiceEnabled, ParticipantInstruction instruction)
        {
            string text = $"{instruction.Subtitle} {instruction.Body}".ToLowerInvariant();
            return conditionId switch
            {
                ExperimentCompensatedConditionOrder.C00 => !robotEnabled && !voiceEnabled && text.Contains("manual") && text.Contains("voz") && text.Contains("desactiv"),
                ExperimentCompensatedConditionOrder.C10 => robotEnabled && !voiceEnabled && text.Contains("robot") && text.Contains("autonom") && text.Contains("voz") && text.Contains("desactiv"),
                ExperimentCompensatedConditionOrder.C11 => robotEnabled && voiceEnabled && text.Contains("robot") && text.Contains("voz"),
                _ => false
            };
        }

        private static string Preview(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            value = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        private static string StatusText(ExperimentRuntimeProtocolSnapshot snapshot)
        {
            if (snapshot.TrialTransitionBusy)
            {
                return "preparando ronda";
            }

            if (snapshot.TrialActive || snapshot.RoundActive)
            {
                return "ronda en ejecucion";
            }

            if (snapshot.CanAdvance)
            {
                return snapshot.HasNextTrial ? "ready_to_advance" : "plan_completado";
            }

            return string.IsNullOrWhiteSpace(snapshot.AdvanceReason) ? "esperando progreso" : snapshot.AdvanceReason;
        }

        private void LogC11Preflight(string conditionId)
        {
            var payload = BuildC11PreflightPayload(conditionId);
            string key = string.Join("|", payload.Values);
            if (!string.Equals(key, _lastLoggedC11PreflightKey, StringComparison.Ordinal))
            {
                _lastLoggedC11PreflightKey = key;
                TiagoExperimentTelemetry.LogEvent("experiment_runtime_c11_preflight", payload);
            }
        }

        private static string FormatC11PreflightDiagnostics(Dictionary<string, object> payload)
        {
            return "Preflight C11\n" +
                $"robot enabled: {payload["preflight_robot_enabled"]}\n" +
                $"voice enabled: {payload["preflight_voice_enabled"]}\n" +
                $"tts backend: {payload["tts_backend_mode"]}\n" +
                $"clips manifest: {payload["audio_clip_manifest_loaded"]} ({payload["audio_clip_manifest_clip_count"]})\n" +
                $"whisper model: {payload["whisper_model_file"]}\n" +
                $"microfono: {payload["microphone_status"]}";
        }

        private Dictionary<string, object> BuildC11PreflightPayload(string conditionId)
        {
            StructuredTtsFeedbackSink tts = FindFirstObjectByType<StructuredTtsFeedbackSink>();
            VoiceRecognitionController voice = FindFirstObjectByType<VoiceRecognitionController>();
            Object whisperManager = ReadPrivate<Object>(voice, "_whisperManager");
            bool diagnosticOverride = ReadPrivate<bool>(voice, "_diagnosticOverrideWhisperModel");
            ASRModelSize requestedModel = ReadPrivate<ASRModelSize>(voice, "_diagnosticModelSize");
            WhisperModelRuntimeInfo whisper = WhisperModelConfiguration.ResolveRuntimeInfo(whisperManager, requestedModel, diagnosticOverride);
            TextAsset manifestAsset = Resources.Load<TextAsset>(AudioClipTtsSpeechBackend.DefaultManifestResourcePath);
            RobotVoiceClipManifest manifest = manifestAsset != null ? JsonUtility.FromJson<RobotVoiceClipManifest>(manifestAsset.text) : null;
            string microphoneStatus = BuildMicrophoneStatus();
            bool baseAvailable = WhisperModelConfiguration.IsModelAvailable(ASRModelSize.Base);

            return new Dictionary<string, object>
            {
                ["preflight_condition_id"] = conditionId,
                ["preflight_condition_name"] = "Robot ON + Voice ON",
                ["preflight_robot_enabled"] = true,
                ["preflight_voice_enabled"] = true,
                ["tts_backend_mode"] = tts != null ? tts.BackendMode : "tts_sink_missing",
                ["tts_backend_available"] = tts != null && tts.IsBackendAvailable,
                ["tts_backend_unavailable_reason"] = tts != null ? tts.BackendUnavailableReason : "tts_sink_missing",
                ["android_audio_clip_expected"] = IsAndroidRuntime(),
                ["audio_clip_manifest_loaded"] = manifestAsset != null,
                ["audio_clip_manifest_clip_count"] = manifest?.clips != null ? manifest.clips.Length : 0,
                ["whisper_requested_model"] = requestedModel.ToString(),
                ["whisper_effective_model"] = whisper.EffectiveModelSize.ToString(),
                ["whisper_model_file"] = whisper.EffectiveModelFileName,
                ["whisper_model_available_at_start"] = whisper.ModelAvailableAtStart,
                ["whisper_fallback_used"] = whisper.ModelFallbackUsed,
                ["whisper_base_model_available"] = baseAvailable,
                ["whisper_base_missing_warning"] = requestedModel == ASRModelSize.Base && !baseAvailable,
                ["whisper_requested_base_but_effective_tiny_warning"] = requestedModel == ASRModelSize.Base && whisper.EffectiveModelSize == ASRModelSize.Tiny,
                ["microphone_status"] = microphoneStatus
            };
        }

        private static T ReadPrivate<T>(object target, string fieldName)
        {
            if (target == null)
            {
                return default;
            }

            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                return default;
            }

            object value = field.GetValue(target);
            return value is T typed ? typed : default;
        }

        private static string BuildMicrophoneStatus()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            bool permission = UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone);
            return permission ? "android_permission_granted" : "android_permission_missing_or_pending";
#else
            int count = Microphone.devices != null ? Microphone.devices.Length : 0;
            return count > 0 ? $"devices:{count}" : "no_devices";
#endif
        }

        private static bool IsAndroidRuntime()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return true;
#else
            return false;
#endif
        }

        private static bool TryPulseOrSetBoolMember(object target, bool value, params string[] memberNames)
        {
            if (target == null || memberNames == null)
            {
                return false;
            }

            Type type = target.GetType();
            foreach (string memberName in memberNames)
            {
                PropertyInfo property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.PropertyType == typeof(bool) && property.CanWrite)
                {
                    object current = property.GetValue(target);
                    if (current is bool currentValue && currentValue == value)
                    {
                        property.SetValue(target, !value);
                    }

                    property.SetValue(target, value);
                    return true;
                }

                FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType == typeof(bool))
                {
                    object current = field.GetValue(target);
                    if (current is bool currentValue && currentValue == value)
                    {
                        field.SetValue(target, !value);
                    }

                    field.SetValue(target, value);
                    return true;
                }
            }

            return false;
        }

        private void ClearContent()
        {
            for (int i = _content.childCount - 1; i >= 0; i--)
            {
                Destroy(_content.GetChild(i).gameObject);
            }
        }

        private void AddTitle(string text)
        {
            TextMeshProUGUI title = AddText(text, 50, FontStyles.Bold, 76f);
            title.color = Color.white;
        }

        private void AddBody(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            AddText(text, 30, FontStyles.Normal, 58f);
        }

        private void AddInstructionBody(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            TextMeshProUGUI body = AddText(text, 34, FontStyles.Normal, 118f);
            ExperimentCanvasTypography.ApplyLegacyLineSpacing(body, 1.08f);
        }

        private void AddQuestionnaireCodeBlock(string code)
        {
            TextMeshProUGUI codeText = AddText(string.IsNullOrWhiteSpace(code) ? "CODIGO NO DISPONIBLE" : code.Trim(), 72, FontStyles.Bold, 132f);
            codeText.alignment = TextAlignmentOptions.Center;
            codeText.color = new Color(1f, 0.92f, 0.50f, 1f);
        }

        private void AddExamples(IReadOnlyList<string> examples)
        {
            if (examples == null || examples.Count == 0)
            {
                return;
            }

            TextMeshProUGUI body = AddText("- " + string.Join("\n- ", examples), 30, FontStyles.Normal, 214f);
            ExperimentCanvasTypography.ApplyLegacyLineSpacing(body, 1.06f);
        }

        private void AddSectionLabel(string text)
        {
            TextMeshProUGUI label = AddText(text, 28, FontStyles.Bold, 44f);
            label.color = new Color(1f, 0.78f, 0.62f, 1f);
        }

        private void AddSpacer(float height)
        {
            RectTransform spacer = CreateRect("Spacer", _content);
            spacer.gameObject.AddComponent<LayoutElement>().minHeight = height;
        }

        private void AddFrontSpacer(float height)
        {
            if (_globalInstructionsFrontContent == null)
            {
                return;
            }

            RectTransform spacer = CreateRect("Spacer", _globalInstructionsFrontContent);
            spacer.gameObject.AddComponent<LayoutElement>().minHeight = height;
        }

        private TextMeshProUGUI AddText(string text, int fontSize, FontStyles style, float minHeight = 0f)
        {
            var item = new GameObject("Text");
            item.transform.SetParent(_content, false);
            TextMeshProUGUI label = item.AddComponent<TextMeshProUGUI>();
            ExperimentCanvasTypography.Configure(label);
            label.text = text ?? string.Empty;
            label.fontSize = fontSize;
            label.fontStyle = style;
            label.color = new Color(0.92f, 0.94f, 0.95f, 1f);
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Overflow;
            label.raycastTarget = false;
            LayoutElement layout = item.AddComponent<LayoutElement>();
            layout.minHeight = Mathf.Max(42f, minHeight > 0f ? minHeight : fontSize + 18f);
            return label;
        }

        private TextMeshProUGUI AddFrontText(string text, int fontSize, FontStyles style, float minHeight, TextAlignmentOptions alignment, Color color)
        {
            if (_globalInstructionsFrontContent == null)
            {
                return null;
            }

            TextMeshProUGUI label = CreateTextInParent("Text", _globalInstructionsFrontContent, text, fontSize, style, alignment);
            label.color = color;
            LayoutElement layout = label.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = Mathf.Max(42f, minHeight > 0f ? minHeight : fontSize + 18f);
            return label;
        }

        private TextMeshProUGUI CreateTextInParent(string name, Transform parent, string text, int fontSize, FontStyles style, TextAlignmentOptions alignment)
        {
            RectTransform rect = CreateRect(name, parent);
            TextMeshProUGUI label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            ExperimentCanvasTypography.Configure(label);
            label.text = text ?? string.Empty;
            label.fontSize = fontSize;
            label.fontStyle = style;
            label.color = new Color(0.92f, 0.94f, 0.95f, 1f);
            label.alignment = alignment;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Overflow;
            label.raycastTarget = false;
            return label;
        }

        private TextMeshProUGUI CreateTmpTextInParent(string name, Transform parent, string text, int fontSize, float minHeight)
        {
            RectTransform rect = CreateRect(name, parent);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(0f, minHeight);
            TextMeshProUGUI label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            ExperimentCanvasTypography.Configure(label);
            label.text = text ?? string.Empty;
            label.fontSize = fontSize;
            label.fontStyle = TMPro.FontStyles.Normal;
            label.color = new Color(0.92f, 0.94f, 0.95f, 1f);
            label.alignment = TextAlignmentOptions.TopLeft;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.raycastTarget = false;
            return label;
        }

        private TMP_InputField AddInput(string value, Action<string> onChanged)
        {
            GameObject inputObject = CreateInputFrame("ParticipantInput", _content);
            TMP_InputField input = inputObject.AddComponent<TMP_InputField>();
            TextMeshProUGUI text = CreateInputText("Text", inputObject.transform, value);
            TextMeshProUGUI placeholder = CreateInputText("Placeholder", inputObject.transform, "1");
            placeholder.color = new Color(0.65f, 0.68f, 0.70f, 1f);
            input.textComponent = text;
            input.placeholder = placeholder;
            input.text = value;
            input.contentType = TMP_InputField.ContentType.IntegerNumber;
            input.onValueChanged.AddListener(newValue => onChanged?.Invoke(newValue));
            inputObject.AddComponent<LayoutElement>().minHeight = 62f;
            return input;
        }

        private TextMeshProUGUI CreateInputText(string name, Transform parent, string value)
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
            text.alignment = TextAlignmentOptions.MidlineLeft;
            return text;
        }

        private GameObject CreateInputFrame(string name, Transform parent)
        {
            RectTransform rect = CreateRect(name, parent);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = new Color(0.16f, 0.18f, 0.20f, 1f);
            return rect.gameObject;
        }

        private void AddHorizontalButtons(params (string label, Action action, ExperimentButtonRole role, bool enabled)[] buttons)
        {
            RectTransform row = CreateRect("ButtonRow", _content);
            HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 12f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            row.gameObject.AddComponent<LayoutElement>().minHeight = 66f;
            foreach ((string label, Action action, ExperimentButtonRole role, bool enabled) in buttons)
            {
                AddButton(label, action, role, enabled, row);
            }
        }

        private void AddButton(string label, Action action, ExperimentButtonRole role, bool enabled)
        {
            AddButton(label, action, role, enabled, _content);
        }

        private void AddButton(string label, Action action, ExperimentButtonRole role, bool enabled, Transform parent)
        {
            RectTransform rect = CreateRect("Button_" + label, parent);
            Image image = rect.gameObject.AddComponent<Image>();
            Button button = rect.gameObject.AddComponent<Button>();
            ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(button, image, action, role, enabled);
            TextMeshProUGUI text = CreateInputText("Label", rect, label);
            text.alignment = TextAlignmentOptions.Center;
            text.fontStyle = FontStyles.Bold;
            rect.gameObject.AddComponent<LayoutElement>().minHeight = 66f;
        }

        private Button FindButtonByLabel(string label)
        {
            if (_canvas == null || string.IsNullOrWhiteSpace(label))
            {
                return null;
            }

            string expectedName = "Button_" + label;
            foreach (Button button in _canvas.GetComponentsInChildren<Button>(false))
            {
                if (button != null && string.Equals(button.gameObject.name, expectedName, StringComparison.Ordinal))
                {
                    return button;
                }
            }

            return null;
        }

        private string ReadRobotModeForRestartLog()
        {
            return _orchestrator != null ? _orchestrator.RestartDiagnosticRobotMode : string.Empty;
        }

        private string ReadRobotCurrentTaskForRestartLog()
        {
            return _orchestrator != null ? _orchestrator.RestartDiagnosticCurrentTask : string.Empty;
        }

        private bool ReadRobotPendingTaskForRestartLog()
        {
            return _orchestrator != null && _orchestrator.RestartDiagnosticPendingTask;
        }

        private string ReadHeldObjectForRestartLog()
        {
            return _orchestrator != null ? _orchestrator.RestartDiagnosticHeldObjectId : string.Empty;
        }

        private static void SetUiLayerRecursively(GameObject root)
        {
            if (root == null)
            {
                return;
            }

            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer < 0)
            {
                return;
            }

            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                child.gameObject.layer = uiLayer;
            }
        }

        private static RectTransform CreateRect(string name, Transform parent)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent, false);
            return child.AddComponent<RectTransform>();
        }

        private static string GetTransformPath(Transform target)
        {
            if (target == null)
            {
                return string.Empty;
            }

            string path = target.name;
            Transform current = target.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        private readonly struct ParticipantInstruction
        {
            public ParticipantInstruction(string title, string subtitle, string body, params string[] examples)
            {
                Title = title ?? string.Empty;
                Subtitle = subtitle ?? string.Empty;
                Body = body ?? string.Empty;
                Examples = examples ?? Array.Empty<string>();
            }

            public string Title { get; }
            public string Subtitle { get; }
            public string Body { get; }
            public string[] Examples { get; }
        }

        private readonly struct UserFacingTrialLabel
        {
            public UserFacingTrialLabel(string label, int sequenceIndex, int totalVisibleTrials)
            {
                Label = label ?? string.Empty;
                SequenceIndex = sequenceIndex;
                TotalVisibleTrials = totalVisibleTrials;
            }

            public string Label { get; }
            public int SequenceIndex { get; }
            public int TotalVisibleTrials { get; }
        }
    }
}
