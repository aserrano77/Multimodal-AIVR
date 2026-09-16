namespace Autonomy.BT.Core
{
    public abstract class Node
    {
        public abstract NodeStatus Tick();

        public virtual void Reset() { }
    }
}
