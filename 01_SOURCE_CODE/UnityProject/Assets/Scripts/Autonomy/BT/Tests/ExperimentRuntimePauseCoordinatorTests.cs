using Autonomy.Domain;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using UnityEngine;

namespace Autonomy.BT.Tests
{
    public sealed class ExperimentRuntimePauseCoordinatorTests
    {
        private float _originalTimeScale;
        private GameObject _coordinatorHost;
        private ExperimentRuntimePauseCoordinator _coordinator;

        [SetUp]
        public void SetUp()
        {
            _originalTimeScale = Time.timeScale;
            ExperimentSimulationPauseAuthority.ResetForTests();
            _coordinatorHost = new GameObject("p46l_pause_coordinator_test");
            _coordinator = ExperimentRuntimePauseCoordinator.EnsureFor(_coordinatorHost);
            _coordinator.CleanupPauseState("test_setup");
        }

        [TearDown]
        public void TearDown()
        {
            if (_coordinator != null)
            {
                _coordinator.CleanupPauseState("test_teardown");
            }

            if (_coordinatorHost != null)
            {
                Object.DestroyImmediate(_coordinatorHost);
            }

            foreach (GameObject probe in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (probe != null && probe.name.StartsWith("p46l_probe_", System.StringComparison.Ordinal))
                {
                    Object.DestroyImmediate(probe);
                }
            }

            ExperimentSimulationPauseAuthority.ResetForTests();
            Time.timeScale = _originalTimeScale;
        }

        [Test]
        public void EnterAndResume_CapturesExactTimeScale_AndIsIdempotent()
        {
            Time.timeScale = 0.35f;

            bool firstEnter = _coordinator.EnterPause("test_first");
            bool repeatedEnter = _coordinator.EnterPause("test_repeated");

            Assert.That(firstEnter, Is.True);
            Assert.That(repeatedEnter, Is.False);
            Assert.That(ExperimentRuntimePauseCoordinator.IsExperimentPaused, Is.True);
            Assert.That(Time.timeScale, Is.Zero);
            Assert.That(_coordinator.TimeScaleBeforePause, Is.EqualTo(0.35f).Within(0.0001f));

            bool firstResume = _coordinator.ResumeFromPause("test_continue");
            bool repeatedResume = _coordinator.ResumeFromPause("test_repeated_continue");

            Assert.That(firstResume, Is.True);
            Assert.That(repeatedResume, Is.False);
            Assert.That(ExperimentRuntimePauseCoordinator.IsExperimentPaused, Is.False);
            Assert.That(Time.timeScale, Is.EqualTo(0.35f).Within(0.0001f));
        }

        [Test]
        public void Pause_DisablesLocomotionTeleportAndGrab_ButKeepsTrackingAndUiRay()
        {
            TestDynamicMoveProvider move = AddProbe<TestDynamicMoveProvider>("p46l_probe_move");
            TestSnapTurnProvider turn = AddProbe<TestSnapTurnProvider>("p46l_probe_turn");
            ControllerInputActionManager controllerInput = AddProbe<ControllerInputActionManager>("p46l_probe_controller_input");
            XRDirectInteractor direct = AddProbe<XRDirectInteractor>("p46l_probe_direct");
            XRGrabInteractable grab = AddProbe<XRGrabInteractable>("p46l_probe_box");
            XRRayInteractor teleportRay = AddProbe<XRRayInteractor>("p46l_probe_Teleport_Interactor");
            XRRayInteractor uiRay = AddProbe<XRRayInteractor>("p46l_probe_UI_Ray");
            TrackedPoseDriver tracking = AddProbe<TrackedPoseDriver>("p46l_probe_hmd_tracking");

            _coordinator.EnterPause("test_xr_controls");

            Assert.That(move.enabled, Is.False);
            Assert.That(turn.enabled, Is.False);
            Assert.That(controllerInput.enabled, Is.False);
            Assert.That(direct.enabled, Is.False);
            Assert.That(grab.enabled, Is.False);
            Assert.That(teleportRay.enabled, Is.False);
            Assert.That(uiRay.enabled, Is.True);
            Assert.That(tracking.enabled, Is.True);
            Assert.That(_coordinator.LocomotionComponentsDisabled, Is.GreaterThanOrEqualTo(3));
            Assert.That(_coordinator.ManipulationComponentsDisabled, Is.GreaterThanOrEqualTo(3));

            _coordinator.ResumeFromPause("test_xr_restore");

            Assert.That(move.enabled, Is.True);
            Assert.That(turn.enabled, Is.True);
            Assert.That(controllerInput.enabled, Is.True);
            Assert.That(direct.enabled, Is.True);
            Assert.That(grab.enabled, Is.True);
            Assert.That(teleportRay.enabled, Is.True);
            Assert.That(uiRay.enabled, Is.True);
            Assert.That(tracking.enabled, Is.True);
        }

        [Test]
        public void PauseContinue_FourCycles_RestoresOriginalComponentStatesEveryTime()
        {
            TestDynamicMoveProvider move = AddProbe<TestDynamicMoveProvider>("p46l_probe_move_cycles");
            XRGrabInteractable initiallyDisabledGrab = AddProbe<XRGrabInteractable>("p46l_probe_box_disabled");
            initiallyDisabledGrab.enabled = false;

            for (int cycle = 0; cycle < 4; cycle++)
            {
                Assert.That(_coordinator.EnterPause($"cycle_{cycle}"), Is.True);
                Assert.That(move.enabled, Is.False);
                Assert.That(initiallyDisabledGrab.enabled, Is.False);
                Assert.That(_coordinator.ResumeFromPause($"cycle_{cycle}"), Is.True);
                Assert.That(move.enabled, Is.True);
                Assert.That(initiallyDisabledGrab.enabled, Is.False);
            }
        }

        private T AddProbe<T>(string name) where T : MonoBehaviour
        {
            return new GameObject(name).AddComponent<T>();
        }

        private sealed class TestDynamicMoveProvider : MonoBehaviour { }
        private sealed class TestSnapTurnProvider : MonoBehaviour { }
        private sealed class ControllerInputActionManager : MonoBehaviour { }
        private sealed class XRDirectInteractor : MonoBehaviour { }
        private sealed class XRGrabInteractable : MonoBehaviour { }
        private sealed class XRRayInteractor : MonoBehaviour { }
        private sealed class TrackedPoseDriver : MonoBehaviour { }
    }
}
