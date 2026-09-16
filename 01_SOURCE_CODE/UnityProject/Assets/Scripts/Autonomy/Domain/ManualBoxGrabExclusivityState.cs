using System.Collections.Generic;

namespace Autonomy.Domain
{
    public sealed class ManualBoxGrabExclusivityState<TBox, TSelector>
        where TBox : class
        where TSelector : class
    {
        private readonly Dictionary<TBox, HashSet<TSelector>> selectorsByBox = new();

        private TBox activeBox;

        public TBox ActiveBox => activeBox;
        public bool HasActiveBox => activeBox != null;

        public bool CanSelect(TBox box)
        {
            return box != null && (activeBox == null || ReferenceEquals(activeBox, box));
        }

        public ManualBoxGrabSelectionResult Select(TBox box, TSelector selector)
        {
            if (box == null || selector == null)
                return ManualBoxGrabSelectionResult.Ignored;

            if (activeBox != null && !ReferenceEquals(activeBox, box))
                return ManualBoxGrabSelectionResult.RejectedDifferentBox;

            if (!selectorsByBox.TryGetValue(box, out HashSet<TSelector> selectors))
            {
                selectors = new HashSet<TSelector>();
                selectorsByBox.Add(box, selectors);
            }

            bool alreadySelected = selectors.Contains(selector);
            selectors.Add(selector);

            if (activeBox == null)
            {
                activeBox = box;
                return ManualBoxGrabSelectionResult.LockAcquired;
            }

            return alreadySelected
                ? ManualBoxGrabSelectionResult.AlreadySelected
                : ManualBoxGrabSelectionResult.SameBoxAllowed;
        }

        public ManualBoxGrabReleaseResult Release(TBox box, TSelector selector)
        {
            if (box == null || selector == null)
                return ManualBoxGrabReleaseResult.Ignored;

            if (!selectorsByBox.TryGetValue(box, out HashSet<TSelector> selectors) ||
                !selectors.Remove(selector))
            {
                return ManualBoxGrabReleaseResult.Ignored;
            }

            if (selectors.Count > 0)
                return ManualBoxGrabReleaseResult.StillSelected;

            selectorsByBox.Remove(box);
            if (ReferenceEquals(activeBox, box))
            {
                activeBox = null;
                return ManualBoxGrabReleaseResult.LockReleased;
            }

            return ManualBoxGrabReleaseResult.Ignored;
        }

        public void Clear()
        {
            selectorsByBox.Clear();
            activeBox = null;
        }
    }

    public enum ManualBoxGrabSelectionResult
    {
        Ignored,
        LockAcquired,
        SameBoxAllowed,
        AlreadySelected,
        RejectedDifferentBox
    }

    public enum ManualBoxGrabReleaseResult
    {
        Ignored,
        StillSelected,
        LockReleased
    }
}
