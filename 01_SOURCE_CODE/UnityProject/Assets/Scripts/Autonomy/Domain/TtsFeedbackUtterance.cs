namespace Autonomy.Domain
{
    public readonly struct TtsFeedbackUtterance
    {
        public TtsFeedbackUtterance(string text, TtsFeedbackPriority priority, string reason)
        {
            Text = text ?? string.Empty;
            Priority = priority;
            Reason = reason ?? string.Empty;
        }

        public string Text { get; }
        public TtsFeedbackPriority Priority { get; }
        public string Reason { get; }
        public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
    }
}
