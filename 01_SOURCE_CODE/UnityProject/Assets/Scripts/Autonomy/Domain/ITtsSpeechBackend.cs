namespace Autonomy.Domain
{
    public interface ITtsSpeechBackend
    {
        bool IsAvailable { get; }
        bool IsSpeaking { get; }
        string UnavailableReason { get; }
        void Configure(string languageOrVoice, float volume, float rate);
        bool TrySpeak(string text, out string failureReason);
        void Tick();
        void Stop();
    }
}
