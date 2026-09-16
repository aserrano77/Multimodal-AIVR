using Autonomy.UnityIntegration;
using NUnit.Framework;
using UnityEngine;

namespace Autonomy.BT.Tests
{
    public sealed class ExperimentSimulationPauseAuthorityTests
    {
        private GameObject _host;
        private ExperimentRuntimePauseCoordinator _coordinator;

        [SetUp]
        public void SetUp()
        {
            ExperimentSimulationPauseAuthority.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            _coordinator?.CleanupPauseState("p46o04_test_teardown");
            if (_host != null) Object.DestroyImmediate(_host);
            ExperimentSimulationPauseAuthority.ResetForTests();
        }

        [Test]
        public void XrGate_AcquiresAndReleasesExactlyOnce()
        {
            ExperimentSimulationPauseAuthority.PauseLease gate =
                ExperimentSimulationPauseAuthority.Acquire("experimental_xr_alignment_gate", "test_gate");

            Assert.That(Time.timeScale, Is.Zero);
            Assert.That(ExperimentSimulationPauseAuthority.ActiveOwnerCount, Is.EqualTo(1));

            gate.Dispose();
            gate.Dispose();

            Assert.That(gate.IsReleased, Is.True);
            Assert.That(ExperimentSimulationPauseAuthority.ActiveOwnerCount, Is.Zero);
            Assert.That(Time.timeScale, Is.EqualTo(1f));
        }

        [Test]
        public void TwoPauseOwners_DoNotReleaseEachOther()
        {
            ExperimentSimulationPauseAuthority.PauseLease gate =
                ExperimentSimulationPauseAuthority.Acquire("experimental_xr_alignment_gate", "test_gate");
            ExperimentSimulationPauseAuthority.PauseLease menu =
                ExperimentSimulationPauseAuthority.Acquire("experiment_pause_menu", "test_menu");

            gate.Dispose();
            Assert.That(ExperimentSimulationPauseAuthority.ActiveOwnerCount, Is.EqualTo(1));
            Assert.That(Time.timeScale, Is.Zero);

            menu.Dispose();
            Assert.That(ExperimentSimulationPauseAuthority.ActiveOwnerCount, Is.Zero);
            Assert.That(Time.timeScale, Is.EqualTo(1f));
        }

        [Test]
        public void OrphanedZero_IsNormalizedBeforeRunningScene()
        {
            Time.timeScale = 0f;

            bool result = ExperimentSimulationPauseAuthority.NormalizeForRunningScene("test_scene_entry");

            Assert.That(result, Is.True);
            Assert.That(Time.timeScale, Is.EqualTo(1f));
            Assert.That(ExperimentSimulationPauseAuthority.ActiveOwnerCount, Is.Zero);
        }

        [Test]
        public void RealPause_AfterReleasedGate_StaysZeroUntilContinue()
        {
            using (ExperimentSimulationPauseAuthority.Acquire("experimental_xr_alignment_gate", "test_gate"))
            {
                Assert.That(Time.timeScale, Is.Zero);
            }

            CreateCoordinator();
            Assert.That(_coordinator.EnterPause("pause_menu_open"), Is.True);
            Assert.That(Time.timeScale, Is.Zero);
            Assert.That(ExperimentRuntimePauseCoordinator.IsExperimentPaused, Is.True);

            Assert.That(_coordinator.ResumeFromPause("pause_menu_continue"), Is.True);
            Assert.That(Time.timeScale, Is.EqualTo(1f));
            Assert.That(ExperimentRuntimePauseCoordinator.IsExperimentPaused, Is.False);
        }

        [Test]
        public void CleanupForSceneExit_ReleasesMenuOwnerAndRestoresRunningTime()
        {
            CreateCoordinator();
            _coordinator.EnterPause("pause_menu_open");

            Assert.That(_coordinator.CleanupPauseState("exit_to_start"), Is.True);

            Assert.That(ExperimentSimulationPauseAuthority.ActiveOwnerCount, Is.Zero);
            Assert.That(Time.timeScale, Is.EqualTo(1f));
            Assert.That(ExperimentRuntimePauseCoordinator.IsExperimentPaused, Is.False);
        }

        [Test]
        public void PauseCoordinator_PreservesValidNonDefaultScale()
        {
            Time.timeScale = 0.35f;
            CreateCoordinator();

            _coordinator.EnterPause("valid_non_default_scale");
            _coordinator.ResumeFromPause("continue");

            Assert.That(Time.timeScale, Is.EqualTo(0.35f).Within(0.0001f));
            Assert.That(ExperimentSimulationPauseAuthority.ActiveOwnerCount, Is.Zero);
        }

        private void CreateCoordinator()
        {
            _host = new GameObject("p46o04_pause_authority_test");
            _coordinator = ExperimentRuntimePauseCoordinator.EnsureFor(_host);
            _coordinator.CleanupPauseState("p46o04_test_setup");
        }
    }
}
