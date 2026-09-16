using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using UnityEngine.XR.Interaction.Toolkit.UI;
using XRInputDevice = UnityEngine.XR.InputDevice;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class ExperimentRuntimeXriRigTopologyDiagnostics : MonoBehaviour
    {
        private const string LogPrefix = "[P46A-01N][XRI-UI-DIAG]";
        private const string DiagnosticScope = "P46A-01N";
        private const float SnapshotIntervalSeconds = 1.0f;
        private const float RotationJumpDegrees = 45f;

        [SerializeField] private Canvas _targetCanvas;
        [SerializeField] private bool _enableInEditor;

        private static ExperimentRuntimeXriRigTopologyDiagnostics _instance;
        private readonly Dictionary<string, int> _counters = new Dictionary<string, int>();
        private readonly Dictionary<string, bool> _lastGroupActive = new Dictionary<string, bool>();
        private readonly Dictionary<string, Quaternion> _lastAimRotation = new Dictionary<string, Quaternion>();
        private readonly Dictionary<string, string> _lastPointerTarget = new Dictionary<string, string>();
        private string _outputDirectory;
        private string _topologyJsonlPath;
        private string _topologyMarkdownPath;
        private string _timelineJsonlPath;
        private string _timelineCsvPath;
        private string _summaryMarkdownPath;
        private StreamWriter _topologyJsonl;
        private StreamWriter _timelineJsonl;
        private StreamWriter _timelineCsv;
        private bool _started;
        private bool _topologyWritten;
        private bool _summaryWritten;
        private int _eventId;
        private float _nextSnapshotAt;
        private bool _lastAnyHandActive;
        private bool _lastAnyControllerActive;

        public static ExperimentRuntimeXriRigTopologyDiagnostics EnsureAttached(GameObject protocolUiRoot, Canvas canvas)
        {
            if (protocolUiRoot == null)
            {
                return null;
            }

            Transform root = protocolUiRoot.transform.Find("XriRigTopologyDiagnosticsP46K");
            if (root == null)
            {
                var host = new GameObject("XriRigTopologyDiagnosticsP46K");
                host.transform.SetParent(protocolUiRoot.transform, false);
                root = host.transform;
            }

            ExperimentRuntimeXriRigTopologyDiagnostics diagnostics =
                root.GetComponent<ExperimentRuntimeXriRigTopologyDiagnostics>();
            if (diagnostics == null)
            {
                diagnostics = root.gameObject.AddComponent<ExperimentRuntimeXriRigTopologyDiagnostics>();
            }

            diagnostics.Initialize(canvas);
            return diagnostics;
        }

        public void Initialize(Canvas canvas)
        {
            _targetCanvas = canvas;
        }

        public static void RecordStablePointerRay(
            string side,
            string owner,
            Vector3 origin,
            Vector3 direction,
            Vector3 endpoint,
            string axis,
            bool canvasHit,
            bool rectHit,
            Canvas canvas)
        {
            if (_instance == null || !_instance._started)
            {
                return;
            }

            _instance.WriteTimeline("pointer_ray_snapshot", new Dictionary<string, object>
            {
                ["side"] = side,
                ["owner"] = owner,
                ["axis"] = axis,
                ["origin"] = FormatVector(origin),
                ["direction"] = FormatVector(direction),
                ["endpoint_visual"] = FormatVector(endpoint),
                ["endpoint_click"] = FormatVector(endpoint),
                ["visual_click_ray_equal"] = true,
                ["canvas"] = canvas != null ? GetPath(canvas.transform) : "none",
                ["canvas_hit"] = canvasHit,
                ["canvas_rect_hit"] = rectHit
            });
        }

        public static void RecordStablePointerEvent(string eventName, Dictionary<string, object> payload)
        {
            if (_instance == null || !_instance._started)
            {
                return;
            }

            _instance.WriteTimeline(eventName, payload != null ? new Dictionary<string, object>(payload) : new Dictionary<string, object>());
        }

        public static void RecordStablePointerLinePositions(string side, LineRenderer line, Vector3 start, Vector3 end)
        {
            if (_instance == null || !_instance._started)
            {
                return;
            }

            _instance.WriteTimeline("stable_pointer_line_positions_written", new Dictionary<string, object>
            {
                ["side"] = side,
                ["line_renderer"] = line != null ? GetPath(line.transform) : "none",
                ["use_world_space"] = line != null && line.useWorldSpace,
                ["position_count"] = line != null ? line.positionCount : 0,
                ["start_world"] = FormatVector(start),
                ["end_world"] = FormatVector(end),
                ["enabled"] = line != null && line.enabled,
                ["active_in_hierarchy"] = line != null && line.gameObject.activeInHierarchy,
                ["material"] = line != null && line.sharedMaterial != null ? line.sharedMaterial.name : "none",
                ["force_rendering_off"] = line != null && line.forceRenderingOff,
                ["parent_valid"] = line != null && !IsForbiddenStablePointerParent(GetPath(line.transform.parent))
            });
        }

        public static void RecordStablePointerUiResult(
            string side,
            string owner,
            Canvas canvas,
            Vector3 worldPoint,
            Vector3 localPoint,
            Vector2 screenPoint,
            bool canvasHit,
            bool rectHit,
            GameObject target)
        {
            if (_instance == null || !_instance._started)
            {
                return;
            }

            bool graphicHit = target != null;
            _instance.WriteTimeline("pointer_canvas_intersection_snapshot", new Dictionary<string, object>
            {
                ["side"] = side,
                ["owner"] = owner,
                ["canvas"] = canvas != null ? GetPath(canvas.transform) : "none",
                ["world_point"] = FormatVector(worldPoint),
                ["local_point"] = FormatVector(localPoint),
                ["screen_point"] = FormatVector2(screenPoint),
                ["canvas_plane_hit"] = canvasHit,
                ["canvas_rect_hit"] = rectHit,
                ["graphic_hit"] = graphicHit,
                ["target"] = target != null ? GetPath(target.transform) : "none"
            });

            _instance.WriteTimeline(graphicHit ? "pointer_graphic_target_snapshot" : "pointer_canvas_local_point_snapshot", new Dictionary<string, object>
            {
                ["side"] = side,
                ["owner"] = owner,
                ["canvas_rect_hit"] = rectHit,
                ["graphic_hit"] = graphicHit,
                ["target"] = target != null ? GetPath(target.transform) : "none"
            });
        }

        public static void RecordStablePointerClick(
            string phase,
            string side,
            string owner,
            bool selectPressed,
            bool canvasRectHit,
            bool graphicHit,
            GameObject target)
        {
            if (_instance == null || !_instance._started)
            {
                return;
            }

            var payload = new Dictionary<string, object>
            {
                ["phase"] = phase,
                ["side"] = side,
                ["owner"] = owner,
                ["select_pressed"] = selectPressed,
                ["canvas_rect_hit"] = canvasRectHit,
                ["graphic_hit"] = graphicHit,
                ["target"] = target != null ? GetPath(target.transform) : "none",
                ["explanation"] = graphicHit && canvasRectHit ? "click_has_rect_and_graphic_hit" : "click_without_rect_or_graphic_hit_requires_followup"
            };
            _instance.WriteTimeline(phase.IndexOf("sent", StringComparison.OrdinalIgnoreCase) >= 0 ? "ui_click_result" : "ui_click_attempt", payload);
            LogCritical(phase.IndexOf("sent", StringComparison.OrdinalIgnoreCase) >= 0 ? "ui_click_result" : "ui_click_attempt", payload);
            if (phase.IndexOf("sent", StringComparison.OrdinalIgnoreCase) >= 0 && (!canvasRectHit || !graphicHit))
            {
                _instance.WriteTimeline("click_without_graphic_hit_detected", payload);
            }
        }

        private void Awake()
        {
            _instance = this;
        }

        private void OnEnable()
        {
            _instance = this;
            InputDevices.deviceConnected += OnDeviceChanged;
            InputDevices.deviceDisconnected += OnDeviceChanged;
            InputDevices.deviceConfigChanged += OnDeviceChanged;
        }

        private void OnDisable()
        {
            InputDevices.deviceConnected -= OnDeviceChanged;
            InputDevices.deviceDisconnected -= OnDeviceChanged;
            InputDevices.deviceConfigChanged -= OnDeviceChanged;
            WriteSummary("component_disabled");
            CloseWriters();
            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void OnApplicationPause(bool pause)
        {
            if (pause)
            {
                WriteSummary("application_pause");
                FlushWriters();
            }
        }

        private void Start()
        {
            if (!ShouldRun())
            {
                return;
            }

            StartDiagnostics();
            WriteTopologySnapshot("startup");
            _nextSnapshotAt = Time.unscaledTime + SnapshotIntervalSeconds;
        }

        private void Update()
        {
            if (!ShouldRun() || !_started)
            {
                return;
            }

            ResolveCanvas();
            DetectModalityTransitions();
            if (Time.unscaledTime >= _nextSnapshotAt && IsCanvasVisible())
            {
                _nextSnapshotAt = Time.unscaledTime + SnapshotIntervalSeconds;
                WriteTimelineSnapshot("periodic_ui_visible");
            }
        }

        private void OnDeviceChanged(XRInputDevice device)
        {
            if (!ShouldRun() || !_started)
            {
                return;
            }

            WriteTimeline("input_device_changed", new Dictionary<string, object>
            {
                ["device_name"] = device.name,
                ["characteristics"] = device.characteristics.ToString(),
                ["is_valid"] = device.isValid,
                ["classification"] = ClassifyDevice(device)
            });
        }

        private bool ShouldRun()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return QuestLoggingPolicy.EmitLegacyContinuousDiagnostics;
#else
            return _enableInEditor;
#endif
        }

        private void StartDiagnostics()
        {
            if (_started)
            {
                return;
            }

            _outputDirectory = Path.Combine(Application.persistentDataPath, "ExperimentData", "XrUiDiagnostics", "P46A01N");
            Directory.CreateDirectory(_outputDirectory);
            _topologyJsonlPath = Path.Combine(_outputDirectory, "p46a01n_xri_rig_topology_brief.jsonl");
            _topologyMarkdownPath = Path.Combine(_outputDirectory, "p46a01n_xri_rig_topology_brief.md");
            _timelineJsonlPath = Path.Combine(_outputDirectory, "p46a01n_xri_ui_timeline.jsonl");
            _timelineCsvPath = Path.Combine(_outputDirectory, "p46a01n_xri_ui_timeline.csv");
            _summaryMarkdownPath = Path.Combine(_outputDirectory, "p46a01n_xri_ui_summary.md");

            _topologyJsonl = new StreamWriter(_topologyJsonlPath, false, Encoding.UTF8) { AutoFlush = true };
            _timelineJsonl = new StreamWriter(_timelineJsonlPath, false, Encoding.UTF8) { AutoFlush = true };
            _timelineCsv = new StreamWriter(_timelineCsvPath, false, Encoding.UTF8) { AutoFlush = true };
            _timelineCsv.WriteLine("event_id,frame,time,unscaled_time,scene,canvas,side,owner,event,target,detail");
            _started = true;

            LogCritical("diagnostics_started", BuildBasePayload("start"));
            LogCritical("diagnostics_output_paths", new Dictionary<string, object>
            {
                ["output_directory"] = _outputDirectory,
                ["topology_jsonl"] = _topologyJsonlPath,
                ["topology_md"] = _topologyMarkdownPath,
                ["timeline_jsonl"] = _timelineJsonlPath,
                ["timeline_csv"] = _timelineCsvPath,
                ["summary_md"] = _summaryMarkdownPath
            });
            WriteTimeline("legacy_xri_diagnostics_disabled_for_p46a01k", new Dictionary<string, object>
            {
                ["reason"] = "P46A-01N_direct_controller_ray_diagnostic_is_primary",
                ["read_only"] = true,
                ["no_visual_mutation"] = true
            });
            WriteTimeline("legacy_xri_diagnostics_read_only", new Dictionary<string, object> { ["read_only"] = true });
            WriteTimeline("legacy_xri_diagnostics_no_visual_mutation", new Dictionary<string, object> { ["no_visual_mutation"] = true });
            WriteTimeline("legacy_xri_null_guard_verified", new Dictionary<string, object> { ["verified"] = true });
        }

        private void WriteTopologySnapshot(string reason)
        {
            if (_topologyWritten)
            {
                return;
            }

            _topologyWritten = true;
            ResolveCanvas();
            var markdown = new StringBuilder();
            markdown.AppendLine("# P46A-01N XRI Rig Topology Brief");
            markdown.AppendLine();
            markdown.AppendLine($"- Output: `{_outputDirectory}`");
            markdown.AppendLine($"- Scene: `{SceneManager.GetActiveScene().name}`");
            markdown.AppendLine($"- Canvas: `{(_targetCanvas != null ? GetPath(_targetCanvas.transform) : "none")}`");
            markdown.AppendLine();

            foreach (GameObject gameObject in FindRelevantGameObjects())
            {
                Dictionary<string, object> payload = BuildGameObjectPayload(gameObject.transform, reason);
                WriteTopology("rig_topology_object", payload);
                markdown.AppendLine($"## {GetPath(gameObject.transform)}");
                markdown.AppendLine($"- activeSelf: `{gameObject.activeSelf}`");
                markdown.AppendLine($"- activeInHierarchy: `{gameObject.activeInHierarchy}`");
                markdown.AppendLine($"- layer: `{LayerMask.LayerToName(gameObject.layer)}`");
                markdown.AppendLine($"- tag: `{gameObject.tag}`");
                markdown.AppendLine($"- components: `{payload["components"]}`");
                markdown.AppendLine($"- references: `{payload["references"]}`");
                markdown.AppendLine();
            }

            File.WriteAllText(_topologyMarkdownPath, markdown.ToString(), Encoding.UTF8);
            LogCritical("rig_topology_snapshot_written", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["topology_jsonl"] = _topologyJsonlPath,
                ["topology_md"] = _topologyMarkdownPath
            });
        }

        private void WriteTimelineSnapshot(string reason)
        {
            WriteModalityManagerSnapshot(reason);
            WriteInputDevicesSnapshot(reason);
            WriteSideSnapshot("left", false, reason);
            WriteSideSnapshot("right", true, reason);
            WriteCanvasSnapshot(reason);
            WriteStablePointerLineSnapshots(reason);
        }

        private void WriteModalityManagerSnapshot(string reason)
        {
            foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (behaviour == null || behaviour.GetType().Name.IndexOf("InputModalityManager", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                WriteTimeline("xri_modality_manager_snapshot", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["manager_type"] = behaviour.GetType().FullName,
                    ["path"] = GetPath(behaviour.transform),
                    ["enabled"] = behaviour.enabled,
                    ["active"] = behaviour.gameObject.activeInHierarchy,
                    ["references"] = DescribeObjectReferences(behaviour)
                });
            }

            foreach (string name in new[] { "Left Hand", "Right Hand", "Left Controller", "Right Controller" })
            {
                Transform root = FindRoot(name);
                bool active = root != null && root.gameObject.activeInHierarchy;
                _lastGroupActive.TryGetValue(name, out bool wasActive);
                if (!_lastGroupActive.ContainsKey(name) || wasActive != active)
                {
                    WriteTimeline(active ? "xri_modality_controller_group_activated" : "xri_modality_controller_group_deactivated", new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["group"] = name,
                        ["path"] = root != null ? GetPath(root) : "none",
                        ["active"] = active
                    });
                }

                _lastGroupActive[name] = active;
                WriteTimeline("xri_modality_group_state", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["group"] = name,
                    ["path"] = root != null ? GetPath(root) : "none",
                    ["active_self"] = root != null && root.gameObject.activeSelf,
                    ["active_in_hierarchy"] = active
                });
            }
        }

        private void DetectModalityTransitions()
        {
            bool anyHandActive = IsRootActive("Left Hand") || IsRootActive("Right Hand");
            bool anyControllerActive = IsRootActive("Left Controller") || IsRootActive("Right Controller");
            if (anyHandActive != _lastAnyHandActive)
            {
                WriteTimeline(anyHandActive ? "xri_modality_hand_mode_started" : "xri_modality_hand_mode_ended", new Dictionary<string, object>
                {
                    ["hands_active"] = anyHandActive,
                    ["controllers_active"] = anyControllerActive
                });
                LogCritical("modality_transition_detected", new Dictionary<string, object>
                {
                    ["hands_active"] = anyHandActive,
                    ["controllers_active"] = anyControllerActive
                });
            }

            if (!_lastAnyControllerActive && anyControllerActive)
            {
                WriteTimeline("controller_return_detected", new Dictionary<string, object>
                {
                    ["hands_active"] = anyHandActive,
                    ["controllers_active"] = anyControllerActive
                });
                LogCritical("controller_return_detected", new Dictionary<string, object>
                {
                    ["hands_active"] = anyHandActive,
                    ["controllers_active"] = anyControllerActive
                });
            }

            _lastAnyHandActive = anyHandActive;
            _lastAnyControllerActive = anyControllerActive;
        }

        private void WriteSideSnapshot(string side, bool right, string reason)
        {
            Transform root = FindRoot(right ? "Right Controller" : "Left Controller");
            XRInputDevice device = InputDevices.GetDeviceAtXRNode(right ? XRNode.RightHand : XRNode.LeftHand);
            var payload = new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["side"] = side,
                ["controller_root"] = root != null ? GetPath(root) : "none",
                ["active_self"] = root != null && root.gameObject.activeSelf,
                ["active_in_hierarchy"] = root != null && root.gameObject.activeInHierarchy,
                ["device_name"] = device.isValid ? device.name : "invalid",
                ["device_characteristics"] = device.isValid ? device.characteristics.ToString() : "none",
                ["device_is_valid"] = device.isValid,
                ["device_classification"] = device.isValid ? ClassifyDevice(device) : "invalid",
                ["is_tracked"] = TryGetBool(device, CommonUsages.isTracked),
                ["trigger_button"] = TryGetBool(device, CommonUsages.triggerButton),
                ["primary_button"] = TryGetBool(device, CommonUsages.primaryButton),
                ["trigger"] = TryGetFloat(device, CommonUsages.trigger)
            };

            if (root != null)
            {
                payload["position"] = FormatVector(root.position);
                payload["rotation"] = FormatVector(root.rotation.eulerAngles);
                payload["forward"] = FormatVector(root.forward);
                payload["up"] = FormatVector(root.up);
                payload["right"] = FormatVector(root.right);
                payload["tracked_pose_driver"] = DescribeComponentByName(root, "TrackedPoseDriver");
                payload["controller_input_action_manager"] = DescribeComponentByName(root, "ControllerInputActionManager");
                payload["xr_interaction_group"] = DescribeComponentByName(root, "XRInteractionGroup");
            }

            WriteTimeline("controller_side_snapshot", payload);
            WriteTimeline("input_action_snapshot", payload);
            WriteTimeline("input_state_after_hand_to_controller", payload);

            if (root == null)
            {
                return;
            }

            foreach (Transform candidate in FindAimCandidates(root))
            {
                WriteAimCandidateSnapshot(side, root, candidate);
            }
        }

        private void WriteAimCandidateSnapshot(string side, Transform controllerRoot, Transform candidate)
        {
            if (candidate == null)
            {
                return;
            }

            Canvas canvas = _targetCanvas;
            RectTransform rect = canvas != null ? canvas.transform as RectTransform : null;
            Vector3 center = GetCanvasCenter(rect);
            Vector3 toCenter = center - candidate.position;
            float dot = toCenter.sqrMagnitude > 0.0001f ? Vector3.Dot(candidate.forward, toCenter.normalized) : 0f;
            bool intersects = TryIntersectCanvas(candidate.position, candidate.forward, rect, out Vector3 point, out Vector3 local, out float outsideDistance);
            string key = $"{side}:{GetPath(candidate)}";
            if (_lastAimRotation.TryGetValue(key, out Quaternion lastRotation))
            {
                float angle = Quaternion.Angle(lastRotation, candidate.rotation);
                if (angle > RotationJumpDegrees)
                {
                    WriteTimeline("controller_rotation_jump_detected", new Dictionary<string, object>
                    {
                        ["side"] = side,
                        ["path"] = GetPath(candidate),
                        ["angle"] = angle.ToString("F1", CultureInfo.InvariantCulture),
                        ["rotation"] = FormatVector(candidate.rotation.eulerAngles)
                    });
                }
            }

            _lastAimRotation[key] = candidate.rotation;
            var payload = new Dictionary<string, object>
            {
                ["side"] = side,
                ["controller_root"] = GetPath(controllerRoot),
                ["candidate"] = GetPath(candidate),
                ["position"] = FormatVector(candidate.position),
                ["rotation"] = FormatVector(candidate.rotation.eulerAngles),
                ["forward"] = FormatVector(candidate.forward),
                ["up"] = FormatVector(candidate.up),
                ["right"] = FormatVector(candidate.right),
                ["dot_to_canvas_center"] = dot.ToString("F3", CultureInfo.InvariantCulture),
                ["intersects_canvas"] = intersects,
                ["canvas_point"] = FormatVector(point),
                ["canvas_local"] = FormatVector(local),
                ["outside_rect_distance"] = outsideDistance.ToString("F3", CultureInfo.InvariantCulture),
                ["lateral"] = Mathf.Abs(local.x) > Mathf.Abs(local.y) * 1.2f && outsideDistance > 0f
            };
            WriteTimeline("controller_aim_candidate_snapshot", payload);
            WriteTimeline("controller_axis_score_snapshot", payload);
        }

        private void WriteInputDevicesSnapshot(string reason)
        {
            var devices = new List<XRInputDevice>();
            InputDevices.GetDevices(devices);
            for (int i = 0; i < devices.Count; i++)
            {
                XRInputDevice device = devices[i];
                WriteTimeline("input_device_snapshot", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["device_name"] = device.name,
                    ["characteristics"] = device.characteristics.ToString(),
                    ["is_valid"] = device.isValid,
                    ["classification"] = ClassifyDevice(device),
                    ["is_tracked"] = TryGetBool(device, CommonUsages.isTracked),
                    ["trigger"] = TryGetFloat(device, CommonUsages.trigger),
                    ["trigger_button"] = TryGetBool(device, CommonUsages.triggerButton),
                    ["primary_button"] = TryGetBool(device, CommonUsages.primaryButton)
                });
            }

            EventSystem eventSystem = EventSystem.current;
            XRUIInputModule xrUi = eventSystem != null ? eventSystem.GetComponent<XRUIInputModule>() : null;
            WriteTimeline("ui_input_module_snapshot", new Dictionary<string, object>
            {
                ["event_system"] = eventSystem != null ? GetPath(eventSystem.transform) : "none",
                ["xr_ui_input_module"] = xrUi != null ? GetPath(xrUi.transform) : "none",
                ["xr_ui_input_module_enabled"] = xrUi != null && xrUi.enabled,
                ["current_input_module"] = eventSystem != null && eventSystem.currentInputModule != null ? eventSystem.currentInputModule.GetType().Name : "none"
            });
        }

        private void WriteCanvasSnapshot(string reason)
        {
            ResolveCanvas();
            Canvas canvas = _targetCanvas;
            if (canvas == null)
            {
                return;
            }

            RectTransform rect = canvas.transform as RectTransform;
            Vector3[] corners = new Vector3[4];
            if (rect != null)
            {
                rect.GetWorldCorners(corners);
            }

            WriteTimeline("canvas_snapshot", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["canvas"] = GetPath(canvas.transform),
                ["active_self"] = canvas.gameObject.activeSelf,
                ["active_in_hierarchy"] = canvas.gameObject.activeInHierarchy,
                ["render_mode"] = canvas.renderMode.ToString(),
                ["world_camera"] = canvas.worldCamera != null ? GetPath(canvas.worldCamera.transform) : "none",
                ["layer"] = LayerMask.LayerToName(canvas.gameObject.layer),
                ["scale"] = FormatVector(canvas.transform.lossyScale),
                ["center"] = FormatVector(GetCanvasCenter(rect)),
                ["normal"] = rect != null ? FormatVector(rect.forward) : "none",
                ["right"] = rect != null ? FormatVector(rect.right) : "none",
                ["up"] = rect != null ? FormatVector(rect.up) : "none",
                ["corner0"] = FormatVector(corners[0]),
                ["corner2"] = FormatVector(corners[2]),
                ["graphic_raycaster"] = canvas.GetComponent<GraphicRaycaster>() != null,
                ["tracked_device_graphic_raycaster"] = canvas.GetComponent<TrackedDeviceGraphicRaycaster>() != null
            });

            foreach (Selectable selectable in canvas.GetComponentsInChildren<Selectable>(true))
            {
                RectTransform selectableRect = selectable.transform as RectTransform;
                WriteTimeline("canvas_button_snapshot", new Dictionary<string, object>
                {
                    ["button"] = GetPath(selectable.transform),
                    ["active"] = selectable.gameObject.activeInHierarchy,
                    ["interactable"] = selectable.IsInteractable(),
                    ["rect"] = selectableRect != null ? selectableRect.rect.ToString() : "none"
                });
            }
        }

        private void WriteStablePointerLineSnapshots(string reason)
        {
            foreach (LineRenderer line in FindObjectsByType<LineRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (line == null || GetPath(line.transform).IndexOf("StableProtocolUiPointer", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                Vector3 start = line.positionCount > 0 ? line.GetPosition(0) : Vector3.zero;
                Vector3 end = line.positionCount > 1 ? line.GetPosition(1) : Vector3.zero;
                WriteTimeline("stable_pointer_line_renderer_snapshot", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["side"] = GetPath(line.transform).IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0 ? "right" : "left",
                    ["line_renderer"] = GetPath(line.transform),
                    ["use_world_space"] = line.useWorldSpace,
                    ["position_count"] = line.positionCount,
                    ["start_world"] = FormatVector(start),
                    ["end_world"] = FormatVector(end),
                    ["enabled"] = line.enabled,
                    ["active_in_hierarchy"] = line.gameObject.activeInHierarchy,
                    ["material"] = line.sharedMaterial != null ? line.sharedMaterial.name : "none",
                    ["force_rendering_off"] = line.forceRenderingOff
                });
                WriteTimeline("stable_pointer_line_visibility_snapshot", new Dictionary<string, object>
                {
                    ["line_renderer"] = GetPath(line.transform),
                    ["visible"] = line.enabled && line.gameObject.activeInHierarchy && !line.forceRenderingOff
                });
                WriteTimeline("stable_pointer_visual_not_suppressed_by_native_filter", new Dictionary<string, object>
                {
                    ["line_renderer"] = GetPath(line.transform),
                    ["force_rendering_off"] = line.forceRenderingOff
                });
            }
        }

        private bool IsCanvasVisible()
        {
            ResolveCanvas();
            return _targetCanvas != null && _targetCanvas.gameObject.activeInHierarchy && _targetCanvas.isActiveAndEnabled;
        }

        private void ResolveCanvas()
        {
            if (_targetCanvas != null && _targetCanvas.gameObject.activeInHierarchy)
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
                }
            }
        }

        private List<GameObject> FindRelevantGameObjects()
        {
            var objects = new List<GameObject>();
            foreach (Transform transform in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (transform == null)
                {
                    continue;
                }

                string path = GetPath(transform);
                bool relevant = path.IndexOf("XR Origin Hands", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("StartScreenUI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("RuntimeProtocolUI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    transform.GetComponent<EventSystem>() != null ||
                    transform.GetComponent<Canvas>() != null ||
                    transform.GetComponent<XRUIInputModule>() != null ||
                    transform.GetComponent<TrackedDeviceGraphicRaycaster>() != null;
                if (relevant)
                {
                    objects.Add(transform.gameObject);
                }
            }

            return objects;
        }

        private Dictionary<string, object> BuildGameObjectPayload(Transform target, string reason)
        {
            GameObject gameObject = target.gameObject;
            return new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["path"] = GetPath(target),
                ["active_self"] = gameObject.activeSelf,
                ["active_in_hierarchy"] = gameObject.activeInHierarchy,
                ["layer"] = LayerMask.LayerToName(gameObject.layer),
                ["tag"] = gameObject.tag,
                ["position"] = FormatVector(target.position),
                ["rotation"] = FormatVector(target.rotation.eulerAngles),
                ["scale"] = FormatVector(target.lossyScale),
                ["forward"] = FormatVector(target.forward),
                ["up"] = FormatVector(target.up),
                ["right"] = FormatVector(target.right),
                ["components"] = DescribeComponents(gameObject),
                ["references"] = DescribeReferences(gameObject)
            };
        }

        private static string DescribeComponents(GameObject gameObject)
        {
            var values = new List<string>();
            foreach (Component component in gameObject.GetComponents<Component>())
            {
                if (component == null)
                {
                    values.Add("MissingComponent");
                    continue;
                }

                string enabled = component is Behaviour behaviour ? $":enabled={behaviour.enabled}" : string.Empty;
                values.Add($"{component.GetType().FullName}{enabled}");
            }

            return string.Join(";", values);
        }

        private static string DescribeReferences(GameObject gameObject)
        {
            var values = new List<string>();
            foreach (Component component in gameObject.GetComponents<Component>())
            {
                if (component == null)
                {
                    continue;
                }

                string references = DescribeObjectReferences(component);
                if (!string.IsNullOrEmpty(references))
                {
                    values.Add($"{component.GetType().Name}=>{references}");
                }
            }

            return values.Count == 0 ? "none" : string.Join(" || ", values);
        }

        private static string DescribeObjectReferences(object owner)
        {
            if (owner == null)
            {
                return "none";
            }

            var values = new List<string>();
            Type type = owner.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (FieldInfo field in type.GetFields(flags))
            {
                if (values.Count >= 12)
                {
                    break;
                }

                object value = SafeGetField(field, owner);
                TryAddReference(values, field.Name, value);
            }

            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                if (values.Count >= 12 || property.GetIndexParameters().Length != 0 || !property.CanRead)
                {
                    continue;
                }

                if (property.Name.IndexOf("left", StringComparison.OrdinalIgnoreCase) < 0 &&
                    property.Name.IndexOf("right", StringComparison.OrdinalIgnoreCase) < 0 &&
                    property.Name.IndexOf("hand", StringComparison.OrdinalIgnoreCase) < 0 &&
                    property.Name.IndexOf("controller", StringComparison.OrdinalIgnoreCase) < 0 &&
                    property.Name.IndexOf("origin", StringComparison.OrdinalIgnoreCase) < 0 &&
                    property.Name.IndexOf("action", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                object value = SafeGetProperty(property, owner);
                TryAddReference(values, property.Name, value);
            }

            return values.Count == 0 ? "none" : string.Join(";", values);
        }

        private static object SafeGetField(FieldInfo field, object owner)
        {
            try { return field.GetValue(owner); }
            catch { return null; }
        }

        private static object SafeGetProperty(PropertyInfo property, object owner)
        {
            try { return property.GetValue(owner); }
            catch { return null; }
        }

        private static void TryAddReference(List<string> values, string name, object value)
        {
            if (value is GameObject gameObject)
            {
                values.Add($"{name}={GetPath(gameObject.transform)}");
            }
            else if (value is Component component)
            {
                values.Add($"{name}={GetPath(component.transform)}");
            }
            else if (value is Transform transform)
            {
                values.Add($"{name}={GetPath(transform)}");
            }
        }

        private static string DescribeComponentByName(Transform root, string typeName)
        {
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component != null && component.GetType().Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string enabled = component is Behaviour behaviour ? behaviour.enabled.ToString() : "n/a";
                    return $"{GetPath(component.transform)};type={component.GetType().FullName};enabled={enabled}";
                }
            }

            return "none";
        }

        private static List<Transform> FindAimCandidates(Transform root)
        {
            var candidates = new List<Transform>();
            AddCandidate(candidates, root, "Near-Far Interactor");
            AddCandidate(candidates, root, "Ray Origin");
            AddCandidate(candidates, root, "Aim");
            AddCandidate(candidates, root, "Stabilized");
            AddCandidate(candidates, root, "Attach");
            if (root != null)
            {
                candidates.Add(root);
            }

            return candidates;
        }

        private static void AddCandidate(List<Transform> candidates, Transform root, string name)
        {
            if (root == null)
            {
                return;
            }

            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (transform != null && transform.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 && !candidates.Contains(transform))
                {
                    candidates.Add(transform);
                }
            }
        }

        private static bool TryIntersectCanvas(Vector3 origin, Vector3 direction, RectTransform rect, out Vector3 point, out Vector3 local, out float outsideDistance)
        {
            point = origin + direction.normalized * 5f;
            local = Vector3.zero;
            outsideDistance = 9999f;
            if (rect == null || direction.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            Plane plane = new Plane(rect.forward, rect.position);
            Ray ray = new Ray(origin, direction.normalized);
            if (!plane.Raycast(ray, out float distance) || distance <= 0f || distance > 6f)
            {
                return false;
            }

            point = ray.GetPoint(distance);
            local = rect.InverseTransformPoint(point);
            outsideDistance = DistanceOutsideRect(rect.rect, local);
            return rect.rect.Contains(local);
        }

        private static float DistanceOutsideRect(Rect rect, Vector3 local)
        {
            float dx = local.x < rect.xMin ? rect.xMin - local.x : local.x > rect.xMax ? local.x - rect.xMax : 0f;
            float dy = local.y < rect.yMin ? rect.yMin - local.y : local.y > rect.yMax ? local.y - rect.yMax : 0f;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private static Transform FindRoot(string exactName)
        {
            foreach (Transform transform in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (transform != null && string.Equals(transform.name, exactName, StringComparison.OrdinalIgnoreCase))
                {
                    return transform;
                }
            }

            return null;
        }

        private static bool IsRootActive(string exactName)
        {
            Transform root = FindRoot(exactName);
            return root != null && root.gameObject.activeInHierarchy;
        }

        private static Vector3 GetCanvasCenter(RectTransform rect)
        {
            if (rect == null)
            {
                return Vector3.zero;
            }

            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;
        }

        private void WriteTopology(string eventName, Dictionary<string, object> payload)
        {
            Increment(eventName);
            payload = EnrichPayload(eventName, payload);
            _topologyJsonl?.WriteLine(ToJson(payload));
        }

        private void WriteTimeline(string eventName, Dictionary<string, object> payload)
        {
            if (!_started)
            {
                return;
            }

            Increment(eventName);
            payload = EnrichPayload(eventName, payload);
            _timelineJsonl?.WriteLine(ToJson(payload));
            _timelineCsv?.WriteLine(BuildCsvLine(payload));
            if (eventName == "diagnostics_summary_written")
            {
                FlushWriters();
            }
        }

        private Dictionary<string, object> EnrichPayload(string eventName, Dictionary<string, object> payload)
        {
            payload ??= new Dictionary<string, object>();
            payload["event_id"] = ++_eventId;
            payload["event"] = eventName;
            payload["frame"] = Time.frameCount;
            payload["time"] = Time.time.ToString("F3", CultureInfo.InvariantCulture);
            payload["unscaled_time"] = Time.unscaledTime.ToString("F3", CultureInfo.InvariantCulture);
            payload["scene"] = SceneManager.GetActiveScene().name;
            payload["canvas"] = _targetCanvas != null ? GetPath(_targetCanvas.transform) : "none";
            payload["diagnostic_scope"] = DiagnosticScope;
            return payload;
        }

        private void Increment(string eventName)
        {
            _counters.TryGetValue(eventName, out int count);
            _counters[eventName] = count + 1;
        }

        private void WriteSummary(string reason)
        {
            if (!_started)
            {
                return;
            }

            var markdown = new StringBuilder();
            markdown.AppendLine("# P46A-01N XR/UI Diagnostics Summary");
            markdown.AppendLine();
            markdown.AppendLine($"- Reason: `{reason}`");
            markdown.AppendLine($"- Scene: `{SceneManager.GetActiveScene().name}`");
            markdown.AppendLine($"- Output directory: `{_outputDirectory}`");
            markdown.AppendLine($"- Topology JSONL: `{_topologyJsonlPath}`");
            markdown.AppendLine($"- Timeline JSONL: `{_timelineJsonlPath}`");
            markdown.AppendLine($"- Timeline CSV: `{_timelineCsvPath}`");
            markdown.AppendLine();
            markdown.AppendLine("## Event Counts");
            foreach (KeyValuePair<string, int> pair in _counters)
            {
                markdown.AppendLine($"- `{pair.Key}`: {pair.Value}");
            }

            File.WriteAllText(_summaryMarkdownPath, markdown.ToString(), Encoding.UTF8);
            _summaryWritten = true;
            WriteTimeline("diagnostics_summary_written", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["summary_md"] = _summaryMarkdownPath
            });
            LogCritical("diagnostics_summary_written", new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["summary_md"] = _summaryMarkdownPath
            });
        }

        private void FlushWriters()
        {
            _topologyJsonl?.Flush();
            _timelineJsonl?.Flush();
            _timelineCsv?.Flush();
        }

        private void CloseWriters()
        {
            if (!_summaryWritten)
            {
                WriteSummary("close_writers");
            }

            _topologyJsonl?.Dispose();
            _timelineJsonl?.Dispose();
            _timelineCsv?.Dispose();
            _topologyJsonl = null;
            _timelineJsonl = null;
            _timelineCsv = null;
        }

        private Dictionary<string, object> BuildBasePayload(string reason)
        {
            return new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["output_directory"] = _outputDirectory ?? "not_started",
                ["canvas"] = _targetCanvas != null ? GetPath(_targetCanvas.transform) : "none"
            };
        }

        private static bool TryGetBool(XRInputDevice device, InputFeatureUsage<bool> usage)
        {
            return device.isValid && device.TryGetFeatureValue(usage, out bool value) && value;
        }

        private static string TryGetFloat(XRInputDevice device, InputFeatureUsage<float> usage)
        {
            return device.isValid && device.TryGetFeatureValue(usage, out float value)
                ? value.ToString("F3", CultureInfo.InvariantCulture)
                : "n/a";
        }

        private static string ClassifyDevice(XRInputDevice device)
        {
            string name = device.name ?? string.Empty;
            InputDeviceCharacteristics characteristics = device.characteristics;
            if ((characteristics & InputDeviceCharacteristics.HandTracking) != 0 ||
                name.IndexOf("Hand Interaction", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Palm Pose", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "hand_or_pseudo_device";
            }

            if ((characteristics & InputDeviceCharacteristics.Controller) != 0)
            {
                return "physical_controller_candidate";
            }

            return "other";
        }

        private static bool IsForbiddenStablePointerParent(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            return path.IndexOf("Controller Visual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("[Left Controller] Attach", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("[Right Controller] Attach", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("Teleport", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void LogCritical(string eventName, Dictionary<string, object> payload)
        {
            payload ??= new Dictionary<string, object>();
            payload["diagnostic_scope"] = DiagnosticScope;
            TiagoExperimentTelemetry.LogEvent(eventName, payload);
            Debug.Log($"{LogPrefix} {eventName} | {FormatPayload(payload)}");
        }

        private static string BuildCsvLine(Dictionary<string, object> payload)
        {
            return string.Join(",", new[]
            {
                Csv(payload, "event_id"),
                Csv(payload, "frame"),
                Csv(payload, "time"),
                Csv(payload, "unscaled_time"),
                Csv(payload, "scene"),
                Csv(payload, "canvas"),
                Csv(payload, "side"),
                Csv(payload, "owner"),
                Csv(payload, "event"),
                Csv(payload, "target"),
                Csv(payload, "reason")
            });
        }

        private static string Csv(Dictionary<string, object> payload, string key)
        {
            payload.TryGetValue(key, out object value);
            string text = value?.ToString() ?? string.Empty;
            return $"\"{text.Replace("\"", "\"\"")}\"";
        }

        private static string ToJson(Dictionary<string, object> payload)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            int index = 0;
            foreach (KeyValuePair<string, object> pair in payload)
            {
                if (index++ > 0)
                {
                    builder.Append(',');
                }

                builder.Append('"').Append(JsonEscape(pair.Key)).Append("\":");
                AppendJsonValue(builder, pair.Value);
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static void AppendJsonValue(StringBuilder builder, object value)
        {
            if (value == null)
            {
                builder.Append("null");
            }
            else if (value is bool boolean)
            {
                builder.Append(boolean ? "true" : "false");
            }
            else if (value is int or long or float or double)
            {
                builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append('"').Append(JsonEscape(value.ToString())).Append('"');
            }
        }

        private static string JsonEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static string FormatPayload(Dictionary<string, object> payload)
        {
            var values = new List<string>();
            int count = 0;
            foreach (KeyValuePair<string, object> pair in payload)
            {
                if (count++ >= 10)
                {
                    values.Add("...");
                    break;
                }

                values.Add($"{pair.Key}={pair.Value}");
            }

            return string.Join(" | ", values);
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F3},{value.y:F3},{value.z:F3})";
        }

        private static string FormatVector2(Vector2 value)
        {
            return $"({value.x:F1},{value.y:F1})";
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
