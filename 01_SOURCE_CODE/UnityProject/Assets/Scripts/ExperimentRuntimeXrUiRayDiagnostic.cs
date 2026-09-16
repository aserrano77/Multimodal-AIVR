using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Object = UnityEngine.Object;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class ExperimentRuntimeXrUiRayDiagnostic : MonoBehaviour
    {
        private const string LogPrefix = "[ExperimentRuntimeXrUiRayDiagnostic]";
        private const int MaxText = 900;
        private static readonly BindingFlags MemberFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [SerializeField] private Canvas _targetCanvas;
        [SerializeField] private bool _runOnStart = true;
        [SerializeField] private float _sampleDurationSeconds = 15f;
        [SerializeField] private float _sampleIntervalSeconds = 1f;
        [SerializeField] private float _diagnosticRayLength = 8f;
        [SerializeField] private bool _drawDiagnosticRaysInEditor = true;

        private readonly List<Component> _interactors = new();
        private readonly List<Component> _uiInteractors = new();
        private readonly List<string> _inputActionProblems = new();

        private bool _eventSystemFound;
        private bool _uiInputModuleOk;
        private bool _trackedDeviceRaycasterOk;
        private bool _inputActionsOk = true;
        private bool _controllerPoseSeen;
        private bool _uiHitsDetected;
        private bool _physicsHitsDetected;
        private bool _lineVisualsEnabled;
        private bool _sampleLoopCompleted;
        private bool _xriUiSelectDetected;

        public static ExperimentRuntimeXrUiRayDiagnostic EnsureAttached(GameObject protocolUiRoot, Canvas canvas)
        {
            if (protocolUiRoot == null)
            {
                return null;
            }

            Transform diagnosticRoot = protocolUiRoot.transform.Find("XRRayDiagnostic");
            if (diagnosticRoot == null)
            {
                var diagnosticObject = new GameObject("XRRayDiagnostic");
                diagnosticObject.transform.SetParent(protocolUiRoot.transform, false);
                diagnosticRoot = diagnosticObject.transform;
            }

            ExperimentRuntimeXrUiRayDiagnostic diagnostic =
                diagnosticRoot.GetComponent<ExperimentRuntimeXrUiRayDiagnostic>();
            if (diagnostic == null)
            {
                diagnostic = diagnosticRoot.gameObject.AddComponent<ExperimentRuntimeXrUiRayDiagnostic>();
            }

            diagnostic.Initialize(canvas);
            return diagnostic;
        }

        public void Initialize(Canvas canvas)
        {
            _targetCanvas = canvas;
        }

        private void Start()
        {
            if (!_runOnStart)
            {
                return;
            }

            StartCoroutine(RunDiagnostic());
        }

        private IEnumerator RunDiagnostic()
        {
            yield return null;
            ResolveCanvas();
            LogEventSystemDiagnostic();
            LogCanvasDiagnostic();
            DiscoverInteractors();
            LogInteractorDiagnostics();
            LogInputActionDiagnostics();

            float startedAt = Time.unscaledTime;
            int sampleIndex = 0;
            while (Time.unscaledTime - startedAt <= _sampleDurationSeconds)
            {
                LogSample(sampleIndex, Time.unscaledTime - startedAt);
                sampleIndex++;
                yield return new WaitForSecondsRealtime(Mathf.Max(0.2f, _sampleIntervalSeconds));
            }

            _sampleLoopCompleted = true;
            LogSummary();
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

            ExperimentRuntimeProtocolUI runtimeUi = FindFirstObjectByType<ExperimentRuntimeProtocolUI>();
            if (runtimeUi != null)
            {
                _targetCanvas = runtimeUi.GetComponent<Canvas>();
            }
        }

        private void LogEventSystemDiagnostic()
        {
            EventSystem eventSystem = EventSystem.current ?? FindFirstObjectByType<EventSystem>();
            _eventSystemFound = eventSystem != null;

            var modules = new List<string>();
            int enabledModules = 0;
            bool standaloneActive = false;
            bool xrModuleFound = false;
            string xrModuleDetails = string.Empty;

            if (eventSystem != null)
            {
                foreach (BaseInputModule module in eventSystem.GetComponents<BaseInputModule>())
                {
                    if (module == null)
                    {
                        continue;
                    }

                    bool enabled = module.enabled;
                    bool active = module.isActiveAndEnabled;
                    if (enabled && active)
                    {
                        enabledModules++;
                    }

                    string typeName = module.GetType().FullName;
                    modules.Add($"{typeName}:enabled={enabled}:active={active}");
                    if (module is StandaloneInputModule && enabled && active)
                    {
                        standaloneActive = true;
                    }

                    if (typeName.IndexOf("XRUIInputModule", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        xrModuleFound = true;
                        xrModuleDetails = DescribeInterestingMembers(module, "Input", "Action", "XR", "Mouse", "Touch");
                    }
                }
            }

            _uiInputModuleOk = _eventSystemFound && xrModuleFound && !standaloneActive;

            LogStructured("xr_ui_ray_diag_eventsystem", new Dictionary<string, object>
            {
                ["event_system_found"] = _eventSystemFound,
                ["event_system_path"] = eventSystem != null ? GetPath(eventSystem.transform) : string.Empty,
                ["current_input_module"] = eventSystem != null && eventSystem.currentInputModule != null
                    ? eventSystem.currentInputModule.GetType().FullName
                    : string.Empty,
                ["modules"] = JoinLimited(modules),
                ["enabled_module_count"] = enabledModules,
                ["multiple_active_modules"] = enabledModules > 1,
                ["standalone_input_module_active"] = standaloneActive,
                ["xr_ui_input_module_found"] = xrModuleFound,
                ["xr_ui_input_module_configuration"] = xrModuleDetails
            });
        }

        private void LogCanvasDiagnostic()
        {
            ResolveCanvas();
            RectTransform rect = _targetCanvas != null ? _targetCanvas.GetComponent<RectTransform>() : null;
            var raycastTargets = new List<string>();
            int graphicCount = 0;
            int raycastTargetCount = 0;

            if (_targetCanvas != null)
            {
                foreach (Graphic graphic in _targetCanvas.GetComponentsInChildren<Graphic>(true))
                {
                    if (graphic == null)
                    {
                        continue;
                    }

                    graphicCount++;
                    if (graphic.raycastTarget)
                    {
                        raycastTargetCount++;
                        if (raycastTargets.Count < 12)
                        {
                            raycastTargets.Add(GetPath(graphic.transform));
                        }
                    }
                }
            }

            TrackedDeviceGraphicRaycaster trackedRaycaster =
                _targetCanvas != null ? _targetCanvas.GetComponent<TrackedDeviceGraphicRaycaster>() : null;
            GraphicRaycaster graphicRaycaster =
                _targetCanvas != null ? _targetCanvas.GetComponent<GraphicRaycaster>() : null;
            _trackedDeviceRaycasterOk = trackedRaycaster != null && trackedRaycaster.enabled;

            Transform xrOrigin = FindNamedTransform("XR Origin") ?? FindNamedTransform("XR Rig");
            Camera mainCamera = Camera.main;
            float cameraDistance = float.NaN;
            if (_targetCanvas != null && mainCamera != null)
            {
                cameraDistance = Vector3.Distance(_targetCanvas.transform.position, mainCamera.transform.position);
            }

            float originDistance = float.NaN;
            if (_targetCanvas != null && xrOrigin != null)
            {
                originDistance = Vector3.Distance(_targetCanvas.transform.position, xrOrigin.position);
            }

            LogStructured("xr_ui_ray_diag_canvas", new Dictionary<string, object>
            {
                ["canvas_found"] = _targetCanvas != null,
                ["canvas_path"] = _targetCanvas != null ? GetPath(_targetCanvas.transform) : string.Empty,
                ["render_mode"] = _targetCanvas != null ? _targetCanvas.renderMode.ToString() : string.Empty,
                ["world_camera"] = _targetCanvas != null && _targetCanvas.worldCamera != null
                    ? GetPath(_targetCanvas.worldCamera.transform)
                    : string.Empty,
                ["scale_factor"] = _targetCanvas != null ? _targetCanvas.scaleFactor : float.NaN,
                ["sorting_order"] = _targetCanvas != null ? _targetCanvas.sortingOrder : 0,
                ["layer"] = _targetCanvas != null ? LayerMask.LayerToName(_targetCanvas.gameObject.layer) : string.Empty,
                ["layer_index"] = _targetCanvas != null ? _targetCanvas.gameObject.layer : -1,
                ["has_tracked_device_graphic_raycaster"] = trackedRaycaster != null,
                ["tracked_device_graphic_raycaster_enabled"] = trackedRaycaster != null && trackedRaycaster.enabled,
                ["has_graphic_raycaster"] = graphicRaycaster != null,
                ["graphic_raycaster_enabled"] = graphicRaycaster != null && graphicRaycaster.enabled,
                ["graphic_count"] = graphicCount,
                ["raycast_target_graphic_count"] = raycastTargetCount,
                ["sample_raycast_targets"] = JoinLimited(raycastTargets),
                ["button_count"] = _targetCanvas != null ? _targetCanvas.GetComponentsInChildren<Button>(true).Length : 0,
                ["rect_size"] = rect != null ? rect.rect.size : Vector2.zero,
                ["rect_size_delta"] = rect != null ? rect.sizeDelta : Vector2.zero,
                ["world_position"] = _targetCanvas != null ? _targetCanvas.transform.position : Vector3.zero,
                ["world_rotation_euler"] = _targetCanvas != null ? _targetCanvas.transform.eulerAngles : Vector3.zero,
                ["lossy_scale"] = _targetCanvas != null ? _targetCanvas.transform.lossyScale : Vector3.zero,
                ["distance_to_main_camera"] = cameraDistance,
                ["distance_to_xr_origin_or_rig"] = originDistance
            });
        }

        private void DiscoverInteractors()
        {
            _interactors.Clear();
            _uiInteractors.Clear();
            Component[] components = FindObjectsByType<Component>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (Component component in components)
            {
                if (component == null)
                {
                    continue;
                }

                Type type = component.GetType();
                string typeName = type.Name;
                string fullName = type.FullName ?? typeName;
                string path = component.transform != null ? GetPath(component.transform) : string.Empty;
                bool relevant =
                    fullName.IndexOf("NearFarInteractor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fullName.IndexOf("XRRayInteractor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("Runtime UI Ray Interactor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("Runtime UI Ray Visual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fullName.IndexOf("ExperimentRuntimeManualUiLaserPointer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ImplementsInterfaceNamed(type, "IUIInteractor") ||
                    ImplementsInterfaceNamed(type, "IXRRayProvider") ||
                    ImplementsInterfaceNamed(type, "IXRInteractor");

                if (!relevant)
                {
                    continue;
                }

                if (component.transform != null &&
                    path.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) < 0 &&
                    path.IndexOf("Runtime UI Ray", StringComparison.OrdinalIgnoreCase) < 0 &&
                    fullName.IndexOf("ExperimentRuntimeManualUiLaserPointer", StringComparison.OrdinalIgnoreCase) < 0 &&
                    fullName.IndexOf("NearFarInteractor", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                _interactors.Add(component);
                if (ImplementsInterfaceNamed(type, "IUIInteractor"))
                {
                    _uiInteractors.Add(component);
                }
            }
        }

        private void LogInteractorDiagnostics()
        {
            int activeInteractorCount = 0;
            int lineVisualCount = 0;
            int activeLineVisualCount = 0;

            foreach (Component interactor in _interactors)
            {
                if (interactor == null)
                {
                    continue;
                }

                if (interactor is Behaviour behaviour && behaviour.isActiveAndEnabled)
                {
                    activeInteractorCount++;
                }

                Component lineVisual = FindChildComponentByName(interactor.transform, "LineVisual");
                LineRenderer lineRenderer = interactor.GetComponentInChildren<LineRenderer>(true);
                bool lineVisualEnabled = lineVisual is Behaviour lineBehaviour && lineBehaviour.enabled && lineBehaviour.gameObject.activeInHierarchy;
                if (lineVisual != null)
                {
                    lineVisualCount++;
                }

                if (lineVisualEnabled)
                {
                    activeLineVisualCount++;
                }

                LogStructured("xr_ui_ray_diag_interactor", new Dictionary<string, object>
                {
                    ["path"] = GetPath(interactor.transform),
                    ["type"] = interactor.GetType().FullName,
                    ["game_object_active_in_hierarchy"] = interactor.gameObject.activeInHierarchy,
                    ["component_enabled"] = interactor is Behaviour interactorBehaviour && interactorBehaviour.enabled,
                    ["component_active_and_enabled"] = interactor is Behaviour activeBehaviour && activeBehaviour.isActiveAndEnabled,
                    ["interfaces"] = DescribeInterfaces(interactor.GetType()),
                    ["enable_ui_interaction"] = ReadMemberString(interactor, "enableUIInteraction", "EnableUIInteraction", "m_EnableUIInteraction"),
                    ["enable_far_casting"] = ReadMemberString(interactor, "enableFarCasting", "EnableFarCasting", "m_EnableFarCasting"),
                    ["block_ui_on_interactable_selection"] = ReadMemberString(interactor, "blockUIOnInteractableSelection", "m_BlockUIOnInteractableSelection"),
                    ["has_selection"] = ReadMemberString(interactor, "hasSelection", "HasSelection"),
                    ["is_select_active"] = ReadMemberString(interactor, "isSelectActive", "IsSelectActive"),
                    ["is_hover_active"] = ReadMemberString(interactor, "isHoverActive", "IsHoverActive"),
                    ["interaction_manager"] = ReadMemberString(interactor, "interactionManager", "m_InteractionManager"),
                    ["interaction_layers"] = ReadMemberString(interactor, "interactionLayers", "m_InteractionLayers"),
                    ["hover_filters"] = DescribeInterestingMembers(interactor, "HoverFilter", "hoverFilter", "m_HoverFilter"),
                    ["select_filters"] = DescribeInterestingMembers(interactor, "SelectFilter", "selectFilter", "m_SelectFilter"),
                    ["line_visual_found"] = lineVisual != null,
                    ["line_visual_path"] = lineVisual != null ? GetPath(lineVisual.transform) : string.Empty,
                    ["line_visual_type"] = lineVisual != null ? lineVisual.GetType().FullName : string.Empty,
                    ["line_visual_enabled"] = lineVisualEnabled,
                    ["line_visual_members"] = lineVisual != null ? DescribeInterestingMembers(lineVisual, "Valid", "Invalid", "Reticle", "Line", "Visibility") : string.Empty,
                    ["line_renderer_found"] = lineRenderer != null,
                    ["line_renderer_path"] = lineRenderer != null ? GetPath(lineRenderer.transform) : string.Empty,
                    ["line_renderer_enabled"] = lineRenderer != null && lineRenderer.enabled,
                    ["line_renderer_position_count"] = lineRenderer != null ? lineRenderer.positionCount : 0,
                    ["line_renderer_width_multiplier"] = lineRenderer != null ? lineRenderer.widthMultiplier : float.NaN,
                    ["line_renderer_material"] = lineRenderer != null && lineRenderer.sharedMaterial != null
                        ? lineRenderer.sharedMaterial.name
                        : string.Empty,
                    ["action_references"] = DescribeActionReferences(interactor, GetPath(interactor.transform))
                });
            }

            _lineVisualsEnabled = activeLineVisualCount > 0;

            LogStructured("xr_ui_ray_diag_interactor", new Dictionary<string, object>
            {
                ["summary"] = true,
                ["interactor_count"] = _interactors.Count,
                ["active_interactor_count"] = activeInteractorCount,
                ["ui_interactor_count"] = _uiInteractors.Count,
                ["line_visual_count"] = lineVisualCount,
                ["active_line_visual_count"] = activeLineVisualCount
            });
        }

        private void LogInputActionDiagnostics()
        {
            _inputActionProblems.Clear();
            var details = new List<string>();

            EventSystem eventSystem = EventSystem.current ?? FindFirstObjectByType<EventSystem>();
            if (eventSystem != null)
            {
                foreach (BaseInputModule module in eventSystem.GetComponents<BaseInputModule>())
                {
                    CollectActionDiagnostics(module, GetPath(module.transform), details);
                }
            }

            foreach (Component interactor in _interactors)
            {
                CollectActionDiagnostics(interactor, GetPath(interactor.transform), details);
                foreach (Component childComponent in interactor.GetComponentsInChildren<Component>(true))
                {
                    if (childComponent == null || childComponent == interactor)
                    {
                        continue;
                    }

                    string typeName = childComponent.GetType().Name;
                    if (typeName.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        typeName.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        typeName.IndexOf("Interactor", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        CollectActionDiagnostics(childComponent, GetPath(childComponent.transform), details);
                    }
                }
            }

            _inputActionsOk = _inputActionProblems.Count == 0 && details.Count > 0;

            LogStructured("xr_ui_ray_diag_input_actions", new Dictionary<string, object>
            {
                ["input_actions_found"] = details.Count,
                ["input_actions_ok"] = _inputActionsOk,
                ["disabled_or_unresolved_actions"] = JoinLimited(_inputActionProblems),
                ["details"] = JoinLimited(details, 2600)
            });
        }

        private void LogSample(int sampleIndex, float elapsedSeconds)
        {
            Camera activeCamera = Camera.main;
            foreach (Component interactor in _interactors)
            {
                if (interactor == null || interactor.transform == null)
                {
                    continue;
                }

                bool runtimeUiRay = IsRuntimeUiRay(interactor);
                if (!runtimeUiRay && interactor is Behaviour inactiveBehaviour && !inactiveBehaviour.isActiveAndEnabled)
                {
                    continue;
                }

                Transform rayOrigin = ResolveRayOrigin(interactor.transform);
                Vector3 origin = rayOrigin.position;
                Vector3 forward = rayOrigin.forward;
                bool poseLooksValid = interactor.gameObject.activeInHierarchy &&
                    origin.sqrMagnitude > 0.0001f &&
                    !float.IsNaN(origin.x) &&
                    !float.IsNaN(forward.x);
                _controllerPoseSeen |= poseLooksValid;

                bool uiGeometryHit = TryHitCanvasGeometry(origin, forward, out string uiHitName, out float uiHitDistance);
                _uiHitsDetected |= uiGeometryHit;

                bool physicsHit = Physics.Raycast(
                    origin,
                    forward,
                    out RaycastHit hit,
                    _diagnosticRayLength,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Collide);
                _physicsHitsDetected |= physicsHit;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (_drawDiagnosticRaysInEditor)
                {
                    Debug.DrawRay(origin, forward * _diagnosticRayLength, uiGeometryHit ? Color.green : Color.yellow, _sampleIntervalSeconds);
                }
#endif

                string uiModel = DescribeUiModel(interactor);
                if (uiModel.IndexOf("select=True", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    uiModel.IndexOf("select=true", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _xriUiSelectDetected = true;
                }

                LogStructured("xr_ui_ray_diag_sample", new Dictionary<string, object>
                {
                    ["sample_index"] = sampleIndex,
                    ["elapsed_seconds"] = elapsedSeconds,
                    ["active_camera"] = activeCamera != null ? GetPath(activeCamera.transform) : string.Empty,
                    ["interactor_path"] = GetPath(interactor.transform),
                    ["ray_origin_path"] = GetPath(rayOrigin),
                    ["position"] = origin,
                    ["rotation_euler"] = rayOrigin.eulerAngles,
                    ["tracking_member_state"] = DescribeTrackingState(interactor),
                    ["pose_looks_valid"] = poseLooksValid,
                    ["enable_ui_interaction"] = ReadMemberString(interactor, "enableUIInteraction", "EnableUIInteraction", "m_EnableUIInteraction"),
                    ["has_selection"] = ReadMemberString(interactor, "hasSelection", "HasSelection"),
                    ["is_select_active"] = ReadMemberString(interactor, "isSelectActive", "IsSelectActive"),
                    ["is_hover_active"] = ReadMemberString(interactor, "isHoverActive", "IsHoverActive"),
                    ["ui_press_active_or_value"] = DescribeInputReaderValue(interactor, "uiPressInput", "m_UIPressInput"),
                    ["select_active_or_value"] = DescribeInputReaderValue(interactor, "selectInput", "m_SelectInput"),
                    ["ui_model"] = uiModel,
                    ["ui_geometry_hit"] = uiGeometryHit,
                    ["ui_geometry_hit_target"] = uiHitName,
                    ["ui_geometry_hit_distance"] = uiHitDistance,
                    ["physics_hit"] = physicsHit,
                    ["physics_hit_target"] = physicsHit ? GetPath(hit.transform) : string.Empty,
                    ["physics_hit_layer"] = physicsHit ? LayerMask.LayerToName(hit.collider.gameObject.layer) : string.Empty,
                    ["physics_hit_distance"] = physicsHit ? hit.distance : float.NaN,
                    ["physics_hit_is_runtime_protocol_ui"] = physicsHit && IsUnderTargetCanvas(hit.transform)
                });
            }
        }

        private void LogSummary()
        {
            string category;
            string reason;
            string fix;

            if (!_eventSystemFound)
            {
                category = "G_eventsystem_missing";
                reason = "No EventSystem was found in Play Mode.";
                fix = "Ensure a single EventSystem with XRUIInputModule is active before the runtime panel is used.";
            }
            else if (!_uiInputModuleOk)
            {
                category = "G_xr_ui_input_module_not_ok";
                reason = "EventSystem exists but XRUIInputModule is missing/inactive or StandaloneInputModule is active.";
                fix = "Leave only XRUIInputModule active for tracked-device UI input.";
            }
            else if (!_trackedDeviceRaycasterOk)
            {
                category = "F_canvas_raycaster_not_ok";
                reason = "The runtime canvas does not expose an enabled TrackedDeviceGraphicRaycaster.";
                fix = "Attach and enable TrackedDeviceGraphicRaycaster on the World Space canvas.";
            }
            else if (_interactors.Count == 0)
            {
                category = "B_no_relevant_interactors";
                reason = "No Near-Far, XR ray, UI, or XR interactor components were found.";
                fix = "Audit the XR Origin controller hierarchy and restore controller interactors.";
            }
            else if (_uiInteractors.Count == 0)
            {
                category = "C_no_ui_interactors_registered";
                reason = "Interactors exist, but none implement IUIInteractor.";
                fix = "Configure the existing Near-Far Interactors for UI or add explicit UI-only ray interactors in a later patch.";
            }
            else if (CountRuntimeUiRayInteractors(out int activeRuntimeUiRays) > 0 && activeRuntimeUiRays == 0)
            {
                category = "B_runtime_ui_ray_created_but_inactive";
                reason = "Runtime UI-only ray interactors were found, but none were active.";
                fix = "Check the chosen Left/Right Controller or Hand parent transforms and whether they are active in Play Mode.";
            }
            else if (CountRuntimeUiRayInteractors(out _) == 0 && CountManualUiLasers(out _) == 0)
            {
                category = "B_runtime_ui_ray_bootstrap_not_visible";
                reason = "No Runtime UI Ray Interactor or ExperimentRuntimeManualUiLaserPointer objects were found.";
                fix = "Filter console by [P45C-05][XR-UI] and verify experiment_runtime_ui_ray_bootstrap_started/result; if absent, inspect the ExperimentRuntimeProtocolUI bootstrap hook.";
            }
            else if (!_inputActionsOk)
            {
                category = "H_input_actions_not_ready";
                reason = "Input actions were missing, unresolved, or disabled.";
                fix = "Activate the XRI UI action map or assign UI Press/Point/Scroll bindings on the real XRUIInputModule/interactors.";
            }
            else if (!_controllerPoseSeen)
            {
                category = "A_controller_pose_not_seen";
                reason = "Controller/interactor transforms did not report a plausible tracked pose during sampling.";
                fix = "Check XR loader/device simulator/controller tracking before changing UI interaction.";
            }
            else if (!_lineVisualsEnabled)
            {
                category = "D_line_visual_not_enabled";
                reason = "No active LineVisual was observed on the controller interactors.";
                fix = "Enable/configure the existing LineVisual components or their line visibility rules.";
            }
            else if (!_uiHitsDetected)
            {
                category = "E_ray_not_hitting_canvas";
                reason = "Diagnostic rays had pose, but did not intersect the RuntimeProtocolUI canvas plane.";
                fix = "Check ray origin orientation, panel rotation, Near/Far mode, and whether the visual only appears on valid UI hover.";
            }
            else
            {
                category = "J_or_C_logical_ui_ray_without_visible_or_processed_hover";
                reason = "Diagnostic rays can intersect the canvas, but the XRI visual/hover path still needs confirmation from UI model/input logs.";
                fix = "Inspect xr_ui_ray_diag_sample ui_model, UI press values, and XRUIInputModule registration before adding UI-only ray interactors.";
            }

            LogStructured("xr_ui_ray_diag_summary", new Dictionary<string, object>
            {
                ["probable_failure_category"] = category,
                ["probable_failure_reason"] = reason,
                ["recommended_next_fix"] = fix,
                ["can_see_controller_pose"] = _controllerPoseSeen,
                ["near_far_interactors_found"] = CountInteractorsNamed("NearFarInteractor"),
                ["runtime_ui_ray_interactors_found"] = CountRuntimeUiRayInteractors(out int activeUiOnlyInteractors),
                ["runtime_ui_ray_interactors_active"] = activeUiOnlyInteractors,
                ["manual_ui_laser_found"] = CountManualUiLasers(out int activeManualUiLasers),
                ["manual_ui_laser_active"] = activeManualUiLasers,
                ["manual_line_visible"] = ExperimentRuntimeManualUiLaserPointer.ManualLineVisible,
                ["manual_ui_hit_detected"] = ExperimentRuntimeManualUiLaserPointer.ManualUiHitCount > 0,
                ["manual_ui_click_detected"] = ExperimentRuntimeManualUiLaserPointer.ManualUiClickCount > 0,
                ["xri_ui_click_detected"] = _xriUiSelectDetected,
                ["ui_interactors_registered_or_found"] = _uiInteractors.Count,
                ["line_visuals_enabled"] = _lineVisualsEnabled,
                ["ui_input_module_ok"] = _uiInputModuleOk,
                ["tracked_device_raycaster_ok"] = _trackedDeviceRaycasterOk,
                ["input_actions_ok"] = _inputActionsOk,
                ["ui_hits_detected"] = _uiHitsDetected,
                ["physics_hits_detected"] = _physicsHitsDetected,
                ["sample_loop_completed"] = _sampleLoopCompleted,
                ["remote_box_interaction_risk"] = "diagnostic_only_no_interactors_or_masks_added_no_remote_box_manipulation_enabled",
                ["explicit_ui_ray_interactors_added"] = false,
                ["object_interaction_layer_masks_changed"] = false,
                ["physics_layers_changed"] = false
            });
        }

        private void CollectActionDiagnostics(Component owner, string ownerPath, List<string> details)
        {
            if (owner == null)
            {
                return;
            }

            foreach (MemberInfo member in GetReadableMembers(owner.GetType()))
            {
                if (!CouldContainAction(member.Name))
                {
                    continue;
                }

                object value = TryGetMemberValue(owner, member);
                if (value == null)
                {
                    continue;
                }

                DescribeActionLikeValue(value, $"{ownerPath}/{owner.GetType().Name}.{member.Name}", details, 0);
            }
        }

        private string DescribeActionReferences(Component owner, string ownerPath)
        {
            var details = new List<string>();
            CollectActionDiagnostics(owner, ownerPath, details);
            return JoinLimited(details, 1600);
        }

        private void DescribeActionLikeValue(object value, string path, List<string> details, int depth)
        {
            if (value == null || depth > 2 || details.Count >= 40)
            {
                return;
            }

            Type type = value.GetType();
            string typeName = type.FullName ?? type.Name;
            if (typeName.IndexOf("InputActionReference", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                DescribeInputActionReference(value, path, details);
                return;
            }

            if (typeName.IndexOf("InputAction", StringComparison.OrdinalIgnoreCase) >= 0 &&
                typeName.IndexOf("Reference", StringComparison.OrdinalIgnoreCase) < 0)
            {
                DescribeInputAction(value, path, details);
                return;
            }

            if (typeName.IndexOf("InputReader", StringComparison.OrdinalIgnoreCase) < 0 &&
                typeName.IndexOf("Action", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            foreach (MemberInfo member in GetReadableMembers(type))
            {
                if (!CouldContainAction(member.Name))
                {
                    continue;
                }

                object nested = TryGetMemberValue(value, member);
                if (nested == null || ReferenceEquals(nested, value))
                {
                    continue;
                }

                DescribeActionLikeValue(nested, $"{path}.{member.Name}", details, depth + 1);
            }
        }

        private void DescribeInputActionReference(object reference, string path, List<string> details)
        {
            object action = TryGetNamedValue(reference, "action", "Action", "m_Action");
            if (action == null)
            {
                details.Add($"{path}:reference_unresolved:{reference}");
                _inputActionProblems.Add($"{path}:reference_unresolved");
                return;
            }

            DescribeInputAction(action, path, details);
        }

        private void DescribeInputAction(object action, string path, List<string> details)
        {
            string name = ReadMemberString(action, "name", "Name");
            string actionMap = DescribeObjectName(TryGetNamedValue(action, "actionMap", "ActionMap", "m_ActionMap"));
            bool enabled = ReadBool(action, false, "enabled", "Enabled");
            int bindingCount = CountEnumerable(TryGetNamedValue(action, "bindings", "Bindings"));
            int controlsCount = CountEnumerable(TryGetNamedValue(action, "controls", "Controls"));
            string bindings = DescribeBindings(TryGetNamedValue(action, "bindings", "Bindings"));
            string value = TryInvokeActionReadValue(action);

            details.Add($"{path}:map={actionMap}:action={name}:enabled={enabled}:bindings={bindingCount}:controls={controlsCount}:value={value}:paths={bindings}");
            if (!enabled || bindingCount == 0)
            {
                _inputActionProblems.Add($"{path}:enabled={enabled}:bindings={bindingCount}");
            }
        }

        private string DescribeInputReaderValue(Component owner, params string[] names)
        {
            object reader = TryGetNamedValue(owner, names);
            if (reader == null)
            {
                return string.Empty;
            }

            string performed = TryInvokeMember(reader, "ReadIsPerformed");
            string value = TryInvokeMember(reader, "ReadValue");
            string objectValue = TryInvokeMember(reader, "ReadValueAsObject");
            return Truncate($"reader={reader.GetType().Name};performed={performed};value={value};object={objectValue}");
        }

        private string DescribeTrackingState(Component interactor)
        {
            var values = new List<string>();
            CollectInterestingState(interactor, values, "isTracked", "trackingState", "m_IsTracked", "m_TrackingState");
            foreach (Component child in interactor.GetComponentsInChildren<Component>(true))
            {
                if (child == null)
                {
                    continue;
                }

                string typeName = child.GetType().Name;
                if (typeName.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) < 0 &&
                    typeName.IndexOf("Tracked", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                CollectInterestingState(child, values, "isTracked", "trackingState", "m_IsTracked", "m_TrackingState");
            }

            return JoinLimited(values);
        }

        private string DescribeUiModel(Component interactor)
        {
            MethodInfo method = FindMethod(interactor.GetType(), "TryGetUIModel");
            if (method == null)
            {
                return "TryGetUIModel_not_found";
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 1 || !parameters[0].ParameterType.IsByRef)
            {
                return "TryGetUIModel_signature_unexpected";
            }

            try
            {
                object[] args = { null };
                object result = method.Invoke(interactor, args);
                bool success = result is bool value && value;
                object model = args[0];
                if (!success || model == null)
                {
                    return $"TryGetUIModel_success={success}:model_null={model == null}";
                }

                return Truncate("TryGetUIModel_success=true:" + DescribeInterestingMembers(model, "hover", "select", "raycast", "hit", "valid", "screen", "world"));
            }
            catch (Exception exception)
            {
                return $"TryGetUIModel_exception={exception.GetType().Name}";
            }
        }

        private bool TryHitCanvasGeometry(Vector3 origin, Vector3 forward, out string targetName, out float distance)
        {
            targetName = string.Empty;
            distance = float.NaN;
            if (_targetCanvas == null)
            {
                return false;
            }

            RectTransform rect = _targetCanvas.GetComponent<RectTransform>();
            if (rect == null)
            {
                return false;
            }

            var ray = new Ray(origin, forward);
            var plane = new Plane(rect.forward, rect.position);
            if (!plane.Raycast(ray, out float enter) || enter < 0f || enter > _diagnosticRayLength)
            {
                return false;
            }

            Vector3 point = ray.GetPoint(enter);
            Vector2 local = rect.InverseTransformPoint(point);
            bool hit = rect.rect.Contains(local);
            if (hit)
            {
                targetName = GetPath(_targetCanvas.transform);
                distance = enter;
            }

            return hit;
        }

        private Transform ResolveRayOrigin(Transform interactorTransform)
        {
            Transform attach = FindChildByNameContains(interactorTransform, "Attach");
            return attach != null ? attach : interactorTransform;
        }

        private bool IsUnderTargetCanvas(Transform target)
        {
            if (_targetCanvas == null || target == null)
            {
                return false;
            }

            return target == _targetCanvas.transform || target.IsChildOf(_targetCanvas.transform);
        }

        private int CountInteractorsNamed(string name)
        {
            int count = 0;
            foreach (Component interactor in _interactors)
            {
                if (interactor != null &&
                    interactor.GetType().Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    count++;
                }
            }

            return count;
        }

        private int CountRuntimeUiRayInteractors(out int activeCount)
        {
            int count = 0;
            activeCount = 0;
            foreach (Component interactor in _interactors)
            {
                if (interactor == null || !IsRuntimeUiRay(interactor))
                {
                    continue;
                }

                count++;
                if (interactor is Behaviour behaviour && behaviour.isActiveAndEnabled)
                {
                    activeCount++;
                }
            }

            return count;
        }

        private int CountManualUiLasers(out int activeCount)
        {
            int count = 0;
            activeCount = 0;
            foreach (ExperimentRuntimeManualUiLaserPointer pointer in
                     FindObjectsByType<ExperimentRuntimeManualUiLaserPointer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (pointer == null)
                {
                    continue;
                }

                count++;
                if (pointer.isActiveAndEnabled)
                {
                    activeCount++;
                }
            }

            return count;
        }

        private static bool IsRuntimeUiRay(Component component)
        {
            return component != null &&
                GetPath(component.transform).IndexOf("Runtime UI Ray Interactor", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void CollectInterestingState(object target, List<string> values, params string[] names)
        {
            foreach (string name in names)
            {
                string value = ReadMemberString(target, name);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add($"{target.GetType().Name}.{name}={value}");
                }
            }
        }

        private static bool CouldContainAction(string name)
        {
            return name.IndexOf("Action", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Press", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Select", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Scroll", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Point", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Tracking", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Position", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Rotation", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<MemberInfo> GetReadableMembers(Type type)
        {
            foreach (FieldInfo field in type.GetFields(MemberFlags))
            {
                if (!field.IsStatic && !Attribute.IsDefined(field, typeof(ObsoleteAttribute)))
                {
                    yield return field;
                }
            }

            foreach (PropertyInfo property in type.GetProperties(MemberFlags))
            {
                if (property.GetIndexParameters().Length == 0 &&
                    property.GetMethod != null &&
                    !property.GetMethod.IsStatic &&
                    !Attribute.IsDefined(property, typeof(ObsoleteAttribute)))
                {
                    yield return property;
                }
            }
        }

        private static object TryGetNamedValue(object target, params string[] names)
        {
            if (target == null)
            {
                return null;
            }

            Type type = target.GetType();
            foreach (string name in names)
            {
                FieldInfo field = type.GetField(name, MemberFlags);
                if (field != null)
                {
                    return SafeGetValue(target, field);
                }

                PropertyInfo property = type.GetProperty(name, MemberFlags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return SafeGetValue(target, property);
                }
            }

            return null;
        }

        private static object TryGetMemberValue(object target, MemberInfo member)
        {
            if (member is FieldInfo field)
            {
                return SafeGetValue(target, field);
            }

            if (member is PropertyInfo property)
            {
                return SafeGetValue(target, property);
            }

            return null;
        }

        private static object SafeGetValue(object target, FieldInfo field)
        {
            try
            {
                return field.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        private static object SafeGetValue(object target, PropertyInfo property)
        {
            try
            {
                return property.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        private static string ReadMemberString(object target, params string[] names)
        {
            object value = TryGetNamedValue(target, names);
            return value == null ? string.Empty : Truncate(value.ToString());
        }

        private static bool ReadBool(object target, bool fallback, params string[] names)
        {
            object value = TryGetNamedValue(target, names);
            return value is bool boolValue ? boolValue : fallback;
        }

        private static MethodInfo FindMethod(Type type, string name)
        {
            while (type != null)
            {
                MethodInfo method = type.GetMethod(name, MemberFlags);
                if (method != null)
                {
                    return method;
                }

                type = type.BaseType;
            }

            return null;
        }

        private static string TryInvokeMember(object target, string methodName)
        {
            if (target == null)
            {
                return string.Empty;
            }

            MethodInfo method = FindMethod(target.GetType(), methodName);
            if (method == null || method.GetParameters().Length != 0)
            {
                return string.Empty;
            }

            try
            {
                object result = method.Invoke(target, null);
                return result != null ? Truncate(result.ToString()) : "null";
            }
            catch
            {
                return "exception";
            }
        }

        private static string TryInvokeActionReadValue(object action)
        {
            string objectValue = TryInvokeMember(action, "ReadValueAsObject");
            return string.IsNullOrWhiteSpace(objectValue) ? "unread" : objectValue;
        }

        private static string DescribeObjectName(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            object name = TryGetNamedValue(value, "name", "Name");
            return name != null ? name.ToString() : value.ToString();
        }

        private static string DescribeBindings(object bindings)
        {
            if (bindings is not IEnumerable enumerable)
            {
                return string.Empty;
            }

            var values = new List<string>();
            foreach (object binding in enumerable)
            {
                if (binding == null)
                {
                    continue;
                }

                string effectivePath = ReadMemberString(binding, "effectivePath", "EffectivePath");
                string path = ReadMemberString(binding, "path", "Path");
                values.Add(string.IsNullOrWhiteSpace(effectivePath) ? path : effectivePath);
                if (values.Count >= 12)
                {
                    break;
                }
            }

            return JoinLimited(values);
        }

        private static int CountEnumerable(object value)
        {
            if (value is not IEnumerable enumerable)
            {
                return 0;
            }

            int count = 0;
            foreach (object _ in enumerable)
            {
                count++;
                if (count > 500)
                {
                    break;
                }
            }

            return count;
        }

        private static string DescribeInterestingMembers(object target, params string[] nameFragments)
        {
            if (target == null)
            {
                return string.Empty;
            }

            var values = new List<string>();
            foreach (MemberInfo member in GetReadableMembers(target.GetType()))
            {
                if (!NameContainsAny(member.Name, nameFragments))
                {
                    continue;
                }

                object value = TryGetMemberValue(target, member);
                if (value == null)
                {
                    continue;
                }

                values.Add($"{member.Name}={ValueToShortString(value)}");
                if (values.Count >= 30)
                {
                    break;
                }
            }

            return JoinLimited(values);
        }

        private static bool NameContainsAny(string name, params string[] fragments)
        {
            foreach (string fragment in fragments)
            {
                if (name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string ValueToShortString(object value)
        {
            if (value == null)
            {
                return "null";
            }

            if (value is Object unityObject)
            {
                return unityObject != null ? unityObject.name : "null_unity_object";
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                return $"count={CountEnumerable(enumerable)}";
            }

            return Truncate(value.ToString());
        }

        private static bool ImplementsInterfaceNamed(Type type, string interfaceName)
        {
            foreach (Type iface in type.GetInterfaces())
            {
                if (string.Equals(iface.Name, interfaceName, StringComparison.Ordinal) ||
                    string.Equals(iface.FullName, interfaceName, StringComparison.Ordinal) ||
                    iface.Name.IndexOf(interfaceName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string DescribeInterfaces(Type type)
        {
            var interfaces = new List<string>();
            foreach (Type iface in type.GetInterfaces())
            {
                string name = iface.Name;
                if (name.IndexOf("XR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("UI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Interactor", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    interfaces.Add(iface.FullName ?? iface.Name);
                }
            }

            return JoinLimited(interfaces);
        }

        private static Component FindChildComponentByName(Transform root, string nameFragment)
        {
            if (root == null)
            {
                return null;
            }

            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component != null &&
                    component.GetType().Name.IndexOf(nameFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return component;
                }
            }

            return null;
        }

        private static Transform FindChildByNameContains(Transform root, string nameFragment)
        {
            if (root == null)
            {
                return null;
            }

            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.IndexOf(nameFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return child;
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

        private static string JoinLimited(IReadOnlyCollection<string> values, int maxLength = MaxText)
        {
            if (values == null || values.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (string value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(" | ");
                }

                builder.Append(value);
                if (builder.Length >= maxLength)
                {
                    builder.Append("...");
                    break;
                }
            }

            return builder.ToString();
        }

        private static string Truncate(string value, int maxLength = MaxText)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maxLength) + "...";
        }

        private static void LogStructured(string eventType, Dictionary<string, object> payload)
        {
            payload ??= new Dictionary<string, object>();
            payload["diagnostic_component"] = nameof(ExperimentRuntimeXrUiRayDiagnostic);
            payload["diagnostic_scope"] = "P45C-03-DIAG";
            TiagoExperimentTelemetry.LogEvent(eventType, payload);
            Debug.Log($"{LogPrefix} {eventType} | {CompactPayload(payload)}");
        }

        private static string CompactPayload(Dictionary<string, object> payload)
        {
            var values = new List<string>();
            foreach (KeyValuePair<string, object> pair in payload)
            {
                values.Add($"{pair.Key}={ValueToShortString(pair.Value)}");
                if (values.Count >= 12)
                {
                    break;
                }
            }

            return JoinLimited(values, 1600);
        }
    }
}
