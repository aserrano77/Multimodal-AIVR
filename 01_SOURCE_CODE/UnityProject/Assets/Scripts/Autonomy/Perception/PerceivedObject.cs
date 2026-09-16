using UnityEngine;

namespace Autonomy.Perception
{
    public enum BoundsSource
    {
        Collider,
        Renderer,
        TransformFallback
    }

    public sealed class PerceivedObject
    {
        public string ObjectId { get; }
        public string DisplayName { get; }
        public string Category { get; }
        public Transform Transform { get; }
        public Vector3 WorldPosition { get; }
        public Vector3 BoundsCenter { get; }
        public Vector3 BoundsExtents { get; }
        public BoundsSource BoundsSource { get; }
        public bool IsActive { get; }
        public bool IsManipulable { get; }
        public bool IsDeposited { get; }
        public bool IsGrabbed { get; }
        public bool IsHeld { get; }
        public float DistanceToReference { get; }
        public string ReasonIfRejected { get; }
        public bool IsAccepted => string.IsNullOrEmpty(ReasonIfRejected);

        public PerceivedObject(
            string objectId,
            string displayName,
            string category,
            Transform transform,
            Vector3 worldPosition,
            Vector3 boundsCenter,
            Vector3 boundsExtents,
            BoundsSource boundsSource,
            bool isActive,
            bool isManipulable,
            bool isDeposited,
            bool isGrabbed,
            bool isHeld,
            float distanceToReference,
            string reasonIfRejected)
        {
            ObjectId = objectId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            Category = category ?? string.Empty;
            Transform = transform;
            WorldPosition = worldPosition;
            BoundsCenter = boundsCenter;
            BoundsExtents = boundsExtents;
            BoundsSource = boundsSource;
            IsActive = isActive;
            IsManipulable = isManipulable;
            IsDeposited = isDeposited;
            IsGrabbed = isGrabbed;
            IsHeld = isHeld;
            DistanceToReference = distanceToReference;
            ReasonIfRejected = reasonIfRejected ?? string.Empty;
        }

        public PerceivedObject WithRejection(string reason)
        {
            return new PerceivedObject(
                ObjectId,
                DisplayName,
                Category,
                Transform,
                WorldPosition,
                BoundsCenter,
                BoundsExtents,
                BoundsSource,
                IsActive,
                IsManipulable,
                IsDeposited,
                IsGrabbed,
                IsHeld,
                DistanceToReference,
                reason);
        }
    }
}
