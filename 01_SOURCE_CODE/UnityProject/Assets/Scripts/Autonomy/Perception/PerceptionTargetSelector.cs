using System;
using System.Collections.Generic;
using Autonomy.Domain;

namespace Autonomy.Perception
{
    public static class PerceptionTargetSelector
    {
        public static bool TrySelect(
            IReadOnlyList<PerceivedObject> candidates,
            PerceptionQuery query,
            out PerceivedObject selected,
            out string failureReason)
        {
            selected = null;
            failureReason = string.Empty;

            if (candidates == null || candidates.Count == 0)
            {
                failureReason = "no_candidates_found";
                return false;
            }

            PerceivedObject best = null;
            PerceivedObject preferred = null;
            float bestDistance = float.PositiveInfinity;

            for (int i = 0; i < candidates.Count; i++)
            {
                PerceivedObject candidate = candidates[i];
                if (!IsAvailable(candidate, query, out _))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(query.Category) &&
                    !string.Equals(candidate.Category, query.Category, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (query.SelectionStrategy != PerceptionSelectionStrategy.ExactObjectId &&
                    !string.IsNullOrWhiteSpace(query.PreferredObjectId) &&
                    string.Equals(candidate.ObjectId, query.PreferredObjectId, StringComparison.OrdinalIgnoreCase))
                {
                    preferred = candidate;
                    continue;
                }

                if (query.SelectionStrategy == PerceptionSelectionStrategy.ExactObjectId)
                {
                    if (string.Equals(candidate.ObjectId, query.ObjectId, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = candidate;
                        return true;
                    }

                    continue;
                }

                if (query.SelectionStrategy == PerceptionSelectionStrategy.FirstAvailable)
                {
                    selected = candidate;
                    return true;
                }

                float distance = candidate.DistanceToReference;
                if (float.IsNaN(distance) || float.IsInfinity(distance))
                {
                    distance = 0f;
                }

                if (best == null || distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }

            if (preferred != null)
            {
                selected = preferred;
                return true;
            }

            if (best != null)
            {
                selected = best;
                return true;
            }

            failureReason = query.SelectionStrategy == PerceptionSelectionStrategy.ExactObjectId
                ? "requested_object_not_available"
                : "no_available_candidates";
            return false;
        }

        public static bool IsAvailable(PerceivedObject candidate, PerceptionQuery query, out string rejectionReason)
        {
            rejectionReason = string.Empty;

            if (candidate == null)
            {
                rejectionReason = "candidate_null";
                return false;
            }

            if (!candidate.IsActive)
            {
                rejectionReason = "object_inactive";
                return false;
            }

            if (!candidate.IsManipulable)
            {
                rejectionReason = "object_not_manipulable";
                return false;
            }

            if (query.RejectDeposited && candidate.IsDeposited)
            {
                rejectionReason = "object_already_deposited";
                return false;
            }

            if (query.RejectGrabbed && candidate.IsGrabbed)
            {
                rejectionReason = "object_grabbed";
                return false;
            }

            if (query.RejectHeld && candidate.IsHeld)
            {
                rejectionReason = "object_already_held";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(candidate.ReasonIfRejected))
            {
                rejectionReason = candidate.ReasonIfRejected;
                return false;
            }

            return true;
        }

        public static TargetDescriptor ToTargetDescriptor(PerceivedObject perceivedObject, UnityEngine.Vector3 navigationPosition)
        {
            if (perceivedObject == null) throw new ArgumentNullException(nameof(perceivedObject));

            return new TargetDescriptor(
                perceivedObject.ObjectId,
                new System.Numerics.Vector3(navigationPosition.x, navigationPosition.y, navigationPosition.z));
        }
    }
}
