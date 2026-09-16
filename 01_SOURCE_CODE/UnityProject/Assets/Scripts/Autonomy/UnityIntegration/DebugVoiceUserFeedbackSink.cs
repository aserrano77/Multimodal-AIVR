using Autonomy.Domain;
using UnityEngine;
using UnityEngine.UI;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class DebugVoiceUserFeedbackSink : MonoBehaviour, IVoiceUserFeedbackSink, IRobotVoiceFeedbackSink
    {
        private const string LogPrefix = "[VoiceUserFeedback]";

        [SerializeField] private bool _writeToDebugLog = true;
        [SerializeField] private Text _optionalFeedbackTextTarget;

        public void Emit(VoiceUserFeedbackMessage message)
        {
            if (message == null)
            {
                return;
            }

            if (_optionalFeedbackTextTarget != null)
            {
                _optionalFeedbackTextTarget.text = message.Text;
            }

            if (_writeToDebugLog)
            {
                Debug.Log($"{LogPrefix} {message.Type} | {message.Text}", this);
            }
        }

        public void Emit(RobotVoiceFeedbackMessage message)
        {
            if (message == null)
            {
                return;
            }

            if (_optionalFeedbackTextTarget != null)
            {
                _optionalFeedbackTextTarget.text = message.Text;
            }

            if (_writeToDebugLog)
            {
                Debug.Log($"{LogPrefix} {message.Kind} | {message.Text}", this);
            }
        }
    }
}
