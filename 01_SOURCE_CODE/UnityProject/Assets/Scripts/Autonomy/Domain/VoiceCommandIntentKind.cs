namespace Autonomy.Domain
{
    public enum VoiceCommandIntentKind
    {
        None,
        Pick,
        PickAndPlace,
        Stop,
        Resume,
        Continue,
        CancelPendingOrder,
        NearestToUser,
        NearestToRobot,
        NearestAmbiguity
    }
}
