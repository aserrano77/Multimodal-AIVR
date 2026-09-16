namespace Autonomy.Domain
{
    [System.Serializable]
    public sealed class AsrDiagnosticExpectedUtterance
    {
        public string ExpectedPhrase = "";
        public string ExpectedNormalizedText = "";
        public string ExpectedIntent = "";
        public string ExpectedAlias = "";
        public string ExpectedDestination = "";

        public bool HasAnyExpectedValue =>
            !string.IsNullOrWhiteSpace(ExpectedPhrase) ||
            !string.IsNullOrWhiteSpace(ExpectedNormalizedText) ||
            !string.IsNullOrWhiteSpace(ExpectedIntent) ||
            !string.IsNullOrWhiteSpace(ExpectedAlias) ||
            !string.IsNullOrWhiteSpace(ExpectedDestination);
    }
}
