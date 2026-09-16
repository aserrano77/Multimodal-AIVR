using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class ExperimentUiRaycastHitMarker : MonoBehaviour
    {
        private const string LogPrefix = "[P46I-02][UI-HIT-MARKER]";
        private const string HostName = "P46IUiHitMarkers";
        private const int DiscSegments = 32;
        private const float RebindIntervalSeconds = 1.0f;
        private const float VisibleChangeLogMinIntervalSeconds = 0.5f;

        [SerializeField] private float _radiusMeters = 0.012f;
        [SerializeField] private float _surfaceOffsetMeters = 0.003f;
        [SerializeField] private Color _markerColor = new(0.82f, 0.96f, 1f, 1f);

        private readonly List<MarkerSide> _sides = new();
        private readonly HashSet<string> _discardedProviderLogs = new(StringComparer.Ordinal);
        private Material _material;
        private Mesh _discMesh;
        private float _nextRebindAt;
        private string _lastBoundSignature = string.Empty;
        private bool _initializedLogged;
        private bool _forbiddenPathLogged;

        public static ExperimentUiRaycastHitMarker EnsureAttached(GameObject requestedRoot)
        {
            GameObject hostObject = GameObject.Find(HostName);
            if (hostObject == null)
            {
                hostObject = new GameObject(HostName);
                SetIgnoreRaycastLayer(hostObject);
            }

            ExperimentUiRaycastHitMarker marker = hostObject.GetComponent<ExperimentUiRaycastHitMarker>();
            if (marker == null)
            {
                marker = hostObject.AddComponent<ExperimentUiRaycastHitMarker>();
            }

            marker.LogHostBound(requestedRoot);
            return marker;
        }

        private void Awake()
        {
            SetIgnoreRaycastLayer(gameObject);
            EnsureResources();
            LogInitializedOnce("awake");
            LogForbiddenPathIfNeeded("awake");
        }

        private void OnEnable()
        {
            RebindProviders("enabled");
        }

        private void OnDisable()
        {
            SetAllVisible(false, "component_disabled");
        }

        private void OnDestroy()
        {
            if (_material != null)
            {
                Destroy(_material);
            }

            if (_discMesh != null)
            {
                Destroy(_discMesh);
            }
        }

        private void LateUpdate()
        {
            EnsureResources();
            LogForbiddenPathIfNeeded("late_update");
            if (Time.unscaledTime >= _nextRebindAt)
            {
                RebindProviders("periodic");
            }

            for (int i = 0; i < _sides.Count; i++)
            {
                UpdateMarker(_sides[i]);
            }
        }

        private void RebindProviders(string reason)
        {
            _nextRebindAt = Time.unscaledTime + RebindIntervalSeconds;
            var activeProviders = new List<Component>();
            BindXrRayInteractors(activeProviders);
            BindNearFarInteractors(activeProviders);

            _sides.RemoveAll(side =>
                side == null ||
                side.Provider == null ||
                !activeProviders.Contains(side.Provider));

            string signature = DescribeProviders();
            if (!string.Equals(_lastBoundSignature, signature, StringComparison.Ordinal))
            {
                _lastBoundSignature = signature;
                Log("p46i_ui_hit_marker_interactors_bound", new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["bound_count"] = _sides.Count,
                    ["providers"] = signature
                });
            }
        }

        private void BindXrRayInteractors(List<Component> activeProviders)
        {
            foreach (XRRayInteractor interactor in FindObjectsByType<XRRayInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!ShouldTrack(interactor, out string reason))
                {
                    LogProviderDiscardedOnce("XRRayInteractor", interactor, reason);
                    continue;
                }

                activeProviders.Add(interactor);
                TryAddSide(interactor, "XRRayInteractor");
            }
        }

        private void BindNearFarInteractors(List<Component> activeProviders)
        {
            foreach (NearFarInteractor interactor in FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!ShouldTrack(interactor, out string reason))
                {
                    LogProviderDiscardedOnce("NearFarInteractor", interactor, reason);
                    continue;
                }

                activeProviders.Add(interactor);
                TryAddSide(interactor, "NearFarInteractor");
            }
        }

        private void TryAddSide(Component provider, string providerType)
        {
            if (_sides.Exists(side => side.Provider == provider))
            {
                return;
            }

            MarkerSide side = CreateSide(provider, providerType);
            _sides.Add(side);
        }

        private static bool ShouldTrack(XRRayInteractor interactor, out string reason)
        {
            if (interactor == null)
            {
                reason = "null";
                return false;
            }

            if (!interactor.gameObject.activeInHierarchy || !interactor.enabled)
            {
                reason = "inactive";
                return false;
            }

            if (!interactor.enableUIInteraction)
            {
                reason = "ui_interaction_disabled";
                return false;
            }

            if (IsTeleportInteractor(interactor.transform))
            {
                reason = "teleport_interactor";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static bool ShouldTrack(NearFarInteractor interactor, out string reason)
        {
            if (interactor == null)
            {
                reason = "null";
                return false;
            }

            if (!interactor.gameObject.activeInHierarchy || !interactor.enabled)
            {
                reason = "inactive";
                return false;
            }

            if (IsTeleportInteractor(interactor.transform))
            {
                reason = "teleport_interactor";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static bool IsTeleportInteractor(Transform transform)
        {
            return transform != null &&
                transform.name.IndexOf("Teleport", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private MarkerSide CreateSide(Component provider, string providerType)
        {
            GameObject markerObject = new GameObject($"P46I02 UI Hit Marker {providerType} {_sides.Count + 1}");
            SetIgnoreRaycastLayer(markerObject);
            markerObject.transform.SetParent(transform, false);
            MeshFilter meshFilter = markerObject.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = _discMesh;
            MeshRenderer renderer = markerObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            markerObject.SetActive(false);
            return new MarkerSide(provider, providerType, markerObject.transform);
        }

        private void UpdateMarker(MarkerSide side)
        {
            if (side == null || side.Provider == null || !IsProviderActive(side))
            {
                SetVisible(side, false, "provider_inactive");
                return;
            }

            if (TryGetCurrentUiHit(side, out RaycastResult result) && IsValidUiHit(result))
            {
                Vector3 normal = ResolveNormal(result, side.Provider.transform);
                MoveMarker(side, result.worldPosition, normal, "ui_hit_xri");
                return;
            }

            if (TryGetFallbackCanvasHit(side, out Vector3 worldPosition, out Vector3 fallbackNormal))
            {
                MoveMarker(side, worldPosition, fallbackNormal, "ui_hit_canvas_fallback");
                return;
            }

            SetVisible(side, false, "no_ui_hit");
        }

        private static bool IsProviderActive(MarkerSide side)
        {
            return side.Provider != null &&
                side.Provider.gameObject.activeInHierarchy &&
                (!(side.Provider is Behaviour behaviour) || behaviour.enabled);
        }

        private static bool TryGetCurrentUiHit(MarkerSide side, out RaycastResult result)
        {
            switch (side.Provider)
            {
                case XRRayInteractor rayInteractor:
                    return rayInteractor.TryGetCurrentUIRaycastResult(out result);
                case NearFarInteractor nearFarInteractor:
                    return nearFarInteractor.TryGetCurrentUIRaycastResult(out result);
                default:
                    result = default;
                    return false;
            }
        }

        private bool TryGetFallbackCanvasHit(MarkerSide side, out Vector3 worldPosition, out Vector3 normal)
        {
            worldPosition = default;
            normal = default;
            if (!TryGetProviderRay(side.Provider, out Ray ray))
            {
                return false;
            }

            float bestDistance = float.PositiveInfinity;
            Canvas bestCanvas = null;
            foreach (TrackedDeviceGraphicRaycaster raycaster in FindObjectsByType<TrackedDeviceGraphicRaycaster>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (raycaster == null || !raycaster.enabled)
                {
                    continue;
                }

                Canvas canvas = raycaster.GetComponent<Canvas>();
                if (!IsRelevantCanvas(canvas) || !TryRaycastCanvasRect(canvas, ray, out Vector3 candidate, out float distance))
                {
                    continue;
                }

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    worldPosition = candidate;
                    bestCanvas = canvas;
                }
            }

            if (bestCanvas == null)
            {
                return false;
            }

            normal = bestCanvas.transform.forward;
            if (Vector3.Dot(normal, ray.origin - worldPosition) < 0f)
            {
                normal = -normal;
            }

            return true;
        }

        private static bool TryGetProviderRay(Component provider, out Ray ray)
        {
            Transform rayOrigin = null;
            if (provider is IXRRayProvider rayProvider)
            {
                rayOrigin = rayProvider.GetOrCreateRayOrigin();
            }

            if (rayOrigin == null)
            {
                rayOrigin = provider != null ? provider.transform : null;
            }

            if (rayOrigin == null)
            {
                ray = default;
                return false;
            }

            ray = new Ray(rayOrigin.position, rayOrigin.forward);
            return ray.direction.sqrMagnitude > 0.001f;
        }

        private static bool TryRaycastCanvasRect(Canvas canvas, Ray ray, out Vector3 worldPosition, out float distance)
        {
            worldPosition = default;
            distance = 0f;
            RectTransform rectTransform = canvas != null ? canvas.GetComponent<RectTransform>() : null;
            if (rectTransform == null)
            {
                return false;
            }

            var plane = new Plane(rectTransform.forward, rectTransform.position);
            if (!plane.Raycast(ray, out distance) || distance < 0.01f)
            {
                return false;
            }

            worldPosition = ray.GetPoint(distance);
            Vector3 local = rectTransform.InverseTransformPoint(worldPosition);
            Rect rect = rectTransform.rect;
            return local.x >= rect.xMin &&
                local.x <= rect.xMax &&
                local.y >= rect.yMin &&
                local.y <= rect.yMax;
        }

        private static bool IsValidUiHit(RaycastResult result)
        {
            if (!result.isValid || result.gameObject == null)
            {
                return false;
            }

            Canvas canvas = result.gameObject.GetComponentInParent<Canvas>();
            return IsRelevantCanvas(canvas);
        }

        private static bool IsRelevantCanvas(Canvas canvas)
        {
            return canvas != null &&
                canvas.renderMode == RenderMode.WorldSpace &&
                canvas.isActiveAndEnabled &&
                canvas.gameObject.activeInHierarchy &&
                canvas.GetComponent<TrackedDeviceGraphicRaycaster>() != null &&
                canvas.GetComponent<TrackedDeviceGraphicRaycaster>().enabled;
        }

        private void MoveMarker(MarkerSide side, Vector3 worldPosition, Vector3 normal, string reason)
        {
            if (normal.sqrMagnitude < 0.25f)
            {
                normal = side.Provider.transform.forward;
            }

            normal.Normalize();
            side.Marker.position = worldPosition + normal * Mathf.Max(0.0005f, _surfaceOffsetMeters);
            side.Marker.rotation = Quaternion.LookRotation(normal, Vector3.up);
            float radius = Mathf.Max(0.002f, _radiusMeters);
            side.Marker.localScale = new Vector3(radius, radius, radius);
            SetVisible(side, true, reason);
        }

        private static Vector3 ResolveNormal(RaycastResult result, Transform providerTransform)
        {
            if (result.worldNormal.sqrMagnitude > 0.25f)
            {
                return result.worldNormal.normalized;
            }

            Canvas canvas = result.gameObject != null ? result.gameObject.GetComponentInParent<Canvas>() : null;
            if (canvas != null)
            {
                Vector3 canvasNormal = canvas.transform.forward;
                if (Vector3.Dot(canvasNormal, providerTransform.position - result.worldPosition) < 0f)
                {
                    canvasNormal = -canvasNormal;
                }

                return canvasNormal.normalized;
            }

            Vector3 fallback = result.worldPosition - providerTransform.position;
            return fallback.sqrMagnitude > 0.0001f ? -fallback.normalized : Vector3.back;
        }

        private void SetAllVisible(bool visible, string reason)
        {
            foreach (MarkerSide side in _sides)
            {
                SetVisible(side, visible, reason);
            }
        }

        private void SetVisible(MarkerSide side, bool visible, string reason)
        {
            if (side == null || side.Marker == null || side.Visible == visible)
            {
                return;
            }

            side.Visible = visible;
            side.Marker.gameObject.SetActive(visible);
            if (!QuestLoggingPolicy.EmitLegacyContinuousDiagnostics ||
                Time.unscaledTime < side.NextVisibleLogAt)
            {
                return;
            }

            side.NextVisibleLogAt = Time.unscaledTime + VisibleChangeLogMinIntervalSeconds;
            Log("p46i_ui_hit_marker_visible_changed", new Dictionary<string, object>
            {
                ["visible"] = visible,
                ["reason"] = reason ?? string.Empty,
                ["provider_type"] = side.ProviderType,
                ["provider_path"] = side.Provider != null ? GetPath(side.Provider.transform) : string.Empty
            });
        }

        private void EnsureResources()
        {
            if (_discMesh == null)
            {
                _discMesh = CreateDiscMesh();
            }

            if (_material == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ??
                    Shader.Find("Unlit/Color") ??
                    Shader.Find("Sprites/Default");
                _material = new Material(shader)
                {
                    name = "P46I02 UI Hit Marker Material",
                    color = _markerColor
                };
                if (_material.HasProperty("_BaseColor"))
                {
                    _material.SetColor("_BaseColor", _markerColor);
                }

                if (_material.HasProperty("_Color"))
                {
                    _material.SetColor("_Color", _markerColor);
                }

                _material.renderQueue = (int)RenderQueue.Transparent;
            }
        }

        private static Mesh CreateDiscMesh()
        {
            var vertices = new Vector3[DiscSegments + 1];
            var triangles = new int[DiscSegments * 3];
            vertices[0] = Vector3.zero;

            for (int i = 0; i < DiscSegments; i++)
            {
                float angle = (i / (float)DiscSegments) * Mathf.PI * 2f;
                vertices[i + 1] = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
            }

            for (int i = 0; i < DiscSegments; i++)
            {
                int triangleIndex = i * 3;
                triangles[triangleIndex] = 0;
                triangles[triangleIndex + 1] = i + 1;
                triangles[triangleIndex + 2] = i == DiscSegments - 1 ? 1 : i + 2;
            }

            var mesh = new Mesh
            {
                name = "P46I02 Filled UI Hit Marker Disc",
                vertices = vertices,
                triangles = triangles
            };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        private void LogHostBound(GameObject requestedRoot)
        {
            Log("p46i_ui_hit_marker_host_bound", new Dictionary<string, object>
            {
                ["requested_root_path"] = requestedRoot != null ? GetPath(requestedRoot.transform) : string.Empty,
                ["host_path"] = GetPath(transform),
                ["host_under_forbidden_path"] = IsForbiddenMarkerPath(transform)
            });
        }

        private void LogProviderDiscardedOnce(string providerType, Component provider, string reason)
        {
            if (provider == null)
            {
                return;
            }

            string key = $"{providerType}:{GetPath(provider.transform)}:{reason}";
            if (!_discardedProviderLogs.Add(key))
            {
                return;
            }

            Log("p46i_ui_hit_marker_provider_discarded", new Dictionary<string, object>
            {
                ["provider_type"] = providerType,
                ["provider_path"] = GetPath(provider.transform),
                ["active"] = provider.gameObject.activeInHierarchy,
                ["enabled"] = provider is Behaviour behaviour && behaviour.enabled,
                ["reason"] = reason ?? string.Empty
            });
        }

        private static void SetIgnoreRaycastLayer(GameObject target)
        {
            if (target == null)
            {
                return;
            }

            int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreRaycastLayer >= 0)
            {
                target.layer = ignoreRaycastLayer;
            }
        }

        private void LogInitializedOnce(string reason)
        {
            if (_initializedLogged)
            {
                return;
            }

            _initializedLogged = true;
            Log("p46i_ui_hit_marker_initialized", new Dictionary<string, object>
            {
                ["reason"] = reason ?? string.Empty,
                ["host_path"] = GetPath(transform),
                ["radius_meters"] = _radiusMeters,
                ["surface_offset_meters"] = _surfaceOffsetMeters,
                ["layer"] = LayerMask.LayerToName(gameObject.layer),
                ["host_under_forbidden_path"] = IsForbiddenMarkerPath(transform)
            });
        }

        private void LogForbiddenPathIfNeeded(string reason)
        {
            if (_forbiddenPathLogged || !IsForbiddenMarkerPath(transform))
            {
                return;
            }

            _forbiddenPathLogged = true;
            Log("p46i_ui_hit_marker_forbidden_path_detected", new Dictionary<string, object>
            {
                ["reason"] = reason ?? string.Empty,
                ["host_path"] = GetPath(transform),
                ["would_be_disabled_by_quest_policy"] = true
            });
        }

        private string DescribeProviders()
        {
            if (_sides.Count == 0)
            {
                return string.Empty;
            }

            var names = new List<string>(_sides.Count);
            foreach (MarkerSide side in _sides)
            {
                if (side?.Provider != null)
                {
                    names.Add($"{side.ProviderType}:active={side.Provider.gameObject.activeInHierarchy}:path={GetPath(side.Provider.transform)}");
                }
            }

            return string.Join(";", names);
        }

        private static bool IsForbiddenMarkerPath(Transform transform)
        {
            string path = GetPath(transform);
            return path.IndexOf("RuntimeUiRayInteractors", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("Runtime UI Ray", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("ManualUiLaser", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void Log(string eventName, Dictionary<string, object> payload)
        {
            Debug.Log($"{LogPrefix} {eventName}");
            TiagoExperimentTelemetry.LogEvent(eventName, payload);
        }

        private static string GetPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            var names = new Stack<string>();
            Transform current = transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", names);
        }

        private sealed class MarkerSide
        {
            public MarkerSide(Component provider, string providerType, Transform marker)
            {
                Provider = provider;
                ProviderType = providerType;
                Marker = marker;
            }

            public Component Provider { get; }
            public string ProviderType { get; }
            public Transform Marker { get; }
            public bool Visible { get; set; }
            public float NextVisibleLogAt { get; set; }
        }
    }
}
