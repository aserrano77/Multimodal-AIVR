using System.Collections.Generic;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Passive contact/collider diagnostics for the TIAGo physical body. It does not alter
    /// navigation, collision layers, or recovery behavior.
    /// </summary>
    public sealed class TiagoCollisionDiagnostics : MonoBehaviour
    {
        private const string LogPrefix = "[CollisionDiagnostic]";
        private const float StallLinearCommandThreshold = 0.2f;
        private const float StallObservedVelocityThreshold = 0.03f;
        private const float StallDurationSeconds = 0.5f;
        private const float SummaryIntervalSeconds = 0.5f;
        private const float OverlapQueryIntervalSeconds = 0.5f;
        private const float NearObstacleQueryDistance = 0.35f;
        private const float TinyBoundsThreshold = 0.01f;

        [Header("References")]
        [SerializeField] private Transform _robotReference;
        [SerializeField] private Transform _robotRoot;

        [Header("Logging")]
        [SerializeField] private bool _diagnosticsEnabled = true;
        [SerializeField] private bool _logContactsOnlyWhenStalled = true;
        [SerializeField] private bool _logColliderInspectionOnStart = true;
        [SerializeField] private bool _logEnvironmentCollidersOnStart = true;
        [SerializeField] private bool _logOverlapDiagnosticsAlwaysWhenNearObstacle = false;
        [SerializeField] private bool _ignoreTriggerOverlapCandidates = true;
        [SerializeField] private bool _ignoreGroundInStuckSummary = true;
        [SerializeField] private bool _ignoreRobotWheelsAndCastersInStuckSummary = true;
        [SerializeField] private float _environmentInspectionRadius = 6f;
        [SerializeField] private float _overlapQueryPadding = 0.08f;
        [SerializeField] private string[] _environmentNameHints = { "pallet", "box", "crate", "obstacle", "wall", "floor", "ground" };
        [SerializeField] private string[] _groundLayerNames = { "Ground", "CasterFloor" };
        [SerializeField] private string[] _robotLocomotionLayerNames = { "RobotWheels", "RobotCasters" };

        private readonly Dictionary<string, ContactSummary> _contactSummaries = new();
        private readonly Dictionary<string, ArmObstacleSummary> _armObstacleSummaries = new();
        private readonly HashSet<Collider> _robotColliders = new();
        private readonly Collider[] _overlapBuffer = new Collider[128];
        private Vector3 _previousPosition;
        private float _previousTime;
        private float _stalledSince = float.NegativeInfinity;
        private float _nextSummaryTime;
        private float _nextOverlapQueryTime;
        private bool _hasPreviousPose;
        private bool _diagnosticsDisabledLogged;

        public bool LogContactsOnlyWhenStalled
        {
            get => _logContactsOnlyWhenStalled;
            set => _logContactsOnlyWhenStalled = value;
        }

        private bool IsStalled => _stalledSince > 0f && Time.time - _stalledSince >= StallDurationSeconds;

        private void Awake()
        {
            _robotRoot = _robotRoot != null ? _robotRoot : transform;
            _robotReference = _robotReference != null ? _robotReference : _robotRoot;
            InstallRelays();
            CapturePoseBaseline();
        }

        private void Start()
        {
            if (!_diagnosticsEnabled)
            {
                return;
            }

            if (_logColliderInspectionOnStart)
            {
                LogLayerMatrixDiagnostics();
                LogRobotColliderInspection();
            }

            if (_logEnvironmentCollidersOnStart)
            {
                LogEnvironmentColliderInspection();
            }
        }

        private void Update()
        {
            if (!_diagnosticsEnabled)
            {
                if (!_diagnosticsDisabledLogged)
                {
                    _diagnosticsDisabledLogged = true;
                    _contactSummaries.Clear();
                    _armObstacleSummaries.Clear();
                    Debug.Log($"{LogPrefix} diagnostics_disabled | collision diagnostic logs suppressed", this);
                }

                return;
            }

            _diagnosticsDisabledLogged = false;

            UpdateStallState();
            if (Time.time >= _nextSummaryTime)
            {
                FlushContactSummaries();
                _nextSummaryTime = Time.time + SummaryIntervalSeconds;
            }

            if (Time.time >= _nextOverlapQueryTime && (IsStalled || (_logOverlapDiagnosticsAlwaysWhenNearObstacle && IsNearObstacle())))
            {
                RunOverlapDiagnostics();
                _nextOverlapQueryTime = Time.time + OverlapQueryIntervalSeconds;
            }
        }

        internal void RecordCollision(Collider robotCollider, Collision collision)
        {
            if (!_diagnosticsEnabled)
            {
                return;
            }

            if (robotCollider == null || collision == null)
            {
                return;
            }

            if (_logContactsOnlyWhenStalled && !IsStalled && !IsLikelyRelevantEnvironment(collision.collider))
            {
                return;
            }

            Collider otherCollider = collision.collider;
            string pairKey = $"{GetPath(robotCollider.transform)}|{GetPath(otherCollider != null ? otherCollider.transform : null)}";
            if (!_contactSummaries.TryGetValue(pairKey, out ContactSummary summary))
            {
                summary = new ContactSummary(robotCollider, otherCollider);
                _contactSummaries[pairKey] = summary;
            }

            int contactCount = collision.contactCount;
            Vector3 impulse = collision.impulse;
            float impulseMagnitude = impulse.magnitude;
            for (int i = 0; i < contactCount; i++)
            {
                ContactPoint contact = collision.GetContact(i);
                summary.Add(contact.normal, impulseMagnitude);
                LogContact(robotCollider, otherCollider, contact, impulseMagnitude, contactCount);
            }
        }

        private void OnValidate()
        {
            if (!_diagnosticsEnabled)
            {
                _contactSummaries.Clear();
                _armObstacleSummaries.Clear();
            }
        }

        private void InstallRelays()
        {
            Collider[] colliders = _robotRoot.GetComponentsInChildren<Collider>(true);
            foreach (Collider collider in colliders)
            {
                if (collider == null)
                {
                    continue;
                }

                _robotColliders.Add(collider);
                CollisionDiagnosticRelay relay = collider.GetComponent<CollisionDiagnosticRelay>();
                if (relay == null)
                {
                    relay = collider.gameObject.AddComponent<CollisionDiagnosticRelay>();
                }

                relay.Initialize(this, collider);
            }
        }

        private void CapturePoseBaseline()
        {
            if (_robotReference == null)
            {
                return;
            }

            _previousPosition = Flatten(_robotReference.position);
            _previousTime = Time.time;
            _hasPreviousPose = true;
        }

        private void UpdateStallState()
        {
            if (_robotReference == null)
            {
                return;
            }

            if (!_hasPreviousPose)
            {
                CapturePoseBaseline();
                return;
            }

            float now = Time.time;
            float dt = Mathf.Max(0.0001f, now - _previousTime);
            Vector3 currentPosition = Flatten(_robotReference.position);
            Vector3 delta = currentPosition - _previousPosition;
            Vector3 forward = Flatten(_robotReference.forward).sqrMagnitude > 0.0001f
                ? Flatten(_robotReference.forward).normalized
                : Vector3.forward;
            float observedV = Vector3.Dot(delta, forward) / dt;
            TiagoExperimentTelemetry.Snapshot snapshot = TiagoExperimentTelemetry.Latest;
            bool commandedForward = snapshot.LinearCommand > StallLinearCommandThreshold;
            bool observedStopped = Mathf.Abs(observedV) < StallObservedVelocityThreshold;
            bool followPathContext = string.Equals(snapshot.ActivePathSource, "Centered") ||
                string.Equals(snapshot.LocomotionMode, "FollowPath") ||
                string.Equals(snapshot.CommandSource, "Autonomous");

            if (commandedForward && observedStopped && followPathContext)
            {
                if (_stalledSince < 0f)
                {
                    _stalledSince = now;
                }
            }
            else
            {
                _stalledSince = float.NegativeInfinity;
            }

            _previousPosition = currentPosition;
            _previousTime = now;
        }

        private void LogContact(Collider robotCollider, Collider otherCollider, ContactPoint contact, float impulseMagnitude, int contactCount)
        {
            string message =
                $"contact | robotCollider={GetPath(robotCollider.transform)} | otherCollider={GetPath(otherCollider != null ? otherCollider.transform : null)} | " +
                $"otherRoot={GetRootName(otherCollider)} | point={Format(contact.point)} | normal={Format(contact.normal)} | separation={contact.separation:F4} | impulse={impulseMagnitude:F3}";
            Debug.Log($"{LogPrefix} {message}", this);
            EmitEvent(
                "collision_diagnostic_contact",
                new Dictionary<string, object>
                {
                    ["robotCollider"] = GetPath(robotCollider.transform),
                    ["robotColliderType"] = robotCollider.GetType().Name,
                    ["otherCollider"] = otherCollider != null ? GetPath(otherCollider.transform) : "<null>",
                    ["otherColliderType"] = otherCollider != null ? otherCollider.GetType().Name : "<null>",
                    ["otherRoot"] = GetRootName(otherCollider),
                    ["point"] = contact.point,
                    ["normal"] = contact.normal,
                    ["separation"] = contact.separation,
                    ["impulse"] = impulseMagnitude,
                    ["contactCount"] = contactCount,
                    ["isStalled"] = IsStalled,
                    ["stalledDuration"] = IsStalled ? Time.time - _stalledSince : 0f,
                    ["vCmd"] = TiagoExperimentTelemetry.Latest.LinearCommand,
                    ["wCmd"] = TiagoExperimentTelemetry.Latest.AngularCommand,
                    ["activePathSource"] = TiagoExperimentTelemetry.Latest.ActivePathSource,
                    ["locomotionMode"] = TiagoExperimentTelemetry.Latest.LocomotionMode
                });
        }

        private void FlushContactSummaries()
        {
            if (_contactSummaries.Count == 0)
            {
                return;
            }

            foreach (ContactSummary summary in _contactSummaries.Values)
            {
                if (summary.ContactCount <= 0)
                {
                    continue;
                }

                Vector3 avgNormal = summary.NormalSum / summary.ContactCount;
                Debug.Log(
                    $"{LogPrefix} stuck_contact_summary | robotCollider={summary.RobotColliderPath} | otherCollider={summary.OtherColliderPath} | contactCount={summary.ContactCount} | avgNormal={Format(avgNormal)} | maxImpulse={summary.MaxImpulse:F3}",
                    this);
                EmitEvent(
                    "collision_diagnostic_stuck_contact_summary",
                    new Dictionary<string, object>
                    {
                        ["robotCollider"] = summary.RobotColliderPath,
                        ["otherCollider"] = summary.OtherColliderPath,
                        ["otherRoot"] = summary.OtherRoot,
                        ["contactCount"] = summary.ContactCount,
                        ["avgNormal"] = avgNormal,
                        ["maxImpulse"] = summary.MaxImpulse,
                        ["isStalled"] = IsStalled,
                        ["stalledDuration"] = IsStalled ? Time.time - _stalledSince : 0f,
                        ["vCmd"] = TiagoExperimentTelemetry.Latest.LinearCommand,
                        ["wCmd"] = TiagoExperimentTelemetry.Latest.AngularCommand,
                        ["activePathSource"] = TiagoExperimentTelemetry.Latest.ActivePathSource,
                        ["locomotionMode"] = TiagoExperimentTelemetry.Latest.LocomotionMode
                    });
            }

            _contactSummaries.Clear();
        }

        private void RunOverlapDiagnostics()
        {
            int candidateCount = 0;
            int penetratingCount = 0;
            float nearestDistance = float.PositiveInfinity;
            string nearestCollider = "<none>";
            float maxPenetrationDistance = 0f;
            string maxPenetrationCollider = "<none>";
            int obstacleCandidateCount = 0;
            int armOrHandCandidateCount = 0;
            float nearestObstacleDistance = float.PositiveInfinity;
            string nearestObstacleCollider = "<none>";
            string nearestObstacleRobotCollider = "<none>";
            string nearestObstacleOtherRoot = "<none>";
            float maxObstaclePenetrationDistance = 0f;
            string penetratingObstacleCollider = "<none>";
            Vector3 robotPosition = _robotReference != null ? _robotReference.position : transform.position;
            float robotYaw = _robotReference != null ? _robotReference.eulerAngles.y : transform.eulerAngles.y;

            foreach (Collider robotCollider in _robotColliders)
            {
                if (robotCollider == null || !robotCollider.enabled)
                {
                    continue;
                }

                Bounds robotBounds = robotCollider.bounds;
                Vector3 halfExtents = robotBounds.extents + Vector3.one * _overlapQueryPadding;
                int hits = Physics.OverlapBoxNonAlloc(
                    robotBounds.center,
                    halfExtents,
                    _overlapBuffer,
                    Quaternion.identity,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Collide);

                for (int i = 0; i < hits; i++)
                {
                    Collider otherCollider = _overlapBuffer[i];
                    _overlapBuffer[i] = null;
                    if (!IsValidOverlapCandidate(robotCollider, otherCollider))
                    {
                        continue;
                    }

                    Vector3 closestPointOnOther = otherCollider.ClosestPoint(robotBounds.center);
                    float distanceApprox = Vector3.Distance(robotBounds.center, closestPointOnOther);
                    bool isPenetrating = Physics.ComputePenetration(
                        robotCollider,
                        robotCollider.transform.position,
                        robotCollider.transform.rotation,
                        otherCollider,
                        otherCollider.transform.position,
                        otherCollider.transform.rotation,
                        out Vector3 penetrationDirection,
                        out float penetrationDistance);
                    if (!isPenetrating)
                    {
                        penetrationDirection = Vector3.zero;
                        penetrationDistance = 0f;
                    }

                    candidateCount++;
                    if (isPenetrating)
                    {
                        penetratingCount++;
                    }

                    if (distanceApprox < nearestDistance)
                    {
                        nearestDistance = distanceApprox;
                        nearestCollider = GetPath(otherCollider.transform);
                    }

                    if (isPenetrating && penetrationDistance > maxPenetrationDistance)
                    {
                        maxPenetrationDistance = penetrationDistance;
                        maxPenetrationCollider = GetPath(otherCollider.transform);
                    }

                    bool isObstacleCandidate = IsObstacleSummaryCandidate(robotCollider, otherCollider);
                    bool isArmOrHand = IsArmOrHandCollider(robotCollider);
                    if (isObstacleCandidate)
                    {
                        obstacleCandidateCount++;
                        if (isArmOrHand)
                        {
                            armOrHandCandidateCount++;
                        }

                        if (distanceApprox < nearestObstacleDistance)
                        {
                            nearestObstacleDistance = distanceApprox;
                            nearestObstacleCollider = GetPath(otherCollider.transform);
                            nearestObstacleRobotCollider = GetPath(robotCollider.transform);
                            nearestObstacleOtherRoot = GetRootName(otherCollider);
                        }

                        if (isPenetrating && penetrationDistance > maxObstaclePenetrationDistance)
                        {
                            maxObstaclePenetrationDistance = penetrationDistance;
                            penetratingObstacleCollider = GetPath(otherCollider.transform);
                        }

                        if (isArmOrHand)
                        {
                            UpdateArmObstacleSummary(robotCollider, otherCollider, distanceApprox, isPenetrating, penetrationDistance);
                        }
                    }

                    LogOverlapCandidate(
                        robotCollider,
                        otherCollider,
                        closestPointOnOther,
                        distanceApprox,
                        isPenetrating,
                        penetrationDirection,
                        penetrationDistance);
                }
            }

            Debug.Log(
                $"{LogPrefix} stalled_overlap_summary | stalledPosition={Format(robotPosition)} | stalledYaw={robotYaw:F1} | candidateCount={candidateCount} | penetratingCount={penetratingCount} | nearestCollider={nearestCollider} | nearestDistance={(float.IsPositiveInfinity(nearestDistance) ? -1f : nearestDistance):F3} | maxPenetrationCollider={maxPenetrationCollider} | maxPenetrationDistance={maxPenetrationDistance:F4}",
                this);
            EmitEvent(
                "collision_diagnostic_stalled_overlap_summary",
                new Dictionary<string, object>
                {
                    ["stalledPosition"] = robotPosition,
                    ["stalledYaw"] = robotYaw,
                    ["candidateCount"] = candidateCount,
                    ["penetratingCount"] = penetratingCount,
                    ["nearestCollider"] = nearestCollider,
                    ["nearestDistance"] = float.IsPositiveInfinity(nearestDistance) ? -1f : nearestDistance,
                    ["maxPenetrationCollider"] = maxPenetrationCollider,
                    ["maxPenetrationDistance"] = maxPenetrationDistance,
                    ["isStalled"] = IsStalled,
                    ["stalledDuration"] = IsStalled ? Time.time - _stalledSince : 0f,
                    ["vCmd"] = TiagoExperimentTelemetry.Latest.LinearCommand,
                    ["wCmd"] = TiagoExperimentTelemetry.Latest.AngularCommand,
                    ["activePathSource"] = TiagoExperimentTelemetry.Latest.ActivePathSource,
                    ["locomotionMode"] = TiagoExperimentTelemetry.Latest.LocomotionMode
                });
            string recommendation = armOrHandCandidateCount > 0
                ? "increase_carving_to_include_arm_envelope_or_use_navigation_posture"
                : "inspect_nearest_obstacle_collider_against_robot_footprint";
            EmitEvent(
                "collision_diagnostic_stalled_obstacle_summary",
                new Dictionary<string, object>
                {
                    ["nearestObstacleCollider"] = nearestObstacleCollider,
                    ["nearestObstacleDistance"] = float.IsPositiveInfinity(nearestObstacleDistance) ? -1f : nearestObstacleDistance,
                    ["penetratingObstacleCollider"] = penetratingObstacleCollider,
                    ["maxObstaclePenetrationDistance"] = maxObstaclePenetrationDistance,
                    ["robotCollider"] = nearestObstacleRobotCollider,
                    ["otherRoot"] = nearestObstacleOtherRoot,
                    ["candidateCountObstacleOnly"] = obstacleCandidateCount,
                    ["armOrHandCandidateCount"] = armOrHandCandidateCount,
                    ["recommendation"] = recommendation,
                    ["isStalled"] = IsStalled,
                    ["stalledDuration"] = IsStalled ? Time.time - _stalledSince : 0f,
                    ["vCmd"] = TiagoExperimentTelemetry.Latest.LinearCommand,
                    ["wCmd"] = TiagoExperimentTelemetry.Latest.AngularCommand,
                    ["activePathSource"] = TiagoExperimentTelemetry.Latest.ActivePathSource,
                    ["locomotionMode"] = TiagoExperimentTelemetry.Latest.LocomotionMode
                });
            EmitArmObstacleSummaries();
        }

        private bool IsValidOverlapCandidate(Collider robotCollider, Collider otherCollider)
        {
            if (otherCollider == null || otherCollider == robotCollider || _robotColliders.Contains(otherCollider))
            {
                return false;
            }

            if (!otherCollider.enabled || (_ignoreTriggerOverlapCandidates && otherCollider.isTrigger))
            {
                return false;
            }

            return true;
        }

        private void LogOverlapCandidate(
            Collider robotCollider,
            Collider otherCollider,
            Vector3 closestPointOnOther,
            float distanceApprox,
            bool isPenetrating,
            Vector3 penetrationDirection,
            float penetrationDistance)
        {
            Bounds robotBounds = robotCollider.bounds;
            Bounds otherBounds = otherCollider.bounds;
            Debug.Log(
                $"{LogPrefix} overlap_candidate | robotCollider={GetPath(robotCollider.transform)} | otherCollider={GetPath(otherCollider.transform)} | otherRoot={GetRootName(otherCollider)} | distanceApprox={distanceApprox:F3} | isPenetrating={isPenetrating} | penetrationDistance={penetrationDistance:F4}",
                this);
            EmitEvent(
                "collision_diagnostic_overlap_candidate",
                new Dictionary<string, object>
                {
                    ["robotCollider"] = GetPath(robotCollider.transform),
                    ["otherCollider"] = GetPath(otherCollider.transform),
                    ["otherRoot"] = GetRootName(otherCollider),
                    ["robotLayer"] = robotCollider.gameObject.layer,
                    ["robotLayerName"] = LayerMask.LayerToName(robotCollider.gameObject.layer),
                    ["otherLayer"] = otherCollider.gameObject.layer,
                    ["otherLayerName"] = LayerMask.LayerToName(otherCollider.gameObject.layer),
                    ["robotBoundsCenter"] = robotBounds.center,
                    ["robotBoundsExtents"] = robotBounds.extents,
                    ["otherBoundsCenter"] = otherBounds.center,
                    ["otherBoundsExtents"] = otherBounds.extents,
                    ["closestPointOnOther"] = closestPointOnOther,
                    ["distanceApprox"] = distanceApprox,
                    ["isPenetrating"] = isPenetrating,
                    ["penetrationDirection"] = isPenetrating ? penetrationDirection : Vector3.zero,
                    ["penetrationDistance"] = isPenetrating ? penetrationDistance : 0f,
                    ["summaryCategory"] = IsGroundOrLocomotionSummaryCandidate(robotCollider, otherCollider) ? "ground_or_locomotion" : "obstacle",
                    ["isArmOrHandObstacleCandidate"] = IsArmOrHandCollider(robotCollider) && IsLikelyObstacleEnvironment(otherCollider),
                    ["isStalled"] = IsStalled,
                    ["stalledDuration"] = IsStalled ? Time.time - _stalledSince : 0f,
                    ["vCmd"] = TiagoExperimentTelemetry.Latest.LinearCommand,
                    ["wCmd"] = TiagoExperimentTelemetry.Latest.AngularCommand,
                    ["activePathSource"] = TiagoExperimentTelemetry.Latest.ActivePathSource,
                    ["locomotionMode"] = TiagoExperimentTelemetry.Latest.LocomotionMode
                });
        }

        private void LogRobotColliderInspection()
        {
            foreach (Collider collider in _robotColliders)
            {
                if (collider == null)
                {
                    continue;
                }

                Bounds bounds = collider.bounds;
                string suspicion = ClassifyRobotCollider(collider, bounds);
                Debug.Log(
                    $"{LogPrefix} robot_footprint_summary | collider={GetPath(collider.transform)} | bounds=center={Format(bounds.center)} size={Format(bounds.size)} extents={Format(bounds.extents)} | type={collider.GetType().Name} | layer={LayerMask.LayerToName(collider.gameObject.layer)} | isTrigger={collider.isTrigger} | suspicious={suspicion}",
                    this);
                EmitEvent(
                    "collision_diagnostic_robot_colliders",
                    new Dictionary<string, object>
                    {
                        ["object"] = GetPath(collider.transform),
                        ["collider"] = collider.GetType().Name,
                        ["center"] = bounds.center,
                        ["size"] = bounds.size,
                        ["extents"] = bounds.extents,
                        ["layer"] = collider.gameObject.layer,
                        ["layerName"] = LayerMask.LayerToName(collider.gameObject.layer),
                        ["isTrigger"] = collider.isTrigger,
                        ["suspicious"] = suspicion,
                        ["collidesWithDefaultLayer"] = !Physics.GetIgnoreLayerCollision(collider.gameObject.layer, 0)
                    });
            }
        }

        private void LogLayerMatrixDiagnostics()
        {
            int robotChassisLayer = LayerMask.NameToLayer("RobotChassis");
            int robotExternalLayer = LayerMask.NameToLayer("RobotExternal");
            int obstacleLayer = LayerMask.NameToLayer("Obstacle");
            if (obstacleLayer < 0)
            {
                obstacleLayer = LayerMask.NameToLayer("Obstacles");
            }

            LogLayerPair("RobotChassis", robotChassisLayer, "Default", 0);
            LogLayerPair("RobotExternal", robotExternalLayer, "Default", 0);
            LogLayerPair("RobotChassis", robotChassisLayer, "RobotExternal", robotExternalLayer);
            if (obstacleLayer >= 0)
            {
                LogLayerPair("RobotChassis", robotChassisLayer, LayerMask.LayerToName(obstacleLayer), obstacleLayer);
                LogLayerPair("RobotExternal", robotExternalLayer, LayerMask.LayerToName(obstacleLayer), obstacleLayer);
            }

            foreach (Collider robotCollider in _robotColliders)
            {
                if (robotCollider == null)
                {
                    continue;
                }

                int layer = robotCollider.gameObject.layer;
                LogLayerPair(LayerMask.LayerToName(layer), layer, "Default", 0);
                if (obstacleLayer >= 0)
                {
                    LogLayerPair(LayerMask.LayerToName(layer), layer, LayerMask.LayerToName(obstacleLayer), obstacleLayer);
                }
            }
        }

        private void LogLayerPair(string layerAName, int layerA, string layerBName, int layerB)
        {
            bool valid = layerA >= 0 && layerB >= 0;
            bool ignored = valid && Physics.GetIgnoreLayerCollision(layerA, layerB);
            EmitEvent(
                "collision_diagnostic_layer_matrix",
                new Dictionary<string, object>
                {
                    ["layerA"] = layerAName,
                    ["layerAIndex"] = layerA,
                    ["layerB"] = layerBName,
                    ["layerBIndex"] = layerB,
                    ["valid"] = valid,
                    ["ignored"] = ignored
                });
            Debug.Log($"{LogPrefix} layer_matrix | layerA={layerAName}({layerA}) | layerB={layerBName}({layerB}) | ignored={ignored} | valid={valid}", this);
        }

        private void LogEnvironmentColliderInspection()
        {
            Collider[] colliders = FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            Vector3 robotPosition = _robotReference != null ? _robotReference.position : transform.position;
            foreach (Collider collider in colliders)
            {
                if (collider == null || _robotColliders.Contains(collider))
                {
                    continue;
                }

                if (!IsLikelyRelevantEnvironment(collider) && Vector3.Distance(robotPosition, collider.bounds.ClosestPoint(robotPosition)) > _environmentInspectionRadius)
                {
                    continue;
                }

                Bounds bounds = collider.bounds;
                Debug.Log(
                    $"{LogPrefix} collider_bounds | object={GetRootName(collider)} | collider={GetPath(collider.transform)} | center={Format(bounds.center)} | size={Format(bounds.size)} | extents={Format(bounds.extents)}",
                    this);
                EmitEvent(
                    "collision_diagnostic_environment_collider",
                    new Dictionary<string, object>
                    {
                        ["object"] = GetRootName(collider),
                        ["collider"] = GetPath(collider.transform),
                        ["colliderType"] = collider.GetType().Name,
                        ["center"] = bounds.center,
                        ["size"] = bounds.size,
                        ["extents"] = bounds.extents,
                        ["layer"] = collider.gameObject.layer,
                        ["layerName"] = LayerMask.LayerToName(collider.gameObject.layer),
                        ["isTrigger"] = collider.isTrigger,
                        ["distanceFromRobot"] = Vector3.Distance(robotPosition, bounds.ClosestPoint(robotPosition))
                    });
            }
        }

        private bool IsLikelyRelevantEnvironment(Collider collider)
        {
            if (collider == null)
            {
                return false;
            }

            string name = $"{collider.name} {GetRootName(collider)}".ToLowerInvariant();
            foreach (string hint in _environmentNameHints)
            {
                if (!string.IsNullOrWhiteSpace(hint) && name.Contains(hint.ToLowerInvariant()))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsLikelyObstacleEnvironment(Collider collider)
        {
            if (collider == null)
            {
                return false;
            }

            string name = $"{collider.name} {GetRootName(collider)} {GetPath(collider.transform)}".ToLowerInvariant();
            return name.Contains("pallet") ||
                name.Contains("box") ||
                name.Contains("crate") ||
                name.Contains("obstacle") ||
                name.Contains("wall");
        }

        private bool IsGroundCollider(Collider collider)
        {
            if (collider == null)
            {
                return false;
            }

            string name = $"{collider.name} {GetRootName(collider)} {GetPath(collider.transform)}".ToLowerInvariant();
            if (name.Contains("floor") || name.Contains("ground") || name.Contains("casterfloor"))
            {
                return true;
            }

            string layerName = LayerMask.LayerToName(collider.gameObject.layer);
            return ContainsName(_groundLayerNames, layerName);
        }

        private bool IsRobotLocomotionCollider(Collider collider)
        {
            if (collider == null)
            {
                return false;
            }

            string name = GetPath(collider.transform).ToLowerInvariant();
            string layerName = LayerMask.LayerToName(collider.gameObject.layer);
            return name.Contains("wheel") ||
                name.Contains("caster") ||
                ContainsName(_robotLocomotionLayerNames, layerName);
        }

        private bool IsArmOrHandCollider(Collider collider)
        {
            if (collider == null)
            {
                return false;
            }

            string path = GetPath(collider.transform).ToLowerInvariant();
            return path.Contains("arm") ||
                path.Contains("wrist") ||
                path.Contains("gripper") ||
                path.Contains("finger") ||
                path.Contains("hand");
        }

        private bool IsGroundOrLocomotionSummaryCandidate(Collider robotCollider, Collider otherCollider)
        {
            return (_ignoreGroundInStuckSummary && IsGroundCollider(otherCollider)) ||
                (_ignoreRobotWheelsAndCastersInStuckSummary && IsRobotLocomotionCollider(robotCollider));
        }

        private bool IsObstacleSummaryCandidate(Collider robotCollider, Collider otherCollider)
        {
            if (IsGroundOrLocomotionSummaryCandidate(robotCollider, otherCollider))
            {
                return false;
            }

            return IsLikelyObstacleEnvironment(otherCollider);
        }

        private static bool ContainsName(string[] names, string value)
        {
            if (names == null || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            foreach (string name in names)
            {
                if (string.Equals(name, value, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void UpdateArmObstacleSummary(Collider robotCollider, Collider otherCollider, float distanceApprox, bool isPenetrating, float penetrationDistance)
        {
            string key = $"{GetPath(robotCollider.transform)}|{GetPath(otherCollider.transform)}";
            if (!_armObstacleSummaries.TryGetValue(key, out ArmObstacleSummary summary))
            {
                summary = new ArmObstacleSummary(robotCollider, otherCollider, Time.time);
                _armObstacleSummaries[key] = summary;
            }

            summary.Add(distanceApprox, isPenetrating, penetrationDistance, Time.time);
        }

        private void EmitArmObstacleSummaries()
        {
            foreach (ArmObstacleSummary summary in _armObstacleSummaries.Values)
            {
                EmitEvent(
                    "collision_diagnostic_arm_obstacle_contact_summary",
                    new Dictionary<string, object>
                    {
                        ["robotCollider"] = summary.RobotColliderPath,
                        ["otherCollider"] = summary.OtherColliderPath,
                        ["otherRoot"] = summary.OtherRoot,
                        ["minDistance"] = summary.MinDistance,
                        ["wasPenetrating"] = summary.WasPenetrating,
                        ["maxPenetrationDistance"] = summary.MaxPenetrationDistance,
                        ["firstTimestamp"] = summary.FirstTimestamp,
                        ["lastTimestamp"] = summary.LastTimestamp,
                        ["recommendation"] = "increase_carving_to_include_arm_envelope_or_use_navigation_posture"
                    });
            }
        }

        private bool IsNearObstacle()
        {
            if (_robotReference == null)
            {
                return false;
            }

            Vector3 robotPosition = _robotReference.position;
            Collider[] colliders = FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (Collider collider in colliders)
            {
                if (collider == null || _robotColliders.Contains(collider) || (_ignoreTriggerOverlapCandidates && collider.isTrigger))
                {
                    continue;
                }

                float distance = Vector3.Distance(robotPosition, collider.bounds.ClosestPoint(robotPosition));
                if (distance <= NearObstacleQueryDistance || IsLikelyRelevantEnvironment(collider))
                {
                    return true;
                }
            }

            return false;
        }

        private string ClassifyRobotCollider(Collider collider, Bounds bounds)
        {
            List<string> tags = new();
            string path = GetPath(collider.transform).ToLowerInvariant();
            if (bounds.size.x < TinyBoundsThreshold || bounds.size.y < TinyBoundsThreshold || bounds.size.z < TinyBoundsThreshold)
            {
                tags.Add("zero_or_tiny_bounds");
            }

            if (collider is MeshCollider meshCollider)
            {
                tags.Add("mesh_collider");
                if (!meshCollider.convex)
                {
                    tags.Add("non_convex_mesh_collider");
                }
            }

            if (bounds.size.x > 1.5f || bounds.size.y > 2.5f || bounds.size.z > 1.5f)
            {
                tags.Add("very_large");
            }

            Vector3 localCenter = _robotRoot != null ? _robotRoot.InverseTransformPoint(bounds.center) : bounds.center;
            if (Mathf.Abs(localCenter.x) > 0.5f || Mathf.Abs(localCenter.z) > 0.55f)
            {
                tags.Add("outside_nominal_footprint");
            }

            if (path.Contains("arm") || path.Contains("hand") || path.Contains("gripper") || path.Contains("finger") || path.Contains("wrist"))
            {
                tags.Add("arm_or_hand");
            }

            if (path.Contains("wheel") || path.Contains("caster"))
            {
                tags.Add("wheel_or_caster");
            }

            if (!Physics.GetIgnoreLayerCollision(collider.gameObject.layer, 0))
            {
                tags.Add("collides_with_default_layer");
            }

            return tags.Count > 0 ? string.Join(",", tags) : "none";
        }

        private void EmitEvent(string eventType, Dictionary<string, object> payload)
        {
            if (!TiagoExperimentTelemetry.LogEvent(eventType, payload))
            {
                TiagoExperimentTelemetry.RecordEvent(eventType, $"logger_unavailable object={name}");
            }
        }

        private static Vector3 Flatten(Vector3 value)
        {
            value.y = 0f;
            return value;
        }

        private static string Format(Vector3 value)
        {
            return $"({value.x:F3},{value.y:F3},{value.z:F3})";
        }

        private static string GetRootName(Collider collider)
        {
            return collider != null && collider.transform.root != null ? collider.transform.root.name : "<null>";
        }

        private static string GetPath(Transform current)
        {
            if (current == null)
            {
                return "<null>";
            }

            string path = current.name;
            while (current.parent != null)
            {
                current = current.parent;
                path = $"{current.name}/{path}";
            }

            return path;
        }

        private sealed class ContactSummary
        {
            public ContactSummary(Collider robotCollider, Collider otherCollider)
            {
                RobotColliderPath = robotCollider != null ? GetPath(robotCollider.transform) : "<null>";
                OtherColliderPath = otherCollider != null ? GetPath(otherCollider.transform) : "<null>";
                OtherRoot = GetRootName(otherCollider);
            }

            public string RobotColliderPath { get; }
            public string OtherColliderPath { get; }
            public string OtherRoot { get; }
            public int ContactCount { get; private set; }
            public Vector3 NormalSum { get; private set; }
            public float MaxImpulse { get; private set; }

            public void Add(Vector3 normal, float impulse)
            {
                ContactCount++;
                NormalSum += normal;
                MaxImpulse = Mathf.Max(MaxImpulse, impulse);
            }
        }

        private sealed class ArmObstacleSummary
        {
            public ArmObstacleSummary(Collider robotCollider, Collider otherCollider, float timestamp)
            {
                RobotColliderPath = robotCollider != null ? GetPath(robotCollider.transform) : "<null>";
                OtherColliderPath = otherCollider != null ? GetPath(otherCollider.transform) : "<null>";
                OtherRoot = GetRootName(otherCollider);
                FirstTimestamp = timestamp;
                LastTimestamp = timestamp;
                MinDistance = float.PositiveInfinity;
            }

            public string RobotColliderPath { get; }
            public string OtherColliderPath { get; }
            public string OtherRoot { get; }
            public float MinDistance { get; private set; }
            public bool WasPenetrating { get; private set; }
            public float MaxPenetrationDistance { get; private set; }
            public float FirstTimestamp { get; }
            public float LastTimestamp { get; private set; }

            public void Add(float distance, bool isPenetrating, float penetrationDistance, float timestamp)
            {
                MinDistance = Mathf.Min(MinDistance, distance);
                WasPenetrating |= isPenetrating;
                MaxPenetrationDistance = Mathf.Max(MaxPenetrationDistance, isPenetrating ? penetrationDistance : 0f);
                LastTimestamp = timestamp;
            }
        }

        private sealed class CollisionDiagnosticRelay : MonoBehaviour
        {
            private TiagoCollisionDiagnostics _owner;
            private Collider _robotCollider;

            public void Initialize(TiagoCollisionDiagnostics owner, Collider robotCollider)
            {
                _owner = owner;
                _robotCollider = robotCollider;
            }

            private void OnCollisionEnter(Collision collision)
            {
                _owner?.RecordCollision(_robotCollider, collision);
            }

            private void OnCollisionStay(Collision collision)
            {
                _owner?.RecordCollision(_robotCollider, collision);
            }
        }
    }
}
