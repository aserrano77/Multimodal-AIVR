using System;
using System.Collections.Generic;

namespace Autonomy.Domain
{
    public sealed class ExperimentTrialMetricsAccumulator
    {
        private readonly ExperimentTrialMetadata _metadata;
        private readonly float _startTime;
        private float _navigationToPickStartedAt = float.NaN;
        private float _pickStartedAt = float.NaN;
        private float _navigationToPlaceStartedAt = float.NaN;
        private float _placeStartedAt = float.NaN;

        public ExperimentTrialMetricsAccumulator(ExperimentTrialMetadata metadata, float startTime)
        {
            _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
            _startTime = startTime;
        }

        public string SelectedTargetId { get; private set; } = string.Empty;
        public string SelectedTargetCategory { get; private set; } = string.Empty;
        public string PlaceTargetId { get; private set; } = string.Empty;
        public float NavigationToPickDurationSeconds { get; private set; } = float.NaN;
        public float PickDurationSeconds { get; private set; } = float.NaN;
        public float NavigationToPlaceDurationSeconds { get; private set; } = float.NaN;
        public float PlaceDurationSeconds { get; private set; } = float.NaN;
        public int ErrorCount { get; private set; }
        public int NonTerminalWarningCount { get; private set; }
        public string NonTerminalWarnings => string.Join("|", _nonTerminalWarnings);
        public string FailureReason { get; private set; } = string.Empty;

        private readonly List<string> _nonTerminalWarnings = new List<string>();

        public void ObserveEvent(string eventType, IDictionary<string, object> payload, float eventTime)
        {
            if (string.IsNullOrWhiteSpace(eventType))
            {
                return;
            }

            switch (eventType)
            {
                case "multimodal_intent_resolved":
                    SetIfPresent(payload, "selected_object_id", value => SelectedTargetId = value);
                    SetIfPresent(payload, "selected_object_category", value => SelectedTargetCategory = value);
                    SetIfPresent(payload, "requested_place_target_id", value => PlaceTargetId = value);
                    break;
                case "autonomy_request_submitted":
                    SetIfPresent(payload, "pickup_target_id", value => SelectedTargetId = value);
                    SetIfPresent(payload, "requested_place_target_id", value => PlaceTargetId = value);
                    break;
                case "navigation_to_pick_started":
                    _navigationToPickStartedAt = eventTime;
                    break;
                case "navigation_to_pick_succeeded":
                    NavigationToPickDurationSeconds = DurationSince(_navigationToPickStartedAt, eventTime);
                    break;
                case "manipulation_pick_requested":
                    _pickStartedAt = eventTime;
                    SetIfPresent(payload, "object_id", value => SelectedTargetId = value);
                    break;
                case "manipulation_pick_succeeded":
                    PickDurationSeconds = DurationSince(_pickStartedAt, eventTime);
                    break;
                case "navigation_to_place_started":
                    _navigationToPlaceStartedAt = eventTime;
                    break;
                case "navigation_to_place_succeeded":
                    NavigationToPlaceDurationSeconds = DurationSince(_navigationToPlaceStartedAt, eventTime);
                    break;
                case "manipulation_place_requested":
                    _placeStartedAt = eventTime;
                    SetIfPresent(payload, "object_id", value => PlaceTargetId = value);
                    break;
                case "manipulation_place_succeeded":
                    PlaceDurationSeconds = DurationSince(_placeStartedAt, eventTime);
                    break;
            }

            if (IsNonTerminalWarningEvent(eventType))
            {
                NonTerminalWarningCount++;
                if (!_nonTerminalWarnings.Contains(eventType))
                {
                    _nonTerminalWarnings.Add(eventType);
                }

                return;
            }

            if (eventType.EndsWith("_failed", StringComparison.Ordinal) ||
                eventType == "multimodal_intent_rejected" ||
                eventType == "task_failed")
            {
                ErrorCount++;
                if (string.IsNullOrWhiteSpace(FailureReason))
                {
                    SetIfPresent(payload, "reason", value => FailureReason = value);
                    if (string.IsNullOrWhiteSpace(FailureReason))
                    {
                        FailureReason = eventType;
                    }
                }
            }
        }

