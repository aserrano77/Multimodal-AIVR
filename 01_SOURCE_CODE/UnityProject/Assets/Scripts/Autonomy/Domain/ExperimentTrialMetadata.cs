namespace Autonomy.Domain
{
    public sealed class ExperimentTrialMetadata
    {
        public ExperimentTrialMetadata(
            ExperimentSessionMetadata session,
            string runId,
            string trialId,
            int trialIndex,
            string timestampStart)
        {
            Session = session;
            RunId = runId ?? string.Empty;
            TrialId = trialId ?? string.Empty;
            TrialIndex = trialIndex;
            TimestampStart = timestampStart ?? string.Empty;
        }

        public ExperimentSessionMetadata Session { get; }
        public string RunId { get; }
        public string TrialId { get; }
        public int TrialIndex { get; }
        public string TimestampStart { get; }
    }
}
