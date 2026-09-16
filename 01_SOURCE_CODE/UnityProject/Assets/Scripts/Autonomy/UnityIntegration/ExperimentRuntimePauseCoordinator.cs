using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Single runtime authority for the experimental pause menu. This pause is
    /// intentionally independent from the supervisory voice stop latch.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ExperimentRuntimePauseCoordinator : MonoBehaviour
    {
        public const string CommandRejectionReason = "experiment_paused_command_rejected";
        public const string AutonomyRequestRejectionReason = "experiment_paused_autonomy_request_rejected";

        private const string LogPrefix = "[P46L-PAUSE]";
        private static ExperimentRuntimePauseCoordinator _instance;
        private static int _pauseEpoch;

        private readonly List<BehaviourState> _disabledBehaviours = new();
        private readonly List<NavMeshAgentState> _navMeshAgents = new();
        private readonly List<AutonomousRobotAdapter> _robotAdapters = new();
        private bool _isExperimentPaused;
        private float _timeScaleBeforePause = 1f;
        private double _pauseStartedRealtime;
        private ExperimentSimulationPauseAuthority.PauseLease _pauseLease;

        public static bool IsExperimentPaused => _instance != null && _instance._isExperimentPaused;
        public static int PauseEpoch => _pauseEpoch;
        public bool IsPaused => _isExperimentPaused;
        public float TimeScaleBeforePause => _timeScaleBeforePause;
        public int LocomotionComponentsDisabled { get; private set; }
        public int ManipulationComponentsDisabled { get; private set; }
        public int NavMeshAgentsSuspended { get; private set; }

        public static ExperimentRuntimePauseCoordinator EnsureFor(GameObject host)
        {
            if (_instance != null)
            {
                return _instance;
            }

            if (host == null)
            {
                return null;
            }

            ExperimentRuntimePauseCoordinator existing = host.GetComponent<ExperimentRuntimePauseCoordinator>();
            ExperimentRuntimePauseCoordinator resolved = existing != null
                ? existing
                : host.AddComponent<ExperimentRuntimePauseCoordinator>();
            // Awake is guaranteed in player runtime, but EditMode tests can create a
            // component without invoking it. Keep EnsureFor authoritative in both cases.
            if (_instance == null)
            {
                _instance = resolved;
            }

            return _instance;
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning($"{LogPrefix} duplicate_authority_removed | existing={GetPath(_instance.transform)} duplicate={GetPath(transform)}", this);
                Destroy(this);
                return;
            }

            _instance = this;
        }

        public bool EnterPause(string reason = "pause_menu")
        {
            Dictionary<string, object> requestedPayload = BuildPausePayload(reason, "requested");
            requestedPayload["time_scale_before"] = Time.timeScale;
            TiagoExperimentTelemetry.LogEvent("experiment_pause_requested", requestedPayload);
            Debug.Log($"{LogPrefix} experiment_pause_requested | reason={reason ?? string.Empty} active={_isExperimentPaused} time_scale_before={Time.timeScale:0.###}", this);

            if (_isExperimentPaused)
            {
                TiagoExperimentTelemetry.LogEvent("experiment_pause_already_active", BuildPausePayload(reason, "already_active"));
                Debug.Log($"{LogPrefix} experiment_pause_already_active | reason={reason ?? string.Empty} time_scale_during={Time.timeScale:0.###}", this);
                return false;
            }

            if (!ExperimentSimulationPauseAuthority.IsPauseActive && Mathf.Approximately(Time.timeScale, 0f))
            {
                ExperimentSimulationPauseAuthority.NormalizeForRunningScene("pause_menu_orphaned_zero_preflight", this);
            }

            _timeScaleBeforePause = Time.timeScale;
            _pauseStartedRealtime = Time.realtimeSinceStartupAsDouble;
            _isExperimentPaused = true;
            _pauseEpoch++;

            CaptureAndDisableRuntimeControls();
            CaptureAndSuspendNavMeshAgents();
            CaptureAndSuspendRobotAdapters();
            _pauseLease = ExperimentSimulationPauseAuthority.Acquire("experiment_pause_menu", reason ?? "pause_menu", this);

            Dictionary<string, object> enteredPayload = BuildPausePayload(reason, "entered");
            enteredPayload["time_scale_before"] = _timeScaleBeforePause;
            enteredPayload["time_scale_during"] = Time.timeScale;
            TiagoExperimentTelemetry.LogEvent("experiment_pause_entered", enteredPayload);
            TiagoExperimentTelemetry.LogEvent("pause_started", new Dictionary<string, object>(enteredPayload));
            Debug.Log(
                $"{LogPrefix} experiment_pause_entered | reason={reason ?? string.Empty} time_scale_before={_timeScaleBeforePause:0.###} time_scale_during={Time.timeScale:0.###} " +
                $"locomotion_components_disabled={LocomotionComponentsDisabled} manipulation_components_disabled={ManipulationComponentsDisabled} nav_agents_suspended={NavMeshAgentsSuspended}",
                this);
            return true;
        }

        public bool ResumeFromPause(string reason = "pause_menu_continue")
        {
            TiagoExperimentTelemetry.LogEvent("experiment_pause_resume_requested", BuildPausePayload(reason, "resume_requested"));
            Debug.Log($"{LogPrefix} experiment_pause_resume_requested | reason={reason ?? string.Empty} active={_isExperimentPaused}", this);
            return RestoreRuntimeState(reason, cleanup: false);
        }

        public bool CleanupPauseState(string reason = "cleanup")
        {
            return RestoreRuntimeState(reason, cleanup: true);
        }

        private bool RestoreRuntimeState(string reason, bool cleanup)
        {
            if (!_isExperimentPaused)
            {
                Dictionary<string, object> inactivePayload = BuildPausePayload(reason, cleanup ? "cleanup_noop" : "resume_noop");
                inactivePayload["cleanup_reason"] = cleanup ? reason ?? string.Empty : string.Empty;
                inactivePayload["resume_result"] = cleanup ? "cleanup_noop_not_paused" : "resume_noop_not_paused";
                TiagoExperimentTelemetry.LogEvent(cleanup ? "experiment_pause_cleanup_noop" : "experiment_pause_resume_noop", inactivePayload);
                return false;
            }

            double duration = Math.Max(0d, Time.realtimeSinceStartupAsDouble - _pauseStartedRealtime);
            float timeScaleDuring = Time.timeScale;

            try
            {
                RestoreNavMeshAgents();
                RestoreRuntimeControls();
            }
            finally
            {
                ExperimentSimulationPauseAuthority.PauseLease lease = _pauseLease;
                _pauseLease = null;
                _isExperimentPaused = false;
                lease?.Dispose();
            }

            foreach (AutonomousRobotAdapter adapter in _robotAdapters)
            {
                if (adapter != null)
                {
                    adapter.ResumeFromExperimentPause(reason);
                }
            }

            Dictionary<string, object> payload = BuildPausePayload(reason, cleanup ? "cleaned" : "resumed");
            payload["pause_duration_seconds"] = duration;
            payload["experiment_pause_duration_seconds"] = duration;
            payload["time_scale_before"] = _timeScaleBeforePause;
            payload["time_scale_during"] = timeScaleDuring;
            payload["time_scale_after"] = Time.timeScale;
            payload["cleanup_reason"] = cleanup ? reason ?? string.Empty : string.Empty;
            payload["resume_result"] = cleanup ? "pause_state_cleaned" : "same_runtime_state_resumed";
            TiagoExperimentTelemetry.LogEvent(cleanup ? "experiment_pause_cleaned" : "experiment_pause_resumed", payload);
            if (!cleanup)
            {
                TiagoExperimentTelemetry.LogEvent("pause_resumed", new Dictionary<string, object>(payload));
            }
            Debug.Log(
                $"{LogPrefix} {(cleanup ? "experiment_pause_cleaned" : "experiment_pause_resumed")} | reason={reason ?? string.Empty} " +
                $"experiment_pause_duration_seconds={duration:0.###} time_scale_before={_timeScaleBeforePause:0.###} time_scale_during={timeScaleDuring:0.###} " +
                $"time_scale_after={Time.timeScale:0.###} resume_result={(cleanup ? "pause_state_cleaned" : "same_runtime_state_resumed")}",
                this);

            ClearCapturedState();
            return true;
        }

        private void LateUpdate()
        {
            if (!_isExperimentPaused)
            {
                return;
            }

            ExperimentSimulationPauseAuthority.EnforceOwnedPause("experiment_pause_coordinator_late_update", this);

            CaptureAndDisableNewRuntimeControls();
            EnforceNavMeshAgentsSuspended();
        }

        private void CaptureAndDisableRuntimeControls()
        {
            _disabledBehaviours.Clear();
            LocomotionComponentsDisabled = 0;
            ManipulationComponentsDisabled = 0;
            CaptureAndDisableNewRuntimeControls();
        }

        private void CaptureAndDisableNewRuntimeControls()
        {
            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null || behaviour == this || ContainsBehaviour(behaviour))
                {
                    continue;
                }

                PauseControlKind kind = ClassifyRuntimeControl(behaviour);
                if (kind == PauseControlKind.None)
                {
                    continue;
                }

                bool wasEnabled = behaviour.enabled;
                _disabledBehaviours.Add(new BehaviourState(behaviour, wasEnabled, kind));
                if (!wasEnabled)
                {
                    continue;
                }

                behaviour.enabled = false;
                if (kind == PauseControlKind.Locomotion)
                {
                    LocomotionComponentsDisabled++;
                }
                else
                {
                    ManipulationComponentsDisabled++;
                }
            }
        }

        private void CaptureAndSuspendNavMeshAgents()
        {
            _navMeshAgents.Clear();
            NavMeshAgentsSuspended = 0;
            NavMeshAgent[] agents = FindObjectsByType<NavMeshAgent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (NavMeshAgent agent in agents)
            {
                if (agent == null)
                {
                    continue;
                }

                bool canSuspend = agent.enabled && agent.gameObject.activeInHierarchy && agent.isOnNavMesh;
                bool wasStopped = canSuspend && agent.isStopped;
                _navMeshAgents.Add(new NavMeshAgentState(agent, canSuspend, wasStopped));
                if (canSuspend)
                {
                    agent.isStopped = true;
                    NavMeshAgentsSuspended++;
                }
            }
        }

        private void CaptureAndSuspendRobotAdapters()
        {
            _robotAdapters.Clear();
            AutonomousRobotAdapter[] adapters = FindObjectsByType<AutonomousRobotAdapter>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (AutonomousRobotAdapter adapter in adapters)
            {
                if (adapter == null)
                {
                    continue;
                }

                _robotAdapters.Add(adapter);
                adapter.SuspendForExperimentPause("pause_entered");
            }
        }

        private void EnforceNavMeshAgentsSuspended()
        {
            foreach (NavMeshAgentState state in _navMeshAgents)
            {
                if (state.Agent != null && state.CanSuspend && state.Agent.enabled && state.Agent.gameObject.activeInHierarchy && state.Agent.isOnNavMesh)
                {
                    state.Agent.isStopped = true;
                }
            }
        }

        private void RestoreNavMeshAgents()
        {
            foreach (NavMeshAgentState state in _navMeshAgents)
            {
                if (state.Agent != null && state.CanSuspend && state.Agent.enabled && state.Agent.gameObject.activeInHierarchy && state.Agent.isOnNavMesh)
                {
                    state.Agent.isStopped = state.WasStopped;
                }
            }
        }

        private void RestoreRuntimeControls()
        {
            foreach (BehaviourState state in _disabledBehaviours)
            {
                if (state.Behaviour != null)
                {
                    state.Behaviour.enabled = state.WasEnabled;
                }
            }
        }

        private Dictionary<string, object> BuildPausePayload(string reason, string phase)
        {
            Dictionary<string, object> payload = new()
            {
                ["reason"] = reason ?? string.Empty,
                ["phase"] = phase ?? string.Empty,
                ["scene"] = SceneManager.GetActiveScene().name,
                ["is_experiment_paused"] = _isExperimentPaused,
                ["pause_epoch"] = _pauseEpoch,
                ["time_scale_before"] = _timeScaleBeforePause,
                ["time_scale_during"] = Time.timeScale,
                ["time_scale_after"] = Time.timeScale,
                ["global_pause_active"] = ExperimentSimulationPauseAuthority.IsPauseActive,
                ["pending_pause_owners"] = ExperimentSimulationPauseAuthority.ActiveOwnerCount,
                ["pause_owners"] = ExperimentSimulationPauseAuthority.ActiveOwners,
                ["pause_duration_seconds"] = _isExperimentPaused ? Math.Max(0d, Time.realtimeSinceStartupAsDouble - _pauseStartedRealtime) : 0d,
                ["locomotion_components_disabled"] = LocomotionComponentsDisabled,
                ["manipulation_components_disabled"] = ManipulationComponentsDisabled,
                ["nav_mesh_agents_suspended"] = NavMeshAgentsSuspended,
                ["nav_agent_was_stopped"] = DescribeNavAgentStoppedStates(),
                ["robot_mode_before"] = string.Empty,
                ["task_status_before"] = string.Empty,
                ["task_phase_before"] = string.Empty,
                ["target_before"] = string.Empty,
                ["destination_before"] = string.Empty,
                ["held_object_id_before"] = string.Empty,
                ["cleanup_reason"] = string.Empty,
                ["resume_result"] = string.Empty
            };

            AutonomousRobotAdapter adapter = FindFirstObjectByType<AutonomousRobotAdapter>(FindObjectsInactive.Include);
            if (adapter != null)
            {
                Dictionary<string, object> robot = adapter.BuildP44IVoiceStopResumeStatePayload("experiment_pause_snapshot");
                payload["robot_mode_before"] = GetPayloadValue(robot, "robot_mode");
                payload["task_status_before"] = GetPayloadValue(robot, "task_status");
                payload["task_phase_before"] = GetPayloadValue(robot, "phase");
                payload["target_before"] = GetPayloadValue(robot, "active_target_id");
                payload["destination_before"] = GetPayloadValue(robot, "active_place_target_id");
                payload["held_object_id_before"] = GetPayloadValue(robot, "held_object_id");
                payload["voice_stop_latched_before"] = GetPayloadValue(robot, "voice_stop_latched");
                payload["stopped_snapshot_valid_before"] = GetPayloadValue(robot, "stopped_snapshot_valid");
            }

            return payload;
        }

        private string DescribeNavAgentStoppedStates()
        {
            if (_navMeshAgents.Count == 0)
            {
                return string.Empty;
            }

            var values = new List<string>(_navMeshAgents.Count);
            foreach (NavMeshAgentState state in _navMeshAgents)
            {
                values.Add($"{GetPath(state.Agent != null ? state.Agent.transform : null)}:{state.WasStopped}");
            }

            return string.Join("|", values);
        }

        private static object GetPayloadValue(IReadOnlyDictionary<string, object> payload, string key)
        {
            return payload != null && payload.TryGetValue(key, out object value) ? value ?? string.Empty : string.Empty;
        }

        private bool ContainsBehaviour(Behaviour behaviour)
        {
            foreach (BehaviourState state in _disabledBehaviours)
            {
                if (state.Behaviour == behaviour)
                {
                    return true;
                }
            }

            return false;
        }

        private static PauseControlKind ClassifyRuntimeControl(MonoBehaviour behaviour)
        {
            string typeName = behaviour.GetType().Name;
            string fullName = behaviour.GetType().FullName ?? typeName;
            string path = GetPath(behaviour.transform);

            if (typeName.Contains("ControllerInputActionManager", StringComparison.Ordinal) ||
                typeName.Contains("ContinuousMoveProvider", StringComparison.Ordinal) ||
                typeName.Contains("DynamicMoveProvider", StringComparison.Ordinal) ||
                typeName.Contains("ContinuousTurnProvider", StringComparison.Ordinal) ||
                typeName.Contains("SnapTurnProvider", StringComparison.Ordinal) ||
                typeName.Contains("TeleportationProvider", StringComparison.Ordinal) ||
                typeName.Contains("GrabMoveProvider", StringComparison.Ordinal) ||
                fullName.Contains("LocomotionProvider", StringComparison.Ordinal))
            {
                return PauseControlKind.Locomotion;
            }

            if (typeName.Equals("XRDirectInteractor", StringComparison.Ordinal) ||
                typeName.Equals("XRGrabInteractable", StringComparison.Ordinal) ||
                (typeName.Equals("XRRayInteractor", StringComparison.Ordinal) &&
                 path.Contains("Teleport", StringComparison.OrdinalIgnoreCase)))
            {
                return PauseControlKind.Manipulation;
            }

            return PauseControlKind.None;
        }

        private void ClearCapturedState()
        {
            _disabledBehaviours.Clear();
            _navMeshAgents.Clear();
            _robotAdapters.Clear();
            LocomotionComponentsDisabled = 0;
            ManipulationComponentsDisabled = 0;
            NavMeshAgentsSuspended = 0;
            _pauseStartedRealtime = 0d;
            _pauseLease = null;
        }

        private void OnApplicationQuit()
        {
            CleanupPauseState("application_quit");
        }

        private void OnDisable()
        {
            if (_instance == this)
            {
                CleanupPauseState("coordinator_disabled");
            }
        }

        private void OnDestroy()
        {
            if (_instance != this)
            {
                return;
            }

            CleanupPauseState("coordinator_destroyed");
            _instance = null;
        }

        private static string GetPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            string path = transform.name;
            Transform parent = transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        private enum PauseControlKind
        {
            None,
            Locomotion,
            Manipulation
        }

        private readonly struct BehaviourState
        {
            public BehaviourState(MonoBehaviour behaviour, bool wasEnabled, PauseControlKind kind)
            {
                Behaviour = behaviour;
                WasEnabled = wasEnabled;
                Kind = kind;
            }

            public MonoBehaviour Behaviour { get; }
            public bool WasEnabled { get; }
            public PauseControlKind Kind { get; }
        }

        private readonly struct NavMeshAgentState
        {
            public NavMeshAgentState(NavMeshAgent agent, bool canSuspend, bool wasStopped)
            {
                Agent = agent;
                CanSuspend = canSuspend;
                WasStopped = wasStopped;
            }

            public NavMeshAgent Agent { get; }
            public bool CanSuspend { get; }
            public bool WasStopped { get; }
        }
    }
}
