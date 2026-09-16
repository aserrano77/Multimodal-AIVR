using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using XRInputDevice = UnityEngine.XR.InputDevice;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class ExperimentRuntimeStableProtocolUiPointer : MonoBehaviour
    {
        private const string LogPrefix = "[P46A-01N][STABLE-PROTOCOL-UI-POINTER]";
        private const string DiagnosticScope = "P46A-01N";
        private const string GlobalInstructionsFrontCanvasRootName = "GlobalInstructionsRuntimeFrontCanvasRoot";
        private const int LeftPointerId = -46011;
        private const int RightPointerId = -46012;
        private const float MaxLength = 5.0f;
        private const float LineWidth = 0.009f;
        private const float OwnerGraceSeconds = 0.45f;
        private const float RaycastLogIntervalSeconds = 0.5f;
        private const StablePointerAimMode PrimaryAimMode = StablePointerAimMode.DirectControllerRay;
        private const StablePointerAxis LeftAxisOverride = StablePointerAxis.Forward;
        private const StablePointerAxis RightAxisOverride = StablePointerAxis.Forward;
        private static readonly Color LineStartColor = new Color(0.04f, 0.82f, 1f, 0.96f);
        private static readonly Color LineEndColor = new Color(0.04f, 0.55f, 1f, 0.40f);

        [SerializeField] private Canvas _targetCanvas;
        [SerializeField] private bool _enableInEditor;
        [SerializeField] private bool _enableHandStablePointer;

        private readonly StablePointerSide _left = new StablePointerSide(false);
        private readonly StablePointerSide _right = new StablePointerSide(true);
        private readonly Dictionary<LineRenderer, NativeLineState> _suppressedNativeLines = new Dictionary<LineRenderer, NativeLineState>();
        private Material _lineMaterial;
        private bool _protocolUiActive;
        private bool _rigLogged;
        private bool _diagnosticOnlyLogged;
        private bool _handPointerDisabledLogged;
        private bool _nativeSuppressionLogged;
        private bool _globalInstructionsFrontCanvasPointerActive;
        private string _lastGlobalInstructionsFrontCanvasPath = string.Empty;
        private float _nextRaycastLogAt;

        public static ExperimentRuntimeStableProtocolUiPointer EnsureAttached(GameObject protocolUiRoot, Canvas canvas)
        {
            if (protocolUiRoot == null)
            {
                return null;
            }

            Transform root = protocolUiRoot.transform.Find("StableProtocolUiPointerP46N") ??
                protocolUiRoot.transform.Find("StableProtocolUiPointerP46L") ??
                protocolUiRoot.transform.Find("StableProtocolUiPointerP46J") ??
                protocolUiRoot.transform.Find("StableProtocolUiPointerP46I");
            if (root == null)
            {
                var host = new GameObject("StableProtocolUiPointerP46N");
                host.transform.SetParent(protocolUiRoot.transform, false);
                root = host.transform;
            }

            ExperimentRuntimeStableProtocolUiPointer pointer =
                root.GetComponent<ExperimentRuntimeStableProtocolUiPointer>();
            if (pointer == null)
            {
                pointer = root.gameObject.AddComponent<ExperimentRuntimeStableProtocolUiPointer>();
            }

            pointer.Initialize(canvas);
            return pointer;
        }

        public void Initialize(Canvas canvas)
        {
            _targetCanvas = canvas;
        }

        private void Awake()
        {
            EnsureSideVisual(_left);
            EnsureSideVisual(_right);
        }

        private void OnEnable()
        {
            InputDevices.deviceConnected += OnInputDeviceChanged;
            InputDevices.deviceDisconnected += OnInputDeviceChanged;
            InputDevices.deviceConfigChanged += OnInputDeviceChanged;
        }

        private void OnDisable()
        {
            InputDevices.deviceConnected -= OnInputDeviceChanged;
            InputDevices.deviceDisconnected -= OnInputDeviceChanged;
            InputDevices.deviceConfigChanged -= OnInputDeviceChanged;
            DisableSide(_left, "component_disabled");
            DisableSide(_right, "component_disabled");
            RestoreNativeVisuals("component_disabled");
        }

        private void OnInputDeviceChanged(XRInputDevice device)
        {
            if (!device.isValid)
            {
                InvalidateInputDeviceCache(_left, "device_event_invalid");
                InvalidateInputDeviceCache(_right, "device_event_invalid");
                return;
            }

            if (!IsPhysicalControllerDevice(device))
            {
                return;
            }

            bool right = (device.characteristics & InputDeviceCharacteristics.Right) != 0;
            bool left = (device.characteristics & InputDeviceCharacteristics.Left) != 0;
            if (left)
            {
                InvalidateInputDeviceCache(_left, "device_event_left");
            }

            if (right)
            {
                InvalidateInputDeviceCache(_right, "device_event_right");
            }
        }

        private void Update()
        {
            if (!ShouldRun())
            {
                return;
            }

            ResolveCanvas();
            if (!IsProtocolCanvasVisible(_targetCanvas))
            {
                if (_protocolUiActive)
                {
                    Log("stable_ui_pointer_disabled_no_protocol_ui", BuildBasePayload("no_protocol_ui"));
                }

                _protocolUiActive = false;
                DisableSide(_left, "no_protocol_ui");
                DisableSide(_right, "no_protocol_ui");
                RestoreNativeVisuals("no_protocol_ui");
                return;
            }

            if (!_protocolUiActive)
            {
                _protocolUiActive = true;
                LogRigInvariants();
                Log("stable_ui_pointer_enabled_for_protocol_ui", BuildBasePayload("protocol_ui_visible"));
                Log("stable_ui_pointer_protocol_ui_control_active", BuildBasePayload("protocol_ui_visible"));
                Log("stable_ui_pointer_hand_pointer_disabled_by_default", BuildBasePayload("controller_first"));
                Log("stable_ui_pointer_direct_ray_mode_enabled", BuildBasePayload("direct_controller_ray"));
                Log("stable_ui_pointer_no_canvas_assisted_aim", BuildBasePayload("direct_controller_ray"));
                Log("stable_ui_pointer_no_runtime_axis_switching", BuildBasePayload("direct_controller_ray"));
                Log("stable_ui_pointer_last_valid_canvas_point_not_used_for_direction", BuildBasePayload("direct_controller_ray"));
                Log("stable_ui_pointer_last_valid_canvas_point_diagnostic_only", BuildBasePayload("direct_controller_ray"));
                Log("stable_ui_pointer_canvas_point_memory_disabled", BuildBasePayload("direct_controller_ray"));
                Log("stable_ui_pointer_no_target_magnetism", BuildBasePayload("direct_controller_ray"));
                Log("stable_ui_pointer_direction_smoothing_disabled_for_direct_ray", BuildBasePayload("direct_controller_ray"));
                Log("stable_ui_pointer_direction_raw_controller_axis_used", BuildBasePayload("direct_controller_ray"));
                Log("stable_ui_pointer_visual_and_click_use_direct_ray", BuildBasePayload("direct_controller_ray"));
                Log("stable_ui_pointer_axis_override_available", BuildBasePayload("left=Forward;right=Forward"));
                Log("stable_ui_pointer_log_volume_guard_active", BuildBasePayload("p46a01n_log_guard"));
                Log("stable_ui_pointer_snapshot_throttle_active", BuildBasePayload("p46a01n_snapshot_throttle"));
                Log("stable_ui_pointer_per_frame_logging_disabled", BuildBasePayload("p46a01n_no_per_frame_logcat"));
            }

            LogDiagnosticOnlyOnce();
            EnsureSideVisual(_left);
            EnsureSideVisual(_right);
            SuppressNativeVisuals("protocol_ui_active");
            ProcessSide(_left);
            ProcessSide(_right);
        }

        private void LateUpdate()
        {
            if (_protocolUiActive)
            {
                SuppressNativeVisuals("late_update_protocol_ui_active");
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

        private void ProcessSide(StablePointerSide side)
        {
            OwnerCandidate owner = SelectControllerOwnerForSide(side);
            bool hadAvailableOwner = side.OwnerAvailable;

            if (!owner.Valid)
            {
                ObserveHandOwner(side);
                if (side.LastValidOwner.Valid && Time.unscaledTime - side.LastValidOwnerAt <= OwnerGraceSeconds)
                {
                    owner = side.LastValidOwner;
                    Log("stable_ui_pointer_visual_grace_period_active", BuildSideOwnerPayload("owner_grace_period", side, owner));
                }
                else
                {
                    DisableSide(side, "no_controller_owner");
                    if (hadAvailableOwner)
                    {
                        ResetSideInteraction(side, "owner_lost");
                    }

                    side.OwnerAvailable = false;
                    return;
                }
            }
            else
            {
                side.LastValidOwner = owner;
                side.LastValidOwnerAt = Time.unscaledTime;
            }

            if (!hadAvailableOwner)
            {
                ResetSideAfterModalityChange(side, "controller_return");
                Log("stable_ui_pointer_controller_restored_after_hand_mode", BuildSideOwnerPayload("controller_return", side, owner));
                Log("stable_ui_pointer_click_restored_after_controller_return", BuildSideOwnerPayload("controller_return", side, owner));
            }

            side.OwnerAvailable = true;
            if (!string.Equals(side.LastOwnerName, owner.Name, StringComparison.Ordinal) ||
                side.LastAimTransform != owner.AimTransform)
            {
                if (!string.IsNullOrEmpty(side.LastOwnerName))
                {
                    Log("stable_ui_pointer_owner_changed", BuildSideOwnerPayload("owner_changed", side, owner));
                    Log("stable_ui_pointer_side_state_reset_after_modality_change", BuildSideOwnerPayload("owner_changed", side, owner));
                    ResetSideAfterModalityChange(side, "owner_changed");
                }

                side.LastOwnerName = owner.Name;
                side.LastAimTransform = owner.AimTransform;
                Log("stable_ui_pointer_side_owner_selected", BuildSideOwnerPayload("owner_selected", side, owner));
                Log("stable_ui_pointer_side_origin_selected", BuildSideOwnerPayload("origin_selected", side, owner));
            }

            StableProtocolUiRay ray = BuildStableRay(side, owner);
            UpdateVisual(side, ray);
            UiHit hit = RaycastProtocolUi(side, ray);
            ray.SelectedTarget = hit.Target;
            LogRayConsistency(side, ray, hit);
            UpdateHover(side, hit);
            UpdateClick(side, owner, hit);
        }

        private void ResolveCanvas()
        {
            Canvas globalInstructionsFrontCanvas = FindGlobalInstructionsFrontCanvas();
            if (IsGlobalInstructionsFrontCanvasVisible(globalInstructionsFrontCanvas))
            {
                if (_targetCanvas != globalInstructionsFrontCanvas)
                {
                    ResetSideInteraction(_left, "global_instructions_front_canvas_selected");
                    ResetSideInteraction(_right, "global_instructions_front_canvas_selected");
                    _targetCanvas = globalInstructionsFrontCanvas;
                    Log("stable_ui_pointer_active_canvas_detected", BuildBasePayload("global_instructions_front_canvas"));
                }

                if (!_globalInstructionsFrontCanvasPointerActive ||
                    !string.Equals(_lastGlobalInstructionsFrontCanvasPath, GetPath(globalInstructionsFrontCanvas.transform), StringComparison.Ordinal))
                {
                    _globalInstructionsFrontCanvasPointerActive = true;
                    _lastGlobalInstructionsFrontCanvasPath = GetPath(globalInstructionsFrontCanvas.transform);
                    Log("global_instructions_front_canvas_pointer_enabled", BuildGlobalInstructionsFrontPointerPayload("front_canvas_active", null, default, default, false));
                    LogGlobalInstructionsButtonTargetsConfigured("front_canvas_active");
                }

                return;
            }

            if (_globalInstructionsFrontCanvasPointerActive)
            {
                Log("global_instructions_front_canvas_pointer_disabled", BuildGlobalInstructionsFrontPointerPayload("front_canvas_inactive", null, default, default, false));
                _globalInstructionsFrontCanvasPointerActive = false;
                _lastGlobalInstructionsFrontCanvasPath = string.Empty;
                ResetSideInteraction(_left, "global_instructions_front_canvas_disabled");
                ResetSideInteraction(_right, "global_instructions_front_canvas_disabled");
            }

            if (IsProtocolCanvasVisible(_targetCanvas))
            {
                return;
            }

            ExperimentRuntimeStartScreenUI start = FindFirstObjectByType<ExperimentRuntimeStartScreenUI>(FindObjectsInactive.Include);
            if (start != null)
            {
                Canvas canvas = start.GetComponentInChildren<Canvas>(true);
                if (canvas != null)
                {
                    _targetCanvas = canvas;
                    Log("stable_ui_pointer_active_canvas_detected", BuildBasePayload("start_screen_canvas"));
                    return;
                }
            }

            ExperimentRuntimeProtocolUI protocol = FindFirstObjectByType<ExperimentRuntimeProtocolUI>(FindObjectsInactive.Include);
            if (protocol != null)
            {
                Canvas canvas = protocol.GetComponentInChildren<Canvas>(true);
                if (canvas != null)
                {
                    _targetCanvas = canvas;
                    Log("stable_ui_pointer_active_canvas_detected", BuildBasePayload("runtime_protocol_canvas"));
                    return;
                }
            }

            Log("stable_ui_pointer_no_active_canvas", BuildBasePayload("canvas_not_found"));
        }

        private static bool IsProtocolCanvasVisible(Canvas canvas)
        {
            if (canvas == null || !canvas.isActiveAndEnabled || !canvas.gameObject.activeInHierarchy)
            {
                return false;
            }

            if (IsGlobalInstructionsFrontCanvasVisible(canvas))
            {
                return true;
            }

            string path = GetPath(canvas.transform);
            return path.IndexOf("StartScreenUI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("RuntimeProtocolUI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                canvas.GetComponentInParent<ExperimentRuntimeStartScreenUI>() != null ||
                canvas.GetComponentInParent<ExperimentRuntimeProtocolUI>() != null;
        }

        private static Canvas FindGlobalInstructionsFrontCanvas()
        {
            foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (IsGlobalInstructionsFrontCanvasVisible(canvas))
                {
                    return canvas;
                }
            }

            return null;
        }

        private static bool IsGlobalInstructionsFrontCanvasVisible(Canvas canvas)
        {
            return canvas != null &&
                canvas.isActiveAndEnabled &&
                canvas.gameObject.activeInHierarchy &&
                string.Equals(canvas.gameObject.name, GlobalInstructionsFrontCanvasRootName, StringComparison.Ordinal);
        }

        private bool IsGlobalInstructionsFrontCanvasTargetActive()
        {
            return IsGlobalInstructionsFrontCanvasVisible(_targetCanvas);
        }

        private void EnsureSideVisual(StablePointerSide side)
        {
            if (side.LineRenderer != null)
            {
                side.LineRenderer.useWorldSpace = true;
                return;
            }

            string objectName = side.IsRight ? "RightStableProtocolUiPointerP46N" : "LeftStableProtocolUiPointerP46N";
            Transform child = transform.Find(objectName);
            GameObject visualRoot = child != null ? child.gameObject : new GameObject(objectName);
            visualRoot.transform.SetParent(transform, false);
            visualRoot.transform.localPosition = Vector3.zero;
            visualRoot.transform.localRotation = Quaternion.identity;
            visualRoot.transform.localScale = Vector3.one;

            LineRenderer line = visualRoot.GetComponent<LineRenderer>();
            if (line == null)
            {
                line = visualRoot.AddComponent<LineRenderer>();
            }

            line.useWorldSpace = true;
            line.positionCount = 2;
            line.numCornerVertices = 4;
            line.numCapVertices = 4;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.startWidth = LineWidth;
            line.endWidth = LineWidth;
            line.widthMultiplier = 1f;
            line.colorGradient = BuildGradient();
            line.sharedMaterial = GetLineMaterial();
            line.enabled = false;
            line.forceRenderingOff = false;
            side.LineRenderer = line;

            Log("stable_ui_pointer_side_created", BuildSidePayload("side_created", side));
            Log("stable_ui_pointer_line_renderer_world_space_enabled", BuildSidePayload("visual_created", side));
            Log("stable_ui_pointer_visual_parent_validated", new Dictionary<string, object>
            {
                ["side"] = side.Name,
                ["parent"] = GetPath(visualRoot.transform.parent),
                ["path"] = GetPath(visualRoot.transform),
                ["forbidden_parent"] = IsForbiddenOriginPath(GetPath(visualRoot.transform.parent))
            });
            Log("stable_ui_pointer_visual_no_default_line", BuildSidePayload("visual_created", side));
        }

        private Material GetLineMaterial()
        {
            if (_lineMaterial != null)
            {
                return _lineMaterial;
            }

            Shader shader = Shader.Find("Sprites/Default") ??
                Shader.Find("Universal Render Pipeline/Unlit") ??
                Shader.Find("Unlit/Color") ??
                Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                return null;
            }

            _lineMaterial = new Material(shader)
            {
                name = "P46A-01N Stable Protocol UI Pointer"
            };
            _lineMaterial.color = LineStartColor;
            if (_lineMaterial.HasProperty("_BaseColor"))
            {
                _lineMaterial.SetColor("_BaseColor", LineStartColor);
            }

            return _lineMaterial;
        }

        private static Gradient BuildGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(LineStartColor, 0f),
                    new GradientColorKey(LineStartColor, 0.65f),
                    new GradientColorKey(LineEndColor, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(LineStartColor.a, 0f),
                    new GradientAlphaKey(0.86f, 0.65f),
                    new GradientAlphaKey(LineEndColor.a, 1f)
                });
            return gradient;
        }

        private void SuppressNativeVisuals(string reason)
        {
            if (!_nativeSuppressionLogged)
            {
                _nativeSuppressionLogged = true;
                Log("stable_ui_pointer_native_visual_suppression_state", BuildBasePayload(reason));
                Log("stable_ui_pointer_native_visual_no_mid_ui_restore", BuildBasePayload("stable_pointer_active"));
            }

            foreach (LineRenderer line in FindObjectsByType<LineRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (line == null)
                {
                    continue;
                }

                if (line == _left.LineRenderer || line == _right.LineRenderer)
                {
                    Log("stable_ui_pointer_native_visual_not_our_pointer", new Dictionary<string, object>
                    {
                        ["path"] = GetPath(line.transform),
                        ["reason"] = "own_stable_pointer_preserved"
                    });
                    continue;
                }

                if (!IsNativeNearFarLineVisual(line.transform))
                {
                    continue;
                }

                if (!_suppressedNativeLines.ContainsKey(line))
                {
                    _suppressedNativeLines[line] = new NativeLineState
                    {
                        Enabled = line.enabled,
                        ForceRenderingOff = line.forceRenderingOff
                    };
                    Log("stable_ui_pointer_native_visual_suppressed", new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["path"] = GetPath(line.transform),
                        ["previous_enabled"] = line.enabled,
                        ["previous_force_rendering_off"] = line.forceRenderingOff
                    });
                    Log("stable_ui_pointer_native_visual_restore_scheduled", new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["path"] = GetPath(line.transform)
                    });
                }

                line.forceRenderingOff = true;
            }
        }

        private void RestoreNativeVisuals(string reason)
        {
            if (_suppressedNativeLines.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<LineRenderer, NativeLineState> pair in _suppressedNativeLines)
            {
                LineRenderer line = pair.Key;
                if (line == null)
                {
                    Log("stable_ui_pointer_native_visual_restore_skipped", new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["skip_reason"] = "line_renderer_missing"
                    });
                    continue;
                }

                line.enabled = pair.Value.Enabled;
                line.forceRenderingOff = pair.Value.ForceRenderingOff;
                Log("stable_ui_pointer_native_visual_restored", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["path"] = GetPath(line.transform),
                    ["enabled_after"] = line.enabled,
                    ["force_rendering_off_after"] = line.forceRenderingOff
                });
            }

            _suppressedNativeLines.Clear();
            _nativeSuppressionLogged = false;
        }

        private static bool IsNativeNearFarLineVisual(Transform transform)
        {
            if (transform == null)
            {
                return false;
            }

            string path = GetPath(transform);
            if (path.IndexOf("StableProtocolUiPointer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("Runtime UI Ray", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("ManualUiLaser", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("Teleport", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return path.IndexOf("Near-Far Interactor", StringComparison.OrdinalIgnoreCase) >= 0 &&
                path.IndexOf("LineVisual", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private OwnerCandidate SelectControllerOwnerForSide(StablePointerSide side)
        {
            Transform root = FindRoot(side.IsRight ? "Right Controller" : "Left Controller");
            if (root == null || !root.gameObject.activeInHierarchy)
            {
                return default;
            }

            string rootPath = GetPath(root);
            if (IsForbiddenOriginPath(rootPath))
            {
                Log("stable_ui_pointer_origin_rejected_forbidden_path", new Dictionary<string, object>
                {
                    ["side"] = side.Name,
                    ["owner"] = side.ControllerOwnerName,
                    ["path"] = rootPath
                });
                return default;
            }

            Transform aim = SelectDirectRayOrigin(side, root);
            bool tracked = RefreshInputDevice(side, "select_owner", false);
            if (!tracked)
            {
                Log("stable_ui_pointer_origin_fallback_used", new Dictionary<string, object>
                {
                    ["side"] = side.Name,
                    ["owner"] = side.ControllerOwnerName,
                    ["path"] = rootPath,
                    ["reason"] = "controller_root_active_but_tracking_partial"
                });
            }

            return new OwnerCandidate
            {
                Valid = true,
                IsController = true,
                IsRight = side.IsRight,
                Name = side.ControllerOwnerName,
                Root = root,
                PositionOrigin = aim != null ? aim : root,
                AimTransform = aim != null ? aim : root,
                Tracked = tracked
            };
        }

        private void ObserveHandOwner(StablePointerSide side)
        {
            Transform handRoot = FindRoot(side.IsRight ? "Right Hand" : "Left Hand");
            if (handRoot == null || !handRoot.gameObject.activeInHierarchy)
            {
                return;
            }

            Log("stable_ui_pointer_hand_owner_observed", new Dictionary<string, object>
            {
                ["side"] = side.Name,
                ["owner"] = side.HandOwnerName,
                ["path"] = GetPath(handRoot),
                ["enable_hand_stable_pointer"] = _enableHandStablePointer
            });

            if (!_enableHandStablePointer)
            {
                if (!_handPointerDisabledLogged)
                {
                    _handPointerDisabledLogged = true;
                    Log("stable_ui_pointer_hand_owner_ignored_by_design", new Dictionary<string, object>
                    {
                        ["side"] = side.Name,
                        ["owner"] = side.HandOwnerName,
                        ["reason"] = "P46A-01N_direct_controller_ray"
                    });
                }
            }
        }

        private Transform SelectDirectRayOrigin(StablePointerSide side, Transform controllerRoot)
        {
            Transform nearFar = FindNearFarInteractorTransform(controllerRoot);
            if (TryUseAimCandidate(side, nearFar, "near_far_interactor", out Transform selected))
            {
                TryLogRejectedCurveVisualOrigin(side, nearFar);
                Transform internalOrigin = TryFindInternalRayOrigin(side, nearFar);
                if (TryUseAimCandidate(side, internalOrigin, "near_far_internal_ray_origin", out Transform internalSelected))
                {
                    return internalSelected;
                }

                return selected;
            }

            foreach (string name in new[] { "Ray Origin", "Aim", "Controller Stabilized", "Stabilized" })
            {
                Transform named = FindChildContaining(controllerRoot, name);
                if (TryUseAimCandidate(side, named, name, out selected))
                {
                    return selected;
                }
            }

            Log("stable_ui_pointer_controller_root_position_only", new Dictionary<string, object>
            {
                ["side"] = side.Name,
                ["owner"] = side.ControllerOwnerName,
                ["path"] = GetPath(controllerRoot),
                ["reason"] = "no_safe_aim_transform_found"
            });
            return controllerRoot;
        }

        private void TryLogRejectedCurveVisualOrigin(StablePointerSide side, Transform nearFar)
        {
            if (nearFar == null)
            {
                return;
            }

            CurveVisualController curve = nearFar.GetComponentInChildren<CurveVisualController>(true);
            if (curve == null || curve.lineOriginTransform == null)
            {
                return;
            }

            Log("stable_ui_pointer_controller_visual_rejected", new Dictionary<string, object>
            {
                ["side"] = side.Name,
                ["source"] = "curve_visual_line_origin",
                ["path"] = GetPath(curve.lineOriginTransform),
                ["reason"] = "direct_controller_ray_does_not_use_curve_visual"
            });
        }

        private Transform TryFindInternalRayOrigin(StablePointerSide side, Transform nearFar)
        {
            if (nearFar == null)
            {
                return null;
            }

            Component[] components = nearFar.GetComponents<Component>();
            foreach (Component component in components)
            {
                if (component == null)
                {
                    continue;
                }

                Transform candidate = TryReadTransformMember(component, "rayOriginTransform") ??
                    TryReadTransformMember(component, "RayOriginTransform") ??
                    TryReadTransformMember(component, "lineOriginTransform") ??
                    TryReadTransformMember(component, "LineOriginTransform");
                if (candidate == null)
                {
                    continue;
                }

                string path = GetPath(candidate);
                if (IsForbiddenOriginPath(path))
                {
                    Log("stable_ui_pointer_attach_transform_rejected", new Dictionary<string, object>
                    {
                        ["side"] = side.Name,
                        ["source"] = component.GetType().Name,
                        ["path"] = path,
                        ["reason"] = "forbidden_internal_ray_origin"
                    });
                    continue;
                }

                return candidate;
            }

            return null;
        }

        private static Transform TryReadTransformMember(Component component, string memberName)
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = component.GetType();
            PropertyInfo property = type.GetProperty(memberName, Flags);
            if (property != null && typeof(Transform).IsAssignableFrom(property.PropertyType) && property.GetIndexParameters().Length == 0)
            {
                try
                {
                    return property.GetValue(component, null) as Transform;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            FieldInfo field = type.GetField(memberName, Flags);
            if (field != null && typeof(Transform).IsAssignableFrom(field.FieldType))
            {
                try
                {
                    return field.GetValue(component) as Transform;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            return null;
        }

        private bool TryUseAimCandidate(StablePointerSide side, Transform candidate, string source, out Transform selected)
        {
            selected = null;
            if (candidate == null)
            {
                return false;
            }

            string path = GetPath(candidate);
            Log("stable_ui_pointer_aim_candidate_found", new Dictionary<string, object>
            {
                ["side"] = side.Name,
                ["source"] = source,
                ["path"] = path,
                ["active"] = candidate.gameObject.activeInHierarchy
            });

            if (IsForbiddenOriginPath(path))
            {
                if (path.IndexOf("Attach", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("stable_ui_pointer_attach_transform_rejected", new Dictionary<string, object>
                    {
                        ["side"] = side.Name,
                        ["source"] = source,
                        ["path"] = path,
                        ["reason"] = "forbidden_origin"
                    });
                }

                if (path.IndexOf("Controller Visual", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("stable_ui_pointer_controller_visual_rejected", new Dictionary<string, object>
                    {
                        ["side"] = side.Name,
                        ["source"] = source,
                        ["path"] = path,
                        ["reason"] = "forbidden_origin"
                    });
                }

                Log("stable_ui_pointer_forbidden_aim_path_rejected", new Dictionary<string, object>
                {
                    ["side"] = side.Name,
                    ["source"] = source,
                    ["path"] = path
                });
                Log("stable_ui_pointer_aim_candidate_rejected", new Dictionary<string, object>
                {
                    ["side"] = side.Name,
                    ["source"] = source,
                    ["path"] = path,
                    ["reason"] = "forbidden_path"
                });
                return false;
            }

            selected = candidate;
            Log("stable_ui_pointer_aim_transform_selected", new Dictionary<string, object>
            {
                ["side"] = side.Name,
                ["source"] = source,
                ["path"] = path
            });
            Log("stable_ui_pointer_direct_ray_origin_selected", new Dictionary<string, object>
            {
                ["side"] = side.Name,
                ["source"] = source,
                ["path"] = path,
                ["aim_mode"] = PrimaryAimMode.ToString()
            });
            return true;
        }

        private StableProtocolUiRay BuildStableRay(StablePointerSide side, OwnerCandidate owner)
        {
            RectTransform rect = _targetCanvas != null ? _targetCanvas.transform as RectTransform : null;
            Vector3 origin = owner.PositionOrigin != null ? owner.PositionOrigin.position : transform.position;
            Transform aim = owner.AimTransform != null ? owner.AimTransform : owner.PositionOrigin;
            Vector3 canvasCenter = GetCanvasCenter(rect);
            StablePointerAxis axis = EnsureDirectRayAxisLocked(side, owner);
            Vector3 directDirection = ResolveAxisDirection(aim, axis);
            if (directDirection.sqrMagnitude < 0.0001f)
            {
                directDirection = aim != null ? aim.forward : transform.forward;
            }

            directDirection = directDirection.normalized;
            side.CurrentDirection = directDirection;
            side.LastValidDirection = directDirection;
            AxisCandidate direct = BuildAxisCandidate(GetAxisName(axis), directDirection, rect, origin, canvasCenter);

            if (direct.CrossesPlane)
            {
                side.LastValidCanvasPoint = direct.WorldPoint;
                side.HasLastValidCanvasPoint = true;
            }

            Vector3 endpoint = direct.CrossesPlane && direct.Distance > 0f
                ? origin + direct.Direction * direct.Distance
                : origin + direct.Direction * MaxLength;

            var ray = new StableProtocolUiRay
            {
                Side = side.Name,
                Owner = owner.Name,
                Origin = origin,
                Direction = direct.Direction.normalized,
                Endpoint = endpoint,
                AxisName = direct.Name,
                Canvas = _targetCanvas,
                CanvasHit = direct.CrossesPlane,
                RectHit = direct.InsideRect,
                WorldPoint = direct.WorldPoint,
                Distance = direct.Distance > 0f ? direct.Distance : MaxLength,
                ScreenPosition = _targetCanvas != null
                    ? RectTransformUtility.WorldToScreenPoint(_targetCanvas.worldCamera, direct.WorldPoint)
                    : Vector2.zero,
                VisualOnlyRecovery = false,
                Interactive = direct.InsideRect
            };
            if (ShouldLogSideState(side))
            {
                Log("stable_ui_pointer_ray_built", BuildAxisPayload("direct_ray_built", side, owner, direct));
                Log("stable_ui_pointer_direction_validated", BuildAxisPayload("direct_direction_validated", side, owner, direct));
                Log("stable_ui_pointer_direction_raw_controller_axis_used", BuildDirectionPayload("direct_raw_axis", side, owner, direct.Direction, direct.Direction));
                Log("stable_ui_pointer_visual_and_click_use_direct_ray", BuildRayPayload("visual_click_direct_ray", side, ray));
            }
            return ray;
        }

        private StablePointerAxis EnsureDirectRayAxisLocked(StablePointerSide side, OwnerCandidate owner)
        {
            if (side.DirectRayAxisLocked)
            {
                return side.DirectRayAxis;
            }

            StablePointerAxis axis = side.IsRight ? RightAxisOverride : LeftAxisOverride;
            side.DirectRayAxis = axis;
            side.DirectRayAxisLocked = true;
            side.LastAimMode = GetAxisName(axis);
            side.LastAxisName = side.LastAimMode;
            side.LastAimModeChangeTime = Time.unscaledTime;
            side.AimModeInvalidSince = -1f;

            var payload = BuildSideOwnerPayload("direct_ray_axis_locked", side, owner);
            payload["aim_mode"] = PrimaryAimMode.ToString();
            payload["axis"] = GetAxisName(axis);
            payload["left_axis_override"] = LeftAxisOverride.ToString();
            payload["right_axis_override"] = RightAxisOverride.ToString();
            Log("stable_ui_pointer_direct_ray_axis_selected", payload);
            Log("stable_ui_pointer_direct_ray_axis_locked", payload);
            Log("stable_ui_pointer_axis_override_applied", payload);
            Log("stable_ui_pointer_no_runtime_axis_switching", payload);
            return axis;
        }

        private static Vector3 ResolveAxisDirection(Transform aim, StablePointerAxis axis)
        {
            if (aim == null)
            {
                return Vector3.forward;
            }

            switch (axis)
            {
                case StablePointerAxis.NegativeForward:
                    return -aim.forward;
                case StablePointerAxis.Up:
                    return aim.up;
                case StablePointerAxis.NegativeUp:
                    return -aim.up;
                case StablePointerAxis.Forward:
                default:
                    return aim.forward;
            }
        }

        private static string GetAxisName(StablePointerAxis axis)
        {
            switch (axis)
            {
                case StablePointerAxis.NegativeForward:
                    return "-forward";
                case StablePointerAxis.Up:
                    return "up";
                case StablePointerAxis.NegativeUp:
                    return "-up";
                case StablePointerAxis.Forward:
                default:
                    return "forward";
            }
        }

        private AxisCandidate BuildAxisCandidate(string name, Vector3 direction, RectTransform rect, Vector3 origin, Vector3 canvasCenter)
        {
            direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
            Vector3 toCenter = canvasCenter - origin;
            float dot = toCenter.sqrMagnitude > 0.0001f ? Vector3.Dot(direction, toCenter.normalized) : 0f;
            bool backwards = dot < 0f;
            bool crossesPlane = false;
            bool inside = false;
            bool lateral = false;
            float outsideDistance = 9999f;
            float normalizedX = 0f;
            float normalizedY = 0f;
            float distance = MaxLength;
            Vector3 worldPoint = origin + direction * MaxLength;
            if (rect != null)
            {
                Plane plane = new Plane(rect.forward, rect.position);
                Ray ray = new Ray(origin, direction);
                if (plane.Raycast(ray, out float planeDistance) && planeDistance > 0f && planeDistance <= MaxLength)
                {
                    crossesPlane = true;
                    distance = planeDistance;
                    worldPoint = ray.GetPoint(planeDistance);
                    Vector3 local = rect.InverseTransformPoint(worldPoint);
                    inside = rect.rect.Contains(local);
                    outsideDistance = DistanceOutsideRect(rect.rect, local);
                    normalizedX = rect.rect.width > 0f ? Mathf.Abs((local.x - rect.rect.center.x) / (rect.rect.width * 0.5f)) : 0f;
                    normalizedY = rect.rect.height > 0f ? Mathf.Abs((local.y - rect.rect.center.y) / (rect.rect.height * 0.5f)) : 0f;
                    lateral = normalizedX > 1.8f && normalizedX > normalizedY * 1.2f;
                }
            }

            float centerDistance = Vector3.Distance(worldPoint, canvasCenter);
            float score = dot * 300f + (crossesPlane ? 150f : -150f) + (inside ? 1200f : -outsideDistance * 0.5f) - centerDistance * 20f;
            if (backwards)
            {
                score -= 10000f;
            }

            if (lateral)
            {
                score -= 5000f;
            }

            return new AxisCandidate
            {
                Name = name,
                Direction = direction,
                CrossesPlane = crossesPlane,
                InsideRect = inside,
                Backwards = backwards,
                Lateral = lateral,
                DotToCanvas = dot,
                Distance = distance,
                OutsideDistance = outsideDistance,
                Score = score,
                WorldPoint = worldPoint,
                NormalizedX = normalizedX,
                NormalizedY = normalizedY
            };
        }

        private static float DistanceOutsideRect(Rect rect, Vector3 local)
        {
            float dx = local.x < rect.xMin ? rect.xMin - local.x : local.x > rect.xMax ? local.x - rect.xMax : 0f;
            float dy = local.y < rect.yMin ? rect.yMin - local.y : local.y > rect.yMax ? local.y - rect.yMax : 0f;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private void UpdateVisual(StablePointerSide side, StableProtocolUiRay ray)
        {
            EnsureSideVisual(side);
            LineRenderer line = side.LineRenderer;
            if (line == null)
            {
                return;
            }

            line.useWorldSpace = true;
            line.SetPosition(0, ray.Origin);
            line.SetPosition(1, ray.Endpoint);
            side.RecordPointerSampleThisFrame = ShouldRecordPointerSample(side);
            if (side.RecordPointerSampleThisFrame)
            {
                ExperimentRuntimeXriRigTopologyDiagnostics.RecordStablePointerRay(
                    side.Name,
                    ray.Owner,
                    ray.Origin,
                    ray.Direction,
                    ray.Endpoint,
                    ray.AxisName,
                    ray.CanvasHit,
                    ray.RectHit,
                    ray.Canvas);
                ExperimentRuntimeXriRigTopologyDiagnostics.RecordStablePointerLinePositions(side.Name, line, ray.Origin, ray.Endpoint);
            }
            if (ShouldLogSideState(side))
            {
                Log("stable_ui_pointer_line_world_positions_written", BuildRayPayload("world_positions_written", side, ray));
            }
            if (!line.enabled)
            {
                line.enabled = true;
                Log("stable_ui_pointer_side_enabled", BuildRayPayload("side_enabled", side, ray));
                Log(side.IsRight ? "stable_ui_pointer_right_visual_visible" : "stable_ui_pointer_left_visual_visible", BuildRayPayload("visual_visible", side, ray));
                Log("stable_ui_pointer_visual_visible", BuildRayPayload("visual_visible", side, ray));
            }

            if (QuestLoggingPolicy.EmitLegacyContinuousDiagnostics &&
                Time.unscaledTime >= _nextRaycastLogAt)
            {
                Log("stable_ui_pointer_visual_updated", BuildRayPayload("visual_updated", side, ray));
                Log("stable_ui_pointer_visual_uses_same_ray_as_click", BuildRayPayload("visual_click_shared_ray", side, ray));
            }
        }

        private void DisableSide(StablePointerSide side, string reason)
        {
            if (side.LineRenderer != null && side.LineRenderer.enabled)
            {
                side.LineRenderer.enabled = false;
                Log("stable_ui_pointer_side_disabled", BuildSidePayload(reason, side));
                Log("stable_ui_pointer_visual_hidden", BuildSidePayload(reason, side));
            }

            ResetSideInteraction(side, reason);
            side.OwnerAvailable = false;
        }

        private UiHit RaycastProtocolUi(StablePointerSide side, StableProtocolUiRay ray)
        {
            UiHit hit = default;
            hit.ScreenPosition = ray.ScreenPosition;
            RectTransform rect = _targetCanvas != null ? _targetCanvas.transform as RectTransform : null;
            if (rect == null)
            {
                return hit;
            }

            if (!ray.CanvasHit)
            {
                LogRaycastThrottled("stable_ui_pointer_direct_ray_canvas_miss", side, "ray_does_not_cross_canvas_plane");
                RecordUiResultIfNeeded(side, ray.Owner, ray.WorldPoint, Vector3.zero, ray.ScreenPosition, false, false, null);
                return hit;
            }

            Vector3 localPoint = rect.InverseTransformPoint(ray.WorldPoint);
            bool rectHit = rect.rect.Contains(localPoint);
            hit.WorldPoint = ray.WorldPoint;
            hit.LocalPoint = localPoint;
            hit.ScreenPosition = ray.ScreenPosition;
            hit.CanvasRectHit = rectHit;
            hit.Interactive = ray.Interactive;
            TrackCanvasPointMotion(side, localPoint, ray.Interactive);
            LogRaycastThrottled("stable_ui_pointer_direct_ray_ux_snapshot", side, FormatVector(localPoint));
            if (!rectHit)
            {
                LogRaycastThrottled("stable_ui_pointer_direct_ray_canvas_rect_miss", side, FormatVector(localPoint));
                RecordUiResultIfNeeded(side, ray.Owner, hit.WorldPoint, hit.LocalPoint, hit.ScreenPosition, true, false, null);
                return hit;
            }

            LogRaycastThrottled("stable_ui_pointer_direct_ray_canvas_rect_hit", side, FormatVector(localPoint));
            if (QuestLoggingPolicy.EmitLegacyContinuousDiagnostics &&
                IsGlobalInstructionsFrontCanvasTargetActive())
            {
                Log("global_instructions_front_canvas_pointer_hit", BuildGlobalInstructionsFrontPointerPayload("front_canvas_rect_hit", side, ray, hit, true));
            }

            GameObject target = FindGraphicTarget(hit.ScreenPosition, side);
            hit.Target = target;
            if (target == null)
            {
                LogRaycastThrottled("stable_ui_pointer_direct_ray_no_target_no_click", side, "no_graphic_target");
                TrackTargetChange(side, null);
                LogNearestButtonDistance(side, localPoint);
                RecordUiResultIfNeeded(side, ray.Owner, hit.WorldPoint, hit.LocalPoint, hit.ScreenPosition, true, true, null);
                return hit;
            }

            hit.Valid = true;
            LogRaycastThrottled("stable_ui_pointer_direct_ray_graphic_hit", side, GetPath(target.transform));
            if (QuestLoggingPolicy.EmitLegacyContinuousDiagnostics &&
                IsGlobalInstructionsFrontCanvasTargetActive())
            {
                Log("global_instructions_front_canvas_target_button", BuildGlobalInstructionsFrontPointerPayload("front_canvas_target", side, ray, hit, true));
            }

            TrackTargetChange(side, target);
            if (target.GetComponent<Selectable>() != null)
            {
                LogRaycastThrottled("stable_ui_pointer_selectable_target_found", side, GetPath(target.transform));
            }
            RecordUiResultIfNeeded(side, ray.Owner, hit.WorldPoint, hit.LocalPoint, hit.ScreenPosition, true, true, target);
            return hit;
        }

        private void RecordUiResultIfNeeded(
            StablePointerSide side,
            string owner,
            Vector3 worldPoint,
            Vector3 localPoint,
            Vector2 screenPoint,
            bool canvasHit,
            bool rectHit,
            GameObject target)
        {
            if (!QuestLoggingPolicy.EmitLegacyContinuousDiagnostics)
            {
                return;
            }

            string targetPath = target != null ? GetPath(target.transform) : "none";
            bool targetChanged = !string.Equals(side.LastRecordedTargetPath, targetPath, StringComparison.Ordinal);
            if (!side.RecordPointerSampleThisFrame && !targetChanged)
            {
                return;
            }

            side.LastRecordedTargetPath = targetPath;
            ExperimentRuntimeXriRigTopologyDiagnostics.RecordStablePointerUiResult(
                side.Name,
                owner,
                _targetCanvas,
                worldPoint,
                localPoint,
                screenPoint,
                canvasHit,
                rectHit,
                target);
        }

        private GameObject FindGraphicTarget(Vector2 screenPosition, StablePointerSide side)
        {
            if (_targetCanvas == null)
            {
                return null;
            }

            Camera eventCamera = _targetCanvas.worldCamera;
            Graphic bestGraphic = null;
            int bestDepth = int.MinValue;
            IList<Graphic> graphics = GraphicRegistry.GetGraphicsForCanvas(_targetCanvas);
            for (int i = 0; i < graphics.Count; i++)
            {
                Graphic graphic = graphics[i];
                if (graphic == null || !graphic.raycastTarget || !graphic.gameObject.activeInHierarchy || graphic.canvasRenderer.cull)
                {
                    continue;
                }

                if (!RectTransformUtility.RectangleContainsScreenPoint(graphic.rectTransform, screenPosition, eventCamera) ||
                    !graphic.Raycast(screenPosition, eventCamera))
                {
                    continue;
                }

                if (graphic.depth > bestDepth)
                {
                    bestDepth = graphic.depth;
                    bestGraphic = graphic;
                }
            }

            if (bestGraphic == null)
            {
                return null;
            }

            Selectable selectable = bestGraphic.GetComponentInParent<Selectable>();
            GameObject target = selectable != null && selectable.IsInteractable() && selectable.gameObject.activeInHierarchy
                ? selectable.gameObject
                : bestGraphic.gameObject;
            LogRaycastThrottled("stable_ui_pointer_button_candidate", side, GetPath(target.transform));
            if (QuestLoggingPolicy.EmitLegacyContinuousDiagnostics &&
                IsGlobalInstructionsFrontCanvasTargetActive())
            {
                Log("global_instructions_front_canvas_button_candidate", BuildGlobalInstructionsFrontGraphicPayload("front_canvas_candidate", side, target, bestGraphic));
            }

            return target;
        }

        private void UpdateHover(StablePointerSide side, UiHit hit)
        {
            GameObject target = hit.Valid ? hit.Target : null;
            if (target == side.HoverTarget)
            {
                return;
            }

            PointerEventData eventData = BuildPointerEvent(side, hit);
            if (side.HoverTarget != null)
            {
                ExecuteEvents.Execute(side.HoverTarget, eventData, ExecuteEvents.pointerExitHandler);
            }

            side.HoverTarget = target;
            if (side.HoverTarget != null)
            {
                ExecuteEvents.Execute(side.HoverTarget, eventData, ExecuteEvents.pointerEnterHandler);
            }
        }

        private void UpdateClick(StablePointerSide side, OwnerCandidate owner, UiHit hit)
        {
            bool pressed = owner.IsController && ReadSelectPressed(side, "update_click");
            if (side.WaitingForReleasedAfterModalityChange)
            {
                Log("stable_ui_pointer_waiting_for_released_after_modality_change", BuildSideOwnerPayload("waiting_for_release", side, owner));
                if (!pressed)
                {
                    side.WaitingForReleasedAfterModalityChange = false;
                    side.ClickEnabledAfterModalityChange = true;
                    Log("stable_ui_pointer_released_observed_after_modality_change", BuildSideOwnerPayload("released_observed", side, owner));
                    Log("stable_ui_pointer_click_reenabled_after_modality_change", BuildSideOwnerPayload("click_reenabled", side, owner));
                }

                side.SelectWasPressed = pressed;
                return;
            }

            if (!hit.Interactive)
            {
                if (pressed && !side.SelectWasPressed)
                {
                    Log("stable_ui_pointer_direct_ray_no_target_no_click", BuildSideOwnerPayload("direct_ray_no_interactive_target", side, owner));
                }

                side.SelectWasPressed = pressed;
                return;
            }

            if (pressed && !side.SelectWasPressed)
            {
                Log("stable_ui_pointer_side_select_pressed", BuildSideOwnerPayload("select_pressed", side, owner));
                ExperimentRuntimeXriRigTopologyDiagnostics.RecordStablePointerClick(
                    side.ClickEnabledAfterModalityChange ? "pressed_after_controller_return" : "pressed",
                    side.Name,
                    owner.Name,
                    true,
                    hit.CanvasRectHit,
                    hit.Valid,
                    hit.Target);
                if (hit.Valid)
                {
                    side.PressedTarget = hit.Target;
                    PointerEventData eventData = BuildPointerEvent(side, hit);
                    ExecuteEvents.ExecuteHierarchy(side.PressedTarget, eventData, ExecuteEvents.pointerDownHandler);
                    Log("stable_ui_pointer_side_click_attempt", BuildClickPayload("click_attempt", side, owner, hit));
                    Log("stable_ui_pointer_click_attempt", BuildClickPayload("click_attempt", side, owner, hit));
                    if (side.ClickEnabledAfterModalityChange)
                    {
                        Log("stable_ui_pointer_click_attempt_after_controller_return", BuildClickPayload("click_attempt_after_controller_return", side, owner, hit));
                    }
                }
                else
                {
                    side.PressedTarget = null;
                    Log("stable_ui_pointer_side_click_no_target", BuildSideOwnerPayload("select_pressed_no_target", side, owner));
                    Log("stable_ui_pointer_click_blocked_no_target", BuildSideOwnerPayload("select_pressed_no_target", side, owner));
                    Log("stable_ui_pointer_direct_ray_no_target_no_click", BuildSideOwnerPayload("select_pressed_no_target", side, owner));
                }
            }

            if (!pressed && side.SelectWasPressed)
            {
                Log("stable_ui_pointer_side_select_released", BuildSideOwnerPayload("select_released", side, owner));
                PointerEventData eventData = BuildPointerEvent(side, hit);
                if (side.PressedTarget != null)
                {
                    ExecuteEvents.ExecuteHierarchy(side.PressedTarget, eventData, ExecuteEvents.pointerUpHandler);
                    if (hit.Valid && hit.Target == side.PressedTarget)
                    {
                        bool afterControllerReturn = side.ClickEnabledAfterModalityChange;
                        ExecuteEvents.ExecuteHierarchy(side.PressedTarget, eventData, ExecuteEvents.pointerClickHandler);
                        if (IsGlobalInstructionsFrontCanvasTargetActive())
                        {
                            Log("global_instructions_front_canvas_click_invoked", BuildGlobalInstructionsFrontPointerPayload("front_canvas_click_invoked", side, default, hit, false));
                        }

                        Log("stable_ui_pointer_side_click_sent", BuildClickPayload("click_sent", side, owner, hit));
                        Log("stable_ui_pointer_click_sent", BuildClickPayload("click_sent", side, owner, hit));
                        Log("stable_ui_pointer_direct_ray_click_sent", BuildClickPayload("direct_ray_click_sent", side, owner, hit));
                        if (afterControllerReturn)
                        {
                            Log("stable_ui_pointer_click_sent_after_controller_return", BuildClickPayload("click_sent_after_controller_return", side, owner, hit));
                            side.ClickEnabledAfterModalityChange = false;
                        }
                        ExperimentRuntimeXriRigTopologyDiagnostics.RecordStablePointerClick(
                            afterControllerReturn ? "sent_after_controller_return" : "sent",
                            side.Name,
                            owner.Name,
                            false,
                            hit.CanvasRectHit,
                            hit.Valid,
                            hit.Target);
                    }
                    else
                    {
                        Log("stable_ui_pointer_side_click_no_target", BuildSideOwnerPayload("release_target_mismatch_or_missing", side, owner));
                        ExperimentRuntimeXriRigTopologyDiagnostics.RecordStablePointerClick(
                            "failed_target_mismatch",
                            side.Name,
                            owner.Name,
                            false,
                            hit.CanvasRectHit,
                            hit.Valid,
                            hit.Target);
                    }
                }

                side.PressedTarget = null;
            }

            side.SelectWasPressed = pressed;
        }

        private bool ReadSelectPressed(StablePointerSide side, string reason)
        {
            if (!RefreshInputDevice(side, reason, true))
            {
                return false;
            }

            if (side.InputDevice.TryGetFeatureValue(CommonUsages.triggerButton, out bool triggerButton) && triggerButton)
            {
                return true;
            }

            if (side.InputDevice.TryGetFeatureValue(CommonUsages.primaryButton, out bool primaryButton) && primaryButton)
            {
                return true;
            }

            return side.InputDevice.TryGetFeatureValue(CommonUsages.trigger, out float trigger) && trigger >= 0.72f;
        }

        private PointerEventData BuildPointerEvent(StablePointerSide side, UiHit hit)
        {
            EventSystem eventSystem = EventSystem.current;
            var eventData = new PointerEventData(eventSystem)
            {
                pointerId = side.IsRight ? RightPointerId : LeftPointerId,
                position = hit.ScreenPosition,
                pressPosition = hit.ScreenPosition,
                button = PointerEventData.InputButton.Left,
                pointerCurrentRaycast = new RaycastResult
                {
                    gameObject = hit.Target,
                    module = _targetCanvas != null ? _targetCanvas.GetComponent<BaseRaycaster>() : null,
                    worldPosition = hit.WorldPoint,
                    screenPosition = hit.ScreenPosition
                },
                pointerPressRaycast = new RaycastResult
                {
                    gameObject = hit.Target,
                    module = _targetCanvas != null ? _targetCanvas.GetComponent<BaseRaycaster>() : null,
                    worldPosition = hit.WorldPoint,
                    screenPosition = hit.ScreenPosition
                }
            };
            return eventData;
        }

        private void ResetSideInteraction(StablePointerSide side, string reason)
        {
            if (side.HoverTarget != null)
            {
                ExecuteEvents.Execute(side.HoverTarget, new PointerEventData(EventSystem.current), ExecuteEvents.pointerExitHandler);
            }

            side.HoverTarget = null;
            side.PressedTarget = null;
            side.SelectWasPressed = false;
            side.LastAxisName = string.Empty;
            if (reason.IndexOf("controller_return", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("owner_changed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Log("stable_ui_pointer_side_state_reset_after_modality_change", BuildSidePayload(reason, side));
            }
        }

        private void ResetSideAfterModalityChange(StablePointerSide side, string reason)
        {
            ResetSideInteraction(side, reason);
            InvalidateInputDeviceCache(side, reason);
            side.WaitingForReleasedAfterModalityChange = true;
            side.ClickEnabledAfterModalityChange = false;
            side.RecoveryWasActive = false;
            side.PointerHiddenUntilRealAxis = false;
            side.CurrentDirection = Vector3.zero;
            side.LastAimMode = string.Empty;
            side.AimModeInvalidSince = -1f;
            side.DirectRayAxisLocked = false;
            side.HasLastValidCanvasPoint = false;
            side.HasLastCanvasLocalPoint = false;
            Log("stable_ui_pointer_select_state_reset_after_modality_change", BuildSidePayload(reason, side));
            Log("stable_ui_pointer_waiting_for_released_after_modality_change", BuildSidePayload(reason, side));
            Log("stable_ui_pointer_direct_ray_mode_enabled", BuildSidePayload(reason, side));
            Log("stable_ui_pointer_no_canvas_assisted_aim", BuildSidePayload(reason, side));
            Log("stable_ui_pointer_no_runtime_axis_switching", BuildSidePayload(reason, side));
        }

        private void InvalidateInputDeviceCache(StablePointerSide side, string reason)
        {
            side.HasInputDevice = false;
            side.InputDevice = default;
            Log("stable_ui_pointer_input_device_cache_invalidated", BuildSidePayload(reason, side));
        }

        private bool RefreshInputDevice(StablePointerSide side, string reason, bool logFailures)
        {
            if (side.HasInputDevice && side.InputDevice.isValid && IsPhysicalControllerDevice(side.InputDevice))
            {
                return IsDeviceTracked(side.InputDevice);
            }

            XRNode node = side.IsRight ? XRNode.RightHand : XRNode.LeftHand;
            XRInputDevice nodeDevice = InputDevices.GetDeviceAtXRNode(node);
            if (nodeDevice.isValid && IsPhysicalControllerDevice(nodeDevice))
            {
                side.InputDevice = nodeDevice;
                side.HasInputDevice = true;
                Log("stable_ui_pointer_input_device_reacquired", BuildInputPayload(reason, side, nodeDevice));
                return IsDeviceTracked(nodeDevice);
            }

            var devices = new List<XRInputDevice>();
            InputDevices.GetDevices(devices);
            InputDeviceCharacteristics handedness = side.IsRight ? InputDeviceCharacteristics.Right : InputDeviceCharacteristics.Left;
            for (int i = 0; i < devices.Count; i++)
            {
                XRInputDevice device = devices[i];
                if (!device.isValid || !IsPhysicalControllerDevice(device) || (device.characteristics & handedness) == 0)
                {
                    continue;
                }

                side.InputDevice = device;
                side.HasInputDevice = true;
                Log("stable_ui_pointer_input_device_reacquired", BuildInputPayload(reason, side, device));
                return IsDeviceTracked(device);
            }

            if (logFailures)
            {
                Log("stable_ui_pointer_input_device_reacquire_failed", BuildSidePayload(reason, side));
            }

            side.HasInputDevice = false;
            side.InputDevice = default;
            return false;
        }

        private void LogRayConsistency(StablePointerSide side, StableProtocolUiRay ray, UiHit hit)
        {
            bool consistent = !hit.CanvasRectHit || Vector3.Distance(ray.WorldPoint, hit.WorldPoint) < 0.002f;
            if (!consistent || ShouldLogSideState(side))
            {
                Log(consistent ? "stable_ui_pointer_ray_consistency_ok" : "stable_ui_pointer_ray_consistency_failed",
                    BuildRayPayload(consistent ? "ray_consistency_ok" : "ray_consistency_failed", side, ray));
            }
        }

        private void TrackCanvasPointMotion(StablePointerSide side, Vector3 localPoint, bool interactive)
        {
            if (!interactive)
            {
                return;
            }

            if (!side.HasLastCanvasLocalPoint)
            {
                side.LastCanvasLocalPoint = localPoint;
                side.LastCanvasPointMoveAt = Time.unscaledTime;
                side.HasLastCanvasLocalPoint = true;
                return;
            }

            float delta = Vector2.Distance(new Vector2(localPoint.x, localPoint.y), new Vector2(side.LastCanvasLocalPoint.x, side.LastCanvasLocalPoint.y));
            if (delta > 4f)
            {
                side.LastCanvasLocalPoint = localPoint;
                side.LastCanvasPointMoveAt = Time.unscaledTime;
                Log("stable_ui_pointer_direct_ray_hit_point_moved", BuildCanvasPointPayload("direct_ray_hit_point_moved", side, localPoint, delta));
                Log("stable_ui_pointer_direct_ray_local_point_delta", BuildCanvasPointPayload("direct_ray_local_point_delta", side, localPoint, delta));
                return;
            }
        }

        private void TrackTargetChange(StablePointerSide side, GameObject target)
        {
            string targetPath = target != null ? GetPath(target.transform) : "none";
            if (string.Equals(side.LastDirectRayTargetPath, targetPath, StringComparison.Ordinal))
            {
                return;
            }

            bool hadTarget = !string.IsNullOrEmpty(side.LastDirectRayTargetPath) &&
                !string.Equals(side.LastDirectRayTargetPath, "none", StringComparison.Ordinal);
            side.LastDirectRayTargetPath = targetPath;

            if (target != null)
            {
                Log("stable_ui_pointer_direct_ray_target_acquired", new Dictionary<string, object>
                {
                    ["side"] = side.Name,
                    ["target"] = targetPath
                });
            }
            else if (hadTarget)
            {
                Log("stable_ui_pointer_direct_ray_target_lost", new Dictionary<string, object>
                {
                    ["side"] = side.Name,
                    ["target"] = "none"
                });
            }
        }

        private void LogNearestButtonDistance(StablePointerSide side, Vector3 localPoint)
        {
            if (_targetCanvas == null || Time.unscaledTime < side.NextNearestButtonLogAt)
            {
                return;
            }

            side.NextNearestButtonLogAt = Time.unscaledTime + 0.5f;
            float best = float.PositiveInfinity;
            string bestPath = "none";
            foreach (Selectable selectable in _targetCanvas.GetComponentsInChildren<Selectable>(true))
            {
                if (selectable == null || !selectable.gameObject.activeInHierarchy || !selectable.IsInteractable())
                {
                    continue;
                }

                RectTransform rect = selectable.transform as RectTransform;
                if (rect == null)
                {
                    continue;
                }

                RectTransform canvasRect = _targetCanvas.transform as RectTransform;
                if (canvasRect == null)
                {
                    continue;
                }

                Vector3 worldCenter = rect.TransformPoint(rect.rect.center);
                Vector3 canvasLocal = canvasRect.InverseTransformPoint(worldCenter);
                float distance = Vector2.Distance(new Vector2(localPoint.x, localPoint.y), new Vector2(canvasLocal.x, canvasLocal.y));
                if (distance < best)
                {
                    best = distance;
                    bestPath = GetPath(selectable.transform);
                }
            }

            Log("stable_ui_pointer_nearest_button_distance", new Dictionary<string, object>
            {
                ["side"] = side.Name,
                ["nearest_button"] = bestPath,
                ["distance"] = float.IsInfinity(best) ? "inf" : best.ToString("F2"),
                ["local_point"] = FormatVector(localPoint)
            });
        }

        private void LogRigInvariants()
        {
            if (_rigLogged)
            {
                return;
            }

            _rigLogged = true;
            Log("stable_ui_pointer_rig_detected", BuildBasePayload("rig_detected"));
            Log("stable_ui_pointer_xr_input_modality_manager_observed", new Dictionary<string, object>
            {
                ["managers"] = DescribeInputModalityManagers()
            });
            Log("stable_ui_pointer_no_rig_replacement", BuildBasePayload("rig_preserved"));
            Log("stable_ui_pointer_no_hand_disable", BuildBasePayload("hands_preserved"));
            Log("stable_ui_pointer_no_locomotion_changes", BuildBasePayload("locomotion_untouched"));
        }

        private void LogDiagnosticOnlyOnce()
        {
            if (_diagnosticOnlyLogged)
            {
                return;
            }

            _diagnosticOnlyLogged = true;
            Log("stable_ui_pointer_diagnostic_only", BuildBasePayload("diagnostic_only"));
            Log("stable_ui_pointer_no_experiment_flow_change", BuildBasePayload("diagnostic_only"));
        }

        private void LogRaycastThrottled(string eventType, StablePointerSide side, string detail)
        {
            if (!QuestLoggingPolicy.EmitLegacyContinuousDiagnostics ||
                Time.unscaledTime < _nextRaycastLogAt)
            {
                return;
            }

            _nextRaycastLogAt = Time.unscaledTime + RaycastLogIntervalSeconds;
            var payload = BuildSidePayload("raycast", side);
            payload["detail"] = detail;
            Log(eventType, payload);
        }

        private static bool ShouldLogSideState(StablePointerSide side)
        {
            if (!QuestLoggingPolicy.EmitLegacyContinuousDiagnostics ||
                Time.unscaledTime < side.NextStateLogAt)
            {
                return false;
            }

            side.NextStateLogAt = Time.unscaledTime + 0.5f;
            return true;
        }

        private static bool ShouldRecordPointerSample(StablePointerSide side)
        {
            if (!QuestLoggingPolicy.EmitLegacyContinuousDiagnostics ||
                Time.unscaledTime < side.NextPointerSampleAt)
            {
                return false;
            }

            side.NextPointerSampleAt = Time.unscaledTime + 0.5f;
            return true;
        }

        private Dictionary<string, object> BuildBasePayload(string reason)
        {
            return new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["canvas"] = _targetCanvas != null ? GetPath(_targetCanvas.transform) : "none",
                ["canvas_active"] = _targetCanvas != null && _targetCanvas.gameObject.activeInHierarchy,
                ["scene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
            };
        }

        private Dictionary<string, object> BuildSidePayload(string reason, StablePointerSide side)
        {
            var payload = BuildBasePayload(reason);
            payload["side"] = side.Name;
            payload["line"] = side.LineRenderer != null ? GetPath(side.LineRenderer.transform) : "none";
            payload["line_world_space"] = side.LineRenderer != null && side.LineRenderer.useWorldSpace;
            return payload;
        }

        private Dictionary<string, object> BuildSideOwnerPayload(string reason, StablePointerSide side, OwnerCandidate owner)
        {
            var payload = BuildSidePayload(reason, side);
            payload["owner"] = owner.Name;
            payload["root"] = owner.Root != null ? GetPath(owner.Root) : "none";
            payload["position_origin"] = owner.PositionOrigin != null ? GetPath(owner.PositionOrigin) : "none";
            payload["aim"] = owner.AimTransform != null ? GetPath(owner.AimTransform) : "none";
            payload["tracked"] = owner.Tracked;
            payload["is_controller"] = owner.IsController;
            payload["is_right"] = owner.IsRight;
            return payload;
        }

        private Dictionary<string, object> BuildAxisPayload(string reason, StablePointerSide side, OwnerCandidate owner, AxisCandidate axis)
        {
            var payload = BuildSideOwnerPayload(reason, side, owner);
            payload["axis"] = axis.Name;
            payload["crosses_plane"] = axis.CrossesPlane;
            payload["inside_rect"] = axis.InsideRect;
            payload["backwards"] = axis.Backwards;
            payload["lateral"] = axis.Lateral;
            payload["dot_to_canvas"] = axis.DotToCanvas.ToString("F3");
            payload["distance"] = axis.Distance.ToString("F3");
            payload["outside_distance"] = axis.OutsideDistance.ToString("F3");
            payload["normalized_x"] = axis.NormalizedX.ToString("F2");
            payload["normalized_y"] = axis.NormalizedY.ToString("F2");
            payload["score"] = axis.Score.ToString("F2");
            return payload;
        }

        private Dictionary<string, object> BuildDirectionPayload(string reason, StablePointerSide side, OwnerCandidate owner, Vector3 targetDirection, Vector3 currentDirection)
        {
            var payload = BuildSideOwnerPayload(reason, side, owner);
            payload["target_direction"] = FormatVector(targetDirection);
            payload["current_direction"] = FormatVector(currentDirection);
            payload["aim_mode"] = side.LastAimMode;
            payload["smoothing"] = "disabled";
            return payload;
        }

        private Dictionary<string, object> BuildCanvasPointPayload(string reason, StablePointerSide side, Vector3 localPoint, float delta)
        {
            var payload = BuildSidePayload(reason, side);
            payload["local_point"] = FormatVector(localPoint);
            payload["delta"] = delta.ToString("F2");
            payload["last_move_at"] = side.LastCanvasPointMoveAt.ToString("F2");
            payload["aim_mode"] = side.LastAimMode;
            return payload;
        }

        private Dictionary<string, object> BuildRayPayload(string reason, StablePointerSide side, StableProtocolUiRay ray)
        {
            var payload = BuildSidePayload(reason, side);
            payload["owner"] = ray.Owner;
            payload["axis"] = ray.AxisName;
            payload["origin"] = FormatVector(ray.Origin);
            payload["end"] = FormatVector(ray.Endpoint);
            payload["direction"] = FormatVector(ray.Direction);
            payload["canvas_hit"] = ray.CanvasHit;
            payload["rect_hit"] = ray.RectHit;
            payload["screen"] = ray.ScreenPosition;
            return payload;
        }

        private Dictionary<string, object> BuildClickPayload(string reason, StablePointerSide side, OwnerCandidate owner, UiHit hit)
        {
            var payload = BuildSideOwnerPayload(reason, side, owner);
            payload["target"] = hit.Target != null ? GetPath(hit.Target.transform) : "none";
            payload["screen"] = hit.ScreenPosition;
            payload["world"] = FormatVector(hit.WorldPoint);
            payload["canvas_rect_hit"] = hit.CanvasRectHit;
            payload["graphic_hit"] = hit.Valid;
            return payload;
        }

        private Dictionary<string, object> BuildGlobalInstructionsFrontPointerPayload(
            string reason,
            StablePointerSide side,
            StableProtocolUiRay ray,
            UiHit hit,
            bool hasRay)
        {
            var payload = BuildBasePayload(reason);
            payload["canvas_path"] = _targetCanvas != null ? GetPath(_targetCanvas.transform) : "none";
            payload["parent_path"] = _targetCanvas != null ? GetPath(_targetCanvas.transform.parent) : "none";
            payload["side"] = side != null ? side.Name : "none";
            payload["ray_origin"] = hasRay ? FormatVector(ray.Origin) : "none";
            payload["ray_direction"] = hasRay ? FormatVector(ray.Direction) : "none";
            payload["hit_world"] = FormatVector(hit.WorldPoint);
            payload["local_point"] = FormatVector(hit.LocalPoint);
            payload["button_path"] = hit.Target != null ? GetPath(hit.Target.transform) : "none";
            payload["button_interactable"] = IsTargetButtonInteractable(hit.Target);
            payload["button_raycast_target"] = IsTargetButtonRaycastTarget(hit.Target);
            AddEventSystemPayload(payload);
            return payload;
        }

        private Dictionary<string, object> BuildGlobalInstructionsFrontGraphicPayload(
            string reason,
            StablePointerSide side,
            GameObject target,
            Graphic graphic)
        {
            var payload = BuildSidePayload(reason, side);
            payload["canvas_path"] = _targetCanvas != null ? GetPath(_targetCanvas.transform) : "none";
            payload["parent_path"] = _targetCanvas != null ? GetPath(_targetCanvas.transform.parent) : "none";
            payload["button_path"] = target != null ? GetPath(target.transform) : "none";
            payload["graphic_path"] = graphic != null ? GetPath(graphic.transform) : "none";
            payload["button_interactable"] = IsTargetButtonInteractable(target);
            payload["button_raycast_target"] = IsTargetButtonRaycastTarget(target);
            AddEventSystemPayload(payload);
            return payload;
        }

        private void LogGlobalInstructionsButtonTargetsConfigured(string reason)
        {
            var payload = BuildGlobalInstructionsFrontPointerPayload(reason, null, default, default, false);
            if (_targetCanvas == null)
            {
                payload["button_count"] = 0;
                Log("global_instructions_front_canvas_button_targets_configured", payload);
                return;
            }

            Button[] buttons = _targetCanvas.GetComponentsInChildren<Button>(true);
            payload["button_count"] = buttons.Length;
            for (int i = 0; i < buttons.Length && i < 6; i++)
            {
                Button button = buttons[i];
                payload[$"button_{i + 1}"] = button != null ? GetPath(button.transform) : "none";
                payload[$"button_{i + 1}_interactable"] = button != null && button.interactable;
                payload[$"button_{i + 1}_raycast"] = button != null && button.targetGraphic != null && button.targetGraphic.raycastTarget;
            }

            Log("global_instructions_front_canvas_button_targets_configured", payload);
        }

        private static bool IsTargetButtonInteractable(GameObject target)
        {
            if (target == null)
            {
                return false;
            }

            Button button = target.GetComponent<Button>() ?? target.GetComponentInParent<Button>();
            if (button != null)
            {
                return button.interactable && button.IsInteractable();
            }

            Selectable selectable = target.GetComponent<Selectable>() ?? target.GetComponentInParent<Selectable>();
            return selectable != null && selectable.IsInteractable();
        }

        private static bool IsTargetButtonRaycastTarget(GameObject target)
        {
            if (target == null)
            {
                return false;
            }

            Button button = target.GetComponent<Button>() ?? target.GetComponentInParent<Button>();
            if (button != null)
            {
                return button.targetGraphic != null && button.targetGraphic.raycastTarget;
            }

            Graphic graphic = target.GetComponent<Graphic>() ?? target.GetComponentInParent<Graphic>();
            return graphic != null && graphic.raycastTarget;
        }

        private static void AddEventSystemPayload(Dictionary<string, object> payload)
        {
            EventSystem eventSystem = EventSystem.current;
            BaseInputModule inputModule = eventSystem != null ? eventSystem.currentInputModule : null;
            payload["event_system"] = eventSystem != null ? GetPath(eventSystem.transform) : "none";
            payload["input_module"] = inputModule != null ? inputModule.GetType().FullName : "none";
        }

        private Dictionary<string, object> BuildInputPayload(string reason, StablePointerSide side, XRInputDevice device)
        {
            var payload = BuildSidePayload(reason, side);
            payload["device_name"] = device.isValid ? device.name : "invalid";
            payload["device_characteristics"] = device.isValid ? device.characteristics.ToString() : "none";
            payload["is_valid"] = device.isValid;
            payload["is_tracked"] = IsDeviceTracked(device);
            return payload;
        }

        private static Transform FindRoot(string exactName)
        {
            foreach (Transform transform in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (transform == null || !string.Equals(transform.name, exactName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string path = GetPath(transform);
                if (path.IndexOf("Controller Visual", StringComparison.OrdinalIgnoreCase) < 0 &&
                    path.IndexOf("Attach", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return transform;
                }
            }

            return null;
        }

        private static Transform FindNearFarInteractorTransform(Transform root)
        {
            if (root == null)
            {
                return null;
            }

            foreach (NearFarInteractor interactor in root.GetComponentsInChildren<NearFarInteractor>(true))
            {
                if (interactor == null)
                {
                    continue;
                }

                string path = GetPath(interactor.transform);
                if (path.IndexOf("Runtime UI Ray", StringComparison.OrdinalIgnoreCase) < 0 &&
                    path.IndexOf("ManualUiLaser", StringComparison.OrdinalIgnoreCase) < 0 &&
                    path.IndexOf("Teleport", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return interactor.transform;
                }
            }

            return null;
        }

        private static Transform FindChildContaining(Transform root, string partialName)
        {
            if (root == null)
            {
                return null;
            }

            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child != null && child != root && child.name.IndexOf(partialName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return child;
                }
            }

            return null;
        }

        private static bool IsForbiddenOriginPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            return path.IndexOf("Controller Visual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("[Left Controller] Attach", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("[Right Controller] Attach", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("Teleport Stabilized Origin", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsPhysicalControllerTracked(XRNode node)
        {
            XRInputDevice device = InputDevices.GetDeviceAtXRNode(node);
            if (!device.isValid || !IsPhysicalControllerDevice(device))
            {
                return false;
            }

            return IsDeviceTracked(device);
        }

        private static bool IsDeviceTracked(XRInputDevice device)
        {
            return device.isValid && device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked;
        }

        private static bool IsPhysicalControllerDevice(XRInputDevice device)
        {
            InputDeviceCharacteristics characteristics = device.characteristics;
            string name = device.name ?? string.Empty;
            if ((characteristics & InputDeviceCharacteristics.HandTracking) != 0 ||
                name.IndexOf("Hand Interaction", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Palm Pose", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return (characteristics & InputDeviceCharacteristics.Controller) != 0 &&
                (name.IndexOf("controller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 name.IndexOf("touch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (characteristics & InputDeviceCharacteristics.HeldInHand) != 0);
        }

        private string DescribeInputModalityManagers()
        {
            var values = new List<string>();
            foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (behaviour != null && behaviour.GetType().Name.IndexOf("InputModalityManager", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    values.Add($"{behaviour.GetType().Name}:path={GetPath(behaviour.transform)};enabled={behaviour.enabled};active={behaviour.gameObject.activeInHierarchy}");
                }
            }

            return values.Count == 0 ? "none" : string.Join(" || ", values);
        }

        private Vector3 GetCanvasCenter(RectTransform rect)
        {
            if (rect == null)
            {
                return _targetCanvas != null ? _targetCanvas.transform.position : transform.position + transform.forward * 2f;
            }

            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;
        }

        private static string FormatNormalizedPoint(RectTransform rect, Vector3 local)
        {
            if (rect == null || Mathf.Approximately(rect.rect.width, 0f) || Mathf.Approximately(rect.rect.height, 0f))
            {
                return "(0.000,0.000)";
            }

            float x = Mathf.InverseLerp(rect.rect.xMin, rect.rect.xMax, local.x);
            float y = Mathf.InverseLerp(rect.rect.yMin, rect.rect.yMax, local.y);
            return $"({x:F3},{y:F3})";
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F3},{value.y:F3},{value.z:F3})";
        }

        private static void Log(string eventType, Dictionary<string, object> payload)
        {
            payload ??= new Dictionary<string, object>();
            payload["diagnostic_scope"] = DiagnosticScope;
            if (ShouldWriteStructuredEvent(eventType))
            {
                ExperimentRuntimeXriRigTopologyDiagnostics.RecordStablePointerEvent(eventType, payload);
            }

            if (!ShouldWriteLogcatEvent(eventType))
            {
                return;
            }

            TiagoExperimentTelemetry.LogEvent(eventType, payload);
            Debug.Log($"{LogPrefix} {eventType} | {FormatPayload(payload)}");
        }

        private static bool ShouldWriteStructuredEvent(string eventType)
        {
            return eventType.IndexOf("click", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("global_instructions_front_canvas", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("aim_mode", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("recovery", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("input_device", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("released_observed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("canvas_point", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("graphic_target", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("selectable_target", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("nearest_button", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("log_volume", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("snapshot_throttle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("per_frame_logging", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("enabled_for_protocol_ui", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("disabled_no_protocol_ui", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("controller_restored", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("direct_ray", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("axis_override", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("target_magnetism", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("canvas_point_memory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("canvas_assisted", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("last_valid_canvas_point", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("direction_smoothing_disabled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("runtime_axis_switching", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ShouldWriteLogcatEvent(string eventType)
        {
            return eventType.IndexOf("click", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("global_instructions_front_canvas", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("aim_mode_selected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("recovery_started", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("recovery_ended", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("recovery_failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("input_device_reacquired", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("released_observed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("graphic_target_found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("selectable_target_found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("canvas_point_stuck", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("log_volume", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("snapshot_throttle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("per_frame_logging", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("enabled_for_protocol_ui", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("disabled_no_protocol_ui", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("direct_ray_mode_enabled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("direct_ray_axis_selected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("direct_ray_axis_locked", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("direct_ray_target_acquired", StringComparison.OrdinalIgnoreCase) >= 0 ||
                eventType.IndexOf("direct_ray_target_lost", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FormatPayload(Dictionary<string, object> payload)
        {
            var values = new List<string>();
            int count = 0;
            foreach (KeyValuePair<string, object> pair in payload)
            {
                if (count++ >= 14)
                {
                    values.Add("...");
                    break;
                }

                values.Add($"{pair.Key}={pair.Value}");
            }

            return string.Join(" | ", values);
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

        private sealed class StablePointerSide
        {
            public StablePointerSide(bool isRight)
            {
                IsRight = isRight;
                Name = isRight ? "right" : "left";
                ControllerOwnerName = isRight ? "RightController" : "LeftController";
                HandOwnerName = isRight ? "RightHand" : "LeftHand";
            }

            public bool IsRight { get; }
            public string Name { get; }
            public string ControllerOwnerName { get; }
            public string HandOwnerName { get; }
            public LineRenderer LineRenderer;
            public OwnerCandidate LastValidOwner;
            public float LastValidOwnerAt = -100f;
            public GameObject HoverTarget;
            public GameObject PressedTarget;
            public bool SelectWasPressed;
            public bool OwnerAvailable;
            public string LastOwnerName = string.Empty;
            public string LastAxisName = string.Empty;
            public string LastAimMode = string.Empty;
            public StablePointerAxis DirectRayAxis = StablePointerAxis.Forward;
            public bool DirectRayAxisLocked;
            public float LastAimModeChangeTime = -100f;
            public float AimModeInvalidSince = -1f;
            public Vector3 CurrentDirection;
            public Vector3 LastValidDirection;
            public Vector3 LastValidCanvasPoint;
            public bool HasLastValidCanvasPoint;
            public bool RecoveryWasActive;
            public bool PointerHiddenUntilRealAxis;
            public XRInputDevice InputDevice;
            public bool HasInputDevice;
            public bool WaitingForReleasedAfterModalityChange;
            public bool ClickEnabledAfterModalityChange;
            public float NextStateLogAt;
            public float NextPointerSampleAt;
            public bool RecordPointerSampleThisFrame;
            public Vector3 LastCanvasLocalPoint;
            public bool HasLastCanvasLocalPoint;
            public float LastCanvasPointMoveAt;
            public float NextNearestButtonLogAt;
            public string LastRecordedTargetPath = string.Empty;
            public string LastDirectRayTargetPath = string.Empty;
            public Transform LastAimTransform;
        }

        private struct OwnerCandidate
        {
            public bool Valid;
            public bool IsController;
            public bool IsRight;
            public bool Tracked;
            public string Name;
            public Transform Root;
            public Transform PositionOrigin;
            public Transform AimTransform;
        }

        private struct AxisCandidate
        {
            public string Name;
            public Vector3 Direction;
            public bool CrossesPlane;
            public bool InsideRect;
            public bool Backwards;
            public bool Lateral;
            public float DotToCanvas;
            public float Distance;
            public float OutsideDistance;
            public float Score;
            public float NormalizedX;
            public float NormalizedY;
            public Vector3 WorldPoint;
        }

        private struct StableProtocolUiRay
        {
            public string Side;
            public string Owner;
            public Vector3 Origin;
            public Vector3 Direction;
            public Vector3 Endpoint;
            public string AxisName;
            public Canvas Canvas;
            public bool CanvasHit;
            public bool RectHit;
            public Vector3 WorldPoint;
            public float Distance;
            public Vector2 ScreenPosition;
            public GameObject SelectedTarget;
            public bool VisualOnlyRecovery;
            public bool Interactive;
        }

        private struct UiHit
        {
            public bool Valid;
            public bool CanvasRectHit;
            public GameObject Target;
            public Vector3 WorldPoint;
            public Vector3 LocalPoint;
            public Vector2 ScreenPosition;
            public bool Interactive;
        }

        private struct NativeLineState
        {
            public bool Enabled;
            public bool ForceRenderingOff;
        }

        private enum StablePointerAimMode
        {
            DirectControllerRay
        }

        private enum StablePointerAxis
        {
            Forward,
            NegativeForward,
            Up,
            NegativeUp
        }
    }
}
