namespace Autonomy.Domain
{
    public sealed class VoiceConfirmationManager
    {
        private MultimodalTaskIntent _pendingIntent;
        private VoiceCommandIntentMappingResult _pendingMapping;
        private float _expiresAtSeconds;

        public bool HasPendingIntent => _pendingIntent != null;
        public MultimodalTaskIntent PendingIntent => _pendingIntent;
        public VoiceCommandIntentMappingResult PendingMapping => _pendingMapping;
        public float ExpiresAtSeconds => _expiresAtSeconds;

        public void SetPending(
            MultimodalTaskIntent intent,
            VoiceCommandIntentMappingResult mapping,
            float nowSeconds,
            float timeoutSeconds)
        {
            _pendingIntent = intent;
            _pendingMapping = mapping;
            _expiresAtSeconds = nowSeconds + timeoutSeconds;
        }

        public bool TryAccept(float nowSeconds, out MultimodalTaskIntent intent, out VoiceCommandIntentMappingResult mapping)
        {
            if (TryExpire(nowSeconds, out _, out _))
            {
                intent = null;
                mapping = null;
                return false;
            }

            intent = _pendingIntent;
            mapping = _pendingMapping;
            Clear();
            return intent != null;
        }

        public bool TryCancel(out MultimodalTaskIntent intent, out VoiceCommandIntentMappingResult mapping)
        {
            intent = _pendingIntent;
            mapping = _pendingMapping;
            Clear();
            return intent != null;
        }

        public bool TryExpire(float nowSeconds, out MultimodalTaskIntent intent, out VoiceCommandIntentMappingResult mapping)
        {
            intent = _pendingIntent;
            mapping = _pendingMapping;
            if (_pendingIntent == null || nowSeconds < _expiresAtSeconds)
            {
                return false;
            }

            Clear();
            return true;
        }

        public void Clear()
        {
            _pendingIntent = null;
            _pendingMapping = null;
            _expiresAtSeconds = 0f;
        }
    }
}
