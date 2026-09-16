using System;
using System.Numerics;
using Autonomy.Core;
using Autonomy.Domain;
using Autonomy.Provisioning;
using NUnit.Framework;

namespace Autonomy.Tests.Provisioning
{
    [TestFixture]
    public class ManualTargetSeederTests
    {
        private RobotBlackboard _blackboard;
        private ManualTargetProvisioner _seeder;

        [SetUp]
        public void SetUp()
        {
            _blackboard = new RobotBlackboard();
            _seeder = new ManualTargetProvisioner(_blackboard);
        }

        [Test]
        public void SeedTarget_SetsTargetInBlackboard()
        {
            // Arrange
            var target = new TargetDescriptor("box_01", new Vector3(1, 0, 2));

            // Act
            _seeder.SeedTarget(target);

            // Assert
            bool found = _blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out var result);
            Assert.That(found, Is.True);
            Assert.That(result, Is.EqualTo(target));
        }

        [Test]
        public void SeedTarget_NullTarget_ThrowsException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => _seeder.SeedTarget(null));
        }

        [Test]
        public void ClearTarget_RemovesTargetFromBlackboard()
        {
            // Arrange
            var target = new TargetDescriptor("box_01", Vector3.Zero);
            _seeder.SeedTarget(target);

            // Act
            _seeder.ClearTarget();

            // Assert
            bool found = _blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out _);
            Assert.That(found, Is.False);
        }

        [Test]
        public void Constructor_NullBlackboard_ThrowsException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new ManualTargetProvisioner(null));
        }
    }
}
