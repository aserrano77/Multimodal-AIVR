namespace Autonomy.Domain
{
    public sealed class ExperimentTrialPlanEntry
    {
        public ExperimentTrialPlanEntry(
            Experiment2x2ConditionDefinition condition,
            int conditionOrderIndex,
            int trialIndex,
            int roundIndex,
            int roundIndexWithinCondition,
            int roundsPerCondition,
            int globalRoundIndex)
        {
            Condition = condition;
            ConditionOrderIndex = conditionOrderIndex;
            TrialIndex = trialIndex;
            RoundIndex = roundIndex;
            RoundIndexWithinCondition = roundIndexWithinCondition;
            RoundsPerCondition = roundsPerCondition;
            GlobalRoundIndex = globalRoundIndex;
        }

        public Experiment2x2ConditionDefinition Condition { get; }
        public int ConditionOrderIndex { get; }
        public int TrialIndex { get; }
        public int RoundIndex { get; }
        public int RoundIndexWithinCondition { get; }
        public int RoundsPerCondition { get; }
        public int GlobalRoundIndex { get; }
    }
}
