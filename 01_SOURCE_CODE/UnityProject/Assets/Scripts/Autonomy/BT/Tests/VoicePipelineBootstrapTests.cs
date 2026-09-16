using System.Collections.Generic;
using Autonomy.UnityIntegration;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    public sealed class VoicePipelineBootstrapTests
    {
        [Test]
        public void GrantedPermission_DoesNotRequestAgain_AndAllowsPreparedC11()
        {
            Assert.That(
                VoicePipelineReadinessPolicy.ShouldRequestPermission(MicrophonePermissionState.Granted),
                Is.False);
            Assert.That(
                VoicePipelineReadinessPolicy.CanStartC11(
                    MicrophonePermissionState.Granted,
                    VoicePipelinePreparationState.Prepared),
                Is.True);
        }

        [TestCase(MicrophonePermissionState.Unknown)]
        [TestCase(MicrophonePermissionState.Denied)]
        public void MissingOrRetryablePermission_IsRequestedBeforeC11(MicrophonePermissionState state)
        {
            Assert.That(VoicePipelineReadinessPolicy.ShouldRequestPermission(state), Is.True);
            Assert.That(
                VoicePipelineReadinessPolicy.CanStartC11(state, VoicePipelinePreparationState.Prepared),
                Is.False);
        }

        [Test]
        public void DeniedDontAskAgain_IsBlockedWithoutRepeatedRequest()
        {
            Assert.That(
                VoicePipelineReadinessPolicy.ShouldRequestPermission(MicrophonePermissionState.DeniedDontAskAgain),
                Is.False);
            Assert.That(
                VoicePipelineReadinessPolicy.CanStartC11(
                    MicrophonePermissionState.DeniedDontAskAgain,
                    VoicePipelinePreparationState.Prepared),
                Is.False);
        }

        [TestCase(VoicePipelinePreparationState.NotStarted)]
        [TestCase(VoicePipelinePreparationState.WaitingForPermission)]
        [TestCase(VoicePipelinePreparationState.PreparingModel)]
        [TestCase(VoicePipelinePreparationState.CreatingRecognizer)]
        [TestCase(VoicePipelinePreparationState.Failed)]
        public void C11CannotStartUntilRecognizerIsPrepared(VoicePipelinePreparationState state)
        {
            Assert.That(
                VoicePipelineReadinessPolicy.CanStartC11(MicrophonePermissionState.Granted, state),
                Is.False);
        }

        [TestCase("C00_robot_off_voice_off")]
        [TestCase("C10_robot_on_voice_off")]
        [TestCase("")]
        public void VoiceDisabledConditionsNeverCaptureEvenWhenRecognizerIsPrepared(string conditionId)
        {
            Assert.That(
                VoicePipelineReadinessPolicy.ShouldCaptureVoice(
                    conditionId,
                    true,
                    MicrophonePermissionState.Granted,
                    VoicePipelinePreparationState.Prepared),
                Is.False);
        }

        [Test]
        public void C11DoesNotCaptureBeforeTrialBoundary()
        {
            Assert.That(
                VoicePipelineReadinessPolicy.ShouldCaptureVoice(
                    VoicePipelineReadinessPolicy.VoiceEnabledConditionId,
                    false,
                    MicrophonePermissionState.Granted,
                    VoicePipelinePreparationState.Prepared),
                Is.False);
        }

        [Test]
        public void PreparedC11CapturesFromTrialStart()
        {
            Assert.That(
                VoicePipelineReadinessPolicy.ShouldCaptureVoice(
                    VoicePipelineReadinessPolicy.VoiceEnabledConditionId,
                    true,
                    MicrophonePermissionState.Granted,
                    VoicePipelinePreparationState.Prepared),
                Is.True);
        }

        [Test]
        public void RequiredEventOrder_PreparesAndActivatesBeforeC11TrialStart()
        {
            string[] sequence =
            {
                "app_voice_bootstrap_started",
                "microphone_permission_granted",
                "asr_model_load_started",
                "asr_model_load_completed",
                "asr_recognizer_create_started",
                "asr_recognizer_create_completed",
                "voice_pipeline_prepared",
                "c11_condition_entered",
                "microphone_open_started",
                "microphone_open_completed",
                "voice_pipeline_activated",
                "c11_trial_started"
            };

            Assert.That(
                VoicePipelineReadinessPolicy.HasPreparedAndActivatedBeforeC11TrialStarted(sequence),
                Is.True);
        }

        [Test]
        public void InvalidEventOrder_IsRejected()
        {
            List<string> sequence = new()
            {
                "c11_trial_started",
                "voice_pipeline_prepared",
                "voice_pipeline_activated"
            };

            Assert.That(
                VoicePipelineReadinessPolicy.HasPreparedAndActivatedBeforeC11TrialStarted(sequence),
                Is.False);
        }
    }
}
