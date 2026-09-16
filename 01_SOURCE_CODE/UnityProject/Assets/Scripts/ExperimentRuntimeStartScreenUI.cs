using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Unity.XR.CoreUtils;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class ExperimentRuntimeStartScreenUI : MonoBehaviour, IExperimentStartSceneRealignmentUiGate
    {
        private const string StartSceneName = "experiment_start_scene";
        private const string FinalSceneName = "final_scene";
        private const string RootName = "StartScreenUI";
        private const string SpawnPointName = "StartScreenSpawnPoint";
        private const float CanvasScale = 0.0018f;
        private const float RuntimeCanvasDynamicPixelsPerUnit = 14f;
        private const string HistoryButtonLabel = "Historial de IDs";
        private const string HistoryButtonObjectName = "Button_HistorialSesiones";
        private const string HistoryBackButtonLabel = "Volver";
        private const string HistoryBackButtonObjectName = "Button_VolverHistorial";
        private const string HistoryPreviousButtonLabel = "Mas recientes";
        private const string HistoryPreviousButtonObjectName = "Button_HistorialAnterior";
        private const string HistoryNextButtonLabel = "Mas antiguas";
        private const string HistoryNextButtonObjectName = "Button_HistorialSiguiente";
        private const int MaxHistoryEntriesToLoad = 50;
        private const int HistoryEntriesPerPage = ExperimentHistoryTableLayout.EntriesPerPage;
        private const float StartScreenPanelWidth = 920f;
        private const float StartScreenPanelHeight = 1080f;
        private const float PanelSafeHorizontalPadding = 48f;
        private const float PanelSafeVerticalPadding = 44f;
        private const float StartScreenLayoutSpacing = 22f;
        private const float SavedExitLayoutSpacing = 20f;
        private const int SavedExitPromptBodyFontSize = 29;
        private const float SavedExitPromptLineHeight = 34f;
        private const float SavedExitPromptLongBodyHeight = 96f;
        private const float ButtonAreaHeight = 86f;
        private const float StartScreenButtonStackSpacing = 26f;
        private const float SavedExitButtonStackSpacing = 24f;
        private const float ButtonStackVerticalPadding = 4f;
        private const float HistoryViewportHeight = ExperimentHistoryTableLayout.ViewportHeight;

        [SerializeField] private Vector2 _panelSize = new(StartScreenPanelWidth, StartScreenPanelHeight);
        [SerializeField] private float _distanceFromCamera = 2.1f;
        [SerializeField] private float _verticalOffset = -0.9f;
        [SerializeField] private float _canvasPitchDegrees = -8f;

        private Canvas _canvas;
        private RectTransform _content;
        private ExperimentRuntimeUiRayInteractorBootstrap _rayBootstrap;
        private Transform _spawnPoint;
        private XROrigin _xrOrigin;
        private Camera _xrCamera;
        private ExperimentStartSceneXrPoseAligner _startScenePoseAligner;
        private bool _protocolStarted;
        private bool _locomotionLocked;
        private bool _fallbackSpawnUsed;
        private bool _validScene;
        private bool _showingHistory;
        private Transform _historyTable;
        private RectTransform _activeButtonContainer;
        private string _activeScreenMode = string.Empty;
        private Coroutine _openHistoryCoroutine;
        private Coroutine _backHistoryCoroutine;
        private Coroutine _startSessionCoroutine;
        private int _buttonConfigurationSequence;
        private int _historyPageIndex;
        private bool _startSessionInProgress;
        private bool _savedExitPromptDismissedForNewAttempt;
        private bool _showingGlobalInstructions;
        private ExperimentGlobalInstructionsState _globalInstructionsState;
        private RectTransform _globalInstructionsRoot;
        private TextMeshProUGUI _globalInstructionsTitleText;
        private TextMeshProUGUI _globalInstructionsPageText;
        private RectTransform _globalInstructionsBulletContainer;
        private Button _globalInstructionsPreviousButton;
        private Button _globalInstructionsNextButton;
        private Button _globalInstructionsCompleteButton;
        private TextMeshProUGUI _globalInstructionsPreviousButtonText;
        private TextMeshProUGUI _globalInstructionsNextButtonText;
        private TextMeshProUGUI _globalInstructionsCompleteButtonText;
        private int _globalInstructionsLastNavFrame = -1;
        private int _lastNarratedGlobalInstructionPage = -1;
        private ExperimentGlobalInstructionsAudioPlayer _globalInstructionsAudioPlayer;
        private ExperimentSessionIdHistoryEntry _pendingSavedExitEntry;
        private ExperimentPauseFrontCanvasPointerBridge _savedExitPromptPointerBridge;
        private readonly List<Button> _savedExitPromptButtons = new();
        private readonly List<BehaviourState> _locomotionStates = new();
        private readonly List<BehaviourState> _recenterMarkerStates = new();
        private RecenterUiGateSnapshot _recenterUiGateSnapshot;
        private bool _recenterUiGateActive;

        public bool IsRecenterUiGateActive => _recenterUiGateActive;
        public int RecenterCanvasRepositionCount { get; private set; }
        public int RecenterUiGateReleaseCount { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            bool supportedScene = IsSupportedSceneForStartScreen(sceneName);
            Transform parent = supportedScene ? FindExperimentParent() : null;
            if (!supportedScene)
            {
                LogBootstrapSceneCheck(sceneName, supportedScene, created: false, parent);
                return;
            }

            if (FindFirstObjectByType<ExperimentRuntimeStartScreenUI>(FindObjectsInactive.Include) != null)
            {
                LogBootstrapSceneCheck(sceneName, supportedScene, created: false, parent);
                return;
            }

            var host = new GameObject(RootName);
            if (parent != null)
            {
                host.transform.SetParent(parent, false);
            }

            host.AddComponent<ExperimentRuntimeStartScreenUI>();
            LogBootstrapSceneCheck(sceneName, supportedScene, created: true, parent);
            TiagoExperimentTelemetry.LogEvent(
                "p45c17_start_screen_bootstrap_created",
                new Dictionary<string, object>
                {
                    ["scene_name"] = sceneName,
                    ["supported_scene"] = supportedScene,
                    ["created"] = true,
                    ["parent_path"] = parent != null ? GetPath(parent) : string.Empty
                });
        }

        private void Awake()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            _validScene = IsSupportedSceneForStartScreen(sceneName);
            if (!_validScene)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "p45c17_start_screen_destroyed_outside_start_scene",
                    new Dictionary<string, object>
                    {
                        ["scene_name"] = sceneName,
                        ["object_path"] = GetPath(transform),
                        ["reason"] = "start_screen_only_allowed_in_start_scene"
                    });
                Destroy(gameObject);
                return;
            }

            BuildCanvas();
            SetVisible(false);
            ResolveInitialSpawn();
            ResolveStartScenePoseAligner();
        }

        private IEnumerator Start()
        {
            if (!_validScene)
            {
                yield break;
            }

            EnsureEventSystem();
            Render();
            LockLocomotion();
            HideProtocolPanel();
            yield return WaitForStartSceneAlignment("initial_start");
            PositionAtDeterministicStartAnchor();
            _rayBootstrap = ExperimentRuntimeUiRayInteractorBootstrap.EnsureAttached(gameObject, _canvas);
            SetVisible(true);
            StartCoroutine(HideProtocolPanelAfterStartup());
        }

        private void OnDestroy()
        {
            if (_startScenePoseAligner != null)
            {
                _startScenePoseAligner.UnregisterRealignmentUiGate(this);
            }

            if (_recenterUiGateActive)
            {
                RestoreRecenterUiGateSnapshot("start_ui_destroyed");
            }
        }

        public void ShowAfterProtocolFinished()
        {
            if (!_validScene)
            {
                return;
            }

            _protocolStarted = false;
            _showingHistory = false;
            _showingGlobalInstructions = false;
            _globalInstructionsState = null;
            _historyPageIndex = 0;
            _startSessionInProgress = false;
            _startSessionCoroutine = null;
            gameObject.SetActive(true);
            SetVisible(false);
            EnsureEventSystem();
            Render();
            LockLocomotion();
            HideProtocolPanel();
            StartCoroutine(ShowAfterProtocolFinishedAligned());
        }

        private IEnumerator ShowAfterProtocolFinishedAligned()
        {
            yield return WaitForStartSceneAlignment("return_after_protocol");
            PositionAtDeterministicStartAnchor();
            _rayBootstrap = ExperimentRuntimeUiRayInteractorBootstrap.EnsureAttached(gameObject, _canvas);
            SetVisible(true);
            TiagoExperimentTelemetry.LogEvent(
                "experiment_runtime_returned_to_start_screen",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["returned_to_initial_spawn"] = true,
                    ["xr_origin_position"] = _xrOrigin != null ? _xrOrigin.transform.position : Vector3.zero,
                    ["start_screen_path"] = GetPath(transform),
                    ["start_screen_position"] = transform.position,
                    ["locomotion_locked"] = true,
                    ["runtime_protocol_ui_visible"] = false
                });
        }

        private IEnumerator WaitForStartSceneAlignment(string source)
        {
            ResolveStartScenePoseAligner();
            if (_startScenePoseAligner == null)
            {
                Debug.LogWarning(
                    $"[ExperimentRuntimeStartScreenUI] start_scene_xr_alignment_failed | reason=aligner_missing source={source} fallback=release_ui_fail_soft");
                yield break;
            }

            if (!_startScenePoseAligner.IsAlignmentRunning)
            {
                _startScenePoseAligner.BeginAlignment();
            }

            while ((_startScenePoseAligner.IsAlignmentRunning && !_startScenePoseAligner.IsAlignmentComplete) ||
                   _startScenePoseAligner.IsRealignmentPendingOrRunning)
            {
                yield return null;
            }

            Debug.Log(
                $"[ExperimentRuntimeStartScreenUI] start_scene_ui_alignment_gate_released | source={source} " +
                $"succeeded={_startScenePoseAligner.AlignmentSucceeded} reason={_startScenePoseAligner.FailureReason} " +
                $"attempts={_startScenePoseAligner.AlignmentAttemptCount} position_error_m={_startScenePoseAligner.FinalPositionErrorMeters:F6} " +
                $"yaw_error_deg={_startScenePoseAligner.FinalYawErrorDegrees:F4}");
        }

        private void ResolveStartScenePoseAligner()
        {
            _startScenePoseAligner ??= FindFirstObjectByType<ExperimentStartSceneXrPoseAligner>(FindObjectsInactive.Include);
            _startScenePoseAligner?.RegisterRealignmentUiGate(this);
        }

        private void ResolveInitialSpawn()
        {
            _xrOrigin = FindFirstObjectByType<XROrigin>();
            _xrCamera = _xrOrigin != null && _xrOrigin.Camera != null
                ? _xrOrigin.Camera
                : Camera.main ?? FindFirstObjectByType<Camera>();

            Transform experiment = FindExperimentParent();
            _spawnPoint = experiment != null ? experiment.Find(SpawnPointName) : null;
            if (_spawnPoint == null)
            {
                GameObject existing = GameObject.Find(SpawnPointName);
                _spawnPoint = existing != null ? existing.transform : null;
            }

            bool anchorFound = _spawnPoint != null;
            _fallbackSpawnUsed = !anchorFound;
            if (_spawnPoint == null)
            {
                var spawnObject = new GameObject(SpawnPointName);
                if (experiment != null)
                {
                    spawnObject.transform.SetParent(experiment, true);
                }

                Transform source = _xrCamera != null ? _xrCamera.transform : (_xrOrigin != null ? _xrOrigin.transform : transform);
                Vector3 forward = source.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.001f)
                {
                    forward = Vector3.forward;
                }

                forward.Normalize();
                spawnObject.transform.SetPositionAndRotation(source.position, Quaternion.LookRotation(forward, Vector3.up));
                _spawnPoint = spawnObject.transform;
            }

            TiagoExperimentTelemetry.LogEvent(
                "experiment_start_screen_spawn_resolved",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["anchor_found"] = anchorFound,
                    ["anchor_path"] = _spawnPoint != null ? GetPath(_spawnPoint) : string.Empty,
                    ["position"] = _spawnPoint != null ? _spawnPoint.position : Vector3.zero,
                    ["rotation"] = _spawnPoint != null ? _spawnPoint.eulerAngles : Vector3.zero,
                    ["fallback_used"] = _fallbackSpawnUsed
                });
        }

        private IEnumerator HideProtocolPanelAfterStartup()
        {
            yield return null;
            if (!_protocolStarted)
            {
                HideProtocolPanel();
            }

            yield return new WaitForSecondsRealtime(0.5f);
            if (!_protocolStarted)
            {
                HideProtocolPanel();
            }
        }

        private void BuildCanvas()
        {
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.sortingOrder = 120;

            RectTransform rect = _canvas.GetComponent<RectTransform>();
            _panelSize = new Vector2(StartScreenPanelWidth, StartScreenPanelHeight);
            rect.sizeDelta = _panelSize;
            rect.localScale = Vector3.one * CanvasScale;

            CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = RuntimeCanvasDynamicPixelsPerUnit;
            scaler.referencePixelsPerUnit = 100f;
            Debug.Log($"[P46J-02] start_screen_typography_configured | panel_size={_panelSize} canvas_scale={CanvasScale} dynamic_pixels_per_unit={scaler.dynamicPixelsPerUnit} reference_pixels_per_unit={scaler.referencePixelsPerUnit} title_font=48 body_font=32 button_font=34 text_mode=TextMeshProUGUI shared_material={ExperimentCanvasTypography.MaterialResourcePath} effects=none");
            gameObject.AddComponent<GraphicRaycaster>();
            EnsureTrackedDeviceGraphicRaycaster(gameObject);

            var background = new GameObject("Panel");
            background.transform.SetParent(transform, false);
            Image image = background.AddComponent<Image>();
            image.color = new Color(0.055f, 0.065f, 0.075f, 0.94f);
            image.raycastTarget = false;
            RectTransform backgroundRect = image.rectTransform;
            backgroundRect.anchorMin = Vector2.zero;
            backgroundRect.anchorMax = Vector2.one;
            backgroundRect.offsetMin = Vector2.zero;
            backgroundRect.offsetMax = Vector2.zero;

            _content = CreateRect("Content", background.transform);
            _content.anchorMin = Vector2.zero;
            _content.anchorMax = Vector2.one;
            _content.offsetMin = new Vector2(PanelSafeHorizontalPadding, PanelSafeVerticalPadding);
            _content.offsetMax = new Vector2(-PanelSafeHorizontalPadding, -PanelSafeVerticalPadding);
            VerticalLayoutGroup layout = _content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = StartScreenLayoutSpacing;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            LogP46HCanvasLayoutConfigured("build_canvas");
        }

        private void Render()
        {
            bool hasPendingSavedExit = false;
            ExperimentSessionIdHistoryEntry pending = null;
            if (!_showingHistory)
            {
                hasPendingSavedExit = ShouldShowSavedExitPrompt(out pending);
            }

            ExperimentStartScreenRenderMode mode = ExperimentStartScreenFlowPriority.Resolve(
                _showingHistory,
                hasPendingSavedExit,
                _showingGlobalInstructions);

            if (mode == ExperimentStartScreenRenderMode.History)
            {
                ClearContent();
                SetContentSpacing(StartScreenLayoutSpacing);
                DisableSavedExitPromptPointerBridge("render_history");
                RenderHistory("render_requested");
                LogP46HCanvasLayoutConfigured("render_history");
                LogP46H04StartScreenGeometry("history", "render_history");
                return;
            }

            if (mode == ExperimentStartScreenRenderMode.SavedExitPrompt)
            {
                ClearContent();
                RenderSavedExitPrompt(pending);
                LogP46HCanvasLayoutConfigured("render_saved_exit_prompt");
                return;
            }

            if (mode == ExperimentStartScreenRenderMode.GlobalInstructions)
            {
                if (_globalInstructionsRoot == null)
                {
                    ClearContent();
                    SetContentSpacing(StartScreenLayoutSpacing);
                }

                RenderGlobalInstructions();
                return;
            }

            ClearContent();
            DisableSavedExitPromptPointerBridge("render_default_start");
            SetContentSpacing(StartScreenLayoutSpacing);
            AddTitle("Estudio de interacción en realidad virtual");
            AddBody("Bienvenido/a. Realizarás una tarea de traslado de cajas en un entorno de realidad virtual.", 116f);
            AddSpacer(10f);
            AddBody("El identificador de sesión y el código del cuestionario se asignarán automáticamente al comenzar.", 98f);
            AddSpacer(8f);
            BeginButtonStack("StartButtonStack", 3, StartScreenButtonStackSpacing);
            AddButton("Comenzar sesi\u00f3n experimental", StartPressed, ExperimentButtonRole.Primary, true);
            AddHistoryButton();
            AddButton("Cerrar aplicaci\u00f3n", ExitPressed, ExperimentButtonRole.Destructive, true);
            LogP46HCanvasLayoutConfigured("render_default_start");
            LogP46H04StartScreenGeometry("start", "render_default_start");
        }

        private bool ShouldShowSavedExitPrompt(out ExperimentSessionIdHistoryEntry pending)
        {
            pending = null;
            if (_savedExitPromptDismissedForNewAttempt || _protocolStarted)
            {
                return false;
            }

            IReadOnlyList<ExperimentSessionIdHistoryEntry> entries = ExperimentSessionIdHistoryStore.LoadRecent(MaxHistoryEntriesToLoad);
            bool found = ExperimentSavedExitResumePromptState.TryFindPendingSession(entries, out pending);
            _pendingSavedExitEntry = pending;
            if (found)
            {
                Debug.Log($"[P46D-PAUSE] saved_exit_detected_on_start | scene={SceneManager.GetActiveScene().name} session_id={pending.session_id} questionnaire_code={pending.questionnaire_code} status={pending.status} history_path={ExperimentSessionIdHistoryStore.DefaultPath}");
                return true;
            }

            Debug.Log($"[P46D-PAUSE] saved_exit_no_pending_session | scene={SceneManager.GetActiveScene().name} history_path={ExperimentSessionIdHistoryStore.DefaultPath}");
            return false;
        }

        private void RenderSavedExitPrompt(ExperimentSessionIdHistoryEntry pending)
        {
            _savedExitPromptButtons.Clear();
            SetContentSpacing(SavedExitLayoutSpacing);
            string displayStatus = ExperimentSavedExitResumePromptState.ToParticipantDisplayStatus(pending?.status);
            string checkpointDescription = ExperimentSavedExitResumePromptState.BuildCheckpointDescription(pending);
            string resumeLabel = ExperimentSavedExitResumePromptState.BuildResumeButtonLabel(pending);
            bool checkpointValid = ExperimentSavedExitResumePromptState.HasValidCheckpoint(pending);
            AddTitle("Sesi\u00f3n guardada/interrumpida");
            AddSavedExitPromptBody("Hay una sesi\u00f3n guardada sin finalizar.", 40f);
            AddSavedExitPromptBody($"Identificador de sesi\u00f3n: {pending?.session_id ?? string.Empty}", SavedExitPromptLineHeight);
            AddSavedExitPromptBody($"C\u00f3digo de cuestionario: {pending?.questionnaire_code ?? string.Empty}", SavedExitPromptLineHeight);
            AddSavedExitPromptBody($"estado: {displayStatus}", SavedExitPromptLineHeight);
            AddSavedExitPromptBody(checkpointValid ? $"\u00daltimo punto guardado: {checkpointDescription}" : "\u00daltimo punto guardado: no disponible", 42f);
            AddSpacer(2f);
            AddSavedExitPromptBody(checkpointValid
                ? $"Puedes continuar desde el inicio de {checkpointDescription} o empezar un nuevo intento desde Prueba 1."
                : "No hay punto de recuperaci\u00f3n seguro para continuar. Puedes empezar un nuevo intento desde Prueba 1 o revisar el historial.", SavedExitPromptLongBodyHeight);
            BeginButtonStack("SavedExitButtonStack", checkpointValid ? 4 : 3, SavedExitButtonStackSpacing);
            if (checkpointValid)
            {
                AddSavedExitPromptButton(resumeLabel, ResumeSavedExitFromCheckpointPressed, ExperimentButtonRole.Primary);
            }
            else
            {
                Debug.LogWarning($"[P46D-PAUSE] saved_exit_resume_checkpoint_invalid | scene={SceneManager.GetActiveScene().name} session_id={pending?.session_id ?? string.Empty} questionnaire_code={pending?.questionnaire_code ?? string.Empty} status={pending?.status ?? string.Empty} saved_condition_id={pending?.saved_condition_id ?? string.Empty} saved_visible_prueba={pending?.saved_visible_prueba ?? 0} reason=missing_saved_condition_id");
            }

            AddSavedExitPromptButton("Empezar nuevo intento", StartNewAttemptAfterSavedExitPressed, ExperimentButtonRole.Warning);
            AddSavedExitPromptButton("Historial", ShowHistoryPressed, ExperimentButtonRole.Secondary);
            AddSavedExitPromptButton("Salir", ExitPressed, ExperimentButtonRole.Destructive);
            RefreshSavedExitPromptRayTargets();
            EnableSavedExitPromptPointerBridge();
            Debug.Log($"[P46D-PAUSE] saved_exit_resume_prompt_status_display_mapped | scene={SceneManager.GetActiveScene().name} internal_status={pending?.status ?? string.Empty} display_status={displayStatus} session_id={pending?.session_id ?? string.Empty} questionnaire_code={pending?.questionnaire_code ?? string.Empty}");
            Debug.Log($"[P46D-PAUSE] saved_exit_resume_prompt_shown | scene={SceneManager.GetActiveScene().name} session_id={pending?.session_id ?? string.Empty} questionnaire_code={pending?.questionnaire_code ?? string.Empty} status={pending?.status ?? string.Empty} saved_condition_id={pending?.saved_condition_id ?? string.Empty} saved_visible_prueba={pending?.saved_visible_prueba ?? 0} saved_round_index={pending?.saved_round_index ?? 0} resume_policy={pending?.resume_policy ?? string.Empty} checkpoint_valid={checkpointValid} start_screen_path={GetPath(transform)}");
            LogP46H04StartScreenGeometry("saved_exit", "render_saved_exit_prompt");
        }

        private void RenderGlobalInstructions()
        {
            Debug.LogWarning($"[P46G-INSTRUCTIONS] start_screen_global_instructions_render_blocked | scene={SceneManager.GetActiveScene().name} start_screen_path={GetPath(transform)} reason=runtime_protocol_pre_session_owner");
            _showingGlobalInstructions = false;
            _globalInstructionsAudioPlayer?.StopAudio("start_screen_global_instructions_blocked");
            if (_globalInstructionsRoot != null)
            {
                Destroy(_globalInstructionsRoot.gameObject);
            }

            ResetGlobalInstructionViewReferences();
            Render();
        }

        private void BuildGlobalInstructionsView()
        {
            _globalInstructionsRoot = CreateRect("DeprecatedStartScreenGlobalInstructions", transform);
            _globalInstructionsRoot.anchorMin = new Vector2(0.5f, 0.5f);
            _globalInstructionsRoot.anchorMax = new Vector2(0.5f, 0.5f);
            _globalInstructionsRoot.pivot = new Vector2(0.5f, 0.5f);
            _globalInstructionsRoot.anchoredPosition = Vector2.zero;
            _globalInstructionsRoot.sizeDelta = new Vector2(860f, 740f);
            _globalInstructionsRoot.SetAsLastSibling();
            Image modalImage = _globalInstructionsRoot.gameObject.AddComponent<Image>();
            modalImage.color = new Color(0.075f, 0.088f, 0.100f, 0.98f);
            modalImage.raycastTarget = true;

            VerticalLayoutGroup modalGroup = _globalInstructionsRoot.gameObject.AddComponent<VerticalLayoutGroup>();
            modalGroup.padding = new RectOffset(46, 46, 34, 34);
            modalGroup.spacing = 14f;
            modalGroup.childAlignment = TextAnchor.UpperLeft;
            modalGroup.childControlWidth = true;
            modalGroup.childControlHeight = true;
            modalGroup.childForceExpandWidth = true;
            modalGroup.childForceExpandHeight = false;

            _globalInstructionsTitleText = CreateText(
                "GlobalInstructionsTitle",
                _globalInstructionsRoot,
                string.Empty,
                46,
                FontStyles.Bold,
                58f,
                TextAlignmentOptions.MidlineLeft);
            _globalInstructionsTitleText.color = Color.white;

            _globalInstructionsPageText = CreateText(
                "GlobalInstructionsPage",
                _globalInstructionsRoot,
                string.Empty,
                25,
                FontStyles.Bold,
                34f,
                TextAlignmentOptions.MidlineLeft);
            _globalInstructionsPageText.color = new Color(0.72f, 0.84f, 0.92f, 1f);

            _globalInstructionsBulletContainer = CreateRect("GlobalInstructionsBullets", _globalInstructionsRoot);
            _globalInstructionsBulletContainer.anchorMin = new Vector2(0f, 1f);
            _globalInstructionsBulletContainer.anchorMax = new Vector2(1f, 1f);
            _globalInstructionsBulletContainer.pivot = new Vector2(0f, 1f);
            _globalInstructionsBulletContainer.anchoredPosition = Vector2.zero;
            _globalInstructionsBulletContainer.sizeDelta = new Vector2(0f, 430f);
            VerticalLayoutGroup bulletGroup = _globalInstructionsBulletContainer.gameObject.AddComponent<VerticalLayoutGroup>();
            bulletGroup.padding = new RectOffset(4, 4, 10, 8);
            bulletGroup.spacing = 18f;
            bulletGroup.childAlignment = TextAnchor.UpperLeft;
            bulletGroup.childControlWidth = true;
            bulletGroup.childControlHeight = true;
            bulletGroup.childForceExpandWidth = true;
            bulletGroup.childForceExpandHeight = false;
            LayoutElement bulletLayout = _globalInstructionsBulletContainer.gameObject.AddComponent<LayoutElement>();
            bulletLayout.minHeight = 430f;
            bulletLayout.preferredHeight = 430f;
            bulletLayout.flexibleHeight = 1f;

            RectTransform buttonRow = CreateRect("GlobalInstructionsButtonRow", _globalInstructionsRoot);
            HorizontalLayoutGroup rowLayout = buttonRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 16f;
            rowLayout.childAlignment = TextAnchor.MiddleCenter;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = true;
            rowLayout.childForceExpandHeight = true;
            LayoutElement rowElement = buttonRow.gameObject.AddComponent<LayoutElement>();
            rowElement.minHeight = 82f;
            rowElement.preferredHeight = 82f;
            rowElement.flexibleHeight = 0f;

            _globalInstructionsPreviousButton = CreatePersistentGlobalInstructionsButton(
                buttonRow,
                "Button_GlobalInstructionsAnterior",
                "Anterior",
                PreviousGlobalInstructionsPagePressed,
                ExperimentButtonRole.Secondary,
                out _globalInstructionsPreviousButtonText);
            _globalInstructionsNextButton = CreatePersistentGlobalInstructionsButton(
                buttonRow,
                "Button_GlobalInstructionsSiguiente",
                "Siguiente",
                NextGlobalInstructionsPagePressed,
                ExperimentButtonRole.Primary,
                out _globalInstructionsNextButtonText);
            _globalInstructionsCompleteButton = CreatePersistentGlobalInstructionsButton(
                buttonRow,
                "Button_GlobalInstructionsCompletar",
                "Entendido / Comenzar",
                CompleteGlobalInstructionsPressed,
                ExperimentButtonRole.Primary,
                out _globalInstructionsCompleteButtonText);
        }

        private void UpdateGlobalInstructionsView()
        {
            if (_globalInstructionsState == null || _globalInstructionsState.PageCount == 0)
            {
                _globalInstructionsState = ExperimentGlobalInstructionsState.CreateDefault();
            }

            ExperimentGlobalInstructionPage page = _globalInstructionsState.CurrentPage;
            if (_globalInstructionsTitleText != null)
            {
                _globalInstructionsTitleText.text = page?.Title ?? "Instrucciones iniciales";
            }

            if (_globalInstructionsPageText != null)
            {
                _globalInstructionsPageText.text = $"P\u00e1gina {_globalInstructionsState.PageIndex + 1} de {_globalInstructionsState.PageCount}";
            }

            RebuildGlobalInstructionBulletRows(page);
            SetGlobalInstructionButtonState(_globalInstructionsPreviousButton, _globalInstructionsState.HasPrevious);
            SetGlobalInstructionButtonState(_globalInstructionsNextButton, !_globalInstructionsState.CanComplete);
            SetGlobalInstructionButtonState(_globalInstructionsCompleteButton, _globalInstructionsState.CanComplete);

            if (_globalInstructionsPreviousButtonText != null)
            {
                _globalInstructionsPreviousButtonText.text = "Anterior";
            }

            if (_globalInstructionsNextButtonText != null)
            {
                _globalInstructionsNextButtonText.text = "Siguiente";
            }

            if (_globalInstructionsCompleteButtonText != null)
            {
                _globalInstructionsCompleteButtonText.text = "Entendido / Comenzar";
            }

            RefreshGlobalInstructionsRayTargets();
            ClearCurrentEventSystemSelection();
            PlayGlobalInstructionsAudioForCurrentPage();
        }

        private Button CreatePersistentGlobalInstructionsButton(
            Transform parent,
            string objectName,
            string label,
            Action action,
            ExperimentButtonRole role,
            out TextMeshProUGUI labelText)
        {
            RectTransform rect = CreateRect(objectName, parent);
            Image image = rect.gameObject.AddComponent<Image>();
            image.raycastTarget = true;
            Button button = rect.gameObject.AddComponent<Button>();
            int listenerCount = ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(button, image, action, role, true);
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            labelText = CreateButtonText("Label", rect, label);
            labelText.alignment = TextAlignmentOptions.Center;
            labelText.fontStyle = FontStyles.Bold;
            labelText.fontSize = 28;
            labelText.raycastTarget = false;

            LayoutElement layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = 78f;
            layout.preferredHeight = 78f;
            layout.flexibleWidth = 1f;

            Debug.Log(
                $"[P46G-INSTRUCTIONS] global_instructions_nav_button_configured | label=\"{label}\" role={role} " +
                $"path={GetPath(rect)} interactable={button.interactable} raycastTarget={image.raycastTarget} " +
                $"listener_count={listenerCount} scene={SceneManager.GetActiveScene().name}");
            return button;
        }

        private void RebuildGlobalInstructionBulletRows(ExperimentGlobalInstructionPage page)
        {
            if (_globalInstructionsBulletContainer == null)
            {
                return;
            }

            for (int i = _globalInstructionsBulletContainer.childCount - 1; i >= 0; i--)
            {
                Destroy(_globalInstructionsBulletContainer.GetChild(i).gameObject);
            }

            if (page?.Lines == null)
            {
                return;
            }

            foreach (string line in page.Lines)
            {
                RectTransform row = CreateRect("InstructionBulletRow", _globalInstructionsBulletContainer);
                row.anchorMin = new Vector2(0f, 1f);
                row.anchorMax = new Vector2(1f, 1f);
                row.pivot = new Vector2(0f, 1f);
                row.anchoredPosition = Vector2.zero;
                row.sizeDelta = new Vector2(0f, 64f);
                HorizontalLayoutGroup rowLayout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
                rowLayout.spacing = 16f;
                rowLayout.childAlignment = TextAnchor.UpperLeft;
                rowLayout.childControlWidth = true;
                rowLayout.childControlHeight = true;
                rowLayout.childForceExpandWidth = true;
                rowLayout.childForceExpandHeight = false;
                LayoutElement rowElement = row.gameObject.AddComponent<LayoutElement>();
                rowElement.minHeight = 58f;
                rowElement.preferredHeight = 64f;
                rowElement.minWidth = 720f;
                rowElement.preferredWidth = 760f;
                rowElement.flexibleWidth = 1f;

                RectTransform markerWrap = CreateRect("BulletMarkerWrap", row);
                markerWrap.anchorMin = new Vector2(0f, 1f);
                markerWrap.anchorMax = new Vector2(0f, 1f);
                markerWrap.pivot = new Vector2(0f, 1f);
                LayoutElement markerWrapLayout = markerWrap.gameObject.AddComponent<LayoutElement>();
                markerWrapLayout.minWidth = 28f;
                markerWrapLayout.preferredWidth = 28f;
                markerWrapLayout.minHeight = 48f;
                markerWrapLayout.flexibleWidth = 0f;

                RectTransform marker = CreateRect("BulletMarker", markerWrap);
                marker.anchorMin = new Vector2(0.5f, 0.5f);
                marker.anchorMax = new Vector2(0.5f, 0.5f);
                marker.sizeDelta = new Vector2(12f, 12f);
                marker.anchoredPosition = new Vector2(0f, 8f);
                Image markerImage = marker.gameObject.AddComponent<Image>();
                markerImage.color = new Color(0.48f, 0.76f, 0.86f, 1f);
                markerImage.raycastTarget = false;

                TextMeshProUGUI text = CreateTmpText(
                    "BulletText",
                    row,
                    line ?? string.Empty,
                    30,
                    64f);
                text.alignment = TextAlignmentOptions.TopLeft;
                text.lineSpacing = 18f;
                text.raycastTarget = false;
                LayoutElement textLayout = text.GetComponent<LayoutElement>();
                if (textLayout != null)
                {
                    textLayout.minWidth = 660f;
                    textLayout.preferredWidth = 700f;
                    textLayout.flexibleWidth = 1f;
                    textLayout.minHeight = 64f;
                    textLayout.preferredHeight = 70f;
                }
            }
        }

        private void SetGlobalInstructionButtonState(Button button, bool visible)
        {
            if (button == null)
            {
                return;
            }

            GameObject target = button.gameObject;
            if (target.activeSelf != visible)
            {
                target.SetActive(visible);
            }

            button.interactable = visible;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            if (button.targetGraphic != null)
            {
                button.targetGraphic.raycastTarget = visible;
            }
        }

        private void RefreshGlobalInstructionsRayTargets()
        {
            if (_globalInstructionsRoot == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_globalInstructionsRoot);
            Canvas.ForceUpdateCanvases();
            Debug.Log(
                $"[P46G-INSTRUCTIONS] global_instructions_nav_state | scene={SceneManager.GetActiveScene().name} " +
                $"page={(_globalInstructionsState != null ? _globalInstructionsState.PageIndex + 1 : 0)} " +
                $"previous_active={(_globalInstructionsPreviousButton != null && _globalInstructionsPreviousButton.gameObject.activeInHierarchy)} previous_interactable={(_globalInstructionsPreviousButton != null && _globalInstructionsPreviousButton.interactable)} " +
                $"next_active={(_globalInstructionsNextButton != null && _globalInstructionsNextButton.gameObject.activeInHierarchy)} next_interactable={(_globalInstructionsNextButton != null && _globalInstructionsNextButton.interactable)} " +
                $"complete_active={(_globalInstructionsCompleteButton != null && _globalInstructionsCompleteButton.gameObject.activeInHierarchy)} complete_interactable={(_globalInstructionsCompleteButton != null && _globalInstructionsCompleteButton.interactable)} " +
                $"button_count={CountActiveButtons()}");
        }

        private void ClearCurrentEventSystemSelection()
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem != null && eventSystem.currentSelectedGameObject != null)
            {
                eventSystem.SetSelectedGameObject(null);
            }
        }

        private void PlayGlobalInstructionsAudioForCurrentPage()
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
            if (_globalInstructionsAudioPlayer == null)
            {
                _globalInstructionsAudioPlayer = ExperimentGlobalInstructionsAudioPlayer.EnsureAttached(gameObject);
            }

            _globalInstructionsAudioPlayer?.PlayPage(pageIndex);
        }

        private void ResumeSavedExitFromCheckpointPressed()
        {
            if (_startSessionInProgress)
            {
                Debug.Log($"[P46D-PAUSE] saved_exit_resume_ignored_already_starting | scene={SceneManager.GetActiveScene().name} start_screen_path={GetPath(transform)}");
                return;
            }

            ExperimentSessionIdHistoryEntry pending = _pendingSavedExitEntry;
            if (pending == null || string.IsNullOrWhiteSpace(pending.session_id))
            {
                Debug.LogWarning($"[P46D-PAUSE] saved_exit_resume_checkpoint_missing | scene={SceneManager.GetActiveScene().name} start_screen_path={GetPath(transform)}");
                return;
            }

            _startSessionInProgress = true;
            DisableSavedExitPromptPointerBridge("resume_selected");
            Debug.Log($"[P46D-PAUSE] saved_exit_resume_from_checkpoint_selected | scene={SceneManager.GetActiveScene().name} previous_session_id={pending.session_id} questionnaire_code={pending.questionnaire_code} saved_condition_id={pending.saved_condition_id} saved_visible_prueba={pending.saved_visible_prueba} saved_round_index={pending.saved_round_index} resume_policy={pending.resume_policy}");
            StartCoroutine(ResumeSavedExitAfterClickFrame(pending));
        }

        private IEnumerator ResumeSavedExitAfterClickFrame(ExperimentSessionIdHistoryEntry pending)
        {
            yield return null;
            _startSessionCoroutine = null;
            _protocolStarted = true;
            string sceneName = SceneManager.GetActiveScene().name;
            if (!string.Equals(sceneName, StartSceneName, StringComparison.Ordinal))
            {
                _startSessionInProgress = false;
                Debug.LogWarning($"[P46D-PAUSE] saved_exit_resume_checkpoint_wrong_scene | scene={sceneName} expected={StartSceneName}");
                yield break;
            }

            if (!PrepareSimulationForFinalSceneTransition("saved_exit_resume"))
            {
                _startSessionInProgress = false;
                yield break;
            }

            RestoreLocomotion();
            ExperimentRuntimeProtocolUI.MarkLaunchFromSavedExitCheckpoint(pending);
            Debug.Log($"[P46D-PAUSE] saved_exit_resume_checkpoint_begin | from_scene={sceneName} target_scene={FinalSceneName} previous_session_id={pending.session_id} questionnaire_code={pending.questionnaire_code} saved_condition_id={pending.saved_condition_id} saved_visible_prueba={pending.saved_visible_prueba} resume_policy={pending.resume_policy}");
            SceneManager.LoadScene(FinalSceneName);
        }

        private void StartNewAttemptAfterSavedExitPressed()
        {
            ExperimentSessionIdHistoryEntry pending = _pendingSavedExitEntry;
            if (pending != null && !string.IsNullOrWhiteSpace(pending.session_id))
            {
                string replacementStatus = string.Equals(
                    pending.status,
                    ExperimentSessionIdHistoryStore.SavedExitStatus,
                    StringComparison.Ordinal)
                    ? ExperimentSessionIdHistoryStore.SupersededAfterSavedExitStatus
                    : ExperimentSessionIdHistoryStore.AbandonedStatus;
                ExperimentSessionIdHistoryStore.TryUpdateSessionStatus(
                    pending.session_id,
                    replacementStatus,
                    "start_screen_new_attempt_selected",
                    out _);
                Debug.Log($"[P46D-PAUSE] saved_exit_previous_session_superseded | scene={SceneManager.GetActiveScene().name} session_id={pending.session_id} questionnaire_code={pending.questionnaire_code} previous_status={pending.status} new_status={replacementStatus}");
            }

            _savedExitPromptDismissedForNewAttempt = true;
            _pendingSavedExitEntry = null;
            DisableSavedExitPromptPointerBridge("new_attempt_selected");
            Debug.Log($"[P46D-PAUSE] saved_exit_new_attempt_selected | scene={SceneManager.GetActiveScene().name} start_screen_path={GetPath(transform)}");
            StartPressed();
        }

        private void RenderHistory(string reason)
        {
            AddTitle("Historial de sesiones");
            IReadOnlyList<ExperimentSessionIdHistoryEntry> entries = ExperimentSessionIdHistoryStore.LoadRecent(MaxHistoryEntriesToLoad);
            ExperimentSessionHistoryPage page = ExperimentSessionIdHistoryStore.ResolvePage(entries.Count, _historyPageIndex, HistoryEntriesPerPage);
            _historyPageIndex = page.PageIndex;
            TiagoExperimentTelemetry.LogEvent(
                "p46d_history_view_rendered",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["reason"] = reason ?? string.Empty,
                    ["entry_count"] = entries.Count,
                    ["page_index"] = page.PageIndex,
                    ["page_count"] = page.PageCount,
                    ["history_path"] = ExperimentSessionIdHistoryStore.DefaultPath,
                    ["start_screen_path"] = GetPath(transform)
                });
            Debug.Log(
                $"[P46D-08] history_page_rendered | scene={SceneManager.GetActiveScene().name} " +
                $"total_entries={page.TotalEntries} page_index={page.PageIndex} page_count={page.PageCount} " +
                $"start_index={page.StartIndex} end_exclusive={page.EndExclusive} entries_per_page={page.EntriesPerPage} " +
                $"history_path={ExperimentSessionIdHistoryStore.DefaultPath}");

            AddHistoryHeader();
            RectTransform scrollContent = CreateHistoryScrollView(page);
            _historyTable = scrollContent;
            if (entries.Count == 0)
            {
                AddHistoryEmptyMessage();
                _historyTable = null;
                AddSpacer(ExperimentHistoryTableLayout.PaginationTopGap);
                AddHistoryPaginationControls(page);
                AddHistoryBackButton();
                return;
            }

            for (int i = page.StartIndex; i < page.EndExclusive; i++)
            {
                AddHistoryRow(entries[i]);
            }
            _historyTable = null;

            AddSpacer(ExperimentHistoryTableLayout.PaginationTopGap);
            AddHistoryPaginationControls(page);
            AddHistoryBackButton();
        }

        private void ShowHistoryPressed()
        {
            DisableSavedExitPromptPointerBridge("history_selected");
            TiagoExperimentTelemetry.LogEvent(
                "p46d_history_button_pressed",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["start_screen_path"] = GetPath(transform),
                    ["history_path"] = ExperimentSessionIdHistoryStore.DefaultPath
                });

            if (_openHistoryCoroutine != null)
            {
                StopCoroutine(_openHistoryCoroutine);
            }

            _openHistoryCoroutine = StartCoroutine(OpenHistoryAfterClickFrame());
        }

        private IEnumerator OpenHistoryAfterClickFrame()
        {
            yield return null;
            _openHistoryCoroutine = null;
            OpenHistoryView("button_pressed");
        }

        private void OpenHistoryView(string reason)
        {
            _showingHistory = true;
            _historyPageIndex = 0;
            IReadOnlyList<ExperimentSessionIdHistoryEntry> entries = ExperimentSessionIdHistoryStore.LoadRecent(MaxHistoryEntriesToLoad);
            TiagoExperimentTelemetry.LogEvent(
                "experiment_start_screen_history_opened",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["start_screen_path"] = GetPath(transform),
                    ["history_path"] = ExperimentSessionIdHistoryStore.DefaultPath,
                    ["entry_count"] = entries.Count,
                    ["reason"] = reason ?? string.Empty
                });
            Render();
        }

        private void BackFromHistoryPressed()
        {
            Debug.Log($"[P46D-08] history_back_button_click_received | scene={SceneManager.GetActiveScene().name} page_index={_historyPageIndex} start_screen_path={GetPath(transform)} history_path={ExperimentSessionIdHistoryStore.DefaultPath}");
            Debug.Log($"[P46D-07] history_back_button_click_received | scene={SceneManager.GetActiveScene().name} start_screen_path={GetPath(transform)} history_path={ExperimentSessionIdHistoryStore.DefaultPath}");
            Debug.Log($"[P46D-04] history_back_button_click_received | scene={SceneManager.GetActiveScene().name} start_screen_path={GetPath(transform)} history_path={ExperimentSessionIdHistoryStore.DefaultPath}");
            if (_backHistoryCoroutine != null)
            {
                StopCoroutine(_backHistoryCoroutine);
            }

            _backHistoryCoroutine = StartCoroutine(BackFromHistoryAfterClickFrame());
        }

        private IEnumerator BackFromHistoryAfterClickFrame()
        {
            yield return null;
            _backHistoryCoroutine = null;
            Debug.Log($"[P46D-04] history_back_return_started | scene={SceneManager.GetActiveScene().name} start_screen_path={GetPath(transform)}");
            _showingHistory = false;
            _historyPageIndex = 0;
            Render();
            Debug.Log($"[P46D-04] history_back_start_screen_rerendered | scene={SceneManager.GetActiveScene().name} start_screen_path={GetPath(transform)} active_button_count={CountActiveButtons()}");
        }

        private void StartPressed()
        {
            StartProtocolAfterGlobalInstructions();
        }

        private void NextGlobalInstructionsPagePressed()
        {
            NavigateGlobalInstructions(1, "next");
        }

        private void PreviousGlobalInstructionsPagePressed()
        {
            NavigateGlobalInstructions(-1, "previous");
        }

        private void NavigateGlobalInstructions(int direction, string source)
        {
            try
            {
                if (_globalInstructionsState == null)
                {
                    _globalInstructionsState = ExperimentGlobalInstructionsState.CreateDefault();
                }

                int frame = Time.frameCount;
                if (_globalInstructionsLastNavFrame == frame)
                {
                    LogGlobalInstructionsNavigationEvent("global_instructions_nav_debounce_ignored", source, _globalInstructionsState.PageIndex, _globalInstructionsState.PageIndex);
                    return;
                }

                _globalInstructionsLastNavFrame = frame;
                int fromPage = _globalInstructionsState.PageIndex;
                int toPage = direction < 0
                    ? _globalInstructionsState.MovePrevious()
                    : _globalInstructionsState.MoveNext();
                LogGlobalInstructionsNavigationEvent("global_instructions_nav_clicked", source, fromPage, toPage);
                if (toPage != fromPage)
                {
                    LogGlobalInstructionsPageChanged(fromPage, toPage);
                }

                UpdateGlobalInstructionsView();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[P46G-INSTRUCTIONS] global_instructions_nav_error | source={source ?? string.Empty} direction={direction} error={ex.GetType().Name}:{ex.Message} scene={SceneManager.GetActiveScene().name}");
                LogGlobalInstructionsNavigationEvent("global_instructions_nav_error", source, _globalInstructionsState != null ? _globalInstructionsState.PageIndex : 0, _globalInstructionsState != null ? _globalInstructionsState.PageIndex : 0);
            }
        }

        private void CompleteGlobalInstructionsPressed()
        {
            if (_globalInstructionsState == null || !_globalInstructionsState.CanComplete)
            {
                return;
            }

            LogGlobalInstructionsEvent("global_instructions_completed", "completed");
            _globalInstructionsAudioPlayer?.StopAudio("global_instructions_completed");
            _showingGlobalInstructions = false;
            if (_globalInstructionsRoot != null)
            {
                Destroy(_globalInstructionsRoot.gameObject);
            }

            ResetGlobalInstructionViewReferences();
            StartProtocolAfterGlobalInstructions();
        }

        private void StartProtocolAfterGlobalInstructions()
        {
            if (_startSessionInProgress)
            {
                Debug.Log($"[P46D-03] start_session_ignored_already_starting | scene={SceneManager.GetActiveScene().name} start_screen_path={GetPath(transform)}");
                return;
            }

            _startSessionInProgress = true;
            string sceneName = SceneManager.GetActiveScene().name;
            Debug.Log($"[P46D-04] start_screen_start_click_received | scene={sceneName} start_screen_path={GetPath(transform)}");
            Debug.Log($"[P46D-03] start_screen_start_click_received | scene={sceneName} start_screen_path={GetPath(transform)}");
            TiagoExperimentTelemetry.LogEvent(
                "experiment_start_screen_start_pressed",
                new Dictionary<string, object>
                {
                    ["scene"] = sceneName,
                    ["start_screen_path"] = GetPath(transform),
                    ["participant_id"] = "auto_pending"
                });

            _startSessionCoroutine = StartCoroutine(StartSessionAfterClickFrame(sceneName));
        }

        private void LogGlobalInstructionsPageChanged(int fromPage, int toPage)
        {
            Dictionary<string, object> payload = BuildGlobalInstructionsPayload("global_instructions_page_changed", "page_changed");
            payload["from_page_index"] = fromPage;
            payload["to_page_index"] = toPage;
            payload["from_page_number"] = fromPage + 1;
            payload["to_page_number"] = toPage + 1;
            TiagoExperimentTelemetry.LogEvent("global_instructions_page_changed", payload);
            Debug.Log($"[P46G-INSTRUCTIONS] global_instructions_page_changed | session_id={payload["session_id"]} questionnaire_code={payload["questionnaire_code"]} visible_prueba={payload["visible_prueba"]} round_index={payload["round_index"]} status={payload["status"]} from_page={fromPage + 1} to_page={toPage + 1} timestamp={payload["timestamp"]}");
        }

        private void LogGlobalInstructionsNavigationEvent(string eventName, string source, int fromPage, int toPage)
        {
            Dictionary<string, object> payload = BuildGlobalInstructionsPayload(eventName, eventName ?? string.Empty);
            payload["source"] = source ?? string.Empty;
            payload["action"] = source ?? string.Empty;
            payload["from_page_index"] = fromPage;
            payload["to_page_index"] = toPage;
            payload["from_page_number"] = fromPage + 1;
            payload["to_page_number"] = toPage + 1;
            payload["page_before"] = fromPage + 1;
            payload["page_after"] = toPage + 1;
            payload["previous_interactable"] = _globalInstructionsPreviousButton != null && _globalInstructionsPreviousButton.interactable;
            payload["next_interactable"] = _globalInstructionsNextButton != null && _globalInstructionsNextButton.interactable;
            payload["complete_interactable"] = _globalInstructionsCompleteButton != null && _globalInstructionsCompleteButton.interactable;
            TiagoExperimentTelemetry.LogEvent(eventName, payload);
            Debug.Log($"[P46G-INSTRUCTIONS] {eventName} | action={source ?? string.Empty} session_id={payload["session_id"]} questionnaire_code={payload["questionnaire_code"]} visible_prueba={payload["visible_prueba"]} round_index={payload["round_index"]} status={payload["status"]} page_before={fromPage + 1} page_after={toPage + 1} previous_interactable={payload["previous_interactable"]} next_interactable={payload["next_interactable"]} complete_interactable={payload["complete_interactable"]} timestamp={payload["timestamp"]}");
        }

        private void LogGlobalInstructionsEvent(string eventName, string status)
        {
            Dictionary<string, object> payload = BuildGlobalInstructionsPayload(eventName, status);
            TiagoExperimentTelemetry.LogEvent(eventName, payload);
            Debug.Log($"[P46G-INSTRUCTIONS] {eventName} | session_id={payload["session_id"]} questionnaire_code={payload["questionnaire_code"]} visible_prueba={payload["visible_prueba"]} round_index={payload["round_index"]} status={payload["status"]} page={payload["page_number"]} timestamp={payload["timestamp"]}");
        }

        private Dictionary<string, object> BuildGlobalInstructionsPayload(string eventName, string status)
        {
            return new Dictionary<string, object>
            {
                ["event_name"] = eventName ?? string.Empty,
                ["session_id"] = ExperimentDataPathResolver.CurrentSessionId,
                ["questionnaire_code"] = ExperimentDataPathResolver.CurrentQuestionnaireCode,
                ["visible_prueba"] = 0,
                ["round_index"] = 0,
                ["status"] = status ?? string.Empty,
                ["page_index"] = _globalInstructionsState != null ? _globalInstructionsState.PageIndex : 0,
                ["page_number"] = _globalInstructionsState != null ? _globalInstructionsState.PageIndex + 1 : 0,
                ["page_count"] = _globalInstructionsState != null ? _globalInstructionsState.PageCount : 0,
                ["timestamp"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };
        }

        private IEnumerator StartSessionAfterClickFrame(string clickedSceneName)
        {
            Debug.Log($"[P46D-04] start_screen_before_deferred_transition_frame | scene={clickedSceneName} start_screen_path={GetPath(transform)}");
            Debug.Log($"[P46D-03] start_screen_before_deferred_transition_frame | scene={clickedSceneName} start_screen_path={GetPath(transform)}");
            yield return null;
            _startSessionCoroutine = null;
            StartPressedCore();
        }

        private void StartPressedCore()
        {
            _protocolStarted = true;
            string sceneName = SceneManager.GetActiveScene().name;
            Debug.Log($"[P46D-04] start_screen_start_transition_begin | scene={sceneName} start_screen_path={GetPath(transform)}");
            Debug.Log($"[P46D-03] start_screen_start_transition_begin | scene={sceneName} start_screen_path={GetPath(transform)}");
            if (string.Equals(sceneName, StartSceneName, StringComparison.Ordinal))
            {
                TiagoExperimentTelemetry.LogEvent(
                    "p45c17_start_scene_start_pressed",
                    new Dictionary<string, object>
                    {
                        ["scene"] = sceneName,
                        ["start_screen_path"] = GetPath(transform),
                        ["participant_id"] = "auto_pending"
                    });
                Debug.Log($"[P46D-03] start_screen_before_restore_locomotion | scene={sceneName}");
                if (!PrepareSimulationForFinalSceneTransition("start_new_session"))
                {
                    _startSessionInProgress = false;
                    return;
                }

                RestoreLocomotion();
                Debug.Log($"[P46D-03] start_screen_after_restore_locomotion | scene={sceneName}");
                TiagoExperimentTelemetry.LogEvent(
                    "p45c17_start_scene_loading_final_scene",
                    new Dictionary<string, object>
                    {
                        ["from_scene"] = sceneName,
                        ["target_scene"] = FinalSceneName,
                        ["participant_id"] = "auto_pending"
                    });
                Debug.Log($"[P46D-03] start_screen_before_mark_launch | from_scene={sceneName} target_scene={FinalSceneName}");
                ExperimentRuntimeProtocolUI.MarkLaunchFromStartScene();
                Debug.Log($"[P46D-03] start_screen_after_mark_launch_before_load_scene | from_scene={sceneName} target_scene={FinalSceneName}");
                Debug.Log($"[P46D-04] start_screen_before_load_scene | from_scene={sceneName} target_scene={FinalSceneName}");
                SceneManager.LoadScene(FinalSceneName);
                return;
            }

            _startSessionInProgress = false;
            TiagoExperimentTelemetry.LogEvent(
                "p45c17_start_screen_start_pressed_outside_start_scene",
                new Dictionary<string, object>
                {
                    ["scene"] = sceneName,
                    ["start_screen_path"] = GetPath(transform),
                    ["reason"] = "start_screen_only_allowed_in_start_scene"
                });
        }

        private static bool IsSupportedSceneForStartScreen(string sceneName)
        {
            return string.Equals(sceneName, StartSceneName, StringComparison.Ordinal);
        }

        private static void LogBootstrapSceneCheck(string sceneName, bool supportedScene, bool created, Transform parent)
        {
            TiagoExperimentTelemetry.LogEvent(
                "p45c17_start_screen_bootstrap_scene_check",
                new Dictionary<string, object>
                {
                    ["scene_name"] = sceneName ?? string.Empty,
                    ["supported_scene"] = supportedScene,
                    ["created"] = created,
                    ["parent_path"] = parent != null ? GetPath(parent) : string.Empty,
                    ["reason"] = supportedScene ? (created ? "created" : "already_exists_or_not_created") : "start_screen_only_allowed_in_start_scene"
                });
        }

        private void ExitPressed()
        {
            DisableSavedExitPromptPointerBridge("exit_selected");
            TiagoExperimentTelemetry.LogEvent(
                "experiment_runtime_exit_requested",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["application_platform"] = Application.platform.ToString(),
                    ["editor"] = Application.isEditor
                });

#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        public void EnterRealignmentGate(int generation)
        {
            if (_recenterUiGateActive)
            {
                Debug.Log(
                    $"[ExperimentRuntimeStartScreenUI] start_scene_recenter_signal_coalesced | " +
                    $"generation={generation} reason=ui_gate_already_active");
                return;
            }

            GraphicRaycaster graphicRaycaster = GetComponent<GraphicRaycaster>();
            TrackedDeviceGraphicRaycaster trackedRaycaster = GetComponent<TrackedDeviceGraphicRaycaster>();
            Transform rayRoot = transform.Find("RuntimeUiRayInteractors");
            _recenterUiGateSnapshot = new RecenterUiGateSnapshot(
                _canvas != null && _canvas.enabled,
                graphicRaycaster != null && graphicRaycaster.enabled,
                trackedRaycaster != null && trackedRaycaster.enabled,
                rayRoot != null && rayRoot.gameObject.activeSelf,
                _savedExitPromptPointerBridge != null && _savedExitPromptPointerBridge.IsBridgeActive);
            _recenterUiGateActive = true;

            if (_canvas != null)
            {
                _canvas.enabled = false;
            }

            if (graphicRaycaster != null)
            {
                graphicRaycaster.enabled = false;
            }

            if (trackedRaycaster != null)
            {
                trackedRaycaster.enabled = false;
            }

            if (rayRoot != null)
            {
                rayRoot.gameObject.SetActive(false);
            }

            _savedExitPromptPointerBridge?.Deactivate("start_scene_recenter_alignment");
            _recenterMarkerStates.Clear();
            foreach (ExperimentUiRaycastHitMarker marker in FindObjectsByType<ExperimentUiRaycastHitMarker>(
                         FindObjectsInactive.Include,
                         FindObjectsSortMode.None))
            {
                if (marker == null)
                {
                    continue;
                }

                _recenterMarkerStates.Add(new BehaviourState(marker, marker.enabled));
                marker.enabled = false;
            }

            Debug.Log(
                $"[ExperimentRuntimeStartScreenUI] start_scene_recenter_alignment_begin | generation={generation} " +
                $"canvas_hidden=true previous_canvas_visible={_recenterUiGateSnapshot.CanvasEnabled} " +
                $"previous_graphic_raycaster={_recenterUiGateSnapshot.GraphicRaycasterEnabled} " +
                $"previous_tracked_raycaster={_recenterUiGateSnapshot.TrackedRaycasterEnabled} " +
                $"previous_ray_interactors={_recenterUiGateSnapshot.RayInteractorsActive} markers_disabled={_recenterMarkerStates.Count}");
        }

        public void RepositionCanvasAfterAlignment(int generation)
        {
            PositionAtDeterministicStartAnchor();
            RecenterCanvasRepositionCount++;
            Debug.Log(
                $"[ExperimentRuntimeStartScreenUI] start_scene_recenter_canvas_repositioned | generation={generation} " +
                $"reposition_count={RecenterCanvasRepositionCount} canvas_pose={DescribeWorldPose(transform)} " +
                $"vertical_offset={_verticalOffset:F4} canvas_pitch_degrees={_canvasPitchDegrees:F3}");
        }

        public void ReleaseRealignmentGate(string reason, bool alignmentSucceeded)
        {
            if (!_recenterUiGateActive)
            {
                return;
            }

            RestoreRecenterUiGateSnapshot(reason ?? string.Empty);
            RecenterUiGateReleaseCount++;
            Debug.Log(
                $"[ExperimentRuntimeStartScreenUI] start_scene_recenter_alignment_gate_released | " +
                $"succeeded={alignmentSucceeded} reason={reason ?? string.Empty} release_count={RecenterUiGateReleaseCount} " +
                $"canvas_visible={_canvas != null && _canvas.enabled} " +
                $"graphic_raycaster={TryGetComponent(out GraphicRaycaster graphic) && graphic.enabled} " +
                $"tracked_raycaster={TryGetComponent(out TrackedDeviceGraphicRaycaster tracked) && tracked.enabled}");
        }

        public ExperimentStartSceneCanvasDiagnostics CaptureDiagnostics()
        {
            GraphicRaycaster graphicRaycaster = GetComponent<GraphicRaycaster>();
            TrackedDeviceGraphicRaycaster trackedRaycaster = GetComponent<TrackedDeviceGraphicRaycaster>();
            Transform rayRoot = transform.Find("RuntimeUiRayInteractors");
            float positionError = float.PositiveInfinity;
            float yawError = float.PositiveInfinity;
            if (TryCalculateDeterministicStartCanvasPose(out Vector3 targetPosition, out Quaternion targetRotation))
            {
                positionError = Vector3.Distance(transform.position, targetPosition);
                yawError = ExperimentStartSceneXrPoseMath.CalculateHorizontalYawErrorDegrees(
                    transform.forward,
                    targetRotation * Vector3.forward);
            }

            return new ExperimentStartSceneCanvasDiagnostics(
                DescribeWorldPose(transform),
                _canvas != null && _canvas.enabled,
                graphicRaycaster != null && graphicRaycaster.enabled,
                trackedRaycaster != null && trackedRaycaster.enabled,
                rayRoot != null && rayRoot.gameObject.activeSelf,
                positionError,
                yawError);
        }

        private void RestoreRecenterUiGateSnapshot(string reason)
        {
            if (!_recenterUiGateActive)
            {
                return;
            }

            GraphicRaycaster graphicRaycaster = GetComponent<GraphicRaycaster>();
            TrackedDeviceGraphicRaycaster trackedRaycaster = GetComponent<TrackedDeviceGraphicRaycaster>();
            Transform rayRoot = transform.Find("RuntimeUiRayInteractors");
            if (_canvas != null)
            {
                _canvas.enabled = _recenterUiGateSnapshot.CanvasEnabled;
            }

            if (graphicRaycaster != null)
            {
                graphicRaycaster.enabled = _recenterUiGateSnapshot.GraphicRaycasterEnabled;
            }

            if (trackedRaycaster != null)
            {
                trackedRaycaster.enabled = _recenterUiGateSnapshot.TrackedRaycasterEnabled;
            }

            if (rayRoot != null)
            {
                rayRoot.gameObject.SetActive(_recenterUiGateSnapshot.RayInteractorsActive);
            }

            foreach (BehaviourState markerState in _recenterMarkerStates)
            {
                if (markerState.Behaviour != null)
                {
                    markerState.Behaviour.enabled = markerState.Enabled;
                }
            }

            if (_recenterUiGateSnapshot.PointerBridgeActive && _savedExitPromptPointerBridge != null && _canvas != null)
            {
                _savedExitPromptPointerBridge.Activate(_canvas);
            }

            _recenterMarkerStates.Clear();
            _recenterUiGateActive = false;
            Debug.Log(
                $"[ExperimentRuntimeStartScreenUI] start_scene_recenter_ui_state_restored | reason={reason} " +
                $"canvas_visible={_canvas != null && _canvas.enabled} " +
                $"graphic_raycaster={graphicRaycaster != null && graphicRaycaster.enabled} " +
                $"tracked_raycaster={trackedRaycaster != null && trackedRaycaster.enabled} " +
                $"ray_interactors={rayRoot != null && rayRoot.gameObject.activeSelf}");
        }

        private void SetVisible(bool visible)
        {
            if (_canvas != null)
            {
                _canvas.enabled = visible;
            }

            GraphicRaycaster graphicRaycaster = GetComponent<GraphicRaycaster>();
            if (graphicRaycaster != null)
            {
                graphicRaycaster.enabled = visible;
            }

            Transform rayRoot = transform.Find("RuntimeUiRayInteractors");
            if (rayRoot != null)
            {
                rayRoot.gameObject.SetActive(visible);
            }
        }

        private void HideProtocolPanel()
        {
            if (!string.Equals(SceneManager.GetActiveScene().name, StartSceneName, StringComparison.Ordinal))
            {
                TiagoExperimentTelemetry.LogEvent(
                    "p45c17_error_start_screen_hide_protocol_called_in_final_scene",
                    new Dictionary<string, object>
                    {
                        ["scene"] = SceneManager.GetActiveScene().name,
                        ["start_screen_path"] = GetPath(transform),
                        ["reason"] = "start_screen_only_allowed_in_start_scene"
                    });
                return;
            }

            ExperimentRuntimeProtocolUI protocolUi = FindFirstObjectByType<ExperimentRuntimeProtocolUI>(FindObjectsInactive.Include);
            if (protocolUi != null)
            {
                protocolUi.SetProtocolVisible(false);
            }
        }

        private void LockLocomotion()
        {
            if (_locomotionLocked)
            {
                return;
            }

            _locomotionStates.Clear();
            foreach (Behaviour behaviour in FindObjectsByType<Behaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (behaviour == null || !IsUserLocomotionProvider(behaviour))
                {
                    continue;
                }

                _locomotionStates.Add(new BehaviourState(behaviour, behaviour.enabled));
                if (behaviour.enabled)
                {
                    behaviour.enabled = false;
                }
            }

            _locomotionLocked = true;
            TiagoExperimentTelemetry.LogEvent(
                "experiment_start_screen_locomotion_locked",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["disabled_component_count"] = _locomotionStates.Count,
                    ["components"] = DescribeLocomotionStates()
                });
        }

        private void RestoreLocomotion()
        {
            if (!_locomotionLocked)
            {
                return;
            }

            int restored = 0;
            foreach (BehaviourState state in _locomotionStates)
            {
                if (state.Behaviour == null)
                {
                    continue;
                }

                state.Behaviour.enabled = state.Enabled;
                restored++;
            }

            _locomotionStates.Clear();
            _locomotionLocked = false;
            TiagoExperimentTelemetry.LogEvent(
                "experiment_start_screen_locomotion_restored",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["restored_component_count"] = restored
                });
        }

        private bool PrepareSimulationForFinalSceneTransition(string source)
        {
            string normalizedSource = "start_scene_to_final_scene:" + (source ?? string.Empty);
            bool normalized = ExperimentSimulationPauseAuthority.NormalizeForRunningScene(normalizedSource, this);
            bool valid = normalized && ExperimentSimulationPauseAuthority.ValidateRunning(normalizedSource, this);
            Dictionary<string, object> payload = ExperimentSimulationPauseAuthority.BuildOwnerSnapshot(normalizedSource);
            payload["target_scene"] = FinalSceneName;
            payload["valid"] = valid;
            TiagoExperimentTelemetry.LogEvent(
                valid ? "experiment_locomotion_state_before_xr_gate" : "experiment_locomotion_state_invariant_failed",
                payload);
            if (!valid)
            {
                Debug.LogError($"[P46O-04] experiment_locomotion_state_invariant_failed | source={normalizedSource} scene={SceneManager.GetActiveScene().name} target_scene={FinalSceneName} time_scale={Time.timeScale:0.###} global_pause_active={ExperimentSimulationPauseAuthority.IsPauseActive} pending_pause_owners={ExperimentSimulationPauseAuthority.ActiveOwnerCount} owners={ExperimentSimulationPauseAuthority.ActiveOwners}", this);
            }

            return valid;
        }

        private static bool IsUserLocomotionProvider(Behaviour behaviour)
        {
            string typeName = behaviour.GetType().Name;
            string fullName = behaviour.GetType().FullName ?? typeName;
            if (typeName.IndexOf("MoveProvider", StringComparison.OrdinalIgnoreCase) >= 0 ||
                typeName.IndexOf("TurnProvider", StringComparison.OrdinalIgnoreCase) >= 0 ||
                typeName.IndexOf("TeleportationProvider", StringComparison.OrdinalIgnoreCase) >= 0 ||
                typeName.IndexOf("LocomotionProvider", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return fullName.IndexOf("XR.Interaction.Toolkit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    GetPath(behaviour.transform).IndexOf("XR Rig", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return false;
        }

        private string DescribeLocomotionStates()
        {
            var values = new List<string>();
            foreach (BehaviourState state in _locomotionStates)
            {
                if (state.Behaviour != null)
                {
                    values.Add($"{state.Behaviour.GetType().Name}@{GetPath(state.Behaviour.transform)}:wasEnabled={state.Enabled}");
                }
            }

            return string.Join(" | ", values);
        }

        private void PositionAtDeterministicStartAnchor()
        {
            if (TryCalculateDeterministicStartCanvasPose(out Vector3 position, out Quaternion rotation))
            {
                transform.SetPositionAndRotation(position, rotation);
                return;
            }

            transform.SetPositionAndRotation(new Vector3(0f, 1.45f, 1.8f), Quaternion.identity);
        }

        private bool TryCalculateDeterministicStartCanvasPose(out Vector3 position, out Quaternion rotation)
        {
            ResolveStartScenePoseAligner();
            Transform poseAnchor = _startScenePoseAligner != null
                ? _startScenePoseAligner.HeadPoseAnchor
                : null;
            Camera camera = Camera.main ?? FindFirstObjectByType<Camera>();
            Transform poseSource = poseAnchor != null ? poseAnchor : camera != null ? camera.transform : null;
            if (poseSource == null)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }

            Vector3 forward = poseSource.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f)
            {
                forward = Vector3.forward;
            }

            forward.Normalize();
            position = poseSource.position + forward * _distanceFromCamera + Vector3.up * _verticalOffset;
            rotation = Quaternion.LookRotation(forward, Vector3.up) * Quaternion.Euler(_canvasPitchDegrees, 0f, 0f);
            return true;
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
            _savedExitPromptButtons.Clear();
            _activeButtonContainer = null;
            _activeScreenMode = string.Empty;
            if (_globalInstructionsRoot != null)
            {
                Destroy(_globalInstructionsRoot.gameObject);
            }

            ResetGlobalInstructionViewReferences();
            for (int i = _content.childCount - 1; i >= 0; i--)
            {
                Destroy(_content.GetChild(i).gameObject);
            }
        }

        private void ResetGlobalInstructionViewReferences()
        {
            _globalInstructionsRoot = null;
            _globalInstructionsTitleText = null;
            _globalInstructionsPageText = null;
            _globalInstructionsBulletContainer = null;
            _globalInstructionsPreviousButton = null;
            _globalInstructionsNextButton = null;
            _globalInstructionsCompleteButton = null;
            _globalInstructionsPreviousButtonText = null;
            _globalInstructionsNextButtonText = null;
            _globalInstructionsCompleteButtonText = null;
            _lastNarratedGlobalInstructionPage = -1;
        }

        private void AddTitle(string text)
        {
            TextMeshProUGUI title = AddText(text, 48, FontStyles.Bold, 78f);
            title.color = Color.white;
        }

        private void AddBody(string text)
        {
            AddBody(text, 170f);
        }

        private void AddBody(string text, float minHeight)
        {
            TextMeshProUGUI body = AddText(text, 32, FontStyles.Normal, minHeight);
            ExperimentCanvasTypography.ApplyLegacyLineSpacing(body, 1.08f);
        }

        private void AddSavedExitPromptBody(string text, float minHeight)
        {
            TextMeshProUGUI body = AddText(text, SavedExitPromptBodyFontSize, FontStyles.Normal, minHeight);
            ExperimentCanvasTypography.ApplyLegacyLineSpacing(body, 1.04f);
        }

        private void AddInstructionPageBody(ExperimentGlobalInstructionPage page)
        {
            string body = page == null || page.Lines == null || page.Lines.Count == 0
                ? string.Empty
                : "- " + string.Join("\n- ", page.Lines);
            TextMeshProUGUI text = AddText(body, 31, FontStyles.Normal, 430f);
            ExperimentCanvasTypography.ApplyLegacyLineSpacing(text, 1.22f);
            text.alignment = TextAlignmentOptions.MidlineLeft;
        }

        private void AddSpacer(float height)
        {
            RectTransform spacer = CreateRect("Spacer", _content);
            spacer.gameObject.AddComponent<LayoutElement>().minHeight = height;
        }

        private RectTransform BeginButtonStack(string name, int expectedButtonCount, float spacing)
        {
            _activeScreenMode = name ?? string.Empty;
            _activeButtonContainer = CreateRect(name, _content);
            VerticalLayoutGroup layout = _activeButtonContainer.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = Mathf.Max(0f, spacing);
            layout.padding = new RectOffset(0, 0, Mathf.RoundToInt(ButtonStackVerticalPadding), Mathf.RoundToInt(ButtonStackVerticalPadding));
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;

            LayoutElement element = _activeButtonContainer.gameObject.AddComponent<LayoutElement>();
            float stackHeight = CalculateButtonStackHeight(Mathf.Max(0, expectedButtonCount), spacing);
            element.minHeight = stackHeight;
            element.preferredHeight = stackHeight;
            element.flexibleHeight = 0f;
            return _activeButtonContainer;
        }

        private static float CalculateButtonStackHeight(int buttonCount, float spacing)
        {
            if (buttonCount <= 0)
            {
                return 0f;
            }

            return buttonCount * ButtonAreaHeight +
                Mathf.Max(0, buttonCount - 1) * Mathf.Max(0f, spacing) +
                ButtonStackVerticalPadding * 2f;
        }

        private void SetContentSpacing(float spacing)
        {
            if (_content == null)
            {
                return;
            }

            VerticalLayoutGroup layout = _content.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
            {
                layout.spacing = Mathf.Max(0f, spacing);
            }
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

        private TextMeshProUGUI CreateText(
            string name,
            Transform parent,
            string text,
            int fontSize,
            FontStyles style,
            float minHeight,
            TextAlignmentOptions alignment)
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
            rect.gameObject.AddComponent<LayoutElement>().minHeight = minHeight;
            return label;
        }

        private TextMeshProUGUI CreateTmpText(
            string name,
            Transform parent,
            string text,
            int fontSize,
            float minHeight)
        {
            RectTransform rect = CreateRect(name, parent);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(0f, minHeight);
            TextMeshProUGUI label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            ExperimentCanvasTypography.Configure(label);
            label.text = text ?? string.Empty;
            label.fontSize = fontSize;
            label.fontStyle = FontStyles.Normal;
            label.color = new Color(0.92f, 0.94f, 0.95f, 1f);
            label.alignment = TextAlignmentOptions.TopLeft;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Overflow;
            label.raycastTarget = false;
            label.margin = Vector4.zero;
            rect.gameObject.AddComponent<LayoutElement>().minHeight = minHeight;
            return label;
        }

        private void ConfigureSavedExitPromptButton(Button button, string label)
        {
            if (button == null)
            {
                Debug.LogWarning($"[P46D-PAUSE] saved_exit_prompt_button_state | label=\"{label}\" button_missing=True scene={SceneManager.GetActiveScene().name}");
                return;
            }

            RectTransform rect = button.transform as RectTransform;
            Image image = button.targetGraphic as Image;
            if (image == null)
            {
                image = button.GetComponent<Image>();
                button.targetGraphic = image;
            }

            if (image != null)
            {
                image.raycastTarget = true;
            }

            LayoutElement layoutElement = button.GetComponent<LayoutElement>();
            if (layoutElement != null)
            {
                layoutElement.minHeight = ButtonAreaHeight;
                layoutElement.preferredHeight = ButtonAreaHeight;
            }

            SavedExitPromptButtonClickTracer tracer = SavedExitPromptButtonClickTracer.Ensure(button.gameObject, label, GetPath(button.transform));
            Debug.Log($"[P46D-PAUSE] saved_exit_prompt_button_click_tracer_attached | label=\"{label}\" path={GetPath(button.transform)} tracer={tracer != null} scene={SceneManager.GetActiveScene().name}");
            Debug.Log($"[P46D-PAUSE] saved_exit_prompt_button_state | label=\"{label}\" path={GetPath(button.transform)} activeSelf={button.gameObject.activeSelf} activeInHierarchy={button.gameObject.activeInHierarchy} interactable={button.interactable} isInteractable={button.IsInteractable()} targetGraphic={(button.targetGraphic != null ? button.targetGraphic.GetType().Name : string.Empty)} raycastTarget={(button.targetGraphic != null && button.targetGraphic.raycastTarget)} listener_count=1 eventSystem={(EventSystem.current != null ? EventSystem.current.name : string.Empty)} inputModule={(EventSystem.current != null && EventSystem.current.currentInputModule != null ? EventSystem.current.currentInputModule.GetType().FullName : string.Empty)}");
            Debug.Log($"[P46D-PAUSE] saved_exit_prompt_button_listener_configured | label=\"{label}\" path={GetPath(button.transform)} listener_count=1 scene={SceneManager.GetActiveScene().name}");
            LogSavedExitPromptButtonRect(label, rect);
        }

        private Button AddSavedExitPromptButton(string label, Action action, ExperimentButtonRole role)
        {
            Button button = AddButton(label, () =>
            {
                Debug.Log($"[P46D-PAUSE] saved_exit_prompt_button_clicked | label=\"{label}\" scene={SceneManager.GetActiveScene().name} button_path={GetPath(EventSystem.current != null ? EventSystem.current.currentSelectedGameObject != null ? EventSystem.current.currentSelectedGameObject.transform : null : null)} session_id={_pendingSavedExitEntry?.session_id ?? string.Empty} questionnaire_code={_pendingSavedExitEntry?.questionnaire_code ?? string.Empty}");
                Debug.Log($"[P46D-PAUSE] saved_exit_prompt_button_action_executed | label=\"{label}\" scene={SceneManager.GetActiveScene().name} session_id={_pendingSavedExitEntry?.session_id ?? string.Empty} questionnaire_code={_pendingSavedExitEntry?.questionnaire_code ?? string.Empty}");
                action?.Invoke();
            }, role, true);
            _savedExitPromptButtons.Add(button);
            ConfigureSavedExitPromptButton(button, label);
            return button;
        }

        private void RefreshSavedExitPromptRayTargets()
        {
            Canvas.ForceUpdateCanvases();
            if (_content != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            }

            Canvas.ForceUpdateCanvases();
            GraphicRaycaster raycaster = _canvas != null ? _canvas.GetComponent<GraphicRaycaster>() : null;
            CanvasGroup group = _canvas != null ? _canvas.GetComponentInChildren<CanvasGroup>(true) : null;
            Debug.Log($"[P46D-PAUSE] saved_exit_prompt_ray_targets_refreshed | scene={SceneManager.GetActiveScene().name} canvas_path={GetPath(_canvas != null ? _canvas.transform : null)} canvas_enabled={(_canvas != null && _canvas.enabled)} graphic_raycaster_enabled={(raycaster != null && raycaster.enabled)} button_count={CountActiveButtons()}");
            Debug.Log($"[P46D-PAUSE] saved_exit_prompt_overlay_state | scene={SceneManager.GetActiveScene().name} canvas_group_present={group != null} interactable={(group == null || group.interactable)} blocksRaycasts={(group == null || group.blocksRaycasts)} canvas_path={GetPath(_canvas != null ? _canvas.transform : null)}");
            foreach (Button button in _content.GetComponentsInChildren<Button>(false))
            {
                LogSavedExitPromptButtonRect(button.name, button.transform as RectTransform);
            }
        }

        private void EnableSavedExitPromptPointerBridge()
        {
            if (_canvas == null)
            {
                return;
            }

            _savedExitPromptPointerBridge = ExperimentPauseFrontCanvasPointerBridge.Ensure(gameObject, _canvas);
            _savedExitPromptPointerBridge?.SetExplicitButtonTargets(_savedExitPromptButtons);
            _savedExitPromptPointerBridge?.Activate(_canvas);
            Debug.Log($"[P46D-PAUSE] saved_exit_prompt_pointer_bridge_enabled | scene={SceneManager.GetActiveScene().name} canvas_path={GetPath(_canvas.transform)} active={(_savedExitPromptPointerBridge != null && _savedExitPromptPointerBridge.IsBridgeActive)} session_id={_pendingSavedExitEntry?.session_id ?? string.Empty} questionnaire_code={_pendingSavedExitEntry?.questionnaire_code ?? string.Empty}");
        }

        private void DisableSavedExitPromptPointerBridge(string reason)
        {
            if (_savedExitPromptPointerBridge == null)
            {
                return;
            }

            _savedExitPromptPointerBridge.Deactivate(reason ?? "saved_exit_prompt_hidden");
            _savedExitPromptPointerBridge.ClearExplicitButtonTargets();
            Debug.Log($"[P46D-PAUSE] saved_exit_prompt_pointer_bridge_disabled | reason={reason ?? string.Empty} scene={SceneManager.GetActiveScene().name} canvas_path={GetPath(_canvas != null ? _canvas.transform : null)}");
        }

        private static void LogSavedExitPromptButtonRect(string label, RectTransform rect)
        {
            if (rect == null)
            {
                return;
            }

            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            Debug.Log($"[P46D-PAUSE] saved_exit_prompt_button_rect | label=\"{label}\" path={GetPath(rect)} activeInHierarchy={rect.gameObject.activeInHierarchy} localPosition={rect.localPosition} anchoredPosition={rect.anchoredPosition} sizeDelta={rect.sizeDelta} rect={rect.rect} parent={GetPath(rect.parent)}");
            Debug.Log($"[P46D-PAUSE] saved_exit_prompt_button_world_corners | label=\"{label}\" path={GetPath(rect)} corners={corners[0]}|{corners[1]}|{corners[2]}|{corners[3]}");
        }

        private Button AddButton(string label, Action action, ExperimentButtonRole role, bool enabled)
        {
            RectTransform rect = CreateRect("Button_" + label, ResolveButtonParent());
            Image image = rect.gameObject.AddComponent<Image>();
            Button button = rect.gameObject.AddComponent<Button>();
            ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(button, image, action, role, enabled);
            TextMeshProUGUI text = CreateButtonText("Label", rect, label);
            text.alignment = TextAlignmentOptions.Center;
            text.fontStyle = FontStyles.Bold;
            LayoutElement layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = ButtonAreaHeight;
            layout.preferredHeight = ButtonAreaHeight;
            return button;
        }

        private Button AddHistoryButton()
        {
            RectTransform rect = CreateRect(HistoryButtonObjectName, ResolveButtonParent());
            Image image = rect.gameObject.AddComponent<Image>();
            Button button = rect.gameObject.AddComponent<Button>();
            int listenerCount = ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(button, image, ShowHistoryPressed, ExperimentButtonRole.Secondary, true);
            TextMeshProUGUI text = CreateButtonText("Label", rect, HistoryButtonLabel);
            text.alignment = TextAlignmentOptions.Center;
            text.fontStyle = FontStyles.Bold;
            LayoutElement layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = ButtonAreaHeight;
            layout.preferredHeight = ButtonAreaHeight;
            LogHistoryButtonConfigured(button, image, layout, listenerCount);
            return button;
        }

        private Transform ResolveButtonParent()
        {
            return _activeButtonContainer != null ? _activeButtonContainer : _content;
        }

        private void AddHistoryBackButton()
        {
            RectTransform rect = CreateRect(HistoryBackButtonObjectName, _content);
            Image image = rect.gameObject.AddComponent<Image>();
            Button button = rect.gameObject.AddComponent<Button>();
            int listenerCount = ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(button, image, BackFromHistoryPressed, ExperimentButtonRole.Secondary, true);
            TextMeshProUGUI text = CreateButtonText("Label", rect, HistoryBackButtonLabel);
            text.alignment = TextAlignmentOptions.Center;
            text.fontStyle = FontStyles.Bold;
            LayoutElement layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = ButtonAreaHeight;
            layout.preferredHeight = ButtonAreaHeight;
            LogHistoryBackButtonConfigured(button, image, layout, listenerCount);
        }

        private void AddHistoryPaginationControls(ExperimentSessionHistoryPage page)
        {
            RectTransform row = CreateRect("HistoryPaginationRow", _content);
            HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = ExperimentHistoryTableLayout.PaginationSpacing;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            LayoutElement rowElement = row.gameObject.AddComponent<LayoutElement>();
            rowElement.minHeight = ExperimentHistoryTableLayout.PaginationHeight;
            rowElement.preferredHeight = ExperimentHistoryTableLayout.PaginationHeight;
            rowElement.flexibleHeight = 0f;

            AddHistoryPaginationButton(
                row,
                HistoryPreviousButtonObjectName,
                HistoryPreviousButtonLabel,
                PreviousHistoryPagePressed,
                ExperimentButtonRole.Secondary,
                page.HasPreviousPage);
            AddHistoryPageIndicator(row, page);
            AddHistoryPaginationButton(
                row,
                HistoryNextButtonObjectName,
                HistoryNextButtonLabel,
                NextHistoryPagePressed,
                ExperimentButtonRole.Primary,
                page.HasNextPage);

            Debug.Log(
                $"[P46D-08] history_pagination_controls_configured | scene={SceneManager.GetActiveScene().name} " +
                $"total_entries={page.TotalEntries} page_index={page.PageIndex} page_count={page.PageCount} " +
                $"previous_enabled={page.HasPreviousPage} next_enabled={page.HasNextPage} " +
                $"history_path={ExperimentSessionIdHistoryStore.DefaultPath}");
        }

        private void AddHistoryPaginationButton(
            Transform parent,
            string objectName,
            string label,
            Action action,
            ExperimentButtonRole role,
            bool enabled)
        {
            RectTransform rect = CreateRect(objectName, parent);
            Image image = rect.gameObject.AddComponent<Image>();
            Button button = rect.gameObject.AddComponent<Button>();
            int listenerCount = ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(button, image, action, role, enabled);
            TextMeshProUGUI text = CreateButtonText("Label", rect, label);
            text.alignment = TextAlignmentOptions.Center;
            text.fontStyle = FontStyles.Bold;
            text.fontSize = ExperimentHistoryTableLayout.PaginationFontSize;
            LayoutElement buttonElement = rect.gameObject.AddComponent<LayoutElement>();
            buttonElement.minHeight = ExperimentHistoryTableLayout.PaginationHeight;
            buttonElement.preferredHeight = ExperimentHistoryTableLayout.PaginationHeight;
            buttonElement.flexibleHeight = 0f;
            Debug.Log(
                $"[P46D-08] history_pagination_button_configured | scene={SceneManager.GetActiveScene().name} " +
                $"button_name={objectName} button_path={GetPath(rect)} interactable={button.interactable} " +
                $"role={role} image_raycast_target={image.raycastTarget} listener_count_expected={listenerCount}");
        }

        private void AddHistoryPageIndicator(Transform parent, ExperimentSessionHistoryPage page)
        {
            RectTransform rect = CreateRect("HistoryPageIndicator", parent);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = new Color(0.06f, 0.07f, 0.08f, 0.78f);
            image.raycastTarget = false;
            TextMeshProUGUI text = CreateButtonText("Label", rect, page.PageCount == 0 ? "Pagina 0/0" : $"Pagina {page.PageIndex + 1}/{page.PageCount}");
            text.alignment = TextAlignmentOptions.Center;
            text.fontStyle = FontStyles.Bold;
            text.fontSize = ExperimentHistoryTableLayout.PaginationFontSize;
            LayoutElement indicatorElement = rect.gameObject.AddComponent<LayoutElement>();
            indicatorElement.minHeight = ExperimentHistoryTableLayout.PaginationHeight;
            indicatorElement.preferredHeight = ExperimentHistoryTableLayout.PaginationHeight;
            indicatorElement.flexibleHeight = 0f;
        }

        private void PreviousHistoryPagePressed()
        {
            IReadOnlyList<ExperimentSessionIdHistoryEntry> entries = ExperimentSessionIdHistoryStore.LoadRecent(MaxHistoryEntriesToLoad);
            ExperimentSessionHistoryPage page = ExperimentSessionIdHistoryStore.ResolvePage(entries.Count, _historyPageIndex, HistoryEntriesPerPage);
            int nextPageIndex = Math.Max(0, page.PageIndex - 1);
            Debug.Log(
                $"[P46D-08] history_previous_page_click_received | scene={SceneManager.GetActiveScene().name} " +
                $"from_page={page.PageIndex} to_page={nextPageIndex} page_count={page.PageCount} total_entries={entries.Count} " +
                $"history_path={ExperimentSessionIdHistoryStore.DefaultPath}");
            if (nextPageIndex == page.PageIndex)
            {
                return;
            }

            _historyPageIndex = nextPageIndex;
            Render();
        }

        private void NextHistoryPagePressed()
        {
            IReadOnlyList<ExperimentSessionIdHistoryEntry> entries = ExperimentSessionIdHistoryStore.LoadRecent(MaxHistoryEntriesToLoad);
            ExperimentSessionHistoryPage page = ExperimentSessionIdHistoryStore.ResolvePage(entries.Count, _historyPageIndex, HistoryEntriesPerPage);
            int nextPageIndex = page.PageCount == 0 ? 0 : Math.Min(page.PageCount - 1, page.PageIndex + 1);
            Debug.Log(
                $"[P46D-08] history_next_page_click_received | scene={SceneManager.GetActiveScene().name} " +
                $"from_page={page.PageIndex} to_page={nextPageIndex} page_count={page.PageCount} total_entries={entries.Count} " +
                $"history_path={ExperimentSessionIdHistoryStore.DefaultPath}");
            if (nextPageIndex == page.PageIndex)
            {
                return;
            }

            _historyPageIndex = nextPageIndex;
            Render();
        }

        private void AddHorizontalButtons(params (string label, Action action, ExperimentButtonRole role, bool enabled)[] buttons)
        {
            RectTransform row = CreateRect("ButtonRow", _content);
            HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 12f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            row.gameObject.AddComponent<LayoutElement>().minHeight = 72f;
            foreach ((string label, Action action, ExperimentButtonRole role, bool enabled) in buttons)
            {
                RectTransform rect = CreateRect("Button_" + label, row);
                Image image = rect.gameObject.AddComponent<Image>();
                Button button = rect.gameObject.AddComponent<Button>();
                ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(button, image, action, role, enabled);
                TextMeshProUGUI text = CreateButtonText("Label", rect, label);
                text.alignment = TextAlignmentOptions.Center;
                text.fontStyle = FontStyles.Bold;
                rect.gameObject.AddComponent<LayoutElement>().minHeight = 72f;
            }
        }

        private void LogHistoryButtonConfigured(Button button, Image image, LayoutElement layout, int listenerCount)
        {
            _buttonConfigurationSequence++;
            RectTransform rect = button != null ? button.GetComponent<RectTransform>() : null;
            TiagoExperimentTelemetry.LogEvent(
                "p46d_history_button_configured",
                new Dictionary<string, object>
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["sequence"] = _buttonConfigurationSequence,
                    ["button_path"] = button != null ? GetPath(button.transform) : string.Empty,
                    ["button_name"] = button != null ? button.name : string.Empty,
                    ["button_exists"] = button != null,
                    ["button_interactable"] = button != null && button.interactable,
                    ["image_exists"] = image != null,
                    ["image_raycast_target"] = image != null && image.raycastTarget,
                    ["listener_count_expected"] = listenerCount,
                    ["layout_min_height"] = layout != null ? layout.minHeight : 0f,
                    ["rect_size"] = rect != null ? rect.rect.size : Vector2.zero,
                    ["canvas_enabled"] = _canvas != null && _canvas.enabled,
                    ["graphic_raycaster_enabled"] = TryGetComponent(out GraphicRaycaster graphicRaycaster) && graphicRaycaster.enabled,
                    ["history_path"] = ExperimentSessionIdHistoryStore.DefaultPath
                });
        }

        private void LogHistoryBackButtonConfigured(Button button, Image image, LayoutElement layout, int listenerCount)
        {
            RectTransform rect = button != null ? button.GetComponent<RectTransform>() : null;
            Debug.Log(
                $"[P46D-04] history_back_button_configured | scene={SceneManager.GetActiveScene().name} " +
                $"button_path={(button != null ? GetPath(button.transform) : string.Empty)} " +
                $"button_name={(button != null ? button.name : string.Empty)} " +
                $"button_exists={button != null} button_interactable={button != null && button.interactable} " +
                $"image_exists={image != null} image_raycast_target={image != null && image.raycastTarget} " +
                $"listener_count_expected={listenerCount} layout_min_height={(layout != null ? layout.minHeight : 0f)} " +
                $"rect_size={(rect != null ? rect.rect.size : Vector2.zero)} canvas_enabled={_canvas != null && _canvas.enabled} " +
                $"graphic_raycaster_enabled={TryGetComponent(out GraphicRaycaster graphicRaycaster) && graphicRaycaster.enabled} " +
                $"history_path={ExperimentSessionIdHistoryStore.DefaultPath}");
        }

        private int CountActiveButtons()
        {
            return _canvas != null ? _canvas.GetComponentsInChildren<Button>(false).Length : 0;
        }

        private void LogP46HCanvasLayoutConfigured(string source)
        {
            RectTransform canvasRect = _canvas != null ? _canvas.GetComponent<RectTransform>() : null;
            Transform panel = _content != null ? _content.parent : null;
            Debug.Log(
                $"p46h_canvas_layout_configured | canvas_name={(_canvas != null ? _canvas.name : RootName)} " +
                $"panel_path={GetPath(panel)} size_delta={(canvasRect != null ? canvasRect.sizeDelta : Vector2.zero)} " +
                $"local_scale={(canvasRect != null ? canvasRect.localScale : Vector3.zero)} " +
                $"world_position={(canvasRect != null ? canvasRect.position : Vector3.zero)} " +
                $"world_rotation={(canvasRect != null ? canvasRect.eulerAngles : Vector3.zero)} " +
                $"button_count={CountActiveButtons()} content_height={CalculateLayoutContentHeight(_content):0.#} " +
                $"safe_padding={PanelSafeVerticalPadding:0.#} layout_spacing={GetContentSpacing(_content):0.#} " +
                $"button_area_height={ButtonAreaHeight:0.#} start_panel_height={StartScreenPanelHeight:0.#} " +
                $"saved_exit_body_font={SavedExitPromptBodyFontSize} saved_exit_long_body_height={SavedExitPromptLongBodyHeight:0.#} " +
                $"source={source ?? string.Empty}");
        }

        private void LogP46H04StartScreenGeometry(string mode, string source)
        {
            Canvas.ForceUpdateCanvases();
            if (_content != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            }

            if (_activeButtonContainer != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_activeButtonContainer);
            }

            Canvas.ForceUpdateCanvases();

            RectTransform canvasRect = _canvas != null ? _canvas.GetComponent<RectTransform>() : null;
            RectTransform panelRect = _content != null ? _content.parent as RectTransform : null;
            RectTransform lastButtonRect = FindLastActiveButtonRect();
            int buttonCount = _activeButtonContainer != null
                ? _activeButtonContainer.GetComponentsInChildren<Button>(false).Length
                : CountActiveButtons();
            float buttonContainerHeight = _activeButtonContainer != null ? _activeButtonContainer.rect.height : 0f;
            bool lastInsidePanel = IsRectInsideParent(lastButtonRect, panelRect, out float lastBottom, out float lastTop);

            Debug.Log(
                $"p46h04_start_screen_geometry | mode={mode ?? string.Empty} source={source ?? string.Empty} " +
                $"canvas_size={(canvasRect != null ? canvasRect.rect.size : Vector2.zero)} " +
                $"canvas_size_delta={(canvasRect != null ? canvasRect.sizeDelta : Vector2.zero)} " +
                $"panel_size={(panelRect != null ? panelRect.rect.size : Vector2.zero)} " +
                $"content_size={(_content != null ? _content.rect.size : Vector2.zero)} " +
                $"button_container_path={GetPath(_activeButtonContainer)} button_container_height={buttonContainerHeight:0.#} " +
                $"button_count={buttonCount} layout_content_height={CalculateLayoutContentHeight(_content):0.#} " +
                $"panel_y_min={(panelRect != null ? panelRect.rect.yMin : 0f):0.#} panel_y_max={(panelRect != null ? panelRect.rect.yMax : 0f):0.#} " +
                $"last_button_path={GetPath(lastButtonRect)} last_button_local_position={(lastButtonRect != null ? lastButtonRect.localPosition : Vector3.zero)} " +
                $"last_button_size={(lastButtonRect != null ? lastButtonRect.rect.size : Vector2.zero)} " +
                $"last_button_bottom_local={lastBottom:0.#} last_button_top_local={lastTop:0.#} " +
                $"last_button_inside_panel={lastInsidePanel}");
            Debug.Log(
                $"p46h04_start_screen_button_bounds | mode={mode ?? string.Empty} source={source ?? string.Empty} " +
                $"last_button_path={GetPath(lastButtonRect)} last_button_inside_panel={lastInsidePanel} " +
                $"last_button_bottom_local={lastBottom:0.#} panel_y_min={(panelRect != null ? panelRect.rect.yMin : 0f):0.#} " +
                $"button_container_height={buttonContainerHeight:0.#} button_stack_spacing={GetContentSpacing(_activeButtonContainer):0.#}");
        }

        private RectTransform FindLastActiveButtonRect()
        {
            Transform searchRoot = _activeButtonContainer != null ? _activeButtonContainer : _content;
            if (searchRoot == null)
            {
                return null;
            }

            Button[] buttons = searchRoot.GetComponentsInChildren<Button>(false);
            return buttons != null && buttons.Length > 0 ? buttons[buttons.Length - 1].transform as RectTransform : null;
        }

        private static bool IsRectInsideParent(RectTransform child, RectTransform parent, out float bottom, out float top)
        {
            bottom = 0f;
            top = 0f;
            if (child == null || parent == null)
            {
                return false;
            }

            var corners = new Vector3[4];
            child.GetWorldCorners(corners);
            bottom = float.PositiveInfinity;
            top = float.NegativeInfinity;
            for (int i = 0; i < corners.Length; i++)
            {
                float localY = parent.InverseTransformPoint(corners[i]).y;
                bottom = Mathf.Min(bottom, localY);
                top = Mathf.Max(top, localY);
            }

            const float tolerance = 0.5f;
            return bottom >= parent.rect.yMin - tolerance && top <= parent.rect.yMax + tolerance;
        }

        private static float GetContentSpacing(RectTransform content)
        {
            VerticalLayoutGroup layout = content != null ? content.GetComponent<VerticalLayoutGroup>() : null;
            return layout != null ? layout.spacing : 0f;
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

        private void AddHistoryHeader()
        {
            RectTransform row = CreateHistoryRowRoot(
                "HistoryHeader",
                ExperimentHistoryTableLayout.ViewportHorizontalInset);
            AddHistoryCells(row, null, isHeader: true);
        }

        private void AddHistoryRow(ExperimentSessionIdHistoryEntry entry)
        {
            RectTransform row = CreateHistoryRowRoot("HistoryRow", horizontalPadding: 0);
            AddHistoryCells(row, entry, isHeader: false);
        }

        private RectTransform CreateHistoryScrollView(ExperimentSessionHistoryPage page)
        {
            RectTransform scrollRoot = CreateRect("HistoryScrollView", _content);
            LayoutElement rootLayout = scrollRoot.gameObject.AddComponent<LayoutElement>();
            rootLayout.minHeight = HistoryViewportHeight;
            rootLayout.preferredHeight = HistoryViewportHeight;
            rootLayout.flexibleHeight = 0f;

            Image rootImage = scrollRoot.gameObject.AddComponent<Image>();
            rootImage.color = new Color(0.075f, 0.085f, 0.095f, 0.84f);
            rootImage.raycastTarget = true;

            ScrollRect scrollRect = scrollRoot.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = false;
            scrollRect.inertia = false;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 18f;

            RectTransform viewport = CreateRect("Viewport", scrollRoot);
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(8f, 8f);
            viewport.offsetMax = new Vector2(-8f, -8f);
            Image viewportImage = viewport.gameObject.AddComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0.01f);
            viewportImage.raycastTarget = true;
            viewport.gameObject.AddComponent<RectMask2D>();

            RectTransform content = CreateRect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 0f);

            VerticalLayoutGroup layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 4f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            ContentSizeFitter fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.viewport = viewport;
            scrollRect.content = content;

            Debug.Log(
                $"[P46D-07] history_scroll_view_created | scene={SceneManager.GetActiveScene().name} " +
                $"entry_count={page.TotalEntries} visible_rows={HistoryEntriesPerPage} viewport_height={HistoryViewportHeight} " +
                $"scroll_enabled={scrollRect.vertical} scroll_view_path={GetPath(scrollRoot)} history_path={ExperimentSessionIdHistoryStore.DefaultPath}");
            Debug.Log(
                $"[P46D-08] history_scroll_view_created | scene={SceneManager.GetActiveScene().name} " +
                $"total_entries={page.TotalEntries} rendered_rows={page.EndExclusive - page.StartIndex} " +
                $"visible_rows={HistoryEntriesPerPage} page_index={page.PageIndex} page_count={page.PageCount} " +
                $"viewport_height={HistoryViewportHeight} scroll_enabled={scrollRect.vertical} " +
                $"scroll_view_path={GetPath(scrollRoot)} history_path={ExperimentSessionIdHistoryStore.DefaultPath}");
            return content;
        }

        private void AddHistoryEmptyMessage()
        {
            RectTransform rect = CreateRect("HistoryEmptyMessage", _historyTable ?? _content);
            TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            ExperimentCanvasTypography.Configure(text);
            text.text = "No hay sesiones registradas todavia.";
            text.fontSize = 32;
            text.fontStyle = FontStyles.Normal;
            text.color = new Color(0.92f, 0.94f, 0.95f, 1f);
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            rect.gameObject.AddComponent<LayoutElement>().minHeight = HistoryViewportHeight - 20f;
        }

        private RectTransform CreateHistoryRowRoot(string name, int horizontalPadding)
        {
            RectTransform row = CreateRect(name, _historyTable ?? _content);
            HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(horizontalPadding, horizontalPadding, 0, 0);
            layout.spacing = ExperimentHistoryTableLayout.ColumnSpacing;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            LayoutElement rowElement = row.gameObject.AddComponent<LayoutElement>();
            rowElement.minHeight = ExperimentHistoryTableLayout.RowHeight;
            rowElement.preferredHeight = ExperimentHistoryTableLayout.RowHeight;
            rowElement.flexibleHeight = 0f;
            return row;
        }

        private void AddHistoryCells(
            Transform row,
            ExperimentSessionIdHistoryEntry entry,
            bool isHeader)
        {
            IReadOnlyList<ExperimentHistoryColumnDefinition> columns = ExperimentHistoryTableLayout.Columns;
            for (int i = 0; i < columns.Count; i++)
            {
                ExperimentHistoryColumnDefinition definition = columns[i];
                string value = isHeader ? definition.Header : ResolveHistoryCellValue(entry, definition.Column);
                AddHistoryCell(row, value, definition, isHeader);
            }
        }

        private static string ResolveHistoryCellValue(
            ExperimentSessionIdHistoryEntry entry,
            ExperimentHistoryColumn column)
        {
            if (entry == null)
            {
                return string.Empty;
            }

            return column switch
            {
                ExperimentHistoryColumn.FullId => entry.full_id ?? string.Empty,
                ExperimentHistoryColumn.QuestionnaireCode => entry.questionnaire_code ?? string.Empty,
                ExperimentHistoryColumn.StartedAt => entry.started_at_display ?? entry.started_at_iso ?? string.Empty,
                ExperimentHistoryColumn.Status => entry.status ?? string.Empty,
                _ => string.Empty
            };
        }

        private void AddHistoryCell(
            Transform row,
            string value,
            ExperimentHistoryColumnDefinition definition,
            bool isHeader)
        {
            RectTransform rect = CreateRect("Cell", row);
            TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            ExperimentCanvasTypography.Configure(text);
            text.text = value ?? string.Empty;
            int fontSize = isHeader ? ExperimentHistoryTableLayout.HeaderFontSize : definition.CellFontSize;
            text.fontSize = fontSize;
            text.fontStyle = isHeader || definition.Column == ExperimentHistoryColumn.QuestionnaireCode
                ? FontStyles.Bold
                : FontStyles.Normal;
            text.color = new Color(0.92f, 0.94f, 0.95f, 1f);
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.enableAutoSizing = !isHeader && definition.AllowAutoSize;
            if (text.enableAutoSizing)
            {
                text.fontSizeMin = definition.MinimumFontSize;
                text.fontSizeMax = definition.CellFontSize;
            }
            text.raycastTarget = false;
            LayoutElement cellElement = rect.gameObject.AddComponent<LayoutElement>();
            cellElement.minWidth = definition.Width;
            cellElement.preferredWidth = definition.Width;
            cellElement.flexibleWidth = 0f;
            cellElement.minHeight = ExperimentHistoryTableLayout.CellHeight;
            cellElement.preferredHeight = ExperimentHistoryTableLayout.CellHeight;
            cellElement.flexibleHeight = 0f;
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
            text.fontSize = 34;
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

        private static Transform FindExperimentParent()
        {
            GameObject root = GameObject.Find("Experiment");
            return root != null ? root.transform : null;
        }

        private static string GetPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            var stack = new Stack<string>();
            Transform current = transform;
            while (current != null)
            {
                stack.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", stack);
        }

        private static string DescribeWorldPose(Transform value)
        {
            return value == null
                ? "missing"
                : $"pos({value.position.x:F5},{value.position.y:F5},{value.position.z:F5})_rot({value.eulerAngles.x:F3},{value.eulerAngles.y:F3},{value.eulerAngles.z:F3})";
        }

        private readonly struct RecenterUiGateSnapshot
        {
            public RecenterUiGateSnapshot(
                bool canvasEnabled,
                bool graphicRaycasterEnabled,
                bool trackedRaycasterEnabled,
                bool rayInteractorsActive,
                bool pointerBridgeActive)
            {
                CanvasEnabled = canvasEnabled;
                GraphicRaycasterEnabled = graphicRaycasterEnabled;
                TrackedRaycasterEnabled = trackedRaycasterEnabled;
                RayInteractorsActive = rayInteractorsActive;
                PointerBridgeActive = pointerBridgeActive;
            }

            public bool CanvasEnabled { get; }
            public bool GraphicRaycasterEnabled { get; }
            public bool TrackedRaycasterEnabled { get; }
            public bool RayInteractorsActive { get; }
            public bool PointerBridgeActive { get; }
        }

        private readonly struct BehaviourState
        {
            public BehaviourState(Behaviour behaviour, bool enabled)
            {
                Behaviour = behaviour;
                Enabled = enabled;
            }

            public Behaviour Behaviour { get; }
            public bool Enabled { get; }
        }

        private sealed class SavedExitPromptButtonClickTracer : MonoBehaviour,
            IPointerEnterHandler,
            IPointerExitHandler,
            IPointerDownHandler,
            IPointerUpHandler,
            IPointerClickHandler,
            ISelectHandler,
            ISubmitHandler
        {
            private string _label;
            private string _configuredPath;

            public static SavedExitPromptButtonClickTracer Ensure(GameObject target, string label, string configuredPath)
            {
                if (target == null)
                {
                    return null;
                }

                SavedExitPromptButtonClickTracer tracer = target.GetComponent<SavedExitPromptButtonClickTracer>();
                if (tracer == null)
                {
                    tracer = target.AddComponent<SavedExitPromptButtonClickTracer>();
                }

                tracer._label = label ?? string.Empty;
                tracer._configuredPath = configuredPath ?? string.Empty;
                return tracer;
            }

            public void OnPointerEnter(PointerEventData eventData) => Log("saved_exit_prompt_pointer_enter", eventData);
            public void OnPointerExit(PointerEventData eventData) => Log("saved_exit_prompt_pointer_exit", eventData);
            public void OnPointerDown(PointerEventData eventData) => Log("saved_exit_prompt_pointer_down", eventData);
            public void OnPointerUp(PointerEventData eventData) => Log("saved_exit_prompt_pointer_up", eventData);
            public void OnPointerClick(PointerEventData eventData) => Log("saved_exit_prompt_button_clicked", eventData);
            public void OnSelect(BaseEventData eventData) => Log("saved_exit_prompt_button_selected", eventData);
            public void OnSubmit(BaseEventData eventData) => Log("saved_exit_prompt_button_submitted", eventData);

            private void Log(string eventName, BaseEventData eventData)
            {
                PointerEventData pointer = eventData as PointerEventData;
                Debug.Log($"[P46D-PAUSE] {eventName} | label=\"{_label}\" path={GetPath(transform)} configured_path={_configuredPath} activeSelf={gameObject.activeSelf} activeInHierarchy={gameObject.activeInHierarchy} pointerId={(pointer != null ? pointer.pointerId : 0)} position={(pointer != null ? pointer.position.ToString() : string.Empty)} eligibleForClick={(pointer != null && pointer.eligibleForClick)} clickCount={(pointer != null ? pointer.clickCount : 0)} eventSystem={(EventSystem.current != null ? EventSystem.current.name : string.Empty)} inputModule={(EventSystem.current != null && EventSystem.current.currentInputModule != null ? EventSystem.current.currentInputModule.GetType().FullName : string.Empty)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            }
        }
    }
}
