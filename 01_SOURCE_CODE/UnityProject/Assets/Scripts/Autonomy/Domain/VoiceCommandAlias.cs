namespace Autonomy.Domain
{
    public sealed class VoiceCommandAlias
    {
        public VoiceCommandAlias(string alias, string canonical, string reason = "")
        {
            Alias = alias ?? string.Empty;
            Canonical = canonical ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        public string Alias { get; }
        public string Canonical { get; }
        public string Reason { get; }
    }
}
