namespace Autonomy.BT.Core
{
    public abstract class DecoratorNode : Node
    {
        protected readonly Node Child;

        protected DecoratorNode(Node child)
        {
            Child = child ?? throw new System.ArgumentNullException(nameof(child));
        }

        public override void Reset()
        {
            Child.Reset();
            base.Reset();
        }
    }
}
