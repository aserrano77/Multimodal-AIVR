using System;
using System.Collections;
using System.Collections.Generic;
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
    public sealed class ExperimentRuntimeXrUiDiagnostics : MonoBehaviour
    {
        private const string LogPrefix = "[P46A-01D][XR-UI-DIAG]";
        private const int MaxItems = 28;
        private const int MaxText = 1800;

        [SerializeField] private Canvas _targetCanvas;
        [SerializeField] private bool _enableInEditor;
        [SerializeField] private float _periodicSnapshotSeconds = 5f;

        private bool _leftControllerTracked;
        private bool _rightControllerTracked;
        private bool _handTrackingSeen;
        private float _nextPeriodicSnapshotAt;

        public static ExperimentRuntimeXrUiDiagnostics EnsureAttached(GameObject protocolUiRoot, Canvas canvas)
        {
            if (protocolUiRoot == null)
            {
                return null;
            }

            Transform root = protocolUiRoot.transform.Find("XRUiDiagnosticsP46D");
            if (root == null)
            {
                var host = new GameObject("XRUiDiagnosticsP46D");
                host.transform.SetParent(protocolUiRoot.transform, false);
                root = host.transform;
            }

            ExperimentRuntimeXrUiDiagnostics diagnostics = root.GetComponent<ExperimentRuntimeXrUiDiagnostics>();
            if (diagnostics == null)
            {
                diagnostics = root.gameObject.AddComponent<ExperimentRuntimeXrUiDiagnostics>();
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

            StartCoroutine(StartSnapshots());
        }

        private void Update()
        {
            if (!ShouldRun())
            {
                return;
            }

            bool leftTracked = IsPhysicalControllerTracked(XRNode.LeftHand);
            bool rightTracked = IsPhysicalControllerTracked(XRNode.RightHand);
            bool anyTracked = leftTracked || rightTracked;
            bool anyPreviousTracked = _leftControllerTracked || _rightControllerTracked;
            bool handTracking = HasHandTrackingDevice();

            if (anyPreviousTracked && !anyTracked)
            {
                Snapshot("xr_ui_snapshot_controller_lost");
            }

            if (!anyPreviousTracked && anyTracked)
            {
                Snapshot("xr_ui_snapshot_before_transition_to_controllers");
                Snapshot("xr_ui_snapshot_controller_reacquired");
                Snapshot("xr_ui_snapshot_after_transition_to_controllers");
            }

            if (!_handTrackingSeen && handTracking)
            {
                Snapshot("xr_ui_snapshot_before_transition_to_hands");
                Snapshot("xr_ui_snapshot_hand_tracking_detected");
                Snapshot("xr_ui_snapshot_after_transition_to_hands");
            }

            _leftControllerTracked = leftTracked;
            _rightControllerTracked = rightTracked;
            _handTrackingSeen = handTracking;

            if (_targetCanvas != null && _targetCanvas.gameObject.activeInHierarchy && Time.unscaledTime >= _nextPeriodicSnapshotAt)
            {
                _nextPeriodicSnapshotAt = Time.unscaledTime + Mathf.Max(1f, _periodicSnapshotSeconds);
                Snapshot("xr_ui_snapshot_every_5s_when_ui_visible");
            }
        }

        private IEnumerator StartSnapshots()
        {
            yield return null;
            ResolveCanvas();
            Snapshot("xr_ui_snapshot_startup");
            yield return new WaitForSecondsRealtime(0.5f);
            Snapshot("xr_ui_snapshot_after_start_screen_ready");
            yield return new WaitForSecondsRealtime(0.5f);
            Snapshot("xr_ui_snapshot_before_first_button_press");
            _nextPeriodicSnapshotAt = Time.unscaledTime + Mathf.Max(1f, _periodicSnapshotSeconds);
        }

        private void OnDeviceConnected(XRInputDevice device)
        {
            if (!ShouldRun())
            {
                return;
            }

            string classification = ClassifyDevice(device, out _);
            Snapshot(string.Equals(classification, "physical_controller", StringComparison.Ordinal)
                ? "xr_ui_snapshot_controller_detected"
                : "xr_ui_snapshot_hand_tracking_detected");
        }

        private void OnDeviceDisconnected(XRInputDevice device)
        {
            if (ShouldRun())
            {
                Snapshot("xr_ui_snapshot_controller_lost");
            }
        }

        private void OnDeviceConfigChanged(XRInputDevice device)
        {
            if (!ShouldRun())
            {
                return;
            }

            string classification = ClassifyDevice(device, out _);
            if (string.Equals(classification, "physical_controller", StringComparison.Ordinal))
            {
                Snapshot("xr_ui_snapshot_controller_detected");
            }
            else if (string.Equals(classification, "hand_tracking", StringComparison.Ordinal))
            {
                Snapshot("xr_ui_snapshot_hand_tracking_detected");
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

        private void Snapshot(string eventType)
        {
            ResolveCanvas();
            Dictionary<string, object> payload = new()
            {
                ["scene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                ["time_unscaled"] = Time.unscaledTime,
                ["hierarchy"] = Limit(DescribeRelevantHierarchy()),
                ["devices"] = Limit(DescribeDevices()),
                ["ui_state"] = Limit(DescribeUiState()),
                ["interaction_state"] = Limit(DescribeInteractionState()),
                ["visual_state"] = Limit(DescribeVisualState()),
                ["ui_only"] = true,
                ["read_only"] = true,
                ["locomotion_modified"] = false
            };

            TiagoExperimentTelemetry.LogEvent(eventType, payload);
            Debug.Log($"{LogPrefix} {eventType} | hierarchy={payload["hierarchy"]} | devices={payload["devices"]} | ui={payload["ui_state"]} | visual={payload["visual_state"]}", this);
        }

        private string DescribeRelevantHierarchy()
        {
            var items = new List<string>();
            foreach (Transform transform in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (transform == null || !IsRelevant(transform))
                {
                    continue;
                }

                items.Add(DescribeTransform(transform));
                if (items.Count >= MaxItems)
                {
                    break;
                }
            }

            return items.Count == 0 ? "none" : string.Join(" || ", items);
        }

        private string DescribeDevices()
        {
            var devices = new List<XRInputDevice>();
            InputDevices.GetDevices(devices);
            var items = new List<string>();
            foreach (XRInputDevice device in devices)
            {
                string classification = ClassifyDevice(device, out string reason);
                items.Add($"name={device.name};valid={device.isValid};chars={device.characteristics};class={classification};reason={reason}");
            }

            return items.Count == 0 ? "none" : string.Join(" || ", items);
        }

        private string DescribeUiState()
        {
            EventSystem eventSystem = EventSystem.current ?? FindFirstObjectByType<EventSystem>();
            XRUIInputModule xrModule = eventSystem != null ? eventSystem.GetComponent<XRUIInputModule>() : null;
            int trackedRaycasters = _targetCanvas != null ? _targetCanvas.GetComponents<TrackedDeviceGraphicRaycaster>().Length : 0;
            int graphicRaycasters = _targetCanvas != null ? _targetCanvas.GetComponents<GraphicRaycaster>().Length : 0;
            int graphicTargets = 0;
            if (_targetCanvas != null)
            {
                foreach (Graphic graphic in _targetCanvas.GetComponentsInChildren<Graphic>(true))
                {
                    if (graphic != null && graphic.raycastTarget)
                    {
                        graphicTargets++;
                    }
                }
            }

            return $"eventSystem={Path(eventSystem != null ? eventSystem.transform : null)};currentModule={(eventSystem?.currentInputModule != null ? eventSystem.currentInputModule.GetType().Name : string.Empty)};xrUiEnabled={xrModule != null && xrModule.enabled};trackedRaycasters={trackedRaycasters};graphicRaycasters={graphicRaycasters};canvas={Path(_targetCanvas != null ? _targetCanvas.transform : null)};renderMode={(_targetCanvas != null ? _targetCanvas.renderMode.ToString() : string.Empty)};worldCamera={(_targetCanvas != null && _targetCanvas.worldCamera != null ? Path(_targetCanvas.worldCamera.transform) : string.Empty)};raycastTargets={graphicTargets}";
        }

        private string DescribeInteractionState()
        {
            var items = new List<string>();
            foreach (LineRenderer line in FindObjectsByType<LineRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (line == null || !line.enabled || !line.gameObject.activeInHierarchy || !IsRelevant(line.transform))
                {
                    continue;
                }

                items.Add(DescribeLineDirection(line));
                if (items.Count >= 12)
                {
                    break;
                }
            }

            return items.Count == 0 ? "no_active_relevant_line_renderers" : string.Join(" || ", items);
        }

        private string DescribeVisualState()
        {
            int activeLineRenderers = 0;
            int runtimeRays = 0;
            int manualLasers = 0;
            int nativeLineVisuals = 0;
            var visible = new List<string>();
            Transform xrOrigin = FindNamed("XR Origin") ?? FindNamed("XR Rig");
            foreach (LineRenderer line in FindObjectsByType<LineRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (line == null || !line.enabled || !line.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (xrOrigin == null || line.transform.IsChildOf(xrOrigin))
                {
                    activeLineRenderers++;
                }

                if (IsRelevant(line.transform) && visible.Count < 12)
                {
                    visible.Add(Path(line.transform));
                }
            }

            foreach (XRRayInteractor ray in FindObjectsByType<XRRayInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (ray != null && Path(ray.transform).IndexOf("Runtime UI Ray", StringComparison.OrdinalIgnoreCase) >= 0 && ray.gameObject.activeInHierarchy)
                {
                    runtimeRays++;
                }
            }

            foreach (ExperimentRuntimeManualUiLaserPointer pointer in FindObjectsByType<ExperimentRuntimeManualUiLaserPointer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (pointer != null && pointer.enabled && pointer.gameObject.activeInHierarchy)
                {
                    manualLasers++;
                }
            }

            foreach (XRInteractorLineVisual lineVisual in FindObjectsByType<XRInteractorLineVisual>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (lineVisual != null && lineVisual.enabled && lineVisual.gameObject.activeInHierarchy && !Path(lineVisual.transform).Contains("Runtime UI Ray"))
                {
                    nativeLineVisuals++;
                }
            }

            string owner = nativeLineVisuals > 0 ? "NativeXRI" : manualLasers > 0 ? "ManualLaser" : runtimeRays > 0 ? "RuntimeRay" : "none";
            return $"owner={owner};activeLineRenderersUnderXr={activeLineRenderers};nativeLineVisuals={nativeLineVisuals};manualLasers={manualLasers};runtimeRays={runtimeRays};visible={string.Join(" || ", visible)}";
        }

        private string DescribeTransform(Transform transform)
        {
            return $"path={Path(transform)};activeSelf={transform.gameObject.activeSelf};active={transform.gameObject.activeInHierarchy};layer={LayerMask.LayerToName(transform.gameObject.layer)};tag={transform.tag};pos={transform.position:F3};rot={transform.eulerAngles:F1};scale={transform.lossyScale:F3};forward={transform.forward:F3};up={transform.up:F3};right={transform.right:F3};parent={Path(transform.parent)};components={DescribeComponents(transform)};line={DescribeLine(transform)};xrray={DescribeRay(transform)};lineVisual={DescribeLineVisual(transform)}";
        }

        private static string DescribeComponents(Transform transform)
        {
            var values = new List<string>();
            foreach (Component component in transform.GetComponents<Component>())
            {
                if (component == null)
                {
                    continue;
                }

                string typeName = component.GetType().Name;
                if (typeName.IndexOf("XR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Interactor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Line", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Tracked", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("InputActionManager", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    values.Add($"{typeName}:enabled={(component is Behaviour behaviour ? behaviour.enabled : true)}");
                }
            }

            return values.Count == 0 ? "none" : string.Join(",", values);
        }

        private static string DescribeLine(Transform transform)
        {
            LineRenderer line = transform.GetComponent<LineRenderer>();
            if (line == null)
            {
                return "none";
            }

            Vector3 first = line.positionCount > 0 ? line.GetPosition(0) : Vector3.zero;
            Vector3 last = line.positionCount > 1 ? line.GetPosition(line.positionCount - 1) : Vector3.zero;
            return $"enabled={line.enabled};positions={line.positionCount};first={first:F3};last={last:F3};width={line.widthMultiplier:F4};material={(line.sharedMaterial != null ? line.sharedMaterial.name : string.Empty)}";
        }

        private static string DescribeRay(Transform transform)
        {
            XRRayInteractor ray = transform.GetComponent<XRRayInteractor>();
            if (ray == null)
            {
                return "none";
            }

            return $"enabled={ray.enabled};ui={ray.enableUIInteraction};mask={ray.raycastMask.value};layers={ray.interactionLayers.value};distance={ray.maxRaycastDistance};hit={ray.hitDetectionType}";
        }

        private static string DescribeLineVisual(Transform transform)
        {
            XRInteractorLineVisual lineVisual = transform.GetComponent<XRInteractorLineVisual>();
            return lineVisual == null ? "none" : $"enabled={lineVisual.enabled}";
        }

        private string DescribeLineDirection(LineRenderer line)
        {
            Vector3 start = line.positionCount > 0 ? line.GetPosition(0) : line.transform.position;
            Vector3 end = line.positionCount > 1 ? line.GetPosition(line.positionCount - 1) : start + line.transform.forward;
            Vector3 direction = (end - start).sqrMagnitude > 0.0001f ? (end - start).normalized : line.transform.forward;
            Vector3 canvasVector = _targetCanvas != null ? (_targetCanvas.transform.position - start).normalized : Vector3.zero;
            float canvasDot = _targetCanvas != null ? Vector3.Dot(direction, canvasVector) : 0f;
            float cameraDot = Camera.main != null ? Vector3.Dot(direction, Camera.main.transform.forward.normalized) : 0f;
            bool pointsAwayFromCanvas = _targetCanvas != null && canvasDot < -0.05f;
            Dictionary<string, object> payload = new()
            {
                ["path"] = Path(line.transform),
                ["start"] = start.ToString("F3"),
                ["end"] = end.ToString("F3"),
                ["direction"] = direction.ToString("F3"),
                ["canvas_dot"] = canvasDot,
                ["camera_dot"] = cameraDot,
                ["points_away_from_canvas"] = pointsAwayFromCanvas,
                ["ui_only"] = true,
                ["read_only"] = true
            };
            TiagoExperimentTelemetry.LogEvent("xr_ui_ray_direction_check", payload);
            Debug.Log($"{LogPrefix} xr_ui_ray_direction_check | path={payload["path"]} | canvas_dot={canvasDot:F3} | camera_dot={cameraDot:F3}", this);

            if (pointsAwayFromCanvas || cameraDot < -0.15f)
            {
                LogDirectionIssue(line, canvasDot, cameraDot);
            }

            return $"path={Path(line.transform)};start={start:F3};end={end:F3};dir={direction:F3};canvasDot={canvasDot:F3};cameraDot={cameraDot:F3};awayCanvas={pointsAwayFromCanvas}";
        }

        private void LogDirectionIssue(LineRenderer line, float canvasDot, float cameraDot)
        {
            Dictionary<string, object> payload = new()
            {
                ["path"] = Path(line.transform),
                ["canvas_dot"] = canvasDot,
                ["camera_dot"] = cameraDot,
                ["reason"] = canvasDot < -0.05f ? "points_away_from_canvas" : "points_against_camera_forward",
                ["ui_only"] = true,
                ["read_only"] = true
            };
            TiagoExperimentTelemetry.LogEvent("xr_ui_ray_direction_inverted_detected", payload);
            Debug.Log($"{LogPrefix} xr_ui_ray_direction_inverted_detected | path={payload["path"]} | canvas_dot={canvasDot:F3} | camera_dot={cameraDot:F3}", this);
        }

        private static bool IsRelevant(Transform transform)
        {
            string path = Path(transform);
            string name = transform.name;
            return path.IndexOf("XR Origin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("XR Rig", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Main Camera", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Camera Offset", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Left Controller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Right Controller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Left Hand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Right Hand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Near-Far Interactor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("Runtime UI Ray", StringComparison.OrdinalIgnoreCase) >= 0 ||
                transform.GetComponent<XRRayInteractor>() != null ||
                transform.GetComponent<XRInteractorLineVisual>() != null ||
                transform.GetComponent<LineRenderer>() != null ||
                transform.GetComponent<ExperimentRuntimeManualUiLaserPointer>() != null;
        }

        private static Transform FindNamed(string nameFragment)
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

        private static bool IsPhysicalControllerTracked(XRNode node)
        {
            XRInputDevice device = InputDevices.GetDeviceAtXRNode(node);
            return device.isValid &&
                string.Equals(ClassifyDevice(device, out _), "physical_controller", StringComparison.Ordinal) &&
                (!device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) || tracked);
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

        private static string ClassifyDevice(XRInputDevice device, out string reason)
        {
            if (!device.isValid)
            {
                reason = "invalid";
                return "invalid";
            }

            InputDeviceCharacteristics characteristics = device.characteristics;
            string name = (device.name ?? string.Empty).ToLowerInvariant();
            if ((characteristics & InputDeviceCharacteristics.HandTracking) != 0 ||
                name.Contains("hand interaction") ||
                name.Contains("palm pose") ||
                name.Contains("hand tracking") ||
                name.Contains("pinch") ||
                name.Contains("poke"))
            {
                reason = "hand_tracking_characteristic_or_name";
                return "hand_tracking";
            }

            if ((characteristics & InputDeviceCharacteristics.Controller) != 0 &&
                ((characteristics & InputDeviceCharacteristics.HeldInHand) != 0 || name.Contains("controller") || name.Contains("touch")))
            {
                reason = "controller_without_handtracking";
                return "physical_controller";
            }

            reason = "not_controller_or_hand";
            return "other";
        }

        private static string Path(Transform transform)
        {
            return transform != null ? ExperimentRuntimeUiRayInteractorBootstrap.GetPath(transform) : string.Empty;
        }

        private static string Limit(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= MaxText)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, MaxText) + "...";
        }
    }
}
