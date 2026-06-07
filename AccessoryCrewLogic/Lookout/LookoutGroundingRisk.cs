using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal struct LookoutGroundingRiskResult
    {
        internal bool Evaluated;
        internal bool HasVelocity;
        internal bool HasHit;
        internal bool WarningEligible;
        internal Vector3 Origin;
        internal Vector3 Direction;
        internal Vector3 End;
        internal Vector3 HitPoint;
        internal float Speed;
        internal float ProjectedDistance;
        internal float HitDistance;
        internal float CooldownRemainingSeconds;
        internal string HitColliderName;
        internal string HitColliderPath;
        internal string IslandName;
        internal string Reason;
    }

    internal static class LookoutGroundingRisk
    {
        private const string Phase = "LookoutGrounding";
        private const float SampleIntervalSeconds = 5f;
        private const float PredictionSeconds = 60f;
        private const float MinimumSpeed = 0.15f;
        private const float WarningCooldownSeconds = 20f * 60f;
        private const float WaterlineOffset = 0.25f;
        private const float NearestIslandMaxDistance = 2500f;
        private const string UnknownIslandKeyPrefix = "collider:";

        private static readonly Dictionary<string, float> LastWarningRealTimeByIsland =
            new Dictionary<string, float>();

        private static float _nextSampleGameTime;
        private static bool _debugVisualsEnabled;
        private static GameObject _originMarker;
        private static GameObject _hitMarker;
        private static LineRenderer _rayLine;
        private static LineRenderer _directionLine;

        internal static LookoutGroundingRiskResult LastResult { get; private set; }
        internal static bool DebugVisualsEnabled => _debugVisualsEnabled;

        internal static void Tick(LookoutTask task)
        {
            float gameNow = Time.time;
            if (gameNow < _nextSampleGameTime)
                return;

            _nextSampleGameTime = gameNow + SampleIntervalSeconds;

            if (task == null || task.AssignedCrewman == null)
            {
                LastResult = new LookoutGroundingRiskResult
                {
                    Evaluated = false,
                    Reason = "No active lookout."
                };
                UpdateDebugVisuals();
                return;
            }

            Sample(Time.realtimeSinceStartup, ringBell: true, recordCooldown: true);
        }

        internal static void ForceSample()
        {
            _nextSampleGameTime = 0f;
            Sample(Time.realtimeSinceStartup, ringBell: false, recordCooldown: false);
        }

        internal static void DumpLatest()
        {
            CrewDebugLog.Info(Phase,
                "evaluated=" + LastResult.Evaluated
                + " reason='" + LastResult.Reason + "'"
                + " speed=" + LastResult.Speed.ToString("0.00")
                + " projected=" + LastResult.ProjectedDistance.ToString("0.0")
                + " hit=" + LastResult.HasHit
                + " eligible=" + LastResult.WarningEligible
                + " cooldown=" + LastResult.CooldownRemainingSeconds.ToString("0")
                + " collider='" + LastResult.HitColliderName + "'"
                + " path='" + LastResult.HitColliderPath + "'"
                + " island='" + LastResult.IslandName + "'"
                + " origin=" + Format(LastResult.Origin)
                + " dir=" + Format(LastResult.Direction)
                + " hitPoint=" + Format(LastResult.HitPoint));
        }

        internal static void ShowDebugVisuals()
        {
            _debugVisualsEnabled = true;
            UpdateDebugVisuals();
        }

        internal static void ClearDebugVisuals()
        {
            _debugVisualsEnabled = false;
            DestroyDebugObjects();
        }

        internal static void ResetRuntimeState()
        {
            _nextSampleGameTime = 0f;
            LastWarningRealTimeByIsland.Clear();
            LastResult = new LookoutGroundingRiskResult();
            DestroyDebugObjects();
        }

        private static void Sample(float now, bool ringBell, bool recordCooldown)
        {
            var result = new LookoutGroundingRiskResult
            {
                Evaluated = true,
                Reason = "No hazard found."
            };

            var context = CrewBoatContextResolver.Resolve();
            if (context == null || !context.WorldBoat)
            {
                result.Reason = "No active vessel context.";
                LastResult = result;
                UpdateDebugVisuals();
                return;
            }

            Vector3 velocity = GetVesselVelocity(context);
            velocity.y = 0f;
            result.Speed = velocity.magnitude;
            if (result.Speed < MinimumSpeed)
            {
                result.HasVelocity = false;
                result.Reason = "Vessel horizontal speed below threshold.";
                LastResult = result;
                UpdateDebugVisuals();
                return;
            }

            result.HasVelocity = true;
            result.Direction = velocity.normalized;
            result.ProjectedDistance = result.Speed * PredictionSeconds;

            Vector3 originBase = context.Rigidbody != null
                ? context.Rigidbody.worldCenterOfMass
                : context.WorldBoat.position;
            result.Origin = new Vector3(
                originBase.x,
                GetWaterlineY(originBase, context.WorldBoat.position.y),
                originBase.z);
            result.End = result.Origin + result.Direction * result.ProjectedDistance;

            if (!TryFindGroundingHit(context, result.Origin, result.Direction, result.ProjectedDistance, out var hit))
            {
                LastResult = result;
                UpdateDebugVisuals();
                return;
            }

            result.HasHit = true;
            result.HitPoint = hit.point;
            result.HitDistance = hit.distance;
            result.HitColliderName = hit.collider != null ? hit.collider.name : "";
            result.HitColliderPath = hit.collider != null ? GetPath(hit.collider.transform) : "";

            IslandHorizon island = FindIslandForHit(hit);
            string cooldownKey = GetCooldownKey(island, hit.collider);
            result.IslandName = GetIslandName(island, hit.collider);

            float cooldownRemaining = GetCooldownRemainingSeconds(cooldownKey, now);
            result.CooldownRemainingSeconds = cooldownRemaining;
            if (cooldownRemaining > 0f)
            {
                result.Reason = "Hazard on cooldown.";
                LastResult = result;
                UpdateDebugVisuals();
                return;
            }

            result.WarningEligible = true;
            result.Reason = "Predicted grounding collision.";
            if (recordCooldown)
                LastWarningRealTimeByIsland[cooldownKey] = now;
            LastResult = result;
            CrewDebugLog.Warn(Phase,
                "Predicted grounding in " + (hit.distance / Mathf.Max(result.Speed, MinimumSpeed)).ToString("0")
                + "s island='" + result.IslandName
                + "' collider='" + result.HitColliderName
                + "' speed=" + result.Speed.ToString("0.00")
                + " distance=" + hit.distance.ToString("0.0"));

            if (ringBell)
                CrewNavigationCoordinator.Instance.RingLookoutGroundingBell();

            UpdateDebugVisuals();
        }

        private static Vector3 GetVesselVelocity(CrewBoatContext context)
        {
            if (context.Rigidbody != null)
                return context.Rigidbody.velocity;

            return Vector3.zero;
        }

        private static float GetWaterlineY(Vector3 position, float fallback)
        {
            try
            {
                if (Ocean.Singleton != null)
                    return Ocean.Singleton.GetWaterHeightAtLocation2(position.x, position.z) + WaterlineOffset;
            }
            catch (Exception e)
            {
                CrewDebugLog.Warn(Phase, "Could not sample waterline: " + e.Message);
            }

            return fallback;
        }

        private static bool TryFindGroundingHit(
            CrewBoatContext context,
            Vector3 origin,
            Vector3 direction,
            float distance,
            out RaycastHit bestHit)
        {
            bestHit = default(RaycastHit);
            var hits = Physics.RaycastAll(origin, direction, distance, ~0, QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0)
                return false;

            foreach (var hit in hits.OrderBy(h => h.distance))
            {
                if (hit.collider == null || ShouldIgnoreHit(context, hit.collider))
                    continue;

                bestHit = hit;
                return true;
            }

            return false;
        }

        private static bool ShouldIgnoreHit(CrewBoatContext context, Collider collider)
        {
            if (!collider || collider.isTrigger)
                return true;

            Transform transform = collider.transform;
            if (context.TopBoat && transform.IsChildOf(context.TopBoat))
                return true;
            if (context.WorldBoat && transform.IsChildOf(context.WorldBoat))
                return true;
            if (context.WalkCol && transform.IsChildOf(context.WalkCol))
                return true;

            Rigidbody attachedRigidbody = collider.attachedRigidbody;
            if (attachedRigidbody != null)
            {
                if (IsActiveVesselRigidbody(context, attachedRigidbody))
                    return true;
            }

            if (IsActiveVesselHullGhost(collider))
                return true;

            if (IsActiveVesselAnchor(context, collider))
                return true;

            int waterLayer = LayerMask.NameToLayer("Water");
            if (waterLayer >= 0 && collider.gameObject.layer == waterLayer)
                return true;

            return false;
        }

        private static bool IsActiveVesselRigidbody(CrewBoatContext context, Rigidbody rigidbody)
        {
            if (rigidbody == null)
                return false;

            Transform rigidbodyTransform = rigidbody.transform;
            return (context.Rigidbody != null && rigidbody == context.Rigidbody)
                || (context.TopBoat && (rigidbodyTransform == context.TopBoat || rigidbodyTransform.IsChildOf(context.TopBoat)))
                || (context.WorldBoat && (rigidbodyTransform == context.WorldBoat || rigidbodyTransform.IsChildOf(context.WorldBoat)));
        }

        private static bool IsActiveVesselHullGhost(Collider collider)
        {
            if (!collider)
                return false;

            string name = collider.name;
            return !string.IsNullOrEmpty(name)
                && string.Equals(name.Trim(), "hull player collider", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsActiveVesselAnchor(CrewBoatContext context, Collider collider)
        {
            if (!collider)
                return false;

            var anchor = collider.GetComponentInParent<Anchor>();
            if (anchor == null)
                return false;

            var joint = anchor.GetComponent<ConfigurableJoint>();
            return joint != null
                && joint.connectedBody != null
                && IsActiveVesselRigidbody(context, joint.connectedBody);
        }

        private static IslandHorizon FindIslandForHit(RaycastHit hit)
        {
            if (hit.collider == null)
                return null;

            var parentIsland = hit.collider.GetComponentInParent<IslandHorizon>();
            if (parentIsland != null)
                return parentIsland;

            var tracker = IslandDistanceTracker.instance;
            if (tracker == null || tracker.islands == null || tracker.islands.Count == 0)
                return null;

            return tracker.islands
                .Where(i => i != null)
                .Select(i => new { Island = i, Distance = Vector3.Distance(i.GetPosition(), hit.point) })
                .Where(x => x.Distance <= NearestIslandMaxDistance)
                .OrderBy(x => x.Distance)
                .Select(x => x.Island)
                .FirstOrDefault();
        }

        private static string GetCooldownKey(IslandHorizon island, Collider collider)
        {
            if (island != null)
                return LookoutVisibility.GetIslandKey(island);

            return UnknownIslandKeyPrefix + (collider != null ? collider.GetInstanceID().ToString() : "unknown");
        }

        private static float GetCooldownRemainingSeconds(string key, float now)
        {
            if (string.IsNullOrEmpty(key)
                || !LastWarningRealTimeByIsland.TryGetValue(key, out float lastWarningTime))
                return 0f;

            return Mathf.Max(0f, WarningCooldownSeconds - (now - lastWarningTime));
        }

        private static string GetIslandName(IslandHorizon island, Collider collider)
        {
            if (island == null)
                return collider != null ? collider.name : "Unknown";

            if (LookoutIslandKnowledge.TryGetPortName(island, out string portName))
                return portName;

            string gameObjectName = island.gameObject != null ? island.gameObject.name : null;
            if (!string.IsNullOrEmpty(gameObjectName) && gameObjectName != "Island")
                return gameObjectName;

            return island.islandIndex >= 0 ? "Island #" + island.islandIndex : "Unknown Island";
        }

        private static void UpdateDebugVisuals()
        {
            if (!_debugVisualsEnabled)
                return;

            EnsureDebugObjects();

            if (!LastResult.Evaluated || !LastResult.HasVelocity)
            {
                SetLine(_rayLine, Vector3.zero, Vector3.zero, false);
                SetLine(_directionLine, Vector3.zero, Vector3.zero, false);
                SetMarker(_originMarker, Vector3.zero, false);
                SetMarker(_hitMarker, Vector3.zero, false);
                return;
            }

            SetLine(_rayLine, LastResult.Origin, LastResult.End, true);
            SetLine(_directionLine, LastResult.Origin, LastResult.Origin + LastResult.Direction * Mathf.Min(25f, LastResult.ProjectedDistance), true);
            SetMarker(_originMarker, LastResult.Origin, true);
            SetMarker(_hitMarker, LastResult.HitPoint, LastResult.HasHit);
        }

        private static void EnsureDebugObjects()
        {
            if (!_originMarker)
                _originMarker = CreateMarker("VC_Lookout_Grounding_Origin", Color.cyan, 0.35f);
            if (!_hitMarker)
                _hitMarker = CreateMarker("VC_Lookout_Grounding_Hit", Color.red, 0.55f);
            if (!_rayLine)
                _rayLine = CreateLine("VC_Lookout_Grounding_Ray", Color.red, 0.08f);
            if (!_directionLine)
                _directionLine = CreateLine("VC_Lookout_Grounding_Direction", Color.cyan, 0.06f);
        }

        private static GameObject CreateMarker(string name, Color color, float size)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = name;
            marker.transform.localScale = Vector3.one * size;
            var collider = marker.GetComponent<Collider>();
            if (collider)
                UnityEngine.Object.Destroy(collider);

            var renderer = marker.GetComponent<Renderer>();
            if (renderer)
                renderer.material.color = color;

            return marker;
        }

        private static LineRenderer CreateLine(string name, Color color, float width)
        {
            var obj = new GameObject(name);
            var line = obj.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.useWorldSpace = true;
            line.widthMultiplier = width;
            line.material = new Material(Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default"));
            line.material.color = color;
            line.startColor = color;
            line.endColor = color;
            return line;
        }

        private static void SetLine(LineRenderer line, Vector3 from, Vector3 to, bool visible)
        {
            if (!line)
                return;

            line.enabled = visible;
            line.SetPosition(0, from);
            line.SetPosition(1, to);
        }

        private static void SetMarker(GameObject marker, Vector3 position, bool visible)
        {
            if (!marker)
                return;

            marker.SetActive(visible);
            marker.transform.position = position;
        }

        private static void DestroyDebugObjects()
        {
            if (_originMarker)
                UnityEngine.Object.Destroy(_originMarker);
            if (_hitMarker)
                UnityEngine.Object.Destroy(_hitMarker);
            if (_rayLine)
                UnityEngine.Object.Destroy(_rayLine.gameObject);
            if (_directionLine)
                UnityEngine.Object.Destroy(_directionLine.gameObject);

            _originMarker = null;
            _hitMarker = null;
            _rayLine = null;
            _directionLine = null;
        }

        private static string Format(Vector3 value)
        {
            return "(" + value.x.ToString("0.000") + ", " + value.y.ToString("0.000") + ", " + value.z.ToString("0.000") + ")";
        }

        private static string GetPath(Transform transform)
        {
            if (!transform)
                return "null";

            string path = transform.name;
            Transform current = transform.parent;
            while (current)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }
    }
}
