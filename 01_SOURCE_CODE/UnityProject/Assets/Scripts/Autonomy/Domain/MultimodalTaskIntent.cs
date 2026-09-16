using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Autonomy.Domain
{
    public enum MultimodalObjectSelectionMode
    {
        #if UNITY_2019_1_OR_NEWER
        [UnityEngine.InspectorName("TargetId")]
        #endif
        ExplicitTargetId,
        Category
    }

    public sealed class MultimodalTaskIntent
    {
        public AutonomousTaskFlow TaskFlow { get; }
        public MultimodalObjectSelectionMode ObjectSelectionMode { get; }
        public string TargetId { get; }
        public string ObjectCategory { get; }
        public string PlaceTargetId { get; }
        public string Source { get; }

        public MultimodalTaskIntent(
            AutonomousTaskFlow taskFlow,
            MultimodalObjectSelectionMode objectSelectionMode,
            string targetId,
            string objectCategory,
            string placeTargetId,
            string source = "")
        {
            TaskFlow = taskFlow;
            ObjectSelectionMode = objectSelectionMode;
            TargetId = targetId ?? string.Empty;
            ObjectCategory = objectCategory ?? string.Empty;
            PlaceTargetId = placeTargetId ?? string.Empty;
            Source = source ?? string.Empty;
        }

        public static MultimodalTaskIntent PickAndPlaceByCategory(string objectCategory, string placeTargetId, string source = "")
        {
            return new MultimodalTaskIntent(
                AutonomousTaskFlow.PickAndPlace,
                MultimodalObjectSelectionMode.Category,
                string.Empty,
                objectCategory,
                placeTargetId,
                source);
        }

        public static MultimodalTaskIntent PickOnlyByCategory(string objectCategory, string source = "")
        {
            return new MultimodalTaskIntent(
                AutonomousTaskFlow.PickOnly,
                MultimodalObjectSelectionMode.Category,
                string.Empty,
                objectCategory,
                string.Empty,
                source);
        }

        public static MultimodalTaskIntent PickAndPlaceByTargetId(string targetId, string placeTargetId, string source = "")
        {
            return new MultimodalTaskIntent(
                AutonomousTaskFlow.PickAndPlace,
                MultimodalObjectSelectionMode.ExplicitTargetId,
                targetId,
                string.Empty,
                placeTargetId,
                source);
        }
    }

    public sealed class P40TraceMetadata
    {
        public string RequestId { get; set; } = string.Empty;
        public string VoiceInteractionId { get; set; } = string.Empty;
        public string Producer { get; set; } = "unknown";
        public string CausalParentId { get; set; } = string.Empty;
        public string RawTranscript { get; set; } = string.Empty;
        public string NormalizedText { get; set; } = string.Empty;
        public string IntentKind { get; set; } = string.Empty;
        public string TargetAlias { get; set; } = string.Empty;
        public string RequestedDestination { get; set; } = string.Empty;
        public string ResolvedDestination { get; set; } = string.Empty;
        public string SubmittedDestination { get; set; } = string.Empty;
        public string LastDecision { get; set; } = string.Empty;
        public string LastReason { get; set; } = string.Empty;
    }

    public static class P40TraceContext
    {
        private static readonly ConditionalWeakTable<MultimodalTaskIntent, P40TraceMetadata> IntentMetadata = new();
        private static int _requestSequence;
        private static int _taskSequence;

        public static P40TraceMetadata LastVoiceCommand { get; private set; }

        public static string NextRequestId(string producer)
        {
            return $"{NormalizeProducer(producer)}_req_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{++_requestSequence:000}";
        }

        public static string NextTaskInstanceId(string producer)
        {
            return $"{NormalizeProducer(producer)}_task_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{++_taskSequence:000}";
        }

        public static P40TraceMetadata RegisterIntent(MultimodalTaskIntent intent, P40TraceMetadata metadata)
        {
            if (intent == null)
            {
                return metadata ?? new P40TraceMetadata();
            }

            metadata ??= new P40TraceMetadata();
            metadata.Producer = NormalizeProducer(metadata.Producer);
            if (string.IsNullOrWhiteSpace(metadata.RequestId))
            {
                metadata.RequestId = NextRequestId(metadata.Producer);
            }

            IntentMetadata.Remove(intent);
            IntentMetadata.Add(intent, metadata);
            return metadata;
        }

        public static P40TraceMetadata EnsureForIntent(MultimodalTaskIntent intent, string fallbackProducer)
        {
            if (intent == null)
            {
                return new P40TraceMetadata
                {
                    RequestId = NextRequestId(fallbackProducer),
                    Producer = NormalizeProducer(fallbackProducer)
                };
            }

            if (IntentMetadata.TryGetValue(intent, out P40TraceMetadata metadata))
            {
                return metadata;
            }

            return RegisterIntent(
                intent,
                new P40TraceMetadata
                {
                    Producer = NormalizeProducer(string.IsNullOrWhiteSpace(intent.Source) ? fallbackProducer : intent.Source),
                    SubmittedDestination = intent.PlaceTargetId,
                    ResolvedDestination = intent.PlaceTargetId
                });
        }

        public static void RecordLastVoiceCommand(P40TraceMetadata metadata)
        {
            if (metadata == null || string.IsNullOrWhiteSpace(metadata.VoiceInteractionId))
            {
                return;
            }

            LastVoiceCommand = Clone(metadata);
        }

        public static Dictionary<string, object> ToPayload(P40TraceMetadata metadata)
        {
            return new Dictionary<string, object>
            {
                ["request_id"] = metadata?.RequestId ?? string.Empty,
                ["voice_interaction_id"] = metadata?.VoiceInteractionId ?? string.Empty,
                ["producer"] = NormalizeProducer(metadata?.Producer),
                ["intent_source"] = NormalizeProducer(metadata?.Producer),
                ["causal_parent_id"] = metadata?.CausalParentId ?? string.Empty,
                ["raw_transcript"] = metadata?.RawTranscript ?? string.Empty,
                ["normalized_text"] = metadata?.NormalizedText ?? string.Empty,
                ["intent_kind"] = metadata?.IntentKind ?? string.Empty,
                ["target_alias"] = metadata?.TargetAlias ?? string.Empty,
                ["requested_destination"] = metadata?.RequestedDestination ?? string.Empty,
                ["symbolic_destination"] = metadata?.RequestedDestination ?? string.Empty,
                ["resolved_destination"] = metadata?.ResolvedDestination ?? string.Empty,
                ["submitted_destination"] = metadata?.SubmittedDestination ?? string.Empty
            };
        }

        public static string NormalizeProducer(string producer)
        {
            if (string.IsNullOrWhiteSpace(producer))
            {
                return "unknown";
            }

            string value = producer.Trim();
            if (string.Equals(value, "debug_inspector", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "manual_stub", StringComparison.OrdinalIgnoreCase))
            {
                return "manual_debug";
            }

            if (string.Equals(value, "voice_command", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "push_to_talk", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "continuous_vad", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "whisper", StringComparison.OrdinalIgnoreCase))
            {
                return "voice_command";
            }

            if (string.Equals(value, "assisted_navmesh_selection", StringComparison.OrdinalIgnoreCase))
            {
                return "assisted_navmesh_selection";
            }

            return value;
        }

        private static P40TraceMetadata Clone(P40TraceMetadata metadata)
        {
            return new P40TraceMetadata
            {
                RequestId = metadata.RequestId,
                VoiceInteractionId = metadata.VoiceInteractionId,
                Producer = metadata.Producer,
                CausalParentId = metadata.CausalParentId,
                RawTranscript = metadata.RawTranscript,
                NormalizedText = metadata.NormalizedText,
                IntentKind = metadata.IntentKind,
                TargetAlias = metadata.TargetAlias,
                RequestedDestination = metadata.RequestedDestination,
                ResolvedDestination = metadata.ResolvedDestination,
                SubmittedDestination = metadata.SubmittedDestination,
                LastDecision = metadata.LastDecision,
                LastReason = metadata.LastReason
            };
        }
    }
}
