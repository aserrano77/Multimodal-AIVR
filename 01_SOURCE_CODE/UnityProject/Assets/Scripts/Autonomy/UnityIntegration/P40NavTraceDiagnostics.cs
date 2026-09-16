using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace Autonomy.UnityIntegration
{
    public static class P40NavTraceDiagnostics
    {
        public static void LogObstacleRegistrySnapshot(
            string phase,
            string reason,
            AutonomousRobotAdapter adapter = null,
            TiagoNavMeshNavigationService navigation = null,
            string taskInstanceId = "",
            string requestId = "",
            string activeTargetId = "",
            string activePlaceTargetId = "",
            string heldObjectId = "",
            string lastDepositedBoxId = "",
            Vector3? robotPosition = null,
            Vector3? activePathEnd = null)
        {
            Vector3 resolvedRobotPosition = robotPosition ?? ResolveRobotPosition(adapter);
            Vector3 resolvedPathEnd = activePathEnd ?? (navigation != null ? navigation.DiagnosticActivePathTargetPosition : Vector3.zero);
            string resolvedTaskInstanceId = FirstNonEmpty(taskInstanceId, adapter != null ? adapter.ActiveP40TaskInstanceId : string.Empty);
            string resolvedRequestId = FirstNonEmpty(requestId, adapter != null ? adapter.ActiveP40RequestId : string.Empty);
            string resolvedTargetId = FirstNonEmpty(activeTargetId, adapter != null ? adapter.ActiveP40TargetId : string.Empty);
            string resolvedPlaceTargetId = FirstNonEmpty(activePlaceTargetId, adapter != null ? adapter.ActiveP40PlaceTargetId : string.Empty);
            string resolvedHeldObjectId = FirstNonEmpty(heldObjectId, adapter != null ? adapter.HeldObjectId : string.Empty);

            var payload = new Dictionary<string, object>
            {
                ["phase"] = phase ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["timestamp"] = DateTime.UtcNow.ToString("O"),
                ["frame_count"] = Time.frameCount,
                ["task_instance_id"] = resolvedTaskInstanceId,
                ["request_id"] = resolvedRequestId,
                ["active_target_id"] = resolvedTargetId,
                ["active_place_target_id"] = resolvedPlaceTargetId,
                ["held_object_id"] = resolvedHeldObjectId,
                ["last_deposited_box_id"] = lastDepositedBoxId ?? string.Empty,
                ["robot_position"] = resolvedRobotPosition,
                ["navigation_phase"] = navigation != null ? navigation.DiagnosticNavigationPhase : string.Empty,
                ["startup_alignment_active"] = navigation != null && navigation.DiagnosticStartupAlignmentActive,
                ["active_path_version"] = navigation != null ? navigation.DiagnosticActivePathVersion : 0,
                ["active_path_source"] = navigation != null ? navigation.DiagnosticActivePathSource : string.Empty,
                ["active_path_target"] = resolvedPathEnd,
                ["active_path_task_instance_id"] = navigation != null ? navigation.DiagnosticActivePathTaskInstanceId : resolvedTaskInstanceId,
                ["scene"] = SceneManager.GetActiveScene().name
            };
            payload["obstacles"] = BuildObstaclePayloads(resolvedRobotPosition, resolvedTargetId, resolvedHeldObjectId, resolvedPathEnd);
            TiagoExperimentTelemetry.LogEvent("nav_obstacle_registry_snapshot", payload);
        }

        public static void LogPostPlaceObstacleRegistrationDiagnostic(
            string phase,
            string reason,
            GameObject box,
            bool obstacleRegistered,
            bool pathsInvalidated,
            AutonomousRobotAdapter adapter = null,
            string boxId = "",
            string zoneId = "",
            bool egressRequired = false,
            string egressResult = "",
            float timeSincePlaceSucceeded = float.NaN)
        {
            NavMeshObstacle obstacle = box != null ? box.GetComponent<NavMeshObstacle>() : null;
            Bounds bounds = CalculateBounds(box);
            var payload = new Dictionary<string, object>
            {
                ["phase"] = phase ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["box_id"] = FirstNonEmpty(boxId, box != null ? box.name : string.Empty),
                ["task_instance_id"] = adapter != null ? adapter.ActiveP40TaskInstanceId : string.Empty,
                ["request_id"] = adapter != null ? adapter.ActiveP40RequestId : string.Empty,
                ["zone_id"] = zoneId ?? string.Empty,
                ["box_position_before_release"] = box != null ? box.transform.position : Vector3.zero,
                ["box_position_after_release"] = box != null ? box.transform.position : Vector3.zero,
                ["robot_position"] = ResolveRobotPosition(adapter),
                ["egress_required"] = egressRequired,
                ["egress_result"] = egressResult ?? string.Empty,
                ["obstacle_registered"] = obstacleRegistered,
                ["obstacle_enabled"] = obstacle != null && obstacle.enabled,
                ["obstacle_carving"] = obstacle != null && obstacle.carving,
                ["obstacle_bounds_min"] = bounds.min,
                ["obstacle_bounds_max"] = bounds.max,
                ["obstacle_bounds_size"] = bounds.size,
                ["paths_invalidated"] = pathsInvalidated,
                ["time_since_place_succeeded"] = timeSincePlaceSucceeded
            };
            TiagoExperimentTelemetry.LogEvent("post_place_obstacle_registration_diagnostic", payload);
        }

        public static Dictionary<string, object> BuildBlockingColliderPayload(
            Collider collider,
            Vector3 samplePoint,
            float minClearance,
            float horizonMeters,
            Vector3 robotPosition,
            TiagoNavMeshNavigationService navigation)
        {
            NavMeshObstacle obstacle = collider != null ? collider.GetComponentInParent<NavMeshObstacle>() : null;
            Bounds bounds = collider != null ? collider.bounds : new Bounds(Vector3.zero, Vector3.zero);
            string logicalId = ResolveLogicalId(collider != null ? collider.gameObject : null);
            return new Dictionary<string, object>
            {
                ["blocking_collider_path"] = collider != null ? BuildHierarchyPath(collider.transform) : string.Empty,
                ["blocking_object_name"] = collider != null ? collider.gameObject.name : string.Empty,
                ["blocking_logical_id"] = logicalId,
                ["blocking_object_kind"] = ResolveObjectKind(collider != null ? collider.gameObject : null, logicalId, string.Empty, string.Empty),
                ["blocking_navmesh_obstacle_enabled"] = obstacle != null && obstacle.enabled,
                ["blocking_navmesh_obstacle_carving"] = obstacle != null && obstacle.carving,
                ["blocking_bounds_min"] = bounds.min,
                ["blocking_bounds_max"] = bounds.max,
                ["blocking_bounds_size"] = bounds.size,
                ["sample_point"] = samplePoint,
                ["min_clearance"] = minClearance,
                ["horizon_meters"] = horizonMeters,
                ["robot_position"] = robotPosition,
                ["active_path_version"] = navigation != null ? navigation.DiagnosticActivePathVersion : 0,
                ["active_path_source"] = navigation != null ? navigation.DiagnosticActivePathSource : string.Empty,
                ["active_path_target"] = navigation != null ? navigation.DiagnosticActivePathTargetPosition : Vector3.zero,
                ["active_task_instance_id"] = navigation != null ? navigation.DiagnosticCurrentTaskInstanceId : string.Empty,
                ["navigation_phase"] = navigation != null ? navigation.DiagnosticNavigationPhase : string.Empty,
                ["startup_alignment_active"] = navigation != null && navigation.DiagnosticStartupAlignmentActive
            };
        }

        public static string BuildHierarchyPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            var names = new Stack<string>();
            Transform current = transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", names);
        }

        public static Vector3 ResolveRobotPosition(AutonomousRobotAdapter adapter)
        {
            return adapter != null && adapter.NavigationReference != null
                ? adapter.NavigationReference.position
                : Vector3.zero;
        }

        private static List<Dictionary<string, object>> BuildObstaclePayloads(
            Vector3 robotPosition,
            string activeTargetId,
            string heldObjectId,
            Vector3 currentPathEnd)
        {
            var records = new List<Dictionary<string, object>>();
            NavMeshObstacle[] obstacles = UnityEngine.Object.FindObjectsByType<NavMeshObstacle>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (NavMeshObstacle obstacle in obstacles)
            {
                if (obstacle == null || obstacle.gameObject == null)
                {
                    continue;
                }

                GameObject gameObject = obstacle.gameObject;
                Bounds bounds = CalculateBounds(gameObject);
                string logicalId = ResolveLogicalId(gameObject);
                string category = ResolveCategory(gameObject);
                Vector3 center = bounds.center;
                records.Add(new Dictionary<string, object>
                {
                    ["object_name"] = gameObject.name,
                    ["hierarchy_path"] = BuildHierarchyPath(gameObject.transform),
                    ["logical_id"] = logicalId,
                    ["category"] = category,
                    ["is_pallet"] = IsPallet(gameObject, logicalId),
                    ["is_box"] = IsBox(gameObject, logicalId),
                    ["is_deposited_box"] = IsDepositedBox(gameObject),
                    ["is_current_target"] = MatchesId(activeTargetId, logicalId, gameObject.name),
                    ["is_held_object"] = MatchesId(heldObjectId, logicalId, gameObject.name),
                    ["navmesh_obstacle_enabled"] = obstacle.enabled,
                    ["carving"] = obstacle.carving,
                    ["carve_only_stationary"] = obstacle.carveOnlyStationary,
                    ["center_world"] = center,
                    ["bounds_min"] = bounds.min,
                    ["bounds_max"] = bounds.max,
                    ["bounds_size"] = bounds.size,
                    ["distance_to_robot"] = PlanarDistance(center, robotPosition),
                    ["distance_to_active_target"] = string.IsNullOrWhiteSpace(activeTargetId) ? float.NaN : PlanarDistance(center, ResolveObjectPosition(activeTargetId)),
                    ["distance_to_current_path_end"] = currentPathEnd == Vector3.zero ? float.NaN : PlanarDistance(center, currentPathEnd)
                });
            }

            return records;
        }

        private static Bounds CalculateBounds(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return new Bounds(Vector3.zero, Vector3.zero);
            }

            Collider[] colliders = gameObject.GetComponentsInChildren<Collider>(true);
            bool hasBounds = false;
            Bounds bounds = default;
            foreach (Collider collider in colliders)
            {
                if (collider == null || collider.isTrigger)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = collider.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }

            if (hasBounds)
            {
                return bounds;
            }

            Renderer[] renderers = gameObject.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasBounds ? bounds : new Bounds(gameObject.transform.position, Vector3.zero);
        }

        private static string ResolveLogicalId(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return string.Empty;
            }

            Component metadata = ResolveBoxMetadata(gameObject);
            return metadata != null ? metadata.gameObject.name : gameObject.name;
        }

        private static string ResolveCategory(GameObject gameObject)
        {
            Component metadata = ResolveBoxMetadata(gameObject);
            return metadata != null ? Convert.ToString(ReadMember(metadata, "boxType")) : string.Empty;
        }

        private static bool IsDepositedBox(GameObject gameObject)
        {
            Component metadata = ResolveBoxMetadata(gameObject);
            object value = metadata != null ? ReadMember(metadata, "isDeposited") : null;
            return value is bool deposited && deposited;
        }

        private static bool IsBox(GameObject gameObject, string logicalId)
        {
            return (gameObject != null && ResolveBoxMetadata(gameObject) != null) ||
                   (!string.IsNullOrWhiteSpace(logicalId) && logicalId.IndexOf("box", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static Component ResolveBoxMetadata(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            MonoBehaviour[] behaviours = gameObject.GetComponentsInParent<MonoBehaviour>(true);
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour != null && string.Equals(behaviour.GetType().Name, "BoxMetadata", StringComparison.Ordinal))
                {
                    return behaviour;
                }
            }

            return null;
        }

        private static object ReadMember(Component component, string memberName)
        {
            if (component == null || string.IsNullOrWhiteSpace(memberName))
            {
                return null;
            }

            Type type = component.GetType();
            System.Reflection.FieldInfo field = type.GetField(memberName);
            if (field != null)
            {
                return field.GetValue(component);
            }

            System.Reflection.PropertyInfo property = type.GetProperty(memberName);
            return property != null ? property.GetValue(component) : null;
        }

        private static bool IsPallet(GameObject gameObject, string logicalId)
        {
            string combined = $"{gameObject?.name} {logicalId}".ToLowerInvariant();
            return combined.Contains("pallet");
        }

        private static string ResolveObjectKind(GameObject gameObject, string logicalId, string activeTargetId, string heldObjectId)
        {
            if (IsPallet(gameObject, logicalId))
            {
                return "pallet";
            }

            if (IsDepositedBox(gameObject))
            {
                return "deposited_box";
            }

            if (MatchesId(activeTargetId, logicalId, gameObject != null ? gameObject.name : string.Empty))
            {
                return "active_box";
            }

            if (MatchesId(heldObjectId, logicalId, gameObject != null ? gameObject.name : string.Empty))
            {
                return "held_box";
            }

            if (IsBox(gameObject, logicalId))
            {
                return "active_box";
            }

            string name = $"{gameObject?.name} {logicalId}".ToLowerInvariant();
            return name.Contains("zone") ? "zone" : "static_obstacle";
        }

        private static Vector3 ResolveObjectPosition(string objectId)
        {
            if (string.IsNullOrWhiteSpace(objectId))
            {
                return Vector3.zero;
            }

            GameObject gameObject = GameObject.Find(objectId);
            return gameObject != null ? gameObject.transform.position : Vector3.zero;
        }

        private static bool MatchesId(string id, string logicalId, string objectName)
        {
            return !string.IsNullOrWhiteSpace(id) &&
                   (string.Equals(id, logicalId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(id, objectName, StringComparison.OrdinalIgnoreCase));
        }

        private static float PlanarDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }
    }
}
