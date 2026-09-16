using System.Collections.Generic;

namespace Autonomy.Domain
{
    public sealed class ExperimentTrialPlanCursor
    {
        private readonly List<ExperimentTrialPlanEntry> _plan = new();

        public int CurrentEntryIndex { get; private set; } = -1;
        public int NextEntryIndex { get; private set; }
        public int Count => _plan.Count;
        public IReadOnlyList<ExperimentTrialPlanEntry> Plan => _plan;

        public void Reset(IEnumerable<ExperimentTrialPlanEntry> plan)
        {
            _plan.Clear();
            if (plan != null)
            {
                _plan.AddRange(plan);
            }

            CurrentEntryIndex = -1;
            NextEntryIndex = 0;
        }

        public void Clear()
        {
            _plan.Clear();
            CurrentEntryIndex = -1;
            NextEntryIndex = 0;
        }

        public bool TryAdvance(out ExperimentTrialPlanEntry entry)
        {
            entry = null;
            if (NextEntryIndex < 0 || NextEntryIndex >= _plan.Count)
            {
                return false;
            }

            CurrentEntryIndex = NextEntryIndex;
            NextEntryIndex++;
            entry = _plan[CurrentEntryIndex];
            return true;
        }

        public bool TryGetCurrent(out ExperimentTrialPlanEntry entry)
        {
            entry = null;
            if (CurrentEntryIndex < 0 || CurrentEntryIndex >= _plan.Count)
            {
                return false;
            }

            entry = _plan[CurrentEntryIndex];
            return true;
        }

        public bool TryGetNext(out ExperimentTrialPlanEntry entry)
        {
            entry = null;
            if (NextEntryIndex < 0 || NextEntryIndex >= _plan.Count)
            {
                return false;
            }

            entry = _plan[NextEntryIndex];
            return true;
        }
    }
}
