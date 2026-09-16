using Autonomy.Core;

namespace Autonomy.Domain
{
    public static class TaskBlackboardKeys
    {
        public static readonly BlackboardKey<TargetDescriptor> CurrentTarget = new BlackboardKey<TargetDescriptor>("CurrentTarget");
        public static readonly BlackboardKey<TargetDescriptor> PlaceTarget = new BlackboardKey<TargetDescriptor>("PlaceTarget");
        public static readonly BlackboardKey<string> ResumeHeldObjectId = new BlackboardKey<string>("ResumeHeldObjectId");
        public static readonly BlackboardKey<TaskStatus> LastTaskStatus = new BlackboardKey<TaskStatus>("LastTaskStatus");
    }
}
