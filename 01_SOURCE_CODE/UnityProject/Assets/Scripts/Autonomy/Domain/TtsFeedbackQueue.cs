using System;
using System.Collections.Generic;
using System.Linq;

namespace Autonomy.Domain
{
    public sealed class TtsFeedbackQueue
    {
        private readonly Queue<TtsFeedbackUtterance> _queue = new();
        private readonly Dictionary<string, double> _lastAcceptedAt = new(StringComparer.Ordinal);

        public TtsFeedbackQueue(int maxQueueSize = 4, double duplicateCooldownSeconds = 1.5)
        {
            MaxQueueSize = Math.Max(1, maxQueueSize);
            DuplicateCooldownSeconds = Math.Max(0.0, duplicateCooldownSeconds);
        }

        public int MaxQueueSize { get; }
        public double DuplicateCooldownSeconds { get; }
        public int Count => _queue.Count;

        public bool TryEnqueue(TtsFeedbackUtterance utterance, double nowSeconds, out string reason)
        {
            reason = string.Empty;
            if (utterance.IsEmpty)
            {
                reason = "empty";
                return false;
            }

            string key = NormalizeKey(utterance.Text);
            if (_lastAcceptedAt.TryGetValue(key, out double lastAccepted) &&
                nowSeconds - lastAccepted < DuplicateCooldownSeconds)
            {
                reason = "duplicate_cooldown";
                return false;
            }

            _lastAcceptedAt[key] = nowSeconds;
            if (_queue.Count >= MaxQueueSize)
            {
                if (utterance.Priority == TtsFeedbackPriority.Critical && TryDropOldestNormal())
                {
                    _queue.Enqueue(utterance);
                    reason = "queued_after_dropping_normal";
                    return true;
                }

                reason = utterance.Priority == TtsFeedbackPriority.Critical
                    ? "queue_full_critical_preserved_existing"
                    : "queue_full_normal_dropped";
                return false;
            }

            _queue.Enqueue(utterance);
            reason = "queued";
            return true;
        }

        public bool TryDequeue(out TtsFeedbackUtterance utterance)
        {
            if (_queue.Count == 0)
            {
                utterance = default;
                return false;
            }

            utterance = _queue.Dequeue();
            return true;
        }

        public void Clear()
        {
            _queue.Clear();
            _lastAcceptedAt.Clear();
        }

        private bool TryDropOldestNormal()
        {
            if (_queue.All(item => item.Priority == TtsFeedbackPriority.Critical))
            {
                return false;
            }

            TtsFeedbackUtterance[] items = _queue.ToArray();
            _queue.Clear();
            bool dropped = false;
            foreach (TtsFeedbackUtterance item in items)
            {
                if (!dropped && item.Priority == TtsFeedbackPriority.Normal)
                {
                    dropped = true;
                    continue;
                }

                _queue.Enqueue(item);
            }

            return dropped;
        }

        private static string NormalizeKey(string text)
        {
            return (text ?? string.Empty).Trim().ToUpperInvariant();
        }
    }
}
