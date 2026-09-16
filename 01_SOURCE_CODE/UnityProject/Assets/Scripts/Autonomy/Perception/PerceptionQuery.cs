using UnityEngine;

namespace Autonomy.Perception
{
    public enum PerceptionSelectionStrategy
    {
        ExactObjectId,
        NearestAvailable,
        FirstAvailable
    }

    public readonly struct PerceptionQuery
    {
        public PerceptionSelectionStrategy SelectionStrategy { get; }
        public string ObjectId { get; }
        public string Category { get; }
        public string PreferredObjectId { get; }
        public Vector3? ReferencePosition { get; }
        public bool RejectDeposited { get; }
        public bool RejectGrabbed { get; }
        public bool RejectHeld { get; }

        public PerceptionQuery(
            PerceptionSelectionStrategy selectionStrategy,
            string objectId = "",
            string category = "",
            Vector3? referencePosition = null,
            bool rejectDeposited = true,
            bool rejectGrabbed = true,
            bool rejectHeld = true,
            string preferredObjectId = "")
        {
            SelectionStrategy = selectionStrategy;
            ObjectId = objectId ?? string.Empty;
            Category = category ?? string.Empty;
            PreferredObjectId = preferredObjectId ?? string.Empty;
            ReferencePosition = referencePosition;
            RejectDeposited = rejectDeposited;
            RejectGrabbed = rejectGrabbed;
            RejectHeld = rejectHeld;
        }
    }
}
