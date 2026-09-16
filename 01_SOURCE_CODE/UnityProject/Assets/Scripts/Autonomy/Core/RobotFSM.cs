using System;
using UnityEngine;

namespace Autonomy.Core
{
    public class RobotFSM
    {
        public RobotMode CurrentMode { get; private set; }

        public event Action<RobotMode, RobotMode> ModeChanged;

        public RobotFSM(RobotMode initialMode = RobotMode.Idle)
        {
            CurrentMode = initialMode;
        }

        public bool TryChangeMode(RobotMode targetMode)
        {
            if (CurrentMode == targetMode)
                return false;

            // Las reglas de transición están intencionadamente simplificadas y son un punto de extensión futuro.
            var previous = CurrentMode;
            CurrentMode = targetMode;

            if (CurrentMode == RobotMode.Autonomous)
            {
                Debug.Log("[Autonomy] Mode changed to: Autonomous");
            }
            else if (CurrentMode == RobotMode.Idle)
            {
                Debug.Log("[Autonomy] FSM -> Idle");
            }

            ModeChanged?.Invoke(previous, CurrentMode);
            return true;
        }

        public void ForceSafetyPause()
        {
            if (CurrentMode == RobotMode.SafetyPause)
                return;

            var previous = CurrentMode;
            CurrentMode = RobotMode.SafetyPause;

            Debug.Log("[Autonomy] FSM -> SafetyPause");

            ModeChanged?.Invoke(previous, CurrentMode);
        }
    }
}
