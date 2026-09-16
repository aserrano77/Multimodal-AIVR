using System.Collections.Generic;

namespace Autonomy.Domain
{
    public static class ExperimentTrialPlanBuilder
    {
        public static List<ExperimentTrialPlanEntry> Build(
            IReadOnlyList<Experiment2x2ConditionDefinition> conditions,
            int roundsPerCondition)
        {
            var plan = new List<ExperimentTrialPlanEntry>();
            if (conditions == null || conditions.Count == 0)
            {
                return plan;
            }

            int normalizedRounds = roundsPerCondition < 1 ? 1 : roundsPerCondition;
            int trialIndex = 1;
            int globalRoundIndex = 1;
            for (int conditionIndex = 0; conditionIndex < conditions.Count; conditionIndex++)
            {
                Experiment2x2ConditionDefinition condition = conditions[conditionIndex];
                for (int roundWithinCondition = 1; roundWithinCondition <= normalizedRounds; roundWithinCondition++)
                {
                    plan.Add(new ExperimentTrialPlanEntry(
                        condition,
                        conditionIndex,
                        trialIndex,
                        globalRoundIndex,
                        roundWithinCondition,
                        normalizedRounds,
                        globalRoundIndex));
                    trialIndex++;
                    globalRoundIndex++;
                }
            }

            return plan;
        }
    }
}
