using System.Numerics;
using Autonomy.BT.Core;

namespace Autonomy.Services
{
    public interface INavigationService
    {
        NodeStatus MoveTo(Vector3 position);
        void Stop();
    }
}
