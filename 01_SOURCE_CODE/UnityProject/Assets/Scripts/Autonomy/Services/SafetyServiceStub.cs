namespace Autonomy.Services
{
    public class SafetyServiceStub : ISafetyService
    {
        public bool IsSafe { get; set; } = true;

        public bool IsSafeToOperate()
        {
            return IsSafe;
        }
    }
}
