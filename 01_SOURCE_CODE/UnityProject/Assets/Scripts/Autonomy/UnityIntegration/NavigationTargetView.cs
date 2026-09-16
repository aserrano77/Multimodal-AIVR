using Autonomy.Domain;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Vista de presentacion del target activo.
    /// Lee el estado compartido desde el adapter/blackboard y lo representa igual en manual y autonomo.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NavigationTargetView : MonoBehaviour
    {
        private const string LogPrefix = "[NavigationTargetView]";

        [Header("References")]
        [SerializeField] private AutonomousRobotAdapter _robotAdapter;
        [SerializeField] private Transform _robotReference;
        [SerializeField] private Transform _targetMarker;
        [SerializeField] private LineRenderer _targetLine;

        [Header("Display")]
        [SerializeField] private bool _hideMarkerWhenNoTarget = true;
        [SerializeField] private bool _enableDiagnostics = true;

        private bool _hadVisibleTarget;
        private bool _hasLoggedFirstLateUpdate;
        private string _lastTargetId;
        private string _lastTargetSource;
        private bool? _lastResolvedState;
        private string _lastResolveReason;
        private Renderer[] _markerRenderers;
        private AppliedVisualState _lastAppliedState;
        private bool _hasAppliedState;

        private void Awake()
        {
            ResolveReferences();
            CacheMarkerRenderers();
            ValidateConfiguration();
            DisableLegacyTargetLine();
            SetVisualsVisible(false);
        }

        private void OnEnable()
        {
            LogDiagnostic("lifecycle", "enabled");
        }

        private void OnDisable()
        {
            LogDiagnostic("lifecycle", "disabled");
        }

        private void Reset()
        {
            ResolveReferences();
        }

        private void LateUpdate()
        {
            if (!_hasLoggedFirstLateUpdate)
            {
                _hasLoggedFirstLateUpdate = true;
                LogDiagnostic("lifecycle", $"first_late_update adapter={(_robotAdapter != null)} robotRef={(_robotReference != null)} marker={(_targetMarker != null)} line={(_targetLine != null)}");
            }

            ResolveReferences();
            DetectVisualOverrideBeforeApply();

            if (_robotAdapter == null)
            {
                LogResolveState(false, "None", null, Vector3.zero, Vector3.zero, "missing_adapter");
                HandleTargetLost("missing_adapter");
                SetVisualsVisible(false);
                return;
            }

            if (!TryResolveActiveTarget(out TargetDescriptor target, out Vector3 targetPosition, out string source, out string reason))
            {
                LogResolveState(false, source, null, Vector3.zero, Vector3.zero, reason);
                HandleTargetLost(reason);
                SetVisualsVisible(false);
                return;
            }

            Transform robotReference = _robotReference != null ? _robotReference : _robotAdapter.NavigationReference;
            Vector3 robotPosition = robotReference != null ? robotReference.position : _robotAdapter.transform.position;
            LogResolveState(true, source, target, targetPosition, robotPosition, string.Empty);

            if (_targetMarker != null)
            {
                _targetMarker.position = targetPosition;
            }

            DisableLegacyTargetLine();

            HandleTargetVisible(target, targetPosition, robotPosition, source);
            SetVisualsVisible(true);
            CaptureAppliedState("target_visible");
        }

        private void ResolveReferences()
        {
            if (_robotAdapter == null)
            {
                _robotAdapter = GetComponent<AutonomousRobotAdapter>();
            }

            if (_robotAdapter == null)
            {
                _robotAdapter = GetComponentInParent<AutonomousRobotAdapter>();
            }

            if (_robotReference == null && _robotAdapter != null)
            {
                _robotReference = _robotAdapter.NavigationReference;
            }
        }

        private void CacheMarkerRenderers()
        {
            _markerRenderers = _targetMarker != null
                ? _targetMarker.GetComponentsInChildren<Renderer>(true)
                : System.Array.Empty<Renderer>();
        }

        private void ValidateConfiguration()
        {
            if (_robotAdapter == null)
            {
                Debug.LogWarning($"{LogPrefix} Missing {nameof(AutonomousRobotAdapter)} reference. The target view will stay idle until one is resolved.", this);
            }

            if (_targetMarker == null)
            {
                Debug.LogWarning($"{LogPrefix} Missing target marker Transform. Only the line will be shown if assigned.", this);
            }

            DisableLegacyTargetLine();
        }

        private void SetVisualsVisible(bool visible)
        {
            if (_hadVisibleTarget == visible)
            {
                DisableLegacyTargetLine();
                return;
            }

            _hadVisibleTarget = visible;
            LogDiagnostic("visibility", $"visible={visible}");

            if (_hideMarkerWhenNoTarget)
            {
                SetMarkerRenderersVisible(visible);
            }

            DisableLegacyTargetLine();

            CaptureAppliedState($"visibility_changed visible={visible}");
        }

        private void DisableLegacyTargetLine()
        {
            if (_targetLine == null)
            {
                return;
            }

            _targetLine.enabled = false;
            _targetLine.positionCount = 0;
        }

        private void SetMarkerRenderersVisible(bool visible)
        {
            if (_markerRenderers == null)
            {
                return;
            }

            foreach (Renderer markerRenderer in _markerRenderers)
            {
                if (markerRenderer != null)
                {
                    markerRenderer.enabled = visible;
                }
            }
        }

        private void HandleTargetVisible(TargetDescriptor target, Vector3 targetPosition, Vector3 robotPosition, string source)
        {
            if (_lastTargetId == target.Id && _lastTargetSource == source)
            {
                return;
            }

            _lastTargetId = target.Id;
            _lastTargetSource = source;
            Debug.Log($"{LogPrefix} Target visible | source={source} id={target.Id} target={targetPosition} robot={robotPosition}", this);
        }

        private void HandleTargetLost(string reason)
        {
            if (string.IsNullOrEmpty(_lastTargetId))
            {
                return;
            }

            Debug.Log($"{LogPrefix} Target hidden | source={_lastTargetSource} id={_lastTargetId} reason={reason}", this);
            _lastTargetId = null;
            _lastTargetSource = null;
        }

        private bool TryResolveActiveTarget(out TargetDescriptor target, out Vector3 targetPosition, out string source, out string reason)
        {
            if (_robotAdapter.Blackboard != null &&
                _robotAdapter.Blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out target) && target != null)
            {
                targetPosition = ToUnity(target.Position);
                source = "Blackboard";
                reason = string.Empty;
                return true;
            }

            if (TiagoTeleopOverride.HasActiveTarget)
            {
                Vector3 pos = TiagoTeleopOverride.ActiveTargetPosition;
                target = new TargetDescriptor("manual_teleop", new System.Numerics.Vector3(pos.x, pos.y, pos.z));
                targetPosition = pos;
                source = "TeleopOverride";
                reason = string.Empty;
                return true;
            }

            target = null;
            targetPosition = Vector3.zero;
            source = "None";
            reason = "no_active_target";
            return false;
        }

        private static Vector3 ToUnity(System.Numerics.Vector3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }

        private void DetectVisualOverrideBeforeApply()
        {
            if (!_hasAppliedState)
            {
                return;
            }

            AppliedVisualState current = ReadCurrentState();
            if (_lastAppliedState.Equals(current))
            {
                return;
            }

            LogDiagnostic(
                "anomaly",
                $"visual_state_overridden_after_view_update last={_lastAppliedState} current={current}");
        }

        private void CaptureAppliedState(string reason)
        {
            _lastAppliedState = ReadCurrentState();
            _hasAppliedState = true;
            LogDiagnostic("applied", $"reason={reason} state={_lastAppliedState}");
        }

        private AppliedVisualState ReadCurrentState()
        {
            Vector3 markerPosition = _targetMarker != null ? _targetMarker.position : Vector3.zero;
            bool markerActive = _targetMarker != null && _targetMarker.gameObject.activeInHierarchy;
            bool markerRendererEnabled = IsAnyMarkerRendererEnabled();

            return new AppliedVisualState(
                markerPosition,
                markerActive,
                markerRendererEnabled);
        }

        private bool IsAnyMarkerRendererEnabled()
        {
            if (_markerRenderers == null || _markerRenderers.Length == 0)
            {
                return false;
            }

            foreach (Renderer markerRenderer in _markerRenderers)
            {
                if (markerRenderer != null && markerRenderer.enabled)
                {
                    return true;
                }
            }

            return false;
        }

        private void LogResolveState(bool resolved, string source, TargetDescriptor target, Vector3 targetPosition, Vector3 robotPosition, string reason)
        {
            bool changed =
                _lastResolvedState != resolved ||
                _lastTargetSource != source ||
                _lastTargetId != (target != null ? target.Id : null) ||
                _lastResolveReason != reason;

            if (!changed)
            {
                return;
            }

            _lastResolvedState = resolved;
            _lastResolveReason = reason;

            string targetId = target != null ? target.Id : "<none>";
            LogDiagnostic(
                "resolve",
                $"resolved={resolved} source={source} targetId={targetId} target={targetPosition} robot={robotPosition} reason={reason}");
        }

        private void LogDiagnostic(string category, string details)
        {
            if (!_enableDiagnostics)
            {
                return;
            }

            if (ExperimentDataPathResolver.IsAndroidRuntime())
            {
                return;
            }

            Debug.Log($"{LogPrefix} {category} | {details}", this);
        }

        private readonly struct AppliedVisualState
        {
            private readonly Vector3 _markerPosition;
            private readonly bool _markerActive;
            private readonly bool _markerRendererEnabled;

            public AppliedVisualState(
                Vector3 markerPosition,
                bool markerActive,
                bool markerRendererEnabled)
            {
                _markerPosition = markerPosition;
                _markerActive = markerActive;
                _markerRendererEnabled = markerRendererEnabled;
            }

            public bool Equals(AppliedVisualState other)
            {
                return Approximately(_markerPosition, other._markerPosition) &&
                    _markerActive == other._markerActive &&
                    _markerRendererEnabled == other._markerRendererEnabled;
            }

            public override string ToString()
            {
                return $"markerPos={_markerPosition} markerActive={_markerActive} markerRenderer={_markerRendererEnabled}";
            }

            private static bool Approximately(Vector3 a, Vector3 b)
            {
                return Vector3.SqrMagnitude(a - b) <= 0.0001f;
            }
        }
    }
}
