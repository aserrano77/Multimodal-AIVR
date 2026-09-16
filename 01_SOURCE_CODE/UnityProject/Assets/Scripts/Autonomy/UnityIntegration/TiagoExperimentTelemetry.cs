using System;
using System.Collections.Generic;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Ultima muestra observable de locomocion para comparar autonomia y teleoperacion.
    /// Es intencionadamente simple: productor unico por tick, logger desacoplado.
    /// </summary>
    public static class TiagoExperimentTelemetry
    {
        public delegate void StructuredEventHandler(string eventType, Dictionary<string, object> payload, float unityTime);

        private static Snapshot _latest = Snapshot.Empty;
        private static int _latestEffectiveFrame = -1;
        private static string _autonomousLocomotionMode = "None";
        private static bool _autonomousActiveCorner;
        private static float _autonomousObstacleFront;

        public static Snapshot Latest => _latest;
        public static event StructuredEventHandler StructuredEventLogged;

        public static void PublishCommand(
            string commandSource,
            bool commandApplied,
            Vector3 target,
            bool hasTarget,
            float remainingDistance,
            float linearCommand,
            float angularCommand,
            TiagoDifferentialDriveBridge.DriveDiagnostics drive,
            string locomotionMode,
            bool activeCorner,
            float obstacleFront,
            string activePathSource = "",
            Vector3 activeLookahead = default,
            Vector3 activeProjected = default,
            int activeSegmentIndex = -1,
            float angleErrorDeg = float.NaN,
            string activeDriveProfile = "",
            string activeAutonomousPolicy = "",
            float activeLookaheadDistance = float.NaN,
            float distanceToNextCorner = float.NaN,
            float nextCornerAngleDeg = float.NaN,
            string speedReductionReason = "")
        {
            if (commandSource == "Autonomous")
            {
                _autonomousLocomotionMode = locomotionMode;
                _autonomousActiveCorner = activeCorner;
                _autonomousObstacleFront = obstacleFront;
            }

            string commandEffect = ResolveCommandEffect(commandSource, commandApplied);
            if (!commandApplied && _latestEffectiveFrame == Time.frameCount)
            {
                _latest = _latest.WithAutonomousState(_autonomousLocomotionMode, _autonomousActiveCorner, _autonomousObstacleFront);
                return;
            }

            _latest = new Snapshot(
                Time.time,
                TiagoLocomotionControlGate.ActiveControlMode,
                commandSource,
                commandApplied,
                commandEffect,
                target,
                hasTarget,
                remainingDistance,
                linearCommand,
                angularCommand,
                drive,
                locomotionMode,
                activeCorner,
                obstacleFront,
                _autonomousLocomotionMode,
                _autonomousActiveCorner,
                _autonomousObstacleFront,
                activePathSource,
                activeLookahead,
                activeProjected,
                activeSegmentIndex,
                angleErrorDeg,
                activeDriveProfile,
                activeAutonomousPolicy,
                activeLookaheadDistance,
                distanceToNextCorner,
                nextCornerAngleDeg,
                speedReductionReason);

            if (commandApplied)
            {
                _latestEffectiveFrame = Time.frameCount;
            }
        }

        public static void RecordEvent(string eventType, string payload)
        {
            TiagoExperimentLogger logger = TiagoExperimentLogger.Active;
            if (logger == null || logger.IsRunInitializationFailed)
            {
                return;
            }

            logger.RecordEvent(eventType, payload);
        }

        public static bool LogEvent(string eventType, Dictionary<string, object> payload)
        {
            payload ??= new Dictionary<string, object>();
            ExperimentInstrumentationController.EnrichPayloadWithActiveContext(payload);
            bool hasAuthoritativeIdentity = ExperimentDataPathResolver.TryStampPayloadWithCurrentSessionIdentity(
                payload,
                out string identityError);
            float unityTime = Time.time;
            bool persisted = false;

            TiagoExperimentLogger logger = TiagoExperimentLogger.Active;
            if (logger != null && !logger.IsRunInitializationFailed)
            {
                if (!hasAuthoritativeIdentity)
                {
                    Debug.Log(
                        $"[TiagoExperimentTelemetry] session_event_not_persisted_pre_session | event_type={eventType ?? string.Empty} reason={identityError}");
                }
                else
                {
                    logger.LogEvent(eventType, payload);
                    persisted = !logger.IsRunInitializationFailed;
                }
            }

            NotifyStructuredEventSubscribers(eventType, payload, unityTime);
            return persisted;
        }

        private static void NotifyStructuredEventSubscribers(
            string eventType,
            Dictionary<string, object> payload,
            float unityTime)
        {
            StructuredEventHandler subscribers = StructuredEventLogged;
            if (subscribers == null)
            {
                return;
            }

            foreach (Delegate subscriber in subscribers.GetInvocationList())
            {
                try
                {
                    ((StructuredEventHandler)subscriber)(eventType, payload, unityTime);
                }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[TiagoExperimentTelemetry] structured_event_subscriber_failed | event_type={eventType ?? string.Empty} subscriber={subscriber.Method.DeclaringType?.FullName}.{subscriber.Method.Name} error={ex}");
                }
            }
        }

        public readonly struct Snapshot
        {
            public static readonly Snapshot Empty = new(
                0f,
                "Autonomous",
                "None",
                false,
                "None",
                Vector3.zero,
                false,
                float.NaN,
                0f,
                0f,
                default,
                "None",
                false,
                0f,
                "None",
                false,
                0f,
                "",
                Vector3.zero,
                Vector3.zero,
                -1,
                float.NaN,
                "",
                "",
                float.NaN,
                float.NaN,
                float.NaN,
                "");

            public float UnityTime { get; }
            public string ControlMode { get; }
            public string CommandSource { get; }
            public bool CommandApplied { get; }
            public string CommandEffect { get; }
            public Vector3 Target { get; }
            public bool HasTarget { get; }
            public float RemainingDistance { get; }
            public float LinearCommand { get; }
            public float AngularCommand { get; }
            public TiagoDifferentialDriveBridge.DriveDiagnostics Drive { get; }
            public string LocomotionMode { get; }
            public bool ActiveCorner { get; }
            public float ObstacleFront { get; }
            public string AutonomousLocomotionMode { get; }
            public bool AutonomousActiveCorner { get; }
            public float AutonomousObstacleFront { get; }
            public string ActivePathSource { get; }
            public Vector3 ActiveLookahead { get; }
            public Vector3 ActiveProjected { get; }
            public int ActiveSegmentIndex { get; }
            public float AngleErrorDeg { get; }
            public string ActiveDriveProfile { get; }
            public string ActiveAutonomousPolicy { get; }
            public float ActiveLookaheadDistance { get; }
            public float DistanceToNextCorner { get; }
            public float NextCornerAngleDeg { get; }
            public string SpeedReductionReason { get; }

            public Snapshot(
                float unityTime,
                string controlMode,
                string commandSource,
                bool commandApplied,
                string commandEffect,
                Vector3 target,
                bool hasTarget,
                float remainingDistance,
                float linearCommand,
                float angularCommand,
                TiagoDifferentialDriveBridge.DriveDiagnostics drive,
                string locomotionMode,
                bool activeCorner,
                float obstacleFront,
                string autonomousLocomotionMode,
                bool autonomousActiveCorner,
                float autonomousObstacleFront,
                string activePathSource,
                Vector3 activeLookahead,
                Vector3 activeProjected,
                int activeSegmentIndex,
                float angleErrorDeg,
                string activeDriveProfile,
                string activeAutonomousPolicy,
                float activeLookaheadDistance,
                float distanceToNextCorner,
                float nextCornerAngleDeg,
                string speedReductionReason)
            {
                UnityTime = unityTime;
                ControlMode = controlMode;
                CommandSource = commandSource;
                CommandApplied = commandApplied;
                CommandEffect = commandEffect;
                Target = target;
                HasTarget = hasTarget;
                RemainingDistance = remainingDistance;
                LinearCommand = linearCommand;
                AngularCommand = angularCommand;
                Drive = drive;
                LocomotionMode = locomotionMode;
                ActiveCorner = activeCorner;
                ObstacleFront = obstacleFront;
                AutonomousLocomotionMode = autonomousLocomotionMode;
                AutonomousActiveCorner = autonomousActiveCorner;
                AutonomousObstacleFront = autonomousObstacleFront;
                ActivePathSource = activePathSource;
                ActiveLookahead = activeLookahead;
                ActiveProjected = activeProjected;
                ActiveSegmentIndex = activeSegmentIndex;
                AngleErrorDeg = angleErrorDeg;
                ActiveDriveProfile = activeDriveProfile;
                ActiveAutonomousPolicy = activeAutonomousPolicy;
                ActiveLookaheadDistance = activeLookaheadDistance;
                DistanceToNextCorner = distanceToNextCorner;
                NextCornerAngleDeg = nextCornerAngleDeg;
                SpeedReductionReason = speedReductionReason;
            }

            public Snapshot WithAutonomousState(string autonomousLocomotionMode, bool autonomousActiveCorner, float autonomousObstacleFront)
            {
                return new Snapshot(
                    UnityTime,
                    ControlMode,
                    CommandSource,
                    CommandApplied,
                    CommandEffect,
                    Target,
                    HasTarget,
                    RemainingDistance,
                    LinearCommand,
                    AngularCommand,
                    Drive,
                    LocomotionMode,
                    ActiveCorner,
                    ObstacleFront,
                    autonomousLocomotionMode,
                    autonomousActiveCorner,
                    autonomousObstacleFront,
                    ActivePathSource,
                    ActiveLookahead,
                    ActiveProjected,
                    ActiveSegmentIndex,
                    AngleErrorDeg,
                    ActiveDriveProfile,
                    ActiveAutonomousPolicy,
                    ActiveLookaheadDistance,
                    DistanceToNextCorner,
                    NextCornerAngleDeg,
                    SpeedReductionReason);
            }
        }

        private static string ResolveCommandEffect(string commandSource, bool commandApplied)
        {
            if (commandApplied)
            {
                return commandSource == "Manual" ? "ManualEffective" : "AutonomousEffective";
            }

            return commandSource == "Autonomous" ? "AutonomousComputedOnly" : "ManualNotApplied";
        }
    }

    /// <summary>
    /// Runtime-only policy for high-frequency legacy diagnostics on standalone Android.
    /// Experimental telemetry, warnings, errors, and exceptions remain unaffected.
    /// </summary>
    public static class QuestLoggingPolicy
    {
        public static bool EmitLegacyContinuousDiagnostics =>
            ShouldEmitLegacyContinuousDiagnostics(Application.platform, Application.isEditor);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ConfigureAndroidStackTraces()
        {
            if (!IsAndroidPlayer(Application.platform, Application.isEditor))
            {
                return;
            }

            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        }

        public static bool ShouldEmitLegacyContinuousDiagnostics(RuntimePlatform platform, bool isEditor)
        {
            return !IsAndroidPlayer(platform, isEditor);
        }

        public static StackTraceLogType ResolveStackTraceLogType(
            LogType logType,
            RuntimePlatform platform,
            bool isEditor,
            StackTraceLogType configuredType)
        {
            return IsAndroidPlayer(platform, isEditor) && logType == LogType.Log
                ? StackTraceLogType.None
                : configuredType;
        }

        private static bool IsAndroidPlayer(RuntimePlatform platform, bool isEditor)
        {
            return platform == RuntimePlatform.Android && !isEditor;
        }
    }
}
