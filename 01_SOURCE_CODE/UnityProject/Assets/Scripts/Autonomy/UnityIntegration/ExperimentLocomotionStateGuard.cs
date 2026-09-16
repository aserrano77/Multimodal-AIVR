using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Autonomy.UnityIntegration
{
    public enum ExperimentTurnPolicy
    {
        Unknown,
        Continuous,
        Snap
    }

    public sealed class ExperimentLocomotionStateReport
    {
        public string Source { get; internal set; } = string.Empty;
        public ExperimentTurnPolicy TurnPolicy { get; internal set; }
        public bool IsValid { get; internal set; }
        public bool WasRestored { get; internal set; }
        public string FailureReason { get; internal set; } = string.Empty;
        public string ProviderStates { get; internal set; } = string.Empty;
        public string InputActionStates { get; internal set; } = string.Empty;
        public int EnabledMoveProviders { get; internal set; }
        public int EnabledContinuousTurnProviders { get; internal set; }
        public int EnabledSnapTurnProviders { get; internal set; }
        public int EnabledInputActionManagers { get; internal set; }
        public int EnabledCharacterControllers { get; internal set; }
        public int EnabledLocomotionMediators { get; internal set; }
        public int EnabledMoveActions { get; internal set; }
        public int EnabledExpectedTurnActions { get; internal set; }
        public int EnabledIncompatibleTurnActions { get; internal set; }
    }

    /// <summary>
    /// Restores and validates the concrete XRI 3.0.10 locomotion topology used by
    /// the experimental rig. XRI components are inspected through their public API
    /// so the Autonomy assembly does not gain a new dependency on scene/UI packages.
    /// </summary>
    public static class ExperimentLocomotionStateGuard
    {
        private const string FinalSceneName = "final_scene";
        private static readonly string[] DiagnosticTypeNames =
        {
            "DynamicMoveProvider", "ContinuousMoveProvider", "ContinuousTurnProvider", "SnapTurnProvider",
            "TeleportationProvider", "LocomotionMediator", "CharacterControllerDriver", "InputActionManager",
            "ControllerInputActionManager"
        };

        public static ExperimentLocomotionStateReport CaptureAndLog(string eventType, string source, bool xrGateReleased)
        {
            ExperimentLocomotionStateReport report = Capture(source, xrGateReleased);
            Log(eventType, report, xrGateReleased);
            return report;
        }

        public static ExperimentLocomotionStateReport RestoreAndValidate(string source, bool xrGateReleased)
        {
            MonoBehaviour[] behaviours = FindBehaviours();
            Behaviour[] inputManagers = FindByTypeName(behaviours, "InputActionManager");
            Behaviour[] controllerManagers = FindByTypeName(behaviours, "ControllerInputActionManager");
            Behaviour[] dynamicMoveProviders = FindByTypeName(behaviours, "DynamicMoveProvider");
            Behaviour[] continuousMoveProviders = FindByTypeName(behaviours, "ContinuousMoveProvider");
            Behaviour[] continuousTurnProviders = FindByTypeName(behaviours, "ContinuousTurnProvider");
            Behaviour[] snapTurnProviders = FindByTypeName(behaviours, "SnapTurnProvider");
            Behaviour[] mediators = FindByTypeName(behaviours, "LocomotionMediator");
            Behaviour[] characterControllerDrivers = FindByTypeName(behaviours, "CharacterControllerDriver");
            CharacterController[] characterControllers = UnityEngine.Object.FindObjectsByType<CharacterController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var failures = new List<string>();
            bool restored = false;

            if (!string.Equals(SceneManager.GetActiveScene().name, FinalSceneName, StringComparison.Ordinal))
            {
                failures.Add("wrong_scene");
            }

            ExperimentTurnPolicy policy = ResolveTurnPolicy(controllerManagers, failures);

            foreach (Behaviour manager in inputManagers)
            {
                restored |= EnsureEnabled(manager);
                InvokePublic(manager, "EnableInput", failures);
            }

            foreach (Behaviour manager in controllerManagers)
            {
                restored |= EnsureEnabled(manager);
                if (!TryGetBoolProperty(manager, "smoothMotionEnabled", out bool smoothMotion) ||
                    !TryGetBoolProperty(manager, "smoothTurnEnabled", out bool smoothTurn))
                {
                    failures.Add("controller_policy_api_missing");
                    continue;
                }

                // Setting the public properties to their serialized values invokes
                // XRI's UpdateLocomotionActions and restores Move/Turn exclusivity.
                TrySetBoolProperty(manager, "smoothMotionEnabled", smoothMotion, failures);
                TrySetBoolProperty(manager, "smoothTurnEnabled", smoothTurn, failures);
            }

            Behaviour expectedMove = dynamicMoveProviders.FirstOrDefault() ?? continuousMoveProviders.FirstOrDefault();
            Behaviour[] allMoveProviders = dynamicMoveProviders.Concat(continuousMoveProviders).Distinct().ToArray();
            foreach (Behaviour provider in allMoveProviders)
            {
                restored |= SetEnabled(provider, provider == expectedMove);
            }

            foreach (Behaviour provider in continuousTurnProviders)
            {
                restored |= SetEnabled(provider, policy == ExperimentTurnPolicy.Continuous);
            }

            foreach (Behaviour provider in snapTurnProviders)
            {
                restored |= SetEnabled(provider, policy == ExperimentTurnPolicy.Snap);
            }

            foreach (Behaviour mediator in mediators) restored |= EnsureEnabled(mediator);
            foreach (Behaviour driver in characterControllerDrivers) restored |= EnsureEnabled(driver);
            foreach (CharacterController controller in characterControllers) restored |= EnsureEnabled(controller);

            ExperimentLocomotionStateReport report = Capture(source, xrGateReleased);
            report.WasRestored = restored;
            if (failures.Count > 0)
            {
                report.IsValid = false;
                report.FailureReason = JoinFailures(report.FailureReason, failures);
            }

            Log(report.IsValid ? "experiment_locomotion_state_restored" : "experiment_locomotion_state_invariant_failed", report, xrGateReleased);
            return report;
        }

        private static ExperimentLocomotionStateReport Capture(string source, bool xrGateReleased)
        {
            MonoBehaviour[] behaviours = FindBehaviours();
            Behaviour[] inputManagers = FindByTypeName(behaviours, "InputActionManager");
            Behaviour[] controllerManagers = FindByTypeName(behaviours, "ControllerInputActionManager");
            Behaviour[] dynamicMoveProviders = FindByTypeName(behaviours, "DynamicMoveProvider");
            Behaviour[] continuousMoveProviders = FindByTypeName(behaviours, "ContinuousMoveProvider");
            Behaviour[] continuousTurnProviders = FindByTypeName(behaviours, "ContinuousTurnProvider");
            Behaviour[] snapTurnProviders = FindByTypeName(behaviours, "SnapTurnProvider");
            Behaviour[] mediators = FindByTypeName(behaviours, "LocomotionMediator");
            CharacterController[] characterControllers = UnityEngine.Object.FindObjectsByType<CharacterController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var failures = new List<string>();
            ExperimentTurnPolicy policy = ResolveTurnPolicy(controllerManagers, failures);
            Behaviour expectedMove = dynamicMoveProviders.FirstOrDefault() ?? continuousMoveProviders.FirstOrDefault();
            Behaviour[] allMoveProviders = dynamicMoveProviders.Concat(continuousMoveProviders).Distinct().ToArray();
            List<InputAction> locomotionActions = GetLocomotionActions(inputManagers, failures);

            int enabledMoveActions = locomotionActions.Count(action => action.enabled && string.Equals(action.name, "Move", StringComparison.Ordinal));
            string expectedTurnAction = policy == ExperimentTurnPolicy.Continuous ? "Turn" : "Snap Turn";
            string incompatibleTurnAction = policy == ExperimentTurnPolicy.Continuous ? "Snap Turn" : "Turn";
            int enabledExpectedTurnActions = locomotionActions.Count(action => action.enabled && string.Equals(action.name, expectedTurnAction, StringComparison.Ordinal));
            int enabledIncompatibleTurnActions = locomotionActions.Count(action => action.enabled && string.Equals(action.name, incompatibleTurnAction, StringComparison.Ordinal));
            int enabledMoveProviders = CountEnabled(allMoveProviders);
            int enabledContinuousTurn = CountEnabled(continuousTurnProviders);
            int enabledSnapTurn = CountEnabled(snapTurnProviders);
            int enabledInputManagers = CountEnabled(inputManagers);
            int enabledControllers = characterControllers.Count(IsEnabledAndActive);
            int enabledMediators = CountEnabled(mediators);

            if (!Mathf.Approximately(Time.timeScale, 1f)) failures.Add("time_scale_not_one");
            if (ExperimentSimulationPauseAuthority.IsPauseActive) failures.Add("pause_owner_active");
            if (!xrGateReleased) failures.Add("xr_gate_not_released");
            if (expectedMove == null || enabledMoveProviders != 1 || !expectedMove.enabled) failures.Add("move_provider_invalid");
            if (policy == ExperimentTurnPolicy.Unknown) failures.Add("turn_policy_unknown");
            if (policy == ExperimentTurnPolicy.Continuous && (enabledContinuousTurn != 1 || enabledSnapTurn != 0)) failures.Add("continuous_turn_policy_invalid");
            if (policy == ExperimentTurnPolicy.Snap && (enabledSnapTurn != 1 || enabledContinuousTurn != 0)) failures.Add("snap_turn_policy_invalid");
            if (enabledInputManagers == 0) failures.Add("input_action_manager_disabled");
            if (enabledControllers == 0) failures.Add("character_controller_disabled");
            if (enabledMediators == 0) failures.Add("locomotion_mediator_disabled");
            if (enabledMoveActions == 0) failures.Add("move_action_disabled");
            if (enabledExpectedTurnActions == 0) failures.Add("expected_turn_action_disabled");
            if (enabledIncompatibleTurnActions != 0) failures.Add("incompatible_turn_action_enabled");

            return new ExperimentLocomotionStateReport
            {
                Source = source ?? string.Empty,
                TurnPolicy = policy,
                IsValid = failures.Count == 0,
                FailureReason = string.Join("|", failures.Distinct()),
                ProviderStates = DescribeProviders(behaviours),
                InputActionStates = DescribeInputActions(locomotionActions),
                EnabledMoveProviders = enabledMoveProviders,
                EnabledContinuousTurnProviders = enabledContinuousTurn,
                EnabledSnapTurnProviders = enabledSnapTurn,
                EnabledInputActionManagers = enabledInputManagers,
                EnabledCharacterControllers = enabledControllers,
                EnabledLocomotionMediators = enabledMediators,
                EnabledMoveActions = enabledMoveActions,
                EnabledExpectedTurnActions = enabledExpectedTurnActions,
                EnabledIncompatibleTurnActions = enabledIncompatibleTurnActions
            };
        }

        private static ExperimentTurnPolicy ResolveTurnPolicy(Behaviour[] managers, List<string> failures)
        {
            if (managers == null || managers.Length == 0)
            {
                failures?.Add("controller_input_action_manager_missing");
                return ExperimentTurnPolicy.Unknown;
            }

            var policies = new List<bool>();
            foreach (Behaviour manager in managers.Where(manager => manager != null))
            {
                if (TryGetBoolProperty(manager, "smoothTurnEnabled", out bool smoothTurn))
                {
                    policies.Add(smoothTurn);
                }
            }

            bool[] distinct = policies.Distinct().ToArray();
            if (distinct.Length != 1)
            {
                failures?.Add("controller_turn_policy_mismatch");
                return ExperimentTurnPolicy.Unknown;
            }

            return distinct[0] ? ExperimentTurnPolicy.Continuous : ExperimentTurnPolicy.Snap;
        }

        private static List<InputAction> GetLocomotionActions(IEnumerable<Behaviour> inputManagers, List<string> failures)
        {
            var assets = new List<InputActionAsset>();
            foreach (Behaviour manager in inputManagers)
            {
                PropertyInfo property = manager.GetType().GetProperty("actionAssets", BindingFlags.Instance | BindingFlags.Public);
                if (property?.GetValue(manager) is not IEnumerable values)
                {
                    failures?.Add("input_action_assets_api_missing");
                    continue;
                }

                foreach (object value in values)
                {
                    if (value is InputActionAsset asset && asset != null && !assets.Contains(asset)) assets.Add(asset);
                }
            }

            return assets.SelectMany(asset => asset.actionMaps)
                .Where(map => map.name.IndexOf("Locomotion", StringComparison.OrdinalIgnoreCase) >= 0)
                .SelectMany(map => map.actions)
                .ToList();
        }

        private static void InvokePublic(Behaviour target, string methodName, List<string> failures)
        {
            MethodInfo method = target?.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (method == null)
            {
                failures.Add(target?.GetType().Name + "_" + methodName + "_missing");
                return;
            }

            method.Invoke(target, null);
        }

        private static bool TryGetBoolProperty(Behaviour target, string propertyName, out bool value)
        {
            value = false;
            PropertyInfo property = target?.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || property.PropertyType != typeof(bool) || !property.CanRead) return false;
            value = (bool)property.GetValue(target);
            return true;
        }

        private static void TrySetBoolProperty(Behaviour target, string propertyName, bool value, List<string> failures)
        {
            PropertyInfo property = target?.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || property.PropertyType != typeof(bool) || !property.CanWrite)
            {
                failures.Add(target?.GetType().Name + "_" + propertyName + "_write_missing");
                return;
            }
            property.SetValue(target, value);
        }

        private static MonoBehaviour[] FindBehaviours() =>
            UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        private static Behaviour[] FindByTypeName(IEnumerable<MonoBehaviour> behaviours, string typeName) =>
            behaviours.Where(component => component != null && string.Equals(component.GetType().Name, typeName, StringComparison.Ordinal)).Cast<Behaviour>().ToArray();

        private static int CountEnabled(IEnumerable<Behaviour> behaviours) => behaviours.Count(IsEnabledAndActive);
        private static bool IsEnabledAndActive(Behaviour behaviour) => behaviour != null && behaviour.enabled && behaviour.gameObject.activeInHierarchy;
        private static bool IsEnabledAndActive(CharacterController controller) => controller != null && controller.enabled && controller.gameObject.activeInHierarchy;

        private static bool EnsureEnabled(Behaviour behaviour) => SetEnabled(behaviour, true);
        private static bool EnsureEnabled(CharacterController controller)
        {
            if (controller == null || controller.enabled) return false;
            controller.enabled = true;
            return true;
        }

        private static bool SetEnabled(Behaviour behaviour, bool enabled)
        {
            if (behaviour == null || behaviour.enabled == enabled) return false;
            behaviour.enabled = enabled;
            return true;
        }

        private static string DescribeProviders(IEnumerable<MonoBehaviour> behaviours) =>
            string.Join(" || ", behaviours.Where(component => component != null && DiagnosticTypeNames.Contains(component.GetType().Name))
                .Select(component => $"{component.GetType().FullName}:{GetPath(component.transform)}:enabled={component.enabled}:active={component.gameObject.activeInHierarchy}"));

        private static string DescribeInputActions(IEnumerable<InputAction> actions) =>
            string.Join(" || ", actions.Select(action => $"{action.actionMap?.name}/{action.name}:enabled={action.enabled}:phase={action.phase}"));

        private static string JoinFailures(string existing, IEnumerable<string> additions) =>
            string.Join("|", (existing ?? string.Empty).Split('|').Concat(additions ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());

        private static void Log(string eventType, ExperimentLocomotionStateReport report, bool xrGateReleased)
        {
            Dictionary<string, object> payload = ExperimentSimulationPauseAuthority.BuildOwnerSnapshot(report.Source);
            payload["xr_gate_released"] = xrGateReleased;
            payload["turn_policy"] = report.TurnPolicy.ToString().ToLowerInvariant();
            payload["valid"] = report.IsValid;
            payload["restored"] = report.WasRestored;
            payload["failure_reason"] = report.FailureReason;
            payload["provider_states"] = report.ProviderStates;
            payload["input_action_states"] = report.InputActionStates;
            payload["enabled_move_providers"] = report.EnabledMoveProviders;
            payload["enabled_continuous_turn_providers"] = report.EnabledContinuousTurnProviders;
            payload["enabled_snap_turn_providers"] = report.EnabledSnapTurnProviders;
            payload["enabled_input_action_managers"] = report.EnabledInputActionManagers;
            payload["enabled_character_controllers"] = report.EnabledCharacterControllers;
            payload["enabled_locomotion_mediators"] = report.EnabledLocomotionMediators;
            payload["enabled_move_actions"] = report.EnabledMoveActions;
            payload["enabled_expected_turn_actions"] = report.EnabledExpectedTurnActions;
            payload["enabled_incompatible_turn_actions"] = report.EnabledIncompatibleTurnActions;
            payload["participant_id"] = ExperimentDataPathResolver.CurrentParticipantId;
            payload["session_id"] = ExperimentDataPathResolver.CurrentSessionId;
            payload["realtime"] = Time.realtimeSinceStartupAsDouble.ToString("0.000", CultureInfo.InvariantCulture);
            TiagoExperimentTelemetry.LogEvent(eventType, payload);

            string message = $"[P46O-04] {eventType} | scene={SceneManager.GetActiveScene().name} source={report.Source} time_scale={Time.timeScale:0.###} global_pause_active={ExperimentSimulationPauseAuthority.IsPauseActive} pending_pause_owners={ExperimentSimulationPauseAuthority.ActiveOwnerCount} owners={ExperimentSimulationPauseAuthority.ActiveOwners} xr_gate_released={xrGateReleased} turn_policy={report.TurnPolicy} valid={report.IsValid} restored={report.WasRestored} failure_reason={report.FailureReason} providers={report.ProviderStates} input_actions={report.InputActionStates} character_controllers_enabled={report.EnabledCharacterControllers} frame={Time.frameCount} realtime={Time.realtimeSinceStartupAsDouble:0.000}";
            if (report.IsValid || !string.Equals(eventType, "experiment_locomotion_state_invariant_failed", StringComparison.Ordinal)) Debug.Log(message);
            else Debug.LogError(message);
        }

        private static string GetPath(Transform transform)
        {
            if (transform == null) return string.Empty;
            string path = transform.name;
            for (Transform parent = transform.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
            return path;
        }
    }
}
