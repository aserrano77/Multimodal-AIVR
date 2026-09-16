using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autonomy.Domain;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Logger persistente para comparar ejecuciones autonomas y manuales sin depender de la consola.
    /// Escribe muestras periodicas en CSV y eventos significativos en JSONL.
    /// </summary>
    public sealed class TiagoExperimentLogger : MonoBehaviour
    {
        private const string LogPrefix = "[TiagoExperimentLogger]";
        private const string NotYetAssignedMetadataValue = "not_yet_assigned";

        [Header("Run")]
        [SerializeField] private bool _startOnAwake = true;
        [SerializeField] private string _runLabel = "tiago_run";

        [Header("Sampling")]
        [SerializeField] private Transform _robotReference;
        [SerializeField] private Transform _fallbackTargetReference;
        [SerializeField] private float _sampleIntervalSeconds = 0.25f;

        public static TiagoExperimentLogger Active { get; private set; }

        private StreamWriter _sampleWriter;
        private StreamWriter _eventWriter;
        private string _runId;
        private string _runDirectory;
        private string _samplePath;
        private string _eventPath;
        private string _manifestPath;
        private ExperimentSessionIdentity _boundIdentity;
        private Dictionary<string, object> _manifestPayload;
        private bool _runRequested;
        private bool _runInitializationFailed;
        private bool _manifestNavigationMetadataResolved;
        private string _runInitializationFailure;
        private string _runStartReason = "manual";
        private float _runStartedAt;
        private float _nextSampleTime;
        private float _nextRuntimeDiagnosticTime;
        private float _nextHighAngleEventTime;
        private bool _hasPreviousPose;
        private Vector3 _previousPosition;
        private float _previousYawDeg;
        private float _previousSampleTime;
        private bool _hasPreviousDiagnosticPose;
        private Vector3 _previousDiagnosticPosition;
        private float _previousDiagnosticYawDeg;
        private float _previousDiagnosticTime;
        private bool _hasLastValidRunSnapshot;
        private float _lastValidUnityTime;
        private float _lastValidRunElapsedTime;
        private Vector3 _lastValidRobotPosition;
        private float _lastValidRemainingDistance = float.NaN;
        private bool _navigationStartupActive;
        private bool _navigationStartupSummaryEmitted;
        private float _navigationStartupStartedAt;
        private float _startupMaxAbsAngleErrorDeg;
        private int _startupVelocitySignChanges;
        private int _startupAngularSignChanges;
        private int _startupForwardReverseSwitches;
        private int _startupPreviousVSign;
        private int _startupPreviousWSign;
        private float _startupMinObservedV;
        private float _startupMaxObservedV;
        private float _startupMaxAbsWCmd;
        private Vector3 _startupFirstLookahead;
        private string _startupFirstPathSource;
        private float _leftWheelNotFollowingSince = float.NegativeInfinity;
        private float _rightWheelNotFollowingSince = float.NegativeInfinity;
        private Transform _leftWheelTransform;
        private Transform _rightWheelTransform;
        private readonly List<PendingEvent> _pendingEvents = new();

        public string RunId => _runId;
        public string RunDirectory => _runDirectory;
        public string SamplePath => _samplePath;
        public string EventPath => _eventPath;
        public string ManifestPath => _manifestPath;
        public string ParticipantId => _boundIdentity?.ParticipantId ?? string.Empty;
        public string SessionId => _boundIdentity?.SessionId ?? string.Empty;
        public string SessionRoot => _boundIdentity?.SessionRoot ?? string.Empty;
        public bool IsRunInitializationFailed => _runInitializationFailed;
        public Transform RobotReference => _robotReference;

        private void Awake()
        {
            if (Active != null && Active != this)
            {
                Debug.LogWarning($"{LogPrefix} Another logger is already active. Disabling duplicate on '{name}'.", this);
                enabled = false;
                return;
            }

            Active = this;
            ValidateConfiguration();

            if (_startOnAwake)
            {
                StartRun("awake");
            }
        }

        private void Update()
        {
            EnsureRunFilesReadyFromSnapshot();

            if (_sampleWriter == null)
            {
                return;
            }

            if (Time.time >= _nextRuntimeDiagnosticTime)
            {
                WriteRuntimeDiagnostics();
                _nextRuntimeDiagnosticTime = Time.time + ResolveRuntimeDiagnosticIntervalSeconds();
            }

            if (Time.time >= _nextSampleTime)
            {
                WriteSample();
                _nextSampleTime = Time.time + Mathf.Max(0.05f, _sampleIntervalSeconds);
            }
        }

        private void OnDestroy()
        {
            StopRun("destroy");

            if (Active == this)
            {
                Active = null;
            }
        }

        private void EnsureRunFilesReadyFromSnapshot()
        {
            if (!_runRequested || _runInitializationFailed)
            {
                return;
            }

            TiagoExperimentTelemetry.Snapshot snapshot = TiagoExperimentTelemetry.Latest;
            if (_eventWriter != null)
            {
                TryEnrichManifestFromNavigationSnapshot(snapshot);
                return;
            }

            if (!snapshot.HasTarget)
            {
                return;
            }

            if (!IsResolvedMetadataValue(snapshot.ActiveDriveProfile) ||
                !IsResolvedMetadataValue(snapshot.ActiveAutonomousPolicy))
            {
                return;
            }

            TryCreateRunFilesFromMetadata(new ExperimentLogMetadata(
                ResolveSceneName(),
                snapshot.ActiveDriveProfile,
                snapshot.ActiveAutonomousPolicy,
                snapshot.Target));
        }

        private bool TryCreateRunFilesFromMetadata(ExperimentLogMetadata metadata)
        {
            if (!_runRequested || _eventWriter != null || _runInitializationFailed || !metadata.IsValid)
            {
                return _eventWriter != null;
            }

            if (!TryValidateBoundIdentity(out string identityError))
            {
                _runInitializationFailed = true;
                _runInitializationFailure = identityError;
                Debug.LogError($"{LogPrefix} session_write_invariant_failed | operation=create_run_files error_reason={identityError} participant_id={ParticipantId} session_id={SessionId} session_root={SessionRoot}", this);
                return false;
            }

            try
            {
                string experimentsRoot = ExperimentDataPathResolver.ResolveDataRoot();
                Directory.CreateDirectory(experimentsRoot);
                string sessionNamingRoot = _boundIdentity.SessionRoot;
                Directory.CreateDirectory(sessionNamingRoot);

                ExperimentLogNameParts nameParts = BuildExperimentLogNameParts(sessionNamingRoot, metadata);
                try
                {
                    InitializeRunFiles(experimentsRoot, metadata, ref nameParts);
                }
                catch (Exception ex) when (ex is PathTooLongException || ex is IOException)
                {
                    CloseWritersDefensively();
                    DeleteEmptyRunDirectoryDefensively();
                    nameParts = BuildFallbackExperimentLogNameParts(sessionNamingRoot, metadata, nameParts, ex.Message);
                    InitializeRunFiles(experimentsRoot, metadata, ref nameParts);
                }

                _manifestNavigationMetadataResolved = metadata.HasTarget &&
                    IsResolvedMetadataValue(metadata.DriveProfile) &&
                    IsResolvedMetadataValue(metadata.AutonomyPolicy);
                ResetRunSamplingState();

                Debug.Log($"{LogPrefix} Run files created | runId={_runId} dir={_runDirectory}", this);
                LogEvent("experiment_log_files_created", BuildLogFilesCreatedPayload(metadata, nameParts));
                LogEvent("experiment_run_started", BuildRunStartedPayload(_runStartReason, _samplePath, _eventPath, metadata));
                RecordEvent("run_started", $"reason={_runStartReason} directory={_runDirectory} samples={_samplePath} events={_eventPath}");
                RecordEvent("run_files_ready", $"runId={_runId} directory={_runDirectory} samples={_samplePath} events={_eventPath} manifest={_manifestPath}");
                FlushPendingEvents();
                return true;
            }
            catch (Exception ex)
            {
                _runInitializationFailed = true;
                _runInitializationFailure = ex.Message;
                CloseWritersDefensively();
                DeleteEmptyRunDirectoryDefensively();
                _pendingEvents.Clear();
                Debug.LogError($"{LogPrefix} Failed to create run files. Logger disabled for this run; no further attempts will be made. | runId={_runId} directory={_runDirectory} error={ex.Message}", this);
                return false;
            }
        }

        private void InitializeRunFiles(string experimentsRoot, ExperimentLogMetadata metadata, ref ExperimentLogNameParts nameParts)
        {
            _runDirectory = _boundIdentity.SessionRoot;
            Directory.CreateDirectory(_runDirectory);

            try
            {
                CreateRunFiles(metadata, nameParts);
            }
            catch (Exception ex) when ((ex is PathTooLongException || ex is IOException) && !nameParts.FileNameFallbackUsed)
            {
                CloseWritersDefensively();
                DeletePartialRunFilesDefensively();
                nameParts = nameParts.WithFileNameFallback(BuildFallbackFilePrefix(metadata, nameParts.AttemptNumber), ex.Message);
                CreateRunFiles(metadata, nameParts);
            }
        }

        private void CreateRunFiles(ExperimentLogMetadata metadata, ExperimentLogNameParts nameParts)
        {
            _samplePath = Path.Combine(_runDirectory, nameParts.SamplesFileName);
            _eventPath = Path.Combine(_runDirectory, nameParts.EventsFileName);
            _manifestPath = Path.Combine(_runDirectory, nameParts.ManifestFileName);

            _sampleWriter = new StreamWriter(_samplePath, false, Encoding.UTF8);
            _eventWriter = new StreamWriter(_eventPath, false, Encoding.UTF8);
            _sampleWriter.WriteLine(GetSampleHeader());
            _sampleWriter.Flush();
            _manifestPayload = BuildManifestPayload(metadata, nameParts);
            if (!TryValidateManifestOrderForIdentity(_manifestPayload, _boundIdentity, null, out string manifestError))
            {
                throw new InvalidOperationException("session_manifest_invariant_failed:" + manifestError);
            }

            File.WriteAllText(
                _manifestPath,
                SerializeJsonObject(_manifestPayload) + Environment.NewLine,
                Encoding.UTF8);
            ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.CurrentSession;
            string[] sessionExportConditionOrder = ResolveSessionExportConditionOrder(
                context.ConditionOrder,
                metadata.ActiveConditionPlan,
                out bool preservedAuthoritativeSessionOrder);
            ExperimentDataPathResolver.WriteSessionExportInfo(
                _runDirectory,
                context.SessionId,
                context.ParticipantId,
                sessionExportConditionOrder,
                context.QuestionnaireCode,
                context.QuestionnaireCodeScheme);
            if (preservedAuthoritativeSessionOrder)
            {
                LogEvent("p46j03r1_session_export_condition_order_preserved", new Dictionary<string, object>
                {
                    ["session_id"] = context.SessionId,
                    ["participant_id"] = context.ParticipantId,
                    ["authoritative_condition_order"] = sessionExportConditionOrder,
                    ["runtime_logger_condition_plan"] = metadata.ActiveConditionPlan,
                    ["reason"] = "active_session_context_is_authoritative"
                });
            }
            LogEvent("p45f_session_manifest_created", new Dictionary<string, object>
            {
                ["run_id"] = _runId,
                ["participant_id"] = context.ParticipantId,
                ["session_id"] = context.SessionId,
                ["session_root"] = _runDirectory,
                ["persistent_root"] = Application.persistentDataPath,
                ["file_path"] = _manifestPath,
                ["platform"] = Application.platform.ToString(),
                ["package_name"] = Application.identifier
            });
            ExperimentDataPathResolver.UpdateFileIndex(_runDirectory);
        }

        private static string[] ResolveSessionExportConditionOrder(
            IReadOnlyList<string> sessionConditionOrder,
            IReadOnlyList<string> runtimeLoggerConditionPlan,
            out bool preservedAuthoritativeSessionOrder)
        {
            string[] authoritativeOrder = CopyConditionOrder(sessionConditionOrder);
            string[] runtimeOrder = CopyConditionOrder(runtimeLoggerConditionPlan);
            if (ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(authoritativeOrder))
            {
                preservedAuthoritativeSessionOrder = !ConditionOrdersMatch(authoritativeOrder, runtimeOrder);
                return authoritativeOrder;
            }

            preservedAuthoritativeSessionOrder = false;
            return runtimeOrder;
        }

        public static string[] ResolveSessionExportConditionOrderForDiagnostics(
            IReadOnlyList<string> sessionConditionOrder,
            IReadOnlyList<string> runtimeLoggerConditionPlan,
            out bool preservedAuthoritativeSessionOrder)
        {
            return ResolveSessionExportConditionOrder(
                sessionConditionOrder,
                runtimeLoggerConditionPlan,
                out preservedAuthoritativeSessionOrder);
        }

        private static string[] CopyConditionOrder(IReadOnlyList<string> source)
        {
            if (source == null || source.Count == 0)
            {
                return Array.Empty<string>();
            }

            var copy = new List<string>(source.Count);
            for (int index = 0; index < source.Count; index++)
            {
                if (!string.IsNullOrWhiteSpace(source[index]))
                {
                    copy.Add(source[index].Trim());
                }
            }

            return copy.ToArray();
        }

        private static bool ConditionOrdersMatch(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            for (int index = 0; index < left.Count; index++)
            {
                if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public static bool TryValidateManifestOrderForDiagnostics(
            Dictionary<string, object> manifestPayload,
            IReadOnlyList<string> authoritativeOrder,
            string questionnaireCode,
            string questionnaireCodeScheme,
            Dictionary<string, object> trialPayload,
            out string errorReason)
        {
            return TryValidateManifestOrder(
                manifestPayload,
                authoritativeOrder,
                questionnaireCode,
                questionnaireCodeScheme,
                trialPayload,
                out errorReason);
        }

        public bool TryValidateManifestJsonForActiveSession(string manifestJson, out string errorReason)
        {
            errorReason = string.Empty;
            if (!TryValidateBoundIdentity(out errorReason))
            {
                return false;
            }

            if (!TryValidateManifestOrderForIdentity(_manifestPayload, _boundIdentity, null, out errorReason))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(manifestJson))
            {
                errorReason = "manifest_json_missing";
                return false;
            }

            string expectedOrderJson = SerializeJsonValue(_boundIdentity.CopyConditionOrderIds());
            foreach (string field in ManifestOrderFields)
            {
                if (manifestJson.IndexOf($"\"{field}\":{expectedOrderJson}", StringComparison.Ordinal) < 0)
                {
                    errorReason = "manifest_json_" + field + "_mismatch";
                    return false;
                }
            }

            if (manifestJson.IndexOf(
                    $"\"questionnaire_code\":\"{EscapeJson(_boundIdentity.QuestionnaireCode)}\"",
                    StringComparison.Ordinal) < 0)
            {
                errorReason = "manifest_json_questionnaire_code_mismatch";
                return false;
            }

            if (manifestJson.IndexOf(
                    $"\"questionnaire_code_scheme\":\"{EscapeJson(_boundIdentity.QuestionnaireCodeScheme)}\"",
                    StringComparison.Ordinal) < 0)
            {
                errorReason = "manifest_json_questionnaire_code_scheme_mismatch";
                return false;
            }

            return true;
        }

        private static readonly string[] ManifestOrderFields =
        {
            "condition_order_ids",
            "condition_order",
            "conditions",
            "active_condition_plan"
        };

        private static bool TryValidateManifestOrderForIdentity(
            Dictionary<string, object> manifestPayload,
            ExperimentSessionIdentity identity,
            Dictionary<string, object> trialPayload,
            out string errorReason)
        {
            if (identity == null)
            {
                errorReason = "manifest_identity_missing";
                return false;
            }

            return TryValidateManifestOrder(
                manifestPayload,
                identity.ConditionOrderIds,
                identity.QuestionnaireCode,
                identity.QuestionnaireCodeScheme,
                trialPayload,
                out errorReason);
        }

        private static bool TryValidateManifestOrder(
            Dictionary<string, object> manifestPayload,
            IReadOnlyList<string> authoritativeOrder,
            string questionnaireCode,
            string questionnaireCodeScheme,
            Dictionary<string, object> trialPayload,
            out string errorReason)
        {
            errorReason = string.Empty;
            string[] expectedOrder = CopyConditionOrder(authoritativeOrder);
            if (!ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(expectedOrder))
            {
                errorReason = "authoritative_condition_order_invalid";
                return false;
            }

            if (manifestPayload == null)
            {
                errorReason = "manifest_payload_missing";
                return false;
            }

            foreach (string field in ManifestOrderFields)
            {
                if (!TryReadManifestOrder(manifestPayload, field, out string[] manifestOrder) ||
                    !ConditionOrdersMatch(expectedOrder, manifestOrder))
                {
                    errorReason = "manifest_" + field + "_mismatch";
                    return false;
                }
            }

            string manifestCode = GetManifestString(manifestPayload, "questionnaire_code");
            string manifestScheme = GetManifestString(manifestPayload, "questionnaire_code_scheme");
            if (!string.Equals(manifestCode, questionnaireCode ?? string.Empty, StringComparison.Ordinal))
            {
                errorReason = "manifest_questionnaire_code_mismatch";
                return false;
            }

            if (!string.Equals(manifestScheme, questionnaireCodeScheme ?? string.Empty, StringComparison.Ordinal))
            {
                errorReason = "manifest_questionnaire_code_scheme_mismatch";
                return false;
            }

            if (string.Equals(manifestScheme, QuestionnaireCodeCodec.Scheme, StringComparison.Ordinal))
            {
                if (!QuestionnaireCodeCodec.TryDecodeOrder(
                        manifestCode,
                        out string[] decodedOrder,
                        out _,
                        out string codeError) ||
                    !ConditionOrdersMatch(expectedOrder, decodedOrder))
                {
                    errorReason = "manifest_questionnaire_code_order_mismatch:" + codeError;
                    return false;
                }
            }
            else if (!string.IsNullOrWhiteSpace(manifestScheme))
            {
                errorReason = "manifest_questionnaire_code_scheme_unknown";
                return false;
            }

            if (trialPayload != null &&
                TryGetPayloadString(trialPayload, "condition_id", out string trialConditionId) &&
                IsCanonicalFinalConditionId(trialConditionId) &&
                TryGetPayloadInt(trialPayload, "trial_index", out int trialIndex) &&
                trialIndex > 0)
            {
                if (!TryGetPayloadInt(trialPayload, "condition_order_index", out int conditionOrderIndex) ||
                    conditionOrderIndex < 0 ||
                    conditionOrderIndex >= expectedOrder.Length ||
                    !string.Equals(expectedOrder[conditionOrderIndex], trialConditionId, StringComparison.Ordinal))
                {
                    errorReason = "trial_condition_order_mismatch";
                    return false;
                }
            }

            return true;
        }

        private static bool TryReadManifestOrder(
            Dictionary<string, object> manifestPayload,
            string field,
            out string[] order)
        {
            order = Array.Empty<string>();
            if (manifestPayload == null ||
                !manifestPayload.TryGetValue(field, out object rawOrder) ||
                rawOrder == null ||
                rawOrder is string ||
                !(rawOrder is IEnumerable values))
            {
                return false;
            }

            var items = new List<string>();
            foreach (object value in values)
            {
                string item = value?.ToString()?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(item))
                {
                    return false;
                }

                items.Add(item);
            }

            order = items.ToArray();
            return order.Length > 0;
        }

        private void ResetRunSamplingState()
        {
            _nextSampleTime = Time.time;
            _nextRuntimeDiagnosticTime = Time.time;
            _hasPreviousPose = false;
            _hasPreviousDiagnosticPose = false;
            _navigationStartupActive = false;
            _navigationStartupSummaryEmitted = false;
            _hasLastValidRunSnapshot = false;
            _lastValidUnityTime = 0f;
            _lastValidRunElapsedTime = 0f;
            _lastValidRobotPosition = Vector3.zero;
            _lastValidRemainingDistance = float.NaN;
        }

        private static float ResolveRuntimeDiagnosticIntervalSeconds()
        {
            return ExperimentDataPathResolver.IsAndroidRuntime() ? 1.0f : 0.1f;
        }

        private bool TryBuildMetadataFromPayload(Dictionary<string, object> payload, out ExperimentLogMetadata metadata)
        {
            metadata = default;
            if (payload == null)
            {
                return false;
            }

            if (IsOrchestrated2x2Payload(payload))
            {
                metadata = ExperimentLogMetadata.ForOrchestrated2x2(
                    TryGetPayloadString(payload, "scene_name", out string orchestratedSceneName)
                        ? orchestratedSceneName
                        : ResolveSceneName(),
                    ResolveVoiceDriveProfile(payload),
                    ResolveVoiceAutonomyPolicy(payload),
                    ExperimentDataPathResolver.CurrentSession.ConditionOrder);
                return metadata.IsValid;
            }

            string driveProfile = TryGetPayloadString(payload, "active_drive_profile", out string activeDriveProfile)
                ? activeDriveProfile
                : (TryGetPayloadString(payload, "normalized_drive_profile", out string normalizedProfile) ? normalizedProfile : "Unknown");
            string autonomyPolicy = TryGetPayloadString(payload, "active_autonomous_policy", out string activePolicy)
                ? activePolicy
                : "Unknown";

            if (!TryGetPayloadVector3(payload, "target", out Vector3 target))
            {
                return false;
            }

            metadata = new ExperimentLogMetadata(
                ResolveSceneName(),
                string.IsNullOrWhiteSpace(driveProfile) ? "Unknown" : driveProfile,
                string.IsNullOrWhiteSpace(autonomyPolicy) ? "Unknown" : autonomyPolicy,
                target);
            return metadata.IsValid;
        }

        private bool TryBuildVoiceMetadataFromPayload(Dictionary<string, object> payload, out ExperimentLogMetadata metadata)
        {
            metadata = default;
            if (payload == null)
            {
                return false;
            }

            if (IsOrchestrated2x2Payload(payload))
            {
                metadata = ExperimentLogMetadata.ForOrchestrated2x2(
                    TryGetPayloadString(payload, "scene_name", out string orchestratedSceneName)
                        ? orchestratedSceneName
                        : ResolveSceneName(),
                    ResolveVoiceDriveProfile(payload),
                    ResolveVoiceAutonomyPolicy(payload),
                    ExperimentDataPathResolver.CurrentSession.ConditionOrder);
                return metadata.IsValid;
            }

            string driveProfile = ResolveVoiceDriveProfile(payload);
            string autonomyPolicy = ResolveVoiceAutonomyPolicy(payload);
            if (!IsResolvedMetadataValue(driveProfile) || !IsResolvedMetadataValue(autonomyPolicy))
            {
                TryResolveDeclaredAdapterMetadata(out string adapterDriveProfile, out string adapterAutonomyPolicy);
                if (!IsResolvedMetadataValue(driveProfile) && IsResolvedMetadataValue(adapterDriveProfile))
                {
                    driveProfile = adapterDriveProfile;
                }

                if (!IsResolvedMetadataValue(autonomyPolicy) && IsResolvedMetadataValue(adapterAutonomyPolicy))
                {
                    autonomyPolicy = adapterAutonomyPolicy;
                }
            }

            if (!IsResolvedMetadataValue(driveProfile))
            {
                driveProfile = "NoDriveProfile";
            }

            if (!IsResolvedMetadataValue(autonomyPolicy))
            {
                autonomyPolicy = "NoAutonomyPolicy";
            }

            string sceneName = TryGetPayloadString(payload, "scene_name", out string payloadSceneName)
                ? payloadSceneName
                : ResolveSceneName();

            metadata = TryResolveConditionLogContext(payload, out string conditionLogContext)
                ? ExperimentLogMetadata.ForConditionContext(
                    sceneName,
                    driveProfile,
                    autonomyPolicy,
                    conditionLogContext,
                    "voice_only")
                : ExperimentLogMetadata.ForVoiceOnly(
                    sceneName,
                    driveProfile,
                    autonomyPolicy);
            return metadata.IsValid;
        }

        private static bool TryResolveConditionLogContext(Dictionary<string, object> payload, out string conditionLogContext)
        {
            conditionLogContext = string.Empty;
            if (!TryGetPayloadString(payload, "condition_id", out string conditionId))
            {
                return false;
            }

            if (IsCanonicalFinalConditionId(conditionId))
            {
                conditionLogContext = conditionId;
                return true;
            }

            return false;
        }

        private static bool IsOrchestrated2x2Payload(Dictionary<string, object> payload)
        {
            return TryGetPayloadString(payload, "experiment_run_mode", out string runMode) &&
                string.Equals(runMode, "Orchestrated2x2", StringComparison.Ordinal);
        }

        private static bool IsCanonicalFinalConditionId(string conditionId)
        {
            return string.Equals(conditionId, "C00_robot_off_voice_off", StringComparison.Ordinal) ||
                string.Equals(conditionId, "C10_robot_on_voice_off", StringComparison.Ordinal) ||
                string.Equals(conditionId, "C11_robot_on_voice_on", StringComparison.Ordinal);
        }

        private static string ResolveVoiceDriveProfile(Dictionary<string, object> payload)
        {
            if (TryGetPayloadString(payload, "drive_profile", out string driveProfile) &&
                IsResolvedMetadataValue(driveProfile))
            {
                return driveProfile;
            }

            if (TryGetPayloadString(payload, "active_drive_profile", out string activeDriveProfile) &&
                IsResolvedMetadataValue(activeDriveProfile))
            {
                return activeDriveProfile;
            }

            if (TryGetPayloadString(payload, "normalized_drive_profile", out string normalizedDriveProfile) &&
                IsResolvedMetadataValue(normalizedDriveProfile))
            {
                return normalizedDriveProfile;
            }

            TiagoExperimentTelemetry.Snapshot snapshot = TiagoExperimentTelemetry.Latest;
            return IsResolvedMetadataValue(snapshot.ActiveDriveProfile) ? snapshot.ActiveDriveProfile : string.Empty;
        }

        private static string ResolveVoiceAutonomyPolicy(Dictionary<string, object> payload)
        {
            if (TryGetPayloadString(payload, "autonomy_policy", out string autonomyPolicy) &&
                IsResolvedMetadataValue(autonomyPolicy))
            {
                return autonomyPolicy;
            }

            if (TryGetPayloadString(payload, "active_autonomous_policy", out string activeAutonomyPolicy) &&
                IsResolvedMetadataValue(activeAutonomyPolicy))
            {
                return activeAutonomyPolicy;
            }

            TiagoExperimentTelemetry.Snapshot snapshot = TiagoExperimentTelemetry.Latest;
            return IsResolvedMetadataValue(snapshot.ActiveAutonomousPolicy) ? snapshot.ActiveAutonomousPolicy : string.Empty;
        }

        private static bool TryResolveDeclaredAdapterMetadata(out string driveProfile, out string autonomyPolicy)
        {
            driveProfile = string.Empty;
            autonomyPolicy = string.Empty;

            AutonomousRobotAdapter adapter = FindFirstObjectByType<AutonomousRobotAdapter>();
            return adapter != null && adapter.TryGetDeclaredAutonomyMetadata(out driveProfile, out autonomyPolicy);
        }

        private static bool IsResolvedMetadataValue(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value, "Unknown", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(value, NotYetAssignedMetadataValue, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetPayloadString(Dictionary<string, object> payload, string key, out string value)
        {
            value = string.Empty;
            if (payload == null || !payload.TryGetValue(key, out object raw) || raw == null)
            {
                return false;
            }

            value = raw.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryGetPayloadBool(Dictionary<string, object> payload, string key, out bool value)
        {
            value = false;
            if (payload == null || !payload.TryGetValue(key, out object raw) || raw == null)
            {
                return false;
            }

            if (raw is bool boolValue)
            {
                value = boolValue;
                return true;
            }

            return bool.TryParse(raw.ToString(), out value);
        }

        private static bool TryGetPayloadInt(Dictionary<string, object> payload, string key, out int value)
        {
            value = 0;
            if (payload == null || !payload.TryGetValue(key, out object raw) || raw == null)
            {
                return false;
            }

            if (raw is int intValue)
            {
                value = intValue;
                return true;
            }

            return int.TryParse(
                raw.ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static bool TryGetPayloadVector3(Dictionary<string, object> payload, string key, out Vector3 value)
        {
            value = Vector3.zero;
            if (payload == null || !payload.TryGetValue(key, out object raw) || raw == null)
            {
                return false;
            }

            if (raw is Vector3 vector)
            {
                value = vector;
                return true;
            }

            return false;
        }

        private ExperimentLogNameParts BuildExperimentLogNameParts(string experimentsRoot, ExperimentLogMetadata metadata)
        {
            string scene = Sanitize(metadata.SceneName);
            string driveProfile = Sanitize(metadata.DriveProfile);
            string autonomyPolicy = Sanitize(metadata.AutonomyPolicy);
            string targetToken = metadata.HasTarget
                ? $"target_X{FormatCoordinate(metadata.Target.x)}_Z{FormatCoordinate(metadata.Target.z)}"
                : Sanitize(metadata.ContextLabel);
            string experimentToken = $"{scene}__{driveProfile}-{autonomyPolicy}__{targetToken}";
            int attemptNumber = ResolveNextAttemptNumber(experimentsRoot, experimentToken);
            string requestedFolderName = Sanitize($"{_runId}__{experimentToken}__attempt{attemptNumber:00}");
            string filePrefix = BuildRequestedFilePrefix(metadata, attemptNumber);
            return new ExperimentLogNameParts(
                requestedFolderName,
                requestedFolderName,
                requestedFolderName,
                $"{filePrefix}__samples.csv",
                $"{filePrefix}__events.jsonl",
                "session_manifest.json",
                $"{filePrefix}__samples.csv",
                $"{filePrefix}__events.jsonl",
                "session_manifest.json",
                attemptNumber,
                false,
                string.Empty,
                2,
                false,
                string.Empty,
                2);
        }

        private ExperimentLogNameParts BuildFallbackExperimentLogNameParts(
            string experimentsRoot,
            ExperimentLogMetadata metadata,
            ExperimentLogNameParts requestedNameParts,
            string fallbackReason)
        {
            string scene = Sanitize(metadata.SceneName);
            string contextToken = metadata.HasTarget
                ? $"target_X{FormatCoordinate(metadata.Target.x)}_Z{FormatCoordinate(metadata.Target.z)}"
                : ShortenToken(metadata.ContextLabel, 24);
            string fallbackToken = $"{ShortenToken(scene, 48)}__{contextToken}";
            int attemptNumber = ResolveNextAttemptNumber(experimentsRoot, fallbackToken);
            string folderName = Sanitize($"{_runId}__{fallbackToken}__attempt{attemptNumber:00}");
            string filePrefix = BuildRequestedFilePrefix(metadata, attemptNumber);
            return new ExperimentLogNameParts(
                folderName,
                requestedNameParts.OriginalFolderName,
                requestedNameParts.RequestedFolderName,
                $"{filePrefix}__samples.csv",
                $"{filePrefix}__events.jsonl",
                "session_manifest.json",
                $"{filePrefix}__samples.csv",
                $"{filePrefix}__events.jsonl",
                "session_manifest.json",
                attemptNumber,
                true,
                fallbackReason,
                2,
                false,
                string.Empty,
                2);
        }

        private string BuildRequestedFilePrefix(ExperimentLogMetadata metadata, int attemptNumber)
        {
            string driveProfile = Sanitize(metadata.DriveProfile);
            string autonomyPolicy = Sanitize(metadata.AutonomyPolicy);
            if (string.Equals(metadata.SessionConditionMode, "multi_condition", StringComparison.Ordinal) ||
                string.Equals(metadata.ExperimentRunMode, "Orchestrated2x2", StringComparison.Ordinal))
            {
                return BuildFallbackFilePrefix(metadata, attemptNumber);
            }

            string contextToken = metadata.HasTarget
                ? $"X{FormatCoordinate(metadata.Target.x)}_Z{FormatCoordinate(metadata.Target.z)}"
                : Sanitize(metadata.ContextLabel);
            return Sanitize($"{_runId}__{driveProfile}-{autonomyPolicy}__{contextToken}__a{attemptNumber:00}");
        }

        private string BuildFallbackFilePrefix(ExperimentLogMetadata metadata, int attemptNumber)
        {
            string driveProfile = ShortenToken(metadata.DriveProfile, 16);
            string autonomyPolicy = ShortenToken(metadata.AutonomyPolicy, 16);
            return Sanitize($"{_runId}__{driveProfile}-{autonomyPolicy}__a{attemptNumber:00}");
        }

        private static int ResolveNextAttemptNumber(string experimentsRoot, string experimentToken)
        {
            int maxAttempt = 0;
            if (!Directory.Exists(experimentsRoot))
            {
                return 1;
            }

            string marker = $"__{experimentToken}__attempt";
            foreach (string directory in Directory.GetDirectories(experimentsRoot))
            {
                string name = Path.GetFileName(directory);
                int markerIndex = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    continue;
                }

                int attemptStart = markerIndex + marker.Length;
                if (attemptStart + 2 > name.Length)
                {
                    continue;
                }

                string digits = name.Substring(attemptStart, Math.Min(2, name.Length - attemptStart));
                if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int attempt))
                {
                    maxAttempt = Mathf.Max(maxAttempt, attempt);
                }
            }

            foreach (string path in Directory.GetFiles(experimentsRoot, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path);
                int markerIndex = name.LastIndexOf("__a", StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0 || markerIndex + 5 > name.Length)
                {
                    continue;
                }

                string digits = name.Substring(markerIndex + 3, 2);
                if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int attempt))
                {
                    maxAttempt = Mathf.Max(maxAttempt, attempt);
                }
            }

            return maxAttempt + 1;
        }

        private static string ResolveSceneName()
        {
            Scene scene = SceneManager.GetActiveScene();
            return scene.IsValid() && !string.IsNullOrWhiteSpace(scene.name)
                ? scene.name
                : "scene_Unknown";
        }

        private static string FormatCoordinate(float value)
        {
            if (Mathf.Abs(value) < 0.0005f)
            {
                value = 0f;
            }

            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public void StartRun(string reason)
        {
            if (_runRequested || _sampleWriter != null)
            {
                return;
            }

            if (_boundIdentity == null)
            {
                ExperimentDataPathResolver.SessionContext activeContext = ExperimentDataPathResolver.CurrentSession;
                if (ExperimentDataPathResolver.IsSessionClosed ||
                    activeContext.Identity == null ||
                    !ExperimentDataPathResolver.TryValidateCurrentIdentity(activeContext.Identity, out _))
                {
                    Debug.Log($"{LogPrefix} Run not started | reason={reason ?? string.Empty} waitingFor=explicit_active_session_identity", this);
                    return;
                }

                _boundIdentity = activeContext.Identity;
            }

            try
            {
                _runId = $"{Sanitize(_runLabel)}_{DateTime.Now:yyyyMMdd_HHmmss}";
                _runStartedAt = Time.time;
                _runRequested = true;
                _runInitializationFailed = false;
                _runInitializationFailure = string.Empty;
                _runStartReason = string.IsNullOrWhiteSpace(reason) ? "manual" : reason;
                _manifestPayload = null;
                _manifestNavigationMetadataResolved = false;

                Debug.Log($"{LogPrefix} Run pending | runId={_runId} reason={_runStartReason} waitingFor=first_navigation_target_and_profile", this);
                RecordEvent("run_pending_metadata", $"reason={_runStartReason} runId={_runId}");
                RecordEvent("control_mode_snapshot", $"current={TiagoLocomotionControlGate.ActiveControlMode} reason=run_pending");
                RecordEvent("navigation_build_stamp", $"version=StartupAlignmentWithFootprintClearanceDiagnostics timestamp={DateTime.Now:O} recoveryMode=MinimalReverseArc source=logger_pending");
                EnsureRunFilesReadyFromSnapshot();
            }
            catch (Exception ex)
            {
                _runInitializationFailed = true;
                _runInitializationFailure = ex.Message;
                Debug.LogError($"{LogPrefix} Failed to start run | reason={reason} error={ex.Message}", this);
                CloseWritersDefensively();
            }
        }

        public void StopRun(string reason)
        {
            try
            {
                if (_sampleWriter != null)
                {
                    WriteSample(isFinalSample: true);
                    LogEvent(
                        "experiment_run_finished",
                        BuildRunFinishedPayload(reason),
                        _hasLastValidRunSnapshot ? _lastValidUnityTime : Time.time,
                        _hasLastValidRunSnapshot ? _lastValidRunElapsedTime : GetRunElapsedTime());
                    RecordEvent("run_finished", $"reason={reason}");
                    _sampleWriter.Flush();
                    _eventWriter.Flush();
                    UpdateManifestOnRunFinished(reason);
                    Debug.Log($"{LogPrefix} Run finished | runId={_runId} dir={_runDirectory}", this);
                }
            }
            finally
            {
                CloseWritersDefensively();
                _runRequested = false;
                _pendingEvents.Clear();
            }
        }

        public bool BindToSession(
            ExperimentDataPathResolver.SessionContext context,
            string reason,
            bool createRunFilesImmediately = false)
        {
            string errorReason = context.Identity == null ? "session_identity_missing" : string.Empty;
            if (context.Identity == null ||
                !ExperimentDataPathResolver.TryValidateCurrentIdentity(context.Identity, out errorReason))
            {
                Debug.LogError($"{LogPrefix} session_identity_bind_rejected | participant_id={context.ParticipantId} session_id={context.SessionId} session_root={context.SessionRoot} error_reason={errorReason}", this);
                return false;
            }

            if (_boundIdentity != null &&
                _boundIdentity.Matches(context.Identity) &&
                _runRequested &&
                !_runInitializationFailed)
            {
                if (createRunFilesImmediately && _eventWriter == null)
                {
                    TryCreateRunFilesFromMetadata(ExperimentLogMetadata.ForPendingSession(
                        ResolveSceneName(),
                        context.ConditionOrder));
                }

                return !createRunFilesImmediately || _eventWriter != null;
            }

            PrepareForSessionTransition("bind_session:" + (reason ?? string.Empty));
            _boundIdentity = context.Identity;
            StartRun(reason ?? "session_bound");
            if (createRunFilesImmediately && _eventWriter == null)
            {
                TryCreateRunFilesFromMetadata(ExperimentLogMetadata.ForPendingSession(
                    ResolveSceneName(),
                    context.ConditionOrder));
            }

            return _runRequested &&
                _boundIdentity.Matches(context.Identity) &&
                (!createRunFilesImmediately || _eventWriter != null);
        }

        public void PrepareForSessionTransition(string reason)
        {
            StopRun(reason ?? "session_transition");
            _boundIdentity = null;
            _runDirectory = string.Empty;
            _samplePath = string.Empty;
            _eventPath = string.Empty;
            _manifestPath = string.Empty;
            _manifestPayload = null;
            _manifestNavigationMetadataResolved = false;
            _runInitializationFailed = false;
            _runInitializationFailure = string.Empty;
        }

        public bool IsBoundTo(ExperimentSessionIdentity identity)
        {
            return identity != null && _boundIdentity != null && _boundIdentity.Matches(identity);
        }

        private void UpdateManifestOnRunFinished(string reason)
        {
            if (_manifestPayload == null || string.IsNullOrWhiteSpace(_manifestPath))
            {
                return;
            }

            try
            {
                if (!TryValidateManifestOrderForIdentity(_manifestPayload, _boundIdentity, null, out string manifestError))
                {
                    Debug.LogError($"{LogPrefix} session_manifest_invariant_failed | operation=finish_update error_reason={manifestError} manifest={_manifestPath}", this);
                    return;
                }

                _manifestPayload["ended_at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                _manifestPayload["ended_at_utc"] = _manifestPayload["ended_at"];
                _manifestPayload["run_end_reason"] = reason ?? string.Empty;
                _manifestPayload["generated_files"] = BuildGeneratedFileList();
                File.WriteAllText(
                    _manifestPath,
                    SerializeJsonObject(_manifestPayload) + Environment.NewLine,
                    Encoding.UTF8);
                // This is the terminal index refresh. Emitting the index diagnostic here
                // would append to events.jsonl after its size has just been measured.
                ExperimentDataPathResolver.UpdateFileIndex(_runDirectory, emitLog: false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Debug.LogWarning($"{LogPrefix} Could not update manifest on run finish | manifest={_manifestPath} error={ex.Message}", this);
            }
        }

        private string[] BuildGeneratedFileList()
        {
            if (string.IsNullOrWhiteSpace(_runDirectory) || !Directory.Exists(_runDirectory))
            {
                return Array.Empty<string>();
            }

            var files = new List<string>();
            foreach (string path in Directory.GetFiles(_runDirectory, "*", SearchOption.AllDirectories))
            {
                files.Add(ToRelativePath(_runDirectory, path).Replace('\\', '/'));
            }

            files.Sort(StringComparer.Ordinal);
            return files.ToArray();
        }

        private static string ToRelativePath(string root, string path)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(fullRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return Path.GetFileName(path);
        }

        public void RecordEvent(string eventType, string payload)
        {
            if (!_runRequested || _runInitializationFailed)
            {
                return;
            }

            if (!TryValidateBoundIdentity(out string identityError))
            {
                Debug.LogError($"{LogPrefix} session_write_invariant_failed | operation=record_event event_type={eventType ?? string.Empty} error_reason={identityError}", this);
                return;
            }

            if (_eventWriter == null)
            {
                QueuePendingEvent(PendingEvent.FromText(eventType, payload, Time.time, GetRunElapsedTime()));
                EnsureRunFilesReadyFromSnapshot();
                return;
            }

            _eventWriter.WriteLine(
                $"{{\"timestamp_unity\":{Format(Time.time)},\"run_elapsed_time\":{Format(GetRunElapsedTime())},\"timestamp_wall\":\"{DateTime.Now:O}\",\"run_id\":\"{EscapeJson(_runId)}\",\"event_type\":\"{EscapeJson(eventType)}\",\"payload\":\"{EscapeJson(payload)}\"}}");
            _eventWriter.Flush();
        }

        public void LogEvent(string eventType, Dictionary<string, object> payload)
        {
            if (!_runRequested || _runInitializationFailed)
            {
                return;
            }

            payload ??= new Dictionary<string, object>();
            if (!TryValidateEventPayloadIdentity(payload, eventType, out string identityError))
            {
                Debug.LogError($"{LogPrefix} session_write_invariant_failed | operation=log_event event_type={eventType ?? string.Empty} error_reason={identityError} participant_id={ParticipantId} session_id={SessionId} session_root={SessionRoot}", this);
                return;
            }

            if (_eventWriter == null)
            {
                bool hasMetadata = TryBuildMetadataFromPayload(payload, out ExperimentLogMetadata payloadMetadata);
                if (!hasMetadata && IsVoiceExperimentEvent(eventType))
                {
                    hasMetadata = TryBuildVoiceMetadataFromPayload(payload, out payloadMetadata);
                }

                TryCreateRunFilesFromMetadata(hasMetadata ? payloadMetadata : default);
            }

            if (_eventWriter == null)
            {
                QueuePendingEvent(PendingEvent.FromJson(eventType, payload, Time.time, GetRunElapsedTime()));
                EnsureRunFilesReadyFromSnapshot();
                return;
            }

            TryEnrichManifestFromNavigationSnapshot(TiagoExperimentTelemetry.Latest);
            TryEnrichManifestFromConditionPayload(payload, eventType);
            LogEvent(eventType, payload, Time.time, GetRunElapsedTime());
        }

        private static bool IsResolverDiagnosticPayload(Dictionary<string, object> payload)
        {
            if (payload == null ||
                !payload.TryGetValue("p45f_resolver_diagnostic", out object value) ||
                value == null)
            {
                return false;
            }

            if (value is bool flag)
            {
                return flag;
            }

            return bool.TryParse(value.ToString(), out bool parsed) && parsed;
        }

        private bool TryValidateBoundIdentity(out string errorReason)
        {
            errorReason = string.Empty;
            if (_boundIdentity == null)
            {
                errorReason = "logger_identity_missing";
                return false;
            }

            if (!ExperimentDataPathResolver.TryValidateCurrentIdentity(_boundIdentity, out errorReason))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_runDirectory) &&
                !ExperimentSessionIdentity.PathsMatch(_runDirectory, _boundIdentity.SessionRoot))
            {
                errorReason = "logger_run_directory_mismatch";
                return false;
            }

            return true;
        }

        private bool TryValidateEventPayloadIdentity(
            Dictionary<string, object> payload,
            string eventType,
            out string errorReason)
        {
            errorReason = string.Empty;
            if (!TryValidateBoundIdentity(out errorReason))
            {
                return false;
            }

            payload ??= new Dictionary<string, object>();
            if (payload.TryGetValue("participant_id", out object rawParticipant) &&
                rawParticipant != null &&
                !string.IsNullOrWhiteSpace(rawParticipant.ToString()) &&
                !string.Equals(rawParticipant.ToString(), _boundIdentity.ParticipantId, StringComparison.Ordinal))
            {
                errorReason = "payload_participant_id_mismatch";
                return false;
            }

            if (payload.TryGetValue("session_id", out object rawSession) &&
                rawSession != null &&
                !string.IsNullOrWhiteSpace(rawSession.ToString()) &&
                !string.Equals(rawSession.ToString(), _boundIdentity.SessionId, StringComparison.Ordinal))
            {
                errorReason = "payload_session_id_mismatch";
                return false;
            }

            if (payload.TryGetValue("session_root", out object rawRoot) &&
                rawRoot != null &&
                !string.IsNullOrWhiteSpace(rawRoot.ToString()) &&
                !ExperimentSessionIdentity.PathsMatch(rawRoot.ToString(), _boundIdentity.SessionRoot))
            {
                errorReason = "payload_session_root_mismatch";
                return false;
            }

            payload["participant_id"] = _boundIdentity.ParticipantId;
            payload["session_id"] = _boundIdentity.SessionId;
            payload["session_root"] = _boundIdentity.SessionRoot;
            return true;
        }

        private static bool IsVoiceExperimentEvent(string eventType)
        {
            return !string.IsNullOrWhiteSpace(eventType) &&
                eventType.StartsWith("voice_", StringComparison.Ordinal);
        }

        private void LogEvent(string eventType, Dictionary<string, object> payload, float timestampUnity, float runElapsedTime)
        {
            if (_eventWriter == null)
            {
                return;
            }

            payload ??= new Dictionary<string, object>();
            if (!TryValidateEventPayloadIdentity(payload, eventType, out string identityError))
            {
                Debug.LogError($"{LogPrefix} session_write_invariant_failed | operation=write_event event_type={eventType ?? string.Empty} error_reason={identityError}", this);
                return;
            }
            if (!payload.ContainsKey("run_id"))
            {
                payload["run_id"] = _runId ?? string.Empty;
            }

            _eventWriter.WriteLine(
                $"{{\"timestamp_unity\":{Format(timestampUnity)},\"run_elapsed_time\":{Format(runElapsedTime)},\"timestamp_wall\":\"{DateTime.Now:O}\",\"run_id\":\"{EscapeJson(_runId)}\",\"event_type\":\"{EscapeJson(eventType)}\",\"payload\":{SerializeJsonValue(payload)}}}");
            _eventWriter.Flush();
        }

        private void TryEnrichManifestFromConditionPayload(Dictionary<string, object> payload, string sourceEventType)
        {
            if (_manifestPayload == null ||
                string.IsNullOrWhiteSpace(_manifestPath) ||
                string.IsNullOrWhiteSpace(sourceEventType))
            {
                return;
            }

            if (!TryValidateManifestOrderForIdentity(_manifestPayload, _boundIdentity, payload, out string manifestError))
            {
                Debug.LogError($"{LogPrefix} session_manifest_invariant_failed | operation=condition_update event_type={sourceEventType} error_reason={manifestError} manifest={_manifestPath}", this);
                return;
            }

            bool changed = TryApplyConditionManifestMetadataForDiagnostics(_manifestPayload, payload, sourceEventType);
            changed |= TryApplyRuntimeManifestMetadata(_manifestPayload, payload, sourceEventType);
            if (!changed)
            {
                return;
            }


            if (!TryValidateManifestOrderForIdentity(_manifestPayload, _boundIdentity, payload, out manifestError))
            {
                Debug.LogError($"{LogPrefix} session_manifest_invariant_failed | operation=condition_update_post_apply event_type={sourceEventType} error_reason={manifestError} manifest={_manifestPath}", this);
                return;
            }

            try
            {
                File.WriteAllText(
                    _manifestPath,
                    SerializeJsonObject(_manifestPayload) + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch (IOException ex)
            {
                Debug.LogWarning($"{LogPrefix} Could not update manifest condition metadata | manifest={_manifestPath} error={ex.Message}", this);
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.LogWarning($"{LogPrefix} Could not update manifest condition metadata | manifest={_manifestPath} error={ex.Message}", this);
            }
        }

        private void TryEnrichManifestFromNavigationSnapshot(TiagoExperimentTelemetry.Snapshot snapshot)
        {
            if (_manifestNavigationMetadataResolved ||
                _manifestPayload == null ||
                string.IsNullOrWhiteSpace(_manifestPath) ||
                !snapshot.HasTarget ||
                !IsResolvedMetadataValue(snapshot.ActiveDriveProfile) ||
                !IsResolvedMetadataValue(snapshot.ActiveAutonomousPolicy))
            {
                return;
            }

            SetManifestValueIfChanged(_manifestPayload, "drive_profile", snapshot.ActiveDriveProfile);
            SetManifestValueIfChanged(_manifestPayload, "autonomy_policy", snapshot.ActiveAutonomousPolicy);
            SetManifestValueIfChanged(_manifestPayload, "has_navigation_target", true);
            SetManifestValueIfChanged(_manifestPayload, "target_x", snapshot.Target.x);
            SetManifestValueIfChanged(_manifestPayload, "target_z", snapshot.Target.z);
            SetManifestValueIfChanged(_manifestPayload, "navigation_metadata_source", "first_valid_telemetry_snapshot");
            SetManifestValueIfChanged(
                _manifestPayload,
                "navigation_metadata_updated_utc",
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

            try
            {
                File.WriteAllText(
                    _manifestPath,
                    SerializeJsonObject(_manifestPayload) + Environment.NewLine,
                    Encoding.UTF8);
                _manifestNavigationMetadataResolved = true;
            }
            catch (IOException ex)
            {
                Debug.LogWarning($"{LogPrefix} Could not update manifest navigation metadata | manifest={_manifestPath} error={ex.Message}", this);
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.LogWarning($"{LogPrefix} Could not update manifest navigation metadata | manifest={_manifestPath} error={ex.Message}", this);
            }
        }

        private static bool TryApplyRuntimeManifestMetadata(
            Dictionary<string, object> manifestPayload,
            Dictionary<string, object> payload,
            string sourceEventType)
        {
            if (manifestPayload == null || payload == null)
            {
                return false;
            }

            bool changed = false;
            if (TryGetPayloadString(payload, "asr_backend_requested", out string asrRequested))
            {
                changed |= SetManifestValueIfChanged(manifestPayload, "asr_backend_requested", asrRequested);
            }

            if (TryGetPayloadString(payload, "requested_backend", out string requestedBackend))
            {
                changed |= SetManifestValueIfChanged(manifestPayload, "asr_backend_requested", requestedBackend);
            }

            if (TryGetPayloadString(payload, "backend_requested", out string backendRequested))
            {
                changed |= SetManifestValueIfChanged(manifestPayload, "asr_backend_requested", backendRequested);
            }

            if (TryGetPayloadString(payload, "asr_backend_effective", out string asrEffective))
            {
                changed |= SetManifestValueIfChanged(manifestPayload, "asr_backend_effective", asrEffective);
            }

            if (TryGetPayloadString(payload, "effective_backend", out string effectiveBackend))
            {
                changed |= SetManifestValueIfChanged(manifestPayload, "asr_backend_effective", effectiveBackend);
            }

            if (TryGetPayloadString(payload, "backend_effective", out string backendEffective))
            {
                changed |= SetManifestValueIfChanged(manifestPayload, "asr_backend_effective", backendEffective);
            }

            if (TryGetPayloadString(payload, "tts_backend_mode", out string ttsBackendMode))
            {
                changed |= SetManifestValueIfChanged(manifestPayload, "tts_backend", ttsBackendMode);
            }

            if (TryGetPayloadString(payload, "backend_mode", out string backendMode) &&
                sourceEventType != null &&
                sourceEventType.StartsWith("tts_", StringComparison.OrdinalIgnoreCase))
            {
                changed |= SetManifestValueIfChanged(manifestPayload, "tts_backend", backendMode);
            }

            if (TryGetPayloadString(payload, "backend_type", out string backendType) &&
                sourceEventType != null &&
                sourceEventType.StartsWith("tts_", StringComparison.OrdinalIgnoreCase))
            {
                changed |= SetManifestValueIfChanged(manifestPayload, "tts_backend_type", backendType);
            }

            if (changed)
            {
                SetManifestValueIfChanged(manifestPayload, "runtime_backend_metadata_source_event", sourceEventType ?? string.Empty);
                SetManifestValueIfChanged(
                    manifestPayload,
                    "runtime_backend_metadata_updated_utc",
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            }

            return changed;
        }

        public static bool TryApplyConditionManifestMetadataForDiagnostics(
            Dictionary<string, object> manifestPayload,
            Dictionary<string, object> conditionPayload,
            string sourceEventType)
        {
            if (manifestPayload == null ||
                conditionPayload == null ||
                !TryResolveConditionLogContext(conditionPayload, out string conditionId))
            {
                return false;
            }

            if (string.Equals(GetManifestString(manifestPayload, "session_condition_mode"), "multi_condition", StringComparison.Ordinal))
            {
                bool multiChanged = false;
                multiChanged |= SetManifestValueIfChanged(manifestPayload, "current_condition_id", conditionId);
                if (TryGetPayloadString(conditionPayload, "condition_name", out string currentConditionName))
                {
                    multiChanged |= SetManifestValueIfChanged(manifestPayload, "current_condition_name", currentConditionName);
                }

                if (TryGetPayloadBool(conditionPayload, "robot_enabled", out bool currentRobotEnabled))
                {
                    multiChanged |= SetManifestValueIfChanged(manifestPayload, "current_robot_enabled", currentRobotEnabled);
                }

                if (TryGetPayloadBool(conditionPayload, "voice_enabled", out bool currentVoiceEnabled))
                {
                    multiChanged |= SetManifestValueIfChanged(manifestPayload, "current_voice_enabled", currentVoiceEnabled);
                }

                if (TryGetPayloadString(conditionPayload, "assistance_mode", out string currentAssistanceMode))
                {
                    multiChanged |= SetManifestValueIfChanged(manifestPayload, "current_assistance_mode", currentAssistanceMode);
                }

                multiChanged |= SetManifestValueIfChanged(manifestPayload, "log_context_condition_mismatch_warning", false);
                if (!multiChanged)
                {
                    return false;
                }

                SetManifestValueIfChanged(manifestPayload, "condition_metadata_source_event", sourceEventType ?? string.Empty);
                SetManifestValueIfChanged(
                    manifestPayload,
                    "condition_metadata_updated_utc",
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                return true;
            }

            bool changed = false;
            changed |= SetManifestValueIfChanged(manifestPayload, "condition_id", conditionId);
            if (TryGetPayloadString(conditionPayload, "condition_name", out string conditionName))
            {
                changed |= SetManifestValueIfChanged(manifestPayload, "condition_name", conditionName);
            }

            if (TryGetPayloadBool(conditionPayload, "robot_enabled", out bool robotEnabled))
            {
                changed |= SetManifestValueIfChanged(manifestPayload, "robot_enabled", robotEnabled);
            }

            if (TryGetPayloadBool(conditionPayload, "voice_enabled", out bool voiceEnabled))
            {
                changed |= SetManifestValueIfChanged(manifestPayload, "voice_enabled", voiceEnabled);
            }

            if (TryGetPayloadString(conditionPayload, "assistance_mode", out string assistanceMode))
            {
                changed |= SetManifestValueIfChanged(manifestPayload, "assistance_mode", assistanceMode);
            }

            string logContext = GetManifestString(manifestPayload, "log_context");
            string legacyContext = GetManifestString(manifestPayload, "legacy_log_context");
            bool logContextIsFinalCondition = IsCanonicalFinalConditionId(logContext);
            bool mismatch = !string.IsNullOrWhiteSpace(logContext) &&
                !string.Equals(logContext, conditionId, StringComparison.Ordinal);

            changed |= SetManifestValueIfChanged(manifestPayload, "condition_log_context", conditionId);
            if (mismatch && string.IsNullOrWhiteSpace(legacyContext) && !logContextIsFinalCondition)
            {
                changed |= SetManifestValueIfChanged(manifestPayload, "legacy_log_context", logContext);
            }

            changed |= SetManifestValueIfChanged(manifestPayload, "log_context_condition_mismatch_warning", mismatch);
            if (!changed)
            {
                return false;
            }

            SetManifestValueIfChanged(manifestPayload, "condition_metadata_source_event", sourceEventType ?? string.Empty);
            SetManifestValueIfChanged(
                manifestPayload,
                "condition_metadata_updated_utc",
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            return true;
        }

        private static string GetManifestString(Dictionary<string, object> manifestPayload, string key)
        {
            if (manifestPayload == null ||
                !manifestPayload.TryGetValue(key, out object raw) ||
                raw == null)
            {
                return string.Empty;
            }

            return raw.ToString() ?? string.Empty;
        }

        private static bool SetManifestValueIfChanged(Dictionary<string, object> manifestPayload, string key, object value)
        {
            value ??= string.Empty;
            if (manifestPayload.TryGetValue(key, out object existing) &&
                string.Equals(existing?.ToString() ?? string.Empty, value.ToString() ?? string.Empty, StringComparison.Ordinal))
            {
                return false;
            }

            manifestPayload[key] = value;
            return true;
        }

        private void QueuePendingEvent(PendingEvent pendingEvent)
        {
            if (_runInitializationFailed || string.IsNullOrWhiteSpace(pendingEvent.EventType))
            {
                return;
            }

            _pendingEvents.Add(pendingEvent);
        }

        private void FlushPendingEvents()
        {
            if (_eventWriter == null || _pendingEvents.Count == 0)
            {
                return;
            }

            foreach (PendingEvent pendingEvent in _pendingEvents)
            {
                if (pendingEvent.IsJsonPayload)
                {
                    LogEvent(pendingEvent.EventType, pendingEvent.JsonPayload, pendingEvent.TimestampUnity, pendingEvent.RunElapsedTime);
                }
                else
                {
                    WriteTextEvent(pendingEvent.EventType, pendingEvent.TextPayload, pendingEvent.TimestampUnity, pendingEvent.RunElapsedTime);
                }
            }

            _pendingEvents.Clear();
        }

        private void WriteTextEvent(string eventType, string payload, float timestampUnity, float runElapsedTime)
        {
            if (_eventWriter == null)
            {
                return;
            }

            _eventWriter.WriteLine(
                $"{{\"timestamp_unity\":{Format(timestampUnity)},\"run_elapsed_time\":{Format(runElapsedTime)},\"timestamp_wall\":\"{DateTime.Now:O}\",\"run_id\":\"{EscapeJson(_runId)}\",\"event_type\":\"{EscapeJson(eventType)}\",\"payload\":\"{EscapeJson(payload)}\"}}");
            _eventWriter.Flush();
        }

        private void WriteSample(bool isFinalSample = false)
        {
            TiagoExperimentTelemetry.Snapshot snapshot = TiagoExperimentTelemetry.Latest;
            float sampleTime = Time.time;
            bool hasValidUnityTimestamp = sampleTime > 0f && snapshot.UnityTime > 0f;
            if (isFinalSample && !hasValidUnityTimestamp)
            {
                RecordEvent("final_sample_skipped", $"reason=invalid_unity_timestamp time={Format(sampleTime)} snapshotTime={Format(snapshot.UnityTime)}");
                return;
            }

            Vector3 position = _robotReference != null ? _robotReference.position : Vector3.zero;
            float yawDeg = _robotReference != null ? _robotReference.eulerAngles.y : 0f;
            float rawDt = _hasPreviousPose ? sampleTime - _previousSampleTime : 0f;
            bool validObservedDelta = _hasPreviousPose && rawDt > 0.0001f;
            if (isFinalSample && _hasPreviousPose && !validObservedDelta)
            {
                RecordEvent("final_sample_skipped", $"reason=invalid_observed_delta time={Format(sampleTime)} previousSampleTime={Format(_previousSampleTime)}");
                return;
            }

            float dt = validObservedDelta ? rawDt : 0f;
            Vector3 delta = _hasPreviousPose ? position - _previousPosition : Vector3.zero;
            Vector3 forward = Quaternion.Euler(0f, yawDeg, 0f) * Vector3.forward;
            float observedLinear = validObservedDelta ? Vector3.Dot(Flatten(delta), forward.normalized) / dt : float.NaN;
            float observedAngular = validObservedDelta ? Mathf.DeltaAngle(_previousYawDeg, yawDeg) * Mathf.Deg2Rad / dt : float.NaN;

            Vector3 target = snapshot.HasTarget
                ? snapshot.Target
                : (_fallbackTargetReference != null ? _fallbackTargetReference.position : Vector3.zero);
            bool hasTarget = snapshot.HasTarget || _fallbackTargetReference != null;
            float remainingDistance = snapshot.HasTarget
                ? snapshot.RemainingDistance
                : (hasTarget && _robotReference != null ? Vector3.Distance(Flatten(position), Flatten(target)) : float.NaN);
            TiagoDifferentialDriveBridge.DriveDiagnostics drive = snapshot.Drive;

            if (sampleTime > 0f)
            {
                _hasLastValidRunSnapshot = true;
                _lastValidUnityTime = sampleTime;
                _lastValidRunElapsedTime = GetRunElapsedTime();
                _lastValidRobotPosition = position;
                _lastValidRemainingDistance = remainingDistance;
            }

            _sampleWriter.WriteLine(string.Join(",",
                Format(sampleTime),
                EscapeCsv(DateTime.Now.ToString("O", CultureInfo.InvariantCulture)),
                EscapeCsv(_runId),
                Format(GetRunElapsedTime()),
                EscapeCsv(snapshot.ControlMode),
                EscapeCsv(snapshot.CommandSource),
                snapshot.CommandApplied ? "1" : "0",
                EscapeCsv(snapshot.CommandEffect),
                Format(position.x),
                Format(position.y),
                Format(position.z),
                Format(yawDeg),
                hasTarget ? Format(target.x) : string.Empty,
                hasTarget ? Format(target.y) : string.Empty,
                hasTarget ? Format(target.z) : string.Empty,
                Format(remainingDistance),
                Format(snapshot.LinearCommand),
                Format(snapshot.AngularCommand),
                Format(drive.LeftTargetDegPerSec),
                Format(drive.RightTargetDegPerSec),
                Format(drive.LeftDriveTargetDegPerSec),
                Format(drive.RightDriveTargetDegPerSec),
                Format(drive.LeftJointVelocityDegPerSec),
                Format(drive.RightJointVelocityDegPerSec),
                Format(drive.LeftLogicalJointVelocityDegPerSec),
                Format(drive.RightLogicalJointVelocityDegPerSec),
                Format(drive.LeftAbsFollowRatio),
                Format(drive.RightAbsFollowRatio),
                Format(drive.TheoreticalLinear),
                Format(drive.TheoreticalAngular),
                Format(observedLinear),
                Format(observedAngular),
                EscapeCsv(snapshot.LocomotionMode),
                snapshot.ActiveCorner ? "1" : "0",
                Format(snapshot.ObstacleFront),
                EscapeCsv(snapshot.AutonomousLocomotionMode),
                snapshot.AutonomousActiveCorner ? "1" : "0",
                Format(snapshot.AutonomousObstacleFront),
                EscapeCsv(snapshot.ActivePathSource),
                EscapeCsv(snapshot.ActiveDriveProfile),
                EscapeCsv(snapshot.ActiveAutonomousPolicy),
                Format(snapshot.ActiveLookahead.x),
                Format(snapshot.ActiveLookahead.z),
                Format(snapshot.ActiveLookaheadDistance),
                Format(snapshot.ActiveProjected.x),
                Format(snapshot.ActiveProjected.z),
                snapshot.ActiveSegmentIndex.ToString(CultureInfo.InvariantCulture),
                Format(snapshot.DistanceToNextCorner),
                Format(snapshot.NextCornerAngleDeg),
                Format(snapshot.AngleErrorDeg),
                EscapeCsv(snapshot.SpeedReductionReason),
                EscapeCsv(drive.LeftName),
                EscapeCsv(drive.RightName)));
            _sampleWriter.Flush();

            _previousPosition = position;
            _previousYawDeg = yawDeg;
            _previousSampleTime = sampleTime;
            _hasPreviousPose = true;
        }

        private void WriteRuntimeDiagnostics()
        {
            TiagoExperimentTelemetry.Snapshot snapshot = TiagoExperimentTelemetry.Latest;
            float now = Time.time;
            Vector3 position = _robotReference != null ? _robotReference.position : Vector3.zero;
            float yawDeg = _robotReference != null ? _robotReference.eulerAngles.y : 0f;
            float observedLinear = 0f;
            float observedAngular = 0f;
            if (_hasPreviousDiagnosticPose)
            {
                float dt = Mathf.Max(0.0001f, now - _previousDiagnosticTime);
                Vector3 delta = position - _previousDiagnosticPosition;
                Vector3 forward = Quaternion.Euler(0f, yawDeg, 0f) * Vector3.forward;
                observedLinear = Vector3.Dot(Flatten(delta), forward.normalized) / dt;
                observedAngular = Mathf.DeltaAngle(_previousDiagnosticYawDeg, yawDeg) * Mathf.Deg2Rad / dt;
            }

            bool autonomousNavigationActive = snapshot.CommandSource == "Autonomous" &&
                snapshot.CommandApplied &&
                (snapshot.HasTarget || !string.IsNullOrEmpty(snapshot.ActivePathSource) || Mathf.Abs(snapshot.LinearCommand) > 0.01f || Mathf.Abs(snapshot.AngularCommand) > 0.01f);
            if (autonomousNavigationActive && !_navigationStartupActive && !_navigationStartupSummaryEmitted)
            {
                StartNavigationStartupDiagnostics(snapshot, observedLinear);
            }

            if (_navigationStartupActive)
            {
                WriteNavigationStartupDiagnostics(snapshot, position, yawDeg, observedLinear, observedAngular);
            }

            WriteHighAngleForwardDiagnostic(snapshot, position, yawDeg);
            WriteDriveRuntimeDiagnostics(snapshot, position, yawDeg, observedLinear, observedAngular);

            _previousDiagnosticPosition = position;
            _previousDiagnosticYawDeg = yawDeg;
            _previousDiagnosticTime = now;
            _hasPreviousDiagnosticPose = true;
        }

        private void StartNavigationStartupDiagnostics(TiagoExperimentTelemetry.Snapshot snapshot, float observedLinear)
        {
            _navigationStartupActive = true;
            _navigationStartupStartedAt = Time.time;
            _startupMaxAbsAngleErrorDeg = 0f;
            _startupVelocitySignChanges = 0;
            _startupAngularSignChanges = 0;
            _startupForwardReverseSwitches = 0;
            _startupPreviousVSign = 0;
            _startupPreviousWSign = 0;
            _startupMinObservedV = observedLinear;
            _startupMaxObservedV = observedLinear;
            _startupMaxAbsWCmd = 0f;
            _startupFirstLookahead = snapshot.ActiveLookahead;
            _startupFirstPathSource = snapshot.ActivePathSource;
        }

        private void WriteNavigationStartupDiagnostics(
            TiagoExperimentTelemetry.Snapshot snapshot,
            Vector3 position,
            float yawDeg,
            float observedLinear,
            float observedAngular)
        {
            float elapsed = Time.time - _navigationStartupStartedAt;
            TiagoDifferentialDriveBridge.DriveDiagnostics drive = snapshot.Drive;
            _startupMaxAbsAngleErrorDeg = Mathf.Max(_startupMaxAbsAngleErrorDeg, Mathf.Abs(snapshot.AngleErrorDeg));
            _startupMinObservedV = Mathf.Min(_startupMinObservedV, observedLinear);
            _startupMaxObservedV = Mathf.Max(_startupMaxObservedV, observedLinear);
            _startupMaxAbsWCmd = Mathf.Max(_startupMaxAbsWCmd, Mathf.Abs(snapshot.AngularCommand));
            TrackCommandSignChanges(snapshot.LinearCommand, snapshot.AngularCommand);

            LogEvent(
                "navigation_startup_sample",
                new Dictionary<string, object>
                {
                    ["runId"] = _runId,
                    ["elapsedSinceNavigationStart"] = elapsed,
                    ["robotPosition"] = position,
                    ["robotYaw"] = yawDeg,
                    ["targetPosition"] = snapshot.HasTarget ? snapshot.Target : (_fallbackTargetReference != null ? _fallbackTargetReference.position : Vector3.zero),
                    ["activePathSource"] = snapshot.ActivePathSource,
                    ["activeSegmentIndex"] = snapshot.ActiveSegmentIndex,
                    ["activeLookaheadPoint"] = snapshot.ActiveLookahead,
                    ["activeProjectedPoint"] = snapshot.ActiveProjected,
                    ["distanceToLookahead"] = Vector3.Distance(Flatten(position), Flatten(snapshot.ActiveLookahead)),
                    ["angleErrorDeg"] = snapshot.AngleErrorDeg,
                    ["remainingPathDistance"] = snapshot.RemainingDistance,
                    ["v_cmd"] = snapshot.LinearCommand,
                    ["w_cmd"] = snapshot.AngularCommand,
                    ["observed_v"] = observedLinear,
                    ["observed_w"] = observedAngular,
                    ["wheel_l_target_deg_s"] = drive.LeftTargetDegPerSec,
                    ["wheel_r_target_deg_s"] = drive.RightTargetDegPerSec,
                    ["wheel_l_joint_logical_deg_s"] = drive.LeftLogicalJointVelocityDegPerSec,
                    ["wheel_r_joint_logical_deg_s"] = drive.RightLogicalJointVelocityDegPerSec,
                    ["wheel_l_follow_ratio"] = drive.LeftAbsFollowRatio,
                    ["wheel_r_follow_ratio"] = drive.RightAbsFollowRatio,
                    ["locomotionMode"] = snapshot.LocomotionMode
                });

            if (elapsed >= 5f)
            {
                LogEvent(
                    "navigation_startup_summary",
                    new Dictionary<string, object>
                    {
                        ["runId"] = _runId,
                        ["maxAbsAngleErrorDeg"] = _startupMaxAbsAngleErrorDeg,
                        ["numberOfVelocitySignChanges"] = _startupVelocitySignChanges,
                        ["numberOfAngularSignChanges"] = _startupAngularSignChanges,
                        ["numberOfForwardReverseSwitches"] = _startupForwardReverseSwitches,
                        ["minObservedV"] = _startupMinObservedV,
                        ["maxObservedV"] = _startupMaxObservedV,
                        ["maxAbsWCmd"] = _startupMaxAbsWCmd,
                        ["firstLookahead"] = _startupFirstLookahead,
                        ["firstPathSource"] = _startupFirstPathSource,
                        ["verdict"] = ResolveStartupVerdict(snapshot)
                    });
                _navigationStartupActive = false;
                _navigationStartupSummaryEmitted = true;
            }
        }

        private void TrackCommandSignChanges(float linearCommand, float angularCommand)
        {
            int vSign = Mathf.Abs(linearCommand) > 0.03f ? (linearCommand > 0f ? 1 : -1) : 0;
            int wSign = Mathf.Abs(angularCommand) > 0.03f ? (angularCommand > 0f ? 1 : -1) : 0;
            if (_startupPreviousVSign != 0 && vSign != 0 && vSign != _startupPreviousVSign)
            {
                _startupVelocitySignChanges++;
                _startupForwardReverseSwitches++;
            }

            if (_startupPreviousWSign != 0 && wSign != 0 && wSign != _startupPreviousWSign)
            {
                _startupAngularSignChanges++;
            }

            if (vSign != 0)
            {
                _startupPreviousVSign = vSign;
            }

            if (wSign != 0)
            {
                _startupPreviousWSign = wSign;
            }
        }

        private string ResolveStartupVerdict(TiagoExperimentTelemetry.Snapshot snapshot)
        {
            TiagoDifferentialDriveBridge.DriveDiagnostics drive = snapshot.Drive;
            if (_startupForwardReverseSwitches > 0)
            {
                return "WARNING_STARTUP_FORWARD_REVERSE_OSCILLATION";
            }

            if (_startupMaxAbsAngleErrorDeg > 60f && _startupMaxObservedV > 0.1f)
            {
                return "WARNING_STARTUP_HIGH_ANGLE_WITH_FORWARD_SPEED";
            }

            if (Mathf.Abs(drive.LeftAbsFollowRatio - drive.RightAbsFollowRatio) > 0.35f)
            {
                return "WARNING_STARTUP_WHEEL_ASYMMETRY";
            }

            return "PASS";
        }

        private void LogDriveAsymmetry(
            TiagoExperimentTelemetry.Snapshot snapshot,
            Vector3 position,
            float yawDeg,
            float observedLinear,
            float observedAngular)
        {
            TiagoDifferentialDriveBridge.DriveDiagnostics drive = snapshot.Drive;
            EnsureWheelTransforms(drive);
            LogEvent(
                "drive_runtime_wheel_asymmetry",
                new Dictionary<string, object>
                {
                    ["runId"] = _runId,
                    ["v_cmd"] = snapshot.LinearCommand,
                    ["w_cmd"] = snapshot.AngularCommand,
                    ["observed_v"] = observedLinear,
                    ["observed_w"] = observedAngular,
                    ["robotPosition"] = position,
                    ["robotYaw"] = yawDeg,
                    ["wheel_l_target_deg_s"] = drive.LeftTargetDegPerSec,
                    ["wheel_r_target_deg_s"] = drive.RightTargetDegPerSec,
                    ["wheel_l_joint_logical_deg_s"] = drive.LeftLogicalJointVelocityDegPerSec,
                    ["wheel_r_joint_logical_deg_s"] = drive.RightLogicalJointVelocityDegPerSec,
                    ["wheel_l_follow_ratio"] = drive.LeftAbsFollowRatio,
                    ["wheel_r_follow_ratio"] = drive.RightAbsFollowRatio,
                    ["leftWheelPosition"] = _leftWheelTransform != null ? _leftWheelTransform.position : Vector3.zero,
                    ["rightWheelPosition"] = _rightWheelTransform != null ? _rightWheelTransform.position : Vector3.zero,
                    ["leftWheelDriveDamping"] = drive.LeftDriveDamping,
                    ["leftWheelDriveStiffness"] = drive.LeftDriveStiffness,
                    ["leftWheelDriveForceLimit"] = drive.LeftForceLimit,
                    ["rightWheelDriveDamping"] = drive.RightDriveDamping,
                    ["rightWheelDriveStiffness"] = drive.RightDriveStiffness,
                    ["rightWheelDriveForceLimit"] = drive.RightForceLimit,
                    ["recommendation"] = "check_left_or_right_wheel_contact_constraint_or_ground_friction"
                });
            RunWheelGroundContactDiagnostic("left", _leftWheelTransform);
            RunWheelGroundContactDiagnostic("right", _rightWheelTransform);
        }

        private void UpdateWheelNotFollowing(
            string wheel,
            float targetDegS,
            float jointLogicalDegS,
            float damping,
            float stiffness,
            float forceLimit,
            ref float notFollowingSince)
        {
            bool notFollowing = Mathf.Abs(targetDegS) > 50f && Mathf.Abs(jointLogicalDegS) < 5f;
            if (notFollowing)
            {
                if (notFollowingSince < 0f)
                {
                    notFollowingSince = Time.time;
                    return;
                }

                if (Time.time - notFollowingSince > 0.5f)
                {
                    LogEvent(
                        "wheel_not_following_target",
                        new Dictionary<string, object>
                        {
                            ["runId"] = _runId,
                            ["wheel"] = wheel,
                            ["targetDegS"] = targetDegS,
                            ["jointLogicalDegS"] = jointLogicalDegS,
                            ["damping"] = damping,
                            ["stiffness"] = stiffness,
                            ["forceLimit"] = forceLimit,
                            ["possibleCauses"] = "wheel_blocked_or_axis_constraint_or_contact"
                        });
                    EnsureWheelTransforms(TiagoExperimentTelemetry.Latest.Drive);
                    RunWheelGroundContactDiagnostic(wheel, wheel == "left" ? _leftWheelTransform : _rightWheelTransform);
                    notFollowingSince = Time.time + 0.5f;
                }
            }
            else
            {
                notFollowingSince = float.NegativeInfinity;
            }
        }

        private void RunWheelGroundContactDiagnostic(string wheel, Transform wheelTransform)
        {
            if (wheelTransform == null)
            {
                return;
            }

            Collider wheelCollider = wheelTransform.GetComponent<Collider>() ?? wheelTransform.GetComponentInChildren<Collider>();
            Collider[] nearby = Physics.OverlapSphere(wheelTransform.position, 0.35f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            foreach (Collider collider in nearby)
            {
                if (collider == null || collider == wheelCollider || (wheelTransform.root != null && collider.transform.root == wheelTransform.root))
                {
                    continue;
                }

                string name = $"{collider.name} {collider.transform.root.name}".ToLowerInvariant();
                if (!name.Contains("floor") && !name.Contains("ground") && !name.Contains("caster"))
                {
                    continue;
                }

                Vector3 closest = collider.ClosestPoint(wheelTransform.position);
                float distance = Vector3.Distance(wheelTransform.position, closest);
                float penetrationDistance = 0f;
                bool isPenetrating = false;
                if (wheelCollider != null)
                {
                    isPenetrating = Physics.ComputePenetration(
                        wheelCollider,
                        wheelCollider.transform.position,
                        wheelCollider.transform.rotation,
                        collider,
                        collider.transform.position,
                        collider.transform.rotation,
                        out _,
                        out penetrationDistance);
                }
                LogEvent(
                    "wheel_ground_contact_diagnostic",
                    new Dictionary<string, object>
                    {
                        ["runId"] = _runId,
                        ["wheel"] = wheel,
                        ["wheelCollider"] = wheelCollider != null ? GetPath(wheelCollider.transform) : GetPath(wheelTransform),
                        ["groundCollider"] = GetPath(collider.transform),
                        ["distanceApprox"] = distance,
                        ["isPenetrating"] = isPenetrating,
                        ["penetrationDistance"] = isPenetrating ? penetrationDistance : 0f,
                        ["wheelWorldPosition"] = wheelTransform.position,
                        ["groundLayer"] = LayerMask.LayerToName(collider.gameObject.layer),
                        ["recommendation"] = "check_wheel_ground_contact_constraint_or_ground_friction"
                    });
                return;
            }
        }

        private void EnsureWheelTransforms(TiagoDifferentialDriveBridge.DriveDiagnostics drive)
        {
            if (_leftWheelTransform == null && !string.IsNullOrEmpty(drive.LeftName))
            {
                GameObject found = GameObject.Find(drive.LeftName);
                _leftWheelTransform = found != null ? found.transform : null;
            }

            if (_rightWheelTransform == null && !string.IsNullOrEmpty(drive.RightName))
            {
                GameObject found = GameObject.Find(drive.RightName);
                _rightWheelTransform = found != null ? found.transform : null;
            }
        }

        private void WriteHighAngleForwardDiagnostic(TiagoExperimentTelemetry.Snapshot snapshot, Vector3 position, float yawDeg)
        {
            if (Time.time < _nextHighAngleEventTime || Mathf.Abs(snapshot.AngleErrorDeg) <= 60f || snapshot.LinearCommand <= 0.1f)
            {
                return;
            }

            _nextHighAngleEventTime = Time.time + 0.5f;
            LogEvent(
                "path_tracking_forward_with_high_angle_error",
                new Dictionary<string, object>
                {
                    ["runId"] = _runId,
                    ["angleErrorDeg"] = snapshot.AngleErrorDeg,
                    ["v_cmd"] = snapshot.LinearCommand,
                    ["w_cmd"] = snapshot.AngularCommand,
                    ["activeLookaheadPoint"] = snapshot.ActiveLookahead,
                    ["robotPosition"] = position,
                    ["robotYaw"] = yawDeg,
                    ["recommendation"] = "consider_rotate_first_or_reduce_v_when_angle_error_high"
                });
        }

        private void WriteDriveRuntimeDiagnostics(
            TiagoExperimentTelemetry.Snapshot snapshot,
            Vector3 position,
            float yawDeg,
            float observedLinear,
            float observedAngular)
        {
            TiagoDifferentialDriveBridge.DriveDiagnostics drive = snapshot.Drive;
            bool meaningfulCommand = Mathf.Abs(snapshot.LinearCommand) > 0.2f || Mathf.Abs(snapshot.AngularCommand) > 0.5f;
            bool stuckObserved = Mathf.Abs(observedLinear) < 0.03f && Mathf.Abs(observedAngular) < 0.03f;
            bool asymmetric = (drive.LeftAbsFollowRatio < 0.1f && (drive.RightAbsFollowRatio > 0.8f || Mathf.Abs(drive.RightLogicalJointVelocityDegPerSec) > 50f)) ||
                (drive.RightAbsFollowRatio < 0.1f && (drive.LeftAbsFollowRatio > 0.8f || Mathf.Abs(drive.LeftLogicalJointVelocityDegPerSec) > 50f));
            if (snapshot.CommandSource == "Autonomous" && meaningfulCommand && stuckObserved && asymmetric)
            {
                LogDriveAsymmetry(snapshot, position, yawDeg, observedLinear, observedAngular);
            }

            UpdateWheelNotFollowing("left", drive.LeftTargetDegPerSec, drive.LeftLogicalJointVelocityDegPerSec, drive.LeftDriveDamping, drive.LeftDriveStiffness, drive.LeftForceLimit, ref _leftWheelNotFollowingSince);
            UpdateWheelNotFollowing("right", drive.RightTargetDegPerSec, drive.RightLogicalJointVelocityDegPerSec, drive.RightDriveDamping, drive.RightDriveStiffness, drive.RightForceLimit, ref _rightWheelNotFollowingSince);
        }

        private static string GetSampleHeader()
        {
            return "timestamp_unity,timestamp_wall,run_id,run_elapsed_time,control_mode,command_source,command_applied,command_effect,robot_x,robot_y,robot_z,robot_yaw_deg,target_x,target_y,target_z,remaining_distance,v_cmd,w_cmd,wheel_l_target_deg_s,wheel_r_target_deg_s,wheel_l_drive_deg_s,wheel_r_drive_deg_s,wheel_l_joint_raw_deg_s,wheel_r_joint_raw_deg_s,wheel_l_joint_logical_deg_s,wheel_r_joint_logical_deg_s,wheel_l_follow_ratio,wheel_r_follow_ratio,theory_v,theory_w,observed_v,observed_w,locomotion_mode,active_corner,obstacle_front,autonomous_locomotion_mode,autonomous_active_corner,autonomous_obstacle_front,active_path_source,active_drive_profile,active_autonomous_policy,active_lookahead_x,active_lookahead_z,active_lookahead_distance,active_projected_x,active_projected_z,active_segment_index,distance_to_next_corner,next_corner_angle_deg,angle_error_deg,speed_reduction_reason,wheel_left_name,wheel_right_name";
        }

        private void ValidateConfiguration()
        {
            if (_robotReference == null)
            {
                Debug.LogWarning($"{LogPrefix} Missing robot reference. Samples will contain zero pose and no reliable observed base velocity.", this);
            }

            if (_sampleIntervalSeconds < 0.05f)
            {
                Debug.LogWarning($"{LogPrefix} Sample interval too low ({_sampleIntervalSeconds:F3}s). It will be clamped to 0.05s.", this);
            }
        }

        private Dictionary<string, object> BuildLogFilesCreatedPayload(ExperimentLogMetadata metadata, ExperimentLogNameParts nameParts)
        {
            return new Dictionary<string, object>
            {
                ["run_id"] = _runId,
                ["questionnaire_code"] = ExperimentDataPathResolver.CurrentQuestionnaireCode,
                ["questionnaire_code_scheme"] = ExperimentDataPathResolver.CurrentQuestionnaireCodeScheme,
                ["participant_id"] = ExperimentDataPathResolver.CurrentParticipantId,
                ["session_id"] = ExperimentDataPathResolver.CurrentSessionId,
                ["condition_order_ids"] = ExperimentDataPathResolver.CurrentSession.ConditionOrder,
                ["experiment_folder"] = _runDirectory,
                ["samples_file"] = _samplePath,
                ["events_file"] = _eventPath,
                ["manifest_file"] = _manifestPath,
                ["samples_file_requested"] = nameParts.RequestedSamplesFileName,
                ["events_file_requested"] = nameParts.RequestedEventsFileName,
                ["manifest_file_requested"] = nameParts.RequestedManifestFileName,
                ["file_naming_schema_version"] = nameParts.FileNamingSchemaVersion,
                ["file_name_fallback_used"] = nameParts.FileNameFallbackUsed,
                ["file_name_fallback_reason"] = nameParts.FileNameFallbackReason,
                ["scene"] = metadata.SceneName,
                ["driveProfile"] = metadata.DriveProfile,
                ["autonomyPolicy"] = metadata.AutonomyPolicy,
                ["log_context"] = metadata.ContextLabel,
                ["condition_log_context"] = metadata.ConditionLogContext,
                ["legacy_log_context"] = metadata.LegacyContextLabel,
                ["log_context_condition_mismatch_warning"] = metadata.LogContextConditionMismatchWarning,
                ["experiment_run_mode"] = metadata.ExperimentRunMode,
                ["session_condition_mode"] = metadata.SessionConditionMode,
                ["active_condition_plan"] = metadata.ActiveConditionPlan,
                ["has_navigation_target"] = metadata.HasTarget,
                ["target_x"] = metadata.Target.x,
                ["target_z"] = metadata.Target.z,
                ["attempt"] = nameParts.AttemptNumber,
                ["folder_name_original"] = nameParts.OriginalFolderName,
                ["folder_name_requested"] = nameParts.RequestedFolderName,
                ["folder_name_actual"] = nameParts.FolderName,
                ["folder_name_fallback_used"] = nameParts.FallbackUsed,
                ["folder_name_fallback_reason"] = nameParts.FallbackReason,
                ["folder_naming_schema_version"] = nameParts.NamingSchemaVersion
            };
        }

        private Dictionary<string, object> BuildManifestPayload(ExperimentLogMetadata metadata, ExperimentLogNameParts nameParts)
        {
            ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.CurrentSession;
            string[] authoritativeOrder = context.Identity != null
                ? context.Identity.CopyConditionOrderIds()
                : CopyConditionOrder(context.ConditionOrder);
            return new Dictionary<string, object>
            {
                ["run_id"] = _runId,
                ["data_root"] = ExperimentDataPathResolver.ResolveDataRoot(),
                ["persistent_root"] = Application.persistentDataPath,
                ["session_root"] = _runDirectory,
                ["application_persistent_data_path"] = Application.persistentDataPath,
                ["session_id"] = string.IsNullOrWhiteSpace(context.SessionId) ? _runId : context.SessionId,
                ["participant_id"] = string.IsNullOrWhiteSpace(context.ParticipantId) ? "PILOT_UNSET" : context.ParticipantId,
                ["questionnaire_code"] = context.QuestionnaireCode ?? string.Empty,
                ["questionnaire_code_scheme"] = context.QuestionnaireCodeScheme ?? string.Empty,
                ["started_at"] = string.IsNullOrWhiteSpace(context.StartedAtUtc) ? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) : context.StartedAtUtc,
                ["ended_at"] = "",
                ["unity_version"] = Application.unityVersion,
                ["application_version"] = Application.version,
                ["device_model"] = SystemInfo.deviceModel,
                ["package_name"] = Application.identifier,
                ["p45f_storage_schema_version"] = "P45F",
                ["condition_order"] = (string[])authoritativeOrder.Clone(),
                ["condition_order_ids"] = (string[])authoritativeOrder.Clone(),
                ["conditions"] = (string[])authoritativeOrder.Clone(),
                ["rounds_per_condition"] = context.RoundsPerCondition,
                ["robot_enabled_by_condition"] = BuildRobotEnabledByCondition(),
                ["voice_enabled_by_condition"] = BuildVoiceEnabledByCondition(),
                ["asr_backend_requested"] = "unknown",
                ["asr_backend_effective"] = "unknown",
                ["tts_backend"] = "unknown",
                ["platform"] = Application.platform.ToString(),
                ["unity_platform"] = Application.platform.ToString(),
                ["scene"] = metadata.SceneName,
                ["drive_profile"] = metadata.DriveProfile,
                ["autonomy_policy"] = metadata.AutonomyPolicy,
                ["log_context"] = metadata.ContextLabel,
                ["condition_log_context"] = metadata.ConditionLogContext,
                ["legacy_log_context"] = metadata.LegacyContextLabel,
                ["log_context_condition_mismatch_warning"] = metadata.LogContextConditionMismatchWarning,
                ["experiment_run_mode"] = metadata.ExperimentRunMode,
                ["session_condition_mode"] = metadata.SessionConditionMode,
                ["active_condition_plan"] = (string[])authoritativeOrder.Clone(),
                ["has_navigation_target"] = metadata.HasTarget,
                ["target_x"] = metadata.Target.x,
                ["target_z"] = metadata.Target.z,
                ["attempt"] = nameParts.AttemptNumber,
                ["timestamp"] = DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
                ["timestamp_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["folder_name"] = nameParts.FolderName,
                ["folder_name_original"] = nameParts.OriginalFolderName,
                ["folder_name_requested"] = nameParts.RequestedFolderName,
                ["folder_name_actual"] = nameParts.FolderName,
                ["folder_name_fallback_used"] = nameParts.FallbackUsed,
                ["folder_name_fallback_reason"] = nameParts.FallbackReason,
                ["folder_naming_schema_version"] = nameParts.NamingSchemaVersion,
                ["samples_file"] = nameParts.SamplesFileName,
                ["events_file"] = nameParts.EventsFileName,
                ["manifest_file"] = nameParts.ManifestFileName,
                ["samples_file_requested"] = nameParts.RequestedSamplesFileName,
                ["events_file_requested"] = nameParts.RequestedEventsFileName,
                ["manifest_file_requested"] = nameParts.RequestedManifestFileName,
                ["file_naming_schema_version"] = nameParts.FileNamingSchemaVersion,
                ["file_name_fallback_used"] = nameParts.FileNameFallbackUsed,
                ["file_name_fallback_reason"] = nameParts.FileNameFallbackReason,
                ["run_start_reason"] = _runStartReason,
                ["relative_files"] = new[]
                {
                    nameParts.SamplesFileName,
                    nameParts.EventsFileName,
                    nameParts.ManifestFileName,
                    "session_export_info.json",
                    "README_EXPORT.txt",
                    "file_index.csv"
                }
            };
        }

        private static Dictionary<string, object> BuildRobotEnabledByCondition()
        {
            return new Dictionary<string, object>
            {
                ["C00_robot_off_voice_off"] = false,
                ["C10_robot_on_voice_off"] = true,
                ["C11_robot_on_voice_on"] = true
            };
        }

        private static Dictionary<string, object> BuildVoiceEnabledByCondition()
        {
            return new Dictionary<string, object>
            {
                ["C00_robot_off_voice_off"] = false,
                ["C10_robot_on_voice_off"] = false,
                ["C11_robot_on_voice_on"] = true
            };
        }

        private Dictionary<string, object> BuildRunStartedPayload(string reason, string samplePath, string eventPath, ExperimentLogMetadata metadata)
        {
            TiagoExperimentTelemetry.Snapshot snapshot = TiagoExperimentTelemetry.Latest;
            TiagoDifferentialDriveBridge.DriveDiagnostics drive = snapshot.Drive;
            Vector3 activeTargetPosition = snapshot.HasTarget
                ? snapshot.Target
                : metadata.Target;
            return new Dictionary<string, object>
            {
                ["runId"] = _runId,
                ["reason"] = reason,
                ["timestampUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["sceneName"] = metadata.SceneName,
                ["activeDriveProfile"] = metadata.DriveProfile,
                ["activeAutonomousPolicy"] = metadata.AutonomyPolicy,
                ["logContext"] = metadata.ContextLabel,
                ["conditionLogContext"] = metadata.ConditionLogContext,
                ["legacyLogContext"] = metadata.LegacyContextLabel,
                ["logContextConditionMismatchWarning"] = metadata.LogContextConditionMismatchWarning,
                ["experimentRunMode"] = metadata.ExperimentRunMode,
                ["sessionConditionMode"] = metadata.SessionConditionMode,
                ["activeConditionPlan"] = metadata.ActiveConditionPlan,
                ["hasNavigationTarget"] = metadata.HasTarget,
                ["applicationFrameRate"] = Application.targetFrameRate,
                ["fixedDeltaTime"] = Time.fixedDeltaTime,
                ["activeTargetPosition"] = activeTargetPosition,
                ["samplesPath"] = samplePath,
                ["eventsPath"] = eventPath,
                ["navigationBuildStamp"] = "StartupAlignmentWithFootprintClearanceDiagnostics",
                ["driveBridgeConfig"] = new Dictionary<string, object>
                {
                    ["leftWheel"] = drive.LeftName,
                    ["rightWheel"] = drive.RightName,
                    ["leftSign"] = drive.LeftSign,
                    ["rightSign"] = drive.RightSign,
                    ["leftDriveDamping"] = drive.LeftDriveDamping,
                    ["rightDriveDamping"] = drive.RightDriveDamping,
                    ["leftDriveStiffness"] = drive.LeftDriveStiffness,
                    ["rightDriveStiffness"] = drive.RightDriveStiffness,
                    ["leftForceLimit"] = drive.LeftForceLimit,
                    ["rightForceLimit"] = drive.RightForceLimit
                },
                ["pathCenteringConfig"] = "unavailable_logger_scope",
                ["navMeshObstacleConfig"] = "unavailable_logger_scope"
            };
        }

        private Dictionary<string, object> BuildRunFinishedPayload(string result)
        {
            TiagoExperimentTelemetry.Snapshot snapshot = TiagoExperimentTelemetry.Latest;
            Vector3 position = _hasLastValidRunSnapshot
                ? _lastValidRobotPosition
                : (_robotReference != null ? _robotReference.position : Vector3.zero);
            float duration = _hasLastValidRunSnapshot ? _lastValidRunElapsedTime : GetRunElapsedTime();
            float remainingDistance = _hasLastValidRunSnapshot ? _lastValidRemainingDistance : snapshot.RemainingDistance;
            return new Dictionary<string, object>
            {
                ["runId"] = _runId,
                ["result"] = result,
                ["duration"] = duration,
                ["finalRobotPosition"] = position,
                ["finalRemainingDistance"] = remainingDistance
            };
        }

        private void CloseWritersDefensively()
        {
            try
            {
                _sampleWriter?.Flush();
                _eventWriter?.Flush();
            }
            catch (IOException)
            {
                // Best effort on shutdown.
            }

            _sampleWriter?.Dispose();
            _eventWriter?.Dispose();
            _sampleWriter = null;
            _eventWriter = null;
        }

        private void DeleteEmptyRunDirectoryDefensively()
        {
            if (string.IsNullOrWhiteSpace(_runDirectory) || !Directory.Exists(_runDirectory))
            {
                return;
            }

            try
            {
                if (Directory.GetFileSystemEntries(_runDirectory).Length == 0)
                {
                    Directory.Delete(_runDirectory, false);
                }
            }
            catch (IOException)
            {
                // Best effort after a failed initialization.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort after a failed initialization.
            }
        }

        private void DeletePartialRunFilesDefensively()
        {
            DeleteFileDefensively(_samplePath);
            DeleteFileDefensively(_eventPath);
            DeleteFileDefensively(_manifestPath);
        }

        private static void DeleteFileDefensively(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Best effort before retrying with fallback names.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort before retrying with fallback names.
            }
        }

        private static Vector3 Flatten(Vector3 value)
        {
            value.y = 0f;
            return value;
        }

        private float GetRunElapsedTime()
        {
            return Time.time - _runStartedAt;
        }

        private static string GetPath(Transform current)
        {
            if (current == null)
            {
                return "<null>";
            }

            string path = current.name;
            while (current.parent != null)
            {
                current = current.parent;
                path = $"{current.name}/{path}";
            }

            return path;
        }

        private static string Format(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? string.Empty
                : value.ToString("F4", CultureInfo.InvariantCulture);
        }

        private static string EscapeCsv(string value)
        {
            value ??= string.Empty;
            if (!value.Contains(",") && !value.Contains("\"") && !value.Contains("\n") && !value.Contains("\r"))
            {
                return value;
            }

            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
        }

        private static string SerializeJsonValue(object value)
        {
            switch (value)
            {
                case null:
                    return "null";
                case string text:
                    return $"\"{EscapeJson(text)}\"";
                case bool flag:
                    return flag ? "true" : "false";
                case int intValue:
                    return intValue.ToString(CultureInfo.InvariantCulture);
                case long longValue:
                    return longValue.ToString(CultureInfo.InvariantCulture);
                case float floatValue:
                    return FormatJsonNumber(floatValue);
                case double doubleValue:
                    return double.IsNaN(doubleValue) || double.IsInfinity(doubleValue)
                        ? "null"
                        : doubleValue.ToString("G9", CultureInfo.InvariantCulture);
                case Vector3 vector:
                    return $"{{\"x\":{FormatJsonNumber(vector.x)},\"y\":{FormatJsonNumber(vector.y)},\"z\":{FormatJsonNumber(vector.z)}}}";
                case Dictionary<string, object> dictionary:
                    return SerializeJsonObject(dictionary);
                case IDictionary genericDictionary:
                    return SerializeGenericDictionary(genericDictionary);
                case IEnumerable enumerable:
                    return SerializeJsonArray(enumerable);
                default:
                    return $"\"{EscapeJson(value.ToString())}\"";
            }
        }

        private static string SerializeJsonArray(IEnumerable values)
        {
            if (values == null)
            {
                return "[]";
            }

            StringBuilder builder = new();
            builder.Append('[');
            bool first = true;
            foreach (object value in values)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                builder.Append(SerializeJsonValue(value));
                first = false;
            }

            builder.Append(']');
            return builder.ToString();
        }

        private static string SerializeGenericDictionary(IDictionary values)
        {
            if (values == null)
            {
                return "{}";
            }

            StringBuilder builder = new();
            builder.Append('{');
            bool first = true;
            foreach (DictionaryEntry entry in values)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                builder.Append('"');
                builder.Append(EscapeJson(entry.Key?.ToString() ?? string.Empty));
                builder.Append("\":");
                builder.Append(SerializeJsonValue(entry.Value));
                first = false;
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static string SerializeJsonObject(Dictionary<string, object> payload)
        {
            if (payload == null)
            {
                return "{}";
            }

            StringBuilder builder = new();
            builder.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, object> pair in payload)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                builder.Append('"');
                builder.Append(EscapeJson(pair.Key));
                builder.Append("\":");
                builder.Append(SerializeJsonValue(pair.Value));
                first = false;
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static string FormatJsonNumber(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? "null"
                : value.ToString("G9", CultureInfo.InvariantCulture);
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "run";
            }

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return value.Replace(' ', '_');
        }

        private static string ShortenToken(string value, int maxLength)
        {
            value = Sanitize(value);
            if (maxLength <= 0 || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength);
        }

        private readonly struct ExperimentLogMetadata
        {
            public ExperimentLogMetadata(string sceneName, string driveProfile, string autonomyPolicy, Vector3 target)
                : this(sceneName, driveProfile, autonomyPolicy, target, true, "navigation_target")
            {
            }

            private ExperimentLogMetadata(
                string sceneName,
                string driveProfile,
                string autonomyPolicy,
                Vector3 target,
                bool hasTarget,
                string contextLabel,
                string conditionLogContext = "",
                string legacyContextLabel = "",
                bool logContextConditionMismatchWarning = false,
                string experimentRunMode = "LegacyDebug",
                string sessionConditionMode = "single_condition",
                string[] activeConditionPlan = null)
            {
                SceneName = string.IsNullOrWhiteSpace(sceneName) ? "scene_Unknown" : sceneName;
                DriveProfile = string.IsNullOrWhiteSpace(driveProfile) ? "Unknown" : driveProfile;
                AutonomyPolicy = string.IsNullOrWhiteSpace(autonomyPolicy) ? "Unknown" : autonomyPolicy;
                Target = target;
                HasTarget = hasTarget;
                ContextLabel = string.IsNullOrWhiteSpace(contextLabel) ? "navigation_target" : contextLabel;
                ConditionLogContext = conditionLogContext ?? string.Empty;
                LegacyContextLabel = legacyContextLabel ?? string.Empty;
                LogContextConditionMismatchWarning = logContextConditionMismatchWarning;
                ExperimentRunMode = string.IsNullOrWhiteSpace(experimentRunMode) ? "LegacyDebug" : experimentRunMode;
                SessionConditionMode = string.IsNullOrWhiteSpace(sessionConditionMode) ? "single_condition" : sessionConditionMode;
                ActiveConditionPlan = activeConditionPlan ?? Array.Empty<string>();
            }

            public string SceneName { get; }
            public string DriveProfile { get; }
            public string AutonomyPolicy { get; }
            public Vector3 Target { get; }
            public bool HasTarget { get; }
            public string ContextLabel { get; }
            public string ConditionLogContext { get; }
            public string LegacyContextLabel { get; }
            public bool LogContextConditionMismatchWarning { get; }
            public string ExperimentRunMode { get; }
            public string SessionConditionMode { get; }
            public string[] ActiveConditionPlan { get; }
            public bool IsValid => !string.IsNullOrWhiteSpace(SceneName) &&
                !string.IsNullOrWhiteSpace(DriveProfile) &&
                !string.IsNullOrWhiteSpace(AutonomyPolicy);

            public static ExperimentLogMetadata ForVoiceOnly(string sceneName, string driveProfile, string autonomyPolicy)
            {
                return new ExperimentLogMetadata(sceneName, driveProfile, autonomyPolicy, Vector3.zero, false, "voice_only");
            }

            public static ExperimentLogMetadata ForConditionContext(string sceneName, string driveProfile, string autonomyPolicy, string conditionLogContext, string legacyContextLabel)
            {
                string context = string.IsNullOrWhiteSpace(conditionLogContext) ? legacyContextLabel : conditionLogContext;
                return new ExperimentLogMetadata(
                    sceneName,
                    driveProfile,
                    autonomyPolicy,
                    Vector3.zero,
                    false,
                    context,
                    conditionLogContext,
                    legacyContextLabel,
                    logContextConditionMismatchWarning: false);
            }

            public static ExperimentLogMetadata ForPendingSession(
                string sceneName,
                IReadOnlyList<string> authoritativeConditionOrder)
            {
                return new ExperimentLogMetadata(
                    sceneName,
                    NotYetAssignedMetadataValue,
                    NotYetAssignedMetadataValue,
                    Vector3.zero,
                    false,
                    "session_pending_metadata",
                    conditionLogContext: string.Empty,
                    legacyContextLabel: string.Empty,
                    logContextConditionMismatchWarning: false,
                    experimentRunMode: NotYetAssignedMetadataValue,
                    sessionConditionMode: "multi_condition",
                    activeConditionPlan: CopyConditionOrder(authoritativeConditionOrder));
            }

            public static ExperimentLogMetadata ForOrchestrated2x2(
                string sceneName,
                string driveProfile,
                string autonomyPolicy,
                IReadOnlyList<string> authoritativeConditionOrder)
            {
                return new ExperimentLogMetadata(
                    sceneName,
                    driveProfile,
                    autonomyPolicy,
                    Vector3.zero,
                    false,
                    "orchestrated_2x2_multi_condition",
                    conditionLogContext: string.Empty,
                    legacyContextLabel: string.Empty,
                    logContextConditionMismatchWarning: false,
                    experimentRunMode: "Orchestrated2x2",
                    sessionConditionMode: "multi_condition",
                    activeConditionPlan: CopyConditionOrder(authoritativeConditionOrder));
            }
        }

        private readonly struct ExperimentLogNameParts
        {
            public ExperimentLogNameParts(
                string folderName,
                string originalFolderName,
                string requestedFolderName,
                string samplesFileName,
                string eventsFileName,
                string manifestFileName,
                string requestedSamplesFileName,
                string requestedEventsFileName,
                string requestedManifestFileName,
                int attemptNumber,
                bool fallbackUsed,
                string fallbackReason,
                int namingSchemaVersion,
                bool fileNameFallbackUsed,
                string fileNameFallbackReason,
                int fileNamingSchemaVersion)
            {
                FolderName = folderName;
                OriginalFolderName = originalFolderName;
                RequestedFolderName = requestedFolderName;
                SamplesFileName = samplesFileName;
                EventsFileName = eventsFileName;
                ManifestFileName = manifestFileName;
                RequestedSamplesFileName = requestedSamplesFileName;
                RequestedEventsFileName = requestedEventsFileName;
                RequestedManifestFileName = requestedManifestFileName;
                AttemptNumber = attemptNumber;
                FallbackUsed = fallbackUsed;
                FallbackReason = fallbackReason ?? string.Empty;
                NamingSchemaVersion = namingSchemaVersion;
                FileNameFallbackUsed = fileNameFallbackUsed;
                FileNameFallbackReason = fileNameFallbackReason ?? string.Empty;
                FileNamingSchemaVersion = fileNamingSchemaVersion;
            }

            public string FolderName { get; }
            public string OriginalFolderName { get; }
            public string RequestedFolderName { get; }
            public string SamplesFileName { get; }
            public string EventsFileName { get; }
            public string ManifestFileName { get; }
            public string RequestedSamplesFileName { get; }
            public string RequestedEventsFileName { get; }
            public string RequestedManifestFileName { get; }
            public int AttemptNumber { get; }
            public bool FallbackUsed { get; }
            public string FallbackReason { get; }
            public int NamingSchemaVersion { get; }
            public bool FileNameFallbackUsed { get; }
            public string FileNameFallbackReason { get; }
            public int FileNamingSchemaVersion { get; }

            public ExperimentLogNameParts WithFileNameFallback(string fallbackPrefix, string fallbackReason)
            {
                return new ExperimentLogNameParts(
                    FolderName,
                    OriginalFolderName,
                    RequestedFolderName,
                    $"{fallbackPrefix}__samples.csv",
                    $"{fallbackPrefix}__events.jsonl",
                    "session_manifest.json",
                    RequestedSamplesFileName,
                    RequestedEventsFileName,
                    RequestedManifestFileName,
                    AttemptNumber,
                    FallbackUsed,
                    FallbackReason,
                    NamingSchemaVersion,
                    true,
                    fallbackReason,
                    FileNamingSchemaVersion);
            }
        }

        private readonly struct PendingEvent
        {
            private PendingEvent(
                string eventType,
                string textPayload,
                Dictionary<string, object> jsonPayload,
                bool isJsonPayload,
                float timestampUnity,
                float runElapsedTime)
            {
                EventType = eventType;
                TextPayload = textPayload;
                JsonPayload = jsonPayload;
                IsJsonPayload = isJsonPayload;
                TimestampUnity = timestampUnity;
                RunElapsedTime = runElapsedTime;
            }

            public string EventType { get; }
            public string TextPayload { get; }
            public Dictionary<string, object> JsonPayload { get; }
            public bool IsJsonPayload { get; }
            public float TimestampUnity { get; }
            public float RunElapsedTime { get; }

            public static PendingEvent FromText(string eventType, string payload, float timestampUnity, float runElapsedTime)
            {
                return new PendingEvent(eventType, payload ?? string.Empty, null, false, timestampUnity, runElapsedTime);
            }

            public static PendingEvent FromJson(string eventType, Dictionary<string, object> payload, float timestampUnity, float runElapsedTime)
            {
                return new PendingEvent(eventType, string.Empty, payload, true, timestampUnity, runElapsedTime);
            }
        }
    }
}
