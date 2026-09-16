using Autonomy.UnityIntegration;
using NUnit.Framework;
using UnityEngine;

namespace Autonomy.BT.Tests
{
    public sealed class ExperimentStartSceneXrPoseAlignerTests
    {
        [TestCase(0f, 0f)]
        [TestCase(90f, 90f)]
        [TestCase(180f, 180f)]
        [TestCase(270f, 90f)]
        public void HorizontalYawError_IsDeterministicAcrossCardinalHeadings(
            float currentYawDegrees,
            float expectedErrorDegrees)
        {
            Vector3 current = Quaternion.Euler(0f, currentYawDegrees, 0f) * Vector3.forward;

            float error = ExperimentStartSceneXrPoseMath.CalculateHorizontalYawErrorDegrees(
                current,
                Vector3.forward);

            Assert.That(error, Is.EqualTo(expectedErrorDegrees).Within(0.0001f));
        }

        [Test]
        public void HorizontalForward_RemovesPitchAndNormalizesWithoutChangingYaw()
        {
            Vector3 pitchedForward = Quaternion.Euler(37f, 123f, 0f) * Vector3.forward;

            bool valid = ExperimentStartSceneXrPoseMath.TryGetHorizontalForward(
                pitchedForward,
                out Vector3 horizontal);

            Assert.That(valid, Is.True);
            Assert.That(horizontal.y, Is.EqualTo(0f).Within(0.000001f));
            Assert.That(horizontal.magnitude, Is.EqualTo(1f).Within(0.000001f));
            Assert.That(
                ExperimentStartSceneXrPoseMath.CalculateHorizontalYawErrorDegrees(
                    horizontal,
                    Quaternion.Euler(0f, 123f, 0f) * Vector3.forward),
                Is.LessThan(0.0001f));
        }

        [Test]
        public void HorizontalForward_RejectsVerticalDirection()
        {
            Assert.That(
                ExperimentStartSceneXrPoseMath.TryGetHorizontalForward(Vector3.up, out Vector3 horizontal),
                Is.False);
            Assert.That(horizontal, Is.EqualTo(Vector3.zero));
        }

        [TestCase(0.03f, 2f, true)]
        [TestCase(0.03001f, 2f, false)]
        [TestCase(0.03f, 2.001f, false)]
        [TestCase(0f, 0f, true)]
        public void Tolerances_AreInclusiveAndIndependent(
            float positionErrorMeters,
            float yawErrorDegrees,
            bool expected)
        {
            Assert.That(
                ExperimentStartSceneXrPoseMath.IsWithinTolerance(
                    positionErrorMeters,
                    yawErrorDegrees,
                    0.03f,
                    2f),
                Is.EqualTo(expected));
        }
    }
}
