using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using UnityEngine.XR.Interaction.Toolkit.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using XRInputDevice = UnityEngine.XR.InputDevice;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class ExperimentRuntimeUiRayInteractorBootstrap : MonoBehaviour
    {
        private const string LogPrefix = "[P45C-10][XR-UI]";
        private const string LeftRayName = "Left Runtime UI Ray Interactor";
        private const string RightRayName = "Right Runtime UI Ray Interactor";
        private const string LeftVisualName = "Left Runtime UI Ray Visual";
        private const string RightVisualName = "Right Runtime UI Ray Visual";
        private const string LeftAnchorName = "Left Runtime UI Ray Anchor";
        private const string RightAnchorName = "Right Runtime UI Ray Anchor";
        private const float RecoveryCooldownSeconds = 2.5f;
        private const float HandTrackingSkipLogCooldownSeconds = 3f;

        [SerializeField] private Canvas _targetCanvas;
        [SerializeField] private bool _enableManualFallback = true;
        [SerializeField] private bool _enableVerboseXrUiRayDiagnostics;
        [SerializeField] private bool _enableUiRayRecoveryWatchdog = true;
        [SerializeField] private bool _disableRuntimeUiRayCreationOnQuest = true;
        [SerializeField] private float _uiRayRecoveryWatchdogIntervalSeconds = 1f;
        [SerializeField] private float _rayLength = 6f;
        [SerializeField] private float _lineWidth = 0.008f;
        [SerializeField] private Color _lineColor = new(0.1f, 0.85f, 1f, 0.95f);

        private int _configureInvocation;
        private bool _cleanupLogged;
        private bool _visualConsolidationLogged;
        private bool _leftControllerTracked;
        private bool _rightControllerTracked;
        private float _nextWatchdogAt;
        private float _nextRecoveryAllowedAt;
        private float _lastVisualWithoutHitLogAt = -100f;
        private float _lastHandTrackingSkipLogAt = -100f;
        private bool _locomotionAuditLogged;
        private bool _inputActionManagerAuditLogged;
        private bool _hasLoggedCanvasRaycasterState;
        private bool _lastCanvasFound;
        private bool _lastCanvasRaycasterEnabled;
        private string _lastCanvasPath = string.Empty;
        private bool _hasLoggedRuntimeUiRayPolicyState;
        private bool _lastRuntimeUiRayQuestPolicy;

        public static ExperimentRuntimeUiRayInteractorBootstrap EnsureAttached(GameObject protocolUiRoot, Canvas canvas)
        {
            if (protocolUiRoot == null)
            {
                Debug.LogError($"{LogPrefix} Cannot attach runtime UI ray bootstrap because protocolUiRoot is null.");
                return null;
            }

            Debug.Log($"{LogPrefix} EnsureAttached requested for {GetPath(protocolUiRoot.transform)}.");
            ExperimentRuntimeXriRigTopologyDiagnostics.EnsureAttached(protocolUiRoot, canvas);
            ExperimentRuntimeStableProtocolUiPointer.EnsureAttached(protocolUiRoot, canvas);
            Transform root = protocolUiRoot.transform.Find("RuntimeUiRayInteractors");
            if (root == null)
            {
                var host = new GameObject("RuntimeUiRayInteractors");
                host.transform.SetParent(protocolUiRoot.transform, false);
                root = host.transform;
            }

            ExperimentRuntimeUiRayInteractorBootstrap bootstrap =
                root.GetComponent<ExperimentRuntimeUiRayInteractorBootstrap>();
            if (bootstrap == null)
            {
                bootstrap = root.gameObject.AddComponent<ExperimentRuntimeUiRayInteractorBootstrap>();
            }

            ExperimentUiRaycastHitMarker.EnsureAttached(protocolUiRoot);
            bootstrap.Initialize(canvas);
            bootstrap.ConfigureNow("ensure_attached");
            return bootstrap;
        }

        public void Initialize(Canvas canvas)
        {
            _targetCanvas = canvas;
        }

        public void RebindCanvas(Canvas canvas, string reason)
        {
            Initialize(canvas);
            ConfigureNow(string.IsNullOrWhiteSpace(reason) ? "rebind" : reason);
        }

        private void Start()
        {
            LogLocomotionAudit("start");
            LogInputActionManagerAudit("start");
            EnforceRuntimeUiRayPolicy("start");
            StartCoroutine(ConfigureAfterRigActivation());
        }

        private void OnEnable()
        {
            InputDevices.deviceConnected += OnInputDeviceConnected;
            InputDevices.deviceConfigChanged += OnInputDeviceConfigChanged;
        }

        private void OnDisable()
        {
            InputDevices.deviceConnected -= OnInputDeviceConnected;
            InputDevices.deviceConfigChanged -= OnInputDeviceConfigChanged;
        }

        private void Update()
        {
            if (!_enableUiRayRecoveryWatchdog || Time.unscaledTime < _nextWatchdogAt)
            {
                return;
            }

            _nextWatchdogAt = Time.unscaledTime + Mathf.Max(0.25f, _uiRayRecoveryWatchdogIntervalSeconds);
            EnforceRuntimeUiRayPolicy("watchdog");
            RunUiRayRecoveryWatchdog("periodic");
        }

        private IEnumerator ConfigureAfterRigActivation()
        {
            yield return null;
            ConfigureNow("start_next_frame");
            yield return new WaitForSecondsRealtime(0.5f);
            ConfigureNow("start_delayed_half_second");
            yield return new WaitForSecondsRealtime(1.5f);
            ConfigureNow("start_delayed_two_seconds");
        }

        private void OnInputDeviceConnected(XRInputDevice device)
        {
            HandleInputDeviceRecoveryEvent(device, "device_connected");
        }

        private void OnInputDeviceConfigChanged(XRInputDevice device)
        {
            HandleInputDeviceRecoveryEvent(device, "device_config_changed");
        }

        private void RunUiRayRecoveryWatchdog(string reason)
        {
            LogHandTrackingDevicesIfPresent(reason);
            bool leftTracked = IsPhysicalControllerTracked(XRNode.LeftHand, out XRInputDevice leftDevice);
            bool rightTracked = IsPhysicalControllerTracked(XRNode.RightHand, out XRInputDevice rightDevice);
            bool leftReacquired = leftTracked && !_leftControllerTracked;
            bool rightReacquired = rightTracked && !_rightControllerTracked;
            if (leftReacquired)
            {
                LogPhysicalControllerReacquired(leftDevice, $"{reason}_left_reacquired");
            }

            if (rightReacquired)
            {
                LogPhysicalControllerReacquired(rightDevice, $"{reason}_right_reacquired");
            }

            _leftControllerTracked = leftTracked;
            _rightControllerTracked = rightTracked;

            if (leftReacquired && Time.unscaledTime >= _nextRecoveryAllowedAt)
            {
                AttemptUiRayRecovery($"{reason}_left_reacquired", leftDevice);
            }

            if (rightReacquired && Time.unscaledTime >= _nextRecoveryAllowedAt)
            {
                AttemptUiRayRecovery($"{reason}_right_reacquired", rightDevice);
            }

            bool recovered = EnsureUiRuntimeServices(reason, out bool recoveryNeeded);
            if (recoveryNeeded && (leftTracked || rightTracked))
            {
                AttemptUiRayRecovery(reason, leftTracked ? leftDevice : rightDevice);
            }
            else if (recoveryNeeded)
            {
                Log("ui_recovery_skipped_locomotion_guard", BuildRecoveryPayload(reason, default, "no_physical_controller_tracked"));
            }
            else if (!recovered)
            {
                Log("ui_recovery_failed", BuildRecoveryPayload(reason, default, "ui_runtime_services_failed"));
            }

            LogVisualWithoutUiHitIfNeeded(reason, leftTracked || rightTracked);
        }

        private void AttemptUiRayRecovery(string reason, XRInputDevice device)
        {
            if (!IsPhysicalControllerDevice(device) && !HasAnyPhysicalControllerTracked(out device))
            {
                Log("ui_recovery_skipped_not_physical_controller", BuildRecoveryPayload(reason, device, "not_physical_controller_or_hand_tracking"));
                return;
            }

            if (!IsDeviceTracked(device))
            {
                Log("ui_recovery_skipped_locomotion_guard", BuildRecoveryPayload(reason, device, "physical_controller_not_tracked"));
                return;
            }

            if (Time.unscaledTime < _nextRecoveryAllowedAt)
            {
                Log("ui_recovery_skipped_locomotion_guard", BuildRecoveryPayload(reason, device, "recovery_debounce_active"));
                return;
            }

            _nextRecoveryAllowedAt = Time.unscaledTime + RecoveryCooldownSeconds;
            Log("ui_ray_recovery_attempted", BuildRecoveryPayload(reason, device, string.Empty));
            bool servicesOk = EnsureUiRuntimeServices(reason, out _);
            bool raysOk = ShouldDisableRuntimeUiRayCreationOnThisPlatform()
                ? HasActiveNativeXriUiVisual()
                : HasActiveRuntimeUiRayOrManualLaser();
            bool canvasOk = _targetCanvas != null &&
                _targetCanvas.GetComponent<TrackedDeviceGraphicRaycaster>() != null &&
                _targetCanvas.GetComponent<TrackedDeviceGraphicRaycaster>().enabled;
            bool succeeded = servicesOk && raysOk && canvasOk && (_leftControllerTracked || _rightControllerTracked);
            Log(
                succeeded ? "ui_ray_recovery_succeeded" : "ui_recovery_failed",
                BuildRecoveryPayload(reason, device, succeeded ? string.Empty : "post_recovery_validation_failed"));
        }

        private bool EnsureUiRuntimeServices(string reason, out bool recoveryNeeded)
        {
            recoveryNeeded = false;
            ResolveCanvas();
            EventSystem eventSystem = EventSystem.current ?? FindFirstObjectByType<EventSystem>();
            if (eventSystem == null)
            {
                eventSystem = new GameObject("EventSystem").AddComponent<EventSystem>();
                recoveryNeeded = true;
            }

            if (!eventSystem.gameObject.activeSelf)
            {
                eventSystem.gameObject.SetActive(true);
                recoveryNeeded = true;
            }

            XRUIInputModule xrInputModule = eventSystem.GetComponent<XRUIInputModule>();
            if (xrInputModule == null)
            {
                xrInputModule = eventSystem.gameObject.AddComponent<XRUIInputModule>();
                recoveryNeeded = true;
            }

            if (!xrInputModule.enabled)
            {
                xrInputModule.enabled = true;
                recoveryNeeded = true;
                Log("ui_input_module_reenabled", BuildRecoveryPayload(reason, default, "xr_ui_input_module_was_disabled"));
            }

            StandaloneInputModule standalone = eventSystem.GetComponent<StandaloneInputModule>();
            if (standalone != null && standalone.enabled)
            {
                standalone.enabled = false;
                recoveryNeeded = true;
            }

            LogInputActionManagerAudit(reason);

            bool canvasOk = EnsureCanvasRaycasterChecked(reason);
            return eventSystem.isActiveAndEnabled && xrInputModule.isActiveAndEnabled && canvasOk;
        }

        private void LogInputActionManagerAudit(string reason)
        {
            if (_inputActionManagerAuditLogged && !_enableVerboseXrUiRayDiagnostics)
            {
                return;
            }

            _inputActionManagerAuditLogged = true;
            int managerCount = 0;
            int enabledCount = 0;
            int activeCount = 0;
            var states = new List<string>();
            foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (behaviour == null)
                {
                    continue;
                }

                Type type = behaviour.GetType();
                if (!string.Equals(type.Name, "InputActionManager", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                managerCount++;
                if (behaviour.enabled)
                {
                    enabledCount++;
                }

                if (behaviour.gameObject.activeInHierarchy)
                {
                    activeCount++;
                }

                if (states.Count < 12)
                {
                    states.Add($"{GetPath(behaviour.transform)}:enabled={behaviour.enabled}:active={behaviour.gameObject.activeInHierarchy}");
                }
            }

            Log("ui_input_action_manager_audit", new Dictionary<string, object>
            {
                ["reason"] = reason ?? string.Empty,
                ["manager_count"] = managerCount,
                ["enabled_count"] = enabledCount,
                ["active_count"] = activeCount,
                ["manager_states"] = states.Count == 0 ? "none" : string.Join(" || ", states),
                ["read_only"] = true,
                ["input_actions_reenabled"] = false
            });
        }

        private void LogLocomotionAudit(string reason)
        {
            if (_locomotionAuditLogged && !_enableVerboseXrUiRayDiagnostics)
            {
                return;
            }

            _locomotionAuditLogged = true;
            int providerCount = 0;
            int characterControllerCount = 0;
            var providerStates = new List<string>();
            var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Behaviour behaviour in FindObjectsByType<Behaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (behaviour == null || !IsUserLocomotionAuditCandidate(behaviour))
                {
                    continue;
                }

                providerCount++;
                string typeName = behaviour.GetType().Name;
                typeCounts[typeName] = typeCounts.TryGetValue(typeName, out int count) ? count + 1 : 1;
                if (providerStates.Count < 16)
                {
                    providerStates.Add($"{typeName}:{GetPath(behaviour.transform)}:enabled={behaviour.enabled}:active={behaviour.gameObject.activeInHierarchy}");
                }
            }

            foreach (CharacterController controller in FindObjectsByType<CharacterController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (controller != null)
                {
                    characterControllerCount++;
                }
            }

            var duplicates = new List<string>();
            foreach (KeyValuePair<string, int> entry in typeCounts)
            {
                if (entry.Value > 1)
                {
                    duplicates.Add($"{entry.Key}={entry.Value}");
                }
            }

            Log("ui_locomotion_guard_audit", new Dictionary<string, object>
            {
                ["reason"] = reason ?? string.Empty,
                ["provider_count"] = providerCount,
                ["provider_states"] = providerStates.Count == 0 ? "none" : string.Join(" || ", providerStates),
                ["duplicate_provider_types"] = duplicates.Count == 0 ? "none" : string.Join(",", duplicates),
                ["character_controller_count"] = characterControllerCount,
                ["read_only"] = true,
                ["locomotion_modified"] = false,
                ["input_actions_reenabled"] = false
            });
        }

        private static bool IsUserLocomotionAuditCandidate(Behaviour behaviour)
        {
            if (behaviour == null)
            {
                return false;
            }

            string typeName = behaviour.GetType().Name;
            return typeName.IndexOf("ContinuousMove", StringComparison.OrdinalIgnoreCase) >= 0 ||
                typeName.IndexOf("ContinuousTurn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                typeName.IndexOf("SnapTurn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                typeName.IndexOf("TurnProvider", StringComparison.OrdinalIgnoreCase) >= 0 ||
                typeName.IndexOf("MoveProvider", StringComparison.OrdinalIgnoreCase) >= 0 ||
                typeName.IndexOf("LocomotionProvider", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool EnsureCanvasRaycasterChecked(string reason)
        {
            ResolveCanvas();
            bool canvasFound = _targetCanvas != null;
            bool raycasterCreated = false;
            bool raycasterReenabled = false;
            bool eventCameraAssigned = false;
            TrackedDeviceGraphicRaycaster raycaster = null;
            if (_targetCanvas != null)
            {
                raycaster = _targetCanvas.GetComponent<TrackedDeviceGraphicRaycaster>();
                if (raycaster == null)
                {
                    raycaster = _targetCanvas.gameObject.AddComponent<TrackedDeviceGraphicRaycaster>();
                    raycasterCreated = true;
                }

                if (!raycaster.enabled)
                {
                    raycaster.enabled = true;
                    raycasterReenabled = true;
                }

                raycaster.ignoreReversedGraphics = false;
                raycaster.checkFor3DOcclusion = false;
                raycaster.checkFor2DOcclusion = false;

                EnsureEventCamera(Camera.main ?? FindFirstObjectByType<Camera>(), out eventCameraAssigned);
            }

            string canvasPath = _targetCanvas != null ? GetPath(_targetCanvas.transform) : string.Empty;
            bool raycasterEnabled = raycaster != null && raycaster.enabled;
            bool stateChanged = !_hasLoggedCanvasRaycasterState ||
                canvasFound != _lastCanvasFound ||
                raycasterEnabled != _lastCanvasRaycasterEnabled ||
                !string.Equals(canvasPath, _lastCanvasPath, StringComparison.Ordinal);
            if (stateChanged || raycasterCreated || raycasterReenabled || eventCameraAssigned)
            {
                Log("ui_canvas_raycaster_checked", new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["canvas_found"] = canvasFound,
                    ["canvas_path"] = canvasPath,
                    ["raycaster_created"] = raycasterCreated,
                    ["raycaster_reenabled"] = raycasterReenabled,
                    ["event_camera_assigned"] = eventCameraAssigned
                });
            }

            _hasLoggedCanvasRaycasterState = true;
            _lastCanvasFound = canvasFound;
            _lastCanvasRaycasterEnabled = raycasterEnabled;
            _lastCanvasPath = canvasPath;
            return raycasterEnabled;
        }

        private bool HasActiveRuntimeUiRayOrManualLaser()
        {
            foreach (XRRayInteractor ray in FindObjectsByType<XRRayInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (ray != null && IsRuntimeUiRayVisualCandidate(ray.transform) && ray.isActiveAndEnabled && ray.enableUIInteraction)
                {
                    return true;
                }
            }

            foreach (ExperimentRuntimeManualUiLaserPointer pointer in
                     FindObjectsByType<ExperimentRuntimeManualUiLaserPointer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (pointer != null && pointer.isActiveAndEnabled && pointer.LineVisible)
                {
                    return true;
                }
            }

            return false;
        }

        private void LogVisualWithoutUiHitIfNeeded(string reason, bool controllerTracked)
        {
            if (!controllerTracked || Time.unscaledTime - _lastVisualWithoutHitLogAt < 3f)
            {
                return;
            }

            bool visualVisible = false;
            bool hasUiHit = false;
            foreach (ExperimentRuntimeManualUiLaserPointer pointer in
                     FindObjectsByType<ExperimentRuntimeManualUiLaserPointer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (pointer == null || !pointer.isActiveAndEnabled)
                {
                    continue;
                }

                visualVisible |= pointer.LineVisible;
                hasUiHit |= pointer.HasUiHit;
            }

            if (!visualVisible || hasUiHit)
            {
                return;
            }

            _lastVisualWithoutHitLogAt = Time.unscaledTime;
            Log("ui_ray_visual_without_ui_hit", BuildRecoveryPayload(reason, default, "manual_line_visible_without_button_hit"));
        }

        private void HandleInputDeviceRecoveryEvent(XRInputDevice device, string reason)
        {
            if (IsHandTrackingDevice(device))
            {
                Log("hand_tracking_detected_no_ui_recovery", BuildRecoveryPayload(reason, device, "hand_tracking_device"));
                return;
            }

            if (!IsPhysicalControllerDevice(device))
            {
                Log("ui_recovery_skipped_not_physical_controller", BuildRecoveryPayload(reason, device, "device_is_not_physical_controller"));
                return;
            }

            if (!IsDeviceTracked(device))
            {
                Log("ui_recovery_skipped_locomotion_guard", BuildRecoveryPayload(reason, device, "physical_controller_not_tracked"));
                return;
            }

            LogPhysicalControllerReacquired(device, reason);
            AttemptUiRayRecovery(reason, device);
        }

        private void LogHandTrackingDevicesIfPresent(string reason)
        {
            if (!QuestLoggingPolicy.EmitLegacyContinuousDiagnostics ||
                Time.unscaledTime - _lastHandTrackingSkipLogAt < HandTrackingSkipLogCooldownSeconds)
            {
                return;
            }

            var devices = new List<XRInputDevice>();
            InputDevices.GetDevices(devices);
            foreach (XRInputDevice device in devices)
            {
                if (!IsHandTrackingDevice(device))
                {
                    continue;
                }

                _lastHandTrackingSkipLogAt = Time.unscaledTime;
                Log("hand_tracking_detected_no_ui_recovery", BuildRecoveryPayload(reason, device, "hand_tracking_periodic"));
                return;
            }
        }

        private static bool IsPhysicalControllerTracked(XRNode node, out XRInputDevice device)
        {
            device = InputDevices.GetDeviceAtXRNode(node);
            if (!device.isValid || !IsPhysicalControllerDevice(device))
            {
                return false;
            }

            return IsDeviceTracked(device);
        }

        private static bool HasAnyPhysicalControllerTracked(out XRInputDevice device)
        {
            if (IsPhysicalControllerTracked(XRNode.LeftHand, out device))
            {
                return true;
            }

            if (IsPhysicalControllerTracked(XRNode.RightHand, out device))
            {
                return true;
            }

            device = default;
            return false;
        }

        private static bool IsDeviceTracked(XRInputDevice device)
        {
            if (!device.isValid)
            {
                return false;
            }

            if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out bool isTracked))
            {
                return isTracked;
            }

            return true;
        }

        private static bool IsPhysicalControllerDevice(XRInputDevice device)
        {
            if (!device.isValid)
            {
                return false;
            }

            InputDeviceCharacteristics characteristics = device.characteristics;
            if ((characteristics & InputDeviceCharacteristics.Controller) == 0)
            {
                return false;
            }

            if ((characteristics & InputDeviceCharacteristics.HandTracking) != 0)
            {
                return false;
            }

            if (LooksLikeHandTrackingDeviceName(device.name))
            {
                return false;
            }

            bool heldInHand = (characteristics & InputDeviceCharacteristics.HeldInHand) != 0;
            bool trackedDevice = (characteristics & InputDeviceCharacteristics.TrackedDevice) != 0;
            bool namedController = LooksLikePhysicalControllerName(device.name);
            return (heldInHand || namedController) && (trackedDevice || namedController);
        }

        private static bool IsHandTrackingDevice(XRInputDevice device)
        {
            if (!device.isValid)
            {
                return false;
            }

            InputDeviceCharacteristics characteristics = device.characteristics;
            return (characteristics & InputDeviceCharacteristics.HandTracking) != 0 ||
                LooksLikeHandTrackingDeviceName(device.name);
        }

        private static bool LooksLikeHandTrackingDeviceName(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return false;
            }

            string name = deviceName.ToLowerInvariant();
            return name.Contains("hand interaction") ||
                name.Contains("palm pose") ||
                name.Contains("hand tracking") ||
                name.Contains("handtracking") ||
                name.Contains("pinch") ||
                name.Contains("poke") ||
                (name.Contains("hand") && !name.Contains("controller"));
        }

        private static bool LooksLikePhysicalControllerName(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return false;
            }

            string name = deviceName.ToLowerInvariant();
            return name.Contains("controller") ||
                name.Contains("touch") ||
                name.Contains("quest") ||
                name.Contains("oculus");
        }

        private void LogPhysicalControllerReacquired(XRInputDevice device, string reason)
        {
            Log("physical_controller_reacquired", BuildRecoveryPayload(reason, device, string.Empty));
            Log("ui_controller_reacquired", BuildRecoveryPayload(reason, device, string.Empty));
        }

        private Dictionary<string, object> BuildRecoveryPayload(string reason, XRInputDevice device, string failureReason)
        {
            return new Dictionary<string, object>
            {
                ["reason"] = reason ?? string.Empty,
                ["failure_reason"] = failureReason ?? string.Empty,
                ["device_valid"] = device.isValid,
                ["device_name"] = device.isValid ? device.name : string.Empty,
                ["device_characteristics"] = device.isValid ? device.characteristics.ToString() : string.Empty,
                ["left_controller_tracked"] = _leftControllerTracked,
                ["right_controller_tracked"] = _rightControllerTracked,
                ["event_system_found"] = (EventSystem.current ?? FindFirstObjectByType<EventSystem>()) != null,
                ["xr_ui_input_module_enabled"] = EventSystem.current != null &&
                    EventSystem.current.GetComponent<XRUIInputModule>() != null &&
                    EventSystem.current.GetComponent<XRUIInputModule>().enabled,
                ["canvas_found"] = _targetCanvas != null,
                ["canvas_path"] = _targetCanvas != null ? GetPath(_targetCanvas.transform) : string.Empty,
                ["tracked_device_graphic_raycaster_enabled"] = _targetCanvas != null &&
                    _targetCanvas.GetComponent<TrackedDeviceGraphicRaycaster>() != null &&
                    _targetCanvas.GetComponent<TrackedDeviceGraphicRaycaster>().enabled,
                ["runtime_ui_ray_or_manual_laser_active"] = HasActiveRuntimeUiRayOrManualLaser(),
                ["ui_only"] = true,
                ["remote_box_manipulation_enabled"] = false
            };
        }

        private void ConfigureNow(string phase)
        {
            _configureInvocation++;
            ResolveCanvas();
            Transform xrOrigin = FindNamedTransform("XR Origin") ?? FindNamedTransform("XR Rig");
            Transform cameraOffset = FindCameraOffset(xrOrigin);
            Camera camera = Camera.main ?? FindFirstObjectByType<Camera>();
            int headCameraRaysRemoved = CleanupHeadCameraRays(camera);

            List<ParentCandidate> leftCandidates = CollectParentCandidates(true, xrOrigin, cameraOffset);
            List<ParentCandidate> rightCandidates = CollectParentCandidates(false, xrOrigin, cameraOffset);

            bool logPhase = ShouldLogPhase(phase);
            if (logPhase)
            {
                LogStarted(phase, xrOrigin, cameraOffset, leftCandidates, rightCandidates);
            }

            EnsureCanvasCanReceiveTrackedDeviceRays(out bool canvasLayerChanged, out int uiLayer);
            EnsureEventCamera(camera, out bool eventCameraAssigned);
            NativeUiInteractorAudit nativeAudit = AuditNativeUiInteractors();
            LogNativeUiInteractorAudit(phase, nativeAudit, logPhase);
            bool runtimeRayCreationDisabled = ShouldDisableRuntimeUiRayCreationOnThisPlatform();
            if (runtimeRayCreationDisabled || nativeAudit.ActiveUiInteractorCount > 0)
            {
                string skipReason = runtimeRayCreationDisabled
                    ? "runtime_ray_creation_disabled_on_quest"
                    : "native_xri_ui_interactors_available";
                int disabledArtifacts = EnforceRuntimeUiRayPolicy(skipReason);
                VisualConsolidationResult nativeConsolidation = SelectNativeXriVisualOwner(skipReason);
                VisualAudit visualAudit = AuditRayVisuals();
                RaySetupResult skippedLeft = RaySetupResult.Skipped(skipReason);
                RaySetupResult skippedRight = RaySetupResult.Skipped(skipReason);
                LogP46D("ui_runtime_ray_creation_skipped", new Dictionary<string, object>
                {
                    ["phase"] = phase,
                    ["reason"] = skipReason,
                    ["native_ui_interactor_count"] = nativeAudit.ActiveUiInteractorCount,
                    ["native_ui_interactors"] = nativeAudit.Describe(),
                    ["runtime_artifacts_disabled"] = disabledArtifacts,
                    ["selected_visual_owner"] = nativeConsolidation.SelectedVisualOwner,
                    ["xri_line_visual_enabled"] = nativeConsolidation.XriLineVisualEnabled,
                    ["manual_laser_enabled"] = nativeConsolidation.ManualLaserEnabled,
                    ["disable_runtime_ui_ray_creation_on_quest"] = _disableRuntimeUiRayCreationOnQuest,
                    ["ui_only"] = true,
                    ["locomotion_modified"] = false
                });

                if (logPhase)
                {
                    LogResult(
                        phase,
                        ParentResolution.None(skipReason),
                        ParentResolution.None(skipReason),
                        skippedLeft,
                        skippedRight,
                        canvasLayerChanged,
                        eventCameraAssigned,
                        uiLayer,
                        EventSystem.current != null &&
                            EventSystem.current.GetComponent<XRUIInputModule>() != null &&
                            EventSystem.current.GetComponent<XRUIInputModule>().enabled,
                        _targetCanvas != null &&
                            _targetCanvas.GetComponent<TrackedDeviceGraphicRaycaster>() != null &&
                            _targetCanvas.GetComponent<TrackedDeviceGraphicRaycaster>().enabled);
                    LogVisualAudit(visualAudit);
                }

                LogCleanup(headCameraRaysRemoved, skippedLeft, skippedRight);
                LogVisualConsolidated(visualAudit, nativeConsolidation, logPhase);
                return;
            }

            ParentResolution leftParent = ResolveRayParent(true, leftCandidates, cameraOffset);
            ParentResolution rightParent = ResolveRayParent(false, rightCandidates, cameraOffset);

            RaySetupResult left = CreateOrConfigureRay(leftParent, true, uiLayer);
            RaySetupResult right = CreateOrConfigureRay(rightParent, false, uiLayer);
            VisualAudit visualAuditBefore = AuditRayVisuals();
            VisualConsolidationResult visualConsolidation = ConsolidateVisibleRayVisuals();
            VisualAudit visualAuditAfter = AuditRayVisuals();

            bool hasXrInputModule = EventSystem.current != null &&
                EventSystem.current.GetComponent<XRUIInputModule>() != null &&
                EventSystem.current.GetComponent<XRUIInputModule>().enabled;
            bool hasTrackedRaycaster = _targetCanvas != null &&
                _targetCanvas.GetComponent<TrackedDeviceGraphicRaycaster>() != null &&
                _targetCanvas.GetComponent<TrackedDeviceGraphicRaycaster>().enabled;

            if (logPhase)
            {
                LogResult(
                    phase,
                    leftParent,
                    rightParent,
                    left,
                    right,
                    canvasLayerChanged,
                    eventCameraAssigned,
                    uiLayer,
                    hasXrInputModule,
                    hasTrackedRaycaster);
            }
            if (logPhase)
            {
                LogVisualAudit(visualAuditBefore);
            }
            LogCleanup(headCameraRaysRemoved, left, right);
            LogVisualConsolidated(visualAuditAfter, visualConsolidation, logPhase);
        }

        private bool ShouldLogPhase(string phase)
        {
            return _enableVerboseXrUiRayDiagnostics ||
                string.Equals(phase, "ensure_attached", StringComparison.Ordinal) ||
                string.Equals(phase, "start_delayed_two_seconds", StringComparison.Ordinal);
        }

        private bool ShouldDisableRuntimeUiRayCreationOnThisPlatform()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return _disableRuntimeUiRayCreationOnQuest;
