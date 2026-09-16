using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class ExperimentPauseFrontCanvasPointerBridge : MonoBehaviour
    {
        private const string UiLogPrefix = "[P46D-PAUSE-UI]";
        private const int LeftPointerId = -46141;
        private const int RightPointerId = -46142;
        private const float TriggerClickThreshold = 0.72f;
        private const float RayLogIntervalSeconds = 0.50f;
        private const float NoHitLogIntervalSeconds = 1.00f;
        private const string PauseRayClipLogPrefix = "[P46I-03][PAUSE-RAY-CLIP]";
        private const string LeftStableVisualName = "LeftStableProtocolUiPointerP46N";
        private const string RightStableVisualName = "RightStableProtocolUiPointerP46N";
        private const float VisualResolveRetrySeconds = 0.50f;

        [SerializeField] private Canvas _targetCanvas;

        private readonly SideState _left = new("left", XRNode.LeftHand, InputDeviceCharacteristics.Left, LeftPointerId);
        private readonly SideState _right = new("right", XRNode.RightHand, InputDeviceCharacteristics.Right, RightPointerId);
        private readonly List<Button> _explicitButtonTargets = new();
        private float _nextRayLogAt;
        private float _nextNoHitLogAt;
        private bool _active;

        public bool IsBridgeActive => _active;
        public Canvas TargetCanvas => _targetCanvas;

        public static ExperimentPauseFrontCanvasPointerBridge Ensure(GameObject host, Canvas targetCanvas)
        {
            if (host == null)
            {
                return null;
            }

            ExperimentPauseFrontCanvasPointerBridge bridge = host.GetComponent<ExperimentPauseFrontCanvasPointerBridge>();
            if (bridge == null)
            {
                bridge = host.AddComponent<ExperimentPauseFrontCanvasPointerBridge>();
            }

            bridge.SetTargetCanvas(targetCanvas);
            bridge.enabled = true;
            return bridge;
        }

        public void SetTargetCanvas(Canvas targetCanvas)
        {
            _targetCanvas = targetCanvas;
        }

        public void SetExplicitButtonTargets(IEnumerable<Button> buttons)
        {
            _explicitButtonTargets.Clear();
            if (buttons != null)
            {
                foreach (Button button in buttons)
                {
                    if (button != null && !_explicitButtonTargets.Contains(button))
                    {
                        _explicitButtonTargets.Add(button);
                    }
                }
            }

            Debug.Log($"[P46D-PAUSE] saved_exit_prompt_pointer_bridge_explicit_buttons_configured | canvas_path={GetPath(_targetCanvas != null ? _targetCanvas.transform : null)} button_count={_explicitButtonTargets.Count} buttons={DescribeButtons(_explicitButtonTargets)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        }

        public void ClearExplicitButtonTargets()
        {
            _explicitButtonTargets.Clear();
        }

        public void Activate(Canvas targetCanvas)
        {
            SetTargetCanvas(targetCanvas);
            _active = _targetCanvas != null && _targetCanvas.enabled;
            ResetSide(_left, "activate");
            ResetSide(_right, "activate");
            Debug.Log($"{UiLogPrefix} pause_front_pointer_bridge_enabled | active={_active} canvas_path={GetPath(_targetCanvas != null ? _targetCanvas.transform : null)} eventSystem={DescribeEventSystem()} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            Debug.Log($"{UiLogPrefix} pause_front_pointer_bridge_target_canvas | canvas_path={GetPath(_targetCanvas != null ? _targetCanvas.transform : null)} renderMode={(_targetCanvas != null ? _targetCanvas.renderMode.ToString() : string.Empty)} eventCamera={(_targetCanvas != null && _targetCanvas.worldCamera != null ? _targetCanvas.worldCamera.name : string.Empty)} eventSystem={DescribeEventSystem()} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        }

        public void Deactivate(string reason)
        {
            if (!_active && _left.HoverTarget == null && _right.HoverTarget == null && _left.PressedTarget == null && _right.PressedTarget == null)
            {
                return;
            }

            ResetSide(_left, reason ?? "deactivate");
            ResetSide(_right, reason ?? "deactivate");
            ClearEventSystemSelectionIfOwned();
            _active = false;
            Debug.Log($"{UiLogPrefix} pause_front_pointer_bridge_disabled | reason={reason ?? string.Empty} canvas_path={GetPath(_targetCanvas != null ? _targetCanvas.transform : null)} eventSystem={DescribeEventSystem()} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            Debug.Log($"{UiLogPrefix} pause_front_pointer_bridge_cleared_pointer_state | reason={reason ?? string.Empty} left_hover=False left_pressed=False right_hover=False right_pressed=False canvas_path={GetPath(_targetCanvas != null ? _targetCanvas.transform : null)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            Debug.Log($"{UiLogPrefix} pause_modal_hidden_no_raycasts | reason={reason ?? string.Empty} canvas_path={GetPath(_targetCanvas != null ? _targetCanvas.transform : null)} canvas_enabled={(_targetCanvas != null && _targetCanvas.enabled)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        }

        private void OnDisable()
        {
            Deactivate("component_disabled");
        }

        private void Update()
        {
            if (!_active || _targetCanvas == null || !_targetCanvas.enabled || !_targetCanvas.gameObject.activeInHierarchy)
            {
                return;
            }

            ProcessSide(_left);
            ProcessSide(_right);
        }

        private void LateUpdate()
        {
            if (!_active || _targetCanvas == null || !_targetCanvas.enabled || !_targetCanvas.gameObject.activeInHierarchy)
            {
                return;
            }

            ApplyPauseVisualClip(_left);
            ApplyPauseVisualClip(_right);
        }

        private void ProcessSide(SideState side)
        {
            if (!IsBridgeTargetAlive())
            {
                Debug.LogWarning($"{UiLogPrefix} pause_bridge_null_guard_triggered | phase=process_side_start side={side.Name} canvas_path={GetPath(_targetCanvas != null ? _targetCanvas.transform : null)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
                Deactivate("target_canvas_missing_process_side");
                return;
            }

            if (!TryBuildControllerRay(side, out Ray ray, out InputDevice device, out bool selectPressed, out string source))
            {
                side.SelectWasPressed = false;
                side.HasPauseCanvasHit = false;
                side.HasControllerRay = false;
                return;
            }

            side.ControllerRay = ray;
            side.HasControllerRay = true;

            bool logRaySnapshot = QuestLoggingPolicy.EmitLegacyContinuousDiagnostics &&
                Time.unscaledTime >= _nextRayLogAt;
            if (logRaySnapshot)
            {
                _nextRayLogAt = Time.unscaledTime + RayLogIntervalSeconds;
                Debug.Log($"{UiLogPrefix} pause_front_pointer_bridge_ray | side={side.Name} source={source} device_name=\"{device.name}\" characteristics={device.characteristics} isValid={device.isValid} origin={ray.origin} direction={ray.direction} canvas_path={GetPath(_targetCanvas.transform)} eventSystem={DescribeEventSystem()} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            }

            PauseCanvasHit hit = RaycastPauseCanvas(_targetCanvas, ray, side.PointerId);
            if (!hit.CanvasHit || !hit.RectHit)
            {
                side.HasPauseCanvasHit = false;
                MaybeLogNoHit(side, ray, hit, source);
                UpdateHover(side, default);
                UpdateClick(side, selectPressed, default);
                return;
            }

            side.HasPauseCanvasHit = true;
            side.PauseCanvasHitPoint = hit.WorldPoint;
            side.PauseCanvasHitDistance = hit.Distance;

            if (logRaySnapshot)
            {
                Debug.Log($"{UiLogPrefix} pause_front_pointer_bridge_canvas_hit | side={side.Name} canvas_path={GetPath(_targetCanvas.transform)} localPoint={hit.LocalPoint} worldPoint={hit.WorldPoint} screenPoint={hit.ScreenPosition} distance={hit.Distance:0.###} source={source} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
                LogSavedExitPromptBridge("saved_exit_prompt_pointer_bridge_canvas_hit", side, null, null, hit, source);
            }

            bool startScreenCanvas = IsStartScreenCanvas(_targetCanvas);
            string buttonResolveReason = string.Empty;
            if (startScreenCanvas && logRaySnapshot)
            {
                LogSavedExitPromptBridge("saved_exit_prompt_pointer_bridge_local_point", side, null, null, hit, source);
            }

            GameObject buttonTarget = startScreenCanvas
                ? ResolveBestButtonTargetAtCanvasLocalPoint(_targetCanvas, hit.LocalPoint, _explicitButtonTargets, out buttonResolveReason)
                : null;
            if (startScreenCanvas && buttonTarget == null && logRaySnapshot)
            {
                LogSavedExitPromptBridge("saved_exit_prompt_no_button_at_local_point", side, null, null, hit, buttonResolveReason);
            }

            GameObject graphicTarget = buttonTarget != null
                ? ResolveButtonGraphicTarget(buttonTarget)
                : ResolveBestGraphicTarget(_targetCanvas, hit.ScreenPosition);
            hit.Target = buttonTarget != null ? buttonTarget : ResolveButtonCandidate(graphicTarget);
            hit.Valid = hit.Target != null;
            if (graphicTarget != null && logRaySnapshot)
            {
                Debug.Log($"{UiLogPrefix} pause_front_pointer_bridge_graphic_hit | side={side.Name} canvas_path={GetPath(_targetCanvas.transform)} graphic_path={GetPath(graphicTarget.transform)} button_path={GetPath(hit.Target != null ? hit.Target.transform : null)} source={source} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
                LogSavedExitPromptBridge("saved_exit_prompt_pointer_bridge_graphic_hit", side, graphicTarget, hit.Target, hit, source);
            }

            if (hit.Target != null && logRaySnapshot)
            {
                Button button = hit.Target.GetComponent<Button>();
                Debug.Log($"{UiLogPrefix} pause_front_pointer_bridge_button_candidate | side={side.Name} button_path={GetPath(hit.Target.transform)} interactable={(button != null && button.interactable)} isInteractable={(button != null && button.IsInteractable())} source={source} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
                LogSavedExitPromptBridge("saved_exit_prompt_pointer_bridge_button_candidate", side, graphicTarget, hit.Target, hit, source);
                LogSavedExitPromptBridge("saved_exit_prompt_pointer_bridge_target_button", side, graphicTarget, hit.Target, hit, source);
            }

            UpdateHover(side, hit);
            UpdateClick(side, selectPressed, hit);
        }

        private void MaybeLogNoHit(SideState side, Ray ray, PauseCanvasHit hit, string source)
        {
            if (!QuestLoggingPolicy.EmitLegacyContinuousDiagnostics ||
                Time.unscaledTime < _nextNoHitLogAt)
            {
                return;
            }

            _nextNoHitLogAt = Time.unscaledTime + NoHitLogIntervalSeconds;
            Debug.Log($"{UiLogPrefix} pause_front_pointer_bridge_no_hit | side={side.Name} source={source} canvas_hit={hit.CanvasHit} rect_hit={hit.RectHit} origin={ray.origin} direction={ray.direction} canvas_path={GetPath(_targetCanvas != null ? _targetCanvas.transform : null)} eventSystem={DescribeEventSystem()} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            Canvas runtimeCanvas = TryFindRuntimeProtocolCanvas();
            if (runtimeCanvas != null)
            {
                PauseCanvasHit runtimeHit = RaycastPauseCanvas(runtimeCanvas, ray, side.PointerId);
                if (runtimeHit.CanvasHit && runtimeHit.RectHit)
                {
                    Debug.Log($"{UiLogPrefix} pause_front_pointer_bridge_blocked_by_runtime_canvas | side={side.Name} runtime_canvas_path={GetPath(runtimeCanvas.transform)} pause_canvas_path={GetPath(_targetCanvas != null ? _targetCanvas.transform : null)} runtime_distance={runtimeHit.Distance:0.###} pause_canvas_hit={hit.CanvasHit} pause_rect_hit={hit.RectHit} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
                }
            }
        }

        private void UpdateHover(SideState side, PauseCanvasHit hit)
        {
            GameObject target = hit.Valid ? hit.Target : null;
            if (target == side.HoverTarget)
            {
                return;
            }

            PointerEventData eventData = BuildPointerEvent(side.PointerId, hit);
            if (side.HoverTarget != null)
            {
                GameObject previousHover = side.HoverTarget;
                if (previousHover != null)
                {
                    ExecuteEvents.Execute(previousHover, eventData, ExecuteEvents.pointerExitHandler);
                }
            }

            side.HoverTarget = target;
            if (side.HoverTarget != null)
            {
                ExecuteEvents.Execute(side.HoverTarget, eventData, ExecuteEvents.pointerEnterHandler);
            }
        }

        private void UpdateClick(SideState side, bool pressed, PauseCanvasHit hit)
        {
            if (!IsBridgeTargetAlive())
            {
                Debug.LogWarning($"{UiLogPrefix} pause_bridge_null_guard_triggered | phase=update_click_start side={side.Name} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
                side.PressedTarget = null;
                side.SelectWasPressed = pressed;
                return;
            }

            if (pressed && !side.SelectWasPressed)
            {
                if (hit.Valid)
                {
                    side.PressedTarget = hit.Target;
                    PointerEventData eventData = BuildPointerEvent(side.PointerId, hit);
                    string pressedPath = GetPath(side.PressedTarget != null ? side.PressedTarget.transform : null);
                    LogSavedExitPromptBridge("saved_exit_prompt_pointer_bridge_target_button", side, side.PressedTarget, side.PressedTarget, hit, "pointer_down");
                    ExecuteEvents.ExecuteHierarchy(side.PressedTarget, eventData, ExecuteEvents.pointerDownHandler);
                    Debug.Log($"{UiLogPrefix} pause_front_pointer_bridge_pointer_down | side={side.Name} button_path={pressedPath} canvas_path={GetPath(_targetCanvas != null ? _targetCanvas.transform : null)} eventSystem={DescribeEventSystem()} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
                    if (!IsBridgeTargetAlive())
                    {
                        Debug.LogWarning($"{UiLogPrefix} pause_bridge_update_aborted_after_ui_destroyed | phase=pointer_down side={side.Name} button_path={pressedPath} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
                        side.PressedTarget = null;
                        side.SelectWasPressed = pressed;
                        return;
                    }
                }
                else
                {
                    side.PressedTarget = null;
                }
            }

            if (!pressed && side.SelectWasPressed)
            {
                PointerEventData eventData = BuildPointerEvent(side.PointerId, hit);
                if (side.PressedTarget != null)
                {
                    GameObject pressedTarget = side.PressedTarget;
                    string pressedPath = GetPath(pressedTarget != null ? pressedTarget.transform : null);
                    string currentPath = GetPath(hit.Target != null ? hit.Target.transform : null);
                    ExecuteEvents.ExecuteHierarchy(pressedTarget, eventData, ExecuteEvents.pointerUpHandler);
                    Debug.Log($"{UiLogPrefix} pause_front_pointer_bridge_pointer_up | side={side.Name} button_path={pressedPath} current_target={currentPath} canvas_path={GetPath(_targetCanvas != null ? _targetCanvas.transform : null)} eventSystem={DescribeEventSystem()} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
                    if (!IsBridgeTargetAlive() || pressedTarget == null)
                    {
                        Debug.LogWarning($"{UiLogPrefix} pause_bridge_update_aborted_after_ui_destroyed | phase=pointer_up side={side.Name} button_path={pressedPath} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
                        side.PressedTarget = null;
                        side.SelectWasPressed = pressed;
                        return;
                    }

                    if (hit.Valid && hit.Target == side.PressedTarget)
                    {
                        ExecuteEvents.ExecuteHierarchy(pressedTarget, eventData, ExecuteEvents.pointerClickHandler);
                        Debug.Log($"{UiLogPrefix} pause_front_pointer_bridge_click_invoked | side={side.Name} button_path={pressedPath} canvas_path={GetPath(_targetCanvas != null ? _targetCanvas.transform : null)} eventSystem={DescribeEventSystem()} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
                        LogSavedExitPromptBridge("saved_exit_prompt_pointer_bridge_click_invoked", side, pressedTarget, pressedTarget, hit, "click");
                        if (!IsBridgeTargetAlive() || !_active || pressedTarget == null)
                        {
                            Debug.LogWarning($"{UiLogPrefix} pause_bridge_update_aborted_after_ui_destroyed | phase=pointer_click side={side.Name} button_path={pressedPath} active={_active} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
                            side.PressedTarget = null;
                            side.SelectWasPressed = pressed;
                            return;
                        }
                    }
                }

                side.PressedTarget = null;
            }

            side.SelectWasPressed = pressed;
        }

        private PointerEventData BuildPointerEvent(int pointerId, PauseCanvasHit hit)
        {
            EventSystem eventSystem = EventSystem.current;
            var raycast = new RaycastResult
            {
                gameObject = hit.Target,
                module = _targetCanvas != null ? _targetCanvas.GetComponent<BaseRaycaster>() : null,
                worldPosition = hit.WorldPoint,
                screenPosition = hit.ScreenPosition,
                distance = hit.Distance
            };
            return new PointerEventData(eventSystem)
            {
                pointerId = pointerId,
                position = hit.ScreenPosition,
                pressPosition = hit.ScreenPosition,
                button = PointerEventData.InputButton.Left,
                eligibleForClick = true,
                clickCount = 1,
                pointerCurrentRaycast = raycast,
                pointerPressRaycast = raycast
            };
        }

        private void ResetSide(SideState side, string reason)
        {
            ReleasePauseVisualClip(side, reason, true);
            if (side.HoverTarget != null)
            {
                GameObject hoverTarget = side.HoverTarget;
                if (hoverTarget != null)
                {
                    ExecuteEvents.Execute(hoverTarget, new PointerEventData(EventSystem.current), ExecuteEvents.pointerExitHandler);
                }
            }

            side.HoverTarget = null;
            side.PressedTarget = null;
            side.SelectWasPressed = false;
            side.HasCachedDevice = false;
            side.CachedDevice = default;
            side.HasPauseCanvasHit = false;
            side.HasControllerRay = false;
        }

        private void ApplyPauseVisualClip(SideState side)
        {
            LineRenderer line = ResolveStableVisualLine(side);
            if (!side.HasPauseCanvasHit)
            {
                ReleasePauseVisualClip(side, "pause_canvas_miss", false);
                return;
            }

            if (line == null || !line.enabled || !line.gameObject.activeInHierarchy || line.positionCount < 2)
            {
                ReleasePauseVisualClip(side, "stable_visual_unavailable", false);
                return;
            }

            int lastIndex = line.positionCount - 1;
            Vector3 firstWorld = line.useWorldSpace
                ? line.GetPosition(0)
                : line.transform.TransformPoint(line.GetPosition(0));
            Vector3 currentEndWorld = line.useWorldSpace
                ? line.GetPosition(lastIndex)
                : line.transform.TransformPoint(line.GetPosition(lastIndex));
            float currentLength = Vector3.Distance(firstWorld, currentEndWorld);
            float clippedLength = Vector3.Distance(firstWorld, side.PauseCanvasHitPoint);
            if (!side.VisualClipApplied || currentLength > clippedLength + 0.01f)
            {
                side.UnclippedVisualLength = Mathf.Max(side.UnclippedVisualLength, currentLength);
            }

            Vector3 linePoint = line.useWorldSpace
                ? side.PauseCanvasHitPoint
                : line.transform.InverseTransformPoint(side.PauseCanvasHitPoint);
            line.SetPosition(lastIndex, linePoint);

            if (side.VisualClipApplied)
            {
                return;
            }

            side.VisualClipApplied = true;
            Debug.Log($"{PauseRayClipLogPrefix} p46i03_pause_ui_ray_clip_visible_changed | side={side.Name} clipped=True line_path={GetPath(line.transform)} canvas_path={GetPath(_targetCanvas != null ? _targetCanvas.transform : null)} hit_point={side.PauseCanvasHitPoint} hit_distance={side.PauseCanvasHitDistance:0.###} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        }

        private void ReleasePauseVisualClip(SideState side, string reason, bool restoreEndpoint)
        {
            if (!side.VisualClipApplied)
            {
                return;
            }

            LineRenderer line = side.VisualLine;
            if (restoreEndpoint && line != null && line.positionCount >= 2 && side.HasControllerRay && side.UnclippedVisualLength > 0.01f)
            {
                Vector3 restoredWorldPoint = side.ControllerRay.origin + side.ControllerRay.direction.normalized * side.UnclippedVisualLength;
                int lastIndex = line.positionCount - 1;
                line.SetPosition(lastIndex, line.useWorldSpace
                    ? restoredWorldPoint
                    : line.transform.InverseTransformPoint(restoredWorldPoint));
            }

            side.VisualClipApplied = false;
            Debug.Log($"{PauseRayClipLogPrefix} p46i03_pause_ui_ray_clip_visible_changed | side={side.Name} clipped=False reason={reason ?? string.Empty} line_path={GetPath(line != null ? line.transform : null)} canvas_path={GetPath(_targetCanvas != null ? _targetCanvas.transform : null)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        }

        private LineRenderer ResolveStableVisualLine(SideState side)
        {
            if (side.VisualLine != null)
            {
                return side.VisualLine;
            }

            if (Time.unscaledTime < side.NextVisualResolveAt)
            {
                return null;
            }

            side.NextVisualResolveAt = Time.unscaledTime + VisualResolveRetrySeconds;
            string expectedName = side.Node == XRNode.RightHand ? RightStableVisualName : LeftStableVisualName;
            foreach (LineRenderer candidate in FindObjectsByType<LineRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (candidate != null && string.Equals(candidate.gameObject.name, expectedName, StringComparison.Ordinal))
                {
                    side.VisualLine = candidate;
                    Debug.Log($"{PauseRayClipLogPrefix} p46i03_pause_ui_ray_visual_bound | side={side.Name} line_path={GetPath(candidate.transform)} position_count={candidate.positionCount} world_space={candidate.useWorldSpace} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
                    return candidate;
                }
            }

            if (!side.VisualResolveFailureLogged)
            {
                side.VisualResolveFailureLogged = true;
                Debug.LogWarning($"{PauseRayClipLogPrefix} p46i03_pause_ui_ray_visual_not_found | side={side.Name} expected_name={expectedName} retry_seconds={VisualResolveRetrySeconds:0.00} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
            }

            return null;
        }

        private bool IsBridgeTargetAlive()
        {
            return _active &&
                _targetCanvas != null &&
                _targetCanvas.gameObject != null &&
                _targetCanvas.enabled &&
                _targetCanvas.gameObject.activeInHierarchy;
        }

        private void LogSavedExitPromptBridge(
            string eventName,
            SideState side,
            GameObject graphicTarget,
            GameObject buttonTarget,
            PauseCanvasHit hit,
            string source)
        {
            if (_targetCanvas == null || !IsStartScreenCanvas(_targetCanvas))
            {
                return;
            }

            Button button = buttonTarget != null ? buttonTarget.GetComponent<Button>() : null;
            string label = ResolveButtonLabel(button);
            Debug.Log($"[P46D-PAUSE] {eventName} | label=\"{label}\" side={side.Name} source={source ?? string.Empty} canvas_path={GetPath(_targetCanvas.transform)} graphic_path={GetPath(graphicTarget != null ? graphicTarget.transform : null)} button_path={GetPath(buttonTarget != null ? buttonTarget.transform : null)} button_interactable={(button != null && button.interactable)} button_isInteractable={(button != null && button.IsInteractable())} localPoint={hit.LocalPoint} worldPoint={hit.WorldPoint} screenPoint={hit.ScreenPosition} distance={hit.Distance:0.###} explicit_button_count={_explicitButtonTargets.Count} session_id={ExperimentDataPathResolver.CurrentSessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        }

        private static bool IsStartScreenCanvas(Canvas canvas)
        {
            if (canvas == null)
            {
                return false;
            }

            return string.Equals(canvas.gameObject.name, "StartScreenUI", StringComparison.Ordinal) ||
                GetPath(canvas.transform).IndexOf("StartScreenUI", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ClearEventSystemSelectionIfOwned()
        {
            EventSystem eventSystem = EventSystem.current;
            GameObject selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
            if (eventSystem == null || selected == null || _targetCanvas == null)
            {
                return;
            }

            if (selected.transform == null || !selected.transform.IsChildOf(_targetCanvas.transform))
            {
                return;
            }

            eventSystem.SetSelectedGameObject(null);
            Debug.Log($"{UiLogPrefix} pause_front_pointer_bridge_cleared_eventsystem_selection | previous_selection={GetPath(selected.transform)} canvas_path={GetPath(_targetCanvas.transform)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        }

        private bool TryBuildControllerRay(
            SideState side,
            out Ray ray,
            out InputDevice device,
            out bool selectPressed,
            out string source)
        {
            ray = default;
            device = default;
            selectPressed = false;
            source = "none";
            if (!TryResolvePhysicalController(side, out device))
            {
                return false;
            }

            selectPressed = ReadSelectPressed(device);
            Transform fallback = FindControllerAimTransform(side);
            if (fallback != null)
            {
                ray = new Ray(fallback.position, fallback.forward);
                source = "controller_aim_transform";
                return true;
            }

            if (device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position) &&
                device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rotation))
            {
                ray = new Ray(position, rotation * Vector3.forward);
                source = "input_device_pose";
                return true;
            }

            return false;
        }

        private bool TryResolvePhysicalController(SideState side, out InputDevice device)
        {
            if (side.HasCachedDevice && side.CachedDevice.isValid && IsPhysicalController(side.CachedDevice, side.Handedness))
            {
                device = side.CachedDevice;
                return true;
            }

            InputDevice nodeDevice = InputDevices.GetDeviceAtXRNode(side.Node);
            if (nodeDevice.isValid && IsPhysicalController(nodeDevice, side.Handedness))
            {
                side.CachedDevice = nodeDevice;
                side.HasCachedDevice = true;
                device = nodeDevice;
                return true;
            }

            var devices = new List<InputDevice>();
            InputDevices.GetDevicesWithCharacteristics(side.Handedness | InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.HeldInHand, devices);
            if (devices.Count == 0)
            {
                InputDevices.GetDevices(devices);
            }

            foreach (InputDevice candidate in devices)
            {
                if (!candidate.isValid || !IsPhysicalController(candidate, side.Handedness))
                {
                    continue;
                }

                side.CachedDevice = candidate;
                side.HasCachedDevice = true;
                device = candidate;
                return true;
            }

            side.HasCachedDevice = false;
            side.CachedDevice = default;
            device = default;
            return false;
        }

        private static bool IsPhysicalController(InputDevice device, InputDeviceCharacteristics handedness)
        {
            string name = device.name ?? string.Empty;
            return device.isValid &&
                (device.characteristics & handedness) == handedness &&
                (device.characteristics & InputDeviceCharacteristics.Controller) == InputDeviceCharacteristics.Controller &&
                (device.characteristics & InputDeviceCharacteristics.HeldInHand) == InputDeviceCharacteristics.HeldInHand &&
                !ExperimentPauseInputDeviceResolver.IsClearlyHandOrPseudoDevice(name);
        }

        private static bool ReadSelectPressed(InputDevice device)
        {
            if (device.TryGetFeatureValue(CommonUsages.triggerButton, out bool triggerButton) && triggerButton)
            {
                return true;
            }

            if (device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primaryButton) && primaryButton)
            {
                return true;
            }

            return device.TryGetFeatureValue(CommonUsages.trigger, out float trigger) && trigger >= TriggerClickThreshold;
        }

        private static Transform FindControllerAimTransform(SideState side)
        {
            string rootName = side.Node == XRNode.RightHand ? "Right Controller" : "Left Controller";
            Transform bestRoot = null;
            foreach (Transform transform in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (transform == null || !transform.gameObject.activeInHierarchy ||
                    transform.name.IndexOf(rootName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (bestRoot == null || GetPath(transform).Length < GetPath(bestRoot).Length)
                {
                    bestRoot = transform;
                }
            }

            if (bestRoot == null)
            {
                return null;
            }

            foreach (string name in new[] { "Ray Origin", "Aim", "Controller Stabilized", "Stabilized" })
            {
                Transform named = FindChildContaining(bestRoot, name);
                if (named != null && named.gameObject.activeInHierarchy)
                {
                    return named;
                }
            }

            Transform nearFar = FindChildContaining(bestRoot, "Near-Far Interactor");
            if (nearFar != null && nearFar.gameObject.activeInHierarchy)
            {
                Transform rayOrigin = FindChildContaining(nearFar, "Ray Origin") ?? FindChildContaining(nearFar, "Aim");
                if (rayOrigin != null && rayOrigin.gameObject.activeInHierarchy)
                {
                    return rayOrigin;
                }

                return nearFar;
            }

            return bestRoot;
        }

        private static Transform FindChildContaining(Transform root, string token)
        {
            if (root == null || string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child != null &&
                    child != root &&
                    child.gameObject.activeInHierarchy &&
                    child.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return child;
                }
            }

            return null;
        }

        public static PauseCanvasHit RaycastPauseCanvas(Canvas canvas, Ray ray, int pointerId = 0)
        {
            PauseCanvasHit hit = default;
            hit.PointerId = pointerId;
            if (canvas == null || !(canvas.transform is RectTransform rect))
            {
                return hit;
            }

            var plane = new Plane(rect.forward, rect.position);
            if (!plane.Raycast(ray, out float distance) || distance < 0f)
            {
                return hit;
            }

            Vector3 worldPoint = ray.GetPoint(distance);
            Vector3 localPoint = rect.InverseTransformPoint(worldPoint);
            hit.CanvasHit = true;
            hit.RectHit = rect.rect.Contains(localPoint);
            hit.WorldPoint = worldPoint;
            hit.LocalPoint = localPoint;
            hit.Distance = distance;
            Camera eventCamera = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
            hit.ScreenPosition = RectTransformUtility.WorldToScreenPoint(eventCamera, worldPoint);
            return hit;
        }

        public static GameObject ResolveBestGraphicTarget(Canvas canvas, Vector2 screenPosition)
        {
            if (canvas == null)
            {
                return null;
            }

            Camera eventCamera = canvas.worldCamera;
            Graphic bestGraphic = null;
            int bestDepth = int.MinValue;
            IList<Graphic> graphics = GraphicRegistry.GetGraphicsForCanvas(canvas);
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

            return bestGraphic != null ? bestGraphic.gameObject : null;
        }

        public static GameObject ResolveButtonCandidate(GameObject graphicTarget)
        {
            if (graphicTarget == null)
            {
                return null;
            }

            Selectable selectable = graphicTarget.GetComponentInParent<Selectable>();
            if (selectable == null || !selectable.IsInteractable() || !selectable.gameObject.activeInHierarchy)
            {
                return null;
            }

            return selectable.gameObject;
        }

        public static GameObject ResolveBestButtonTarget(Canvas canvas, Vector2 screenPosition)
        {
            if (canvas == null)
            {
                return null;
            }

            Camera eventCamera = canvas.worldCamera;
            Button bestButton = null;
            int bestDepth = int.MinValue;
            Button[] buttons = canvas.GetComponentsInChildren<Button>(false);
            for (int i = 0; i < buttons.Length; i++)
            {
                Button button = buttons[i];
                if (button == null || !button.gameObject.activeInHierarchy || !button.IsInteractable())
                {
                    continue;
                }

                Graphic targetGraphic = button.targetGraphic;
                RectTransform rect = targetGraphic != null
                    ? targetGraphic.rectTransform
                    : button.transform as RectTransform;
                if (rect == null ||
                    !RectTransformUtility.RectangleContainsScreenPoint(rect, screenPosition, eventCamera))
                {
                    continue;
                }

                if (targetGraphic != null &&
                    targetGraphic.raycastTarget &&
                    !targetGraphic.Raycast(screenPosition, eventCamera))
                {
                    continue;
                }

                int depth = targetGraphic != null ? targetGraphic.depth : button.transform.GetSiblingIndex();
                if (depth >= bestDepth)
                {
                    bestDepth = depth;
                    bestButton = button;
                }
            }

            return bestButton != null ? bestButton.gameObject : null;
        }

        public static GameObject ResolveBestButtonTargetAtCanvasLocalPoint(
            Canvas canvas,
            Vector2 canvasLocalPoint,
            IReadOnlyList<Button> explicitButtons,
            out string reason)
        {
            reason = string.Empty;
            if (canvas == null || !(canvas.transform is RectTransform canvasRect))
            {
                reason = "canvas_missing";
                return null;
            }

            Vector3 worldPoint = canvasRect.TransformPoint(new Vector3(canvasLocalPoint.x, canvasLocalPoint.y, 0f));
            IReadOnlyList<Button> buttons = explicitButtons != null && explicitButtons.Count > 0
                ? explicitButtons
                : canvas.GetComponentsInChildren<Button>(false);
            Button bestButton = null;
            int bestScore = int.MinValue;
            string inspected = string.Empty;
            for (int i = 0; i < buttons.Count; i++)
            {
                Button button = buttons[i];
                if (button == null || !button.gameObject.activeInHierarchy || !button.IsInteractable())
                {
                    continue;
                }

                RectTransform rect = button.transform as RectTransform;
                if (rect == null)
                {
                    continue;
                }

                Vector3 local = rect.InverseTransformPoint(worldPoint);
                bool contains = rect.rect.Contains(local);
                inspected += $"{ResolveButtonLabel(button)}:contains={contains}:local={local}:rect={rect.rect};";
                if (!contains)
                {
                    continue;
                }

                int score = CalculateButtonPriorityScore(button);
                if (score >= bestScore)
                {
                    bestScore = score;
                    bestButton = button;
                }
            }

            reason = bestButton != null
                ? $"matched:{ResolveButtonLabel(bestButton)}"
                : $"no_rect_contains_local_point:{inspected}";
            return bestButton != null ? bestButton.gameObject : null;
        }

        private static GameObject ResolveButtonGraphicTarget(GameObject buttonTarget)
        {
            Button button = buttonTarget != null ? buttonTarget.GetComponent<Button>() : null;
            return button != null && button.targetGraphic != null
                ? button.targetGraphic.gameObject
                : buttonTarget;
        }

        private static int CalculateButtonPriorityScore(Button button)
        {
            if (button == null || button.transform == null)
            {
                return int.MinValue;
            }

            Graphic targetGraphic = button.targetGraphic;
            int depth = targetGraphic != null ? targetGraphic.depth : 0;
            return depth * 1000 + button.transform.GetSiblingIndex();
        }

        private static string ResolveButtonLabel(Button button)
        {
            if (button == null)
            {
                return string.Empty;
            }

            string name = button.gameObject.name ?? string.Empty;
            return name.StartsWith("Button_", StringComparison.Ordinal)
                ? name.Substring("Button_".Length)
                : name;
        }

        private static string DescribeButtons(IReadOnlyList<Button> buttons)
        {
            if (buttons == null || buttons.Count == 0)
            {
                return string.Empty;
            }

            var labels = new List<string>();
            for (int i = 0; i < buttons.Count; i++)
            {
                Button button = buttons[i];
                if (button != null)
                {
                    labels.Add($"{ResolveButtonLabel(button)}@{GetPath(button.transform)}");
                }
            }

            return string.Join("|", labels);
        }

        public static bool InvokeClickForTests(GameObject target, Canvas canvas)
        {
            if (target == null || canvas == null)
            {
                return false;
            }

            var hit = new PauseCanvasHit
            {
                Valid = true,
                Target = target,
                CanvasHit = true,
                RectHit = true,
                ScreenPosition = Vector2.zero,
                WorldPoint = target.transform.position,
                Distance = 0f,
                PointerId = -46999
            };
            var eventData = new PointerEventData(EventSystem.current)
            {
                pointerId = hit.PointerId,
                button = PointerEventData.InputButton.Left,
                eligibleForClick = true,
                clickCount = 1,
                pointerCurrentRaycast = new RaycastResult
                {
                    gameObject = target,
                    module = canvas.GetComponent<BaseRaycaster>(),
                    worldPosition = hit.WorldPoint,
                    screenPosition = hit.ScreenPosition
                }
            };

            ExecuteEvents.ExecuteHierarchy(target, eventData, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.ExecuteHierarchy(target, eventData, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.ExecuteHierarchy(target, eventData, ExecuteEvents.pointerClickHandler);
            return true;
        }

        private static Canvas TryFindRuntimeProtocolCanvas()
        {
            GameObject runtime = GameObject.Find("RuntimeProtocolUI");
            return runtime != null ? runtime.GetComponent<Canvas>() : null;
        }

        private static string DescribeEventSystem()
        {
            EventSystem eventSystem = EventSystem.current ?? UnityEngine.Object.FindFirstObjectByType<EventSystem>();
            BaseInputModule inputModule = eventSystem != null ? eventSystem.currentInputModule : null;
            return eventSystem != null
                ? $"{eventSystem.name}:{(inputModule != null ? inputModule.GetType().FullName : string.Empty)}"
                : string.Empty;
        }

        private static string GetPath(Transform target)
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

        private sealed class SideState
        {
            public SideState(string name, XRNode node, InputDeviceCharacteristics handedness, int pointerId)
            {
                Name = name;
                Node = node;
                Handedness = handedness;
                PointerId = pointerId;
            }

            public string Name { get; }
            public XRNode Node { get; }
            public InputDeviceCharacteristics Handedness { get; }
            public int PointerId { get; }
            public bool HasCachedDevice;
            public InputDevice CachedDevice;
            public bool SelectWasPressed;
            public GameObject HoverTarget;
            public GameObject PressedTarget;
            public Ray ControllerRay;
            public bool HasControllerRay;
            public bool HasPauseCanvasHit;
            public Vector3 PauseCanvasHitPoint;
            public float PauseCanvasHitDistance;
            public LineRenderer VisualLine;
            public bool VisualClipApplied;
            public float UnclippedVisualLength;
            public float NextVisualResolveAt;
            public bool VisualResolveFailureLogged;
        }
    }

    public struct PauseCanvasHit
    {
        public bool CanvasHit;
        public bool RectHit;
        public bool Valid;
        public int PointerId;
        public float Distance;
        public Vector3 WorldPoint;
        public Vector3 LocalPoint;
        public Vector2 ScreenPosition;
        public GameObject Target;
    }
}
