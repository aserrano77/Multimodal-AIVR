namespace Autonomy.UnityIntegration
{
    public enum TiagoAutonomousPolicy
    {
        Safe,
        Standard,
        FastDemo
    }

    public enum TiagoDriveProfile
    {
        Conservative = 0,
        Realistic = 1,
        Agile = 2,
        Arcade = 3,

        [UnityEngine.InspectorName("Legacy Compatibility/Custom")]
        [System.Obsolete("Compatibility only. Disable profile application and use serialized custom parameters instead.")]
        Custom = 4,
        [UnityEngine.InspectorName("Legacy Compatibility/Autonomous Safe Profile")]
        [System.Obsolete("Compatibility only. Use Realistic; autonomy maps it to Safe policy.")]
        AutonomousSafeProfile = 5,
        [UnityEngine.InspectorName("Legacy Compatibility/Autonomous FastDemo Profile")]
        [System.Obsolete("Compatibility only. Use Arcade; autonomy maps it to FastDemo policy.")]
        AutonomousFastDemoProfile = 6,
        [UnityEngine.InspectorName("Legacy Compatibility/Autonomous Fast Profile")]
        [System.Obsolete("Compatibility only. Use Arcade; autonomy maps it to FastDemo policy.")]
        AutonomousFastProfile = 7,
        [UnityEngine.InspectorName("Legacy Compatibility/Assisted Fast Profile")]
        [System.Obsolete("Compatibility only. Use Arcade; autonomy maps it to FastDemo policy.")]
        AssistedFastProfile = 7
    }

    public readonly struct TiagoDriveProfileSettings
    {
        public TiagoDriveProfileSettings(
            float maxLinearSpeed,
            float maxAngularSpeed,
            float acceleration,
            float angularGain,
            float pathLookAheadDistance,
            bool fastDemoTracking = false,
            float fastMinLookaheadDistance = 0f,
            float fastMaxLookaheadDistance = 0f,
            float fastLookaheadSpeedFactor = 0f,
            float fastCornerBrakeDistance = 0f,
            float fastMediumTurnAngleDeg = 0f,
            float fastSevereTurnAngleDeg = 0f,
            float fastMinCornerSpeed = 0f,
            float fastDeceleration = 0f,
            float fastHeadingSpeedLimitStartAngleDeg = 0f,
            float fastCornerCrawlAngleDeg = 0f)
        {
            MaxLinearSpeed = maxLinearSpeed;
            MaxAngularSpeed = maxAngularSpeed;
            Acceleration = acceleration;
            AngularGain = angularGain;
            PathLookAheadDistance = pathLookAheadDistance;
            FastDemoTracking = fastDemoTracking;
            FastMinLookaheadDistance = fastMinLookaheadDistance;
            FastMaxLookaheadDistance = fastMaxLookaheadDistance;
            FastLookaheadSpeedFactor = fastLookaheadSpeedFactor;
            FastCornerBrakeDistance = fastCornerBrakeDistance;
            FastMediumTurnAngleDeg = fastMediumTurnAngleDeg;
            FastSevereTurnAngleDeg = fastSevereTurnAngleDeg;
            FastMinCornerSpeed = fastMinCornerSpeed;
            FastDeceleration = fastDeceleration;
            FastHeadingSpeedLimitStartAngleDeg = fastHeadingSpeedLimitStartAngleDeg;
            FastCornerCrawlAngleDeg = fastCornerCrawlAngleDeg;
        }

        public float MaxLinearSpeed { get; }
        public float MaxAngularSpeed { get; }
        public float Acceleration { get; }
        public float AngularGain { get; }
        public float PathLookAheadDistance { get; }
        public bool FastDemoTracking { get; }
        public float FastMinLookaheadDistance { get; }
        public float FastMaxLookaheadDistance { get; }
        public float FastLookaheadSpeedFactor { get; }
        public float FastCornerBrakeDistance { get; }
        public float FastMediumTurnAngleDeg { get; }
        public float FastSevereTurnAngleDeg { get; }
        public float FastMinCornerSpeed { get; }
        public float FastDeceleration { get; }
        public float FastHeadingSpeedLimitStartAngleDeg { get; }
        public float FastCornerCrawlAngleDeg { get; }

        public static TiagoDriveProfileSettings Resolve(TiagoDriveProfile profile)
        {
            switch (NormalizePublicProfile(profile))
            {
                case TiagoDriveProfile.Conservative:
                    return new TiagoDriveProfileSettings(0.4f, 0.8f, 6f, 2.0f, 0.65f);
                case TiagoDriveProfile.Realistic:
                    return new TiagoDriveProfileSettings(0.7f, 1.5f, 10f, 2.5f, 0.8f);
                case TiagoDriveProfile.Agile:
                    return new TiagoDriveProfileSettings(1.0f, 2.0f, 14f, 3.0f, 1.0f);
                case TiagoDriveProfile.Arcade:
                    return new TiagoDriveProfileSettings(1.4f, 2.8f, 20f, 3.4f, 1.15f);
                default:
                    return new TiagoDriveProfileSettings(0f, 0f, 0f, 0f, 0f);
            }
        }

        public static TiagoDriveProfileSettings ResolveAutonomy(TiagoDriveProfile profile)
        {
            return ResolveAutonomousPolicy(profile) == TiagoAutonomousPolicy.FastDemo ? ControlledFastDemo() : Resolve(profile);
        }

        public static TiagoDriveProfile NormalizePublicProfile(TiagoDriveProfile profile)
        {
            switch ((int)profile)
            {
                case 5:
                    return TiagoDriveProfile.Realistic;
                case 6:
                case 7:
                    return TiagoDriveProfile.Arcade;
                default:
                    return profile;
            }
        }

        public static bool IsCustomProfile(TiagoDriveProfile profile)
        {
            return (int)profile == 4;
        }

        public static TiagoAutonomousPolicy ResolveAutonomousPolicy(TiagoDriveProfile profile)
        {
            switch (NormalizePublicProfile(profile))
            {
                case TiagoDriveProfile.Realistic:
                    return TiagoAutonomousPolicy.Safe;
                case TiagoDriveProfile.Arcade:
                    return TiagoAutonomousPolicy.FastDemo;
                default:
                    return TiagoAutonomousPolicy.Standard;
            }
        }

        private static TiagoDriveProfileSettings ControlledFastDemo()
        {
            return new TiagoDriveProfileSettings(
                1.15f,
                2.2f,
                1.6f,
                3.0f,
                0.8f,
                fastDemoTracking: true,
                fastMinLookaheadDistance: 0.75f,
                fastMaxLookaheadDistance: 1.35f,
                fastLookaheadSpeedFactor: 0.45f,
                fastCornerBrakeDistance: 1.05f,
                fastMediumTurnAngleDeg: 30f,
                fastSevereTurnAngleDeg: 55f,
                fastMinCornerSpeed: 0.10f,
                fastDeceleration: 3.0f,
                fastHeadingSpeedLimitStartAngleDeg: 10f,
                fastCornerCrawlAngleDeg: 55f);
        }

        public static string GetDriveProfileName(TiagoDriveProfile profile)
        {
            return NormalizePublicProfile(profile).ToString();
        }

        public static string GetAutonomousPolicyName(TiagoDriveProfile profile)
        {
            switch (ResolveAutonomousPolicy(profile))
            {
                case TiagoAutonomousPolicy.Safe:
                    return "Safe";
                case TiagoAutonomousPolicy.FastDemo:
                    return "FastDemo";
                default:
                    return "Standard";
            }
        }
    }
}