#else
            return false;
#endif
        }

        private bool HasActiveNativeXriUiVisual()
        {
            foreach (XRInteractorLineVisual lineVisual in FindObjectsByType<XRInteractorLineVisual>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (lineVisual == null ||
                    IsRuntimeCreatedUiArtifact(lineVisual.transform) ||
                    IsForbiddenRuntimeRayParent(lineVisual.transform, GetPath(lineVisual.transform)))
                {
                    continue;
                }

                bool nativeOwner = lineVisual.GetComponentInParent<NearFarInteractor>(true) != null ||
                    lineVisual.GetComponentInParent<XRRayInteractor>(true) != null;
                if (nativeOwner && lineVisual.isActiveAndEnabled)
                {
                    return true;
                }
            }

            return false;
        }

        private NativeUiInteractorAudit AuditNativeUiInteractors()
        {
            var audit = new NativeUiInteractorAudit();
            foreach (NearFarInteractor interactor in FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (interactor == null || IsRuntimeCreatedUiArtifact(interactor.transform) || IsForbiddenRuntimeRayParent(interactor.transform, GetPath(interactor.transform)))
                {
                    continue;
                }

                audit.NearFarCount++;
                if (interactor.isActiveAndEnabled)
                {
                    audit.ActiveUiInteractorCount++;
                }

                audit.AddCandidate(interactor, "NearFarInteractor");
            }

            foreach (XRRayInteractor interactor in FindObjectsByType<XRRayInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (interactor == null ||
                    IsRuntimeCreatedUiArtifact(interactor.transform) ||
                    IsForbiddenRuntimeRayParent(interactor.transform, GetPath(interactor.transform)) ||
                    interactor.transform.name.IndexOf("Teleport", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                audit.XrRayCount++;
                if (interactor.isActiveAndEnabled && interactor.enableUIInteraction)
                {
                    audit.ActiveUiInteractorCount++;
                }

                audit.AddCandidate(interactor, $"XRRayInteractor:ui={interactor.enableUIInteraction}");
            }

            return audit;
        }

        private void LogNativeUiInteractorAudit(string phase, NativeUiInteractorAudit audit, bool force)
        {
            if (!force && !_enableVerboseXrUiRayDiagnostics)
            {
                return;
            }

            Log("ui_native_xri_interactor_audit", new Dictionary<string, object>
            {
                ["phase"] = phase ?? string.Empty,
                ["near_far_count"] = audit.NearFarCount,
                ["xr_ray_count"] = audit.XrRayCount,
                ["active_ui_interactor_count"] = audit.ActiveUiInteractorCount,
                ["candidates"] = audit.Describe(),
                ["read_only"] = true,
                ["locomotion_modified"] = false
            });
        }

        private int EnforceRuntimeUiRayPolicy(string reason)
        {
            int disabled = 0;
            bool questPolicy = ShouldDisableRuntimeUiRayCreationOnThisPlatform();
            var disabledPaths = new List<string>();
            foreach (Transform transform in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (transform == null || transform == this.transform || !IsRuntimeCreatedUiArtifact(transform))
                {
                    continue;
                }

                XRRayInteractor ray = transform.GetComponent<XRRayInteractor>();
                if (ray != null && ray.enabled)
                {
                    ray.enabled = false;
                    disabled++;
                    AddDisabledPath(disabledPaths, transform);
                }

                XRInteractorLineVisual lineVisual = transform.GetComponent<XRInteractorLineVisual>();
                if (lineVisual != null && lineVisual.enabled)
                {
                    lineVisual.enabled = false;
                    disabled++;
                    AddDisabledPath(disabledPaths, transform);
                }

                LineRenderer lineRenderer = transform.GetComponent<LineRenderer>();
                if (lineRenderer != null && lineRenderer.enabled)
                {
                    lineRenderer.enabled = false;
                    disabled++;
                    AddDisabledPath(disabledPaths, transform);
                }

                ExperimentRuntimeManualUiLaserPointer manualLaser = transform.GetComponent<ExperimentRuntimeManualUiLaserPointer>();
                if (manualLaser != null && manualLaser.enabled)
                {
                    manualLaser.enabled = false;
                    disabled++;
                    AddDisabledPath(disabledPaths, transform);
                }

                if (transform.gameObject.activeSelf)
                {
                    transform.gameObject.SetActive(false);
                    disabled++;
                    AddDisabledPath(disabledPaths, transform);
                }
            }

            bool shouldLogPolicyState = (disabled > 0 || questPolicy) &&
                (disabled > 0 || !_hasLoggedRuntimeUiRayPolicyState || questPolicy != _lastRuntimeUiRayQuestPolicy);
            if (shouldLogPolicyState)
            {
                LogP46D("runtime_ui_rays_disabled_on_quest", new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["disabled_count"] = disabled,
                    ["disabled_paths"] = disabledPaths.Count == 0 ? "none" : string.Join(" || ", disabledPaths),
                    ["quest_policy_active"] = questPolicy,
                    ["ui_only"] = true,
                    ["locomotion_modified"] = false
                });
                LogP46D("xr_ui_runtime_ray_disabled", new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["disabled_count"] = disabled,
                    ["disabled_paths"] = disabledPaths.Count == 0 ? "none" : string.Join(" || ", disabledPaths),
                    ["quest_policy_active"] = questPolicy,
                    ["ui_only"] = true,
                    ["locomotion_modified"] = false
                });
            }

            _hasLoggedRuntimeUiRayPolicyState = true;
            _lastRuntimeUiRayQuestPolicy = questPolicy;

            return disabled;
        }

        private static void AddDisabledPath(List<string> disabledPaths, Transform transform)
        {
            if (disabledPaths == null || transform == null || disabledPaths.Count >= 18)
            {
                return;
            }

            string path = GetPath(transform);
            if (!disabledPaths.Contains(path))
            {
                disabledPaths.Add(path);
            }
        }

        private VisualConsolidationResult SelectNativeXriVisualOwner(string reason)
        {
            var result = new VisualConsolidationResult
            {
                SelectedVisualOwner = "NativeXRI"
            };

            foreach (ExperimentRuntimeManualUiLaserPointer pointer in
                     FindObjectsByType<ExperimentRuntimeManualUiLaserPointer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (pointer == null)
                {
                    continue;
                }

                if (pointer.enabled)
                {
                    pointer.enabled = false;
                    result.DuplicatesRemovedOrDisabled++;
                }

                LineRenderer line = pointer.GetComponent<LineRenderer>();
                if (line != null && line.enabled)
                {
                    line.enabled = false;
                    result.LineRenderersDisabled++;
                }
            }

            int enabledNativeVisuals = 0;
            foreach (XRInteractorLineVisual lineVisual in FindObjectsByType<XRInteractorLineVisual>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (lineVisual == null || IsRuntimeCreatedUiArtifact(lineVisual.transform) || IsForbiddenRuntimeRayParent(lineVisual.transform, GetPath(lineVisual.transform)))
                {
                    continue;
                }

                bool nativeUiOwner = lineVisual.GetComponentInParent<NearFarInteractor>(true) != null ||
                    lineVisual.GetComponentInParent<XRRayInteractor>(true) != null;
                if (!nativeUiOwner)
                {
                    continue;
                }

                if (!lineVisual.enabled)
                {
                    lineVisual.enabled = true;
                }

                enabledNativeVisuals++;
                result.XriLineVisualEnabled = true;
            }

            foreach (LineRenderer lineRenderer in FindObjectsByType<LineRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (lineRenderer == null || !IsRuntimeCreatedUiArtifact(lineRenderer.transform))
                {
                    continue;
                }

                if (lineRenderer.enabled)
                {
                    lineRenderer.enabled = false;
                    result.LineRenderersDisabled++;
                }
            }

            result.ManualLaserEnabled = false;
            LogP46D("xr_ui_ray_owner_selected", new Dictionary<string, object>
            {
                ["reason"] = reason ?? string.Empty,
                ["selected_visual_owner"] = result.SelectedVisualOwner,
                ["native_xri_line_visuals_enabled"] = enabledNativeVisuals,
                ["manual_laser_enabled"] = false,
                ["runtime_rays_enabled"] = false,
                ["ui_only"] = true,
                ["locomotion_modified"] = false
            });
            return result;
        }

        private void ResolveCanvas()
        {
            if (_targetCanvas != null)
            {
                return;
            }

            _targetCanvas = GetComponentInParent<Canvas>();
            if (_targetCanvas != null)
            {
                return;
            }

            ExperimentRuntimeProtocolUI protocolUi = FindFirstObjectByType<ExperimentRuntimeProtocolUI>();
            if (protocolUi != null)
            {
                _targetCanvas = protocolUi.GetComponent<Canvas>();
            }
        }

        private void EnsureCanvasCanReceiveTrackedDeviceRays(out bool canvasLayerChanged, out int uiLayer)
        {
            canvasLayerChanged = false;
            uiLayer = LayerMask.NameToLayer("UI");
            if (_targetCanvas == null || uiLayer < 0)
            {
                return;
            }

            foreach (Transform child in _targetCanvas.GetComponentsInChildren<Transform>(true))
            {
                if (child.gameObject.layer == uiLayer)
                {
                    continue;
                }

                child.gameObject.layer = uiLayer;
                canvasLayerChanged = true;
            }
        }

        private void EnsureEventCamera(Camera camera, out bool assigned)
        {
            assigned = false;
            if (_targetCanvas == null || _targetCanvas.renderMode != RenderMode.WorldSpace || _targetCanvas.worldCamera != null)
            {
                return;
            }

            if (camera == null)
            {
                return;
            }

            _targetCanvas.worldCamera = camera;
            assigned = true;
        }

        private List<ParentCandidate> CollectParentCandidates(bool left, Transform xrOrigin, Transform cameraOffset)
        {
            string side = left ? "Left" : "Right";
            var candidates = new List<ParentCandidate>();
            foreach (Transform candidate in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (candidate == null || !IsSideController(candidate.name, side))
                {
                    continue;
                }

                string path = GetPath(candidate);
                if (IsForbiddenRuntimeRayParent(candidate, path))
                {
                    candidates.Add(new ParentCandidate(candidate)
                    {
                        Components = DescribeXrComponents(candidate),
                        Decision = "discard_forbidden_runtime_ray_parent"
                    });
                    continue;
                }

                bool inExpectedRig =
                    path.IndexOf("XR Origin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("XR Rig", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("/XR/", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!inExpectedRig)
                {
                    continue;
                }

                var item = new ParentCandidate(candidate)
                {
                    Components = DescribeXrComponents(candidate),
                    Decision = candidate.gameObject.activeInHierarchy && PoseLooksPlausible(candidate)
                        ? "accept_active_pose_candidate"
                        : "discard_inactive_or_no_pose"
                };
                candidates.Add(item);
            }

            if (cameraOffset != null)
            {
                foreach (Transform child in cameraOffset.GetComponentsInChildren<Transform>(true))
                {
                    if (child == cameraOffset || !child.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    string childName = child.name;
                    if (childName.IndexOf(side, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    if (LooksLikeHandTrackingTransformName(childName))
                    {
                        continue;
                    }

                    string childPath = GetPath(child);
                    if (IsForbiddenRuntimeRayParent(child, childPath) || !IsSideController(child.name, side))
                    {
                        continue;
                    }

                    candidates.Add(new ParentCandidate(child)
                    {
                        Components = DescribeXrComponents(child),
                        Decision = "fallback_active_child_under_camera_offset"
                    });
                }
            }

            return candidates;
        }

        private ParentResolution ResolveRayParent(bool left, List<ParentCandidate> candidates, Transform cameraOffset)
        {
            foreach (ParentCandidate candidate in candidates)
            {
                if (candidate.Transform != null &&
                    !IsMainCameraOrDescendant(candidate.Transform) &&
                    IsSideController(candidate.Transform.name, left ? "Left" : "Right") &&
                    !IsForbiddenRuntimeRayParent(candidate.Transform, GetPath(candidate.Transform)) &&
                    candidate.Transform.gameObject.activeInHierarchy &&
                    PoseLooksPlausible(candidate.Transform))
                {
                    return new ParentResolution(candidate.Transform, candidate.Transform, "active_stable_controller_with_pose", false);
                }
            }

            return ParentResolution.None("no_stable_controller_parent");
        }

        private RaySetupResult CreateOrConfigureRay(ParentResolution parent, bool left, int uiLayer)
        {
            var result = new RaySetupResult();
            if (parent.Parent == null || !parent.Parent.gameObject.activeInHierarchy)
            {
                result.FailureReason = "no_active_parent";
                return result;
            }
            if (IsMainCameraOrDescendant(parent.Parent))
            {
                result.FailureReason = "head_camera_parent_rejected";
                return result;
            }
            if (IsForbiddenRuntimeRayParent(parent.Parent, GetPath(parent.Parent)))
            {
                result.FailureReason = "forbidden_runtime_ray_parent";
                DisableExistingRuntimeRayChild(parent.Parent, left ? LeftRayName : RightRayName, result.FailureReason);
                Log("ui_runtime_ray_parent_rejected", BuildRuntimeRayParentPayload(parent.Parent, parent.PoseSource, result.FailureReason));
                return result;
            }
            if (!ValidateRuntimeRayParentOrientation(parent.Parent, out float cameraDot, out string orientationReason))
            {
                result.FailureReason = orientationReason;
                DisableExistingRuntimeRayChild(parent.Parent, left ? LeftRayName : RightRayName, result.FailureReason);
                Log("ui_runtime_ray_orientation_rejected", BuildRuntimeRayParentPayload(parent.Parent, parent.PoseSource, result.FailureReason, cameraDot));
                return result;
            }

            string rayName = left ? LeftRayName : RightRayName;
            Transform rayTransform = parent.Parent.Find(rayName);
            if (rayTransform == null)
            {
                var rayObject = new GameObject(rayName);
                rayObject.SetActive(false);
                rayObject.transform.SetParent(parent.Parent, false);
                rayTransform = rayObject.transform;
                result.Created = true;
            }

            rayTransform.localPosition = Vector3.zero;
            rayTransform.localRotation = Quaternion.identity;
            rayTransform.localScale = Vector3.one;
            rayTransform.gameObject.SetActive(false);

            XRRayInteractor ray = rayTransform.GetComponent<XRRayInteractor>();
            if (ray == null)
            {
                ray = rayTransform.gameObject.AddComponent<XRRayInteractor>();
                result.InteractorCreated = true;
            }

            ConfigureRay(ray, left, uiLayer, result);

            LineRenderer xriLineRenderer = rayTransform.GetComponent<LineRenderer>();
            if (xriLineRenderer == null)
            {
                xriLineRenderer = rayTransform.gameObject.AddComponent<LineRenderer>();
                result.LineRendererCreated = true;
            }

            ConfigureLineRenderer(xriLineRenderer);
            xriLineRenderer.enabled = false;

            XRInteractorLineVisual lineVisual = rayTransform.GetComponent<XRInteractorLineVisual>();
            if (lineVisual == null)
            {
                lineVisual = rayTransform.gameObject.AddComponent<XRInteractorLineVisual>();
                result.LineVisualCreated = true;
            }

            ConfigureLineVisual(lineVisual);
            lineVisual.enabled = false;
            ConfigureManualVisual(rayTransform, parent, left, result);

            rayTransform.gameObject.SetActive(true);
            result.Success = ray.isActiveAndEnabled;
            result.RayPath = GetPath(rayTransform);
            result.ActiveSelf = rayTransform.gameObject.activeSelf;
            result.ActiveInHierarchy = rayTransform.gameObject.activeInHierarchy;
            result.ComponentType = ray.GetType().FullName;
            result.EnableUiInteraction = ray.enableUIInteraction;
            result.InteractionLayers = ray.interactionLayers.value;
            result.RaycastMask = ray.raycastMask.value;
            result.XriRayEnabled = ray.enabled && ray.gameObject.activeInHierarchy;
            result.LineVisualEnabled = lineVisual.enabled && lineVisual.gameObject.activeInHierarchy;
            result.LineRendererEnabled = xriLineRenderer.enabled && xriLineRenderer.gameObject.activeInHierarchy;
            return result;
        }

        private VisualAudit AuditRayVisuals()
        {
            var audit = new VisualAudit();
            foreach (LineRenderer lineRenderer in FindObjectsByType<LineRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (lineRenderer == null || !IsRuntimeCreatedUiArtifact(lineRenderer.transform))
                {
                    continue;
                }

                string path = GetPath(lineRenderer.transform);
                string candidate = DescribeVisualCandidate(lineRenderer, path);
                if (IsMainCameraOrDescendant(lineRenderer.transform))
                {
                    audit.HeadCandidates.Add(candidate);
                    if (lineRenderer.enabled && lineRenderer.gameObject.activeInHierarchy)
                    {
                        audit.HeadCameraVisualCount++;
                    }
                }
                else if (IsLeftRuntimeUiVisual(path))
                {
                    audit.LeftLineRendererCount++;
                    audit.LeftVisualCandidates.Add(candidate);
                    if (lineRenderer.enabled && lineRenderer.gameObject.activeInHierarchy)
                    {
                        audit.LeftVisibleRayCount++;
                    }
                }
                else if (IsRightRuntimeUiVisual(path))
                {
                    audit.RightLineRendererCount++;
                    audit.RightVisualCandidates.Add(candidate);
                    if (lineRenderer.enabled && lineRenderer.gameObject.activeInHierarchy)
                    {
                        audit.RightVisibleRayCount++;
                    }
                }
            }

            foreach (XRInteractorLineVisual lineVisual in FindObjectsByType<XRInteractorLineVisual>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (lineVisual == null || !IsRuntimeCreatedUiArtifact(lineVisual.transform))
                {
                    continue;
                }

                string path = GetPath(lineVisual.transform);
                string candidate = DescribeVisualCandidate(lineVisual, path);
                if (IsMainCameraOrDescendant(lineVisual.transform))
                {
                    audit.HeadCandidates.Add(candidate);
                    if (lineVisual.enabled && lineVisual.gameObject.activeInHierarchy)
                    {
                        audit.HeadCameraVisualCount++;
                    }
                }
                else if (IsLeftRuntimeUiVisual(path))
                {
                    audit.LeftXriLineVisualCount++;
                    audit.LeftVisualCandidates.Add(candidate);
                    if (lineVisual.enabled && lineVisual.gameObject.activeInHierarchy)
                    {
                        audit.LeftVisibleRayCount++;
                    }
                }
                else if (IsRightRuntimeUiVisual(path))
                {
                    audit.RightXriLineVisualCount++;
                    audit.RightVisualCandidates.Add(candidate);
                    if (lineVisual.enabled && lineVisual.gameObject.activeInHierarchy)
                    {
                        audit.RightVisibleRayCount++;
                    }
                }
            }

            foreach (ExperimentRuntimeManualUiLaserPointer pointer in
                     FindObjectsByType<ExperimentRuntimeManualUiLaserPointer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (pointer == null)
                {
                    continue;
                }

                string path = GetPath(pointer.transform);
                string candidate = DescribeVisualCandidate(pointer, path);
                if (IsMainCameraOrDescendant(pointer.transform))
                {
                    audit.HeadCandidates.Add(candidate);
                    if (pointer.enabled && pointer.gameObject.activeInHierarchy)
                    {
                        audit.HeadCameraVisualCount++;
                    }
                }
                else if (IsLeftRuntimeUiVisual(path))
                {
                    audit.LeftManualLaserCount++;
                    audit.LeftVisualCandidates.Add(candidate);
                }
                else if (IsRightRuntimeUiVisual(path))
                {
                    audit.RightManualLaserCount++;
                    audit.RightVisualCandidates.Add(candidate);
                }
            }

            audit.DuplicatesDetected =
                audit.LeftVisibleRayCount > 1 ||
                audit.RightVisibleRayCount > 1 ||
                audit.HeadCameraVisualCount > 0 ||
                audit.LeftManualLaserCount > 1 ||
                audit.RightManualLaserCount > 1;
            return audit;
        }

        private VisualConsolidationResult ConsolidateVisibleRayVisuals()
        {
            var result = new VisualConsolidationResult();
            ExperimentRuntimeManualUiLaserPointer leftPointer = SelectManualPointer(true);
            ExperimentRuntimeManualUiLaserPointer rightPointer = SelectManualPointer(false);
            LineRenderer leftLine = leftPointer != null ? leftPointer.GetComponent<LineRenderer>() : null;
            LineRenderer rightLine = rightPointer != null ? rightPointer.GetComponent<LineRenderer>() : null;

            foreach (ExperimentRuntimeManualUiLaserPointer pointer in
                     FindObjectsByType<ExperimentRuntimeManualUiLaserPointer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (pointer == null)
                {
                    continue;
                }

                if (pointer == leftPointer || pointer == rightPointer)
                {
                    pointer.enabled = _enableManualFallback;
                    pointer.gameObject.SetActive(true);
                    result.ManualLaserEnabled = result.ManualLaserEnabled || pointer.enabled;
                    continue;
                }

                pointer.enabled = false;
                LineRenderer duplicateLine = pointer.GetComponent<LineRenderer>();
                if (duplicateLine != null && duplicateLine.enabled)
                {
                    duplicateLine.enabled = false;
                    result.LineRenderersDisabled++;
                }

                if (IsRuntimeUiRayVisualCandidate(pointer.transform))
                {
                    pointer.gameObject.SetActive(false);
                    result.DuplicatesRemovedOrDisabled++;
                }
            }

            foreach (LineRenderer lineRenderer in FindObjectsByType<LineRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (lineRenderer == null || !IsRuntimeUiRayVisualCandidate(lineRenderer.transform))
                {
                    continue;
                }

                if (lineRenderer == leftLine || lineRenderer == rightLine)
                {
                    lineRenderer.enabled = true;
                    ConfigureLineRenderer(lineRenderer);
                    continue;
                }

                if (lineRenderer.enabled)
                {
                    lineRenderer.enabled = false;
                    result.LineRenderersDisabled++;
                    result.DuplicatesRemovedOrDisabled++;
                }
            }

            foreach (XRInteractorLineVisual lineVisual in FindObjectsByType<XRInteractorLineVisual>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (lineVisual == null || !IsRuntimeUiRayVisualCandidate(lineVisual.transform))
                {
                    continue;
                }

                if (lineVisual.enabled)
                {
                    lineVisual.enabled = false;
                    result.XriVisualsDisabled++;
                    result.DuplicatesRemovedOrDisabled++;
                }
            }

            result.SelectedVisualOwner = "ExperimentRuntimeManualUiLaserPointer";
            result.XriLineVisualEnabled = false;
            return result;
        }

        private ExperimentRuntimeManualUiLaserPointer SelectManualPointer(bool left)
        {
            string preferredName = left ? LeftVisualName : RightVisualName;
            string side = left ? "Left" : "Right";
            ExperimentRuntimeManualUiLaserPointer best = null;
            int bestScore = int.MinValue;
            foreach (ExperimentRuntimeManualUiLaserPointer pointer in
                     FindObjectsByType<ExperimentRuntimeManualUiLaserPointer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (pointer == null || IsMainCameraOrDescendant(pointer.transform))
                {
                    continue;
                }

                string path = GetPath(pointer.transform);
                if (!IsRuntimeUiRayVisualCandidate(pointer.transform) || path.IndexOf(side, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                int score = 0;
                if (string.Equals(pointer.name, preferredName, StringComparison.OrdinalIgnoreCase))
                {
                    score += 100;
                }

                if (path.IndexOf("RuntimeUiRayInteractors", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 50;
                }

                if (pointer.gameObject.activeInHierarchy)
                {
                    score += 10;
                }

                if (score > bestScore)
                {
                    best = pointer;
                    bestScore = score;
                }
            }

            return best;
        }

        private void ConfigureRay(XRRayInteractor ray, bool left, int uiLayer, RaySetupResult result)
        {
            ray.handedness = left ? InteractorHandedness.Left : InteractorHandedness.Right;
            ray.lineType = XRRayInteractor.LineType.StraightLine;
            ray.maxRaycastDistance = _rayLength;
            ray.sampleFrequency = 2;
            ray.hitDetectionType = XRRayInteractor.HitDetectionType.Raycast;
            ray.raycastTriggerInteraction = QueryTriggerInteraction.Ignore;
            ray.raycastMask = uiLayer >= 0 ? 1 << uiLayer : 0;
            ray.interactionLayers = 0;
            ray.keepSelectedTargetValid = false;
            ray.disableVisualsWhenBlockedInGroup = false;
            ray.selectActionTrigger = XRBaseInputInteractor.InputTriggerType.StateChange;
            ray.allowHoveredActivate = false;
            ray.hitClosestOnly = true;
            ray.hoverToSelect = false;
            ray.autoDeselect = false;
            ray.enableUIInteraction = true;
            ray.blockUIOnInteractableSelection = false;
            ray.manipulateAttachTransform = false;
            ray.useForceGrab = false;
            ray.enableARRaycasting = false;
            ray.occludeARHitsWith3DObjects = false;
            ray.occludeARHitsWith2DObjects = false;

            XRInteractionManager manager = FindFirstObjectByType<XRInteractionManager>();
            if (manager != null)
            {
                ray.interactionManager = manager;
            }

            Component inputSource = FindBestInputSource(ray.transform.parent, left);
            if (inputSource != null)
            {
                CopyInputReaders(inputSource, ray, result);
            }
            else
            {
                result.InputCopySource = "none";
            }
        }

        private void ConfigureManualVisual(Transform rayTransform, ParentResolution parent, bool left, RaySetupResult result)
        {
            string visualName = left ? LeftVisualName : RightVisualName;
            Transform visualTransform = rayTransform.Find(visualName);
            if (visualTransform == null)
            {
                var visualObject = new GameObject(visualName);
                visualObject.transform.SetParent(rayTransform, false);
                visualTransform = visualObject.transform;
                result.ManualVisualCreated = true;
            }

            visualTransform.localPosition = Vector3.zero;
            visualTransform.localRotation = Quaternion.identity;
            visualTransform.localScale = Vector3.one;
            visualTransform.gameObject.SetActive(true);

            LineRenderer lineRenderer = visualTransform.GetComponent<LineRenderer>();
            if (lineRenderer == null)
            {
                lineRenderer = visualTransform.gameObject.AddComponent<LineRenderer>();
                result.ManualLineRendererCreated = true;
            }

            ConfigureLineRenderer(lineRenderer);

            ExperimentRuntimeManualUiLaserPointer pointer =
                visualTransform.GetComponent<ExperimentRuntimeManualUiLaserPointer>();
            if (pointer == null)
            {
                pointer = visualTransform.gameObject.AddComponent<ExperimentRuntimeManualUiLaserPointer>();
                result.ManualPointerCreated = true;
            }

            Transform poseSource = parent.PoseSource != null && !IsMainCameraOrDescendant(parent.PoseSource)
                ? parent.PoseSource
                : parent.Parent;

            pointer.Initialize(
                _targetCanvas,
                poseSource,
                lineRenderer,
                _rayLength,
                left,
                _enableManualFallback,
                _enableVerboseXrUiRayDiagnostics);
            pointer.enabled = _enableManualFallback;

            result.ManualFallbackEnabled = _enableManualFallback && pointer.isActiveAndEnabled;
            result.ManualLineVisible = lineRenderer.enabled && lineRenderer.gameObject.activeInHierarchy;
            result.ManualVisualPath = GetPath(visualTransform);
            result.ParentReason = parent.Reason;
            result.PoseSourcePath = parent.PoseSource != null ? GetPath(parent.PoseSource) : string.Empty;
        }

        private Component FindBestInputSource(Transform parent, bool left)
        {
            if (parent == null)
            {
                return null;
            }

            Component best = null;
            int bestScore = int.MinValue;
            foreach (Component component in parent.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component.transform == null)
                {
                    continue;
                }

                int score = ScoreInputSource(component, left);
                if (score > bestScore)
                {
                    best = component;
                    bestScore = score;
                }
            }

            return bestScore > 0 ? best : null;
        }

        private int CleanupHeadCameraRays(Camera camera)
        {
            if (camera == null)
            {
                return 0;
            }

            int removed = 0;
            foreach (Transform child in camera.GetComponentsInChildren<Transform>(true))
            {
                if (child == null || child == camera.transform)
                {
                    continue;
                }

                string name = child.name;
                bool isRuntimeRay =
                    string.Equals(name, LeftRayName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, RightRayName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, LeftVisualName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, RightVisualName, StringComparison.OrdinalIgnoreCase) ||
                    name.IndexOf("Runtime UI Ray", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("ManualUiLaser", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    child.GetComponent<ExperimentRuntimeManualUiLaserPointer>() != null;

                if (!isRuntimeRay)
                {
                    continue;
                }

                Destroy(child.gameObject);
                removed++;
            }

            return removed;
        }

        private void LogCleanup(int headCameraRaysRemoved, RaySetupResult left, RaySetupResult right)
        {
            if (_cleanupLogged)
            {
                return;
            }

            _cleanupLogged = true;
            int preserved = 0;
            if (!string.IsNullOrWhiteSpace(left.RayPath))
            {
                preserved++;
            }

            if (!string.IsNullOrWhiteSpace(right.RayPath))
            {
                preserved++;
            }

            Log("experiment_runtime_ui_ray_cleanup", new Dictionary<string, object>
            {
                ["head_camera_rays_removed"] = headCameraRaysRemoved,
                ["controller_rays_preserved"] = preserved,
                ["head_gaze_fallback_enabled"] = false,
                ["remote_box_manipulation_enabled"] = false,
                ["ui_only"] = true
            });
        }

        private void LogVisualAudit(VisualAudit audit)
        {
            Log("experiment_runtime_ui_ray_visual_audit", new Dictionary<string, object>
            {
                ["left_visual_candidates"] = string.Join(" || ", audit.LeftVisualCandidates),
                ["right_visual_candidates"] = string.Join(" || ", audit.RightVisualCandidates),
                ["head_camera_candidates"] = string.Join(" || ", audit.HeadCandidates),
                ["left_line_renderer_count"] = audit.LeftLineRendererCount,
                ["right_line_renderer_count"] = audit.RightLineRendererCount,
                ["left_xri_line_visual_count"] = audit.LeftXriLineVisualCount,
                ["right_xri_line_visual_count"] = audit.RightXriLineVisualCount,
                ["left_manual_laser_count"] = audit.LeftManualLaserCount,
                ["right_manual_laser_count"] = audit.RightManualLaserCount,
                ["head_camera_visual_count"] = audit.HeadCameraVisualCount,
                ["duplicates_detected"] = audit.DuplicatesDetected,
                ["remote_box_manipulation_enabled"] = false
            });
        }

        private void LogVisualConsolidated(VisualAudit audit, VisualConsolidationResult consolidation, bool logPhase)
        {
            if (_visualConsolidationLogged && !_enableVerboseXrUiRayDiagnostics && !logPhase)
            {
                return;
            }

            _visualConsolidationLogged = true;
            Log("experiment_runtime_ui_ray_visual_consolidated", new Dictionary<string, object>
            {
                ["left_visible_ray_count"] = audit.LeftVisibleRayCount,
                ["right_visible_ray_count"] = audit.RightVisibleRayCount,
                ["head_visible_ray_count"] = audit.HeadCameraVisualCount,
                ["manual_laser_enabled"] = consolidation.ManualLaserEnabled,
                ["xri_line_visual_enabled"] = consolidation.XriLineVisualEnabled,
                ["line_renderers_disabled"] = consolidation.LineRenderersDisabled,
                ["xri_visuals_disabled"] = consolidation.XriVisualsDisabled,
                ["duplicates_removed_or_disabled"] = consolidation.DuplicatesRemovedOrDisabled,
                ["selected_visual_owner"] = consolidation.SelectedVisualOwner,
                ["remote_box_manipulation_enabled"] = false
            });
        }

        private static bool IsRuntimeUiRayVisualCandidate(Transform transform)
        {
            if (transform == null)
            {
                return false;
            }

            string path = GetPath(transform);
            return path.IndexOf("RuntimeUiRayInteractors", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("Runtime UI Ray", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("ManualUiLaser", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("Near-Far Interactor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("Teleport Interactor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                transform.GetComponent<ExperimentRuntimeManualUiLaserPointer>() != null;
        }

        private static bool IsRuntimeCreatedUiArtifact(Transform transform)
        {
            if (transform == null)
            {
                return false;
            }

            string path = GetPath(transform);
            return path.IndexOf("RuntimeUiRayInteractors", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("Runtime UI Ray", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("ManualUiLaser", StringComparison.OrdinalIgnoreCase) >= 0 ||
                transform.GetComponent<ExperimentRuntimeManualUiLaserPointer>() != null;
        }

        private static bool IsLeftRuntimeUiVisual(string path)
        {
            return path.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsRightRuntimeUiVisual(string path)
        {
            return path.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string DescribeVisualCandidate(Component component, string path)
        {
            bool enabled = component is Behaviour behaviour ? behaviour.enabled : true;
            bool active = component != null && component.gameObject.activeInHierarchy;
            string parent = component != null && component.transform.parent != null ? GetPath(component.transform.parent) : string.Empty;
            bool underMainCamera = component != null && IsMainCameraOrDescendant(component.transform);
            bool underLeft = path.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0;
            bool underRight = path.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0;
            bool underRuntimeRoot = path.IndexOf("RuntimeUiRayInteractors", StringComparison.OrdinalIgnoreCase) >= 0;
            return $"path={path};component={component.GetType().Name};enabled={enabled};activeInHierarchy={active};parent={parent};under_main_camera={underMainCamera};under_left={underLeft};under_right={underRight};under_runtime_ui_ray_interactors={underRuntimeRoot}";
        }

        private static int ScoreInputSource(Component component, bool left)
        {
            Type type = component.GetType();
            string typeName = type.Name;
            string path = GetPath(component.transform);
            int score = 0;
            if (component is NearFarInteractor)
            {
                score += 80;
            }
            else if (component is XRRayInteractor)
            {
                score += 60;
            }
            else if (component is XRBaseInputInteractor)
            {
                score += 30;
            }

            if (score == 0)
            {
                return 0;
            }

            if (typeName.IndexOf("NearFar", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 10;
            }

            if (typeName.IndexOf("Teleport", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("Teleport", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score -= 20;
            }

            string side = left ? "Left" : "Right";
            if (path.IndexOf(side, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 5;
            }

            if (path.IndexOf("Runtime UI Ray", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score = 0;
            }

            return score;
        }

        private void CopyInputReaders(Component inputSource, XRRayInteractor ray, RaySetupResult result)
        {
            result.InputCopySource = $"{inputSource.GetType().Name}:{GetPath(inputSource.transform)}";

            if (inputSource is XRBaseInputInteractor inputInteractor)
            {
                ray.selectInput = CloneButtonReader(inputInteractor.selectInput, "Select");
                ray.activateInput = CloneButtonReader(inputInteractor.activateInput, "Activate");
                result.SelectInputCopied = true;
                result.ActivateInputCopied = true;
            }

            switch (inputSource)
            {
                case XRRayInteractor sourceRay:
                    ray.uiPressInput = CloneButtonReader(sourceRay.uiPressInput, "UI Press");
                    ray.uiScrollInput = CloneValueReader(sourceRay.uiScrollInput, "UI Scroll");
                    result.UiPressInputCopied = true;
                    result.UiScrollInputCopied = true;
                    break;
                case NearFarInteractor sourceNearFar:
                    ray.uiPressInput = CloneButtonReader(sourceNearFar.uiPressInput, "UI Press");
                    ray.uiScrollInput = CloneValueReader(sourceNearFar.uiScrollInput, "UI Scroll");
                    result.UiPressInputCopied = true;
                    result.UiScrollInputCopied = true;
                    break;
            }
        }

        private static XRInputButtonReader CloneButtonReader(XRInputButtonReader source, string name)
        {
            if (source == null)
            {
                return new XRInputButtonReader(name);
            }

            var clone = new XRInputButtonReader(name)
            {
                inputSourceMode = source.inputSourceMode,
                inputActionPerformed = source.inputActionPerformed,
                inputActionValue = source.inputActionValue,
                inputActionReferencePerformed = source.inputActionReferencePerformed,
                inputActionReferenceValue = source.inputActionReferenceValue,
                manualPerformed = source.manualPerformed,
                manualValue = source.manualValue
            };
            clone.SetObjectReference(source.GetObjectReference());
            return clone;
        }

        private static XRInputValueReader<Vector2> CloneValueReader(XRInputValueReader<Vector2> source, string name)
        {
            if (source == null)
            {
                return new XRInputValueReader<Vector2>(name);
            }

            var clone = new XRInputValueReader<Vector2>(name)
            {
                inputSourceMode = source.inputSourceMode,
                inputAction = source.inputAction,
                inputActionReference = source.inputActionReference,
                manualValue = source.manualValue
            };
            clone.SetObjectReference(source.GetObjectReference());
            return clone;
        }

        private void ConfigureLineRenderer(LineRenderer lineRenderer)
        {
            lineRenderer.enabled = true;
            lineRenderer.useWorldSpace = true;
            lineRenderer.positionCount = 2;
            lineRenderer.widthMultiplier = _lineWidth;
            lineRenderer.numCapVertices = 4;
            lineRenderer.numCornerVertices = 2;
            lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lineRenderer.receiveShadows = false;

            if (lineRenderer.sharedMaterial == null)
            {
                Shader shader = Shader.Find("Sprites/Default") ??
                    Shader.Find("Universal Render Pipeline/Unlit") ??
                    Shader.Find("Unlit/Color");
                if (shader != null)
                {
                    lineRenderer.sharedMaterial = new Material(shader)
                    {
                        name = "Runtime UI Ray Line Material",
                        color = _lineColor
                    };
                }
            }
        }

        private void ConfigureLineVisual(XRInteractorLineVisual lineVisual)
        {
            lineVisual.enabled = true;
            lineVisual.overrideInteractorLineLength = true;
            lineVisual.lineLength = _rayLength;
            lineVisual.autoAdjustLineLength = false;
            lineVisual.minLineLength = _rayLength;
            lineVisual.useDistanceToHitAsMaxLineLength = false;
            lineVisual.lineWidth = _lineWidth;
            lineVisual.setLineColorGradient = true;
            lineVisual.validColorGradient = SolidGradient(_lineColor);
            lineVisual.invalidColorGradient = SolidGradient(_lineColor);
            lineVisual.blockedColorGradient = SolidGradient(Color.yellow);
        }

        private static Gradient SolidGradient(Color color)
        {
            return new Gradient
            {
                colorKeys = new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
                alphaKeys = new[] { new GradientAlphaKey(color.a, 0f), new GradientAlphaKey(color.a, 1f) }
            };
        }

        private void LogStarted(
            string phase,
            Transform xrOrigin,
            Transform cameraOffset,
            List<ParentCandidate> leftCandidates,
            List<ParentCandidate> rightCandidates)
        {
            var payload = new Dictionary<string, object>
            {
                ["time"] = Time.time,
                ["phase"] = phase,
                ["configure_invocation"] = _configureInvocation,
                ["scene"] = SceneManager.GetActiveScene().name,
                ["bootstrap_gameobject_path"] = GetPath(transform),
                ["bootstrap_active_in_hierarchy"] = gameObject.activeInHierarchy,
                ["protocol_ui_path"] = transform.parent != null ? GetPath(transform.parent) : string.Empty,
                ["canvas_path"] = _targetCanvas != null ? GetPath(_targetCanvas.transform) : string.Empty,
                ["xr_origin_found"] = xrOrigin != null,
                ["xr_origin_path"] = xrOrigin != null ? GetPath(xrOrigin) : string.Empty,
                ["camera_offset_found"] = cameraOffset != null,
                ["camera_offset_path"] = cameraOffset != null ? GetPath(cameraOffset) : string.Empty,
                ["left_parent_candidates"] = DescribeCandidates(leftCandidates),
                ["right_parent_candidates"] = DescribeCandidates(rightCandidates)
            };
            Log("experiment_runtime_ui_ray_bootstrap_started", payload);
        }

        private void LogResult(
            string phase,
            ParentResolution leftParent,
            ParentResolution rightParent,
            RaySetupResult left,
            RaySetupResult right,
            bool canvasLayerChanged,
            bool eventCameraAssigned,
            int uiLayer,
            bool hasXrInputModule,
            bool hasTrackedRaycaster)
        {
            string failedReason = string.Empty;
            if (!left.Success && !right.Success)
            {
                failedReason = $"left={left.FailureReason};right={right.FailureReason}";
            }

            var payload = new Dictionary<string, object>
            {
                ["time"] = Time.time,
                ["phase"] = phase,
                ["configure_invocation"] = _configureInvocation,
                ["left_parent_path"] = leftParent.Parent != null ? GetPath(leftParent.Parent) : string.Empty,
                ["right_parent_path"] = rightParent.Parent != null ? GetPath(rightParent.Parent) : string.Empty,
                ["left_parent_reason"] = leftParent.Reason,
                ["right_parent_reason"] = rightParent.Reason,
                ["left_pose_source"] = left.PoseSourcePath,
                ["right_pose_source"] = right.PoseSourcePath,
                ["left_ray_created"] = left.Created || left.InteractorCreated,
                ["right_ray_created"] = right.Created || right.InteractorCreated,
                ["left_ray_path"] = left.RayPath,
                ["right_ray_path"] = right.RayPath,
                ["left_ray_activeSelf"] = left.ActiveSelf,
                ["right_ray_activeSelf"] = right.ActiveSelf,
                ["left_ray_activeInHierarchy"] = left.ActiveInHierarchy,
                ["right_ray_activeInHierarchy"] = right.ActiveInHierarchy,
                ["left_line_renderer_enabled"] = left.LineRendererEnabled,
                ["right_line_renderer_enabled"] = right.LineRendererEnabled,
                ["left_line_visual_enabled"] = left.LineVisualEnabled,
                ["right_line_visual_enabled"] = right.LineVisualEnabled,
                ["left_manual_visual_path"] = left.ManualVisualPath,
                ["right_manual_visual_path"] = right.ManualVisualPath,
                ["manual_line_visible"] = left.ManualLineVisible || right.ManualLineVisible,
                ["creation_failed_reason"] = failedReason,
                ["fallback_manual_enabled"] = left.ManualFallbackEnabled || right.ManualFallbackEnabled,
                ["xri_ray_enabled"] = left.XriRayEnabled || right.XriRayEnabled,
                ["ui_only"] = true,
                ["enable_ui_interaction"] = left.EnableUiInteraction || right.EnableUiInteraction,
                ["interaction_layers"] = $"{left.InteractionLayers}|{right.InteractionLayers}",
                ["raycast_mask"] = $"{left.RaycastMask}|{right.RaycastMask}",
                ["ui_layer"] = uiLayer,
                ["ui_layer_name"] = uiLayer >= 0 ? LayerMask.LayerToName(uiLayer) : string.Empty,
                ["canvas_layer_changed_to_ui"] = canvasLayerChanged,
                ["event_camera_assigned"] = eventCameraAssigned,
                ["uses_existing_xr_ui_input_module"] = hasXrInputModule,
                ["uses_existing_tracked_device_graphic_raycaster"] = hasTrackedRaycaster,
                ["remote_box_manipulation_enabled"] = false,
                ["object_interaction_layer_masks_changed"] = false,
                ["physics_layers_changed"] = false
            };
            Log("experiment_runtime_ui_ray_bootstrap_result", payload);
        }

        private static string DescribeCandidates(List<ParentCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return "none";
            }

            var parts = new List<string>();
            foreach (ParentCandidate candidate in candidates)
            {
                parts.Add(candidate.Describe());
            }

            return string.Join(" || ", parts);
        }

        private static Transform FindCameraOffset(Transform xrOrigin)
        {
            foreach (Transform transform in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (transform == null || !string.Equals(transform.name, "Camera Offset", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (xrOrigin == null || transform.IsChildOf(xrOrigin))
                {
                    return transform;
                }
            }

            return null;
        }

        private static Transform FindNamedTransform(string nameFragment)
        {
            foreach (Transform transform in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (transform != null && transform.name.IndexOf(nameFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return transform;
                }
            }

            return null;
        }

        private static bool IsSideController(string name, string side)
        {
            return string.Equals(name, $"{side} Controller", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeHandTrackingTransformName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            return name.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Palm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Pinch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Poke", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsForbiddenRuntimeRayParent(Transform transform, string path)
        {
            if (transform == null)
            {
                return true;
            }

            string name = transform.name ?? string.Empty;
            string resolvedPath = path ?? GetPath(transform);
            return name.IndexOf("Controller Visual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                resolvedPath.IndexOf("Controller Visual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(name, "Left Hand", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Right Hand", StringComparison.OrdinalIgnoreCase) ||
                resolvedPath.IndexOf("/Left Hand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                resolvedPath.IndexOf("/Right Hand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                resolvedPath.IndexOf("Teleport Stabilized Origin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                resolvedPath.IndexOf("Teleport Interactor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                LooksLikeHandTrackingTransformName(name);
        }

        private bool ValidateRuntimeRayParentOrientation(Transform parent, out float cameraForwardDot, out string reason)
        {
            cameraForwardDot = 1f;
            reason = string.Empty;
            if (parent == null)
            {
                reason = "parent_missing";
                return false;
            }

            Camera camera = Camera.main ?? FindFirstObjectByType<Camera>();
            if (camera == null)
            {
                reason = "camera_unavailable_orientation_not_validated";
                return false;
            }

            Vector3 parentForward = parent.forward.normalized;
            Vector3 cameraForward = camera.transform.forward.normalized;
            cameraForwardDot = Vector3.Dot(parentForward, cameraForward);
            if (cameraForwardDot < -0.15f)
            {
                reason = "runtime_ray_parent_points_backward";
                return false;
            }

            return true;
        }

        private static Dictionary<string, object> BuildRuntimeRayParentPayload(Transform parent, Transform poseSource, string reason, float cameraForwardDot = 0f)
        {
            return new Dictionary<string, object>
            {
                ["reason"] = reason ?? string.Empty,
                ["parent_path"] = parent != null ? GetPath(parent) : string.Empty,
                ["pose_source_path"] = poseSource != null ? GetPath(poseSource) : string.Empty,
                ["parent_forward"] = parent != null ? parent.forward.ToString("F3") : string.Empty,
                ["parent_up"] = parent != null ? parent.up.ToString("F3") : string.Empty,
                ["camera_forward_dot"] = cameraForwardDot,
                ["ui_only"] = true,
                ["locomotion_modified"] = false
            };
        }

        private static void DisableExistingRuntimeRayChild(Transform parent, string rayName, string reason)
        {
            if (parent == null || string.IsNullOrWhiteSpace(rayName))
            {
                return;
            }

            Transform existing = parent.Find(rayName);
            if (existing != null && IsRuntimeCreatedUiArtifact(existing))
            {
                existing.gameObject.SetActive(false);
            }
        }

        private static string DescribeXrComponents(Transform transform)
        {
            var values = new List<string>();
            foreach (Component component in transform.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                {
                    continue;
                }

                string typeName = component.GetType().Name;
                if (typeName.IndexOf("XR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Interactor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Tracked", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    values.Add($"{typeName}:enabled={(component is Behaviour behaviour ? behaviour.enabled : true)}:active={component.gameObject.activeInHierarchy}");
                }

                if (values.Count >= 12)
                {
                    break;
                }
            }

            return values.Count == 0 ? "none" : string.Join(",", values);
        }

        private static bool PoseLooksPlausible(Transform transform)
        {
            if (transform == null)
            {
                return false;
            }

            Vector3 position = transform.position;
            Quaternion rotation = transform.rotation;
            return !float.IsNaN(position.x) &&
                !float.IsNaN(position.y) &&
                !float.IsNaN(position.z) &&
                !float.IsNaN(rotation.x) &&
                !float.IsNaN(rotation.y) &&
                !float.IsNaN(rotation.z) &&
                !float.IsNaN(rotation.w);
        }

        private static bool IsMainCameraOrDescendant(Transform transform)
        {
            if (transform == null)
            {
                return false;
            }

            Camera camera = Camera.main;
            if (camera != null && transform.IsChildOf(camera.transform))
            {
                return true;
            }

            string path = GetPath(transform);
            return path.IndexOf("/Main Camera/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.EndsWith("/Main Camera", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(transform.name, "Main Camera", StringComparison.OrdinalIgnoreCase);
        }

        internal static string GetPath(Transform transform)
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

        private static void Log(string eventType, Dictionary<string, object> payload)
        {
            payload ??= new Dictionary<string, object>();
            payload["diagnostic_scope"] = "P45C-10";
            TiagoExperimentTelemetry.LogEvent(eventType, payload);
            Debug.Log($"{LogPrefix} {eventType} | {CompactPayload(payload)}");
        }

        private static void LogP46D(string eventType, Dictionary<string, object> payload)
        {
            payload ??= new Dictionary<string, object>();
            payload["diagnostic_scope"] = "P46A-01D";
            TiagoExperimentTelemetry.LogEvent(eventType, payload);
            Debug.Log($"[P46A-01D][XR-UI-DIAG] {eventType} | {CompactPayload(payload)}");
        }

        private static string CompactPayload(Dictionary<string, object> payload)
        {
            var values = new List<string>();
            foreach (KeyValuePair<string, object> pair in payload)
            {
                values.Add($"{pair.Key}={pair.Value}");
                if (values.Count >= 14)
                {
                    break;
                }
            }

            return string.Join(" | ", values);
        }

        private sealed class ParentCandidate
        {
            public ParentCandidate(Transform transform)
            {
                Transform = transform;
            }

            public Transform Transform { get; }
            public string Components = string.Empty;
            public string Decision = string.Empty;

            public string Describe()
            {
                return $"path={GetPath(Transform)};activeSelf={Transform.gameObject.activeSelf};activeInHierarchy={Transform.gameObject.activeInHierarchy};position={Transform.position};rotation={Transform.eulerAngles};pose_valid={PoseLooksPlausible(Transform)};components={Components};decision={Decision}";
            }
        }

        private readonly struct ParentResolution
        {
            public ParentResolution(Transform parent, Transform poseSource, string reason, bool createdAnchor)
            {
                Parent = parent;
                PoseSource = poseSource;
                Reason = reason;
                CreatedAnchor = createdAnchor;
            }

            public Transform Parent { get; }
            public Transform PoseSource { get; }
            public string Reason { get; }
            public bool CreatedAnchor { get; }

            public static ParentResolution None(string reason)
            {
                return new ParentResolution(null, null, reason, false);
            }
        }

        private sealed class RaySetupResult
        {
            public bool Success;
            public bool Created;
            public bool InteractorCreated;
            public bool LineVisualCreated;
            public bool LineRendererCreated;
            public bool ManualVisualCreated;
            public bool ManualLineRendererCreated;
            public bool ManualPointerCreated;
            public bool ManualFallbackEnabled;
            public bool ManualLineVisible;
            public bool EnableUiInteraction;
            public bool SelectInputCopied;
            public bool ActivateInputCopied;
            public bool UiPressInputCopied;
            public bool UiScrollInputCopied;
            public bool XriRayEnabled;
            public bool ActiveSelf;
            public bool ActiveInHierarchy;
            public bool LineVisualEnabled;
            public bool LineRendererEnabled;
            public int InteractionLayers;
            public int RaycastMask;
            public string ComponentType = string.Empty;
            public string FailureReason = string.Empty;
            public string InputCopySource = string.Empty;
            public string RayPath = string.Empty;
            public string ManualVisualPath = string.Empty;
            public string ParentReason = string.Empty;
            public string PoseSourcePath = string.Empty;

            public static RaySetupResult Skipped(string reason)
            {
                return new RaySetupResult
                {
                    FailureReason = reason ?? string.Empty,
                    ParentReason = reason ?? string.Empty
                };
            }
        }

        private sealed class VisualAudit
        {
            public readonly List<string> LeftVisualCandidates = new();
            public readonly List<string> RightVisualCandidates = new();
            public readonly List<string> HeadCandidates = new();
            public int LeftLineRendererCount;
            public int RightLineRendererCount;
            public int LeftXriLineVisualCount;
            public int RightXriLineVisualCount;
            public int LeftManualLaserCount;
            public int RightManualLaserCount;
            public int HeadCameraVisualCount;
            public int LeftVisibleRayCount;
            public int RightVisibleRayCount;
            public bool DuplicatesDetected;
        }

        private sealed class VisualConsolidationResult
        {
            public int LineRenderersDisabled;
            public int XriVisualsDisabled;
            public int DuplicatesRemovedOrDisabled;
            public bool ManualLaserEnabled;
            public bool XriLineVisualEnabled;
            public string SelectedVisualOwner = string.Empty;
        }

        private sealed class NativeUiInteractorAudit
        {
            public int NearFarCount;
            public int XrRayCount;
            public int ActiveUiInteractorCount;
            private readonly List<string> _candidates = new();

            public void AddCandidate(Component component, string label)
            {
                if (component == null || _candidates.Count >= 16)
                {
                    return;
                }

                bool enabled = component is Behaviour behaviour ? behaviour.enabled : true;
                _candidates.Add($"{label}:{GetPath(component.transform)}:enabled={enabled}:active={component.gameObject.activeInHierarchy}");
            }

            public string Describe()
            {
                return _candidates.Count == 0 ? "none" : string.Join(" || ", _candidates);
            }
        }
    }

    [DisallowMultipleComponent]
    public sealed class ExperimentRuntimeManualUiLaserPointer : MonoBehaviour
    {
        private const string LogPrefix = "[P45C-10][XR-UI]";
        private static int s_ManualUiHitCount;
        private static int s_ManualUiClickCount;
        private static bool s_ManualLineVisible;

        [SerializeField] private Canvas _canvas;
        [SerializeField] private Transform _poseSource;
        [SerializeField] private LineRenderer _lineRenderer;
        [SerializeField] private float _length = 6f;
        [SerializeField] private bool _leftHand = true;
        [SerializeField] private bool _enableClickFallback = true;
        [SerializeField] private bool _verboseDiagnostics;

        private Button _lastButton;
        private bool _lastPressed;
        private float _nextSampleLogAt;
        private string _lastHitPath = string.Empty;
        private string _lastInputSource = string.Empty;

        public static int ManualUiHitCount => s_ManualUiHitCount;
        public static int ManualUiClickCount => s_ManualUiClickCount;
        public static bool ManualLineVisible => s_ManualLineVisible;

        public bool HasUiHit => _lastButton != null;
        public bool LineVisible => _lineRenderer != null && _lineRenderer.enabled && _lineRenderer.gameObject.activeInHierarchy;
        public string LastHitPath => _lastHitPath;
        public string LastInputSource => _lastInputSource;

        public void Initialize(Canvas canvas, Transform poseSource, LineRenderer lineRenderer, float length, bool leftHand, bool enableClickFallback, bool verboseDiagnostics)
        {
            _canvas = canvas;
            _poseSource = poseSource;
            _lineRenderer = lineRenderer;
            _length = length;
            _leftHand = leftHand;
            _enableClickFallback = enableClickFallback;
            _verboseDiagnostics = verboseDiagnostics;
        }

        private void Update()
        {
            if (_canvas == null || _poseSource == null || _lineRenderer == null)
            {
                return;
            }

            Vector3 origin = _poseSource.position;
            Vector3 direction = ResolveDirection(_poseSource);
            var ray = new Ray(origin, direction);

            bool uiHit = TryGetButtonHit(ray, out Button button, out float hitDistance, out string hitPath);
            float lineDistance = uiHit ? Mathf.Min(hitDistance, _length) : _length;
            _lineRenderer.positionCount = 2;
            _lineRenderer.SetPosition(0, origin);
            _lineRenderer.SetPosition(1, origin + direction * lineDistance);
            _lineRenderer.enabled = true;
            s_ManualLineVisible = LineVisible;

            bool hitChanged = !string.Equals(hitPath, _lastHitPath, StringComparison.Ordinal);
            _lastButton = button;
            _lastHitPath = hitPath;
            if (uiHit)
            {
                s_ManualUiHitCount++;
            }

            bool pressed = ReadPress(out string inputSource);
            bool pressedThisFrame = pressed && !_lastPressed;
            _lastPressed = pressed;
            _lastInputSource = inputSource;

            if (_enableClickFallback && pressedThisFrame && button != null && button.IsInteractable())
            {
                button.onClick.Invoke();
                s_ManualUiClickCount++;
                Log("experiment_runtime_manual_ui_laser_click", new Dictionary<string, object>
                {
                    ["side"] = _leftHand ? "left" : "right",
                    ["button_path"] = ExperimentRuntimeUiRayInteractorBootstrap.GetPath(button.transform),
                    ["input_source"] = inputSource,
                    ["origin"] = origin,
                    ["direction"] = direction,
                    ["remote_box_manipulation_enabled"] = false,
                    ["ui_only"] = true
                });
            }

            if (_verboseDiagnostics && (Time.unscaledTime >= _nextSampleLogAt || uiHit && hitChanged))
            {
                _nextSampleLogAt = Time.unscaledTime + 1f;
                Log("experiment_runtime_manual_ui_laser_sample", new Dictionary<string, object>
                {
                    ["side"] = _leftHand ? "left" : "right",
                    ["manual_line_visible"] = LineVisible,
                    ["manual_ui_hit_detected"] = uiHit,
                    ["hit_path"] = hitPath,
                    ["input_source"] = inputSource,
                    ["origin"] = origin,
                    ["direction"] = direction,
                    ["length"] = _length,
                    ["pose_source"] = ExperimentRuntimeUiRayInteractorBootstrap.GetPath(_poseSource),
                    ["ui_only"] = true,
                    ["remote_box_manipulation_enabled"] = false
                });
            }
        }

        private static Vector3 ResolveDirection(Transform source)
        {
            if (source == null)
            {
                return Vector3.forward;
            }

            Vector3 direction = source.forward;
            return direction.sqrMagnitude < 0.001f ? Vector3.forward : direction.normalized;
        }

        private bool TryGetButtonHit(Ray ray, out Button button, out float distance, out string hitPath)
        {
            button = null;
            distance = float.PositiveInfinity;
            hitPath = string.Empty;
            if (_canvas == null)
            {
                return false;
            }

            Camera eventCamera = _canvas.worldCamera ?? Camera.main;
            if (eventCamera == null)
            {
                return false;
            }

            foreach (Graphic graphic in _canvas.GetComponentsInChildren<Graphic>(false))
            {
                if (graphic == null || !graphic.raycastTarget || graphic.canvasRenderer.cull)
                {
                    continue;
                }

                RectTransform rectTransform = graphic.rectTransform;
                var plane = new Plane(rectTransform.forward, rectTransform.position);
                if (!plane.Raycast(ray, out float enter) || enter < 0f || enter > _length || enter >= distance)
                {
                    continue;
                }

                Vector3 worldPoint = ray.GetPoint(enter);
                Vector2 local = rectTransform.InverseTransformPoint(worldPoint);
                if (!rectTransform.rect.Contains(local))
                {
                    continue;
                }

                Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(eventCamera, worldPoint);
                if (!graphic.Raycast(screenPoint, eventCamera))
                {
                    continue;
                }

                Button hitButton = graphic.GetComponentInParent<Button>();
                if (hitButton == null || !hitButton.gameObject.activeInHierarchy)
                {
                    continue;
                }

                button = hitButton;
                distance = enter;
                hitPath = ExperimentRuntimeUiRayInteractorBootstrap.GetPath(hitButton.transform);
            }

            return button != null;
        }

        private bool ReadPress(out string inputSource)
        {
            inputSource = "none";
            XRNode node = _leftHand ? XRNode.LeftHand : XRNode.RightHand;
            UnityEngine.XR.InputDevice device = InputDevices.GetDeviceAtXRNode(node);
            if (device.isValid)
            {
                if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out bool triggerPressed) && triggerPressed)
                {
                    inputSource = "trigger_button";
                    return true;
                }

                if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out bool primaryPressed) && primaryPressed)
                {
                    inputSource = "primary_button";
                    return true;
                }
            }

#if ENABLE_INPUT_SYSTEM && UNITY_EDITOR
            if (Keyboard.current != null &&
                (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.spaceKey.wasPressedThisFrame))
            {
                inputSource = "editor_keyboard_fallback";
                return true;
            }
#endif

            return false;
        }

        private static void Log(string eventType, Dictionary<string, object> payload)
        {
            payload ??= new Dictionary<string, object>();
            payload["diagnostic_scope"] = "P45C-10";
            TiagoExperimentTelemetry.LogEvent(eventType, payload);
            Debug.Log($"{LogPrefix} {eventType} | {CompactPayload(payload)}");
        }

        private static string CompactPayload(Dictionary<string, object> payload)
        {
            var values = new List<string>();
            foreach (KeyValuePair<string, object> pair in payload)
            {
                values.Add($"{pair.Key}={pair.Value}");
                if (values.Count >= 12)
                {
                    break;
                }
            }

            return string.Join(" | ", values);
        }
    }
}
