namespace Autonomy.Core
{
    public interface IRobotBlackboard : IReadOnlyRobotBlackboard
    {
        void SetMode(RobotMode newMode);

        void Set<T>(BlackboardKey<T> key, T value);

        bool Remove<T>(BlackboardKey<T> key);
    }
}
