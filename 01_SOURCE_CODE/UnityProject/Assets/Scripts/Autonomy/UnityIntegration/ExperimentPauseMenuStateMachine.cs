namespace Autonomy.UnityIntegration
{
    public enum ExperimentPauseMenuConfirmation
    {
        None,
        Restart,
        SaveExit,
        ExitWithoutCompletion
    }

    public enum ExperimentPauseMenuCommand
    {
        None,
        OpenPause,
        Continue,
        YIgnoredDueToConfirmation
    }

    public readonly struct ExperimentPauseMenuYButtonResult
    {
        public ExperimentPauseMenuYButtonResult(bool risingEdge, ExperimentPauseMenuCommand command)
        {
            RisingEdge = risingEdge;
            Command = command;
        }

        public bool RisingEdge { get; }
        public ExperimentPauseMenuCommand Command { get; }
    }

    public sealed class ExperimentPauseMenuStateMachine
    {
        private bool _previousYPressed;

        public bool IsPaused { get; private set; }

        public ExperimentPauseMenuConfirmation Confirmation { get; private set; }

        public ExperimentPauseMenuYButtonResult UpdateYPressed(bool yPressed)
        {
            bool risingEdge = yPressed && !_previousYPressed;
            _previousYPressed = yPressed;
            if (!risingEdge)
            {
                return new ExperimentPauseMenuYButtonResult(false, ExperimentPauseMenuCommand.None);
            }

            if (!IsPaused)
            {
                return new ExperimentPauseMenuYButtonResult(true, ExperimentPauseMenuCommand.OpenPause);
            }

            if (Confirmation != ExperimentPauseMenuConfirmation.None)
            {
                return new ExperimentPauseMenuYButtonResult(true, ExperimentPauseMenuCommand.YIgnoredDueToConfirmation);
            }

            return new ExperimentPauseMenuYButtonResult(true, ExperimentPauseMenuCommand.Continue);
        }

        public void Open()
        {
            IsPaused = true;
            Confirmation = ExperimentPauseMenuConfirmation.None;
        }

        public void Continue()
        {
            IsPaused = false;
            Confirmation = ExperimentPauseMenuConfirmation.None;
        }

        public void RequestRestart()
        {
            IsPaused = true;
            Confirmation = ExperimentPauseMenuConfirmation.Restart;
        }

        public void ConfirmRestart()
        {
            IsPaused = false;
            Confirmation = ExperimentPauseMenuConfirmation.None;
        }

        public void RequestSaveExit()
        {
            IsPaused = true;
            Confirmation = ExperimentPauseMenuConfirmation.SaveExit;
        }

        public void ConfirmSaveExit()
        {
            IsPaused = false;
            Confirmation = ExperimentPauseMenuConfirmation.None;
        }

        public void RequestExitWithoutCompletion()
        {
            IsPaused = true;
            Confirmation = ExperimentPauseMenuConfirmation.ExitWithoutCompletion;
        }

        public void ConfirmExitWithoutCompletion()
        {
            IsPaused = false;
            Confirmation = ExperimentPauseMenuConfirmation.None;
        }

        public void CancelConfirmation()
        {
            IsPaused = true;
            Confirmation = ExperimentPauseMenuConfirmation.None;
        }
    }
}
