using System.Collections.Generic;
using Autonomy.BT.Core;
using Autonomy.UnityIntegration;

namespace Autonomy.BT.Nodes.Actions
{
    public class LogBtEventAction : Node
    {
        private readonly string _eventName;
        private bool _logged;

        public LogBtEventAction(string eventName)
        {
            _eventName = eventName;
        }

        public override NodeStatus Tick()
        {
            if (!_logged && !string.IsNullOrWhiteSpace(_eventName))
            {
                TiagoExperimentTelemetry.LogEvent(
                    _eventName,
                    new Dictionary<string, object>
                    {
                        ["bt_event"] = _eventName
                    });
                _logged = true;
            }

            return NodeStatus.Success;
        }

        public override void Reset()
        {
            _logged = false;
            base.Reset();
        }
    }
}