        public ExperimentTrialSummary BuildSummary(bool success, bool aborted, string failureReason, float endTime, string timestampEnd)
        {
            ExperimentSessionMetadata session = _metadata.Session;
            string resolvedFailureReason = failureReason;
            if (!success && string.IsNullOrWhiteSpace(resolvedFailureReason))
            {
                resolvedFailureReason = FailureReason;
            }

            return new ExperimentTrialSummary
            {
                RunId = _metadata.RunId,
                ParticipantId = session.ParticipantId,
                SessionId = session.SessionId,
                TrialId = _metadata.TrialId,
                TrialIndex = _metadata.TrialIndex,
                ConditionId = session.ConditionId,
                ConditionName = session.ConditionName,
                ConditionOrderIndex = session.ConditionOrderIndex,
                RobotEnabled = session.RobotEnabled,
                VoiceEnabled = session.VoiceEnabled,
                AssistanceMode = session.AssistanceMode,
                SpawnGenerationMode = session.SpawnGenerationMode,
                RoundId = session.RoundId,
                RoundIndex = session.RoundIndex,
                RoundIndexWithinCondition = session.RoundIndexWithinCondition,
                RoundsPerCondition = session.RoundsPerCondition,
                GlobalRoundIndex = session.GlobalRoundIndex,
                AllowNonSlotDynamicPlaceFallback = session.AllowNonSlotDynamicPlaceFallback,
                UseDynamicPlacePose = session.UseDynamicPlacePose,
                UseDepositZoneSlotAllocator = session.UseDepositZoneSlotAllocator,
                MaxExpectedPlaceDistance = session.MaxExpectedPlaceDistance,
                MaxRelaxedPlaceDistance = session.MaxRelaxedPlaceDistance,
                PlaceCandidateReachabilityMargin = session.PlaceCandidateReachabilityMargin,
                MaxPlaceApproachRetries = session.MaxPlaceApproachRetries,
                PlaceFailureRecoveryMode = session.PlaceFailureRecoveryMode,
                PostPlaceEgressMode = session.PostPlaceEgressMode,
                TaskId = session.TaskId,
                InputMode = session.InputMode,
                DriveProfile = session.DriveProfile,
                AutonomyPolicy = session.AutonomyPolicy,
                SceneName = session.SceneName,
                SelectedTargetId = SelectedTargetId,
                SelectedTargetCategory = SelectedTargetCategory,
                PlaceTargetId = PlaceTargetId,
                Success = success,
                FailureReason = resolvedFailureReason ?? string.Empty,
                TotalDurationSeconds = Math.Max(0f, endTime - _startTime),
                NavigationToPickDurationSeconds = NavigationToPickDurationSeconds,
                PickDurationSeconds = PickDurationSeconds,
                NavigationToPlaceDurationSeconds = NavigationToPlaceDurationSeconds,
                PlaceDurationSeconds = PlaceDurationSeconds,
                ErrorCount = ErrorCount,
                NonTerminalWarningCount = NonTerminalWarningCount,
                NonTerminalWarnings = NonTerminalWarnings,
                Aborted = aborted,
                TimestampStart = _metadata.TimestampStart,
                TimestampEnd = timestampEnd ?? string.Empty,
                Notes = session.Notes
            };
        }

        private static bool IsNonTerminalWarningEvent(string eventType)
        {
            return string.Equals(eventType, "post_place_egress_failed", StringComparison.Ordinal) ||
                string.Equals(eventType, "post_place_local_retreat_failed", StringComparison.Ordinal) ||
                string.Equals(eventType, "post_place_egress_continue_with_warning", StringComparison.Ordinal);
        }

        private static float DurationSince(float startTime, float endTime)
        {
            return float.IsNaN(startTime) ? float.NaN : Math.Max(0f, endTime - startTime);
        }

        private static void SetIfPresent(IDictionary<string, object> payload, string key, Action<string> setter)
        {
            if (payload == null || !payload.TryGetValue(key, out object raw) || raw == null)
            {
                return;
            }

            string value = raw.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                setter(value);
            }
        }
    }
}
