using UnityEngine;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Bridge minimo de locomocion diferencial para autonomia.
    /// Traduce consignas lineal/angular a velocidades objetivo de ruedas mediante ArticulationBody.
    /// </summary>
    public sealed class TiagoDifferentialDriveBridge
    {
        private readonly ArticulationBody _wheelLeft;
        private readonly ArticulationBody _wheelRight;
        private readonly float _wheelRadius;
        private readonly float _wheelSeparation;
        private readonly int _leftSign;
        private readonly int _rightSign;
        private float _wheelDriveStiffness;
        private float _wheelDriveDamping;
        private float _wheelForceLimit;
        private readonly float _linearVelocityCommandScale;
        private readonly float _angularVelocityCommandScale;
        private readonly float _minWheelTargetDegPerSecond;
        private readonly float _maxWheelTargetDegPerSecond;
        private DriveDiagnostics _lastDiagnostics;

        public TiagoDifferentialDriveBridge(
            ArticulationBody wheelLeft,
            ArticulationBody wheelRight,
            float wheelRadius,
            float wheelSeparation,
            int leftSign,
            int rightSign,
            float wheelForceLimit,
            float linearVelocityCommandScale = 1f,
            float angularVelocityCommandScale = 1f,
            float minWheelTargetDegPerSecond = 0f,
            float maxWheelTargetDegPerSecond = 0f,
            float wheelDriveStiffness = 0f,
            float wheelDriveDamping = 10000f)
        {
            _wheelLeft = wheelLeft;
            _wheelRight = wheelRight;
            _wheelRadius = wheelRadius;
            _wheelSeparation = wheelSeparation;
            _leftSign = leftSign;
            _rightSign = rightSign;
            _wheelForceLimit = Mathf.Max(0f, wheelForceLimit);
            _wheelDriveStiffness = Mathf.Max(0f, wheelDriveStiffness);
            _wheelDriveDamping = Mathf.Max(0f, wheelDriveDamping);
            _linearVelocityCommandScale = Mathf.Max(0f, linearVelocityCommandScale);
            _angularVelocityCommandScale = Mathf.Max(0f, angularVelocityCommandScale);
            _minWheelTargetDegPerSecond = Mathf.Max(0f, minWheelTargetDegPerSecond);
            _maxWheelTargetDegPerSecond = Mathf.Max(0f, maxWheelTargetDegPerSecond);
        }

        public bool IsValid =>
            _wheelLeft != null &&
            _wheelRight != null &&
            _wheelRadius > 0f &&
            _wheelSeparation > 0f;

        public DriveDiagnostics LastDiagnostics => _lastDiagnostics;

        public void ConfigureWheelDrive(float wheelDriveStiffness, float wheelDriveDamping, float wheelForceLimit)
        {
            _wheelDriveStiffness = Mathf.Max(0f, wheelDriveStiffness);
            _wheelDriveDamping = Mathf.Max(0f, wheelDriveDamping);
            _wheelForceLimit = Mathf.Max(0f, wheelForceLimit);
            ConfigureWheel(_wheelLeft);
            ConfigureWheel(_wheelRight);
        }

        public void Initialize()
        {
            ConfigureWheel(_wheelLeft);
            ConfigureWheel(_wheelRight);
        }

        public void ApplyCommand(float linearVelocity, float angularVelocity)
        {
            if (!IsValid)
            {
                return;
            }

            float effectiveLinearVelocity = linearVelocity * _linearVelocityCommandScale;
            float effectiveAngularVelocity = angularVelocity * _angularVelocityCommandScale;
            float leftWheelOmega = (2f * effectiveLinearVelocity - effectiveAngularVelocity * _wheelSeparation) / (2f * _wheelRadius);
            float rightWheelOmega = (2f * effectiveLinearVelocity + effectiveAngularVelocity * _wheelSeparation) / (2f * _wheelRadius);
            float unsignedLeftWheelOmega = leftWheelOmega;
            float unsignedRightWheelOmega = rightWheelOmega;

            leftWheelOmega *= _leftSign;
            rightWheelOmega *= _rightSign;

            SetWheelTargetVelocity(_wheelLeft, leftWheelOmega);
            SetWheelTargetVelocity(_wheelRight, rightWheelOmega);

            _lastDiagnostics = DriveDiagnostics.FromCommand(
                linearVelocity,
                angularVelocity,
                effectiveLinearVelocity,
                effectiveAngularVelocity,
                unsignedLeftWheelOmega,
                unsignedRightWheelOmega,
                leftWheelOmega,
                rightWheelOmega,
                _wheelRadius,
                _wheelSeparation,
                _leftSign,
                _rightSign,
                _linearVelocityCommandScale,
                _angularVelocityCommandScale,
                _minWheelTargetDegPerSecond,
                _maxWheelTargetDegPerSecond,
                _wheelLeft,
                _wheelRight);
        }

        public void ApplyRawWheelTargetsDegPerSecond(float leftTargetDegPerSecond, float rightTargetDegPerSecond)
        {
            if (!IsValid)
            {
                return;
            }

            SetWheelTargetVelocityDegPerSecond(_wheelLeft, leftTargetDegPerSecond);
            SetWheelTargetVelocityDegPerSecond(_wheelRight, rightTargetDegPerSecond);

            float leftSignedRadPerSecond = leftTargetDegPerSecond * Mathf.Deg2Rad;
            float rightSignedRadPerSecond = rightTargetDegPerSecond * Mathf.Deg2Rad;
            _lastDiagnostics = DriveDiagnostics.FromCommand(
                0f,
                0f,
                0f,
                0f,
                _leftSign != 0 ? leftSignedRadPerSecond / _leftSign : leftSignedRadPerSecond,
                _rightSign != 0 ? rightSignedRadPerSecond / _rightSign : rightSignedRadPerSecond,
                leftSignedRadPerSecond,
                rightSignedRadPerSecond,
                _wheelRadius,
                _wheelSeparation,
                _leftSign,
                _rightSign,
                _linearVelocityCommandScale,
                _angularVelocityCommandScale,
                _minWheelTargetDegPerSecond,
                _maxWheelTargetDegPerSecond,
                _wheelLeft,
                _wheelRight);
        }

        public void Stop()
        {
            if (!IsValid)
            {
                return;
            }

            SetWheelTargetVelocity(_wheelLeft, 0f);
            SetWheelTargetVelocity(_wheelRight, 0f);
            _lastDiagnostics = DriveDiagnostics.FromCommand(
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                _wheelRadius,
                _wheelSeparation,
                _leftSign,
                _rightSign,
                _linearVelocityCommandScale,
                _angularVelocityCommandScale,
                _minWheelTargetDegPerSecond,
                _maxWheelTargetDegPerSecond,
                _wheelLeft,
                _wheelRight);
        }

        private void ConfigureWheel(ArticulationBody wheel)
        {
            if (wheel == null)
            {
                return;
            }

            var drive = wheel.xDrive;
            drive.driveType = ArticulationDriveType.Velocity;
            drive.stiffness = _wheelDriveStiffness;
            drive.damping = _wheelDriveDamping;
            drive.forceLimit = _wheelForceLimit;
            drive.targetVelocity = 0f;
            wheel.xDrive = drive;
            wheel.jointFriction = 0f;
        }

        private void SetWheelTargetVelocity(ArticulationBody wheel, float omegaRadPerSecond)
        {
            if (wheel == null)
            {
                return;
            }

            SetWheelTargetVelocityDegPerSecond(wheel, ClampWheelTargetDegPerSecond(omegaRadPerSecond * Mathf.Rad2Deg));
        }

        private float ClampWheelTargetDegPerSecond(float targetDegPerSecond)
        {
            float absTarget = Mathf.Abs(targetDegPerSecond);
            if (_minWheelTargetDegPerSecond > 0f && absTarget > 0.001f && absTarget < _minWheelTargetDegPerSecond)
            {
                targetDegPerSecond = Mathf.Sign(targetDegPerSecond) * _minWheelTargetDegPerSecond;
            }

            if (_maxWheelTargetDegPerSecond > 0f && Mathf.Abs(targetDegPerSecond) > _maxWheelTargetDegPerSecond)
            {
                targetDegPerSecond = Mathf.Sign(targetDegPerSecond) * _maxWheelTargetDegPerSecond;
            }

            return targetDegPerSecond;
        }

        private void SetWheelTargetVelocityDegPerSecond(ArticulationBody wheel, float targetDegPerSecond)
        {
            if (wheel == null)
            {
                return;
            }

            var drive = wheel.xDrive;
            drive.driveType = ArticulationDriveType.Velocity;
            drive.stiffness = _wheelDriveStiffness;
            drive.damping = _wheelDriveDamping;
            drive.forceLimit = _wheelForceLimit;
            drive.targetVelocity = targetDegPerSecond;
            wheel.xDrive = drive;
        }

        public readonly struct DriveDiagnostics
        {
            public float CommandLinear { get; }
            public float CommandAngular { get; }
            public float EffectiveCommandLinear { get; }
            public float EffectiveCommandAngular { get; }
            public float LinearVelocityCommandScale { get; }
            public float AngularVelocityCommandScale { get; }
            public float MinWheelTargetDegPerSec { get; }
            public float MaxWheelTargetDegPerSec { get; }
            public float LeftUnsignedRadPerSec { get; }
            public float RightUnsignedRadPerSec { get; }
            public float LeftSignedRadPerSec { get; }
            public float RightSignedRadPerSec { get; }
            public float LeftTargetDegPerSec { get; }
            public float RightTargetDegPerSec { get; }
            public float LeftDriveTargetDegPerSec { get; }
            public float RightDriveTargetDegPerSec { get; }
            public float LeftJointVelocityDegPerSec { get; }
            public float RightJointVelocityDegPerSec { get; }
            public float LeftLogicalJointVelocityDegPerSec { get; }
            public float RightLogicalJointVelocityDegPerSec { get; }
            public float LeftAbsFollowRatio { get; }
            public float RightAbsFollowRatio { get; }
            public bool LeftSignedDirectionMatchesTarget { get; }
            public bool RightSignedDirectionMatchesTarget { get; }
            public float TheoreticalLinear { get; }
            public float TheoreticalAngular { get; }
            public int LeftSign { get; }
            public int RightSign { get; }
            public string LeftName { get; }
            public string RightName { get; }
            public string LeftJointType { get; }
            public string RightJointType { get; }
            public float LeftDriveStiffness { get; }
            public float RightDriveStiffness { get; }
            public float LeftDriveDamping { get; }
            public float RightDriveDamping { get; }
            public float LeftForceLimit { get; }
            public float RightForceLimit { get; }

            private DriveDiagnostics(
                float commandLinear,
                float commandAngular,
                float effectiveCommandLinear,
                float effectiveCommandAngular,
                float linearVelocityCommandScale,
                float angularVelocityCommandScale,
                float minWheelTargetDegPerSec,
                float maxWheelTargetDegPerSec,
                float leftUnsignedRadPerSec,
                float rightUnsignedRadPerSec,
                float leftSignedRadPerSec,
                float rightSignedRadPerSec,
                float leftTargetDegPerSec,
                float rightTargetDegPerSec,
                float leftDriveTargetDegPerSec,
                float rightDriveTargetDegPerSec,
                float leftJointVelocityDegPerSec,
                float rightJointVelocityDegPerSec,
                float leftLogicalJointVelocityDegPerSec,
                float rightLogicalJointVelocityDegPerSec,
                float leftAbsFollowRatio,
                float rightAbsFollowRatio,
                bool leftSignedDirectionMatchesTarget,
                bool rightSignedDirectionMatchesTarget,
                float theoreticalLinear,
                float theoreticalAngular,
                int leftSign,
                int rightSign,
                string leftName,
                string rightName,
                string leftJointType,
                string rightJointType,
                float leftDriveStiffness,
                float rightDriveStiffness,
                float leftDriveDamping,
                float rightDriveDamping,
                float leftForceLimit,
                float rightForceLimit)
            {
                CommandLinear = commandLinear;
                CommandAngular = commandAngular;
                EffectiveCommandLinear = effectiveCommandLinear;
                EffectiveCommandAngular = effectiveCommandAngular;
                LinearVelocityCommandScale = linearVelocityCommandScale;
                AngularVelocityCommandScale = angularVelocityCommandScale;
                MinWheelTargetDegPerSec = minWheelTargetDegPerSec;
                MaxWheelTargetDegPerSec = maxWheelTargetDegPerSec;
                LeftUnsignedRadPerSec = leftUnsignedRadPerSec;
                RightUnsignedRadPerSec = rightUnsignedRadPerSec;
                LeftSignedRadPerSec = leftSignedRadPerSec;
                RightSignedRadPerSec = rightSignedRadPerSec;
                LeftTargetDegPerSec = leftTargetDegPerSec;
                RightTargetDegPerSec = rightTargetDegPerSec;
                LeftDriveTargetDegPerSec = leftDriveTargetDegPerSec;
                RightDriveTargetDegPerSec = rightDriveTargetDegPerSec;
                LeftJointVelocityDegPerSec = leftJointVelocityDegPerSec;
                RightJointVelocityDegPerSec = rightJointVelocityDegPerSec;
                LeftLogicalJointVelocityDegPerSec = leftLogicalJointVelocityDegPerSec;
                RightLogicalJointVelocityDegPerSec = rightLogicalJointVelocityDegPerSec;
                LeftAbsFollowRatio = leftAbsFollowRatio;
                RightAbsFollowRatio = rightAbsFollowRatio;
                LeftSignedDirectionMatchesTarget = leftSignedDirectionMatchesTarget;
                RightSignedDirectionMatchesTarget = rightSignedDirectionMatchesTarget;
                TheoreticalLinear = theoreticalLinear;
                TheoreticalAngular = theoreticalAngular;
                LeftSign = leftSign;
                RightSign = rightSign;
                LeftName = leftName;
                RightName = rightName;
                LeftJointType = leftJointType;
                RightJointType = rightJointType;
                LeftDriveStiffness = leftDriveStiffness;
                RightDriveStiffness = rightDriveStiffness;
                LeftDriveDamping = leftDriveDamping;
                RightDriveDamping = rightDriveDamping;
                LeftForceLimit = leftForceLimit;
                RightForceLimit = rightForceLimit;
            }

            public static DriveDiagnostics FromCommand(
                float commandLinear,
                float commandAngular,
                float effectiveCommandLinear,
                float effectiveCommandAngular,
                float leftUnsignedRadPerSec,
                float rightUnsignedRadPerSec,
                float leftSignedRadPerSec,
                float rightSignedRadPerSec,
                float wheelRadius,
                float wheelSeparation,
                int leftSign,
                int rightSign,
                float linearVelocityCommandScale,
                float angularVelocityCommandScale,
                float minWheelTargetDegPerSec,
                float maxWheelTargetDegPerSec,
                ArticulationBody wheelLeft,
                ArticulationBody wheelRight)
            {
                float leftTargetDegPerSec = leftSignedRadPerSec * Mathf.Rad2Deg;
                float rightTargetDegPerSec = rightSignedRadPerSec * Mathf.Rad2Deg;
                float theoreticalLinear = wheelRadius * (leftUnsignedRadPerSec + rightUnsignedRadPerSec) * 0.5f;
                float theoreticalAngular = wheelRadius * (rightUnsignedRadPerSec - leftUnsignedRadPerSec) / wheelSeparation;
                float leftJointVelocityDegPerSec = ReadJointVelocityDegPerSec(wheelLeft);
                float rightJointVelocityDegPerSec = ReadJointVelocityDegPerSec(wheelRight);
                float leftLogicalJointVelocityDegPerSec = leftJointVelocityDegPerSec * leftSign;
                float rightLogicalJointVelocityDegPerSec = rightJointVelocityDegPerSec * rightSign;

                return new DriveDiagnostics(
                    commandLinear,
                    commandAngular,
                    effectiveCommandLinear,
                    effectiveCommandAngular,
                    linearVelocityCommandScale,
                    angularVelocityCommandScale,
                    minWheelTargetDegPerSec,
                    maxWheelTargetDegPerSec,
                    leftUnsignedRadPerSec,
                    rightUnsignedRadPerSec,
                    leftSignedRadPerSec,
                    rightSignedRadPerSec,
                    leftTargetDegPerSec,
                    rightTargetDegPerSec,
                    wheelLeft != null ? wheelLeft.xDrive.targetVelocity : 0f,
                    wheelRight != null ? wheelRight.xDrive.targetVelocity : 0f,
                    leftJointVelocityDegPerSec,
                    rightJointVelocityDegPerSec,
                    leftLogicalJointVelocityDegPerSec,
                    rightLogicalJointVelocityDegPerSec,
                    ComputeAbsFollowRatio(leftTargetDegPerSec, leftJointVelocityDegPerSec),
                    ComputeAbsFollowRatio(rightTargetDegPerSec, rightJointVelocityDegPerSec),
                    HasSameDirection(leftTargetDegPerSec, leftJointVelocityDegPerSec),
                    HasSameDirection(rightTargetDegPerSec, rightJointVelocityDegPerSec),
                    theoreticalLinear,
                    theoreticalAngular,
                    leftSign,
                    rightSign,
                    wheelLeft != null ? wheelLeft.name : "<null>",
                    wheelRight != null ? wheelRight.name : "<null>",
                    wheelLeft != null ? wheelLeft.jointType.ToString() : "<null>",
                    wheelRight != null ? wheelRight.jointType.ToString() : "<null>",
                    wheelLeft != null ? wheelLeft.xDrive.stiffness : 0f,
                    wheelRight != null ? wheelRight.xDrive.stiffness : 0f,
                    wheelLeft != null ? wheelLeft.xDrive.damping : 0f,
                    wheelRight != null ? wheelRight.xDrive.damping : 0f,
                    wheelLeft != null ? wheelLeft.xDrive.forceLimit : 0f,
                    wheelRight != null ? wheelRight.xDrive.forceLimit : 0f);
            }

            private static float ReadJointVelocityDegPerSec(ArticulationBody wheel)
            {
                if (wheel == null || wheel.jointVelocity.dofCount <= 0)
                {
                    return 0f;
                }

                return wheel.jointVelocity[0] * Mathf.Rad2Deg;
            }

            private static float ComputeAbsFollowRatio(float targetDegPerSec, float jointDegPerSec)
            {
                float targetAbs = Mathf.Abs(targetDegPerSec);
                if (targetAbs < 1f)
                {
                    return 1f;
                }

                return Mathf.Abs(jointDegPerSec) / targetAbs;
            }

            private static bool HasSameDirection(float targetDegPerSec, float jointDegPerSec)
            {
                if (Mathf.Abs(targetDegPerSec) < 1f || Mathf.Abs(jointDegPerSec) < 1f)
                {
                    return true;
                }

                return Mathf.Sign(targetDegPerSec) == Mathf.Sign(jointDegPerSec);
            }
        }
    }
}
