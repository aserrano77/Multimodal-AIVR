using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Process-wide authority for simulation freezes. Every legitimate writer of
    /// Time.timeScale=0 must own a balanced lease so scene changes cannot inherit
    /// an anonymous freeze.
    /// </summary>
    public static class ExperimentSimulationPauseAuthority
    {
        private const float RunningTimeScale = 1f;
        private static readonly Dictionary<int, OwnerRecord> Owners = new();
        private static int _nextTokenId;
        private static float _timeScaleBeforeFirstOwner = RunningTimeScale;

        public static int ActiveOwnerCount => Owners.Count;
        public static bool IsPauseActive => Owners.Count > 0;
        public static string ActiveOwners => Owners.Count == 0
            ? string.Empty
            : string.Join("|", Owners.Values.OrderBy(value => value.TokenId).Select(value => value.Describe()));

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticsForPlayerRun()
        {
            Owners.Clear();
            _nextTokenId = 0;
            _timeScaleBeforeFirstOwner = RunningTimeScale;
            Time.timeScale = RunningTimeScale;
        }

        public static PauseLease Acquire(string owner, string source, UnityEngine.Object context = null)
        {
            string normalizedOwner = string.IsNullOrWhiteSpace(owner) ? "unnamed_pause_owner" : owner.Trim();
            string normalizedSource = source ?? string.Empty;
            ReleaseOrphanedOwners(normalizedSource, context, includeInactive: false, includeUnverifiable: false);
            string scene = SceneManager.GetActiveScene().name;
            float before = Time.timeScale;

            if (Owners.Count == 0)
            {
                // Zero without an owner is never a valid baseline. It is precisely
                // the leaked state P46O-04 must not preserve into a new pause.
                if (Mathf.Approximately(before, 0f))
                {
                    LogInvariantFailure("orphaned_zero_before_owner_acquire", normalizedSource, context);
                    Time.timeScale = RunningTimeScale;
                    LogNormalized(normalizedSource, "orphaned_zero_before_owner_acquire", before, context);
                    before = RunningTimeScale;
                }

                _timeScaleBeforeFirstOwner = before;
            }

            int tokenId = ++_nextTokenId;
            var record = new OwnerRecord(tokenId, normalizedOwner, normalizedSource, scene, Time.frameCount, Time.realtimeSinceStartupAsDouble, context);
            Owners.Add(tokenId, record);
            Time.timeScale = 0f;

            try
            {
                var payload = BuildPayload(normalizedSource);
                payload["owner"] = normalizedOwner;
                payload["token_id"] = tokenId;
                payload["time_scale_before"] = before;
                payload["time_scale_after"] = Time.timeScale;
                TiagoExperimentTelemetry.LogEvent("experiment_time_scale_owner_acquired", payload);
                LogOwnerSnapshot(normalizedSource);
                Debug.Log($"[P46O-04] experiment_time_scale_owner_acquired | owner={normalizedOwner} token_id={tokenId} source={normalizedSource} scene={scene} active_owner_count={Owners.Count} owners={ActiveOwners} time_scale_before={before:0.###} time_scale_after={Time.timeScale:0.###}", context);
                return new PauseLease(tokenId);
            }
            catch
            {
                Owners.Remove(tokenId);
                Time.timeScale = Owners.Count == 0 ? _timeScaleBeforeFirstOwner : 0f;
                throw;
            }
        }

        public static bool NormalizeForRunningScene(string source, UnityEngine.Object context = null)
        {
            string normalizedSource = source ?? string.Empty;
            ReleaseOrphanedOwners(normalizedSource, context, includeInactive: true, includeUnverifiable: true);
            if (Owners.Count > 0)
            {
                LogInvariantFailure("normalization_blocked_by_active_owners", normalizedSource, context);
                return false;
            }

            float before = Time.timeScale;
            if (!Mathf.Approximately(before, RunningTimeScale))
            {
                Time.timeScale = RunningTimeScale;
                LogNormalized(normalizedSource, "no_registered_pause_owner", before, context);
            }

            return Mathf.Approximately(Time.timeScale, RunningTimeScale);
        }

        public static bool ValidateRunning(string source, UnityEngine.Object context = null)
        {
            ReleaseOrphanedOwners(source ?? string.Empty, context, includeInactive: false, includeUnverifiable: false);
            bool valid = Owners.Count == 0 && Mathf.Approximately(Time.timeScale, RunningTimeScale);
            if (!valid)
            {
                LogInvariantFailure("running_state_invalid", source ?? string.Empty, context);
            }

            return valid;
        }

        public static void EnforceOwnedPause(string source, UnityEngine.Object context = null)
        {
            ReleaseOrphanedOwners(source ?? string.Empty, context, includeInactive: false, includeUnverifiable: false);
            if (Owners.Count == 0 || Mathf.Approximately(Time.timeScale, 0f))
            {
                return;
            }

            float before = Time.timeScale;
            Time.timeScale = 0f;
            Debug.LogWarning($"[P46O-04] experiment_time_scale_owner_reasserted | source={source ?? string.Empty} time_scale_before={before:0.###} time_scale_after=0 active_owner_count={Owners.Count} owners={ActiveOwners}", context);
        }

        public static Dictionary<string, object> BuildOwnerSnapshot(string source)
        {
            return BuildPayload(source ?? string.Empty);
        }

        public static void ResetForTests()
        {
            Owners.Clear();
            _nextTokenId = 0;
            _timeScaleBeforeFirstOwner = RunningTimeScale;
            Time.timeScale = RunningTimeScale;
        }

        private static bool Release(int tokenId)
        {
            if (!Owners.TryGetValue(tokenId, out OwnerRecord record))
            {
                return false;
            }

            float before = Time.timeScale;
            Owners.Remove(tokenId);
            Time.timeScale = Owners.Count == 0 ? _timeScaleBeforeFirstOwner : 0f;

            var payload = BuildPayload(record.Source);
            payload["owner"] = record.Owner;
            payload["token_id"] = tokenId;
            payload["time_scale_before"] = before;
            payload["time_scale_after"] = Time.timeScale;
            payload["owner_duration_seconds"] = Math.Max(0d, Time.realtimeSinceStartupAsDouble - record.AcquiredRealtime);
            TiagoExperimentTelemetry.LogEvent("experiment_time_scale_owner_released", payload);
            LogOwnerSnapshot(record.Source);
            Debug.Log($"[P46O-04] experiment_time_scale_owner_released | owner={record.Owner} token_id={tokenId} source={record.Source} scene={SceneManager.GetActiveScene().name} active_owner_count={Owners.Count} owners={ActiveOwners} time_scale_before={before:0.###} time_scale_after={Time.timeScale:0.###}");
            return true;
        }

        private static int ReleaseOrphanedOwners(
            string source,
            UnityEngine.Object context,
            bool includeInactive,
            bool includeUnverifiable)
        {
            var orphanReasons = new Dictionary<int, string>();
            foreach (OwnerRecord record in Owners.Values)
            {
                if (IsOwnerOrphaned(record, includeInactive, includeUnverifiable, out string reason))
                {
                    orphanReasons[record.TokenId] = reason;
                }
            }

            if (orphanReasons.Count == 0)
            {
                return 0;
            }

            float before = Time.timeScale;
            var releasedRecords = new List<OwnerRecord>();
            foreach (int tokenId in orphanReasons.Keys)
            {
                if (Owners.TryGetValue(tokenId, out OwnerRecord record))
                {
                    Owners.Remove(tokenId);
                    releasedRecords.Add(record);
                }
            }

            Time.timeScale = Owners.Count == 0 ? _timeScaleBeforeFirstOwner : 0f;

            foreach (OwnerRecord record in releasedRecords)
            {
                try
                {
                    var payload = BuildPayload(source);
                    payload["owner"] = record.Owner;
                    payload["token_id"] = record.TokenId;
                    payload["orphan_reason"] = orphanReasons[record.TokenId];
                    payload["owner_duration_seconds"] = Math.Max(0d, Time.realtimeSinceStartupAsDouble - record.AcquiredRealtime);
                    payload["time_scale_before"] = before;
                    payload["time_scale_after"] = Time.timeScale;
                    TiagoExperimentTelemetry.LogEvent("experiment_time_scale_orphan_owner_released", payload);
                    Debug.LogWarning(
                        $"[P46O-04] experiment_time_scale_orphan_owner_released | owner={record.Owner} token_id={record.TokenId} " +
                        $"orphan_reason={orphanReasons[record.TokenId]} source={source ?? string.Empty} scene={SceneManager.GetActiveScene().name} " +
                        $"active_owner_count={Owners.Count} owners={ActiveOwners} time_scale_before={before:0.###} time_scale_after={Time.timeScale:0.###}",
                        context);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, context);
                }
            }

            return releasedRecords.Count;
        }

        private static bool IsOwnerOrphaned(
            OwnerRecord record,
            bool includeInactive,
            bool includeUnverifiable,
            out string reason)
        {
            reason = string.Empty;
            if (!record.HadContext)
            {
                if (includeUnverifiable)
                {
                    reason = "owner_context_unverifiable_at_running_boundary";
                    return true;
                }

                return false;
            }

            if (record.Context == null)
            {
                reason = "owner_context_destroyed";
                return true;
            }

            if (!includeInactive)
            {
                return false;
            }

            if (record.Context is Behaviour behaviour && !behaviour.isActiveAndEnabled)
            {
                reason = "owner_behaviour_inactive";
                return true;
            }

            if (record.Context is GameObject gameObject && !gameObject.activeInHierarchy)
            {
                reason = "owner_game_object_inactive";
                return true;
            }

            if (record.Context is Component component && !component.gameObject.activeInHierarchy)
            {
                reason = "owner_game_object_inactive";
                return true;
            }

            return false;
        }

        private static Dictionary<string, object> BuildPayload(string source)
        {
            return new Dictionary<string, object>
            {
                ["scene"] = SceneManager.GetActiveScene().name,
                ["source"] = source ?? string.Empty,
                ["time_scale"] = Time.timeScale,
                ["global_pause_active"] = Owners.Count > 0,
                ["pending_pause_owners"] = Owners.Count,
                ["pause_owners"] = ActiveOwners,
                ["frame"] = Time.frameCount,
                ["realtime"] = Time.realtimeSinceStartupAsDouble.ToString("0.000", CultureInfo.InvariantCulture)
            };
        }

        private static void LogNormalized(string source, string reason, float before, UnityEngine.Object context)
        {
            var payload = BuildPayload(source);
            payload["reason"] = reason;
            payload["time_scale_before"] = before;
            payload["time_scale_after"] = Time.timeScale;
            TiagoExperimentTelemetry.LogEvent("experiment_time_scale_normalized", payload);
            Debug.LogWarning($"[P46O-04] experiment_time_scale_normalized | source={source} reason={reason} scene={SceneManager.GetActiveScene().name} time_scale_before={before:0.###} time_scale_after={Time.timeScale:0.###} active_owner_count={Owners.Count}", context);
        }

        private static void LogOwnerSnapshot(string source)
        {
            TiagoExperimentTelemetry.LogEvent("experiment_pause_owner_snapshot", BuildPayload(source));
        }

        private static void LogInvariantFailure(string reason, string source, UnityEngine.Object context)
        {
            var payload = BuildPayload(source);
            payload["reason"] = reason;
            TiagoExperimentTelemetry.LogEvent("experiment_locomotion_state_invariant_failed", payload);
            Debug.LogError($"[P46O-04] experiment_locomotion_state_invariant_failed | reason={reason} source={source} scene={SceneManager.GetActiveScene().name} time_scale={Time.timeScale:0.###} active_owner_count={Owners.Count} owners={ActiveOwners}", context);
        }

        public sealed class PauseLease : IDisposable
        {
            private readonly int _tokenId;
            private bool _released;

            internal PauseLease(int tokenId)
            {
                _tokenId = tokenId;
            }

            public int TokenId => _tokenId;
            public bool IsReleased => _released;

            public void Dispose()
            {
                if (_released)
                {
                    return;
                }

                _released = true;
                Release(_tokenId);
            }
        }

        private readonly struct OwnerRecord
        {
            public OwnerRecord(
                int tokenId,
                string owner,
                string source,
                string scene,
                int frame,
                double acquiredRealtime,
                UnityEngine.Object context)
            {
                TokenId = tokenId;
                Owner = owner;
                Source = source;
                Scene = scene;
                Frame = frame;
                AcquiredRealtime = acquiredRealtime;
                Context = context;
                HadContext = context != null;
            }

            public int TokenId { get; }
            public string Owner { get; }
            public string Source { get; }
            public string Scene { get; }
            public int Frame { get; }
            public double AcquiredRealtime { get; }
            public UnityEngine.Object Context { get; }
            public bool HadContext { get; }

            public string Describe()
            {
                return $"{TokenId}:{Owner}@{Scene}:frame={Frame}";
            }
        }
    }
}
