using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace SailwindVirtualCrew
{
    /// <summary>
    /// A NavMesh over the port the boat is moored at, so deckhands walk around buildings on the way to the trader
    /// and to dock cargo instead of straight through them.
    ///
    /// Baked in the background once the boat is moored or anchored within reach of a trader: the island's colliders are collected,
    /// the scenery meshes the builder can't read are rebuilt from raycasts a few per frame
    /// (<see cref="ColliderStandInBuilder"/>), then the NavMesh is built asynchronously. It's held relative to the port,
    /// and moved with it when the floating origin shifts the world. Anything asking for a walk before it's ready, or
    /// where it has no path, gets a straight line as before.
    /// </summary>
    internal static class ShoreNavMesh
    {
        private const string Phase = "ShoreNav";
        private const float CheckInterval = 1f;
        private const float HorizontalMargin = 40f;
        private const float MaxHorizontalSize = 320f;
        private const float BelowMargin = 15f;
        private const float AboveMargin = 60f;
        // The boat may move this close to the edge of the baked area before it's baked again around it.
        private const float RebakeEdgeMargin = 20f;
        private const float StandInBudgetMilliseconds = 4f;
        private const float StartSampleRadius = 3f;
        private const float EndSampleRadius = 5f;
        // A path that stops this close to where it was meant to go counts; the last stretch is walked straight.
        private const float ReachedDistance = 2.5f;
        // Walks are cut into pieces this long, each set on the ground, so the walk follows the terrain.
        private const float PieceLength = 1.25f;
        private const float GroundSnapRadius = 1f;
        // The sea is at world y = 0 (floating origin shifts only x and z). The terrain carries on under it, so
        // everything below this is marked unwalkable: walks and landings stay out of the water.
        private const float WaterlineY = 0.4f;
        // The NavMesh "Not Walkable" area.
        private const int NotWalkableArea = 1;
        // Looking for a landing: probes this far apart, each searching this far for the NavMesh.
        private const float LandingProbeSpacing = 3f;
        private const float LandingProbeRadius = 6f;

        private enum State
        {
            None,
            StandIns,
            Building,
            Ready,
            Failed
        }

        private static State _state;
        private static PortDude _dude;
        private static Transform _frame;
        private static Vector3 _anchoredAt;
        private static Bounds _localBounds;
        private static List<NavMeshBuildSource> _sources;
        private static ColliderStandInBuilder _standIns;
        private static NavMeshBuildSettings _settings;
        private static NavMeshData _data;
        private static NavMeshDataInstance _instance;
        private static AsyncOperation _build;
        private static Stopwatch _buildTimer;
        private static float _nextCheckTime;
        private static bool _forceBake;
        private static PortDude _failedDude;

        internal static bool IsReady => _state == State.Ready && _instance.valid;
        internal static string StatusLabel => _state == State.Ready && _dude ? "ready at " + _dude.GetPort().GetPortName() : _state.ToString();
        /// <summary>Bumped whenever a bake finishes or is discarded.</summary>
        internal static int Version { get; private set; }
        internal static Transform Frame => _frame;
        internal static Bounds LocalBounds => _localBounds;
        internal static ColliderStandInBuilder StandIns => _standIns;
        internal static List<string> Report { get; } = new List<string>();

        /// <summary>Bakes again around the nearest trader, even if the boat isn't moored.</summary>
        internal static void Rebake()
        {
            Discard();
            _failedDude = null;
            _forceBake = true;
            _nextCheckTime = 0f;
        }

        internal static void Tick()
        {
            FollowPort();

            switch (_state)
            {
                case State.StandIns:
                    if (_standIns.Step(StandInBudgetMilliseconds))
                        StartBuild();
                    break;
                case State.Building:
                    if (_build.isDone)
                        FinishBuild();
                    break;
            }

            if (Time.unscaledTime >= _nextCheckTime)
            {
                _nextCheckTime = Time.unscaledTime + CheckInterval;
                CheckWanted();
            }
        }

        /// <summary>
        /// Points to walk from <paramref name="from"/> to <paramref name="to"/> around obstacles, set on the ground
        /// and raised by <paramref name="lift"/>; the ends are exactly from and to. False when there's no NavMesh or
        /// no way there, and the caller walks straight.
        /// </summary>
        internal static bool TryBuildWalk(Vector3 from, Vector3 to, float lift, out List<Vector3> points)
        {
            return TryBuildWalk(from, to, lift, out points, out _);
        }

        internal static bool TryBuildWalk(Vector3 from, Vector3 to, float lift, out List<Vector3> points, out string detail)
        {
            points = null;
            if (!IsReady)
            {
                detail = "no NavMesh (" + _state + ")";
                return false;
            }

            if (!NavMesh.SamplePosition(from, out var startHit, StartSampleRadius, NavMesh.AllAreas))
            {
                detail = "start is off the NavMesh";
                return false;
            }
            if (!NavMesh.SamplePosition(to, out var endHit, EndSampleRadius, NavMesh.AllAreas))
            {
                detail = "end is off the NavMesh";
                return false;
            }

            var path = new NavMeshPath();
            NavMesh.CalculatePath(startHit.position, endHit.position, NavMesh.AllAreas, path);
            var corners = path.corners;
            if (path.status == NavMeshPathStatus.PathInvalid || corners.Length == 0)
            {
                detail = "no path";
                return false;
            }

            float gap = Flat(corners[corners.Length - 1] - to).magnitude;
            if (path.status != NavMeshPathStatus.PathComplete && gap > ReachedDistance)
            {
                detail = "path stops " + gap.ToString("0.0") + "m short";
                return false;
            }

            points = new List<Vector3> { from };
            AddPoint(points, corners[0] + Vector3.up * lift);
            for (int i = 1; i < corners.Length; i++)
            {
                Vector3 a = corners[i - 1], b = corners[i];
                int pieces = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(a, b) / PieceLength));
                for (int p = 1; p <= pieces; p++)
                {
                    Vector3 point = Vector3.Lerp(a, b, (float)p / pieces);
                    if (p < pieces && NavMesh.SamplePosition(point, out var ground, GroundSnapRadius, NavMesh.AllAreas))
                        point.y = ground.position.y;
                    AddPoint(points, point + Vector3.up * lift);
                }
            }
            AddPoint(points, to);

            detail = path.status + ", " + corners.Length + " corners, " + points.Count + " points";
            return points.Count >= 2;
        }

        /// <summary>
        /// Where deckhands from a boat anchored at <paramref name="boatWorld"/> can land: the nearest walkable point,
        /// within <paramref name="maxDistance"/>, that has a complete path to <paramref name="targetWorld"/>.
        /// </summary>
        internal static bool TryFindLanding(Vector3 boatWorld, Vector3 targetWorld, float maxDistance, out Vector3 landing)
        {
            landing = Vector3.zero;
            if (!IsReady || !NavMesh.SamplePosition(targetWorld, out var targetHit, EndSampleRadius, NavMesh.AllAreas))
                return false;

            // Probe just above the water: at the boat, then out towards the trader, then all around the boat.
            var probes = new List<Vector3> { boatWorld };
            Vector3 toTarget = Flat(targetWorld - boatWorld);
            Vector3 towards = toTarget.sqrMagnitude > 0.01f ? toTarget.normalized : Vector3.forward;
            for (float d = LandingProbeSpacing; d <= maxDistance; d += LandingProbeSpacing)
                probes.Add(boatWorld + towards * d);
            for (int i = 0; i < 12; i++)
            {
                Vector3 around = Quaternion.Euler(0f, i * 30f, 0f) * Vector3.forward;
                probes.Add(boatWorld + around * (maxDistance * 0.4f));
                probes.Add(boatWorld + around * (maxDistance * 0.8f));
            }

            float best = float.MaxValue;
            var path = new NavMeshPath();
            foreach (var probe in probes)
            {
                var p = new Vector3(probe.x, WaterlineY + 1f, probe.z);
                if (!NavMesh.SamplePosition(p, out var hit, LandingProbeRadius, NavMesh.AllAreas))
                    continue;

                float distance = Flat(hit.position - boatWorld).magnitude;
                if (distance > maxDistance || distance >= best)
                    continue;

                if (!NavMesh.CalculatePath(hit.position, targetHit.position, NavMesh.AllAreas, path)
                    || path.status != NavMeshPathStatus.PathComplete)
                    continue;

                best = distance;
                landing = hit.position;
            }
            return best < float.MaxValue;
        }

        private static void AddPoint(List<Vector3> points, Vector3 point)
        {
            if ((points[points.Count - 1] - point).sqrMagnitude > 0.0025f)
                points.Add(point);
        }

        // ---------------------------------------------------------------- when to bake

        private static void CheckWanted()
        {
            if (_state == State.StandIns || _state == State.Building)
                return;

            if ((!ShoreRoute.IsBoatHeld() && !_forceBake) || !SupercargoTradeService.TryFindNearestPortDude(out var dude))
                return; // Keep what's baked: moored or anchored again at the same port, it's still good.

            if (!_forceBake && dude == _failedDude)
                return;

            if (_state == State.Ready && dude == _dude && BoatWellInside())
                return;

            _forceBake = false;
            BeginBake(dude);
        }

        private static bool BoatWellInside()
        {
            var boat = CrewBoatContextResolver.GetActiveWorldBoat();
            if (!boat || !_frame)
                return false;

            var inner = _localBounds;
            inner.Expand(new Vector3(-2f * RebakeEdgeMargin, 0f, -2f * RebakeEdgeMargin));
            Vector3 local = boat.position - _frame.position;
            local.y = inner.center.y;
            return inner.Contains(local);
        }

        // The NavMesh doesn't move with the world, so follow the port when the floating origin shifts it.
        private static void FollowPort()
        {
            if (_state == State.None)
                return;

            if (!_frame)
            {
                CrewDebugLog.Info(Phase, "Port unloaded; discarding the shore NavMesh.");
                Discard();
                return;
            }

            if (!_instance.valid || (_frame.position - _anchoredAt).sqrMagnitude < 0.0001f)
                return;

            _anchoredAt = _frame.position;
            _instance.Remove();
            _instance = NavMesh.AddNavMeshData(_data, _anchoredAt, Quaternion.identity);
            CrewDebugLog.Info(Phase, "World shifted; moved the shore NavMesh with the port, valid=" + _instance.valid);
        }

        // ---------------------------------------------------------------- baking

        private static void BeginBake(PortDude dude)
        {
            Discard();
            Report.Clear();

            var context = CrewBoatContextResolver.Resolve();
            if (context == null)
                return;

            _dude = dude;
            // Only the port's position is used: shifts only translate the world, and terrain sources can't be rotated.
            _frame = dude.GetPort() ? dude.GetPort().transform : dude.transform;

            var worldBounds = BuildWorldBounds(dude, context);
            _localBounds = new Bounds(worldBounds.center - _frame.position, worldBounds.size);

            var collectTimer = Stopwatch.StartNew();
            var collected = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(worldBounds, ~0, NavMeshCollectGeometry.PhysicsColliders, 0,
                new List<NavMeshBuildMarkup>(), collected);
            _sources = FilterSources(collected, context, out var unreadable);
            collectTimer.Stop();

            Matrix4x4 toFrame = Matrix4x4.Translate(-_frame.position);
            for (int i = 0; i < _sources.Count; i++)
            {
                var source = _sources[i];
                source.transform = toFrame * source.transform;
                _sources[i] = source;
            }
            AddWaterlineModifier(worldBounds);

            AddReport("Baking " + worldBounds.size.x.ToString("0") + "x" + worldBounds.size.z.ToString("0") + "m around "
                + dude.GetPort().GetPortName() + ": " + _sources.Count + " of " + collected.Count + " colliders used, "
                + unreadable.Count + " to stand in for, collected in " + collectTimer.ElapsedMilliseconds + " ms.");

            _standIns = new ColliderStandInBuilder(unreadable, _frame, _localBounds);
            _state = State.StandIns;
        }

        private static void StartBuild()
        {
            if (!_frame)
            {
                Discard();
                return;
            }

            _sources.AddRange(_standIns.Sources);
            AddReport(_standIns.ColliderCount + " scenery colliders stood in for by " + _standIns.Sources.Count + " boxes ("
                + _standIns.Cells + " columns, " + _standIns.Rays + " rays, " + _standIns.ElapsedMilliseconds + " ms spread over frames).");

            _settings = NavMesh.GetSettingsByID(0);
            _settings.agentRadius = 0.3f;
            _settings.agentHeight = 1.8f;
            _settings.agentClimb = 0.5f;
            _settings.agentSlope = 45f;
            _settings.minRegionArea = 2f;
            _settings.overrideVoxelSize = true;
            _settings.voxelSize = 0.1f;
            _settings.overrideTileSize = true;
            _settings.tileSize = 256;

            _data = new NavMeshData(_settings.agentTypeID);
            _buildTimer = Stopwatch.StartNew();
            _build = NavMeshBuilder.UpdateNavMeshDataAsync(_data, _settings, _sources, _localBounds);
            _state = State.Building;
        }

        private static void FinishBuild()
        {
            _buildTimer.Stop();
            _build = null;
            if (!_frame)
            {
                Discard();
                return;
            }

            _anchoredAt = _frame.position;
            _instance = NavMesh.AddNavMeshData(_data, _anchoredAt, Quaternion.identity);
            if (!_instance.valid)
            {
                AddReport("The NavMesh couldn't be added; walking straight at " + _dude.GetPort().GetPortName() + ".");
                _failedDude = _dude;
                _state = State.Failed;
                return;
            }

            _state = State.Ready;
            _failedDude = null;
            Version++;
            AddReport("NavMesh built in " + _buildTimer.ElapsedMilliseconds + " ms (in the background).");
        }

        private static void Discard()
        {
            if (_instance.valid)
                _instance.Remove();
            _instance = new NavMeshDataInstance();
            _data = null;
            _build = null;
            _sources = null;
            _standIns = null;
            _dude = null;
            _frame = null;
            if (_state != State.None)
                Version++;
            _state = State.None;
        }

        // Everything in the baked area below the waterline is unwalkable.
        private static void AddWaterlineModifier(Bounds worldBounds)
        {
            float bottom = worldBounds.min.y - 1f;
            if (WaterlineY <= bottom)
                return;

            var center = new Vector3(worldBounds.center.x, (bottom + WaterlineY) * 0.5f, worldBounds.center.z);
            _sources.Add(new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.ModifierBox,
                size = new Vector3(worldBounds.size.x + 2f, WaterlineY - bottom, worldBounds.size.z + 2f),
                transform = Matrix4x4.Translate(center - _frame.position),
                area = NotWalkableArea
            });
        }

        private static Bounds BuildWorldBounds(PortDude dude, CrewBoatContext context)
        {
            var bounds = new Bounds(dude.transform.position, Vector3.zero);
            bounds.Encapsulate(context.WorldBoat.position);
            if (MooringLocator.TryFindActiveRoute(context.WorldBoat.position, out var route))
                bounds.Encapsulate(route.DockWorld);

            var market = dude.GetPort()?.GetComponent<IslandMarket>();
            var warehouse = market ? market.GetWarehouseArea() : null;
            if (warehouse)
            {
                var col = warehouse.GetComponent<Collider>();
                bounds.Encapsulate(col ? col.bounds.center : warehouse.transform.position);
            }

            Vector3 size = bounds.size;
            size.x = Mathf.Min(size.x + HorizontalMargin * 2f, MaxHorizontalSize);
            size.z = Mathf.Min(size.z + HorizontalMargin * 2f, MaxHorizontalSize);
            float bottom = bounds.min.y - BelowMargin;
            float top = bounds.max.y + AboveMargin;
            size.y = top - bottom;
            return new Bounds(new Vector3(bounds.center.x, (top + bottom) * 0.5f, bounds.center.z), size);
        }

        private static List<NavMeshBuildSource> FilterSources(List<NavMeshBuildSource> sources, CrewBoatContext context, out List<MeshCollider> unreadable)
        {
            var kept = new List<NavMeshBuildSource>();
            var skipped = new Dictionary<string, int>();
            unreadable = new List<MeshCollider>();

            foreach (var source in sources)
            {
                string skip = GetSkipReason(source, context);
                if (skip != null)
                {
                    skipped.TryGetValue(skip, out int count);
                    skipped[skip] = count + 1;
                    continue;
                }

                // The builder would skip it with a warning; the stand-in builder covers it instead.
                if (source.component is MeshCollider meshCollider && meshCollider.sharedMesh && !meshCollider.sharedMesh.isReadable)
                {
                    unreadable.Add(meshCollider);
                    continue;
                }

                kept.Add(source);
            }

            CrewDebugLog.Info(Phase, "Skipped colliders: " + (skipped.Count == 0
                ? "none"
                : string.Join(", ", skipped.OrderByDescending(p => p.Value).Select(p => p.Key + "=" + p.Value).ToArray())));
            return kept;
        }

        private static string GetSkipReason(NavMeshBuildSource source, CrewBoatContext context)
        {
            var collider = source.component as Collider;
            if (!collider)
                return null; // Terrain sources reference the Terrain; keep anything that isn't a plain collider.

            if (collider.isTrigger)
                return "trigger";
            if (collider is CharacterController)
                return "character";
            var t = collider.transform;
            if (t.IsChildOf(context.TopBoat) || t.IsChildOf(context.WorldBoat))
                return "own boat";
            if (collider.GetComponentInParent<ShipItem>() || collider.GetComponentInParent<ItemRigidbody>())
                return "item";
            if (collider.attachedRigidbody && !collider.attachedRigidbody.isKinematic)
                return "dynamic body";
            if (collider.GetComponentInParent<BoatRefs>())
                return "other boat";
            if (collider.CompareTag("OceanBottom"))
                return "ocean bottom";
            return null;
        }

        private static void AddReport(string message)
        {
            Report.Add(message);
            CrewDebugLog.Info(Phase, message);
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v;
        }
    }
}
