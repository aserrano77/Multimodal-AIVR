using Autonomy.Domain;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    public sealed class ManualBoxGrabExclusivityStateTests
    {
        [Test]
        public void CanSelect_AllowsAnyBox_WhenNoLockExists()
        {
            var state = new ManualBoxGrabExclusivityState<object, object>();
            object boxA = new();

            Assert.That(state.CanSelect(boxA), Is.True);
        }

        [Test]
        public void Select_AllowsSecondController_OnSameActiveBox()
        {
            var state = new ManualBoxGrabExclusivityState<object, object>();
            object boxA = new();
            object left = new();
            object right = new();

            Assert.That(state.Select(boxA, left), Is.EqualTo(ManualBoxGrabSelectionResult.LockAcquired));
            Assert.That(state.CanSelect(boxA), Is.True);
            Assert.That(state.Select(boxA, right), Is.EqualTo(ManualBoxGrabSelectionResult.SameBoxAllowed));
        }

        [Test]
        public void Select_RejectsDifferentBox_WhileLockIsActive()
        {
            var state = new ManualBoxGrabExclusivityState<object, object>();
            object boxA = new();
            object boxB = new();
            object left = new();
            object right = new();

            state.Select(boxA, left);

            Assert.That(state.CanSelect(boxB), Is.False);
            Assert.That(state.Select(boxB, right), Is.EqualTo(ManualBoxGrabSelectionResult.RejectedDifferentBox));
        }

        [Test]
        public void Release_KeepsLockUntilLastControllerReleases()
        {
            var state = new ManualBoxGrabExclusivityState<object, object>();
            object boxA = new();
            object boxB = new();
            object left = new();
            object right = new();

            state.Select(boxA, left);
            state.Select(boxA, right);

            Assert.That(state.Release(boxA, left), Is.EqualTo(ManualBoxGrabReleaseResult.StillSelected));
            Assert.That(state.CanSelect(boxB), Is.False);
            Assert.That(state.Release(boxA, right), Is.EqualTo(ManualBoxGrabReleaseResult.LockReleased));
            Assert.That(state.CanSelect(boxB), Is.True);
        }
    }
}
