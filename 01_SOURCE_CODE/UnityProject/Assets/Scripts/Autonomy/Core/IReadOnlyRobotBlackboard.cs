namespace Autonomy.Core
{
    public interface IReadOnlyRobotBlackboard
    {
        RobotMode CurrentMode { get; }

        bool TryGet<T>(BlackboardKey<T> key, out T value);
    }
}
