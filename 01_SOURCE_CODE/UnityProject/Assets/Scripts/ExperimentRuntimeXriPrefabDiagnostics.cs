using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using UnityEngine.XR.Interaction.Toolkit.UI;
using XRInputDevice = UnityEngine.XR.InputDevice;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class ExperimentRuntimeXriPrefabDiagnostics : MonoBehaviour
    {
        private const string LogPrefix = "[P46A-01H][XRI-MODALITY-OWNER]";
        private const string VerifiedXriPackageVersion = "3.0.10";
        private const int MaxPayloadText = 1900;
        private const float StableUiLineLengthMeters = 4f;
        private const float StableUiLineWidthMeters = 0.009f;
        private static readonly Color StableUiLineStartColor = new Color(0.05f, 0.82f, 1f, 0.95f);
        private static readonly Color StableUiLineEndColor = new Color(0.05f, 0.55f, 1f, 0.35f);

        [SerializeField] private Canvas _targetCanvas;
        [SerializeField] private bool _enableInEditor;
        [SerializeField] private bool _repairNativeXri = true;
        [SerializeField] private float _periodicAuditSeconds = 5f;

        private bool _leftControllerTracked;
        private bool _rightControllerTracked;
        private bool _handTrackingSeen;
        private float _nextPeriodicAuditAt;
        private Material _stableCurveLineMaterial;
        private XriRayOwner _lastLeftOwner = XriRayOwner.None;
        private XriRayOwner _lastRightOwner = XriRayOwner.None;
        private bool _stablePointerPassiveLogged;
        private bool _stablePointerNoRepairLogged;
        private readonly Dictionary<string, int> _activeOwnerZeroPositionSamples = new();
        private readonly Dictionary<string, LineVisualSnapshot> _lastLineVisualStates = new();

        public static ExperimentRuntimeXriPrefabDiagnostics EnsureAttached(GameObject protocolUiRoot, Canvas canvas)
        {
            if (protocolUiRoot == null)
            {
                return null;
            }

            Transform root = protocolUiRoot.transform.Find("XRINativeDiagnosticsP46H") ??
                protocolUiRoot.transform.Find("XRINativeDiagnosticsP46G") ??
                protocolUiRoot.transform.Find("XRINativeDiagnosticsP46E");
            if (root == null)
            {
                var host = new GameObject("XRINativeDiagnosticsP46H");
                host.transform.SetParent(protocolUiRoot.transform, false);
                root = host.transform;
            }

            ExperimentRuntimeXriPrefabDiagnostics diagnostics =
                root.GetComponent<ExperimentRuntimeXriPrefabDiagnostics>();
            if (diagnostics == null)
            {
                diagnostics = root.gameObject.AddComponent<ExperimentRuntimeXriPrefabDiagnostics>();
            }

            diagnostics.Initialize(canvas);
            return diagnostics;
        }

        public void Initialize(Canvas canvas)
        {
            _targetCanvas = canvas;
        }

        private void OnEnable()
        {
            InputDevices.deviceConnected += OnDeviceConnected;
            InputDevices.deviceDisconnected += OnDeviceDisconnected;
            InputDevices.deviceConfigChanged += OnDeviceConfigChanged;
        }

        private void OnDisable()
        {
            InputDevices.deviceConnected -= OnDeviceConnected;
            InputDevices.deviceDisconnected -= OnDeviceDisconnected;
            InputDevices.deviceConfigChanged -= OnDeviceConfigChanged;
        }

        private void Start()
        {
            if (!ShouldRun())
            {
                return;
            }

            ApplyStablePointerPassiveMode("start");
            StartCoroutine(StartupAudit());
        }

        private void Update()
        {
            if (!ShouldRun())
            {
                return;
            }

            ApplyStablePointerPassiveMode("update");
            bool leftTracked = IsPhysicalControllerTracked(XRNode.LeftHand);
            bool rightTracked = IsPhysicalControllerTracked(XRNode.RightHand);
            bool handTrackingSeen = HasHandTrackingDevice();
            bool hadController = _leftControllerTracked || _rightControllerTracked;
            bool hasController = leftTracked || rightTracked;

            if (hadController && !hasController)
            {
                Log("xri_transition_before_hands", BuildTransitionPayload("before_hands"));
                AuditModalityOwners("controller_to_hand_owner_check", _repairNativeXri);
                AuditAndRepair("controller_to_hand_transition", false);
                Log("xri_controller_to_hand_transition", BuildTransitionPayload("controller_to_hand"));
                Log("xri_transition_after_hands", BuildTransitionPayload("after_hands"));
            }

            if (!hadController && hasController)
            {
                Log("xri_transition_before_controllers", BuildTransitionPayload("before_controllers"));
                ApplyStableStyleToAllNativeLineVisuals("controller_reacquired_style_verify", _repairNativeXri, false);
                AuditModalityOwners("hand_to_controller_owner_check", _repairNativeXri);
                AuditAndRepair("hand_to_controller_transition", _repairNativeXri);
                Log("xri_hand_to_controller_transition", BuildTransitionPayload("hand_to_controller"));
                Log(
                    ControllersHaveActiveNativeVisuals()
                        ? "xri_modality_manager_reactivated_controllers"
                        : "xri_modality_manager_failed_to_reactivate_controllers",
                    BuildTransitionPayload("after_controllers"));
                Log("xri_transition_after_controllers", BuildTransitionPayload("after_controllers"));
            }

            if (!_handTrackingSeen && handTrackingSeen)
            {
                Log("xri_transition_before_hands", BuildTransitionPayload("hand_tracking_detected"));
                ApplyStableStyleToAllNativeLineVisuals("hand_tracking_detected_style_verify", _repairNativeXri, false);
                AuditModalityOwners("hand_tracking_detected_owner_check", _repairNativeXri);
                AuditAndRepair("hand_tracking_detected", false);
                Log("xri_transition_after_hands", BuildTransitionPayload("hand_tracking_detected_after"));
            }

            _leftControllerTracked = leftTracked;
            _rightControllerTracked = rightTracked;
            _handTrackingSeen = handTrackingSeen;

            if (Time.unscaledTime >= _nextPeriodicAuditAt && IsCanvasVisible())
            {
                _nextPeriodicAuditAt = Time.unscaledTime + Mathf.Max(1f, Mathf.Min(_periodicAuditSeconds, 1f));
                Log("xri_curve_stabilization_throttled", new Dictionary<string, object>
                {
                    ["reason"] = "periodic_when_ui_visible",
                    ["next_periodic_audit_at"] = _nextPeriodicAuditAt.ToString("F2"),
                    ["max_rate_hz"] = 1f
                });
                ApplyStableStyleToAllNativeLineVisuals("periodic_style_verify", _repairNativeXri, false);
                AuditModalityOwners("periodic_when_ui_visible", _repairNativeXri);
                AuditAndRepair("periodic_when_ui_visible", false);
                LogLineVisualStateEvent("xri_line_visual_state_periodic_when_ui_visible", "periodic_when_ui_visible", _repairNativeXri);
            }
        }

        private IEnumerator StartupAudit()
        {
            yield return null;
            ApplyStablePointerPassiveMode("startup_audit");
            ResolveCanvas();
            Log("xri_prefab_audit_start", new Dictionary<string, object>
            {
                ["scene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                ["repair_native_xri"] = _repairNativeXri,
                ["manual_laser_created"] = false,
                ["runtime_rays_created"] = false,
                ["xri_origin_hands_rig_preserved"] = true,
                ["controller_only_rig"] = false,
                ["hands_globally_disabled"] = false
            });
            LogPackageVersion();
            Log("xri_curve_stabilization_mode", new Dictionary<string, object>
            {
                ["mode"] = "one_shot_style_all_native_line_visuals_plus_owner_change_repair",
                ["max_periodic_rate_hz"] = 1f,
                ["force_modality"] = false,
                ["force_controller_roots"] = false,
                ["disable_hands_globally"] = false
            });
            ApplyStableStyleToAllNativeLineVisuals("startup_one_shot_style", _repairNativeXri, true);
            AuditModalityOwners("startup_owner_detected", _repairNativeXri);
            AuditAndRepair("startup", _repairNativeXri);
            LogLineVisualStateEvent("xri_line_visual_state_startup", "startup", _repairNativeXri);
            yield return new WaitForSecondsRealtime(0.5f);
            AuditAndRepair("after_start_screen_ready", _repairNativeXri);
            Log("xri_start_screen_ready_state", BuildTransitionPayload("after_start_screen_ready"));
            yield return new WaitForSecondsRealtime(0.5f);
            LogLineVisualStateEvent("xri_line_visual_state_after_1s", "startup_plus_1s", _repairNativeXri);
            yield return new WaitForSecondsRealtime(2f);
            AuditAndRepair("startup_plus_3s", _repairNativeXri);
            LogLineVisualStateEvent("xri_line_visual_state_after_3s", "startup_plus_3s", _repairNativeXri);
            yield return new WaitForSecondsRealtime(2f);
            AuditAndRepair("startup_plus_5s", _repairNativeXri);
            LogLineVisualStateEvent("xri_line_visual_state_after_5s", "startup_plus_5s", _repairNativeXri);
            _nextPeriodicAuditAt = Time.unscaledTime + Mathf.Max(1f, _periodicAuditSeconds);
        }

        private void OnDeviceConnected(XRInputDevice device)
        {
            if (!ShouldRun())
            {
                return;
            }

            LogDeviceEvent(device, "device_connected");
            if (IsPhysicalControllerDevice(device))
            {
                AuditAndRepair("device_connected_controller", _repairNativeXri);
                LogLineVisualStateEvent("xri_line_visual_state_device_connected_controller", "device_connected_controller", _repairNativeXri);
            }
        }

        private void OnDeviceDisconnected(XRInputDevice device)
        {
            if (!ShouldRun())
            {
                return;
            }

            LogDeviceEvent(device, "device_disconnected");
            AuditAndRepair("device_disconnected", false);
        }

        private void OnDeviceConfigChanged(XRInputDevice device)
        {
            if (!ShouldRun())
            {
                return;
            }

            LogDeviceEvent(device, "device_config_changed");
            if (IsPhysicalControllerDevice(device))
            {
                AuditAndRepair("device_config_changed_controller", _repairNativeXri);
                if (IsDeviceTracked(device))
                {
                    LogLineVisualStateEvent("xri_line_visual_state_device_tracked_controller", "device_tracked_controller", _repairNativeXri);
                }
            }
        }

        private bool ShouldRun()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return true;
#else
            return _enableInEditor;
#endif
        }

        private void ApplyStablePointerPassiveMode(string reason)
        {
            if (!HasStableProtocolPointer())
            {
                return;
            }

            if (_repairNativeXri)
            {
                _repairNativeXri = false;
            }

            if (!_stablePointerPassiveLogged)
            {
                _stablePointerPassiveLogged = true;
                Log("stable_ui_pointer_legacy_xri_diagnostics_passive", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["repair_native_xri"] = _repairNativeXri,
                    ["periodic_audit_seconds"] = _periodicAuditSeconds
                });
                Log("stable_ui_pointer_legacy_xri_null_guard_applied", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["guard"] = "LogScreenRayState_material_null_guard"
                });
            }

            if (!_stablePointerNoRepairLogged)
            {
                _stablePointerNoRepairLogged = true;
                Log("stable_ui_pointer_legacy_xri_no_visual_repair_during_stable_pointer", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["repair_native_xri"] = false
                });
                Log("stable_ui_pointer_legacy_xri_diagnostics_throttled", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["periodic_audit_seconds"] = Mathf.Max(1f, _periodicAuditSeconds)
                });
            }
        }

        private static bool HasStableProtocolPointer()
        {
            foreach (ExperimentRuntimeStableProtocolUiPointer pointer in FindObjectsByType<ExperimentRuntimeStableProtocolUiPointer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (pointer != null && pointer.isActiveAndEnabled)
                {
                    return true;
                }
            }

            return false;
        }

        private void AuditAndRepair(string reason, bool allowRepair)
        {
            if (allowRepair && HasStableProtocolPointer())
            {
                allowRepair = false;
                ApplyStablePointerPassiveMode($"{reason}_audit");
            }

            ResolveCanvas();
            LogOriginState(reason);
            LogInputModalityManagerState(reason);
            LogControllerAndHandState(reason);
            int disabledLegacy = DisableOwnRuntimeRayArtifacts(reason, allowRepair);
            ApplyStableStyleToAllNativeLineVisuals($"{reason}_style_all_native", allowRepair, false);
            AuditAndRepairNativeController("left", reason, allowRepair);
            AuditAndRepairNativeController("right", reason, allowRepair);
            AuditAndRepairUi(reason, allowRepair);
            LogLineVisualSampleDiff(reason);
            LogInteractorCounts(reason, disabledLegacy);
        }

        private void LogPackageVersion()
        {
            Log("xri_package_version", new Dictionary<string, object>
            {
                ["manifest_version"] = VerifiedXriPackageVersion,
                ["assembly_version"] = typeof(XRRayInteractor).Assembly.GetName().Version,
                ["assembly"] = typeof(XRRayInteractor).Assembly.GetName().Name,
                ["note"] = "Verified from Packages/manifest.json and Packages/packages-lock.json before P46A-01E edits."
            });
        }

        private void LogOriginState(string reason)
        {
            var origins = new List<string>();
            int activeOrigins = 0;
            foreach (Transform transform in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (transform == null)
                {
                    continue;
                }

                bool namedOrigin = transform.name.IndexOf("XR Origin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    transform.name.IndexOf("XR Rig", StringComparison.OrdinalIgnoreCase) >= 0;
                bool componentOrigin = HasComponentName(transform, "XROrigin") || HasComponentName(transform, "XRRig");
                if (!namedOrigin && !componentOrigin)
                {
                    continue;
                }

                if (transform.gameObject.activeInHierarchy)
                {
                    activeOrigins++;
                }

                AddLimited(origins, DescribeTransform(transform));
            }

            Log("xri_origin_detected", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["active_origin_count"] = activeOrigins,
                ["origins"] = JoinOrNone(origins)
            });
        }

        private void LogInputModalityManagerState(string reason)
        {
            var managers = new List<string>();
            foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (behaviour == null || behaviour.GetType().Name.IndexOf("InputModalityManager", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                AddLimited(managers, $"{DescribeBehaviour(behaviour)};refs={DescribeObjectReferences(behaviour)}");
            }

            Log("xri_input_modality_manager_state", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["count"] = managers.Count,
                ["managers"] = JoinOrNone(managers)
            });
        }

        private void LogControllerAndHandState(string reason)
        {
            Transform leftController = FindControllerRoot(true);
            Transform rightController = FindControllerRoot(false);
            Transform leftHand = FindHandRoot(true);
            Transform rightHand = FindHandRoot(false);

            Log("xri_controller_group_state", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["left"] = DescribeGroup(leftController),
                ["right"] = DescribeGroup(rightController)
            });
            Log("xri_hand_group_state", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["left"] = DescribeGroup(leftHand),
                ["right"] = DescribeGroup(rightHand)
            });
        }

        private int DisableOwnRuntimeRayArtifacts(string reason, bool allowRepair)
        {
            int disabled = 0;
            var paths = new List<string>();
            foreach (Transform transform in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (transform == null || transform == this.transform || !IsOwnRuntimeRayArtifact(transform))
                {
                    continue;
                }

                foreach (Behaviour behaviour in transform.GetComponents<Behaviour>())
                {
                    if (behaviour == null || !behaviour.enabled)
                    {
                        continue;
                    }

                    if (allowRepair)
                    {
                        behaviour.enabled = false;
                    }

                    disabled++;
                    AddLimited(paths, $"{GetPath(transform)}:{behaviour.GetType().Name}");
                }

                if (transform.gameObject.activeSelf)
                {
                    if (allowRepair)
                    {
                        transform.gameObject.SetActive(false);
                    }

                    disabled++;
                    AddLimited(paths, $"{GetPath(transform)}:activeSelf");
                }

                string path = GetPath(transform);
                if (path.IndexOf("Runtime UI Ray", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("xri_legacy_runtime_ray_disabled", new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["path"] = path,
                        ["allowed"] = allowRepair,
                        ["active_after"] = transform.gameObject.activeInHierarchy
                    });
                }

                if (path.IndexOf("ManualUiLaser", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    transform.GetComponent<ExperimentRuntimeManualUiLaserPointer>() != null)
                {
                    Log("xri_legacy_manual_laser_disabled", new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["path"] = path,
                        ["allowed"] = allowRepair,
                        ["active_after"] = transform.gameObject.activeInHierarchy
                    });
                }

                if (IsForbiddenRayOrigin(path))
                {
                    Log("xri_forbidden_ray_path_disabled", new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["path"] = path,
                        ["allowed"] = allowRepair,
                        ["active_after"] = transform.gameObject.activeInHierarchy
                    });
                }
            }

            Log(disabled > 0 ? "xri_prefab_repair_applied" : "xri_prefab_repair_skipped", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["repair"] = "disable_own_runtime_ui_rays_and_manual_lasers",
                ["allowed"] = allowRepair,
                ["disabled_or_detected_count"] = disabled,
                ["paths"] = JoinOrNone(paths)
            });
            return disabled;
        }

        private void ApplyStableStyleToAllNativeLineVisuals(string reason, bool allowRepair, bool logUnchanged)
        {
            foreach (NearFarInteractor nearFarInteractor in FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (nearFarInteractor == null || IsOwnRuntimeRayArtifact(nearFarInteractor.transform) || IsTeleportPath(GetPath(nearFarInteractor.transform)))
                {
                    continue;
                }

                XriRayOwner owner = InferOwnerFromPath(GetPath(nearFarInteractor.transform));
                if (owner == XriRayOwner.None)
                {
                    continue;
                }

                CurveVisualController curveVisual = FindCurveVisualController(nearFarInteractor, GetOwnerRoot(owner));
                Transform lineVisualRoot = curveVisual != null ? curveVisual.transform : FindLineVisualRoot(nearFarInteractor.transform);
                LineRenderer lineRenderer = curveVisual != null ? curveVisual.lineRenderer : null;
                if (lineRenderer == null && lineVisualRoot != null)
                {
                    lineRenderer = lineVisualRoot.GetComponent<LineRenderer>();
                }

                Log("xri_curve_style_target_found", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["owner"] = owner.ToString(),
                    ["nearFarPath"] = GetPath(nearFarInteractor.transform),
                    ["lineVisualPath"] = lineVisualRoot != null ? GetPath(lineVisualRoot) : "none",
                    ["curveVisual"] = curveVisual != null ? GetPath(curveVisual.transform) : "none",
                    ["lineRenderer"] = lineRenderer != null ? GetPath(lineRenderer.transform) : "none"
                });

                if (curveVisual == null)
                {
                    Log("xri_curve_property_write_skipped", new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["owner"] = owner.ToString(),
                        ["property"] = "CurveVisualController",
                        ["skip_reason"] = "curve_visual_missing"
                    });
                    continue;
                }

                bool changed = ApplyStableStyleToCurveVisual(nearFarInteractor, curveVisual, lineRenderer, owner, reason, allowRepair, logUnchanged);
                if (changed || logUnchanged)
                {
                    Log(IsControllerOwner(owner) ? "xri_curve_style_applied_controller" : "xri_curve_style_applied_hand",
                        BuildCurveVisualPayload(reason, OwnerToSide(owner), nearFarInteractor, GetOwnerRoot(owner), curveVisual, lineRenderer, owner));
                    Log("xri_curve_style_after_verify",
                        BuildCurveVisualPayload($"{reason}_after_verify", OwnerToSide(owner), nearFarInteractor, GetOwnerRoot(owner), curveVisual, lineRenderer, owner));
                }
            }
        }

        private void AuditAndRepairNativeController(string hand, string reason, bool allowRepair)
        {
            bool left = string.Equals(hand, "left", StringComparison.OrdinalIgnoreCase);
            Transform controller = FindControllerRoot(left);
            if (controller == null)
            {
                Log("xri_native_ray_missing", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["hand"] = hand,
                    ["missing"] = "controller_root"
                });
                return;
            }

            MaybeReactivateControllerRoot(controller, hand, reason, allowRepair);
            EnableControllerSupportComponents(controller, hand, reason, allowRepair);

            var nativeInteractors = FindNativeInteractors(controller);
            if (nativeInteractors.Count == 0)
            {
                Log("xri_native_ray_missing", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["hand"] = hand,
                    ["controller"] = GetPath(controller),
                    ["missing"] = "NearFarInteractor_or_XRRayInteractor"
                });
                return;
            }

            foreach (Component interactor in nativeInteractors)
            {
                string path = GetPath(interactor.transform);
                Log("xri_native_ray_found", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["hand"] = hand,
                    ["type"] = interactor.GetType().Name,
                    ["path"] = path,
                    ["active"] = interactor.gameObject.activeInHierarchy,
                    ["enabled"] = interactor is Behaviour behaviour && behaviour.enabled
                });

                if (interactor is Behaviour interactorBehaviour && !interactorBehaviour.enabled)
                {
                    Log("xri_own_script_disabled_native_visual", new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["hand"] = hand,
                        ["component"] = interactor.GetType().Name,
                        ["path"] = path
                    });
                    if (allowRepair)
                    {
                        interactorBehaviour.enabled = true;
                    }
                    Log("xri_native_ray_enabled", BuildComponentPayload(reason, hand, interactorBehaviour, allowRepair));
                }

                EnsureBooleanProperty(interactor, "enableUIInteraction", true, allowRepair, "xri_native_ray_enabled", reason, hand);
                ValidateAndRepairRayOrigin(interactor, controller, hand, reason, allowRepair);
                EnableLineVisualsForInteractor(interactor, controller, hand, reason, allowRepair);
                LogRayForwardCheck(interactor, controller, hand, reason);
                LogNativeRayVisibleCheck(interactor, controller, hand, reason);
            }
        }

        private void MaybeReactivateControllerRoot(Transform controller, string hand, string reason, bool allowRepair)
        {
            bool tracked = string.Equals(hand, "left", StringComparison.OrdinalIgnoreCase)
                ? IsPhysicalControllerTracked(XRNode.LeftHand)
                : IsPhysicalControllerTracked(XRNode.RightHand);
            if (controller.gameObject.activeSelf || !tracked)
            {
                return;
            }

            if (AnyHandRootActiveInHierarchy())
            {
                Log("xri_curve_controller_inactive_no_force", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["hand"] = hand,
                    ["path"] = GetPath(controller),
                    ["tracked"] = tracked,
                    ["activeInputModalityManagerState"] = DescribeInputModalityManagers()
                });
                Log("xri_curve_hand_mode_observed_no_override", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["hand"] = hand,
                    ["controller"] = GetPath(controller),
                    ["left_hand"] = DescribeGroup(FindHandRoot(true)),
                    ["right_hand"] = DescribeGroup(FindHandRoot(false))
                });
                return;
            }

            Log("xri_prefab_deviation_detected", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["deviation"] = "tracked_controller_root_inactive",
                ["hand"] = hand,
                ["path"] = GetPath(controller)
            });

            if (allowRepair)
            {
                controller.gameObject.SetActive(true);
            }

            Log("xri_prefab_repair_applied", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["repair"] = "reactivate_tracked_controller_root",
                ["hand"] = hand,
                ["path"] = GetPath(controller),
                ["allowed"] = allowRepair
            });
        }

        private void EnableControllerSupportComponents(Transform controller, string hand, string reason, bool allowRepair)
        {
            foreach (Behaviour behaviour in controller.GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour == null)
                {
                    continue;
                }

                string typeName = behaviour.GetType().Name;
                bool supportComponent =
                    behaviour is XRInteractionGroup ||
                    typeName.IndexOf("ControllerInputActionManager", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("TrackedPoseDriver", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!supportComponent || behaviour.enabled)
                {
                    continue;
                }

                Log("xri_prefab_deviation_detected", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["hand"] = hand,
                    ["component"] = typeName,
                    ["path"] = GetPath(behaviour.transform),
                    ["deviation"] = "native_controller_support_component_disabled"
                });

                if (allowRepair)
                {
                    behaviour.enabled = true;
                }

                Log("xri_prefab_repair_applied", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["repair"] = "enable_native_controller_support_component",
                    ["hand"] = hand,
                    ["component"] = typeName,
                    ["path"] = GetPath(behaviour.transform),
                    ["allowed"] = allowRepair
                });
            }
        }

        private void EnableLineVisualsForInteractor(Component interactor, Transform controller, string hand, string reason, bool allowRepair)
        {
            foreach (XRInteractorLineVisual lineVisual in controller.GetComponentsInChildren<XRInteractorLineVisual>(true))
            {
                if (lineVisual == null || IsOwnRuntimeRayArtifact(lineVisual.transform) || IsTeleportPath(GetPath(lineVisual.transform)))
                {
                    continue;
                }

                if (lineVisual.GetComponentInParent<NearFarInteractor>(true) == null &&
                    lineVisual.GetComponentInParent<XRRayInteractor>(true) == null)
                {
                    continue;
                }

                if (!lineVisual.enabled)
                {
                    if (allowRepair)
                    {
                        lineVisual.enabled = true;
                    }

                    Log("xri_native_line_visual_enabled", BuildComponentPayload(reason, hand, lineVisual, allowRepair));
                }
            }

            foreach (LineRenderer lineRenderer in controller.GetComponentsInChildren<LineRenderer>(true))
            {
                if (lineRenderer == null)
                {
                    continue;
                }

                string path = GetPath(lineRenderer.transform);
                bool nativeLine = !IsOwnRuntimeRayArtifact(lineRenderer.transform) &&
                    !IsTeleportPath(path) &&
                    (path.IndexOf("Near-Far Interactor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     path.IndexOf("Ray Interactor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     lineRenderer.GetComponentInParent<XRInteractorLineVisual>(true) != null);
                if (!nativeLine)
                {
                    continue;
                }

                if (!lineRenderer.enabled)
                {
                    if (allowRepair)
                    {
                        lineRenderer.enabled = true;
                    }

                    Log("xri_native_line_renderer_enabled", BuildComponentPayload(reason, hand, lineRenderer, allowRepair));
                }
            }

            if (interactor is NearFarInteractor nearFarInteractor)
            {
                AuditAndRepairCurveLineVisual(nearFarInteractor, controller, hand, reason, allowRepair);
            }
        }

        private void AuditAndRepairCurveLineVisual(NearFarInteractor nearFarInteractor, Transform controller, string hand, string reason, bool allowRepair)
        {
            CurveVisualController curveVisual = FindCurveVisualController(nearFarInteractor, controller);
            Transform lineVisualRoot = curveVisual != null ? curveVisual.transform : FindLineVisualRoot(nearFarInteractor.transform);
            LineRenderer lineRenderer = curveVisual != null ? curveVisual.lineRenderer : null;
            if (lineRenderer == null && lineVisualRoot != null)
            {
                lineRenderer = lineVisualRoot.GetComponent<LineRenderer>();
            }

            LogLineVisualComponentInventory(hand, reason, lineVisualRoot, nearFarInteractor, curveVisual, lineRenderer);

            if (curveVisual == null)
            {
                Log("xri_prefab_deviation_detected", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["hand"] = hand,
                    ["deviation"] = "curve_visual_controller_missing",
                    ["near_far_path"] = GetPath(nearFarInteractor.transform),
                    ["line_visual_path"] = lineVisualRoot != null ? GetPath(lineVisualRoot) : "none"
                });
                return;
            }

            if (!nearFarInteractor.enableFarCasting)
            {
                if (allowRepair)
                {
                    nearFarInteractor.enableFarCasting = true;
                }

                Log("xri_prefab_repair_applied", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["repair"] = "enable_near_far_far_casting_for_line_visual",
                    ["hand"] = hand,
                    ["path"] = GetPath(nearFarInteractor.transform),
                    ["enabled_after"] = nearFarInteractor.enableFarCasting,
                    ["allowed"] = allowRepair
                });
            }

            EnableCurveCasterBehaviours(nearFarInteractor, hand, reason, allowRepair);

            if (!curveVisual.enabled)
            {
                if (allowRepair)
                {
                    curveVisual.enabled = true;
                }

                Log("xri_line_visual_component_enabled", BuildComponentPayload(reason, hand, curveVisual, allowRepair));
            }

            if (lineRenderer == null)
            {
                lineRenderer = curveVisual.GetComponentInChildren<LineRenderer>(true);
            }

            if (lineRenderer != null && curveVisual.lineRenderer == null)
            {
                if (allowRepair)
                {
                    curveVisual.lineRenderer = lineRenderer;
                }

                Log("xri_line_visual_interactor_reference_repaired", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["hand"] = hand,
                    ["repair"] = "curve_visual_line_renderer_reference",
                    ["curve_visual"] = GetPath(curveVisual.transform),
                    ["line_renderer"] = GetPath(lineRenderer.transform),
                    ["allowed"] = allowRepair,
                    ["reference_after"] = curveVisual.lineRenderer != null ? GetPath(curveVisual.lineRenderer.transform) : "none"
                });
            }

            if (lineRenderer != null && !lineRenderer.enabled)
            {
                if (allowRepair)
                {
                    lineRenderer.enabled = true;
                }

                Log("xri_line_renderer_component_enabled", BuildComponentPayload(reason, hand, lineRenderer, allowRepair));
            }

            ICurveInteractionDataProvider dataProvider = curveVisual.curveInteractionDataProvider;
            if (!ReferenceEquals(dataProvider, nearFarInteractor))
            {
                if (allowRepair)
                {
                    curveVisual.curveInteractionDataProvider = nearFarInteractor;
                }

                Log("xri_line_visual_interactor_reference_repaired", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["hand"] = hand,
                    ["repair"] = "curve_visual_data_provider_reference",
                    ["curve_visual"] = GetPath(curveVisual.transform),
                    ["previous_provider"] = DescribeCurveDataProvider(dataProvider),
                    ["expected_provider"] = GetPath(nearFarInteractor.transform),
                    ["allowed"] = allowRepair,
                    ["provider_after"] = DescribeCurveDataProvider(curveVisual.curveInteractionDataProvider)
                });
            }

            if (curveVisual.lineOriginTransform == null)
            {
                if (allowRepair)
                {
                    curveVisual.lineOriginTransform = nearFarInteractor.transform;
                }

                Log("xri_prefab_repair_applied", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["repair"] = "curve_visual_line_origin_reference",
                    ["hand"] = hand,
                    ["curve_visual"] = GetPath(curveVisual.transform),
                    ["line_origin_after"] = curveVisual.lineOriginTransform != null ? GetPath(curveVisual.lineOriginTransform) : "none",
                    ["allowed"] = allowRepair
                });
            }

            if (curveVisual.visualPointCount <= 1)
            {
                if (allowRepair)
                {
                    curveVisual.visualPointCount = 20;
                }

                Log("xri_prefab_repair_applied", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["repair"] = "curve_visual_point_count",
                    ["hand"] = hand,
                    ["curve_visual"] = GetPath(curveVisual.transform),
                    ["visual_point_count_after"] = curveVisual.visualPointCount,
                    ["allowed"] = allowRepair
                });
            }

            StabilizeCurveVisualForUi(nearFarInteractor, controller, hand, reason, allowRepair, curveVisual, lineRenderer);

            LineVisualSnapshot snapshot = CaptureLineVisualSnapshot(hand, nearFarInteractor);
            if (snapshot.LineRendererEnabled && snapshot.LineRendererPositionCount == 0)
            {
                Log("xri_line_renderer_has_zero_positions_after_enable", snapshot.ToPayload(reason));
                Log("xri_curve_line_renderer_zero_positions_after_stabilization", snapshot.ToPayload(reason));
            }

            LogLineVisualComponentInventory(hand, $"{reason}_after_repair", lineVisualRoot, nearFarInteractor, curveVisual, lineRenderer);
        }

        private void StabilizeCurveVisualForUi(
            NearFarInteractor nearFarInteractor,
            Transform controller,
            string hand,
            string reason,
            bool allowRepair,
            CurveVisualController curveVisual,
            LineRenderer lineRenderer)
        {
            bool controllerActive = controller != null && controller.gameObject.activeInHierarchy;
            bool providerActive = curveVisual.curveInteractionDataProvider != null && curveVisual.curveInteractionDataProvider.isActive;

            Log("xri_curve_visual_inventory", BuildCurveVisualPayload(reason, hand, nearFarInteractor, controller, curveVisual, lineRenderer));
            Log("xri_curve_visual_controller_state", BuildCurveVisualPayload(reason, hand, nearFarInteractor, controller, curveVisual, lineRenderer));
            Log("xri_curve_visual_line_renderer_state", BuildCurveVisualPayload(reason, hand, nearFarInteractor, controller, curveVisual, lineRenderer));
            Log("xri_curve_visual_provider_state", BuildCurveVisualPayload(reason, hand, nearFarInteractor, controller, curveVisual, lineRenderer));
            Log("xri_curve_visual_far_casting_state", BuildCurveVisualPayload(reason, hand, nearFarInteractor, controller, curveVisual, lineRenderer));
            Log("xri_curve_visual_modality_state", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["hand"] = hand,
                ["controller_active"] = controllerActive,
                ["provider_active"] = providerActive,
                ["left_hand"] = DescribeGroup(FindHandRoot(true)),
                ["right_hand"] = DescribeGroup(FindHandRoot(false)),
                ["activeInputModalityManagerState"] = DescribeInputModalityManagers()
            });

            if (!controllerActive)
            {
                Log("xri_curve_controller_inactive_no_force", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["hand"] = hand,
                    ["controller"] = controller != null ? GetPath(controller) : "none",
                    ["curve_visual"] = GetPath(curveVisual.transform),
                    ["line_renderer"] = lineRenderer != null ? GetPath(lineRenderer.transform) : "none",
                    ["activeInputModalityManagerState"] = DescribeInputModalityManagers()
                });
                return;
            }

            XriRayOwner owner = InferOwnerFromPath(GetPath(nearFarInteractor.transform));
            ApplyStableStyleToCurveVisual(nearFarInteractor, curveVisual, lineRenderer, owner, reason, allowRepair, false);
            if (lineRenderer != null && allowRepair && !lineRenderer.enabled && providerActive)
            {
                lineRenderer.enabled = true;
                Log("xri_curve_line_renderer_enabled", BuildComponentPayload(reason, hand, lineRenderer, allowRepair));
            }

            Log("xri_curve_visual_enabled", BuildComponentPayload(reason, hand, curveVisual, allowRepair));
            Log("xri_curve_provider_repaired", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["hand"] = hand,
                ["provider"] = DescribeCurveDataProvider(curveVisual.curveInteractionDataProvider),
                ["provider_active"] = providerActive,
                ["expected_provider"] = nearFarInteractor != null ? GetPath(nearFarInteractor.transform) : "none"
            });
            Log("xri_curve_line_origin_validated", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["hand"] = hand,
                ["curve_visual"] = GetPath(curveVisual.transform),
                ["line_origin"] = curveVisual.lineOriginTransform != null ? GetPath(curveVisual.lineOriginTransform) : "none",
                ["forbidden_origin"] = curveVisual.lineOriginTransform != null && IsForbiddenRayOrigin(GetPath(curveVisual.lineOriginTransform))
            });

            if (controllerActive && providerActive)
            {
                Log("xri_curve_controller_mode_restored_visual", BuildCurveVisualPayload(reason, hand, nearFarInteractor, controller, curveVisual, lineRenderer));
                Log("xri_curve_controller_active_visual_restored", BuildCurveVisualPayload(reason, hand, nearFarInteractor, controller, curveVisual, lineRenderer));
            }
        }

        private void EnableCurveCasterBehaviours(NearFarInteractor nearFarInteractor, string hand, string reason, bool allowRepair)
        {
            foreach (Behaviour behaviour in nearFarInteractor.GetComponents<Behaviour>())
            {
                if (behaviour == null || behaviour.enabled)
                {
                    continue;
                }

                string typeName = behaviour.GetType().Name;
                bool caster = typeName.IndexOf("CurveInteractionCaster", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("SphereInteractionCaster", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("InteractionCaster", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!caster)
                {
                    continue;
                }

                if (allowRepair)
                {
                    behaviour.enabled = true;
                }

                Log("xri_prefab_repair_applied", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["repair"] = "enable_near_far_interaction_caster_component",
                    ["hand"] = hand,
                    ["component"] = typeName,
                    ["path"] = GetPath(behaviour.transform),
                    ["enabled_after"] = behaviour.enabled,
                    ["allowed"] = allowRepair
                });
            }
        }

        private Dictionary<string, object> BuildCurveVisualPayload(
            string reason,
            string hand,
            NearFarInteractor nearFarInteractor,
            Transform controller,
            CurveVisualController curveVisual,
            LineRenderer lineRenderer)
        {
            return BuildCurveVisualPayload(reason, hand, nearFarInteractor, controller, curveVisual, lineRenderer, InferOwnerFromPath(nearFarInteractor != null ? GetPath(nearFarInteractor.transform) : string.Empty));
        }

        private bool ApplyStableStyleToCurveVisual(
            NearFarInteractor nearFarInteractor,
            CurveVisualController curveVisual,
            LineRenderer lineRenderer,
            XriRayOwner owner,
            string reason,
            bool allowRepair,
            bool logUnchanged)
        {
            bool changed = false;
            string ownerName = owner.ToString();
            if (curveVisual == null)
            {
                Log("xri_curve_property_write_skipped", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["owner"] = ownerName,
                    ["property"] = "CurveVisualController",
                    ["skip_reason"] = "missing"
                });
                return false;
            }

            changed |= WriteCurveFloat(curveVisual, ownerName, reason, "restingVisualLineLength", curveVisual.restingVisualLineLength, StableUiLineLengthMeters, allowRepair, logUnchanged, value => curveVisual.restingVisualLineLength = value);
            if (curveVisual.maxVisualCurveDistance < StableUiLineLengthMeters)
            {
                changed |= WriteCurveFloat(curveVisual, ownerName, reason, "maxVisualCurveDistance", curveVisual.maxVisualCurveDistance, StableUiLineLengthMeters, allowRepair, logUnchanged, value => curveVisual.maxVisualCurveDistance = value);
            }
            else if (logUnchanged)
            {
                Log("xri_curve_property_write_skipped", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["owner"] = ownerName,
                    ["property"] = "maxVisualCurveDistance",
                    ["skip_reason"] = "already_at_or_above_target",
                    ["value"] = curveVisual.maxVisualCurveDistance
                });
            }

            if (!curveVisual.extendLineToEmptyHit)
            {
                if (allowRepair)
                {
                    curveVisual.extendLineToEmptyHit = true;
                    changed = true;
                    Log("xri_curve_extend_to_empty_hit_configured", new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["owner"] = ownerName,
                        ["curve_visual"] = GetPath(curveVisual.transform),
                        ["extend_line_to_empty_hit_after"] = curveVisual.extendLineToEmptyHit
                    });
                    Log("xri_curve_property_write_applied", BuildPropertyPayload(reason, ownerName, curveVisual, "extendLineToEmptyHit", true));
                }
                else
                {
                    Log("xri_curve_property_write_skipped", BuildPropertyPayload(reason, ownerName, curveVisual, "extendLineToEmptyHit", "write_not_allowed"));
                }
            }

            if (curveVisual.lineDynamicsMode != LineDynamicsMode.Traditional)
            {
                if (allowRepair)
                {
                    curveVisual.lineDynamicsMode = LineDynamicsMode.Traditional;
                    changed = true;
                    Log("xri_curve_property_write_applied", BuildPropertyPayload(reason, ownerName, curveVisual, "lineDynamicsMode", curveVisual.lineDynamicsMode.ToString()));
                }
                else
                {
                    Log("xri_curve_property_write_skipped", BuildPropertyPayload(reason, ownerName, curveVisual, "lineDynamicsMode", "write_not_allowed"));
                }
            }

            changed |= WriteCurveFloat(curveVisual, ownerName, reason, "linePropertyAnimationSpeed", curveVisual.linePropertyAnimationSpeed, 0f, allowRepair, logUnchanged, value => curveVisual.linePropertyAnimationSpeed = value);
            changed |= WriteCurveFloat(curveVisual, ownerName, reason, "extensionRate", curveVisual.extensionRate, 30f, allowRepair, logUnchanged, value => curveVisual.extensionRate = value);

            if (allowRepair)
            {
                ConfigureLinePropertiesForStableUi(curveVisual.noValidHitProperties, 1f);
                ConfigureLinePropertiesForStableUi(curveVisual.uiHitProperties, 1f);
                ConfigureLinePropertiesForStableUi(curveVisual.uiPressHitProperties, 1f);
                ConfigureLinePropertiesForStableUi(curveVisual.hoverHitProperties, 1f);
                ConfigureLinePropertiesForStableUi(curveVisual.selectHitProperties, 1f);
            }

            if (lineRenderer != null)
            {
                bool defaultWhite = IsDefaultWhiteLine(lineRenderer);
                if (allowRepair)
                {
                    Material stableMaterial = GetStableCurveLineMaterial();
                    if (stableMaterial != null && lineRenderer.sharedMaterial != stableMaterial)
                    {
                        lineRenderer.sharedMaterial = stableMaterial;
                        changed = true;
                        Log("xri_curve_material_configured", BuildPropertyPayload(reason, ownerName, lineRenderer, "material", stableMaterial.name));
                    }

                    if (!Mathf.Approximately(lineRenderer.startWidth, StableUiLineWidthMeters) ||
                        !Mathf.Approximately(lineRenderer.endWidth, StableUiLineWidthMeters) ||
                        !Mathf.Approximately(lineRenderer.widthMultiplier, 1f))
                    {
                        lineRenderer.startWidth = StableUiLineWidthMeters;
                        lineRenderer.endWidth = StableUiLineWidthMeters;
                        lineRenderer.widthMultiplier = 1f;
                        changed = true;
                        Log("xri_curve_width_configured", BuildPropertyPayload(reason, ownerName, lineRenderer, "width", StableUiLineWidthMeters));
                    }

                    lineRenderer.colorGradient = BuildStableUiGradient();
                    lineRenderer.numCornerVertices = Mathf.Max(lineRenderer.numCornerVertices, 4);
                    lineRenderer.numCapVertices = Mathf.Max(lineRenderer.numCapVertices, 4);
                    lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    lineRenderer.receiveShadows = false;
                    lineRenderer.useWorldSpace = curveVisual.renderLineInWorldSpace;
                    if (defaultWhite)
                    {
                        Log("xri_curve_style_default_white_removed", new Dictionary<string, object>
                        {
                            ["reason"] = reason,
                            ["owner"] = ownerName,
                            ["lineRenderer"] = GetPath(lineRenderer.transform),
                            ["material_after"] = lineRenderer.sharedMaterial != null ? lineRenderer.sharedMaterial.name : "none",
                            ["gradient_after"] = DescribeGradient(lineRenderer.colorGradient)
                        });
                    }
                }
                else if (defaultWhite)
                {
                    Log("xri_curve_property_write_skipped", new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["owner"] = ownerName,
                        ["property"] = "LineRendererStyle",
                        ["skip_reason"] = "default_white_detected_but_write_not_allowed",
                        ["lineRenderer"] = GetPath(lineRenderer.transform)
                    });
                }
            }

            if (changed || logUnchanged)
            {
                Log("xri_curve_style_after_verify", BuildCurveVisualPayload(reason, OwnerToSide(owner), nearFarInteractor, GetOwnerRoot(owner), curveVisual, lineRenderer, owner));
            }

            return changed;
        }

        private Dictionary<string, object> BuildCurveVisualPayload(
            string reason,
            string hand,
            NearFarInteractor nearFarInteractor,
            Transform controller,
            CurveVisualController curveVisual,
            LineRenderer lineRenderer,
            XriRayOwner owner)
        {
            ICurveInteractionDataProvider provider = curveVisual != null ? curveVisual.curveInteractionDataProvider : null;
            return new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["hand"] = hand,
                ["owner"] = owner.ToString(),
                ["controller"] = controller != null ? DescribeTransform(controller) : "none",
                ["nearFarPath"] = nearFarInteractor != null ? GetPath(nearFarInteractor.transform) : "none",
                ["nearFarActive"] = nearFarInteractor != null && nearFarInteractor.gameObject.activeInHierarchy,
                ["nearFarEnabled"] = nearFarInteractor != null && nearFarInteractor.enabled,
                ["farCastingEnabled"] = nearFarInteractor != null && nearFarInteractor.enableFarCasting,
                ["curveVisualPath"] = curveVisual != null ? GetPath(curveVisual.transform) : "none",
                ["curveVisualEnabled"] = curveVisual != null && curveVisual.enabled,
                ["provider"] = provider != null ? DescribeCurveDataProvider(provider) : "none",
                ["providerActive"] = provider != null && provider.isActive,
                ["lineOrigin"] = curveVisual != null && curveVisual.lineOriginTransform != null ? GetPath(curveVisual.lineOriginTransform) : "none",
                ["restingVisualLineLength"] = curveVisual != null ? curveVisual.restingVisualLineLength : -1f,
                ["maxVisualCurveDistance"] = curveVisual != null ? curveVisual.maxVisualCurveDistance : -1f,
                ["extendLineToEmptyHit"] = curveVisual != null && curveVisual.extendLineToEmptyHit,
                ["lineDynamicsMode"] = curveVisual != null ? curveVisual.lineDynamicsMode.ToString() : "none",
                ["linePropertyAnimationSpeed"] = curveVisual != null ? curveVisual.linePropertyAnimationSpeed : -1f,
                ["extensionRate"] = curveVisual != null ? curveVisual.extensionRate : -1f,
                ["lineRendererPath"] = lineRenderer != null ? GetPath(lineRenderer.transform) : "none",
                ["lineRendererActive"] = lineRenderer != null && lineRenderer.gameObject.activeInHierarchy,
                ["lineRendererEnabled"] = lineRenderer != null && lineRenderer.enabled,
                ["lineRendererPositionCount"] = lineRenderer != null ? lineRenderer.positionCount : -1,
                ["lineRendererWidthStart"] = lineRenderer != null ? lineRenderer.startWidth : 0f,
                ["lineRendererWidthEnd"] = lineRenderer != null ? lineRenderer.endWidth : 0f,
                ["lineRendererWidthMultiplier"] = lineRenderer != null ? lineRenderer.widthMultiplier : 0f,
                ["lineRendererMaterial"] = lineRenderer != null && lineRenderer.sharedMaterial != null ? lineRenderer.sharedMaterial.name : "none",
                ["lineRendererGradient"] = lineRenderer != null ? DescribeGradient(lineRenderer.colorGradient) : "none",
                ["activeInputModalityManagerState"] = DescribeInputModalityManagers()
            };
        }

        private void ConfigureLinePropertiesForStableUi(LineProperties properties, float bendRatio)
        {
            if (properties == null)
            {
                return;
            }

            properties.smoothlyCurveLine = false;
            properties.lineBendRatio = bendRatio;
            properties.adjustWidth = true;
            properties.starWidth = StableUiLineWidthMeters;
            properties.endWidth = StableUiLineWidthMeters;
            properties.endWidthScaleDistanceFactor = 0f;
            properties.adjustGradient = true;
            properties.gradient = BuildStableUiGradient();
            properties.customizeExpandLineDrawPercent = false;
            properties.expandModeLineDrawPercent = 1f;
        }

        private bool WriteCurveFloat(
            CurveVisualController curveVisual,
            string owner,
            string reason,
            string property,
            float current,
            float target,
            bool allowRepair,
            bool logUnchanged,
            Action<float> setter)
        {
            if (Mathf.Approximately(current, target))
            {
                if (logUnchanged)
                {
                    Log("xri_curve_property_write_skipped", new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["owner"] = owner,
                        ["property"] = property,
                        ["skip_reason"] = "already_target",
                        ["value"] = current
                    });
                }

                return false;
            }

            if (!allowRepair)
            {
                Log("xri_curve_property_write_skipped", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["owner"] = owner,
                    ["property"] = property,
                    ["current"] = current,
                    ["target"] = target,
                    ["skip_reason"] = "write_not_allowed"
                });
                return false;
            }

            setter(target);
            Log("xri_curve_property_write_applied", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["owner"] = owner,
                ["property"] = property,
                ["curve_visual"] = curveVisual != null ? GetPath(curveVisual.transform) : "none",
                ["old"] = current,
                ["target"] = target
            });

            if (string.Equals(property, "restingVisualLineLength", StringComparison.Ordinal))
            {
                Log("xri_curve_resting_line_length_configured", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["owner"] = owner,
                    ["curve_visual"] = curveVisual != null ? GetPath(curveVisual.transform) : "none",
                    ["target_length_m"] = target,
                    ["resting_line_length_after"] = curveVisual != null ? curveVisual.restingVisualLineLength : -1f
                });
            }

            return true;
        }

        private static Dictionary<string, object> BuildPropertyPayload(string reason, string owner, Component component, string property, object value)
        {
            return new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["owner"] = owner,
                ["property"] = property,
                ["path"] = component != null ? GetPath(component.transform) : "none",
                ["value"] = value
            };
        }

        private static bool IsDefaultWhiteLine(LineRenderer lineRenderer)
        {
            if (lineRenderer == null)
            {
                return false;
            }

            string materialName = lineRenderer.sharedMaterial != null ? lineRenderer.sharedMaterial.name : string.Empty;
            Color start = lineRenderer.colorGradient.Evaluate(0f);
            Color end = lineRenderer.colorGradient.Evaluate(1f);
            bool whiteGradient = start.r > 0.9f && start.g > 0.9f && start.b > 0.9f &&
                end.r > 0.9f && end.g > 0.9f && end.b > 0.9f;
            return materialName.IndexOf("Default-Line", StringComparison.OrdinalIgnoreCase) >= 0 ||
                materialName.IndexOf("Default", StringComparison.OrdinalIgnoreCase) >= 0 && whiteGradient;
        }

        private Material GetStableCurveLineMaterial()
        {
            if (_stableCurveLineMaterial != null)
            {
                return _stableCurveLineMaterial;
            }

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            }

            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }

            if (shader == null)
            {
                shader = Shader.Find("Hidden/Internal-Colored");
            }

            if (shader == null)
            {
                Log("xri_curve_material_configured", new Dictionary<string, object>
                {
                    ["reason"] = "material_shader_missing",
                    ["target_color_start"] = FormatColor(StableUiLineStartColor),
                    ["target_color_end"] = FormatColor(StableUiLineEndColor),
                    ["fallback"] = "keep_existing_line_renderer_material"
                });
                return null;
            }

            _stableCurveLineMaterial = new Material(shader)
            {
                name = "P46A-01H Native XRI Curve UI Line"
            };
            _stableCurveLineMaterial.color = StableUiLineStartColor;
            if (_stableCurveLineMaterial.HasProperty("_BaseColor"))
            {
                _stableCurveLineMaterial.SetColor("_BaseColor", StableUiLineStartColor);
            }

            return _stableCurveLineMaterial;
        }

        private static Gradient BuildStableUiGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(StableUiLineStartColor, 0f),
                    new GradientColorKey(StableUiLineStartColor, 0.55f),
                    new GradientColorKey(StableUiLineEndColor, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(StableUiLineStartColor.a, 0f),
                    new GradientAlphaKey(0.8f, 0.55f),
                    new GradientAlphaKey(StableUiLineEndColor.a, 1f)
                });
            return gradient;
        }

        private static string FormatColor(Color color)
        {
            return $"({color.r:F2},{color.g:F2},{color.b:F2},{color.a:F2})";
        }

        private static string DescribeGradient(Gradient gradient)
        {
            if (gradient == null)
            {
                return "none";
            }

            Color start = gradient.Evaluate(0f);
            Color mid = gradient.Evaluate(0.5f);
            Color end = gradient.Evaluate(1f);
            return $"start={FormatColor(start)};mid={FormatColor(mid)};end={FormatColor(end)}";
        }

        private void ValidateAndRepairRayOrigin(Component interactor, Transform controller, string hand, string reason, bool allowRepair)
        {
            Transform origin = GetTransformMember(interactor, "rayOriginTransform");
            if (origin == null)
            {
                return;
            }

            string path = GetPath(origin);
            if (!IsForbiddenRayOrigin(path))
            {
                return;
            }

            Log("xri_prefab_deviation_detected", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["hand"] = hand,
                ["interactor"] = GetPath(interactor.transform),
                ["ray_origin"] = path,
                ["deviation"] = "native_ray_origin_points_to_visual_or_attach_transform"
            });

            if (allowRepair && TrySetTransformMember(interactor, "rayOriginTransform", controller))
            {
                Log("xri_prefab_repair_applied", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["repair"] = "restore_native_ray_origin_to_controller_root",
                    ["hand"] = hand,
                    ["interactor"] = GetPath(interactor.transform),
                    ["old_ray_origin"] = path,
                    ["new_ray_origin"] = GetPath(controller)
                });
            }
            else
            {
                Log("xri_prefab_repair_skipped", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["repair"] = "restore_native_ray_origin_to_controller_root",
                    ["hand"] = hand,
                    ["interactor"] = GetPath(interactor.transform),
                    ["old_ray_origin"] = path,
                    ["why"] = allowRepair ? "rayOriginTransform_not_writable" : "repair_not_allowed"
                });
            }
        }

        private void LogRayForwardCheck(Component interactor, Transform controller, string hand, string reason)
        {
            Transform origin = GetRayOrigin(interactor) ?? controller;
            Vector3 toCanvas = GetCanvasCenter() - origin.position;
            float alignmentToCanvas = toCanvas.sqrMagnitude > 0.0001f
                ? Vector3.Dot(origin.forward, toCanvas.normalized)
                : 0f;
            Camera camera = Camera.main;
            float alignmentToCameraForward = camera != null
                ? Vector3.Dot(origin.forward, camera.transform.forward)
                : 0f;
            bool inverted = alignmentToCanvas < -0.25f && alignmentToCameraForward < -0.25f;

            Log("xri_native_ray_forward_check", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["hand"] = hand,
                ["interactor"] = GetPath(interactor.transform),
                ["ray_origin"] = GetPath(origin),
                ["origin_forward"] = FormatVector(origin.forward),
                ["origin_up"] = FormatVector(origin.up),
                ["controller_forward"] = controller != null ? FormatVector(controller.forward) : "none",
                ["to_canvas_alignment"] = alignmentToCanvas.ToString("F3"),
                ["camera_forward_alignment"] = alignmentToCameraForward.ToString("F3"),
                ["inverted_detected"] = inverted
            });
        }

        private void LogNativeRayVisibleCheck(Component interactor, Transform controller, string hand, string reason)
        {
            bool lineVisualEnabled = false;
            bool curveVisualControllerEnabled = false;
            bool lineRendererEnabled = false;
            int lineRendererPositions = 0;
            string lineRendererPath = "none";

            foreach (XRInteractorLineVisual lineVisual in controller.GetComponentsInChildren<XRInteractorLineVisual>(true))
            {
                if (lineVisual != null && !IsOwnRuntimeRayArtifact(lineVisual.transform) && lineVisual.isActiveAndEnabled)
                {
                    lineVisualEnabled = true;
                    break;
                }
            }

            foreach (CurveVisualController curveVisual in controller.GetComponentsInChildren<CurveVisualController>(true))
            {
                if (curveVisual != null && !IsOwnRuntimeRayArtifact(curveVisual.transform) && curveVisual.isActiveAndEnabled)
                {
                    curveVisualControllerEnabled = true;
                    lineVisualEnabled = true;
                    break;
                }
            }

            foreach (LineRenderer lineRenderer in controller.GetComponentsInChildren<LineRenderer>(true))
            {
                if (lineRenderer == null || IsOwnRuntimeRayArtifact(lineRenderer.transform))
                {
                    continue;
                }

                string path = GetPath(lineRenderer.transform);
                if (IsTeleportPath(path))
                {
                    continue;
                }

                if (lineRenderer.enabled && lineRenderer.gameObject.activeInHierarchy)
                {
                    lineRendererEnabled = true;
                    lineRendererPositions = lineRenderer.positionCount;
                    lineRendererPath = path;
                    break;
                }
            }

            Log("xri_native_ray_visible_check", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["hand"] = hand,
                ["interactor"] = GetPath(interactor.transform),
                ["controller_active"] = controller.gameObject.activeInHierarchy,
                ["line_visual_enabled"] = lineVisualEnabled,
                ["curve_visual_controller_enabled"] = curveVisualControllerEnabled,
                ["line_renderer_enabled"] = lineRendererEnabled,
                ["line_renderer_positions"] = lineRendererPositions,
                ["line_renderer_path"] = lineRendererPath
            });
        }

        private void LogLineVisualComponentInventory(string hand, string reason, Transform lineVisualRoot, NearFarInteractor nearFarInteractor, CurveVisualController curveVisual, LineRenderer lineRenderer)
        {
            XRInteractorLineVisual xrLineVisual = lineVisualRoot != null ? lineVisualRoot.GetComponent<XRInteractorLineVisual>() : null;
            Log("xri_line_visual_component_inventory", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["hand"] = hand,
                ["lineVisualPath"] = lineVisualRoot != null ? GetPath(lineVisualRoot) : "none",
                ["activeSelf"] = lineVisualRoot != null && lineVisualRoot.gameObject.activeSelf,
                ["activeInHierarchy"] = lineVisualRoot != null && lineVisualRoot.gameObject.activeInHierarchy,
                ["components"] = lineVisualRoot != null ? DescribeComponentInventory(lineVisualRoot) : "none",
                ["lineRendererEnabled"] = lineRenderer != null && lineRenderer.enabled,
                ["lineRendererPositionCount"] = lineRenderer != null ? lineRenderer.positionCount : -1,
                ["lineRendererWidthStart"] = lineRenderer != null ? lineRenderer.startWidth : 0f,
                ["lineRendererWidthEnd"] = lineRenderer != null ? lineRenderer.endWidth : 0f,
                ["lineRendererMaterial"] = lineRenderer != null && lineRenderer.sharedMaterial != null ? lineRenderer.sharedMaterial.name : "none",
                ["xrInteractorLineVisualEnabled"] = xrLineVisual != null && xrLineVisual.enabled,
                ["curveVisualControllerEnabled"] = curveVisual != null && curveVisual.enabled,
                ["referencedInteractorPath"] = curveVisual != null ? DescribeCurveDataProvider(curveVisual.curveInteractionDataProvider) : "none",
                ["lineBendRatio"] = "CurveVisualController_state_specific",
                ["lineLength"] = curveVisual != null ? curveVisual.restingVisualLineLength.ToString("F3") : "none",
                ["extendLineToEmptyHit"] = curveVisual != null && curveVisual.extendLineToEmptyHit,
                ["visualPointCount"] = curveVisual != null ? curveVisual.visualPointCount : -1,
                ["nearFarPath"] = nearFarInteractor != null ? GetPath(nearFarInteractor.transform) : "none"
            });
        }

        private void LogLineVisualStateEvent(string eventType, string reason, bool allowRepair)
        {
            foreach (NearFarInteractor nearFarInteractor in FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (nearFarInteractor == null || IsOwnRuntimeRayArtifact(nearFarInteractor.transform) || IsTeleportPath(GetPath(nearFarInteractor.transform)))
                {
                    continue;
                }

                string hand = InferHandFromPath(GetPath(nearFarInteractor.transform));
                if (!string.Equals(hand, "left", StringComparison.Ordinal) && !string.Equals(hand, "right", StringComparison.Ordinal))
                {
                    continue;
                }

                AuditAndRepairCurveLineVisual(nearFarInteractor, FindControllerRoot(string.Equals(hand, "left", StringComparison.Ordinal)), hand, reason, allowRepair);
                LineVisualSnapshot snapshot = CaptureLineVisualSnapshot(hand, nearFarInteractor);
                Log(eventType, snapshot.ToPayload(reason));
                _lastLineVisualStates[hand] = snapshot;
            }
        }

        private void DetectLineVisualStateTransitions(string reason)
        {
            foreach (NearFarInteractor nearFarInteractor in FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (nearFarInteractor == null || IsOwnRuntimeRayArtifact(nearFarInteractor.transform) || IsTeleportPath(GetPath(nearFarInteractor.transform)))
                {
                    continue;
                }

                string hand = InferHandFromPath(GetPath(nearFarInteractor.transform));
                if (!string.Equals(hand, "left", StringComparison.Ordinal) && !string.Equals(hand, "right", StringComparison.Ordinal))
                {
                    continue;
                }

                LineVisualSnapshot current = CaptureLineVisualSnapshot(hand, nearFarInteractor);
                if (!_lastLineVisualStates.TryGetValue(hand, out LineVisualSnapshot previous))
                {
                    _lastLineVisualStates[hand] = current;
                    continue;
                }

                if (previous.ControllerActiveInHierarchy && !current.ControllerActiveInHierarchy)
                {
                    Log("xri_controller_root_disabled_after_repair", BuildTransitionPayload(reason, previous, current));
                }

                if (previous.NearFarEnabled && !current.NearFarEnabled)
                {
                    Log("xri_interactor_disabled_after_repair", BuildTransitionPayload(reason, previous, current));
                }

                if (previous.CurveVisualEnabled && !current.CurveVisualEnabled)
                {
                    Log("xri_line_visual_disabled_after_repair", BuildTransitionPayload(reason, previous, current));
                    Log("xri_curve_visual_disabled_after_stabilization", BuildTransitionPayload(reason, previous, current));
                }

                if (previous.LineRendererEnabled && !current.LineRendererEnabled)
                {
                    Log("xri_line_renderer_disabled_after_repair", BuildTransitionPayload(reason, previous, current));
                    Log("xri_curve_line_renderer_disabled_after_stabilization", BuildTransitionPayload(reason, previous, current));
                }

                if (current.ControllerActiveInHierarchy && current.LineRendererEnabled && current.LineRendererPositionCount == 0)
                {
                    Log("xri_curve_line_renderer_zero_positions_after_stabilization", current.ToPayload(reason));
                }

                if (current.ControllerActiveInHierarchy &&
                    (!current.CurveVisualEnabled || !current.LineRendererEnabled || current.LineRendererPositionCount == 0))
                {
                    AuditAndRepairCurveLineVisual(
                        nearFarInteractor,
                        FindControllerRoot(string.Equals(hand, "left", StringComparison.Ordinal)),
                        hand,
                        "controller_active_visual_monitor_restore",
                        _repairNativeXri);
                }
                else if (!current.ControllerActiveInHierarchy)
                {
                    Log("xri_curve_controller_inactive_no_force", current.ToPayload(reason));
                }

                _lastLineVisualStates[hand] = current;
            }
        }

        private Dictionary<string, object> BuildTransitionPayload(string reason, LineVisualSnapshot previous, LineVisualSnapshot current)
        {
            Dictionary<string, object> payload = current.ToPayload(reason);
            payload["previousState"] = previous.Describe();
            payload["currentState"] = current.Describe();
            payload["frame"] = Time.frameCount;
            payload["likelyCause"] = InferLineVisualLikelyCause(previous, current);
            payload["activeInputModalityManagerState"] = DescribeInputModalityManagers();
            return payload;
        }

        private string InferLineVisualLikelyCause(LineVisualSnapshot previous, LineVisualSnapshot current)
        {
            if (previous.ControllerActiveInHierarchy && !current.ControllerActiveInHierarchy)
            {
                return "controller_group_disabled_after_repair_possible_xr_input_modality_manager_or_hand_mode";
            }

            if (previous.LineRendererEnabled && !current.LineRendererEnabled && !current.CurveDataProviderActive)
            {
                return "CurveVisualController_disables_LineRenderer_when_curveInteractionDataProvider_is_inactive";
            }

            if (previous.CurveVisualEnabled && !current.CurveVisualEnabled)
            {
                return "line_visual_component_disabled_after_repair_unknown_owner";
            }

            if (previous.NearFarEnabled && !current.NearFarEnabled)
            {
                return "near_far_interactor_disabled_after_repair_unknown_owner";
            }

            return "state_changed_after_repair_owner_unknown";
        }

        private void LogLineVisualSampleDiff(string reason)
        {
            foreach (NearFarInteractor nearFarInteractor in FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (nearFarInteractor == null || IsOwnRuntimeRayArtifact(nearFarInteractor.transform) || IsTeleportPath(GetPath(nearFarInteractor.transform)))
                {
                    continue;
                }

                string hand = InferHandFromPath(GetPath(nearFarInteractor.transform));
                CurveVisualController curveVisual = FindCurveVisualController(nearFarInteractor, FindControllerRoot(string.Equals(hand, "left", StringComparison.Ordinal)));
                LineRenderer lineRenderer = curveVisual != null ? curveVisual.lineRenderer : null;
                var payload = new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["hand"] = hand,
                    ["nearFarPath"] = GetPath(nearFarInteractor.transform),
                    ["sample_xri_version"] = VerifiedXriPackageVersion,
                    ["sample_line_visual_type"] = "CurveVisualController",
                    ["sample_line_renderer_component_enabled"] = false,
                    ["sample_curve_visual_enabled"] = true,
                    ["sample_visual_point_count"] = 20,
                    ["sample_resting_visual_line_length"] = 0.25f,
                    ["sample_extend_line_to_empty_hit"] = false,
                    ["actual_curve_visual_enabled"] = curveVisual != null && curveVisual.enabled,
                    ["actual_line_renderer_enabled"] = lineRenderer != null && lineRenderer.enabled,
                    ["actual_line_renderer_position_count"] = lineRenderer != null ? lineRenderer.positionCount : -1,
                    ["actual_visual_point_count"] = curveVisual != null ? curveVisual.visualPointCount : -1,
                    ["actual_resting_visual_line_length"] = curveVisual != null ? curveVisual.restingVisualLineLength : -1f,
                    ["actual_extend_line_to_empty_hit"] = curveVisual != null && curveVisual.extendLineToEmptyHit
                };
                Log("xri_line_visual_sample_diff", payload);
                Log("xri_curve_visual_sample_comparison", payload);
            }
        }

        private LineVisualSnapshot CaptureLineVisualSnapshot(string hand, NearFarInteractor nearFarInteractor)
        {
            Transform controller = FindControllerRoot(string.Equals(hand, "left", StringComparison.Ordinal));
            CurveVisualController curveVisual = FindCurveVisualController(nearFarInteractor, controller);
            Transform lineVisualRoot = curveVisual != null ? curveVisual.transform : FindLineVisualRoot(nearFarInteractor.transform);
            LineRenderer lineRenderer = curveVisual != null ? curveVisual.lineRenderer : null;
            if (lineRenderer == null && lineVisualRoot != null)
            {
                lineRenderer = lineVisualRoot.GetComponent<LineRenderer>();
            }

            XRInteractionGroup group = controller != null ? controller.GetComponentInChildren<XRInteractionGroup>(true) : null;
            Behaviour controllerInputActionManager = FindBehaviourByTypeName(controller, "ControllerInputActionManager");
            ICurveInteractionDataProvider dataProvider = curveVisual != null ? curveVisual.curveInteractionDataProvider : null;

            return new LineVisualSnapshot
            {
                Hand = hand,
                ControllerPath = controller != null ? GetPath(controller) : "none",
                ControllerActiveSelf = controller != null && controller.gameObject.activeSelf,
                ControllerActiveInHierarchy = controller != null && controller.gameObject.activeInHierarchy,
                NearFarPath = nearFarInteractor != null ? GetPath(nearFarInteractor.transform) : "none",
                NearFarActiveInHierarchy = nearFarInteractor != null && nearFarInteractor.gameObject.activeInHierarchy,
                NearFarEnabled = nearFarInteractor != null && nearFarInteractor.enabled,
                NearFarFarCastingEnabled = nearFarInteractor != null && nearFarInteractor.enableFarCasting,
                InteractionGroupPath = group != null ? GetPath(group.transform) : "none",
                InteractionGroupActiveInHierarchy = group != null && group.gameObject.activeInHierarchy,
                InteractionGroupEnabled = group != null && group.enabled,
                ControllerInputActionManagerPath = controllerInputActionManager != null ? GetPath(controllerInputActionManager.transform) : "none",
                ControllerInputActionManagerActiveInHierarchy = controllerInputActionManager != null && controllerInputActionManager.gameObject.activeInHierarchy,
                ControllerInputActionManagerEnabled = controllerInputActionManager != null && controllerInputActionManager.enabled,
                LineVisualPath = lineVisualRoot != null ? GetPath(lineVisualRoot) : "none",
                LineVisualActiveSelf = lineVisualRoot != null && lineVisualRoot.gameObject.activeSelf,
                LineVisualActiveInHierarchy = lineVisualRoot != null && lineVisualRoot.gameObject.activeInHierarchy,
                CurveVisualEnabled = curveVisual != null && curveVisual.enabled,
                CurveDataProviderPath = DescribeCurveDataProvider(dataProvider),
                CurveDataProviderActive = dataProvider != null && dataProvider.isActive,
                LineRendererPath = lineRenderer != null ? GetPath(lineRenderer.transform) : "none",
                LineRendererActiveInHierarchy = lineRenderer != null && lineRenderer.gameObject.activeInHierarchy,
                LineRendererEnabled = lineRenderer != null && lineRenderer.enabled,
                LineRendererPositionCount = lineRenderer != null ? lineRenderer.positionCount : -1,
                LineRendererFirstPosition = DescribeLineRendererPosition(lineRenderer, true),
                LineRendererLastPosition = DescribeLineRendererPosition(lineRenderer, false),
                LineRendererMaterial = lineRenderer != null && lineRenderer.sharedMaterial != null ? lineRenderer.sharedMaterial.name : "none",
                LineRendererWidthStart = lineRenderer != null ? lineRenderer.startWidth : 0f,
                LineRendererWidthEnd = lineRenderer != null ? lineRenderer.endWidth : 0f,
                VisibleCandidateCount = CountActiveNativeLineVisuals(),
                Time = Time.unscaledTime,
                Frame = Time.frameCount
            };
        }

        private static CurveVisualController FindCurveVisualController(NearFarInteractor nearFarInteractor, Transform controller)
        {
            if (nearFarInteractor != null)
            {
                CurveVisualController curveVisual = nearFarInteractor.GetComponentInChildren<CurveVisualController>(true);
                if (curveVisual != null)
                {
                    return curveVisual;
                }
            }

            return controller != null ? controller.GetComponentInChildren<CurveVisualController>(true) : null;
        }

        private static Transform FindLineVisualRoot(Transform root)
        {
            if (root == null)
            {
                return null;
            }

            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child != null && string.Equals(child.name, "LineVisual", StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }
            }

            return null;
        }

        private static Behaviour FindBehaviourByTypeName(Transform root, string typeName)
        {
            if (root == null)
            {
                return null;
            }

            foreach (Behaviour behaviour in root.GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour != null && string.Equals(behaviour.GetType().Name, typeName, StringComparison.OrdinalIgnoreCase))
                {
                    return behaviour;
                }
            }

            return null;
        }

        private static string DescribeComponentInventory(Transform root)
        {
            var values = new List<string>();
            foreach (Component component in root.GetComponents<Component>())
            {
                if (component == null)
                {
                    AddLimited(values, "missing_component");
                    continue;
                }

                string enabled = component is Behaviour behaviour ? behaviour.enabled.ToString() : "n/a";
                string assembly = component.GetType().Assembly.GetName().Name;
                AddLimited(values, $"{component.GetType().FullName}@{assembly}:enabled={enabled}:hideFlags={component.hideFlags}");
            }

            return JoinOrNone(values);
        }

        private static string DescribeInputModalityManagers()
        {
            var values = new List<string>();
            foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (behaviour == null || behaviour.GetType().Name.IndexOf("InputModalityManager", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                AddLimited(values, DescribeBehaviour(behaviour));
            }

            return JoinOrNone(values);
        }

        private static string DescribeCurveDataProvider(ICurveInteractionDataProvider dataProvider)
        {
            if (dataProvider == null)
            {
                return "none";
            }

            if (dataProvider is Component component)
            {
                return $"{component.GetType().Name}:{GetPath(component.transform)}";
            }

            return dataProvider.GetType().Name;
        }

        private static string DescribeLineRendererPosition(LineRenderer lineRenderer, bool first)
        {
            if (lineRenderer == null || lineRenderer.positionCount <= 0)
            {
                return "none";
            }

            int index = first ? 0 : lineRenderer.positionCount - 1;
            return FormatVector(lineRenderer.GetPosition(index));
        }

        private static string InferHandFromPath(string path)
        {
            if (path.IndexOf("Left Controller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("/Left", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "left";
            }

            if (path.IndexOf("Right Controller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("/Right", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "right";
            }

            return "unknown";
        }

        private void AuditAndRepairUi(string reason, bool allowRepair)
        {
            EventSystem[] eventSystems = FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            EventSystem eventSystem = EventSystem.current ?? (eventSystems.Length > 0 ? eventSystems[0] : null);
            if (eventSystem == null && allowRepair)
            {
                eventSystem = new GameObject("EventSystem").AddComponent<EventSystem>();
                Log("xri_prefab_repair_applied", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["repair"] = "create_eventsystem_for_xr_ui"
                });
            }

            if (eventSystem != null)
            {
                if (!eventSystem.gameObject.activeSelf && allowRepair)
                {
                    eventSystem.gameObject.SetActive(true);
                }

                XRUIInputModule xrUiInput = eventSystem.GetComponent<XRUIInputModule>();
                if (xrUiInput == null && allowRepair)
                {
                    xrUiInput = eventSystem.gameObject.AddComponent<XRUIInputModule>();
                    Log("xri_prefab_repair_applied", new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["repair"] = "add_xr_ui_input_module",
                        ["path"] = GetPath(eventSystem.transform)
                    });
                }

                if (xrUiInput != null && !xrUiInput.enabled)
                {
                    if (allowRepair)
                    {
                        xrUiInput.enabled = true;
                    }

                    Log("xri_ui_input_module_ok", new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["path"] = GetPath(xrUiInput.transform),
                        ["enabled_after_repair"] = xrUiInput.enabled,
                        ["repair_allowed"] = allowRepair
                    });
                }

                DisableConflictingUiModules(eventSystem, reason, allowRepair);
            }

            Log("xri_eventsystem_audit", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["eventsystem_count"] = eventSystems.Length,
                ["current"] = eventSystem != null ? GetPath(eventSystem.transform) : "none",
                ["current_active"] = eventSystem != null && eventSystem.gameObject.activeInHierarchy,
                ["modules"] = eventSystem != null ? DescribeUiModules(eventSystem) : "none"
            });

            AuditAndRepairCanvas(reason, allowRepair);
        }

        private void DisableConflictingUiModules(EventSystem eventSystem, string reason, bool allowRepair)
        {
            XRUIInputModule xrInput = eventSystem.GetComponent<XRUIInputModule>();
            foreach (BaseInputModule module in eventSystem.GetComponents<BaseInputModule>())
            {
                if (module == null || module == xrInput || !module.enabled)
                {
                    continue;
                }

                string typeName = module.GetType().Name;
                bool conflicts = string.Equals(typeName, "StandaloneInputModule", StringComparison.Ordinal) ||
                    string.Equals(typeName, "InputSystemUIInputModule", StringComparison.Ordinal);
                if (!conflicts)
                {
                    continue;
                }

                if (allowRepair)
                {
                    module.enabled = false;
                }

                Log("xri_conflicting_ui_module_disabled", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["path"] = GetPath(module.transform),
                    ["component"] = typeName,
                    ["repair_allowed"] = allowRepair,
                    ["enabled_after"] = module.enabled
                });
            }
        }

        private void AuditAndRepairCanvas(string reason, bool allowRepair)
        {
            ResolveCanvas();
            if (_targetCanvas == null)
            {
                Log("xri_canvas_raycaster_state", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["canvas"] = "none"
                });
                return;
            }

            TrackedDeviceGraphicRaycaster trackedRaycaster = _targetCanvas.GetComponent<TrackedDeviceGraphicRaycaster>();
            if (trackedRaycaster == null && allowRepair)
            {
                trackedRaycaster = _targetCanvas.gameObject.AddComponent<TrackedDeviceGraphicRaycaster>();
                Log("xri_prefab_repair_applied", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["repair"] = "add_tracked_device_graphic_raycaster",
                    ["canvas"] = GetPath(_targetCanvas.transform)
                });
            }

            if (trackedRaycaster != null && !trackedRaycaster.enabled)
            {
                if (allowRepair)
                {
                    trackedRaycaster.enabled = true;
                }

                Log("xri_tracked_device_graphic_raycaster_ok", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["canvas"] = GetPath(_targetCanvas.transform),
                    ["enabled_after"] = trackedRaycaster.enabled,
                    ["repair_allowed"] = allowRepair
                });
            }

            GraphicRaycaster graphicRaycaster = _targetCanvas.GetComponent<GraphicRaycaster>();
            Log("xri_canvas_raycaster_state", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["canvas"] = DescribeCanvas(_targetCanvas),
                ["tracked_device_graphic_raycaster"] = trackedRaycaster != null ? trackedRaycaster.enabled : false,
                ["graphic_raycaster"] = graphicRaycaster != null ? graphicRaycaster.enabled : false
            });
            LogCanvasGeometryAudit(reason);
            LogCanvasMaskCheck(reason);
            LogButtonRaycastTargets(reason);
            LogUiRaycastHitCheck(reason);
        }

        private void LogCanvasGeometryAudit(string reason)
        {
            ResolveCanvas();
            if (_targetCanvas == null)
            {
                Log("xri_canvas_geometry_audit", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["canvas"] = "none"
                });
                return;
            }

            RectTransform rect = _targetCanvas.transform as RectTransform;
            Vector3 center = GetCanvasCenter();
            Camera camera = Camera.main;
            float cameraDistance = camera != null ? Vector3.Distance(camera.transform.position, center) : -1f;
            float cameraAlignment = camera != null ? Vector3.Dot(camera.transform.forward, (center - camera.transform.position).normalized) : 0f;
            Log("xri_canvas_geometry_audit", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["canvas"] = DescribeCanvas(_targetCanvas),
                ["center"] = FormatVector(center),
                ["position"] = FormatVector(_targetCanvas.transform.position),
                ["rotation"] = FormatVector(_targetCanvas.transform.eulerAngles),
                ["scale"] = FormatVector(_targetCanvas.transform.lossyScale),
                ["rect"] = rect != null ? rect.rect.ToString() : "none",
                ["worldCamera"] = _targetCanvas.worldCamera != null ? GetPath(_targetCanvas.worldCamera.transform) : "none",
                ["cameraDistance"] = cameraDistance.ToString("F2"),
                ["cameraAlignment"] = cameraAlignment.ToString("F3")
            });

            Log("xri_canvas_world_camera_audit", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["canvas"] = GetPath(_targetCanvas.transform),
                ["worldCamera"] = _targetCanvas.worldCamera != null ? GetPath(_targetCanvas.worldCamera.transform) : "none",
                ["mainCamera"] = camera != null ? GetPath(camera.transform) : "none"
            });

            Log("xri_canvas_raycast_layer_audit", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["canvas"] = GetPath(_targetCanvas.transform),
                ["layer"] = LayerMask.LayerToName(_targetCanvas.gameObject.layer),
                ["trackedDeviceGraphicRaycaster"] = _targetCanvas.GetComponent<TrackedDeviceGraphicRaycaster>() != null,
                ["xrUiInputModule"] = EventSystem.current != null ? EventSystem.current.GetComponent<XRUIInputModule>() != null : false
            });

            Log("xri_canvas_geometry_no_fix_needed", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["canvas"] = GetPath(_targetCanvas.transform),
                ["note"] = "P46A-01H audits canvas geometry but does not redesign or reposition protocol UI."
            });
        }

        private void LogCanvasMaskCheck(string reason)
        {
            var interactors = new List<string>();
            int canvasLayer = _targetCanvas != null ? _targetCanvas.gameObject.layer : 0;
            foreach (Component interactor in FindNativeInteractors(null))
            {
                bool layerReachable = HasLayerInMask(interactor, "raycastMask", canvasLayer);
                AddLimited(interactors, $"{interactor.GetType().Name}:{GetPath(interactor.transform)}:layerReachable={layerReachable}");
            }

            Log("xri_canvas_layer_mask_check", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["canvas_layer"] = LayerMask.LayerToName(canvasLayer),
                ["interactors"] = JoinOrNone(interactors)
            });
        }

        private void LogButtonRaycastTargets(string reason)
        {
            int graphics = 0;
            int raycastTargets = 0;
            if (_targetCanvas != null)
            {
                foreach (Graphic graphic in _targetCanvas.GetComponentsInChildren<Graphic>(true))
                {
                    graphics++;
                    if (graphic.raycastTarget)
                    {
                        raycastTargets++;
                    }
                }
            }

            Log("xri_button_raycast_target_check", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["graphics"] = graphics,
                ["raycast_targets"] = raycastTargets
            });
        }

        private void LogUiRaycastHitCheck(string reason)
        {
            var hits = new List<string>();
            foreach (Component interactor in FindNativeInteractors(null))
            {
                Transform origin = GetRayOrigin(interactor) ?? interactor.transform;
                Vector3 toCanvas = GetCanvasCenter() - origin.position;
                float alignment = toCanvas.sqrMagnitude > 0.0001f ? Vector3.Dot(origin.forward, toCanvas.normalized) : 0f;
                AddLimited(hits, $"{interactor.GetType().Name}:{GetPath(interactor.transform)}:origin={GetPath(origin)}:alignToCanvas={alignment:F3}:distance={toCanvas.magnitude:F2}");
                LogNativeInteractorCanvasRaycast(reason, interactor, origin);
            }

            Log("xri_ui_raycast_hit_check", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["checks"] = JoinOrNone(hits)
            });
        }

        private void LogNativeInteractorCanvasRaycast(string reason, Component interactor, Transform origin)
        {
            ResolveCanvas();
            bool hit = false;
            string target = "none";
            string missReason = "canvas_missing";
            float distance = -1f;
            if (_targetCanvas != null && origin != null)
            {
                RectTransform rect = _targetCanvas.transform as RectTransform;
                Plane plane = new Plane(_targetCanvas.transform.forward, _targetCanvas.transform.position);
                Ray ray = new Ray(origin.position, origin.forward);
                if (rect != null && plane.Raycast(ray, out distance))
                {
                    Vector3 worldPoint = ray.GetPoint(distance);
                    Vector3 localPoint = rect.InverseTransformPoint(worldPoint);
                    hit = rect.rect.Contains(localPoint);
                    target = hit ? GetPath(_targetCanvas.transform) : "canvas_plane_outside_rect";
                    missReason = hit ? string.Empty : $"outside_canvas_rect_local={FormatVector(localPoint)}";
                    Log("xri_canvas_plane_intersection", new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["hand"] = InferHandFromPath(GetPath(interactor.transform)),
                        ["interactor"] = GetPath(interactor.transform),
                        ["origin"] = origin != null ? GetPath(origin) : "none",
                        ["distance"] = distance.ToString("F3"),
                        ["worldPoint"] = FormatVector(worldPoint),
                        ["localPoint"] = FormatVector(localPoint)
                    });
                    Log("xri_canvas_rect_hit_test", new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["hand"] = InferHandFromPath(GetPath(interactor.transform)),
                        ["canvas"] = GetPath(_targetCanvas.transform),
                        ["hit"] = hit,
                        ["target"] = target,
                        ["miss_reason"] = missReason
                    });
                }
                else
                {
                    missReason = "ray_does_not_cross_canvas_plane_forward";
                }
            }

            Log("xri_ui_raycast_from_native_interactor", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["hand"] = InferHandFromPath(GetPath(interactor.transform)),
                ["interactor"] = GetPath(interactor.transform),
                ["origin"] = origin != null ? GetPath(origin) : "none",
                ["hit"] = hit,
                ["target"] = target,
                ["canvas"] = _targetCanvas != null ? GetPath(_targetCanvas.transform) : "none",
                ["distance"] = distance.ToString("F3"),
                ["miss_reason"] = missReason
            });

            Log(hit ? "xri_native_ui_raycast_hit" : "xri_native_ui_raycast_miss", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["hand"] = InferHandFromPath(GetPath(interactor.transform)),
                ["interactor"] = GetPath(interactor.transform),
                ["origin"] = origin != null ? GetPath(origin) : "none",
                ["hit"] = hit,
                ["target"] = target,
                ["canvas"] = _targetCanvas != null ? GetPath(_targetCanvas.transform) : "none",
                ["distance"] = distance.ToString("F3"),
                ["miss_reason"] = missReason
            });
            Log("xri_native_ui_hover_target", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["hand"] = InferHandFromPath(GetPath(interactor.transform)),
                ["interactor"] = GetPath(interactor.transform),
                ["diagnostic"] = "native_hover_state_not_mutated_by_P46A_01G",
                ["canvas_plane_hit"] = hit,
                ["target"] = target
            });
            Log("xri_native_ui_select_action_observed", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["hand"] = InferHandFromPath(GetPath(interactor.transform)),
                ["interactor"] = GetPath(interactor.transform),
                ["diagnostic"] = "passive_raycast_snapshot; no click failure emitted without real select action",
                ["canvas_plane_hit"] = hit,
                ["target"] = target
            });
        }

        private void LogInteractorCounts(string reason, int disabledLegacy)
        {
            int xrRayCount = FindObjectsByType<XRRayInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            int nearFarCount = FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            int lineVisualCount = FindObjectsByType<XRInteractorLineVisual>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            int lineRendererCount = FindObjectsByType<LineRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;

            Log("xri_controller_interactor_state", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["xr_ray_count"] = xrRayCount,
                ["near_far_count"] = nearFarCount,
                ["line_visual_count"] = lineVisualCount,
                ["line_renderer_count"] = lineRendererCount,
                ["legacy_runtime_artifacts_disabled_or_detected"] = disabledLegacy,
                ["native_interactors"] = DescribeNativeInteractorList()
            });
            Log("xri_ray_visual_state", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["line_visual_count"] = lineVisualCount,
                ["line_renderer_count"] = lineRendererCount,
                ["active_native_visuals"] = CountActiveNativeLineVisuals(),
                ["active_own_runtime_visuals"] = CountActiveOwnRuntimeVisuals()
            });
        }

        private List<Component> FindNativeInteractors(Transform controllerRoot)
        {
            var interactors = new List<Component>();
            foreach (NearFarInteractor interactor in FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (IsNativeInteractorCandidate(interactor, controllerRoot))
                {
                    interactors.Add(interactor);
                }
            }

            foreach (XRRayInteractor interactor in FindObjectsByType<XRRayInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (IsNativeInteractorCandidate(interactor, controllerRoot))
                {
                    interactors.Add(interactor);
                }
            }

            return interactors;
        }

        private static bool IsNativeInteractorCandidate(Component interactor, Transform controllerRoot)
        {
            if (interactor == null)
            {
                return false;
            }

            string path = GetPath(interactor.transform);
            if (IsOwnRuntimeRayArtifact(interactor.transform) || IsTeleportPath(path))
            {
                return false;
            }

            if (controllerRoot != null && !interactor.transform.IsChildOf(controllerRoot) && interactor.transform != controllerRoot)
            {
                return false;
            }

            return path.IndexOf("Controller Visual", StringComparison.OrdinalIgnoreCase) < 0 &&
                path.IndexOf("ManualUiLaser", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private Transform FindControllerRoot(bool left)
        {
            string exactName = left ? "Left Controller" : "Right Controller";
            Transform best = null;
            int bestScore = int.MinValue;
            foreach (Transform transform in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (transform == null || !string.Equals(transform.name, exactName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string path = GetPath(transform);
                int score = path.IndexOf("XR Origin Hands", StringComparison.OrdinalIgnoreCase) >= 0 ? 100 : 0;
                if (path.IndexOf("Camera Offset", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 25;
                }

                if (path.IndexOf("Controller Visual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("Attach", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score -= 100;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = transform;
                }
            }

            return best;
        }

        private Transform FindHandRoot(bool left)
        {
            string exactName = left ? "Left Hand" : "Right Hand";
            foreach (Transform transform in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (transform != null && string.Equals(transform.name, exactName, StringComparison.OrdinalIgnoreCase))
                {
                    return transform;
                }
            }

            return null;
        }

        private bool AnyHandRootActiveInHierarchy()
        {
            Transform left = FindHandRoot(true);
            Transform right = FindHandRoot(false);
            return (left != null && left.gameObject.activeInHierarchy) ||
                (right != null && right.gameObject.activeInHierarchy);
        }

        private void AuditModalityOwners(string reason, bool allowRepair)
        {
            AuditModalityOwnerForSide(true, reason, allowRepair);
            AuditModalityOwnerForSide(false, reason, allowRepair);
        }

        private void AuditModalityOwnerForSide(bool left, string reason, bool allowRepair)
        {
            ModalityOwnerState state = DetectModalityOwner(left);
            XriRayOwner previous = left ? _lastLeftOwner : _lastRightOwner;
            Log("xri_modality_owner_detected", state.ToPayload(reason));

            if (state.ConflictDetected)
            {
                Log("xri_modality_owner_conflict_detected", state.ToPayload(reason));
            }

            if (state.Owner == XriRayOwner.None)
            {
                Log("xri_modality_owner_none", state.ToPayload(reason));
            }
            else if (IsControllerOwner(state.Owner))
            {
                Log("xri_modality_owner_controller", state.ToPayload(reason));
            }
            else
            {
                Log("xri_modality_owner_hand", state.ToPayload(reason));
            }

            if (previous != state.Owner)
            {
                Log("xri_modality_owner_changed", state.ToPayload(reason));
                if (left)
                {
                    _lastLeftOwner = state.Owner;
                }
                else
                {
                    _lastRightOwner = state.Owner;
                }

                if (state.Owner != XriRayOwner.None && allowRepair)
                {
                    RepairActiveOwnerVisual(state, $"{reason}_owner_changed", allowRepair);
                    Log("xri_curve_repair_applied_on_owner_change", state.ToPayload(reason));
                }
            }

            if (state.Owner == XriRayOwner.None || !state.RootActive || !state.NearFarActive || !state.ProviderActive)
            {
                if (state.LineRendererPositionCount == 0)
                {
                    Log("xri_curve_zero_positions_ignored_inactive_owner", state.ToPayload(reason));
                }
            }
            else if (state.LineRendererPositionCount == 0)
            {
                string key = state.Owner.ToString();
                _activeOwnerZeroPositionSamples.TryGetValue(key, out int samples);
                samples++;
                _activeOwnerZeroPositionSamples[key] = samples;
                Log("xri_curve_zero_positions_active_owner_sample", state.ToPayload($"{reason};sample={samples}"));
                if (samples >= 3)
                {
                    Log("xri_curve_zero_positions_active_owner_confirmed", state.ToPayload(reason));
                    RepairActiveOwnerVisual(state, $"{reason}_zero_positions_confirmed", allowRepair);
                }
            }
            else
            {
                _activeOwnerZeroPositionSamples[state.Owner.ToString()] = 0;
            }

            LogOwnerPathConsistency(state, reason);
            LogScreenRayState(state, reason);
        }

        private ModalityOwnerState DetectModalityOwner(bool left)
        {
            ModalityOwnerState controller = CaptureOwnerCandidate(left ? XriRayOwner.LeftController : XriRayOwner.RightController);
            ModalityOwnerState hand = CaptureOwnerCandidate(left ? XriRayOwner.LeftHand : XriRayOwner.RightHand);
            bool controllerActive = controller.RootActive && controller.NearFarActive && controller.NearFarEnabled;
            bool handActive = hand.RootActive && hand.NearFarActive && hand.NearFarEnabled;

            if (controllerActive && handActive)
            {
                int controllerScore = ScoreOwnerCandidate(controller);
                int handScore = ScoreOwnerCandidate(hand);
                ModalityOwnerState selected = controllerScore >= handScore ? controller : hand;
                selected.ConflictDetected = true;
                selected.ConflictSummary = $"controllerScore={controllerScore};handScore={handScore};controller={controller.DescribeShort()};hand={hand.DescribeShort()}";
                return selected;
            }

            if (controllerActive)
            {
                return controller;
            }

            if (handActive)
            {
                return hand;
            }

            ModalityOwnerState none = left
                ? new ModalityOwnerState { Owner = XriRayOwner.None, Side = "left", RootPath = "none" }
                : new ModalityOwnerState { Owner = XriRayOwner.None, Side = "right", RootPath = "none" };
            none.ControllerCandidate = controller.DescribeShort();
            none.HandCandidate = hand.DescribeShort();
            return none;
        }

        private ModalityOwnerState CaptureOwnerCandidate(XriRayOwner owner)
        {
            Transform root = GetOwnerRoot(owner);
            NearFarInteractor nearFar = FindNearFarInteractorUnderRoot(root);
            CurveVisualController curveVisual = nearFar != null ? FindCurveVisualController(nearFar, root) : null;
            LineRenderer lineRenderer = curveVisual != null ? curveVisual.lineRenderer : null;
            if (lineRenderer == null && curveVisual != null)
            {
                lineRenderer = curveVisual.GetComponent<LineRenderer>();
            }

            ICurveInteractionDataProvider provider = curveVisual != null ? curveVisual.curveInteractionDataProvider : null;
            return new ModalityOwnerState
            {
                Owner = owner,
                Side = OwnerToSide(owner),
                RootPath = root != null ? GetPath(root) : "none",
                RootActive = root != null && root.gameObject.activeInHierarchy,
                NearFarPath = nearFar != null ? GetPath(nearFar.transform) : "none",
                NearFarActive = nearFar != null && nearFar.gameObject.activeInHierarchy,
                NearFarEnabled = nearFar != null && nearFar.enabled,
                CurveVisualPath = curveVisual != null ? GetPath(curveVisual.transform) : "none",
                CurveVisualEnabled = curveVisual != null && curveVisual.enabled,
                ProviderPath = DescribeCurveDataProvider(provider),
                ProviderActive = provider != null && provider.isActive,
                LineRendererPath = lineRenderer != null ? GetPath(lineRenderer.transform) : "none",
                LineRendererEnabled = lineRenderer != null && lineRenderer.enabled,
                LineRendererPositionCount = lineRenderer != null ? lineRenderer.positionCount : -1,
                LineRendererMaterial = lineRenderer != null && lineRenderer.sharedMaterial != null ? lineRenderer.sharedMaterial.name : "none",
                RestingVisualLineLength = curveVisual != null ? curveVisual.restingVisualLineLength : -1f,
                ExtendLineToEmptyHit = curveVisual != null && curveVisual.extendLineToEmptyHit,
                ActiveInputModalityManagerState = DescribeInputModalityManagers()
            };
        }

        private void RepairActiveOwnerVisual(ModalityOwnerState state, string reason, bool allowRepair)
        {
            if (state.Owner == XriRayOwner.None)
            {
                return;
            }

            Transform root = GetOwnerRoot(state.Owner);
            NearFarInteractor nearFar = FindNearFarInteractorUnderRoot(root);
            if (nearFar == null)
            {
                Log("xri_curve_repair_skipped_inactive_owner", state.ToPayload($"{reason};near_far_missing"));
                return;
            }

            CurveVisualController curveVisual = FindCurveVisualController(nearFar, root);
            LineRenderer lineRenderer = curveVisual != null ? curveVisual.lineRenderer : null;
            if (curveVisual == null)
            {
                Log("xri_curve_repair_skipped_inactive_owner", state.ToPayload($"{reason};curve_visual_missing"));
                return;
            }

            ApplyStableStyleToCurveVisual(nearFar, curveVisual, lineRenderer, state.Owner, reason, allowRepair, false);
            if (allowRepair && root != null && root.gameObject.activeInHierarchy && nearFar.gameObject.activeInHierarchy && nearFar.enabled)
            {
                if (!curveVisual.enabled)
                {
                    curveVisual.enabled = true;
                }

                ICurveInteractionDataProvider provider = curveVisual.curveInteractionDataProvider;
                if (lineRenderer != null && provider != null && provider.isActive && !lineRenderer.enabled)
                {
                    lineRenderer.enabled = true;
                }
            }
        }

        private void LogOwnerPathConsistency(ModalityOwnerState state, string reason)
        {
            if (state.Owner == XriRayOwner.None || string.IsNullOrEmpty(state.NearFarPath) || string.Equals(state.NearFarPath, "none", StringComparison.Ordinal))
            {
                return;
            }

            bool mismatch = IsControllerOwner(state.Owner)
                ? state.NearFarPath.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) < 0 || state.NearFarPath.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) >= 0
                : state.NearFarPath.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) < 0;
            Log(mismatch ? "xri_owner_path_mismatch_detected" : "xri_owner_path_consistency_ok", state.ToPayload(reason));
        }

        private void LogScreenRayState(ModalityOwnerState state, string reason)
        {
            ResolveCanvas();
            string canvasPath = _targetCanvas != null ? GetPath(_targetCanvas.transform) : string.Empty;
            bool startScreen = canvasPath.IndexOf("StartScreen", StringComparison.OrdinalIgnoreCase) >= 0;
            string ownerEvent = startScreen ? "xri_start_screen_ray_owner" : "xri_runtime_protocol_ray_owner";
            string visualEvent = startScreen ? "xri_start_screen_ray_visual_state" : "xri_runtime_protocol_ray_visual_state";
            string defaultDetectedEvent = startScreen ? "xri_start_screen_default_line_detected" : "xri_runtime_protocol_default_line_detected";
            string defaultFixedEvent = startScreen ? "xri_start_screen_default_line_fixed" : "xri_runtime_protocol_default_line_fixed";
            string lineRendererMaterial = state.LineRendererMaterial ?? string.Empty;
            Log(ownerEvent, state.ToPayload(reason));
            Log(visualEvent, state.ToPayload(reason));
            if (lineRendererMaterial.IndexOf("Default", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Log(defaultDetectedEvent, state.ToPayload(reason));
                if (lineRendererMaterial.IndexOf("P46A-01H", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    Mathf.Approximately(state.RestingVisualLineLength, StableUiLineLengthMeters))
                {
                    Log(defaultFixedEvent, state.ToPayload(reason));
                }
            }
        }

        private static int ScoreOwnerCandidate(ModalityOwnerState state)
        {
            int score = 0;
            if (state.ProviderActive)
            {
                score += 4;
            }

            if (state.LineRendererPositionCount > 0)
            {
                score += 2;
            }

            if (state.LineRendererEnabled)
            {
                score += 1;
            }

            return score;
        }

        private NearFarInteractor FindNearFarInteractorUnderRoot(Transform root)
        {
            if (root == null)
            {
                return null;
            }

            foreach (NearFarInteractor nearFar in root.GetComponentsInChildren<NearFarInteractor>(true))
            {
                if (nearFar != null && !IsOwnRuntimeRayArtifact(nearFar.transform) && !IsTeleportPath(GetPath(nearFar.transform)))
                {
                    return nearFar;
                }
            }

            return null;
        }


        private void ResolveCanvas()
        {
            if (_targetCanvas != null)
            {
                return;
            }

            ExperimentRuntimeStartScreenUI startScreen = FindFirstObjectByType<ExperimentRuntimeStartScreenUI>();
            if (startScreen != null)
            {
                _targetCanvas = startScreen.GetComponent<Canvas>();
            }

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

        private bool IsCanvasVisible()
        {
            ResolveCanvas();
            return _targetCanvas != null && _targetCanvas.gameObject.activeInHierarchy;
        }

        private Vector3 GetCanvasCenter()
        {
            ResolveCanvas();
            if (_targetCanvas == null)
            {
                Camera camera = Camera.main;
                return camera != null ? camera.transform.position + camera.transform.forward * 2f : Vector3.forward * 2f;
            }

            RectTransform rect = _targetCanvas.transform as RectTransform;
            if (rect == null)
            {
                return _targetCanvas.transform.position;
            }

            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;
        }

        private Dictionary<string, object> BuildTransitionPayload(string reason)
        {
            return new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["left_controller_tracked"] = IsPhysicalControllerTracked(XRNode.LeftHand),
                ["right_controller_tracked"] = IsPhysicalControllerTracked(XRNode.RightHand),
                ["hand_tracking_seen"] = HasHandTrackingDevice(),
                ["left_controller"] = DescribeGroup(FindControllerRoot(true)),
                ["right_controller"] = DescribeGroup(FindControllerRoot(false)),
                ["left_hand"] = DescribeGroup(FindHandRoot(true)),
                ["right_hand"] = DescribeGroup(FindHandRoot(false)),
                ["devices"] = DescribeDevices()
            };
        }

        private static Dictionary<string, object> BuildComponentPayload(string reason, string hand, Component component, bool repairAllowed)
        {
            return new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["hand"] = hand,
                ["component"] = component != null ? component.GetType().Name : "none",
                ["path"] = component != null ? GetPath(component.transform) : "none",
                ["enabled_after"] = component is Behaviour behaviour && behaviour.enabled,
                ["active_in_hierarchy"] = component != null && component.gameObject.activeInHierarchy,
                ["repair_allowed"] = repairAllowed
            };
        }

        private string DescribeGroup(Transform root)
        {
            if (root == null)
            {
                return "none";
            }

            var components = new List<string>();
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                {
                    continue;
                }

                string typeName = component.GetType().Name;
                if (typeName.IndexOf("XR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Interactor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Line", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("ControllerInputActionManager", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("TrackedPoseDriver", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    AddLimited(components, $"{typeName}:enabled={(component is Behaviour behaviour ? behaviour.enabled : true)}:active={component.gameObject.activeInHierarchy}:path={GetPath(component.transform)}");
                }
            }

            return $"{DescribeTransform(root)};components={JoinOrNone(components)}";
        }

        private static string DescribeTransform(Transform transform)
        {
            if (transform == null)
            {
                return "none";
            }

            return $"path={GetPath(transform)};activeSelf={transform.gameObject.activeSelf};activeInHierarchy={transform.gameObject.activeInHierarchy};layer={LayerMask.LayerToName(transform.gameObject.layer)};pos={FormatVector(transform.position)};rot={FormatVector(transform.eulerAngles)};forward={FormatVector(transform.forward)};up={FormatVector(transform.up)};right={FormatVector(transform.right)}";
        }

        private static string DescribeBehaviour(Behaviour behaviour)
        {
            return behaviour == null
                ? "none"
                : $"{behaviour.GetType().Name}:path={GetPath(behaviour.transform)};enabled={behaviour.enabled};active={behaviour.gameObject.activeInHierarchy}";
        }

        private static string DescribeCanvas(Canvas canvas)
        {
            if (canvas == null)
            {
                return "none";
            }

            Camera worldCamera = canvas.worldCamera;
            return $"path={GetPath(canvas.transform)};active={canvas.gameObject.activeInHierarchy};renderMode={canvas.renderMode};worldCamera={(worldCamera != null ? GetPath(worldCamera.transform) : "none")};layer={LayerMask.LayerToName(canvas.gameObject.layer)};sortingLayer={canvas.sortingLayerName};sortingOrder={canvas.sortingOrder}";
        }

        private static string DescribeUiModules(EventSystem eventSystem)
        {
            var modules = new List<string>();
            foreach (BaseInputModule module in eventSystem.GetComponents<BaseInputModule>())
            {
                if (module != null)
                {
                    AddLimited(modules, $"{module.GetType().Name}:enabled={module.enabled}");
                }
            }

            return JoinOrNone(modules);
        }

        private string DescribeNativeInteractorList()
        {
            var values = new List<string>();
            foreach (Component interactor in FindNativeInteractors(null))
            {
                AddLimited(values, $"{interactor.GetType().Name}:path={GetPath(interactor.transform)}:enabled={(interactor is Behaviour behaviour && behaviour.enabled)}:active={interactor.gameObject.activeInHierarchy}");
            }

            return JoinOrNone(values);
        }

        private string DescribeDevices()
        {
            var devices = new List<XRInputDevice>();
            InputDevices.GetDevices(devices);
            var values = new List<string>();
            foreach (XRInputDevice device in devices)
            {
                string classification = ClassifyDevice(device, out string reason);
                AddLimited(values, $"{device.name}:valid={device.isValid}:characteristics={device.characteristics}:class={classification}:reason={reason}");
            }

            return JoinOrNone(values);
        }

        private void LogDeviceEvent(XRInputDevice device, string reason)
        {
            Log("xri_device_classification_snapshot", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["name"] = device.name,
                ["valid"] = device.isValid,
                ["characteristics"] = device.characteristics,
                ["classification"] = ClassifyDevice(device, out string classificationReason),
                ["classification_reason"] = classificationReason
            });
        }

        private static string ClassifyDevice(XRInputDevice device, out string reason)
        {
            InputDeviceCharacteristics characteristics = device.characteristics;
            string name = device.name ?? string.Empty;
            if ((characteristics & InputDeviceCharacteristics.HandTracking) != 0 ||
                name.IndexOf("Hand Interaction", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Palm Pose", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                reason = "hand_tracking_characteristic_or_pose_name";
                return "hand_tracking";
            }

            if ((characteristics & InputDeviceCharacteristics.Controller) != 0 &&
                (name.IndexOf("controller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 name.IndexOf("touch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (characteristics & InputDeviceCharacteristics.HeldInHand) != 0))
            {
                reason = "controller_characteristic_with_physical_name_or_held_in_hand";
                return "physical_controller";
            }

            reason = "no_physical_controller_or_hand_tracking_signature";
            return "other";
        }

        private static bool IsPhysicalControllerDevice(XRInputDevice device)
        {
            return string.Equals(ClassifyDevice(device, out _), "physical_controller", StringComparison.Ordinal);
        }

        private static bool IsPhysicalControllerTracked(XRNode node)
        {
            XRInputDevice device = InputDevices.GetDeviceAtXRNode(node);
            if (!device.isValid || !IsPhysicalControllerDevice(device))
            {
                return false;
            }

            return device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked;
        }

        private static bool IsDeviceTracked(XRInputDevice device)
        {
            return device.isValid && device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked;
        }

        private static bool HasHandTrackingDevice()
        {
            var devices = new List<XRInputDevice>();
            InputDevices.GetDevices(devices);
            foreach (XRInputDevice device in devices)
            {
                if (string.Equals(ClassifyDevice(device, out _), "hand_tracking", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private bool ControllersHaveActiveNativeVisuals()
        {
            return CountActiveNativeLineVisuals() > 0 &&
                (FindControllerRoot(true) != null || FindControllerRoot(false) != null);
        }

        private int CountActiveNativeLineVisuals()
        {
            int count = 0;
            foreach (XRInteractorLineVisual lineVisual in FindObjectsByType<XRInteractorLineVisual>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (lineVisual != null && !IsOwnRuntimeRayArtifact(lineVisual.transform) && lineVisual.isActiveAndEnabled)
                {
                    count++;
                }
            }

            foreach (CurveVisualController curveVisual in FindObjectsByType<CurveVisualController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (curveVisual != null && !IsOwnRuntimeRayArtifact(curveVisual.transform) && curveVisual.isActiveAndEnabled)
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountActiveOwnRuntimeVisuals()
        {
            int count = 0;
            foreach (LineRenderer lineRenderer in FindObjectsByType<LineRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (lineRenderer != null && lineRenderer.enabled && IsOwnRuntimeRayArtifact(lineRenderer.transform))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool HasComponentName(Transform transform, string typeName)
        {
            foreach (Component component in transform.GetComponents<Component>())
            {
                if (component != null && string.Equals(component.GetType().Name, typeName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string DescribeObjectReferences(MonoBehaviour behaviour)
        {
            var refs = new List<string>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (FieldInfo field in behaviour.GetType().GetFields(flags))
            {
                string name = field.Name;
                if (name.IndexOf("left", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("right", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("controller", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("hand", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                object value = field.GetValue(behaviour);
                if (value is GameObject gameObject)
                {
                    AddLimited(refs, $"{name}={GetPath(gameObject.transform)}");
                }
                else if (value is Component component)
                {
                    AddLimited(refs, $"{name}={GetPath(component.transform)}");
                }
            }

            return JoinOrNone(refs);
        }

        private static void EnsureBooleanProperty(Component component, string propertyName, bool expected, bool allowRepair, string eventName, string reason, string hand)
        {
            PropertyInfo property = component.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || property.PropertyType != typeof(bool) || !property.CanRead)
            {
                return;
            }

            bool current = (bool)property.GetValue(component);
            if (current == expected)
            {
                return;
            }

            if (allowRepair && property.CanWrite)
            {
                property.SetValue(component, expected);
            }

            Log(eventName, new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["hand"] = hand,
                ["component"] = component.GetType().Name,
                ["path"] = GetPath(component.transform),
                ["property"] = propertyName,
                ["expected"] = expected,
                ["actual_after"] = property.CanRead ? property.GetValue(component) : current,
                ["repair_allowed"] = allowRepair
            });
        }

        private static Transform GetRayOrigin(Component interactor)
        {
            if (interactor is IXRRayProvider rayProvider)
            {
                Transform nativeOrigin = rayProvider.GetOrCreateRayOrigin();
                if (nativeOrigin != null)
                {
                    return nativeOrigin;
                }
            }

            return GetTransformMember(interactor, "rayOriginTransform") ??
                GetTransformMember(interactor, "attachTransform") ??
                interactor.transform;
        }

        private static Transform GetTransformMember(Component component, string memberName)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo property = component.GetType().GetProperty(memberName, flags);
            if (property != null && typeof(Transform).IsAssignableFrom(property.PropertyType) && property.CanRead)
            {
                return property.GetValue(component) as Transform;
            }

            FieldInfo field = component.GetType().GetField(memberName, flags);
            if (field != null && typeof(Transform).IsAssignableFrom(field.FieldType))
            {
                return field.GetValue(component) as Transform;
            }

            return null;
        }

        private static bool TrySetTransformMember(Component component, string memberName, Transform value)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo property = component.GetType().GetProperty(memberName, flags);
            if (property != null && typeof(Transform).IsAssignableFrom(property.PropertyType) && property.CanWrite)
            {
                property.SetValue(component, value);
                return true;
            }

            FieldInfo field = component.GetType().GetField(memberName, flags);
            if (field != null && typeof(Transform).IsAssignableFrom(field.FieldType))
            {
                field.SetValue(component, value);
                return true;
            }

            return false;
        }

        private static bool HasLayerInMask(Component component, string propertyName, int layer)
        {
            PropertyInfo property = component.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || !property.CanRead)
            {
                return true;
            }

            object value = property.GetValue(component);
            int maskValue;
            if (value is LayerMask layerMask)
            {
                maskValue = layerMask.value;
            }
            else if (value is int intMask)
            {
                maskValue = intMask;
            }
            else
            {
                return true;
            }

            return (maskValue & (1 << layer)) != 0;
        }

        private static bool IsOwnRuntimeRayArtifact(Transform transform)
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

        private static bool IsForbiddenRayOrigin(string path)
        {
            return path.IndexOf("Controller Visual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("[Left Controller] Attach", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("[Right Controller] Attach", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("Left Hand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("Right Hand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("Teleport Stabilized Origin", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsTeleportPath(string path)
        {
            return path.IndexOf("Teleport", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void AddLimited(List<string> values, string value)
        {
            if (values == null || values.Count >= 16)
            {
                return;
            }

            values.Add(value);
        }

        private static string JoinOrNone(List<string> values)
        {
            return values == null || values.Count == 0 ? "none" : Limit(string.Join(" || ", values));
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F3},{value.y:F3},{value.z:F3})";
        }

        private static string Limit(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= MaxPayloadText)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, MaxPayloadText) + "...";
        }

        private static void Log(string eventType, Dictionary<string, object> payload)
        {
            payload ??= new Dictionary<string, object>();
            payload["diagnostic_scope"] = "P46A-01H";
            TiagoExperimentTelemetry.LogEvent(eventType, payload);

            var builder = new StringBuilder();
            int count = 0;
            foreach (KeyValuePair<string, object> pair in payload)
            {
                if (count++ >= 14)
                {
                    builder.Append(" | ...");
                    break;
                }

                if (builder.Length > 0)
                {
                    builder.Append(" | ");
                }

                builder.Append(pair.Key);
                builder.Append('=');
                builder.Append(pair.Value);
            }

            Debug.Log($"{LogPrefix} {eventType} | {Limit(builder.ToString())}");
        }

        private enum XriRayOwner
        {
            None,
            LeftController,
            RightController,
            LeftHand,
            RightHand
        }

        private Transform GetOwnerRoot(XriRayOwner owner)
        {
            return owner switch
            {
                XriRayOwner.LeftController => FindControllerRoot(true),
                XriRayOwner.RightController => FindControllerRoot(false),
                XriRayOwner.LeftHand => FindHandRoot(true),
                XriRayOwner.RightHand => FindHandRoot(false),
                _ => null
            };
        }

        private static bool IsControllerOwner(XriRayOwner owner)
        {
            return owner == XriRayOwner.LeftController || owner == XriRayOwner.RightController;
        }

        private static string OwnerToSide(XriRayOwner owner)
        {
            return owner == XriRayOwner.LeftController || owner == XriRayOwner.LeftHand
                ? "left"
                : owner == XriRayOwner.RightController || owner == XriRayOwner.RightHand
                    ? "right"
                    : "none";
        }

        private static XriRayOwner InferOwnerFromPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return XriRayOwner.None;
            }

            if (path.IndexOf("Left Controller", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return XriRayOwner.LeftController;
            }

            if (path.IndexOf("Right Controller", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return XriRayOwner.RightController;
            }

            if (path.IndexOf("Left Hand", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return XriRayOwner.LeftHand;
            }

            if (path.IndexOf("Right Hand", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return XriRayOwner.RightHand;
            }

            return XriRayOwner.None;
        }

        private struct ModalityOwnerState
        {
            public XriRayOwner Owner;
            public string Side;
            public string RootPath;
            public bool RootActive;
            public string NearFarPath;
            public bool NearFarActive;
            public bool NearFarEnabled;
            public string CurveVisualPath;
            public bool CurveVisualEnabled;
            public string ProviderPath;
            public bool ProviderActive;
            public string LineRendererPath;
            public bool LineRendererEnabled;
            public int LineRendererPositionCount;
            public string LineRendererMaterial;
            public float RestingVisualLineLength;
            public bool ExtendLineToEmptyHit;
            public bool ConflictDetected;
            public string ConflictSummary;
            public string ControllerCandidate;
            public string HandCandidate;
            public string ActiveInputModalityManagerState;

            public Dictionary<string, object> ToPayload(string reason)
            {
                return new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["side"] = Side,
                    ["owner"] = Owner.ToString(),
                    ["root"] = RootPath,
                    ["rootActive"] = RootActive,
                    ["nearFarPath"] = NearFarPath,
                    ["nearFarActive"] = NearFarActive,
                    ["nearFarEnabled"] = NearFarEnabled,
                    ["curveVisualPath"] = CurveVisualPath,
                    ["curveVisualEnabled"] = CurveVisualEnabled,
                    ["provider"] = ProviderPath,
                    ["providerActive"] = ProviderActive,
                    ["lineRendererPath"] = LineRendererPath,
                    ["lineRendererEnabled"] = LineRendererEnabled,
                    ["lineRendererPositionCount"] = LineRendererPositionCount,
                    ["lineRendererMaterial"] = LineRendererMaterial,
                    ["restingVisualLineLength"] = RestingVisualLineLength,
                    ["extendLineToEmptyHit"] = ExtendLineToEmptyHit,
                    ["conflict"] = ConflictDetected,
                    ["conflictSummary"] = ConflictSummary ?? string.Empty,
                    ["controllerCandidate"] = ControllerCandidate ?? string.Empty,
                    ["handCandidate"] = HandCandidate ?? string.Empty,
                    ["activeInputModalityManagerState"] = ActiveInputModalityManagerState ?? string.Empty
                };
            }

            public string DescribeShort()
            {
                return $"owner={Owner};rootActive={RootActive};nearFarActive={NearFarActive};nearFarEnabled={NearFarEnabled};providerActive={ProviderActive};lineEnabled={LineRendererEnabled};positions={LineRendererPositionCount};material={LineRendererMaterial};length={RestingVisualLineLength:F2}";
            }
        }

        private struct LineVisualSnapshot
        {
            public string Hand;
            public string ControllerPath;
            public bool ControllerActiveSelf;
            public bool ControllerActiveInHierarchy;
            public string NearFarPath;
            public bool NearFarActiveInHierarchy;
            public bool NearFarEnabled;
            public bool NearFarFarCastingEnabled;
            public string InteractionGroupPath;
            public bool InteractionGroupActiveInHierarchy;
            public bool InteractionGroupEnabled;
            public string ControllerInputActionManagerPath;
            public bool ControllerInputActionManagerActiveInHierarchy;
            public bool ControllerInputActionManagerEnabled;
            public string LineVisualPath;
            public bool LineVisualActiveSelf;
            public bool LineVisualActiveInHierarchy;
            public bool CurveVisualEnabled;
            public string CurveDataProviderPath;
            public bool CurveDataProviderActive;
            public string LineRendererPath;
            public bool LineRendererActiveInHierarchy;
            public bool LineRendererEnabled;
            public int LineRendererPositionCount;
            public string LineRendererFirstPosition;
            public string LineRendererLastPosition;
            public string LineRendererMaterial;
            public float LineRendererWidthStart;
            public float LineRendererWidthEnd;
            public int VisibleCandidateCount;
            public float Time;
            public int Frame;

            public Dictionary<string, object> ToPayload(string reason)
            {
                return new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["hand"] = Hand,
                    ["controllerRoot"] = ControllerPath,
                    ["controllerActiveSelf"] = ControllerActiveSelf,
                    ["controllerActiveInHierarchy"] = ControllerActiveInHierarchy,
                    ["nearFarPath"] = NearFarPath,
                    ["nearFarActiveInHierarchy"] = NearFarActiveInHierarchy,
                    ["nearFarEnabled"] = NearFarEnabled,
                    ["nearFarFarCastingEnabled"] = NearFarFarCastingEnabled,
                    ["xrInteractionGroup"] = $"{InteractionGroupPath};active={InteractionGroupActiveInHierarchy};enabled={InteractionGroupEnabled}",
                    ["controllerInputActionManager"] = $"{ControllerInputActionManagerPath};active={ControllerInputActionManagerActiveInHierarchy};enabled={ControllerInputActionManagerEnabled}",
                    ["lineVisualPath"] = LineVisualPath,
                    ["lineVisualActiveSelf"] = LineVisualActiveSelf,
                    ["lineVisualActiveInHierarchy"] = LineVisualActiveInHierarchy,
                    ["curveVisualControllerEnabled"] = CurveVisualEnabled,
                    ["curveDataProvider"] = CurveDataProviderPath,
                    ["curveDataProviderActive"] = CurveDataProviderActive,
                    ["lineRendererPath"] = LineRendererPath,
                    ["lineRendererActiveInHierarchy"] = LineRendererActiveInHierarchy,
                    ["lineRendererEnabled"] = LineRendererEnabled,
                    ["lineRendererPositionCount"] = LineRendererPositionCount,
                    ["lineRendererFirstPosition"] = LineRendererFirstPosition,
                    ["lineRendererLastPosition"] = LineRendererLastPosition,
                    ["lineRendererMaterial"] = LineRendererMaterial,
                    ["lineRendererWidthStart"] = LineRendererWidthStart,
                    ["lineRendererWidthEnd"] = LineRendererWidthEnd,
                    ["visibleCandidateCount"] = VisibleCandidateCount,
                    ["time"] = Time,
                    ["frame"] = Frame
                };
            }

            public string Describe()
            {
                return $"controllerActive={ControllerActiveInHierarchy};nearFarEnabled={NearFarEnabled};curveVisualEnabled={CurveVisualEnabled};curveDataActive={CurveDataProviderActive};lineRendererEnabled={LineRendererEnabled};positionCount={LineRendererPositionCount};visibleCandidates={VisibleCandidateCount};frame={Frame};time={Time:F2}";
            }
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
    }
}
