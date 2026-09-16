using System;
using Autonomy.Core;
using Autonomy.BT.Core;
using Autonomy.Domain;
using Autonomy.Services;

namespace Autonomy.Integration
{
    /// <summary>
    /// Orquestador de alto nivel encargado de sincronizar el ciclo de vida de la FSM
    /// con la ejecución del Behavior Tree. Implementa IDisposable para asegurar la
    /// limpieza de suscripciones a eventos.
    /// </summary>
    public class RobotController : IDisposable
    {
        private readonly RobotFSM _fsm;
        private readonly IRobotBlackboard _blackboard;
        private readonly BehaviorTreeRunner _btRunner;
        private readonly INavigationService _navigationService;
        private readonly IManipulationService _manipulationService;
        private readonly ISafetyService _safetyService;
        private bool _isDisposed;

        // Defensa contra relanzamientos espurios: memoriza el target que no fue limpiado por el BT.
        private TargetDescriptor _cooldownTarget = null;

        public RobotMode CurrentMode => _fsm.CurrentMode;

        public TaskStatus LastTaskStatus
        {
            get
            {
                return _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out var status)
                    ? status
                    : TaskStatus.None;
            }
        }

        public RobotController(
            RobotFSM fsm, 
            IRobotBlackboard blackboard,
            BehaviorTreeRunner btRunner,
            INavigationService navigationService,
            IManipulationService manipulationService,
            ISafetyService safetyService)
        {
            _fsm = fsm ?? throw new ArgumentNullException(nameof(fsm));
            _blackboard = blackboard ?? throw new ArgumentNullException(nameof(blackboard));
            _btRunner = btRunner ?? throw new ArgumentNullException(nameof(btRunner));
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _manipulationService = manipulationService ?? throw new ArgumentNullException(nameof(manipulationService));
            _safetyService = safetyService ?? throw new ArgumentNullException(nameof(safetyService));

            _fsm.ModeChanged += HandleModeChanged;

            if (_fsm.CurrentMode != RobotMode.Autonomous)
            {
                _btRunner.Reset();
            }
        }

        public void Tick()
        {
            bool isSafe = _safetyService.IsSafeToOperate();

            if (_fsm.CurrentMode == RobotMode.Autonomous && !isSafe)
            {
                _fsm.ForceSafetyPause();
                return;
            }

            if (_fsm.CurrentMode == RobotMode.Idle)
            {
                bool hasTarget = _blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out var currentTarget);
                
                // Levantamos el bloqueo si no hay target o si el target inyectado es distinto al que falló.
                if (!hasTarget || currentTarget == null || currentTarget != _cooldownTarget)
                {
                    _cooldownTarget = null;
                }

                if (hasTarget && currentTarget != null && isSafe && _cooldownTarget == null)
                {
                    _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.None);
                    _fsm.TryChangeMode(RobotMode.Autonomous);
                }
            }

            if (_fsm.CurrentMode == RobotMode.Autonomous)
            {
                if (TryCompleteTerminalTaskCycle())
                {
                    return;
                }

                _btRunner.Tick();

                TryCompleteTerminalTaskCycle();
            }
        }

        public bool TryResetForNewTask(out TaskStatus previousTaskStatus, out RobotMode previousMode, out bool wasTerminal, out string rejectionReason)
        {
            previousTaskStatus = LastTaskStatus;
            previousMode = _fsm.CurrentMode;
            wasTerminal = previousTaskStatus == TaskStatus.Succeeded || previousTaskStatus == TaskStatus.Failed;
            rejectionReason = string.Empty;

            if (previousTaskStatus == TaskStatus.InProgress)
            {
                rejectionReason = "task_in_progress";
                return false;
            }

            if (previousMode == RobotMode.SafetyPause)
            {
                rejectionReason = "robot_in_safety_pause";
                return false;
            }

            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.None);
            _cooldownTarget = null;
            _btRunner.Reset();
            return true;
        }

        public void CancelInProgressTaskForReplacement(string reason)
        {
            StopAllServices();
            _blackboard.Remove(TaskBlackboardKeys.CurrentTarget);
            _blackboard.Remove(TaskBlackboardKeys.PlaceTarget);
            _blackboard.Remove(TaskBlackboardKeys.ResumeHeldObjectId);
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.None);
            _cooldownTarget = null;
            _btRunner.Reset();
            _fsm.TryChangeMode(RobotMode.Idle);
        }

        public void ResetForExperimentTrial()
        {
            StopAllServices();
            _blackboard.Remove(TaskBlackboardKeys.CurrentTarget);
            _blackboard.Remove(TaskBlackboardKeys.PlaceTarget);
            _blackboard.Remove(TaskBlackboardKeys.ResumeHeldObjectId);
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.None);
            _cooldownTarget = null;
            _btRunner.Reset();
            _fsm.TryChangeMode(RobotMode.Idle);
        }

        private bool TryCompleteTerminalTaskCycle()
        {
                if (_blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out var status))
                {
                    if (status == TaskStatus.Succeeded || status == TaskStatus.Failed)
                    {
                        // Registramos el target actual como problemático si no fue limpiado por el BT.
                        if (_blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out var leftOverTarget))
                        {
                            _cooldownTarget = leftOverTarget;
                        }
                        
                        _fsm.TryChangeMode(RobotMode.Idle);
                        return true;
                    }
                }

            return false;
        }

        private void HandleModeChanged(RobotMode previousMode, RobotMode newMode)
        {
            if (newMode == RobotMode.Idle || newMode == RobotMode.SafetyPause)
            {
                StopAllServices();
            }

            if (newMode == RobotMode.Idle)
            {
                _btRunner.Reset();
            }
        }

        private void StopAllServices()
        {
            _navigationService.Stop();
            _manipulationService.Stop();
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            
            _fsm.ModeChanged -= HandleModeChanged;
            _isDisposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
